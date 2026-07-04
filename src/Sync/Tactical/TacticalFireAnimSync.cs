using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Feature C — client-side ATTACK ANIMATION (tac.fire.start). Today the client shows DAMAGE (tac.damage)
    /// but the shooter plays NO fire/throw animation (the shot just "happens"): on a mirroring client both
    /// <c>ShootAbility.Activate</c> (AbilityActivateRelayPatch) and the roll coroutine
    /// (<c>TacticalLevelController.FireWeaponAtTargetCrt</c> via FireWeaponPatch) are fully suppressed.
    ///
    /// This module makes the client play the REAL shooting / grenade-throw animation CONCURRENTLY with the
    /// host, while keeping DAMAGE exclusively on tac.damage and the camera SILENT. MELEE (BashAbility) is a
    /// documented follow-on: it animates via its own BashCrt (NOT FireWeaponAtTargetCrt), so the shoot-coroutine
    /// replay here does not cover it — see TacticalAbilityRelay.FireStartAnimAbilityTypeNames.
    ///
    /// HOST broadcast (<see cref="HostBroadcastFireStart"/>): called from the host branch of
    /// <c>FireWeaponPatch</c> — the prefix on <c>TacticalLevelController.FireWeaponAtTargetCrt</c>, i.e. the
    /// moment the host's authoritative shot animation ACTUALLY begins. RE-TIMING FIX: it used to fire at the
    /// <c>Activate</c>/enqueue prefix (<c>TacticalCombatSync.ClientInterceptAbility</c>), but a long-range
    /// sniper shot is ENQUEUED with a camera-blend defer, so the client replayed the shot early at enqueue while
    /// the host's real shot fired later → a SEQUENTIAL double-play (client anim → late host anim → damage).
    /// Broadcasting from the shot coroutine's host prefix makes the client replay coincide with the host's
    /// visible shot (and damage lands together), matching MOVE. The single coroutine runs for the host's OWN
    /// click, a relayed client intent (re-Activated in <c>HostOnAbilityIntent</c>), AND overwatch/return-fire
    /// reactions — once per shoot action (the whole burst loops inside it). Gated by
    /// <see cref="TacticalAbilityRelay.ShouldBroadcastFireStartAtShotStart"/> (shoot/grenade set, and never the
    /// client's own Synced replay) so the animation surface stays in lock-step with the tac.damage surface.
    ///
    /// CLIENT play (<see cref="ClientOnFireStart"/>): resolve shooter (netId) + ability (guid) + weapon
    /// (<c>ability.Weapon</c>) + target (netId or world point), then REPLAY the native
    /// <c>FireWeaponAtTargetCrt</c> with three client-only guards active for the duration of the replay:
    ///   • <see cref="ReplayActive"/>  → lets the client <c>FireWeaponPatch</c> NOT suppress THIS replay (it
    ///     normally returns false on a non-host, which would block us).
    ///   • <see cref="NeuterProjectileDamage"/> → <c>Weapon.FireProjectile</c> runs FULLY so the firing visuals
    ///     appear (muzzle flash, smoke, shell, SFX, and a real tracer projectile that flies to the target); only
    ///     the projectile's DAMAGE is suppressed — the client-only <c>ProjectileDamageNeuterPatch</c> skips
    ///     <c>ProjectileLogic.AffectTarget</c> so <c>_damageAccum</c> stays null and <c>ApplyAddedDamage</c> never
    ///     runs → ZERO client damage (damage stays owned by tac.damage), and <c>WaitForProjectilesNeuterPatch</c>
    ///     returns an empty coroutine so the shot-events never re-raise on the client.
    ///   • <see cref="CameraSilent"/>  → the client-only <c>FireCameraHintGuardPatch</c> no-ops every
    ///     <c>CameraDirector.Hint(CameraDirectorHint, …)</c> push during the replay → the camera NEVER flies,
    ///     even for the <c>ShootingStarted</c> hint the engine fires regardless of AttackType.
    ///
    /// The replay target is built with <c>AttackType.Synced</c> (the engine's camera-silent MP attack type),
    /// which already gates off the in-coroutine Shoot hint, the ProjectileFired hint, and return-fire. We do
    /// NOT enter <c>TacticalAbility.Activate</c> (which would fire the AbilityActivated camera hint + spend
    /// AP/WP) — we drive <c>FireWeaponAtTargetCrt</c> directly, exactly like the host's authoritative shot but
    /// damage-less (the tracer flies for the visual; only <c>ProjectileLogic.AffectTarget</c> is skipped).
    /// </summary>
    public static class TacticalFireAnimSync
    {
        // ─── Client-only replay guards (read by the Harmony patches) ───────────────────────────────
        // Set only on the CLIENT for the lifetime of one ClientOnFireStart replay coroutine. The Unity
        // tactical sim is single-threaded (main thread), but a coroutine yields across frames, so these are
        // PLAIN statics (NOT ThreadStatic) held for the whole coroutine and cleared in its finally. A nested
        // re-entrant replay (a second fire.start arriving mid-replay) is impossible for a single actor's shot
        // and harmless across actors (the guards are global flags consumed only inside FireWeaponAtTargetCrt),
        // but we ref-count to be safe so an overlapping replay never clears a still-active guard early.
        private static int _replayDepth;
        public static bool ReplayActive => _replayDepth > 0;
        public static bool NeuterProjectileDamage => _replayDepth > 0;
        public static bool CameraSilent => _replayDepth > 0;

        // CLIENT-PREDICTED fire-anim de-dup ledger (combat concurrency fix). The originating client animates its OWN
        // shot immediately on press (ClientPredictFireStart) and records the shooter here; the host's echoed
        // tac.fire.start for that same shooter is then SKIPPED in ClientOnFireStart so the shooter animates exactly
        // once. Pure decision logic + TTL self-expiry are unit-tested in PredictedFireGuard.
        private static readonly PredictedFireGuard _predictedFire = new PredictedFireGuard();

        // Monotonic wall-clock seconds for the de-dup TTL (client runtime only; the pure guard takes it as a param).
        private static float NowSeconds() => Time.unscaledTime;

        /// <summary>Reset all replay state (mission exit). Idempotent.</summary>
        public static void Reset() { _replayDepth = 0; _predictedFire.Reset(); }

        // ─── HOST: an attack is STARTING → broadcast tac.fire.start so clients animate concurrently ──
        /// <summary>HOST: at the moment the host's authoritative shot animation BEGINS — the host prefix on
        /// <c>FireWeaponAtTargetCrt</c> (own click, a relayed client intent re-Activated in
        /// <c>HostOnAbilityIntent</c>, OR an overwatch/return-fire reaction all run this single coroutine) —
        /// read {shooterNetId, abilityDefGuid, target(actor/pos), shotCount} and broadcast <c>tac.fire.start</c>
        /// to all peers so clients replay the shot animation CONCURRENTLY with the host. Animation-only — the
        /// DAMAGE rides tac.damage. Gated by <see cref="TacticalAbilityRelay.ShouldBroadcastFireStartAtShotStart"/>:
        /// the shoot-coroutine set (shoot+grenade) on a real host attack type, never the client's own Synced
        /// replay; melee + any non-shoot ability is skipped. Runs once per shoot action (the burst loops inside
        /// the coroutine). Fail-open: any failure is logged + swallowed so the native host attack always proceeds.</summary>
        public static void HostBroadcastFireStart(object ability, object parameter)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || !engine.IsHost) return;
                if (ability == null) return;
                // Read the attack type so the shot-start gate can reject the client's OWN Synced replay
                // (defense-in-depth beside the IsHost gate above) while passing real host shots (Regular /
                // Burst / Overwatch reaction / ReturnFire). Per-action: one FireWeaponAtTargetCrt = one shoot
                // action (the whole burst loops inside the single coroutine) → exactly one broadcast.
                string attackTypeName = ReadAttackTypeName(parameter);
                if (!TacticalAbilityRelay.ShouldBroadcastFireStartAtShotStart(ability.GetType().Name, attackTypeName)) return;

                object actor = GetProp(ability, "TacticalActorBase");
                if (actor == null) return;
                int shooterNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (shooterNetId < 0) return;

                string abilityGuid = Network.Sync.DefReflection.GetGuid(GetProp(ability, "BaseDef"));
                if (string.IsNullOrEmpty(abilityGuid)) return;

                ReadTarget(parameter, out int targetNetId, out Vector3 targetPos);
                int shotCount = ReadShotCount(ability, parameter);

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacFireStart);
                byte[] payload = TacticalLiveCodec.EncodeFireStart(
                    seq, shooterNetId, abilityGuid, targetNetId, targetPos.x, targetPos.y, targetPos.z, shotCount);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacFireStart, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.fire.start@shot-start seq=" + seq + " shooter=" + shooterNetId +
                          " type=" + ability.GetType().Name + " ability=" + abilityGuid + " attack=" + attackTypeName +
                          " targetNetId=" + targetNetId + " shots=" + shotCount);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastFireStart failed: " + ex); }
        }

        // ─── CLIENT: play the attack animation CONCURRENTLY (projectile flies, damage-less, camera-silent) ─
        /// <summary>CLIENT inbound (<c>tac.fire.start</c>): resolve shooter+ability+weapon+target and REPLAY
        /// <c>FireWeaponAtTargetCrt</c> with the damage + camera guards active so the real animation plays (tracer
        /// flies) with NO damage and NO camera fly. No AP/WP/overwatch (we never enter Activate). No-op off-client /
        /// off-session / when the seq is stale.</summary>
        public static void ClientOnFireStart(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeFireStart(payload, out var s)) { Debug.LogError("[Multiplayer][tac] tac.fire.start decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacFireStart, s.Seq)) return;

            // DE-DUP (client-predicted local anim): if THIS client already played a predicted local fire animation
            // for this shooter (ClientPredictFireStart on its own press), the host's echoed fire-start is that SAME
            // shot coming back — consume the predicted entry + SKIP the replay so the shooter animates exactly ONCE.
            // A non-originating viewer / host-origin shot (e.g. overwatch reaction) has no predicted entry → replays.
            if (_predictedFire.ConsumeIfPredicted(s.ShooterNetId, NowSeconds()))
            {
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacFireStart, s.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT skipped echoed tac.fire.start (own predicted anim) seq=" + s.Seq +
                          " shooter=" + s.ShooterNetId);
                return;
            }

            object shooter = TacticalDeploySync.ResolveLiveActor(s.ShooterNetId);
            if (shooter == null) { Debug.LogError("[Multiplayer][tac] tac.fire.start: no actor for netId " + s.ShooterNetId); return; }

            try
            {
                object ability = ResolveAbilityByGuid(shooter, s.AbilityDefGuid);
                if (ability == null) { Debug.LogError("[Multiplayer][tac] tac.fire.start: shooter has no ability with guid " + s.AbilityDefGuid); return; }

                object weapon = GetProp(ability, "Weapon");
                if (weapon == null) { Debug.LogError("[Multiplayer][tac] tac.fire.start: ability has no Weapon (type=" + ability.GetType().Name + ")"); return; }

                object target = BuildSyncedFireTarget(s);
                if (target == null) { Debug.LogError("[Multiplayer][tac] tac.fire.start: could not build target"); return; }

                // Enemy-turn chase cam: snap to the shooter for the replayed shot (follow=false → one-shot snap).
                if (ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(TacticalTurnSync.IsClientEnemyTurn, shooter != null))
                    TacticalEnemyTurnCamera.ChaseActor(shooter, follow: false);

                object tlc = TacticalDeploySync.LiveTlc;
                if (tlc == null) { Debug.LogError("[Multiplayer][tac] tac.fire.start: no LiveTlc"); return; }

                var fire = AccessTools.Method(tlc.GetType(), "FireWeaponAtTargetCrt");
                if (fire == null) { Debug.LogError("[Multiplayer][tac] tac.fire.start: FireWeaponAtTargetCrt not found"); return; }

                object timing = GetTiming(shooter, tlc);
                if (timing == null) { Debug.LogError("[Multiplayer][tac] tac.fire.start: no Timing to drive the replay"); return; }

                // Drive our own wrapper coroutine: it holds the replay guards for the WHOLE inner coroutine
                // (the guards must survive the cross-frame yields), then clears them in finally.
                var crt = ReplayFireCrt(tlc, fire, weapon, target, ability);
                if (!InvokeStart(timing, timing.GetType(), crt))
                {
                    Debug.LogError("[Multiplayer][tac] tac.fire.start: could not start replay coroutine netId=" + s.ShooterNetId);
                    return;
                }
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacFireStart, s.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT playing tac.fire.start seq=" + s.Seq + " shooter=" + s.ShooterNetId +
                          " type=" + ability.GetType().Name + " targetNetId=" + s.TargetNetId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientOnFireStart failed: " + ex); }
        }

        // ─── CLIENT: PREDICTED local fire animation (Option B — animate instantly on the player's own press) ─
        /// <summary>CLIENT (mirroring): kicked off from <c>TacticalCombatSync.ClientInterceptAbility</c> the instant
        /// the client SUPPRESSES its own shot and sends <c>tac.intent.ability</c>. Plays the EXISTING damage-less /
        /// camera-silent <c>FireWeaponAtTargetCrt</c> replay (same <see cref="ReplayFireCrt"/> path as the wire-driven
        /// <see cref="ClientOnFireStart"/>) LOCALLY using the live ability + aim target, so the shooter animates
        /// CONCURRENTLY with the press instead of waiting for the host's DEFERRED (EnqueueAction + camera-blend) shot
        /// and its echoed <c>tac.fire.start</c> (which always arrives late). DAMAGE stays 100% host-authoritative
        /// (tac.damage) — this is animation-only; a mispredict (host rejects the shot) is cosmetic.
        ///
        /// DE-DUP: records the shooter in <see cref="PredictedFireGuard"/> so the host's echoed <c>tac.fire.start</c>
        /// for THIS shooter is consumed + skipped in <see cref="ClientOnFireStart"/> → the shooter animates EXACTLY
        /// ONCE. SHOOT/grenade only (<see cref="TacticalAbilityRelay.ShouldBroadcastFireStart"/>) — melee animates via
        /// its own BashCrt path. No-op off-client / off-session / non-fire ability. Fail-open: any failure is logged +
        /// swallowed and (crucially) NO predicted entry is recorded, so the host echo will REPLAY normally instead of
        /// being wrongly skipped — the client never ends up with no animation at all.</summary>
        public static void ClientPredictFireStart(object ability, object parameter)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || engine.IsHost) return;
                if (ability == null) return;
                // Shoot/grenade only — melee animates via its own BashCrt path (TacticalMeleeAnimSync), not this coroutine.
                if (!TacticalAbilityRelay.ShouldBroadcastFireStart(ability.GetType().Name)) return;

                object shooter = GetProp(ability, "TacticalActorBase");
                if (shooter == null) return;
                int shooterNetId = TacticalDeploySync.NetIdForLiveActor(shooter);
                if (shooterNetId < 0) return;

                object weapon = GetProp(ability, "Weapon");
                if (weapon == null) { Debug.LogError("[Multiplayer][tac] predict fire: ability has no Weapon (type=" + ability.GetType().Name + ")"); return; }

                // Build a fresh Synced (camera-silent, return-fire-gated) target from the LIVE aim target — never reuse
                // the Regular-typed live parameter (its AttackType would let the engine fire return-fire / extra hints).
                ReadTarget(parameter, out int targetNetId, out Vector3 targetPos);
                object target = BuildSyncedFireTarget(targetNetId, targetPos);
                if (target == null) { Debug.LogError("[Multiplayer][tac] predict fire: could not build target"); return; }

                object tlc = TacticalDeploySync.LiveTlc;
                if (tlc == null) { Debug.LogError("[Multiplayer][tac] predict fire: no LiveTlc"); return; }

                var fire = AccessTools.Method(tlc.GetType(), "FireWeaponAtTargetCrt");
                if (fire == null) { Debug.LogError("[Multiplayer][tac] predict fire: FireWeaponAtTargetCrt not found"); return; }

                object timing = GetTiming(shooter, tlc);
                if (timing == null) { Debug.LogError("[Multiplayer][tac] predict fire: no Timing to drive the replay"); return; }

                var crt = ReplayFireCrt(tlc, fire, weapon, target, ability);
                if (!InvokeStart(timing, timing.GetType(), crt))
                {
                    // Could not start the predicted anim → do NOT record the guard, so the host echo will replay
                    // normally (the client still gets ONE animation, just the late echoed one instead of the predict).
                    Debug.LogError("[Multiplayer][tac] predict fire: could not start predicted coroutine netId=" + shooterNetId);
                    return;
                }

                // Record AFTER a successful start so the host's echoed fire-start for this shooter is de-duped (skipped).
                _predictedFire.RecordPredicted(shooterNetId, NowSeconds());
                Debug.Log("[Multiplayer][tac] CLIENT predicted fire anim shooter=" + shooterNetId +
                          " type=" + ability.GetType().Name + " targetNetId=" + targetNetId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientPredictFireStart failed: " + ex); }
        }

        /// <summary>Wrapper coroutine: raise the client-only replay guards, run the native
        /// <c>FireWeaponAtTargetCrt</c> to completion, then lower the guards in finally. Holding the guards
        /// here (not around the synchronous Start call) is REQUIRED because the inner coroutine yields across
        /// frames and the FireProjectile/camera-hint patches read the guards on those later frames.</summary>
        private static IEnumerator<NextUpdate> ReplayFireCrt(object tlc, MethodInfo fire, object weapon, object target, object ability)
        {
            _replayDepth++;
            // try/finally in an iterator: the finally runs both on normal completion AND when the scheduler
            // DISPOSES the coroutine (e.g. force-stopped at mission exit) — so the replay guard never leaks
            // mid-mission. (OnMissionExit also calls Reset() as a hard backstop.)
            try
            {
                object inner = null;
                try { inner = fire.Invoke(tlc, new[] { weapon, target, ability }); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.fire.start: FireWeaponAtTargetCrt invoke failed: " + ex); }

                if (inner is IEnumerator e)
                {
                    // Manually pump the inner enumerator so a throw inside it is caught + the guards still lowered.
                    while (true)
                    {
                        bool moved;
                        try { moved = e.MoveNext(); }
                        catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.fire.start replay step failed: " + ex); break; }
                        if (!moved) break;
                        object cur = e.Current;
                        yield return cur is NextUpdate nu ? nu : NextUpdate.NextFrame;
                    }
                }
            }
            finally
            {
                _replayDepth--;
                if (_replayDepth < 0) _replayDepth = 0;
            }
        }

        // ─── Build the SYNCED (camera-silent, projectile-free) fire target ──────────────────────────
        /// <summary>Build a <c>TacticalAbilityTarget</c> for the client replay from the fire.start, tagged
        /// <c>AttackType.Synced</c> (the engine's camera-silent MP attack type — gates off the Shoot hint,
        /// ProjectileFired hint, and return-fire).
        ///
        /// ACTOR target (<c>TargetNetId &gt;= 0</c>, FIX #4): build with the actor ctor
        /// <c>TacticalAbilityTarget(TacticalActorBase, AttackType)</c> — which leaves <c>PositionToApply</c> NaN
        /// (<c>InvalidPosition</c>) — AND set <c>DamageReceiver = targetActor</c>, exactly mirroring native
        /// <c>Weapon.GetShootTargets</c> (decompile <c>Weapon.cs:1070-1073</c>: <c>new TacticalAbilityTarget(target)
        /// { DamageReceiver = target.Actor }</c>). The wire-sent <c>pos</c> is the host's NaN-substituted
        /// <c>actor.Pos</c> (feet); using it as <c>PositionToApply</c> made <c>GetWorkingPosition()</c> return the
        /// floor (decompile <c>TacticalAbilityTarget.cs:303-305</c> short-circuits on a non-NaN PositionToApply →
        /// projectile flew into the ground). With PositionToApply NaN, <c>GetWorkingPosition()</c> falls through to
        /// <c>GetDamageReceiverAimPointPos()</c> (decompile <c>:307,320-329</c> — 2nd in the ?? chain, BEFORE the
        /// GameObject/ActorGridPosition entries the actor ctor also sets) → <c>DamageReceiver.GetAimPoint().position</c>
        /// = the target's TORSO, the native aim point.
        ///
        /// BARE-POSITION target (<c>TargetNetId &lt; 0</c>, e.g. grenade/ground-cell): keep the <c>(Vector3)</c> ctor —
        /// these legitimately aim at the sent world point, so PositionToApply=pos is correct.
        ///
        /// Fallback (default-ctor + field-set): same split — actor target → set Actor + DamageReceiver, leave
        /// PositionToApply NaN; bare position → set PositionToApply=pos. AttackType set to Synced on every path.</summary>
        private static object BuildSyncedFireTarget(TacticalLiveCodec.FireStart s)
            => BuildSyncedFireTarget(s.TargetNetId, new Vector3(s.TX, s.TY, s.TZ));

        /// <summary>Overload building the Synced fire target from a resolved {targetNetId, groundPos} — shared by the
        /// wire-driven replay (<see cref="ClientOnFireStart"/>) and the CLIENT-PREDICTED local anim
        /// (<see cref="ClientPredictFireStart"/>, which reads them straight off the live aim target). Same actor /
        /// bare-position split as the wire path.</summary>
        private static object BuildSyncedFireTarget(int targetNetId, Vector3 pos)
        {
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) return null;
            var attackType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.AttackType");
            object synced = SyncedAttackType(attackType);
            object targetActor = targetNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(targetNetId) : null;

            try
            {
                if (targetActor != null)
                {
                    // FIX #4: actor ctor leaves PositionToApply NaN so the native aim-point chain resolves the
                    // torso; DamageReceiver=actor is what GetWorkingPosition reads first (GetDamageReceiverAimPointPos).
                    var actorBaseType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
                    var ctor = (actorBaseType != null && attackType != null)
                        ? targetType.GetConstructor(new[] { actorBaseType, attackType })
                        : null;
                    if (ctor != null)
                    {
                        object t = ctor.Invoke(new[] { targetActor, synced });
                        AccessTools.Field(targetType, "DamageReceiver")?.SetValue(t, targetActor);
                        return t;
                    }
                }
                else
                {
                    var posCtor = targetType.GetConstructor(new[] { typeof(Vector3) });
                    if (posCtor != null)
                    {
                        object t0 = posCtor.Invoke(new object[] { pos });
                        SetAttackType(targetType, t0, synced);
                        return t0;
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildSyncedFireTarget ctor failed, falling back to field-set: " + ex); }

            object target = Activator.CreateInstance(targetType);
            if (targetActor != null)
            {
                // Actor target: mirror native — Actor + DamageReceiver, NO PositionToApply (stays NaN → torso aim).
                AccessTools.Field(targetType, "Actor")?.SetValue(target, targetActor);
                AccessTools.Field(targetType, "DamageReceiver")?.SetValue(target, targetActor);
            }
            else
            {
                AccessTools.Field(targetType, "PositionToApply")?.SetValue(target, pos);
            }
            SetAttackType(targetType, target, synced);
            return target;
        }

        private static void SetAttackType(Type targetType, object target, object synced)
        {
            if (synced == null) return;
            var f = AccessTools.Field(targetType, "AttackType");
            if (f != null) { f.SetValue(target, synced); return; }
            var p = AccessTools.Property(targetType, "AttackType");
            if (p != null && p.CanWrite) p.SetValue(target, synced, null);
        }

        private static object SyncedAttackType(Type attackType)
        {
            if (attackType == null) return null;
            try { return Enum.Parse(attackType, "Synced"); } catch { return null; }
        }

        // ─── Engine reflection helpers (mirror TacticalCombatSync / TacticalMoveSync) ───────────────

        /// <summary>Resolve a <c>TacticalAbility</c> on an actor whose <c>BaseDef.Guid</c> matches (covers
        /// ShootAbility + variants + BashAbility). Identical to <c>TacticalCombatSync.ResolveAbilityByGuid</c>.</summary>
        private static object ResolveAbilityByGuid(object actor, string abilityGuid)
        {
            try
            {
                var tacAbilityType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility");
                if (tacAbilityType == null) return null;
                var getAbilities = AccessTools.Method(actor.GetType(), "GetAbilities");
                if (getAbilities == null || !getAbilities.IsGenericMethodDefinition) return null;
                var gen = getAbilities.MakeGenericMethod(tacAbilityType);
                if (gen.GetParameters().Length != 0) return null;
                var result = gen.Invoke(actor, null) as IEnumerable;
                if (result == null) return null;
                foreach (var a in result)
                {
                    if (a == null) continue;
                    string g = Network.Sync.DefReflection.GetGuid(GetProp(a, "BaseDef"));
                    if (g == abilityGuid) return a;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] (fire) ResolveAbilityByGuid failed: " + ex); }
            return null;
        }

        /// <summary>Read {targetNetId, targetPos} from a <c>TacticalAbilityTarget</c> — identical shape to
        /// <c>TacticalCombatSync.ReadTarget</c> so the host fire.start and the host intent encode the same.</summary>
        private static void ReadTarget(object parameter, out int targetNetId, out Vector3 targetPos)
        {
            targetNetId = TacticalLiveCodec.TargetNetIdNone;
            targetPos = Vector3.zero;
            if (parameter == null) return;
            object actor = GetField(parameter, "Actor");
            if (actor != null)
            {
                int net = TacticalDeploySync.NetIdForLiveActor(actor);
                if (net >= 0) targetNetId = net;
            }
            object pos = GetField(parameter, "PositionToApply");
            if (pos is Vector3 v && !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z))) targetPos = v;
            else if (actor != null && GetProp(actor, "Pos") is Vector3 ap) targetPos = ap;
        }

        /// <summary>Best-effort shot count for diagnostics/parity (the host's real GetNumberOfShots drives the
        /// replay regardless). Reads <c>weapon.GetNumberOfShots(attackType, executionsCount)</c> when reachable;
        /// 1 otherwise.</summary>
        private static int ReadShotCount(object ability, object parameter)
        {
            try
            {
                object weapon = GetProp(ability, "Weapon");
                if (weapon == null) return 1;
                object atk = GetField(parameter, "AttackType");
                var shootDef = GetProp(ability, "ShootAbilityDef");
                int exec = shootDef != null ? Convert.ToInt32(GetProp(shootDef, "ExecutionsCount") ?? 1) : 1;
                var attackType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.AttackType");
                var m = attackType != null ? AccessTools.Method(weapon.GetType(), "GetNumberOfShots", new[] { attackType, typeof(int) }) : null;
                if (m != null && atk != null)
                    return Convert.ToInt32(m.Invoke(weapon, new[] { atk, (object)exec }));
            }
            catch { }
            return 1;
        }

        /// <summary>Read the <c>AttackType</c> enum NAME (e.g. "Regular", "Overwatch", "Synced") off a
        /// <c>TacticalAbilityTarget</c>; "" when unreadable. Feeds <c>ShouldBroadcastFireStartAtShotStart</c>
        /// so the client's own Synced replay of this coroutine is never re-broadcast as a fire-start.</summary>
        private static string ReadAttackTypeName(object parameter)
        {
            object atk = GetField(parameter, "AttackType");
            return atk != null ? atk.ToString() : "";
        }

        private static object GetTiming(object actor, object tlc)
        {
            object timing = GetProp(actor, "Timing");
            if (timing != null) return timing;
            return tlc != null ? GetProp(tlc, "Timing") : null;
        }

        /// <summary>Find + invoke <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …)</c> — identical to the
        /// proven helper in <c>TacticalMoveSync.InvokeStart</c> (optional trailing params → Type.Missing).</summary>
        private static bool InvokeStart(object timingInstance, Type timingType, object crt)
        {
            try
            {
                MethodInfo best = null;
                foreach (var mth in timingType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (mth.Name != "Start") continue;
                    var pars = mth.GetParameters();
                    if (pars.Length < 1) continue;
                    if (!typeof(IEnumerator).IsAssignableFrom(pars[0].ParameterType)) continue;
                    bool restOptional = true;
                    for (int i = 1; i < pars.Length; i++) if (!pars[i].IsOptional) { restOptional = false; break; }
                    if (!restOptional) continue;
                    if (best == null || pars.Length < best.GetParameters().Length) best = mth;
                }
                if (best == null) return false;
                var bp = best.GetParameters();
                var args = new object[bp.Length];
                args[0] = crt;
                for (int i = 1; i < bp.Length; i++) args[i] = Type.Missing;
                best.Invoke(timingInstance, args);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] (fire) InvokeStart failed: " + ex); return false; }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        private static object GetField(object obj, string name)
        {
            if (obj == null) return null;
            var f = AccessTools.Field(obj.GetType(), name);
            if (f != null) return f.GetValue(obj);
            var p = AccessTools.Property(obj.GetType(), name);
            return p != null ? p.GetValue(obj, null) : null;
        }
    }
}
