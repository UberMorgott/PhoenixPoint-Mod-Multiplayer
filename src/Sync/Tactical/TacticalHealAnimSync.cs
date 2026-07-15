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
    /// Feature C (heal) — client-side HEAL PRESENTATION (tac.heal.start, 0xC0). The heal counterpart of
    /// <see cref="TacticalFireAnimSync"/> / <see cref="TacticalMeleeAnimSync"/>: HealAbility HP + medkit charge
    /// already sync host-authoritatively (0x8F Health bit + the host-owned charge), but NO heal ANIMATION plays on
    /// any peer — a mirroring client SUPPRESSES its own <c>HealAbility.Activate</c> (the generic 0x8E relay) and no
    /// heal presentation surface existed, so the target just silently gains HP.
    ///
    /// HOST broadcast (<see cref="HostBroadcastHealStart"/>): called from the HOST branch of
    /// <c>TacticalCombatSync.ClientInterceptGenericAbility</c> (the generic Activate prefix) — the host's OWN heal
    /// click AND a relayed client heal re-Activated in <c>HostOnGenericIntent</c> both run that one branch, so a
    /// single chokepoint covers every origin. Reads {healerNetId, abilityDefGuid, targetNetId} and broadcasts
    /// <c>tac.heal.start</c> to all peers. Unlike fire there is NO predicted local play (heal is fully suppressed on
    /// the origin client), so every peer replays exactly ONCE — no de-dup ledger needed.
    ///
    /// CLIENT play (<see cref="ClientOnHealStart"/>): resolve healer+ability(guid)+target, then REPLAY the native
    /// private <c>HealAbility.HealTargetCrt(PlayingAction)</c> DIRECTLY (same drive-the-coroutine pattern melee uses
    /// for BashCrt — NOT via Activate, so no AP/WP spend and no AbilityActivated camera hint) with the
    /// <see cref="HealReplayActive"/> guard held for the whole coroutine. Driving the real coroutine reproduces the
    /// full native animation setup (AnimatorOverrides + the medkit side-animator clip + face + DoActionAnimation) for
    /// free. The OUTCOME inside HealTargetCrt is neutered for the duration of the replay (HealAnimSyncPatches):
    ///   • HP — <c>HealHpNeuterPatch</c> Prefix-skips <c>BaseStat.Add(float)</c> (the SOLE heal apply — both the
    ///     general <c>other.Health.Add</c> HealAbility.cs:111 and each bodypart <c>GetHealth().Add</c> :105). HP stays
    ///     owned by the 0x8F Health mirror (which applies via <c>Set</c>, NOT <c>Add</c> — damage uses <c>Subtract</c>
    ///     — so an <c>Add</c>-only neuter can never eat a concurrent host-authoritative HP write).
    ///   • CHARGE — the SHARED <c>FireAmmoChargeNeuterPatch</c> (gated on <c>FireReplayGate.AnyReplay</c>, extended to
    ///     include the heal replay) no-ops <c>CommonItemData.ModifyCharges</c> (HealAbility.cs:125) so the medkit's
    ///     host-authoritative charge never double-drains.
    /// KNOWN CEILING (deliberate): ConditionalHealEffects (<c>Effect.Apply</c>, HealAbility.cs:119) and the healer's
    /// Contribution stat are NOT neutered — a heal-effect STATUS the peer applies is reconciled away by the next 0x8F
    /// status flush (host-owned set), and client Contribution never feeds authority (mission results ride TS4). Most
    /// medkits have neither; not worth patching the broad static <c>Effect.Apply</c>.
    /// </summary>
    public static class TacticalHealAnimSync
    {
        // ─── Client-only replay guard (read by the Harmony heal patches) ────────────────────────────
        // Set only on the CLIENT for the lifetime of one ClientOnHealStart replay coroutine. Mirrors
        // TacticalMeleeAnimSync._replayDepth EXACTLY: a plain static ref-count held across the coroutine's
        // cross-frame yields (HealTargetCrt yields over many frames; the HP/charge applies run on later frames, so
        // the guard must survive the whole pump) and cleared in the wrapper's finally.
        private static int _replayDepth;
        public static bool HealReplayActive => _replayDepth > 0;

        /// <summary>Reset all heal-replay state (mission exit). Idempotent. Mirrors
        /// <see cref="TacticalMeleeAnimSync.Reset"/> so a stuck replay-guard depth never leaks across missions.</summary>
        public static void Reset() => _replayDepth = 0;

        // ─── HOST: a heal is STARTING → broadcast tac.heal.start so clients animate the heal ──────────
        /// <summary>HOST: at the moment the host BEGINS a heal (own click OR a relayed client heal, both via the
        /// generic Activate prefix's host branch), read {healerNetId, abilityDefGuid, targetNetId} and broadcast
        /// <c>tac.heal.start</c> to all peers so they replay the native heal animation. Only <c>HealAbility</c> is
        /// broadcast (no subclasses in base game or TFTV). Fail-open: any failure is logged + swallowed so the native
        /// host heal always proceeds. Mirrors <see cref="TacticalMeleeAnimSync.HostBroadcastMeleeStart"/>.</summary>
        public static void HostBroadcastHealStart(object ability, object parameter)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || !engine.IsHost) return;
                if (ability == null || ability.GetType().Name != "HealAbility") return;

                object healer = GetProp(ability, "TacticalActorBase");
                if (healer == null) return;
                int healerNetId = TacticalDeploySync.NetIdForLiveActor(healer);
                if (healerNetId < 0) return;

                string abilityGuid = Network.Sync.DefReflection.GetGuid(GetProp(ability, "BaseDef"));
                if (string.IsNullOrEmpty(abilityGuid)) return;

                // The healed actor: parameter.Actor (self-heal → the healer itself). Always an actor target.
                int targetNetId = ReadTargetNetId(parameter);
                if (targetNetId < 0) targetNetId = healerNetId;   // self-heal / unreadable target → the healer

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacHealStart);
                byte[] payload = TacticalLiveCodec.EncodeHealStart(seq, healerNetId, abilityGuid, targetNetId);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacHealStart, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.heal.start seq=" + seq + " healer=" + healerNetId +
                          " ability=" + abilityGuid + " targetNetId=" + targetNetId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastHealStart failed: " + ex); }
        }

        // ─── CLIENT: play the heal animation (native HealTargetCrt replay, outcome-neutered) ──────────
        /// <summary>CLIENT inbound (<c>tac.heal.start</c>): resolve healer+ability+target and REPLAY the native
        /// <c>HealAbility.HealTargetCrt</c> with the heal guards active so the real animation plays with NO HP add and
        /// NO charge drain. No AP/WP (we never enter Activate). No-op off-client / off-session / when the seq is stale.
        /// Mirrors <see cref="TacticalMeleeAnimSync.ClientOnMeleeStart"/>.</summary>
        public static void ClientOnHealStart(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeHealStart(payload, out var s)) { Debug.LogError("[Multiplayer][tac] tac.heal.start decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacHealStart, s.Seq)) return;

            object healer = TacticalDeploySync.ResolveLiveActor(s.HealerNetId);
            if (healer == null) { Debug.LogError("[Multiplayer][tac] tac.heal.start: no actor for netId " + s.HealerNetId); return; }

            try
            {
                object ability = ResolveAbilityByGuid(healer, s.AbilityDefGuid);
                if (ability == null) { Debug.LogError("[Multiplayer][tac] tac.heal.start: healer has no ability with guid " + s.AbilityDefGuid); return; }

                object targetActor = TacticalDeploySync.ResolveLiveActor(s.TargetNetId);
                if (targetActor == null) { Debug.LogError("[Multiplayer][tac] tac.heal.start: no target actor for netId " + s.TargetNetId); return; }

                object target = BuildHealTarget(targetActor);
                if (target == null) { Debug.LogError("[Multiplayer][tac] tac.heal.start: could not build heal target"); return; }

                // Enemy-turn chase cam: snap to the healer for the replay (follow=false → one-shot snap). VISIBILITY
                // GATE: snap ONLY to a mirror-visible enemy (heal by enemies is rare but possible). Reuses the 0x97 policy.
                if (ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(TacticalTurnSync.IsClientEnemyTurn, healer != null)
                    && TacticalEnemyTurnCamera.IsActorVisibleToPlayerFaction(healer))
                    TacticalEnemyTurnCamera.ChaseActor(healer, follow: false);

                // HealTargetCrt is private: HealAbility.HealTargetCrt(PlayingAction) — drive it directly (NOT via
                // Activate), exactly like melee drives BashCrt. It reads ONLY action.Param (the heal target).
                var healCrt = AccessTools.Method(ability.GetType(), "HealTargetCrt");
                if (healCrt == null) { Debug.LogError("[Multiplayer][tac] tac.heal.start: HealTargetCrt not found on " + ability.GetType().Name); return; }

                object playingAction = BuildStubPlayingAction(target);
                if (playingAction == null) { Debug.LogError("[Multiplayer][tac] tac.heal.start: could not build PlayingAction"); return; }

                object timing = GetTiming(healer, TacticalDeploySync.LiveTlc);
                if (timing == null) { Debug.LogError("[Multiplayer][tac] tac.heal.start: no Timing to drive the replay"); return; }

                // Drive our own wrapper coroutine: it holds the heal replay guard for the WHOLE inner coroutine
                // (the guard must survive the cross-frame yields), then clears it in finally.
                var crt = ReplayHealCrt(ability, healCrt, playingAction);
                if (!InvokeStart(timing, timing.GetType(), crt))
                {
                    Debug.LogError("[Multiplayer][tac] tac.heal.start: could not start replay coroutine netId=" + s.HealerNetId);
                    return;
                }
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacHealStart, s.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT playing tac.heal.start seq=" + s.Seq + " healer=" + s.HealerNetId +
                          " targetNetId=" + s.TargetNetId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientOnHealStart failed: " + ex); }
        }

        /// <summary>Wrapper coroutine: raise the client-only heal replay guard, run the native
        /// <c>HealTargetCrt</c> to completion, then lower the guard in finally. Holding the guard here (not around the
        /// synchronous Start call) is REQUIRED because the inner coroutine yields across frames and the HP/charge
        /// neuter patches read the guard on those later frames. Identical shape to
        /// <c>TacticalMeleeAnimSync.ReplayMeleeCrt</c>.</summary>
        private static IEnumerator<NextUpdate> ReplayHealCrt(object ability, MethodInfo healCrt, object playingAction)
        {
            _replayDepth++;
            // try/finally in an iterator: the finally runs both on normal completion AND when the scheduler DISPOSES
            // the coroutine (e.g. force-stopped at mission exit) — so the replay guard never leaks mid-mission.
            // (OnMissionExit also calls Reset() as a hard backstop.)
            try
            {
                object inner = null;
                try { inner = healCrt.Invoke(ability, new[] { playingAction }); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.heal.start: HealTargetCrt invoke failed: " + ex); }

                if (inner is IEnumerator e)
                {
                    // Manually pump the inner enumerator so a throw inside it is caught + the guard still lowered.
                    while (true)
                    {
                        bool moved;
                        try { moved = e.MoveNext(); }
                        catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.heal.start replay step failed: " + ex); break; }
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

        // ─── Build the heal target (actor target; HealTargetCrt reads only target.Actor) ──────────────
        /// <summary>Build a <c>TacticalAbilityTarget</c> whose <c>Actor</c> is the healed actor —
        /// <c>HealTargetCrt</c> reads ONLY <c>((TacticalAbilityTarget)action.Param).Actor</c> (HealAbility.cs:67).
        /// Prefer the actor ctor <c>TacticalAbilityTarget(TacticalActorBase, AttackType)</c> (heal ignores AttackType;
        /// Synced is harmless), fall back to a default-ctor + field-set. Mirrors the actor-target build in
        /// <c>TacticalMeleeAnimSync.BuildMeleeTarget</c>.</summary>
        private static object BuildHealTarget(object targetActor)
        {
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) return null;
            var attackType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.AttackType");
            object synced = SyncedAttackType(attackType);

            try
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
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildHealTarget ctor failed, falling back to field-set: " + ex); }

            object target = Activator.CreateInstance(targetType);
            AccessTools.Field(targetType, "Actor")?.SetValue(target, targetActor);
            AccessTools.Field(targetType, "DamageReceiver")?.SetValue(target, targetActor);
            return target;
        }

        private static object SyncedAttackType(Type attackType)
        {
            if (attackType == null) return null;
            try { return Enum.Parse(attackType, "Synced"); } catch { return null; }
        }

        /// <summary>Build a stub <c>PlayingAction</c> whose <c>.Param</c> is the heal target. <c>HealTargetCrt</c>
        /// reads ONLY <c>action.Param</c> (HealAbility.cs:67) — never the ActionComponent / channel / delegates — so a
        /// PlayingAction with everything null except Param drives the heal correctly. Identical to
        /// <c>TacticalMeleeAnimSync.BuildStubPlayingAction</c>.</summary>
        private static object BuildStubPlayingAction(object target)
        {
            try
            {
                var paType = AccessTools.TypeByName("Base.Entities.PlayingAction");
                if (paType == null) { Debug.LogError("[Multiplayer][tac] heal: PlayingAction type not found"); return null; }
                var ctors = paType.GetConstructors();
                if (ctors.Length == 0) return null;
                var ctor = ctors[0];
                var pars = ctor.GetParameters();
                var args = new object[pars.Length];
                bool sawObject = false;
                for (int i = 0; i < pars.Length; i++)
                {
                    var pt = pars[i].ParameterType;
                    if (pt == typeof(object)) { args[i] = target; sawObject = true; continue; }   // the Param slot
                    if (pt.IsValueType) { args[i] = Activator.CreateInstance(pt); continue; }       // ActionChannel enum → 0
                    args[i] = null;                                                                  // ActionComponent / delegates (unused by HealTargetCrt)
                }
                if (!sawObject) { Debug.LogError("[Multiplayer][tac] heal: PlayingAction ctor has no object Param slot"); return null; }
                return ctor.Invoke(args);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] heal BuildStubPlayingAction failed: " + ex); return null; }
        }

        // ─── Engine reflection helpers (mirror TacticalMeleeAnimSync) ────────────────────────────────

        /// <summary>Resolve a <c>TacticalAbility</c> on an actor whose <c>BaseDef.Guid</c> matches (HealAbility).
        /// Identical to <c>TacticalMeleeAnimSync.ResolveAbilityByGuid</c>.</summary>
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
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] (heal) ResolveAbilityByGuid failed: " + ex); }
            return null;
        }

        /// <summary>The target actor's netId from a <c>TacticalAbilityTarget</c> (its <c>Actor</c>); -1 when unreadable.</summary>
        private static int ReadTargetNetId(object parameter)
        {
            if (parameter == null) return -1;
            object actor = GetField(parameter, "Actor");
            if (actor == null) return -1;
            return TacticalDeploySync.NetIdForLiveActor(actor);
        }

        private static object GetTiming(object actor, object tlc)
        {
            object timing = GetProp(actor, "Timing");
            if (timing != null) return timing;
            return tlc != null ? GetProp(tlc, "Timing") : null;
        }

        /// <summary>Find + invoke <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …)</c> — identical to
        /// <c>TacticalMeleeAnimSync.InvokeStart</c> (optional trailing params → Type.Missing).</summary>
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
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] (heal) InvokeStart failed: " + ex); return false; }
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
