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
    /// Feature C (melee) — client-side MELEE ATTACK ANIMATION (tac.melee.start, 0x91). The MELEE counterpart
    /// of <see cref="TacticalFireAnimSync"/>: today a mirroring client shows melee DAMAGE (tac.damage) but the
    /// attacker plays NO swing animation (the bash just "happens") — on a mirroring client both
    /// <c>BashAbility.Activate</c> (AbilityActivateRelayPatch) and its roll path are suppressed.
    ///
    /// HOST broadcast (<see cref="HostBroadcastMeleeStart"/>): mirrors <c>HostBroadcastFireStart</c> but emits
    /// 0x91 and gates on <see cref="TacticalAbilityRelay.ShouldBroadcastMeleeStart"/> (BashAbility only — the
    /// gate is DISJOINT from the fire-start gate, so a Bash broadcasts melee-start and NEVER fire-start).
    /// Animation-only; DAMAGE rides tac.damage. Fail-open (logs + swallows).
    ///
    /// CLIENT play (<see cref="ClientOnMeleeStart"/>, PHASE 2): resolve attacker (netId) + ability (guid) +
    /// target (netId or world point), then REPLAY the native <c>BashAbility.BashCrt</c> swing coroutine with
    /// the client-only melee guards active for the duration of the replay:
    ///   • <see cref="MeleeReplayActive"/> → the client melee neuter patches key off this flag.
    ///   • DAMAGE-LESS — <c>MeleeDamageNeuterPatch</c> Prefix-skips <c>BashAbility.ApplyPayloadEffects</c>
    ///     (BashCrt.cs:501, the SOLE melee damage application incl the rare ProjectileVisuals branch) so the
    ///     swing plays with ZERO client damage (DAMAGE stays owned by tac.damage / 0x88).
    ///   • NO RETURN-FIRE — <c>MeleeReturnFireNeuterPatch</c> short-circuits <c>TacticalLevelController.ReturnFire</c>
    ///     (BashCrt.cs:543) to an empty coroutine so the host stays the sole authority for return-fire.
    ///   • AMMO-STABLE — the shared <c>FireAmmoChargeNeuterPatch</c> (extended to fire on melee replay too)
    ///     no-ops <c>CommonItemData.ModifyCharges</c> (BashCrt.cs:525) so the swing never drains charges.
    ///
    /// ENTRY (mirror of fire): we DRIVE <c>BashCrt</c> DIRECTLY via a reflected <c>MethodInfo</c> + a stub
    /// <c>PlayingAction</c> whose <c>.Param</c> is the target (the only PlayingAction member BashCrt reads —
    /// BashAbility.cs:441), bypassing <c>BashAbility.Activate</c> / <c>TacticalAbility.Activate</c> entirely.
    /// That bypass means NO <c>ApplyCosts</c> (no AP/WP double-spend) and NO <c>CameraDirector.Hint</c>
    /// AbilityActivated push — exactly like fire driving <c>FireWeaponAtTargetCrt</c> directly. BashCrt itself
    /// raises NO camera hint, so no camera guard is needed for melee.
    /// </summary>
    public static class TacticalMeleeAnimSync
    {
        // ─── Client-only replay guard (read by the Harmony melee patches) ───────────────────────────
        // Set only on the CLIENT for the lifetime of one ClientOnMeleeStart replay coroutine. Mirrors
        // TacticalFireAnimSync._replayDepth EXACTLY: a plain static ref-count held across the coroutine's
        // cross-frame yields (the swing yields over many frames; ApplyPayloadEffects + ReturnFire run on later
        // frames, so the guard must survive the whole pump) and cleared in the wrapper's finally.
        private static int _replayDepth;
        public static bool MeleeReplayActive => _replayDepth > 0;

        /// <summary>Reset all melee-replay state (mission exit). Idempotent. Mirrors
        /// <see cref="TacticalFireAnimSync.Reset"/> so a stuck replay-guard depth never leaks across missions.</summary>
        public static void Reset() => _replayDepth = 0;

        // ─── HOST: a melee swing is STARTING → broadcast tac.melee.start so clients animate concurrently ──
        /// <summary>HOST: at the moment the host BEGINS a melee swing (own click OR a relayed client intent,
        /// both via the patched <c>Activate(object)</c>), read {attackerNetId, abilityDefGuid, target(actor/pos)}
        /// and broadcast <c>tac.melee.start</c> to all peers. Animation-only — the DAMAGE rides tac.damage.
        /// Only the melee set (BashAbility) is broadcast; shoot/grenade (fire-start) + any non-melee ability is
        /// skipped. Fail-open: any failure is logged + swallowed so the native host attack always proceeds.
        /// Mirrors <see cref="TacticalFireAnimSync.HostBroadcastFireStart"/> minus the shot count.</summary>
        public static void HostBroadcastMeleeStart(object ability, object parameter)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || !engine.IsHost) return;
                if (ability == null) return;
                if (!TacticalAbilityRelay.ShouldBroadcastMeleeStart(ability.GetType().Name)) return;

                object actor = GetProp(ability, "TacticalActorBase");
                if (actor == null) return;
                int attackerNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (attackerNetId < 0) return;

                string abilityGuid = Network.Sync.DefReflection.GetGuid(GetProp(ability, "BaseDef"));
                if (string.IsNullOrEmpty(abilityGuid)) return;

                ReadTarget(parameter, out int targetNetId, out Vector3 targetPos);

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacMeleeStart);
                byte[] payload = TacticalLiveCodec.EncodeMeleeStart(
                    seq, attackerNetId, abilityGuid, targetNetId, targetPos.x, targetPos.y, targetPos.z);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacMeleeStart, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.melee.start seq=" + seq + " attacker=" + attackerNetId +
                          " type=" + ability.GetType().Name + " ability=" + abilityGuid +
                          " targetNetId=" + targetNetId);

                // Vision cadence: covers an already-adjacent enemy that BASHES without moving this turn — the move-outcome
                // push would never fire for it. Push the fresh snapshot after the melee broadcast; the _lastBroadcastSig
                // dedup collapses it when nothing changed.
                TacticalVisionSync.HostBroadcastVision();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastMeleeStart failed: " + ex); }
        }

        // ─── CLIENT: play the melee swing CONCURRENTLY (damage-less, no return-fire, ammo-stable) ─────
        /// <summary>CLIENT inbound (<c>tac.melee.start</c>): resolve attacker+ability+target and REPLAY
        /// <c>BashAbility.BashCrt</c> with the melee guards active so the real swing plays with NO damage, NO
        /// return-fire, and NO ammo drain. No AP/WP (we never enter Activate). No-op off-client / off-session /
        /// when the seq is stale. Mirrors <see cref="TacticalFireAnimSync.ClientOnFireStart"/>.</summary>
        public static void ClientOnMeleeStart(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeMeleeStart(payload, out var s)) { Debug.LogError("[Multiplayer][tac] tac.melee.start decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacMeleeStart, s.Seq)) return;

            object attacker = TacticalDeploySync.ResolveLiveActor(s.AttackerNetId);
            if (attacker == null) { Debug.LogError("[Multiplayer][tac] tac.melee.start: no actor for netId " + s.AttackerNetId); return; }

            try
            {
                object ability = ResolveAbilityByGuid(attacker, s.AbilityDefGuid);
                if (ability == null) { Debug.LogError("[Multiplayer][tac] tac.melee.start: attacker has no ability with guid " + s.AbilityDefGuid); return; }

                object target = BuildMeleeTarget(s);
                if (target == null) { Debug.LogError("[Multiplayer][tac] tac.melee.start: could not build target"); return; }

                // NRE guard: a bare-position target (TargetNetId < 0) is built via the (Vector3) ctor and leaves
                // DamageReceiver NULL. BashCrt's AimIK block derefs target.DamageReceiver.GetAimPoint() UNGUARDED
                // (BashAbility.cs:472) on the !MultipleTargetSimulation path. A position-bash normally sets
                // MultipleTargetSimulation==true (OriginTargetData.TargetResult==Position, BashAbility.cs:54), which
                // skips that block — but if the host ever sends a position-target for a NON-multitarget bash, the
                // replay would NRE. The host shouldn't legitimately do this; skip the replay rather than crash.
                if (s.TargetNetId < 0 && GetProp(ability, "MultipleTargetSimulation") is bool mts && !mts)
                {
                    Debug.LogError("[Multiplayer][tac] melee.start replay SKIP: position-target on non-multitarget bash seq=" + s.Seq +
                                   " attacker=" + s.AttackerNetId + " ability=" + s.AbilityDefGuid);
                    TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMeleeStart, s.Seq);
                    return;
                }

                // Enemy-turn chase cam: snap to the attacker for the replayed swing (follow=false → one-shot snap). VISIBILITY
                // GATE (cheap enemy-turn gate first, then the vision walk): snap ONLY to a mirror-visible enemy — the host
                // replays every enemy swing for world-state sync (incl. fog-hidden ones). Reuses the exact 0x97 policy.
                if (ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(TacticalTurnSync.IsClientEnemyTurn, attacker != null)
                    && TacticalEnemyTurnCamera.IsActorVisibleToPlayerFaction(attacker))
                    TacticalEnemyTurnCamera.ChaseActor(attacker, follow: false);

                // BashCrt is private: BashAbility.BashCrt(PlayingAction) — drive it directly (NOT via Activate),
                // exactly like fire drives FireWeaponAtTargetCrt directly. It reads ONLY action.Param (the target).
                var bash = AccessTools.Method(ability.GetType(), "BashCrt");
                if (bash == null) { Debug.LogError("[Multiplayer][tac] tac.melee.start: BashCrt not found on " + ability.GetType().Name); return; }

                object playingAction = BuildStubPlayingAction(target);
                if (playingAction == null) { Debug.LogError("[Multiplayer][tac] tac.melee.start: could not build PlayingAction"); return; }

                object timing = GetTiming(attacker, TacticalDeploySync.LiveTlc);
                if (timing == null) { Debug.LogError("[Multiplayer][tac] tac.melee.start: no Timing to drive the replay"); return; }

                // Drive our own wrapper coroutine: it holds the melee replay guard for the WHOLE inner coroutine
                // (the guard must survive the cross-frame yields), then clears it in finally.
                var crt = ReplayMeleeCrt(ability, bash, playingAction);
                if (!InvokeStart(timing, timing.GetType(), crt))
                {
                    Debug.LogError("[Multiplayer][tac] tac.melee.start: could not start replay coroutine netId=" + s.AttackerNetId);
                    return;
                }
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMeleeStart, s.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT playing tac.melee.start seq=" + s.Seq + " attacker=" + s.AttackerNetId +
                          " type=" + ability.GetType().Name + " targetNetId=" + s.TargetNetId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientOnMeleeStart failed: " + ex); }
        }

        /// <summary>Wrapper coroutine: raise the client-only melee replay guard, run the native
        /// <c>BashCrt</c> to completion, then lower the guard in finally. Holding the guard here (not around the
        /// synchronous Start call) is REQUIRED because the inner coroutine yields across frames and the
        /// damage/return-fire/charge patches read the guard on those later frames. Identical shape to
        /// <c>TacticalFireAnimSync.ReplayFireCrt</c>.</summary>
        private static IEnumerator<NextUpdate> ReplayMeleeCrt(object ability, MethodInfo bash, object playingAction)
        {
            _replayDepth++;
            // try/finally in an iterator: the finally runs both on normal completion AND when the scheduler
            // DISPOSES the coroutine (e.g. force-stopped at mission exit) — so the replay guard never leaks
            // mid-mission. (OnMissionExit also calls Reset() as a hard backstop.)
            try
            {
                object inner = null;
                try { inner = bash.Invoke(ability, new[] { playingAction }); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.melee.start: BashCrt invoke failed: " + ex); }

                if (inner is IEnumerator e)
                {
                    // Manually pump the inner enumerator so a throw inside it is caught + the guard still lowered.
                    while (true)
                    {
                        bool moved;
                        try { moved = e.MoveNext(); }
                        catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.melee.start replay step failed: " + ex); break; }
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

        /// <summary>Build a stub <c>PlayingAction</c> whose <c>.Param</c> is the melee target. <c>BashCrt</c>
        /// reads ONLY <c>action.Param</c> (BashAbility.cs:441) — never the ActionComponent / channel / delegates
        /// / Timing — so a PlayingAction with everything null except Param drives the swing correctly. The
        /// single public ctor is <c>PlayingAction(ActionComponent, ActionChannel, Func&lt;…&gt;, Action, Action,
        /// object param)</c>; we pass null for the unused refs and the zero <c>ActionChannel</c> enum value.</summary>
        private static object BuildStubPlayingAction(object target)
        {
            try
            {
                var paType = AccessTools.TypeByName("Base.Entities.PlayingAction");
                if (paType == null) { Debug.LogError("[Multiplayer][tac] melee: PlayingAction type not found"); return null; }
                var ctors = paType.GetConstructors();
                if (ctors.Length == 0) return null;
                var ctor = ctors[0];
                var pars = ctor.GetParameters();
                var args = new object[pars.Length];
                for (int i = 0; i < pars.Length; i++)
                {
                    var pt = pars[i].ParameterType;
                    if (pt == typeof(object)) { args[i] = target; continue; }            // the Param slot
                    if (pt.IsValueType) { args[i] = Activator.CreateInstance(pt); continue; }  // ActionChannel enum → 0
                    args[i] = null;                                                      // ActionComponent / delegates (unused by BashCrt)
                }
                // Defensive: if the ctor shape ever drifts and there is no object-typed Param slot, fail loud.
                bool sawObject = false;
                foreach (var p in pars) if (p.ParameterType == typeof(object)) { sawObject = true; break; }
                if (!sawObject) { Debug.LogError("[Multiplayer][tac] melee: PlayingAction ctor has no object Param slot"); return null; }
                return ctor.Invoke(args);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildStubPlayingAction failed: " + ex); return null; }
        }

        // ─── Build the melee target (mirror TacticalFireAnimSync.BuildSyncedFireTarget) ───────────────
        /// <summary>Build a <c>TacticalAbilityTarget</c> for the melee replay from the melee.start.
        ///
        /// ACTOR target (<c>TargetNetId &gt;= 0</c>): build with the actor ctor
        /// <c>TacticalAbilityTarget(TacticalActorBase, AttackType)</c> — which leaves <c>PositionToApply</c> NaN —
        /// AND set <c>DamageReceiver = targetActor</c>, exactly mirroring the fire path. BashCrt reads
        /// <c>target.DamageReceiver.GetAimPoint()</c> for the AimIK aim + the anim context (BashAbility.cs:314,472),
        /// so DamageReceiver MUST be set or the swing's IK setup NREs; with PositionToApply NaN the working
        /// position falls through to the torso aim-point. AttackType.Synced is harmless for melee (the melee
        /// coroutine has no attack-type camera gate; return-fire is neutered by patch).
        ///
        /// BARE-POSITION target (<c>TargetNetId &lt; 0</c>): keep the <c>(Vector3)</c> ctor — these aim at the sent
        /// world point (a position-bash sets MultipleTargetSimulation, which skips the per-actor AimIK block).</summary>
        private static object BuildMeleeTarget(TacticalLiveCodec.MeleeStart s)
        {
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) return null;
            var attackType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.AttackType");
            object synced = SyncedAttackType(attackType);
            var pos = new Vector3(s.TX, s.TY, s.TZ);
            object targetActor = s.TargetNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(s.TargetNetId) : null;

            try
            {
                if (targetActor != null)
                {
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
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildMeleeTarget ctor failed, falling back to field-set: " + ex); }

            object target = Activator.CreateInstance(targetType);
            if (targetActor != null)
            {
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

        /// <summary>Resolve a <c>TacticalAbility</c> on an actor whose <c>BaseDef.Guid</c> matches (BashAbility +
        /// variants). Identical to <c>TacticalFireAnimSync.ResolveAbilityByGuid</c>.</summary>
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
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] (melee) ResolveAbilityByGuid failed: " + ex); }
            return null;
        }

        private static object GetTiming(object actor, object tlc)
        {
            object timing = GetProp(actor, "Timing");
            if (timing != null) return timing;
            return tlc != null ? GetProp(tlc, "Timing") : null;
        }

        /// <summary>Find + invoke <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …)</c> — identical to
        /// <c>TacticalFireAnimSync.InvokeStart</c> (optional trailing params → Type.Missing).</summary>
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
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] (melee) InvokeStart failed: " + ex); return false; }
        }

        // ─── Engine reflection helpers (mirror TacticalFireAnimSync) ────────────────────────────────

        /// <summary>Read {targetNetId, targetPos} from a <c>TacticalAbilityTarget</c> — identical shape to
        /// <c>TacticalFireAnimSync.ReadTarget</c> so the host melee.start and the host intent encode the same.</summary>
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
