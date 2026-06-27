using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multipleer.Harmony.Tactical;
using Multipleer.Network;
using Multipleer.Network.Sync;
using UnityEngine;

// CS0162 (unreachable code): SyncStatuses is a compile-time `const` gate. It is now TRUE (Feature B — visual-
// only status mirror), so the status branches are LIVE; the const remains a single kill-switch should the
// visual mirror ever need to be disabled wholesale. Keep the suppression so flipping the const back to false
// does not break the build on the then-dead branches.
#pragma warning disable CS0162

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// LIVE generic per-actor STATE-DELTA spine (surface <c>tac.actorstate</c> 0x8F, state-spine design §4/§9,
    /// Inc T1). The REUSABLE host→all convergence layer: each flush tick the host builds, per live actor, the
    /// synced fields {AP, WP, status-set(filtered)}, computes a per-actor signature, and broadcasts ONLY the
    /// actors whose signature drifted (idle actor = 0 bytes). The client applies ABSOLUTE values under a
    /// re-entrancy flag and RECONCILES the status set (add-missing / remove-absent by {defGuid, sourceNetId}).
    ///
    /// T1 carries AP/WP (the AP-sync generalized to EVERY actor, not just the shooter at fire time) + the
    /// generic STATUS SET (buffs/debuffs/stances/disables). The wire fieldMask (<see cref="TacticalLiveCodec"/>)
    /// makes it EXTENSIBLE — later increments fold in pos/facing/health/equip/overwatch bits with no wire break.
    ///
    /// ADDITIVE: runs ALONGSIDE the existing per-action surfaces (move/damage/equip/overwatch/vision). AP/WP +
    /// the targeted statuses have no existing owner → no conflict. Statuses OWNED by another surface
    /// (OverwatchStatus → tac.overwatch.state) or DAMAGE-BORNE (DoT family → tac.damage) are EXCLUDED by the pure
    /// policy <see cref="TacticalActorStateDiff.IsSyncableStatusType"/> to avoid double-apply (spec risk #1).
    ///
    /// HOST change-detection: a flush coroutine on the live tactical level's <c>Timing</c> (the same
    /// <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;)</c> mechanism the deploy/move/turn modules use), started at
    /// deploy-capture and self-terminating at mission exit (it watches <c>TacticalDeploySync.LiveTlc</c>). It
    /// flushes every <see cref="FlushFrameInterval"/> frames (~3-4 Hz heartbeat) — the signature pre-check makes
    /// an idle tick ~free. The pure cores (codec + diff + signature + policy) are unit-tested
    /// (<see cref="TacticalLiveCodec"/> / <see cref="TacticalActorStateDiff"/>); this layer is the only reflection
    /// boundary and is in-game verified.
    /// </summary>
    public static class TacticalActorStateSync
    {
        /// <summary>
        /// STATUS-MIRROR GATE — ON (Feature B). The generic delta mirrors every host status whose healthbar
        /// VISIBILITY is not Hidden (<see cref="TacticalActorStateDiff.ShouldMirrorStatus"/>) so its ICON draws
        /// on the client; the mirrored status is made INERT by <c>ClientStatusMirrorGuards</c> (its OnApply/
        /// AfterApply/StartTurn/EndTurn/ApplyEffect/OnUnapply are skipped on the client mirror) so NO gameplay
        /// effect ever runs — no double DoT damage, AP drain, faction flip, or stat double-apply. This SUPERSEDES
        /// the old type-name allowlist (<see cref="TacticalActorStateDiff.IsSyncableStatusType"/>, now unused):
        /// the new policy is "visible-on-healthbar → mirror as inert icon", inertness enforced by the guards
        /// rather than a per-type review. Single kill-switch: flip to false to disable the visual mirror.
        /// </summary>
        internal const bool SyncStatuses = true;

        /// <summary>Heartbeat cadence: flush every N tactical frames. At ~60 fps this is ~4 Hz (idle = 0 bytes
        /// via the signature pre-check). Low enough to be cheap, high enough that a mid-turn AP/WP change
        /// mirrors within ~0.25 s of the host applying it.</summary>
        public const int FlushFrameInterval = 15;

        // Per-actor last-broadcast signature (netId → signature). Skip an actor whose signature is unchanged
        // since the last flush — the dominant traffic saver (idle actors produce 0 bytes). Reset on capture.
        private static readonly Dictionary<int, string> _lastSig = new Dictionary<int, string>();

        // Re-entrancy guard: true only while the CLIENT is applying a host-received delta, so any status
        // apply/unapply we drive does not feed back into a (host-only) broadcast. Defense-in-depth (the flush
        // is IsHost-gated anyway) + keeps client-side status-change side effects from re-entering our apply.
        [ThreadStatic] private static bool _applyingRemote;
        public static bool IsApplyingRemote => _applyingRemote;

        // The TLC the active flush coroutine is bound to (so a re-deploy / mission-exit makes the old loop exit).
        private static object _flushBoundTlc;
        private static bool _flushRunning;

        // ─── HOST: start the flush heartbeat on the level Timing (called at deploy-capture) ─────────────

        /// <summary>HOST: (re)seed the per-actor signature guard so the first flush of a fresh mission always
        /// ships (a stale signature from a prior mission must never suppress it). Called from the deploy capture.</summary>
        public static void HostResetFlushGuard() => _lastSig.Clear();

        /// <summary>HOST: start the flush heartbeat coroutine on the tactical level's <c>Timing</c>. Idempotent
        /// per TLC — a second call for the same live TLC is ignored. No-op off-host / when mirroring / no Timing.
        /// The coroutine self-terminates when <c>LiveTlc</c> changes or clears (mission exit / re-deploy).</summary>
        public static void HostStartFlush(object tacticalLevelController)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (tacticalLevelController == null) return;
            if (_flushRunning && ReferenceEquals(_flushBoundTlc, tacticalLevelController)) return;   // already running for this TLC

            try
            {
                object timing = GetProp(tacticalLevelController, "Timing");
                if (timing == null) { Debug.LogError("[Multipleer][tac] actorstate: no Timing to start flush"); return; }

                _flushBoundTlc = tacticalLevelController;
                HostResetFlushGuard();
                if (InvokeTimingStart(timing, FlushCrt(tacticalLevelController)))
                {
                    _flushRunning = true;
                    Debug.Log("[Multipleer][tac] HOST actorstate flush started (every " + FlushFrameInterval + " frames)");
                }
                else
                {
                    _flushBoundTlc = null;
                    Debug.LogError("[Multipleer][tac] actorstate: Timing.Start failed — flush NOT running");
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HostStartFlush failed: " + ex); }
        }

        /// <summary>The flush heartbeat: every <see cref="FlushFrameInterval"/> frames, broadcast the changed
        /// actors. Self-terminates when the bound TLC is no longer the live one (mission exit / re-deploy) or the
        /// session is no longer a co-op host. MUST be <c>IEnumerator&lt;NextUpdate&gt;</c> for the native
        /// <c>Timing.Start</c> overload (mirrors the deploy/move coroutines).</summary>
        private static IEnumerator<NextUpdate> FlushCrt(object tlc)
        {
            int frame = 0;
            while (true)
            {
                // Exit conditions (checked OUTSIDE try so the iterator can yield-break cleanly).
                if (!ReferenceEquals(TacticalDeploySync.LiveTlc, tlc)) break;   // re-deploy / mission exit
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || !engine.IsHost) break;
                if (TacticalDeploySync.IsClientMirroring) break;

                if (++frame >= FlushFrameInterval)
                {
                    frame = 0;
                    try { HostFlushOnce(engine); }
                    catch (Exception ex) { Debug.LogError("[Multipleer][tac] actorstate flush tick failed: " + ex); }
                }
                yield return NextUpdate.NextFrame;
            }
            _flushRunning = false;
            _flushBoundTlc = null;
            Debug.Log("[Multipleer][tac] HOST actorstate flush stopped");
        }

        /// <summary>HOST: build + broadcast the changed-actor batch ONCE. Walks the registry, reads each actor's
        /// {AP, WP, filtered status set}, signature-skips unchanged actors, and broadcasts the non-empty batch.</summary>
        public static void HostFlushOnce(NetworkEngine engine)
        {
            var registry = TacticalDeploySync.Registry;
            if (registry == null) return;

            // HOST coverage: ground any LIVE map actor missing from the registry (a mid-mission spawn, or a
            // deploy position-drift actor that never bound) via the host mint path BEFORE the walk — so its
            // AP/WP/state still broadcasts. MUST run before iterating registry.Entries (mutating the registry
            // mid-enumeration would throw).
            TacticalDeploySync.HostEnsureLiveActorsRegistered();

            var changed = new List<TacticalLiveCodec.ActorStateRecord>();
            var liveNetIds = new HashSet<int>();

            foreach (var kv in registry.Entries)
            {
                int netId = kv.Key;
                object actor = (kv.Value is TacticalActorAdapter ad) ? ad.Actor : null;
                if (actor == null) continue;
                liveNetIds.Add(netId);

                if (!ReadActorState(actor, out float ap, out float wp, out float health, out Vector3 pos,
                        out bool hasPos, out Vector3 forward, out bool hasFacing, out var statuses, out var bodyParts))
                    continue;   // not a stats-bearing actor (turret/vehicle/destructible) → skip

                // Signature includes AP/WP + HEALTH + POSITION + FACING + the mirrored status set + the bodypart-HP
                // set, so a HEAL, a status icon change, a MOVE, a TURN-IN-PLACE or a limb-HP change re-broadcasts
                // (idle actor = 0 bytes via the unchanged-sig skip). Health + position + facing ride AP/WP (not
                // gated by SyncStatuses) → always in the signature. (Inc1: position drives the client walk/teleport
                // mirror; Inc2: facing drives the client SetForward mirror.)
                string healthSig = "$hp=" + health.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                string posSig = hasPos
                    ? "$p=" + pos.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
                            + pos.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
                            + pos.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    : "";
                // Inc2: facing rides the signature (F2-rounded → dedups to 0.01 precision, like posSig) so a
                // turn-in-place (pos unchanged, facing changed) still re-broadcasts. NOT gated by SyncStatuses.
                string facingSig = hasFacing
                    ? "$f=" + forward.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
                            + forward.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
                            + forward.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    : "";
                string sig = (SyncStatuses
                    ? TacticalActorStateDiff.Signature(ap, wp, statuses) + BodyPartSig(bodyParts)
                    : TacticalActorStateDiff.Signature(ap, wp, null)) + healthSig + posSig + facingSig;
                if (_lastSig.TryGetValue(netId, out var prev) && prev == sig)
                    continue;   // unchanged since last flush → skip (idle = 0 bytes)
                _lastSig[netId] = sig;

                // TASK 2 (HOST staleness): the signature DRIFTED for this actor (AP/WP/status/pos changed) — if the
                // change was a relayed CLIENT action the host executed programmatically (AP→0 + OverwatchStatus), the
                // host's ability bar is never natively re-cycled, so its buttons stay lit. Re-stamp it here. No-op
                // unless the host's currently-selected actor IS this netId (host's OWN soldiers grey natively on
                // re-select), and it reads the settled post-SetToMin AP. Pure view re-push, not a state mutation.
                RefreshHostSelectedBarForActor(netId);

                ushort mask = (ushort)(TacticalLiveCodec.ActorFieldAp | TacticalLiveCodec.ActorFieldWp);
                // Feature D: ship the actor-level HEALTH bit ONLY when HP > 0. Death (HP <= 0) is owned by
                // tac.damage (0x88); the client apply is death-safe anyway (ShouldApplyHealthMirror skips <= 0),
                // but not setting the bit at all keeps a dead/dying actor's delta clean.
                bool shipHealth = health > TacticalActorStateDiff.HealthDeathThreshold;
                if (shipHealth) mask |= TacticalLiveCodec.ActorFieldHealth;
                // Inc1 full-state: ship the absolute POSITION bit whenever the actor exposes a readable Pos.
                // NOT gated by SyncStatuses (it is core movement state, like Health). The client turns it into a
                // native walk (or a snap) — see TacticalMoveSync.ApplyMirrorPosition / DecidePositionApply.
                if (hasPos) mask |= TacticalLiveCodec.ActorFieldPos;
                // Inc2: ship the absolute FACING bit whenever the actor exposes a readable forward. NOT gated by
                // SyncStatuses (core presentation state, like Pos). The client applies it via ActorComponent.SetForward
                // (skip-while-navigating) — see TacticalMoveSync.ApplyMirrorFacing.
                if (hasFacing) mask |= TacticalLiveCodec.ActorFieldFacing;
                if (SyncStatuses)
                {
                    mask |= TacticalLiveCodec.ActorFieldStatuses;
                    if (bodyParts.Count > 0) mask |= TacticalLiveCodec.ActorFieldBodyPartHp;
                }
                var rec = new TacticalLiveCodec.ActorStateRecord
                {
                    NetId = netId,
                    FieldMask = mask,
                    Ap = ap,
                    Wp = wp,
                    Health = health,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    FacingX = forward.x,
                    FacingY = forward.y,
                    FacingZ = forward.z,
                };
                if (SyncStatuses)
                {
                    foreach (var s in statuses)
                        rec.Statuses.Add(new TacticalLiveCodec.ActorStatus(s.DefGuid, s.SourceNetId, s.Value));
                    foreach (var b in bodyParts)
                        rec.BodyParts.Add(new TacticalLiveCodec.BodyPartHp(b.SlotName, b.Hp));
                }
                changed.Add(rec);
            }

            // Drop signatures for actors that left the registry (death/despawn) so a re-registered netId re-ships.
            if (_lastSig.Count > liveNetIds.Count)
            {
                var stale = new List<int>();
                foreach (var id in _lastSig.Keys) if (!liveNetIds.Contains(id)) stale.Add(id);
                foreach (var id in stale) _lastSig.Remove(id);
            }

            if (changed.Count == 0) return;

            uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacActorState);
            byte[] payload = TacticalLiveCodec.EncodeActorState(new TacticalLiveCodec.ActorStateBatch(seq, changed));
            TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacActorState, payload);
            Debug.Log("[Multipleer][tac] HOST broadcast tac.actorstate seq=" + seq + " changedActors=" + changed.Count);
        }

        // ─── CLIENT: apply a host state delta ───────────────────────────────────────────────────────────

        /// <summary>CLIENT inbound (<c>tac.actorstate</c>): seq-guard, then per-actor resolve + apply under
        /// <see cref="_applyingRemote"/>: set AP/WP absolute (only if the field bit is set) and reconcile the
        /// status set (add-missing / remove-absent by {defGuid, sourceNetId}). Idempotent. No-op on host.</summary>
        public static void HandleActorState(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeActorState(payload, out var batch))
            { Debug.LogError("[Multipleer][tac] tac.actorstate decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacActorState, batch.Seq)) return;

            int applied = 0, apwp = 0, sAdd = 0, sRem = 0, sRef = 0, bp = 0, hp = 0, posCnt = 0, facingCnt = 0;
            // TASK 3: netIds whose AP/WP this batch actually wrote — used AFTER the apply loop to re-grey the ability bar
            // if the client's currently-selected actor is among them (the async AP delta arrives after the activation-time
            // re-grey, so the bar can otherwise stay lit). Collected under the apply, acted on once below.
            var apAppliedNetIds = new HashSet<int>();
            try
            {
                _applyingRemote = true;
                try
                {
                    foreach (var rec in batch.Actors)
                    {
                        object actor = TacticalDeploySync.ResolveLiveActor(rec.NetId);
                        if (actor == null)
                            // CLIENT coverage: the netId is unbound (deploy position drift or a mid-mission
                            // spawn). LAZY re-bind against the live map via the existing deploy matcher
                            // (GeoUnitId-exact preferred; position fallback within PosEpsilon) before dropping.
                            actor = TacticalDeploySync.ClientTryLazyRebind(rec.NetId, rec.HasPos, rec.PosX, rec.PosY, rec.PosZ);
                        if (actor == null) continue;   // truly absent on the frozen client (host-only spawn) → drop (birth)
                        applied++;

                        // APPLY ORDER: reconcile STATUSES FIRST, then set AP/WP ABSOLUTE LAST — so the host's
                        // authoritative absolute AP/WP always WINS over any stat change a status touches (the
                        // mirrored statuses are INERT via the guards, so they apply no stat delta anyway, but
                        // the order is kept correct). Body-part HP is independent (own stat) → apply any time.
                        if (rec.HasStatuses)
                        {
                            ReconcileStatuses(actor, rec.Statuses, ref sAdd, ref sRem, ref sRef);
                        }
                        if (rec.HasBodyParts)
                        {
                            bp += ApplyBodyPartHp(actor, rec.BodyParts);
                        }
                        // Feature D: actor-level absolute HEALTH (heal / drift correction), DEATH-SAFE. The host
                        // only ships the bit when HP > 0, but ShouldApplyHealthMirror double-guards: a non-positive
                        // value is NEVER set (death owned by tac.damage), and an unchanged value is a no-op.
                        if (rec.HasHealth)
                        {
                            if (ApplyHealthMirror(actor, rec.Health)) hp++;
                        }
                        if (rec.HasAp || rec.HasWp)
                        {
                            if (SetApWpAbsolute(actor, rec)) { apwp++; apAppliedNetIds.Add(rec.NetId); }
                        }
                        // Inc2: apply the host's ABSOLUTE facing BEFORE position. A stationary actor keeps the host
                        // heading (turn-in-place); a moved actor's Pos may start a Walk — which owns rotation and
                        // makes ApplyMirrorFacing SKIP (skip-while-navigating) — then the next heartbeat converges
                        // the final facing. SetForward is absolute/idempotent + sub-epsilon-skipped (no churn / no
                        // ActorMovedEvent re-fire) — see TacticalMoveSync.ApplyMirrorFacing.
                        if (rec.HasFacing)
                        {
                            if (TacticalMoveSync.ApplyMirrorFacing(
                                    actor, new Vector3(rec.FacingX, rec.FacingY, rec.FacingZ))) facingCnt++;
                        }
                        // Inc1 full-state: drive the actor toward the host's ABSOLUTE position. The native walk
                        // animation (or an instant snap for a sub-cell nudge / disconnected jump) is triggered by
                        // TacticalMoveSync.ApplyMirrorPosition, which reuses the SAME mirror NavigationSettings +
                        // walk/teleport primitives as the tac.move.start rail and SKIPS re-animating an actor that
                        // the move rail already set navigating (no double-animate while running ADDITIVE). Applied
                        // LAST (after stats) so the transform move lands on a fully-converged actor.
                        if (rec.HasPos)
                        {
                            if (TacticalMoveSync.ApplyMirrorPosition(
                                    actor, new Vector3(rec.PosX, rec.PosY, rec.PosZ))) posCnt++;
                        }
                    }
                }
                finally { _applyingRemote = false; }

                // TASK 3: re-grey the ability bar at the AP-DELTA apply site (re-grey site #2). Done OUTSIDE the
                // _applyingRemote guard (it is a pure view re-push, not a state apply). No-op unless the selected actor's
                // AP changed this batch.
                ReGreySelectedBarAfterApDelta(apAppliedNetIds);

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacActorState, batch.Seq);
                if (applied > 0 || sAdd > 0 || sRem > 0 || sRef > 0 || bp > 0 || hp > 0 || posCnt > 0 || facingCnt > 0)
                    Debug.Log("[Multipleer][tac] CLIENT applied tac.actorstate seq=" + batch.Seq +
                              " actors=" + applied + " apwpSet=" + apwp + " status+" + sAdd + " status-" + sRem +
                              " status~" + sRef + " limbHp=" + bp + " hp=" + hp + " pos=" + posCnt + " facing=" + facingCnt);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HandleActorState failed: " + ex); }
        }

        /// <summary>AP-delta re-grey (CLIENT, re-grey site #2). When the async host AP/WP delta lands for the
        /// client's CURRENTLY-SELECTED actor, DIRECTLY re-push the ability bar via
        /// <c>UIModuleAbilities.SetAbilities(selected, Context.Input)</c> (UIModuleAbilities.cs:112) — exactly the
        /// native call from <c>UIStateCharacterSelected.cs:247</c> — so every ability button re-evaluates
        /// <c>IsEnabled()</c> against the now-decremented AP (button lit/grey state is stamped once and only
        /// re-evaluated on a re-push). This REPLACES the old <c>TacticalView.ResetCharacterSelectedState()</c>
        /// trigger, which SELF-GUARDS on <c>CurrentState is UIStateCharacterSelected</c> (TacticalView.cs:308) and
        /// so NO-OPs while the view sits in a shoot/melee/overwatch sub-state — leaving the buttons lit. The direct
        /// refresh is state-INDEPENDENT. The activation-time re-grey (<c>SuppressedAbilityViewClearPatch</c>) runs
        /// BEFORE this delta arrives, so this is the second, robust trigger. NRE-guarded: no live view / no
        /// selection / actor not AP-updated this batch → no-op.</summary>
        private static void ReGreySelectedBarAfterApDelta(HashSet<int> apAppliedNetIds)
        {
            try
            {
                if (apAppliedNetIds == null || apAppliedNetIds.Count == 0) return;
                int selNet = ResolveSelectedActorNet(out object view, out object selected);
                if (selNet < 0 || !apAppliedNetIds.Contains(selNet)) return; // selected actor's AP didn't change → nothing to re-grey
                DoDirectAbilityBarRefresh(view, selected, selNet, "CLIENT@apdelta");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] re-grey@apdelta failed: " + ex); }
        }

        /// <summary>TASK 2 (HOST staleness). After the host EXECUTES a relayed CLIENT action programmatically
        /// (<c>TacticalCombatSync.HostOnAbilityIntent</c> → <c>Activate</c>), it never UI-selected that soldier, so
        /// the host's ability bar is not natively re-cycled. If the host UI happens to have THAT actor selected, do
        /// the SAME direct, state-independent <c>UIModuleAbilities.SetAbilities</c> refresh as the client re-grey.
        /// No-op unless the host's currently-selected actor IS <paramref name="actorNetId"/> — so the host's OWN
        /// soldiers (which grey natively on re-select) are never touched. Runs on the HOST only.</summary>
        public static void RefreshHostSelectedBarForActor(int actorNetId)
        {
            try
            {
                if (actorNetId < 0) return;
                int selNet = ResolveSelectedActorNet(out object view, out object selected);
                if (selNet < 0 || selNet != actorNetId) return;   // host has a different (or no) actor selected → nothing stale
                DoDirectAbilityBarRefresh(view, selected, selNet, "HOST@relay");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] host re-grey failed: " + ex); }
        }

        /// <summary>Resolve the live tactical view's currently-SELECTED actor + its netId (-1 when none / unresolved).
        /// Shared by the client AP-delta re-grey and the host post-relay re-grey. The out params are the live
        /// <c>TacticalView</c> and the selected <c>TacticalActor</c> (both null when -1 is returned).</summary>
        private static int ResolveSelectedActorNet(out object view, out object selected)
        {
            view = null; selected = null;
            view = GetProp(TacticalDeploySync.LiveTlc, "View");          // TacticalLevelController.View → TacticalView
            if (view == null) return -1;
            selected = GetProp(view, "SelectedActor");                  // TacticalView.SelectedActor (TacticalView.cs:148)
            if (selected == null) return -1;
            return TacticalDeploySync.NetIdForLiveActor(selected);
        }

        /// <summary>DIRECT, state-independent ability-bar refresh: <c>UIModuleAbilities.SetAbilities(selected,
        /// Context.Input)</c> (UIModuleAbilities.cs:112) — the exact native call from
        /// <c>UIStateCharacterSelected.cs:247</c>. Reaches the module the native way:
        /// <c>TacticalView.TacticalModules</c> (TacticalView.cs:114) <c>.AbilitiesModule</c>
        /// (TacticalModulesData.cs:18, a <c>UIModuleAbilities</c>); and the input via
        /// <c>TacticalView._context.Input</c> (TacticalView.cs:91 → TacticalViewContext.Input,
        /// TacticalViewContext.cs:15, == <c>GameUtl.GameComponent&lt;InputController&gt;()</c>). Bypasses
        /// <c>ResetCharacterSelectedState</c>'s state self-guard so it re-stamps the buttons in ANY view sub-state.
        /// Fully NRE-guarded; diag logs the caller tag + the view CurrentState type + the actor netId.</summary>
        private static void DoDirectAbilityBarRefresh(object view, object selected, int selNet, string who)
        {
            object modules = GetProp(view, "TacticalModules");           // TacticalView.TacticalModules : TacticalModulesData
            object abilitiesModule = GetProp(modules, "AbilitiesModule"); // TacticalModulesData.AbilitiesModule : UIModuleAbilities
            if (abilitiesModule == null) return;
            object ctx = GetField(view, "_context");                     // TacticalView._context : TacticalViewContext
            object input = GetProp(ctx, "Input");                        // TacticalViewContext.Input : InputController
            if (input == null) return;

            string stateName = "<null>";
            try { object cs = GetProp(view, "CurrentState"); stateName = cs?.GetType().Name ?? "<null>"; } catch { /* diag only */ }

            var setAbilities = AccessTools.Method(abilitiesModule.GetType(), "SetAbilities"); // (TacticalActor, InputController) — single overload
            Debug.Log("[Multipleer][tac] " + who + " direct SetAbilities found=" + (setAbilities != null) +
                      " state=" + stateName + " actorNet=" + selNet);
            setAbilities?.Invoke(abilitiesModule, new[] { selected, input });
        }

        // ─── HOST read helpers (AP/WP + filtered status set) ────────────────────────────────────────────

        /// <summary>Read {AP, WP, current HEALTH, filtered status set, bodypart HP} for an actor. Returns FALSE
        /// when the actor has no CharacterStats (turret/vehicle/destructible) — the caller must NOT then ship
        /// 0/0 as authoritative. Statuses are filtered by <see cref="TacticalActorStateDiff.ShouldMirrorStatus"/>
        /// (visible-on-healthbar, non-surface-owned). Source→netId via the registry (-1 when unresolvable).
        /// <paramref name="health"/> is the actor's CURRENT HP (Feature D); the host only ships it when > 0
        /// (see HostFlushOnce) — death is owned by tac.damage, never this delta.</summary>
        private static bool ReadActorState(object actor, out float ap, out float wp, out float health,
            out Vector3 pos, out bool hasPos, out Vector3 forward, out bool hasFacing,
            out List<TacticalActorStateDiff.StatusRec> statuses,
            out List<TacticalActorStateDiff.BodyPartHpRec> bodyParts)
        {
            ap = 0f; wp = 0f; health = 0f; pos = Vector3.zero; hasPos = false;
            forward = Vector3.forward; hasFacing = false;
            statuses = new List<TacticalActorStateDiff.StatusRec>();
            bodyParts = new List<TacticalActorStateDiff.BodyPartHpRec>();

            object stats = GetProp(actor, "CharacterStats");
            if (stats == null) return false;                       // not a stats-bearing TacticalActor
            object apStat = GetField(stats, "ActionPoints");
            object wpStat = GetField(stats, "WillPoints");
            if (apStat == null || wpStat == null) return false;
            ap = StatValue(apStat);
            wp = StatValue(wpStat);

            // Feature D: actor-level CURRENT HP (CharacterStats.Health, a StatusStat read via the same
            // op_Implicit float as AP/WP). Best-effort: 0 when unreadable (a 0/no-Health actor is then NOT
            // health-mirrored — HostFlushOnce only sets the Health bit when health > 0).
            health = StatValue(GetProp(stats, "Health"));

            // Inc1 full-state: the actor's ABSOLUTE world position (ActorComponent.Pos = transform.position).
            // Read off the live actor (not CharacterStats) and shipped unconditionally (core movement state, NOT
            // gated by SyncStatuses). A NaN/unreadable Pos is dropped (hasPos stays false → no Pos bit).
            if (TryReadPos(actor, out Vector3 p)) { pos = p; hasPos = true; }

            // Inc2: actor facing as a forward vector. ActorComponent.Rot is the world rotation (ActorComponent.cs:43);
            // forward = Rot * Vector3.forward. Shipped unconditionally (core presentation state, like Pos), NOT gated
            // by SyncStatuses. A NaN/unreadable rotation is dropped (hasFacing stays false → no Facing bit).
            if (TryReadForward(actor, out Vector3 fwd)) { forward = fwd; hasFacing = true; }

            // Single kill-switch (Feature B): when off, the wire + signature carry only AP/WP/HEALTH/POSITION.
            if (!SyncStatuses) return true;

            // Statuses: enumerate StatusComponent.Statuses, mirror those whose healthbar visibility != Hidden
            // (visual-only icon), flatten {defGuid, sourceNetId, value}. The mirrored status is made INERT on
            // the client by ClientStatusMirrorGuards — so no effect runs there.
            object statusComponent = GetProp(actor, "Status");
            if (statusComponent != null && GetProp(statusComponent, "Statuses") is IEnumerable list)
            {
                foreach (var st in list)
                {
                    if (st == null) continue;
                    object def = GetProp(st, "Def");
                    if (!TacticalActorStateDiff.ShouldMirrorStatus(ReadVisibility(def), st.GetType().Name)) continue;
                    string guid = DefReflection.GetGuid(def);
                    if (string.IsNullOrEmpty(guid)) continue;       // un-resolvable def → can't reconcile cross-side
                    int sourceNetId = ResolveSourceNetId(GetProp(st, "Source"));
                    float value = ReadStatusValue(st);
                    statuses.Add(new TacticalActorStateDiff.StatusRec(guid, sourceNetId, value));
                }
            }

            // Body-part HP (Feature B PART 1): each health slot's absolute HP, keyed by slot name (stable
            // host↔client). The client sets these so the native StatChangeEvent drives the disabled-limb UI.
            ReadBodyPartHp(actor, bodyParts);
            return true;
        }

        /// <summary>Read a status def's <c>TacStatusDef.VisibleOnHealthbar</c> enum as an int (Hidden=0 default).
        /// A def without the field (a non-tactical status) reads as Hidden → not mirrored.</summary>
        private static int ReadVisibility(object def)
        {
            if (def == null) return TacticalActorStateDiff.HealthBarVisibilityHidden;
            try
            {
                object v = GetField(def, "VisibleOnHealthbar");
                return v == null ? TacticalActorStateDiff.HealthBarVisibilityHidden : Convert.ToInt32(v);
            }
            catch { return TacticalActorStateDiff.HealthBarVisibilityHidden; }
        }

        /// <summary>Enumerate the actor's body-part health slots (<c>TacticalActor.BodyState.GetHealthSlots()</c>)
        /// and flatten {slotName, currentHp}. Slot name (<c>ItemSlot.GetSlotName()</c>) is the stable cross-side
        /// key; HP is the slot's <c>GetHealth()</c> StatusStat current value. Best-effort: silently skips an
        /// actor with no body state (turret/destructible).</summary>
        private static void ReadBodyPartHp(object actor, List<TacticalActorStateDiff.BodyPartHpRec> outList)
        {
            try
            {
                object bodyState = GetProp(actor, "BodyState");
                if (bodyState == null) return;
                var getHealthSlots = AccessTools.Method(bodyState.GetType(), "GetHealthSlots");
                if (getHealthSlots == null) return;
                if (!(getHealthSlots.Invoke(bodyState, null) is IEnumerable slots)) return;
                foreach (var slot in slots)
                {
                    if (slot == null) continue;
                    string slotName = InvokeGetSlotName(slot);
                    if (string.IsNullOrEmpty(slotName)) continue;
                    object healthStat = InvokeGetHealth(slot);
                    if (healthStat == null) continue;
                    outList.Add(new TacticalActorStateDiff.BodyPartHpRec(slotName, StatValue(healthStat)));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] actorstate ReadBodyPartHp failed: " + ex); }
        }

        /// <summary>Inc1 full-state: read the actor's absolute world position (<c>ActorComponent.Pos</c> =
        /// transform.position). Returns false (so no Pos bit is shipped) when Pos is unreadable or NaN — a
        /// turret/destructible without a transform, or a mid-spawn actor. Mirrors <c>TacticalMoveSync.GetPos</c>
        /// but rejects NaN (a half-initialized transform must not ship a garbage position).</summary>
        private static bool TryReadPos(object actor, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                object raw = GetProp(actor, "Pos");
                if (!(raw is Vector3 v)) return false;
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) return false;
                pos = v;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Inc2: read the actor's absolute world FORWARD vector (<c>ActorComponent.Rot</c> =
        /// transform.rotation, ActorComponent.cs:43) as <c>Rot * Vector3.forward</c>. Returns false (so no Facing
        /// bit is shipped) when the rotation is unreadable or the resulting forward is NaN — a turret/destructible
        /// without a transform, or a mid-spawn actor. Mirrors <see cref="TryReadPos"/>.</summary>
        private static bool TryReadForward(object actor, out Vector3 forward)
        {
            forward = Vector3.forward;
            try
            {
                object raw = GetProp(actor, "Rot");          // ActorComponent.Rot : Quaternion
                if (!(raw is Quaternion q)) return false;
                Vector3 f = q * Vector3.forward;
                if (float.IsNaN(f.x) || float.IsNaN(f.y) || float.IsNaN(f.z)) return false;
                forward = f;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Map a status <c>Source</c> to a netId if it is a resolvable live actor, else -1 (weapon /
        /// global / null source). The pair {defGuid, sourceNetId} is the cross-side reconcile identity.</summary>
        private static int ResolveSourceNetId(object source)
        {
            if (source == null) return TacticalActorStateDiff.SourceNetIdNone;
            try
            {
                int netId = TacticalDeploySync.NetIdForLiveActor(source);
                return netId >= 0 ? netId : TacticalActorStateDiff.SourceNetIdNone;
            }
            catch { return TacticalActorStateDiff.SourceNetIdNone; }
        }

        /// <summary>The status' carried DISPLAY magnitude — its <c>Status.Value</c> (BleedStatus.Value = (int)bleed
        /// level; DamageOverTimeStatus.Value = level = InitialAmount/DamagePerTurn). Drives the signature (a
        /// magnitude change re-broadcasts) AND is reconstructed on the inert client mirror (bug C: was
        /// <c>Duration</c>, so the mirror always showed level 0; magnitude used to ride tac.damage 0x88, now
        /// retired — canon inv 2). Best-effort: 0 when unreadable. (BleedStatus.cs:29; DamageOverTimeStatus.cs:25)</summary>
        private static float ReadStatusValue(object status)
        {
            try { object v = GetProp(status, "Value"); return v != null ? Convert.ToSingle(v) : 0f; }
            catch { return 0f; }
        }

        /// <summary>Order-stable signature fragment over the bodypart-HP set (sorted by slot name) so a limb-HP
        /// change re-broadcasts the actor. Appended to the AP/WP/status signature.</summary>
        private static string BodyPartSig(List<TacticalActorStateDiff.BodyPartHpRec> parts)
        {
            if (parts == null || parts.Count == 0) return "";
            var list = new List<TacticalActorStateDiff.BodyPartHpRec>(parts);
            list.Sort((a, b) => string.CompareOrdinal(a.SlotName ?? "", b.SlotName ?? ""));
            var sb = new System.Text.StringBuilder("@");
            foreach (var p in list)
                sb.Append(p.SlotName ?? "").Append('=')
                  .Append(p.Hp.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            return sb.ToString();
        }

        // ─── CLIENT apply helpers ───────────────────────────────────────────────────────────────────────

        /// <summary>Apply host-absolute per-bodypart HP to the client actor's matching health slots. For each
        /// incoming {slotName, hp} whose client HP drifted (<see cref="TacticalActorStateDiff.ComputeBodyPartHpDiff"/>),
        /// <c>ItemSlot.GetHealth().Set(hp, triggerStatChangeEvent:true)</c> — the native StatChangeEvent then
        /// fires <c>OnBodyPartHealthChanged</c> → the disabled-limb UI updates for FREE (limb-disable is NOT a
        /// status). Idempotent: an unchanged part is skipped (no churn, no event). Returns the count applied.</summary>
        private static int ApplyBodyPartHp(object actor, List<TacticalLiveCodec.BodyPartHp> incoming)
        {
            if (incoming == null || incoming.Count == 0) return 0;
            object bodyState = GetProp(actor, "BodyState");
            if (bodyState == null) return 0;
            var getHealthSlots = AccessTools.Method(bodyState.GetType(), "GetHealthSlots");
            if (getHealthSlots == null) return 0;

            // Build the client's current {slotName → hp} + a lookup back to the live ItemSlot for the apply.
            var current = new Dictionary<string, float>();
            var slotByName = new Dictionary<string, object>();
            if (getHealthSlots.Invoke(bodyState, null) is IEnumerable slots)
            {
                foreach (var slot in slots)
                {
                    if (slot == null) continue;
                    string name = InvokeGetSlotName(slot);
                    if (string.IsNullOrEmpty(name)) continue;
                    object health = InvokeGetHealth(slot);
                    if (health == null) continue;
                    current[name] = StatValue(health);
                    slotByName[name] = slot;
                }
            }

            var inc = new List<TacticalActorStateDiff.BodyPartHpRec>(incoming.Count);
            foreach (var b in incoming) inc.Add(new TacticalActorStateDiff.BodyPartHpRec(b.SlotName, b.Hp));

            int applied = 0;
            foreach (var toApply in TacticalActorStateDiff.ComputeBodyPartHpDiff(current, inc))
            {
                if (!slotByName.TryGetValue(toApply.SlotName, out var slot) || slot == null) continue;
                object health = InvokeGetHealth(slot);
                if (health == null) continue;
                SetStat(health, toApply.Hp);   // fires StatChangeEvent → OnBodyPartHealthChanged → UI
                applied++;
            }
            return applied;
        }

        /// <summary>Invoke <c>ItemSlot.GetSlotName()</c> → string (the stable host↔client bodypart key).</summary>
        private static string InvokeGetSlotName(object slot)
        {
            try
            {
                var m = AccessTools.Method(slot.GetType(), "GetSlotName");
                return m?.Invoke(slot, null) as string;
            }
            catch { return null; }
        }

        /// <summary>Invoke <c>ItemSlot.GetHealth()</c> → the slot's HP StatusStat (read via the BaseStat implicit
        /// float, written via <c>Set(float,bool)</c>).</summary>
        private static object InvokeGetHealth(object slot)
        {
            try
            {
                var m = AccessTools.Method(slot.GetType(), "GetHealth");
                return m?.Invoke(slot, null);
            }
            catch { return null; }
        }

        /// <summary>Set the actor's AP/WP to the host-absolute values via <c>BaseStat.Set(float,bool)</c> (only
        /// the fields whose bit is set). Returns true if at least one stat was set.</summary>
        private static bool SetApWpAbsolute(object actor, TacticalLiveCodec.ActorStateRecord rec)
        {
            object stats = GetProp(actor, "CharacterStats");
            if (stats == null) return false;
            bool any = false;
            float apBefore = 0f, apAfter = 0f, wpBefore = 0f, wpAfter = 0f;
            if (rec.HasAp)
            {
                object apStat = GetField(stats, "ActionPoints");
                apBefore = StatValue(apStat); SetStat(apStat, rec.Ap); apAfter = StatValue(apStat); any = true;
            }
            if (rec.HasWp)
            {
                object wpStat = GetField(stats, "WillPoints");
                wpBefore = StatValue(wpStat); SetStat(wpStat, rec.Wp); wpAfter = StatValue(wpStat); any = true;
            }
            // DIAG (co-op tactical client-mirror path only — runs solely under HandleActorState): trace the
            // authoritative AP/WP write so a clean re-test can confirm whether the client actually receives + applies
            // the host's decremented AP (AP-not-greying RCA). Cheap: one line per actor that carried an AP/WP bit.
            if (any)
                Debug.Log("[Multipleer][tac] SetApWpAbsolute netId=" + rec.NetId +
                          (rec.HasAp ? (" AP " + apBefore + "->" + apAfter) : "") +
                          (rec.HasWp ? (" WP " + wpBefore + "->" + wpAfter) : ""));
            return any;
        }

        /// <summary>Feature D — apply the host-absolute actor HP to the client mirror, DEATH-SAFE. Reads the
        /// client's current <c>CharacterStats.Health</c>, asks the pure
        /// <see cref="TacticalActorStateDiff.ShouldApplyHealthMirror"/> whether/what to set, then writes via
        /// <see cref="SetStat"/> (<c>BaseStat.Set(float,true)</c> → fires <c>StatChangeType.Value</c> → the
        /// healthbar updates). DEATH PROOF: the engine's <c>TacticalActorBase.OnHealthChange</c> calls
        /// <c>Die()</c> ONLY when a Value change crosses <c>prevValue &gt;= 1E-05 → Value &lt; 1E-05</c>;
        /// <see cref="TacticalActorStateDiff.ShouldApplyHealthMirror"/> only ever returns a value
        /// <c>&gt; 1E-05</c>, so the set can never cross to death — and a non-positive host HP is dropped here
        /// (death stays owned by tac.damage 0x88). Returns true when a value was actually written.</summary>
        private static bool ApplyHealthMirror(object actor, float incomingHp)
        {
            object stats = GetProp(actor, "CharacterStats");
            if (stats == null) return false;
            object healthStat = GetProp(stats, "Health");
            if (healthStat == null) return false;
            float current = StatValue(healthStat);
            if (!TacticalActorStateDiff.ShouldApplyHealthMirror(current, incomingHp, out float toSet)) return false;
            SetStat(healthStat, toSet);   // Set(float,true) → StatChangeType.Value → healthbar; toSet>0 → no Die()
            return true;
        }

        /// <summary>Reconcile the actor's MIRRORED status set to the incoming set: ApplyStatus the genuinely-
        /// MISSING ones (def resolved by guid; source = the resolved live actor or null) as INERT mirrors,
        /// UnapplyStatus the absent ones. A status already present (by {defGuid, sourceNetId}) is left untouched
        /// so its OnApply never re-runs. Only VISIBLE-on-healthbar statuses participate in the "current" set, so
        /// a non-mirrored / surface-owned status (Overwatch) is never seen as "extra" and removed.</summary>
        private static void ReconcileStatuses(object actor, List<TacticalLiveCodec.ActorStatus> incoming,
            ref int addCount, ref int removeCount, ref int refreshCount)
        {
            object statusComponent = GetProp(actor, "Status");
            if (statusComponent == null) return;

            // Build the CLIENT's current mirrored set + a lookup back to the live Status object for removal.
            var current = new List<TacticalActorStateDiff.StatusRec>();
            var liveByKey = new Dictionary<string, object>();
            if (GetProp(statusComponent, "Statuses") is IEnumerable list)
            {
                foreach (var st in list)
                {
                    if (st == null) continue;
                    object def = GetProp(st, "Def");
                    if (!TacticalActorStateDiff.ShouldMirrorStatus(ReadVisibility(def), st.GetType().Name)) continue;
                    string guid = DefReflection.GetGuid(def);
                    if (string.IsNullOrEmpty(guid)) continue;
                    int src = ResolveSourceNetId(GetProp(st, "Source"));
                    float val = ReadStatusValue(st);
                    var rec = new TacticalActorStateDiff.StatusRec(guid, src, val);
                    current.Add(rec);
                    liveByKey[TacticalActorStateDiff.KeyOf(rec)] = st;
                }
            }

            var inc = new List<TacticalActorStateDiff.StatusRec>(incoming.Count);
            foreach (var s in incoming)
                inc.Add(new TacticalActorStateDiff.StatusRec(s.DefGuid, s.SourceNetId, s.Value));

            var diff = TacticalActorStateDiff.Compute(current, inc);

            // Inc2 follow-up: in-place MAGNITUDE refresh for an ALREADY-PRESENT status whose host display value
            // DRIFTED (bleed stacking from a 2nd shot, a DoT ticking down each turn). The diff identity
            // {DefGuid, SourceNetId} ignores Value, so a present-key magnitude change is in NEITHER ToAdd nor
            // ToRemove — the mirror would go stale. Refresh keys are disjoint from ToAdd (absent on client) and
            // ToRemove (absent on host), so this pass is independent of + safe alongside add/remove, and MUST run
            // even when the diff is otherwise empty (a pure value drift). Re-derives the mirror's
            // DamageAccumulation.InitialAmount IN PLACE — no remove+re-add → OnApply never re-runs.
            RefreshPresentStatusMagnitudes(current, inc, liveByKey, ref refreshCount);

            if (!diff.HasChanges) return;

            // REMOVE absent first, then ADD missing. TASK 3: each per-status apply is ISOLATED in try/catch so one
            // bad status (e.g. a mirrored status whose OnApply NREs on the inert client mirror — BleedStatus.OnApply
            // foreach-iterates the null _slotNames that its Applied=true inert branch never populated) NEVER aborts
            // the remaining statuses or the rest of the actor-state apply (AP/WP/health/pos). The inner Invoke*
            // helpers already swallow + return false; this loop guard additionally covers any throw BEFORE the
            // invoke (def/source resolution). On failure: log the status id + exception type/message, then CONTINUE.
            foreach (var r in diff.ToRemove)
            {
                try
                {
                    if (liveByKey.TryGetValue(TacticalActorStateDiff.KeyOf(r), out var liveStatus) && liveStatus != null)
                    {
                        if (InvokeUnapplyStatus(statusComponent, liveStatus)) removeCount++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Multipleer][tac] status mirror failed: " + (r.DefGuid ?? "<null>") +
                                   " " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            foreach (var a in diff.ToAdd)
            {
                try
                {
                    object def = DefReflection.GetDefByGuid(a.DefGuid);
                    if (def == null) continue;
                    object source = a.SourceNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(a.SourceNetId) : null;
                    // a.Value = host Status.Value (display magnitude) → reconstructed on the inert mirror (bug C).
                    if (InvokeApplyStatus(statusComponent, def, source, actor, a.Value)) addCount++;
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Multipleer][tac] status mirror failed: " + (a.DefGuid ?? "<null>") +
                                   " " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>Inc2 follow-up: refresh the IN-PLACE display magnitude of every ALREADY-MIRRORED status whose
        /// host value drifted beyond <see cref="TacticalActorStateDiff.StatusMagnitudeEpsilon"/>. For each incoming
        /// status whose {DefGuid, SourceNetId} is ALSO present on the client (so the reconcile add/remove never
        /// touches it), compare the mirrored display Value against the incoming one; on real drift, re-derive + set
        /// the live mirror's <c>DamageAccumulation.InitialAmount</c> via <see cref="RefreshMirrorMagnitude"/>. The
        /// pure decision <see cref="TacticalActorStateDiff.ShouldRefreshMagnitude"/> drives the gate; each per-status
        /// write is isolated in try/catch so one bad status never aborts the rest of the reconcile.</summary>
        private static void RefreshPresentStatusMagnitudes(
            List<TacticalActorStateDiff.StatusRec> current,
            List<TacticalActorStateDiff.StatusRec> incoming,
            Dictionary<string, object> liveByKey,
            ref int refreshCount)
        {
            // The mirror's current display value by key (taken from the already-built current set — no re-read).
            var mirroredByKey = new Dictionary<string, float>();
            foreach (var c in current) mirroredByKey[TacticalActorStateDiff.KeyOf(c)] = c.Value;

            foreach (var s in incoming)
            {
                string key = TacticalActorStateDiff.KeyOf(s);
                if (!mirroredByKey.TryGetValue(key, out float mirroredVal)) continue;           // absent on client → ToAdd owns it
                if (!TacticalActorStateDiff.ShouldRefreshMagnitude(mirroredVal, s.Value)) continue; // converged → no-op
                if (!liveByKey.TryGetValue(key, out var liveStatus) || liveStatus == null) continue;
                try { if (RefreshMirrorMagnitude(liveStatus, s.Value)) refreshCount++; }
                catch (Exception ex)
                {
                    Debug.LogError("[Multipleer][tac] status mirror refresh failed: " + (s.DefGuid ?? "<null>") +
                                   " " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>Inc2 follow-up: refresh an already-mirrored status's display magnitude IN PLACE. Re-derives
        /// <c>DamageAccumulation.InitialAmount</c> (+ <c>Amount</c>) from the host display <paramref name="value"/>
        /// via the SAME pure map + direct-field write <see cref="SeedInertStatusFields"/> uses on first add —
        /// NEVER DoT <c>SetValue</c> (which RequestUnapply's at IntValue&lt;=0, DamageOverTimeStatus.cs:186-188).
        /// DoT vs Bleed is discriminated by the <c>DamagePerTurn</c> property (Bleed has none → maps 1:1, like the
        /// seed). No-op (returns false) when the status carries no <c>_damageAccum</c> (seed missed / not a damage
        /// status) or the value is non-positive (a near-0 level means the host is removing it → ToRemove handles
        /// it). Best-effort; the caller isolates any throw.</summary>
        private static bool RefreshMirrorMagnitude(object status, float value)
        {
            if (status == null || value <= 1e-05f) return false;
            var daF = AccessTools.Field(status.GetType(), "_damageAccum");
            object accum = daF != null ? daF.GetValue(status) : null;
            if (accum == null) return false;
            float dpt = 0f;
            var dptProp = AccessTools.Property(status.GetType(), "DamagePerTurn");
            if (dptProp != null) { object d = dptProp.GetValue(status, null); if (d != null) dpt = Convert.ToSingle(d); }
            float initialAmount = TacticalActorStateDiff.StatusMagnitudeToInitialAmount(value, dpt);
            AccessTools.Field(daF.FieldType, "InitialAmount")?.SetValue(accum, initialAmount);
            AccessTools.Field(daF.FieldType, "Amount")?.SetValue(accum, initialAmount);
            Debug.Log("[Multipleer][tac] status mirror magnitude refreshed: " + status.GetType().Name +
                      " value=" + value.ToString("0.##") + " initialAmount=" + initialAmount.ToString("0.##"));
            return true;
        }

        // ─── engine reflection ──────────────────────────────────────────────────────────────────────────

        /// <summary>Apply a status as an INERT visual-only MIRROR. Instead of the simple
        /// <c>ApplyStatus(StatusDef,…)</c> (whose subclass OnApply would run gameplay effects), this replicates
        /// the engine's own deserialize/"load a saved status" path: instantiate the Status, set Source/Target,
        /// PRE-SET <c>Applied = true</c> so every subclass OnApply takes its inert reattach branch (the pervasive
        /// `bool applied = base.Applied; if(!applied){effects}` / `if(CurrentlyDeserializing) return;` idiom),
        /// then drive <c>ApplyStatus(Status)</c> — the status lands in the list (icon draws) + the healthbar
        /// events fire, but NO gameplay side effect runs. The instance is registered with
        /// <c>ClientStatusMirrorGuards</c> so its per-turn ticks (DoT) + its effect-reverting OnUnapply are also
        /// skipped. Returns true on success.</summary>
        private static bool InvokeApplyStatus(object statusComponent, object statusDef, object source, object target, float value)
        {
            try
            {
                object repo = GetField(statusComponent, "Repo");
                if (repo == null) { Debug.LogError("[Multipleer][tac] actorstate: StatusComponent.Repo null"); return false; }

                // DefRepository.Instantiate(BaseDef def, …optional) → object (the new Status). Use the non-generic
                // overload (no MakeGenericMethod needed); optional params → Type.Missing.
                var inst = FindInstantiate(repo.GetType());
                if (inst == null) { Debug.LogError("[Multipleer][tac] actorstate: DefRepository.Instantiate(BaseDef,…) not found"); return false; }
                var ip = inst.GetParameters();
                var iargs = new object[ip.Length];
                iargs[0] = statusDef;
                for (int i = 1; i < ip.Length; i++) iargs[i] = Type.Missing;
                object status = inst.Invoke(repo, iargs);
                if (status == null) { Debug.LogError("[Multipleer][tac] actorstate: Instantiate returned null status"); return false; }

                // Set Source/Target (the engine's ApplyStatus(StatusDef,…) does this) + pre-set Applied=true.
                SetMember(status, "Source", source);
                SetMember(status, "Target", target);
                // ABORT if we can't pre-set Applied=true: without it the subclass OnApply runs its LIVE gameplay
                // side effects on the client (faction flip, AP drain, double DoT, …). A status that can't be made
                // inert must NEVER be applied live — drop the mirror entirely (no ApplyStatus, no Mirrored entry).
                if (!SetApplied(status, true))
                {
                    Debug.LogWarning("[Multipleer][tac] mirror-apply ABORTED: SetApplied failed for "
                        + (DefReflection.GetGuid(statusDef) ?? "<unknown def>"));
                    return false;
                }

                // ATOMIC-MIRROR SEED: the Applied=true inert path makes each subclass OnApply take its
                // deserialize/"reattach a saved status" branch — which ASSUMES every [SerializeMember] field the
                // live first-apply branch would have built was already restored by deserialization. The mirror
                // instantiates fresh (no deserialize), so those fields are still null. BleedStatus.OnApply is the
                // confirmed casualty: its inert branch `foreach (slotName in _slotNames)` derefs the null
                // `_slotNames` that only its skipped `if(!applied)` branch (or PostRead) would populate → NRE
                // (TargetInvocationException). The throw landed AFTER OnApply already subscribed OnBodyPartDetaching
                // to the enemy's AddonsManager → partial, never-completed mutation → the actor's addon tree goes
                // inconsistent → the next native post-shot RefreshIdle→GetBestEnemyTarget→DestroyTargetAddon NRE-
                // loops, the fire CompleteAction coroutine never finishes, and the client HUD/camera lock up.
                // Seeding the field(s) the inert branch needs lets OnApply RUN TO COMPLETION cleanly (empty list →
                // zero reattach iterations, no particles, no NRE, addon tree untouched), exactly mirroring the
                // engine's own restored-from-save state — so the mirror is atomic, not partially applied.
                SeedInertStatusFields(status, statusDef, value);

                // Register BEFORE ApplyStatus so the per-turn/unapply guards already know this instance.
                ClientStatusMirrorGuards.RegisterMirror(status);

                var statusType = AccessTools.TypeByName("Base.Entities.Statuses.Status");
                var apply = AccessTools.Method(statusComponent.GetType(), "ApplyStatus", new[] { statusType });
                if (apply == null) { Debug.LogError("[Multipleer][tac] actorstate: ApplyStatus(Status) not found"); ClientStatusMirrorGuards.UnregisterMirror(status); return false; }
                apply.Invoke(statusComponent, new[] { status });
                return true;
            }
            catch (Exception ex)
            {
                // TASK 3: the actual OnApply NRE site for a mirrored status (e.g. BleedStatus.OnApply derefs the
                // null _slotNames that its Applied=true inert branch never populated). Log the status NAME +
                // exception type/message in the agreed format and return false so ReconcileStatuses CONTINUES with
                // the remaining statuses + the rest of the actor-state apply. (Proper status-mirror RCA is deferred.)
                Debug.LogError("[Multipleer][tac] status mirror failed: " + DescribeDef(statusDef) +
                               " " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Best-effort human-readable id for a status DEF (its <c>UnityEngine.Object.name</c>, falling
        /// back to its <c>BaseDef.Guid</c>, then its CLR type name) for diagnostics. Never throws.</summary>
        private static string DescribeDef(object def)
        {
            if (def == null) return "<null def>";
            try { if (GetProp(def, "name") is string n && !string.IsNullOrEmpty(n)) return n; } catch { }
            try { string g = DefReflection.GetGuid(def); if (!string.IsNullOrEmpty(g)) return g; } catch { }
            return def.GetType().Name;
        }

        /// <summary>Resolve <c>DefRepository.Instantiate(BaseDef, …)</c> — the non-generic overload whose first
        /// param is a BaseDef and the rest optional. Avoids the generic <c>Instantiate&lt;T&gt;</c> overload.</summary>
        private static MethodInfo FindInstantiate(Type repoType)
        {
            foreach (var m in repoType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Instantiate" || m.IsGenericMethod) continue;
                var p = m.GetParameters();
                if (p.Length < 1) continue;
                if (!typeof(Base.Defs.BaseDef).IsAssignableFrom(p[0].ParameterType)) continue;
                bool restOptional = true;
                for (int i = 1; i < p.Length; i++) if (!p[i].IsOptional) { restOptional = false; break; }
                if (restOptional) return m;
            }
            return null;
        }

        /// <summary>Set <c>Status.Applied</c> (the protected setter) via reflection so a freshly-instantiated
        /// mirror enters its subclass OnApply on the engine's inert deserialize branch.</summary>
        private static bool SetApplied(object status, bool value)
        {
            try
            {
                var p = AccessTools.Property(status.GetType(), "Applied");
                if (p != null && p.GetSetMethod(true) != null)
                {
                    p.GetSetMethod(true).Invoke(status, new object[] { value });
                    return true;
                }
                var f = AccessTools.Field(status.GetType(), "Applied");
                if (f != null) { f.SetValue(status, value); return true; }
                // Auto-property backing field fallback.
                var bf = AccessTools.Field(status.GetType(), "<Applied>k__BackingField");
                if (bf != null) { bf.SetValue(status, value); return true; }
                return false;
            }
            catch { return false; }
        }

        /// <summary>ATOMIC-MIRROR SEED. A fresh inert mirror (Applied pre-set true) drives each subclass OnApply
        /// down its deserialize/reattach branch, which assumes the <c>[SerializeMember]</c> collection fields the
        /// live first-apply branch builds were already restored. They are null on a non-deserialized instance, so
        /// the reattach loop NREs (BleedStatus.OnApply: <c>foreach (slotName in _slotNames)</c>) AFTER it has
        /// already half-mutated the actor (event subscription) → corrupt addon tree → post-shot UI/camera lockup.
        /// Pre-seed the known null collection field(s) to EMPTY so OnApply runs clean (zero reattach iterations,
        /// no gameplay, no NRE) — the engine's restored-from-save state for an actor with no recorded slots. Only
        /// touches a field that EXISTS on this status type AND is currently null; never throws (best-effort).</summary>
        private static void SeedInertStatusFields(object status, object statusDef, float value)
        {
            if (status == null) return;
            try
            {
                // BleedStatus._slotNames (List<string>): the inert OnApply reattach loop derefs it. Native populates
                // it only in the skipped first-apply branch or via [SerializeMember] deserialization. Seed empty.
                var f = AccessTools.Field(status.GetType(), "_slotNames");
                if (f != null && typeof(System.Collections.IList).IsAssignableFrom(f.FieldType) && f.GetValue(status) == null)
                {
                    f.SetValue(status, Activator.CreateInstance(f.FieldType));
                    Debug.Log("[Multipleer][tac] status mirror slotNames seeded: " + DescribeDef(statusDef));
                }

                // BUG3a — BleedStatus._damageAccum (DamageAccumulation): the inert MERGE path derefs it. When a SECOND
                // live BleedStatus is later applied to the same actor (a real mirrored bleed), its first-apply branch
                // finds THIS mirror via statusComponent.GetStatus<BleedStatus>() and calls mirror.AddBleedDamage(num)
                // → `_damageAccum.InitialAmount += amount` (BleedStatus.cs:107,150) → NRE if the mirror's _damageAccum
                // is still null. Native populates it ONLY in the skipped first-apply branch (`new DamageAccumulation(
                // DamageEffect.DamageEffectDef, this)`, BleedStatus.cs:157). Construct it EXACTLY that way (Effect is
                // set in TacEffectStatus.Init, already run by Instantiate, so DamageEffect.DamageEffectDef is available
                // here), then ZERO the rolled InitialAmount/Amount → an EMPTY seed (parallel to the empty _slotNames
                // above; the real bleed level is host-authoritative). Field-exists + null guarded; any miss is caught
                // below (fail-open) and only risks the pre-existing CAUGHT NRE.
                var daF = AccessTools.Field(status.GetType(), "_damageAccum");
                if (daF != null && daF.GetValue(status) == null)
                {
                    var deProp = AccessTools.Property(status.GetType(), "DamageEffect");
                    object de = deProp != null ? deProp.GetValue(status, null) : null;
                    var defProp = de != null ? AccessTools.Property(de.GetType(), "DamageEffectDef") : null;
                    object dmgDef = defProp != null ? defProp.GetValue(de, null) : null;
                    if (dmgDef != null)
                    {
                        // public DamageAccumulation(DamageEffectDef dmgDef, object source) — EXACT param match on the
                        // declared DamageEffectDef type (defProp.PropertyType) + object (AccessTools is exact-match).
                        var ctor = AccessTools.Constructor(daF.FieldType, new[] { defProp.PropertyType, typeof(object) });
                        if (ctor != null)
                        {
                            object accum = ctor.Invoke(new object[] { dmgDef, status });
                            AccessTools.Field(daF.FieldType, "InitialAmount")?.SetValue(accum, 0f);
                            AccessTools.Field(daF.FieldType, "Amount")?.SetValue(accum, 0f);
                            daF.SetValue(status, accum);
                            Debug.Log("[Multipleer][tac] status mirror damageAccum seeded: " + DescribeDef(statusDef));
                        }
                    }
                }

                // BUG C / canon inv 4: APPLY the host's display magnitude to the freshly-seeded accum so the inert
                // mirror shows the right level (was always 0 — magnitude used to ride tac.damage 0x88, now retired).
                // value = host Status.Value: Bleed.Value=(int)InitialAmount, DoT.Value=InitialAmount/DamagePerTurn.
                // Map via the PURE helper, then set the FIELD directly — NEVER DoT SetValue (it RequestUnapply's at
                // IntValue<=0, DamageOverTimeStatus.cs:186-188). DoT discriminated by its DamagePerTurn property
                // (BleedStatus has none → maps 1:1).
                object accumNow = daF != null ? daF.GetValue(status) : null;
                if (accumNow != null && value > 1e-05f)
                {
                    float dpt = 0f;
                    var dptProp = AccessTools.Property(status.GetType(), "DamagePerTurn");
                    if (dptProp != null) { object d = dptProp.GetValue(status, null); if (d != null) dpt = Convert.ToSingle(d); }
                    float initialAmount = TacticalActorStateDiff.StatusMagnitudeToInitialAmount(value, dpt);
                    AccessTools.Field(daF.FieldType, "InitialAmount")?.SetValue(accumNow, initialAmount);
                    AccessTools.Field(daF.FieldType, "Amount")?.SetValue(accumNow, initialAmount);
                    Debug.Log("[Multipleer][tac] status mirror magnitude applied: " + DescribeDef(statusDef) +
                              " value=" + value.ToString("0.##") + " initialAmount=" + initialAmount.ToString("0.##"));
                }
            }
            catch (Exception ex)
            {
                // Fail-open: a seed miss only risks the pre-existing OnApply NRE (still caught by InvokeApplyStatus);
                // it must never itself abort the mirror.
                Debug.LogWarning("[Multipleer][tac] status mirror seed skipped: " +
                                 DescribeDef(statusDef) + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void SetMember(object obj, string name, object value)
        {
            if (obj == null) return;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null && p.GetSetMethod(true) != null) { p.GetSetMethod(true).Invoke(obj, new[] { value }); return; }
            var f = AccessTools.Field(obj.GetType(), name);
            f?.SetValue(obj, value);
        }

        /// <summary>Remove an inert MIRROR status under the unapply guard (so its effect-reverting OnUnapply is
        /// skipped) + unregister it. The status still leaves the list + the icon clears (StatusComponent removes
        /// it + raises OnStatusUnapplied/OnStatusesChanged, OUTSIDE the skipped OnUnapply).</summary>
        private static bool InvokeUnapplyStatus(object statusComponent, object status)
        {
            try
            {
                var statusType = AccessTools.TypeByName("Base.Entities.Statuses.Status");
                if (statusType == null) return false;
                var m = AccessTools.Method(statusComponent.GetType(), "UnapplyStatus", new[] { statusType });
                if (m == null) { Debug.LogError("[Multipleer][tac] actorstate: UnapplyStatus(Status) not found"); return false; }
                ClientStatusMirrorGuards.UnapplyInProgress = true;
                try { m.Invoke(statusComponent, new[] { status }); }
                finally { ClientStatusMirrorGuards.UnapplyInProgress = false; }
                ClientStatusMirrorGuards.UnregisterMirror(status);
                return true;
            }
            catch (Exception ex)
            {
                // TASK 3: same isolation for the removal path — log the status type + exception, then return false
                // so ReconcileStatuses keeps applying the rest of the state.
                Debug.LogError("[Multipleer][tac] status mirror failed: " + (status?.GetType().Name ?? "<null status>") +
                               " " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static float StatValue(object stat)
        {
            if (stat == null) return 0f;
            try
            {
                var op = AccessTools.Method(stat.GetType(), "op_Implicit", new[] { stat.GetType() });
                if (op != null) return Convert.ToSingle(op.Invoke(null, new[] { stat }));
            }
            catch { }
            try { return Convert.ToSingle(GetProp(stat, "IntValue") ?? 0); } catch { return 0f; }
        }

        private static void SetStat(object stat, float value)
        {
            if (stat == null) return;
            var set = AccessTools.Method(stat.GetType(), "Set", new[] { typeof(float), typeof(bool) });
            if (set != null) { set.Invoke(stat, new object[] { value, true }); return; }
            var set1 = AccessTools.Method(stat.GetType(), "Set", new[] { typeof(float) });
            set1?.Invoke(stat, new object[] { value });
        }

        /// <summary>Find and invoke <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …optional)</c> (first param the
        /// coroutine, remaining optional → Type.Missing). Mirrors TacticalMoveSync.InvokeStart.</summary>
        private static bool InvokeTimingStart(object timing, IEnumerator crt)
        {
            try
            {
                Type timingType = timing.GetType();
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
                if (best == null) { Debug.LogError("[Multipleer][tac] actorstate: no Timing.Start overload found"); return false; }
                var bp = best.GetParameters();
                var args = new object[bp.Length];
                args[0] = crt;
                for (int i = 1; i < bp.Length; i++) args[i] = Type.Missing;
                best.Invoke(timing, args);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] actorstate InvokeTimingStart failed: " + ex); return false; }
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
