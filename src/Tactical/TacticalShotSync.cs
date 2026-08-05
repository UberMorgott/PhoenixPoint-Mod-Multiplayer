using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Weapons;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// A3b — THE SHOT ITSELF, not the order that started it (laws L104(k)(l)(m)).
    ///
    /// ─── WHY AN ORDER IS NOT A SHOT ───
    /// The rail relays the ACTIVATION (<c>TacticalCommandSync</c> 0x82: ability + target) and then EVERY peer
    /// runs the game's own fire coroutine for itself. Three things inside that coroutine are decided locally,
    /// and each one is a divergence the order cannot cover:
    ///
    /// 1. THE ROLL. <c>Weapon.SpreadInCone</c>:482 / <c>SpreadInRadius</c>:488-491 draw from the global,
    ///    UNSEEDED <c>UnityEngine.Random</c> — twice per projectile. A shot has no hit/miss die at all: the
    ///    trajectory IS the outcome, so two peers rolling independently point their projectiles at different
    ///    places. Only DAMAGE is authoritative (0x84, <c>ActorDamageClientGate</c>), and the impact/blood VFX
    ///    hangs off the LOCAL raycast — hence "a client sees a hit with blood and no damage is dealt".
    ///    ANCHORED by seeding the roll per projectile from a per-order seed every peer receives. Not by
    ///    suppressing the symptom: seeded, the trajectory, the visuals AND the damage agree by construction,
    ///    for every weapon and every future ability that rolls spread.
    ///
    /// 2. THE BRANCH. <c>TacticalLevelController.FireWeaponAtTargetCrt</c>:1645 —
    ///    <c>shootAnimAction.UseAiming &amp;&amp; !stepOutNeeded &amp;&amp; !tacticalActor.CurrentlyAiming</c> —
    ///    decides a ~1 s AIM_START wait (:1647-1678) and, through <c>stepOutNeeded</c> alone, the STEPPING_OUT
    ///    nav (:1610-1627). <c>CurrentlyAiming</c> is already anchored
    ///    (<c>RelayedAimBranchIsTheSameOnEveryPeer</c>). <c>UseAiming</c> comes off the anim action bound at
    ///    :1566 from (weapon, target, ShootFromPos, ability) — all four mirrored, so it agrees by construction.
    ///    <c>stepOutNeeded</c> :1556 is the last local term: it compares the MIRRORED <c>ShootFromPos</c>
    ///    against THIS peer's own <c>actor.Pos</c>. Anchored by shipping the acting peer's answer and forcing
    ///    it in, so the whole branch has ONE answer and the shot has ONE duration.
    ///
    /// 3. THE WAIT. :1464, :1759 and :1797 park on <c>Map.HasActiveProjectiles</c> — <c>TacticalMap</c>:133,
    ///    <c>ProjectilesInFlight.Any(p =&gt; p.IsActive)</c>, EVERY actor on the map. Single player never has
    ///    two simultaneous shooters; in co-op peer B's mirrored shot spawns projectiles on peer A's map and
    ///    A's own fire coroutine stalls behind them, with zero log lines. While parked the actor stays in
    ///    <c>ExecutingAbilities</c>, so the native tactical UI stays hidden and the host's
    ///    <c>BusyWithOwnOrder</c> keeps deferring that peer's next order up to <c>DeferCeilingSeconds</c>.
    ///    That is a direct mandate violation — no peer may wait on another peer's action — so the wait is
    ///    narrowed to the projectiles of the shot that is waiting, at every site at once.
    ///
    /// ─── WHY THE SEED CAN BE BRACKETED AT ALL ───
    /// The burst loop yields between shots (<c>ShootAndWaitRF</c>:1827-1849), so a seed bracketed around the
    /// whole coroutine would NOT hold — anything else that draws between frames would shift the stream. The
    /// bracket is therefore per <c>Weapon.FireProjectile</c> (:423), which is SYNCHRONOUS, and inside it
    /// <c>CreateTrajectory</c>:445 — the only spread draw — is the FIRST consumer of the freshly seeded
    /// stream, ahead of <c>SpawnFlash</c>/<c>SpawnSmoke</c>/<c>SpawnShell</c>/<c>Instantiate</c>. So nothing a
    /// peer plays differently can get in front of the draw, and the saved state is restored in a finally, so
    /// nothing leaks out of the bracket either.
    ///
    /// Every anchor here is a NO-OP outside a relayed order: nothing is armed in single player, so the game
    /// keeps its own answer everywhere.
    /// </summary>
    internal static class TacticalShotSync
    {
        // ─── THE ARMED ORDER, per actor key ───────────────────────────────
        // Armed at the SAME two points as the L104 camera/aim tokens (the acting path and the mirror path), so
        // every peer holds the same answer for the same order. Overwritten by the next order on that actor, so
        // a stale arm cannot outlive the shot it belongs to. Per-BATTLE, cleared with the rest in Reset.
        private static readonly Dictionary<int, int> _seed = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> _draw = new Dictionary<int, int>();
        private static readonly Dictionary<int, bool> _stepOut = new Dictionary<int, bool>();

        private static readonly System.Random _mint = new System.Random();

        internal static void Reset() { _seed.Clear(); _draw.Clear(); _stepOut.Clear(); }

        /// <summary>A seed for an order nobody shipped one for — the acting peer's own click. Never
        /// <c>UnityEngine.Random</c>: minting from the very stream we are about to seed would make the anchor
        /// depend on the state it replaces.</summary>
        internal static int MintSeed() { lock (_mint) return _mint.Next(int.MinValue, int.MaxValue); }

        /// <summary>Arm this actor's next shot. Both halves ride together on purpose: a seed armed without a
        /// step-out answer (or the other way round) would leave the stale half of the PREVIOUS order in place,
        /// which is the silent-swallow shape this repo keeps paying for.</summary>
        internal static void ArmShot(int key, int seed, bool stepOut)
        {
            if (key == 0) return;   // no shared identity: no peer could agree with it anyway
            _seed[key] = seed;
            _draw[key] = 0;
            _stepOut[key] = stepOut;
        }

        /// <summary>The roll for the NEXT projectile of this actor's armed shot, or false when nothing is
        /// armed (single player, and every unrelayed ability, keep the game's own unseeded roll). Successive
        /// projectiles must differ — a burst whose pellets all share one seed is one pellet fired N times —
        /// and the SAME arm must reproduce the SAME sequence, which is the whole anchor: two peers arming the
        /// same seed draw the same trajectories in the same order.</summary>
        internal static bool NextShotRoll(int key, out int seed)
        {
            seed = 0;
            int baseSeed;
            if (key == 0 || !_seed.TryGetValue(key, out baseSeed)) return false;
            int draw;
            _draw.TryGetValue(key, out draw);
            _draw[key] = draw + 1;
            unchecked { seed = baseSeed * 486187739 + draw; }
            return true;
        }

        /// <summary>The <c>stepOutNeeded</c> anchor (:1556), with the Unity half taken out so RailCheck can
        /// execute the decision itself. Armed = the acting peer's answer wins on every peer; unarmed = this
        /// peer's own computation is untouched.</summary>
        internal static bool StepOutAnchor(bool computed, int key)
        {
            bool shipped;
            return key != 0 && _stepOut.TryGetValue(key, out shipped) ? shipped : computed;
        }

        /// <summary>Does this projectile belong to somebody ELSE's shot? Reference identity on purpose: a
        /// Unity object that has begun tearing down compares == null while still being its own owner (law
        /// L113), and a projectile mis-attributed to nobody would put the global wait straight back.
        /// A null shooter means "we could not tell whose shot this is" — then nothing is foreign and the game
        /// keeps its own global answer.</summary>
        internal static bool ForeignProjectile(object projectileOwner, object shooter) =>
            shooter != null && projectileOwner != null && !ReferenceEquals(projectileOwner, shooter);

        /// <summary>THE NARROWED WAIT (law L104(m)). Replaces <c>Map.HasActiveProjectiles</c> at every site
        /// that parks a shot on it. Outside a live session, and whenever the shooter cannot be named, it
        /// answers exactly what the game answers.</summary>
        internal static bool HasActiveProjectilesForShot(TacticalMap map, object waiter)
        {
            if (map == null) return false;
            var shooter = ShooterOf(waiter);
            if (shooter == null || LiveEngine() == null) return map.HasActiveProjectiles;
            var flight = map.ProjectilesInFlight;
            for (int i = 0; i < flight.Count; i++)
            {
                var p = flight[i];
                if (p == null || !p.IsActive) continue;
                if (ForeignProjectile(p.TacticalActor, shooter)) continue;
                return true;
            }
            return false;
        }

        /// <summary>Who is shooting, read off the coroutine state machine that is doing the waiting. By TYPE
        /// and not by name: the hoisted-local names are the compiler's, the <c>Weapon</c> parameter is the
        /// game's. All three native waits live in coroutines that carry one — <c>FireWeaponAtTargetCrt</c>,
        /// <c>ShootAndWaitRF</c> and <c>ReturnFire</c> — so one lookup covers shoot, overwatch and return fire
        /// without a per-kind list. Anything else answers null and keeps the game's own behaviour.</summary>
        private static readonly Dictionary<Type, FieldInfo> _weaponField = new Dictionary<Type, FieldInfo>();

        internal static TacticalActor ShooterOf(object stateMachine)
        {
            if (stateMachine == null) return null;
            var type = stateMachine.GetType();
            FieldInfo field;
            if (!_weaponField.TryGetValue(type, out field))
            {
                field = null;
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.FieldType == typeof(Weapon)) { field = f; break; }
                _weaponField[type] = field;
            }
            var weapon = field == null ? null : field.GetValue(stateMachine) as Weapon;
            return weapon == null ? null : weapon.TacticalActor;
        }

        /// <summary>The step-out anchor as the transpiled branch calls it, plus the ONE line the next live run
        /// needs. The RCA that pinned this branch could argue the two peers disagree but never OBSERVE it —
        /// and an unobserved divergence is this repo's dominant bug class — so every shot says what it
        /// computed and what it was told. One line per shot, not per projectile.</summary>
        internal static bool StepOutForShot(bool computed, object stateMachine)
        {
            var shooter = ShooterOf(stateMachine);
            int key = shooter == null ? 0 : TacticalActorKey.Of(shooter);
            bool shipped = false;
            bool armed = key != 0 && _stepOut.TryGetValue(key, out shipped);
            Debug.Log("[Multiplayer][tac] shot stepOut " + (shooter == null ? "<unnamed shooter>" : shooter.name) +
                      " key=" + key + " computed=" + computed +
                      (armed ? " shipped=" + shipped + (shipped == computed ? " (agreed)" : " (DISAGREED — this peer " +
                               "would have played a different shot length)")
                             : " (not a relayed order — this peer's own answer stands)"));
            return StepOutAnchor(computed, key);
        }

        private static NetworkEngine LiveEngine()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return null;
            var coord = engine.SaveTransfer;
            return coord != null && coord.SessionStarted ? engine : null;
        }
    }

    /// <summary>
    /// L104(k) — EVERY PEER ROLLS THE SAME SHOT. The bracket is per projectile and the seeded stream's first
    /// consumer is the spread draw inside <c>CreateTrajectory</c>:445, so no VFX, no camera hint and no
    /// frame boundary can get in front of it. The previous state is restored unconditionally, so the game's
    /// own stream is exactly where it was for everything that is not a relayed shot.
    /// </summary>
    [HarmonyPatch(typeof(Weapon), "FireProjectile")]
    internal static class RelayedShotRollsTheSameOnEveryPeer
    {
        private static void Prefix(TacticalActor sourceActor, ref UnityEngine.Random.State? __state)
        {
            __state = null;
            if (sourceActor == null) return;
            int seed;
            if (!TacticalShotSync.NextShotRoll(TacticalActorKey.Of(sourceActor), out seed)) return;
            __state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
        }

        private static void Postfix(ref UnityEngine.Random.State? __state)
        {
            if (__state.HasValue) UnityEngine.Random.state = __state.Value;
        }
    }

    /// <summary>
    /// L104(l) — THE WHOLE AIM BRANCH, not one of its three terms. Patches the branch INPUT inside
    /// <c>FireWeaponAtTargetCrt</c> rather than any caller, so overwatch, return fire and every future rider
    /// inherit it. Nothing is inserted when the actor is not under a relayed order.
    /// </summary>
    [HarmonyPatch]
    internal static class RelayedStepOutIsTheSameOnEveryPeer
    {
        /// <summary>Plural on purpose: a coroutine body that cannot be found must leave the mod loading and
        /// say so, not throw out of PatchAll and take every other patch with it.</summary>
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            var body = MoveNextOf("FireWeaponAtTargetCrt");
            if (body == null)
                Debug.LogError("[Multiplayer][tac] TacticalLevelController.FireWeaponAtTargetCrt has no coroutine " +
                               "body any more — the step-out branch is unanchored and the same shot plays a " +
                               "different length on every peer (law L104(l)).");
            else yield return body;
        }

        /// <summary>Compiler-generated coroutine bodies live on the state machine's MoveNext, not on the method
        /// that returns the enumerator — patching the latter patches one <c>newobj</c> and nothing else.</summary>
        internal static MethodBase MoveNextOf(string coroutine)
        {
            foreach (var t in typeof(TacticalLevelController).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (t.Name.IndexOf(coroutine, StringComparison.Ordinal) >= 0)
                {
                    var mn = AccessTools.Method(t, "MoveNext");
                    if (mn != null) return mn;
                }
            return null;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var anchor = AccessTools.Method(typeof(TacticalShotSync), nameof(TacticalShotSync.StepOutForShot));
            int hits = 0;
            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Stfld && IsStepOut(ins.operand as FieldInfo))
                {
                    hits++;
                    var carry = new CodeInstruction(OpCodes.Ldarg_0);
                    carry.labels.AddRange(ins.labels);   // a branch to the store must not jump PAST the anchor
                    ins.labels.Clear();
                    yield return carry;
                    yield return new CodeInstruction(OpCodes.Call, anchor);
                }
                yield return ins;
            }
            if (hits == 0)
                Debug.LogError("[Multiplayer][tac] FireWeaponAtTargetCrt no longer stores a 'stepOutNeeded' " +
                               "field — the aim/step-out branch is back to a per-peer answer and the same shot " +
                               "plays a different length on every screen (law L104(l)).");
        }

        private static bool IsStepOut(FieldInfo f) =>
            f != null && f.FieldType == typeof(bool) && f.Name.IndexOf("stepOutNeeded", StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// L104(m) — A SHOT WAITS FOR ITS OWN PROJECTILES AND NOBODY ELSE'S. Every method that parks on the
    /// map-global wait is found by reading its IL, so shoot (:1759), the pre-shot clear (:1797) and return
    /// fire (:1464) are covered by ONE rule and a fourth site the game adds is covered the day it appears.
    /// </summary>
    [HarmonyPatch]
    internal static class AShotWaitsOnlyForItsOwnProjectiles
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static MethodInfo NativeWait() =>
            AccessTools.PropertyGetter(typeof(TacticalMap), nameof(TacticalMap.HasActiveProjectiles));

        /// <summary>Every method of the fire controller — including the compiler-generated coroutine bodies
        /// its waits actually live in — that reads the map-global projectile flag.</summary>
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            var wait = NativeWait();
            foreach (var m in Bodies())
                if (Reads(m, wait)) yield return m;
        }

        private static IEnumerable<MethodBase> Bodies()
        {
            foreach (var m in typeof(TacticalLevelController).GetMethods(All)) yield return m;
            foreach (var nested in typeof(TacticalLevelController).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var m in nested.GetMethods(All)) yield return m;
        }

        private static bool Reads(MethodBase m, MethodInfo callee)
        {
            if (m == null || callee == null || m.IsAbstract || m.ContainsGenericParameters) return false;
            try
            {
                foreach (var step in PatchProcessor.ReadMethodBody(m))
                {
                    var called = step.Value as MethodBase;
                    if (called != null && called.MetadataToken == callee.MetadataToken &&
                        called.Module == callee.Module) return true;
                }
            }
            catch { }
            return false;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var wait = NativeWait();
            var narrowed = AccessTools.Method(typeof(TacticalShotSync),
                                              nameof(TacticalShotSync.HasActiveProjectilesForShot));
            foreach (var ins in instructions)
            {
                if (ins.Calls(wait))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);   // the waiter: the coroutine's own state
                    yield return new CodeInstruction(OpCodes.Call, narrowed);
                }
                else yield return ins;
            }
        }
    }
}
