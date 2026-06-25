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

            var changed = new List<TacticalLiveCodec.ActorStateRecord>();
            var liveNetIds = new HashSet<int>();

            foreach (var kv in registry.Entries)
            {
                int netId = kv.Key;
                object actor = (kv.Value is TacticalActorAdapter ad) ? ad.Actor : null;
                if (actor == null) continue;
                liveNetIds.Add(netId);

                if (!ReadActorState(actor, out float ap, out float wp, out float health, out Vector3 pos,
                        out bool hasPos, out var statuses, out var bodyParts))
                    continue;   // not a stats-bearing actor (turret/vehicle/destructible) → skip

                // Signature includes AP/WP + HEALTH + POSITION + the mirrored status set + the bodypart-HP set,
                // so a HEAL, a status icon change, a MOVE or a limb-HP change re-broadcasts (idle actor = 0 bytes
                // via the unchanged-sig skip). Health + position ride AP/WP (not gated by SyncStatuses) → always
                // in the signature. (Inc1 full-state: position drives the client walk/teleport mirror.)
                string healthSig = "$hp=" + health.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                string posSig = hasPos
                    ? "$p=" + pos.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
                            + pos.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
                            + pos.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    : "";
                string sig = (SyncStatuses
                    ? TacticalActorStateDiff.Signature(ap, wp, statuses) + BodyPartSig(bodyParts)
                    : TacticalActorStateDiff.Signature(ap, wp, null)) + healthSig + posSig;
                if (_lastSig.TryGetValue(netId, out var prev) && prev == sig)
                    continue;   // unchanged since last flush → skip (idle = 0 bytes)
                _lastSig[netId] = sig;

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

            int applied = 0, apwp = 0, sAdd = 0, sRem = 0, bp = 0, hp = 0, posCnt = 0;
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
                        if (actor == null) continue;   // not deployed/registered on the client yet → re-push heals
                        applied++;

                        // APPLY ORDER: reconcile STATUSES FIRST, then set AP/WP ABSOLUTE LAST — so the host's
                        // authoritative absolute AP/WP always WINS over any stat change a status touches (the
                        // mirrored statuses are INERT via the guards, so they apply no stat delta anyway, but
                        // the order is kept correct). Body-part HP is independent (own stat) → apply any time.
                        if (rec.HasStatuses)
                        {
                            ReconcileStatuses(actor, rec.Statuses, ref sAdd, ref sRem);
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
                if (applied > 0 || sAdd > 0 || sRem > 0 || bp > 0 || hp > 0 || posCnt > 0)
                    Debug.Log("[Multipleer][tac] CLIENT applied tac.actorstate seq=" + batch.Seq +
                              " actors=" + applied + " apwpSet=" + apwp + " status+" + sAdd + " status-" + sRem +
                              " limbHp=" + bp + " hp=" + hp + " pos=" + posCnt);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HandleActorState failed: " + ex); }
        }

        /// <summary>TASK 3 (re-grey site #2 — AP-delta apply). CLIENT-only: when the async host AP/WP delta lands for the
        /// client's CURRENTLY-SELECTED actor, re-push <c>UIStateCharacterSelected</c> via
        /// <c>TacticalView.ResetCharacterSelectedState()</c> (TacticalView.cs:306) so <c>UIModuleAbilities.SetAbilities</c>
        /// re-runs and every ability button re-evaluates <c>IsEnabled()</c> against the now-decremented AP (the buttons'
        /// lit/grey state is stamped once and only re-evaluated on a CharacterSelected re-entry). The activation-time
        /// re-grey (<c>SuppressedAbilityViewClearPatch</c>) runs BEFORE this delta arrives, so this is the second, robust
        /// trigger. ResetCharacterSelectedState SELF-GUARDS on <c>CurrentState is UIStateCharacterSelected</c> (no-op in a
        /// shoot/melee/overwatch sub-state). NRE-guarded: no live view / no selection / actor not AP-updated → no-op.
        /// Diag logs whether it fired + the view CurrentState type + the actor netId (so a blocking state is visible).</summary>
        private static void ReGreySelectedBarAfterApDelta(HashSet<int> apAppliedNetIds)
        {
            try
            {
                if (apAppliedNetIds == null || apAppliedNetIds.Count == 0) return;
                object view = GetProp(TacticalDeploySync.LiveTlc, "View");   // TacticalLevelController.View (TacticalLevelController.cs:165)
                if (view == null) return;
                object selected = GetProp(view, "SelectedActor");            // TacticalView.SelectedActor (TacticalView.cs:148)
                if (selected == null) return;
                int selNet = TacticalDeploySync.NetIdForLiveActor(selected);
                if (selNet < 0 || !apAppliedNetIds.Contains(selNet)) return; // selected actor's AP didn't change → nothing to re-grey

                string stateName = "<null>";
                try { object cs = GetProp(view, "CurrentState"); stateName = cs?.GetType().Name ?? "<null>"; } catch { /* diag only */ }
                var reset = AccessTools.Method(view.GetType(), "ResetCharacterSelectedState");
                Debug.Log("[Multipleer][tac] CLIENT re-grey@apdelta fired=" + (reset != null) +
                          " state=" + stateName + " actorNet=" + selNet);
                reset?.Invoke(view, null);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] re-grey@apdelta failed: " + ex); }
        }

        // ─── HOST read helpers (AP/WP + filtered status set) ────────────────────────────────────────────

        /// <summary>Read {AP, WP, current HEALTH, filtered status set, bodypart HP} for an actor. Returns FALSE
        /// when the actor has no CharacterStats (turret/vehicle/destructible) — the caller must NOT then ship
        /// 0/0 as authoritative. Statuses are filtered by <see cref="TacticalActorStateDiff.ShouldMirrorStatus"/>
        /// (visible-on-healthbar, non-surface-owned). Source→netId via the registry (-1 when unresolvable).
        /// <paramref name="health"/> is the actor's CURRENT HP (Feature D); the host only ships it when > 0
        /// (see HostFlushOnce) — death is owned by tac.damage, never this delta.</summary>
        private static bool ReadActorState(object actor, out float ap, out float wp, out float health,
            out Vector3 pos, out bool hasPos,
            out List<TacticalActorStateDiff.StatusRec> statuses,
            out List<TacticalActorStateDiff.BodyPartHpRec> bodyParts)
        {
            ap = 0f; wp = 0f; health = 0f; pos = Vector3.zero; hasPos = false;
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

        /// <summary>The status' carried value (its <c>Duration</c>, informational — drives the signature so a
        /// duration change re-broadcasts). Best-effort: 0 when unreadable.</summary>
        private static float ReadStatusValue(object status)
        {
            try { object d = GetProp(status, "Duration"); return d != null ? Convert.ToSingle(d) : 0f; }
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
            ref int addCount, ref int removeCount)
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
            if (!diff.HasChanges) return;

            // REMOVE absent first, then ADD missing.
            foreach (var r in diff.ToRemove)
            {
                if (liveByKey.TryGetValue(TacticalActorStateDiff.KeyOf(r), out var liveStatus) && liveStatus != null)
                {
                    if (InvokeUnapplyStatus(statusComponent, liveStatus)) removeCount++;
                }
            }
            foreach (var a in diff.ToAdd)
            {
                object def = DefReflection.GetDefByGuid(a.DefGuid);
                if (def == null) continue;
                object source = a.SourceNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(a.SourceNetId) : null;
                if (InvokeApplyStatus(statusComponent, def, source, actor)) addCount++;
            }
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
        private static bool InvokeApplyStatus(object statusComponent, object statusDef, object source, object target)
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

                // Register BEFORE ApplyStatus so the per-turn/unapply guards already know this instance.
                ClientStatusMirrorGuards.RegisterMirror(status);

                var statusType = AccessTools.TypeByName("Base.Entities.Statuses.Status");
                var apply = AccessTools.Method(statusComponent.GetType(), "ApplyStatus", new[] { statusType });
                if (apply == null) { Debug.LogError("[Multipleer][tac] actorstate: ApplyStatus(Status) not found"); ClientStatusMirrorGuards.UnregisterMirror(status); return false; }
                apply.Invoke(statusComponent, new[] { status });
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] actorstate ApplyStatus(mirror) failed: " + ex); return false; }
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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] actorstate UnapplyStatus failed: " + ex); return false; }
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
