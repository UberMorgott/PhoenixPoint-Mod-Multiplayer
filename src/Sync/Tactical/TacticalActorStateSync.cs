using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using UnityEngine;

// CS0162 (unreachable code): INTENTIONAL. SyncStatuses is a compile-time `const false` gate (T1 ships AP/WP
// only); the `if (SyncStatuses) …` status branches are deliberately dead-but-kept (correct, unit-tested) for
// when the flag flips in a later increment. Suppress the warning for this file only so the gated code can stay
// in-place rather than be deleted + re-added.
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
        /// STATUS-SYNC GATE — DEFAULT OFF. When false (T1 ship state), the generic delta carries ONLY AP/WP:
        /// the host never reads/encodes statuses (the STATUSES fieldMask bit is never set, the signature is
        /// AP+WP only → no chatty duration-tick re-broadcasts), and the client naturally skips reconcile (the
        /// bit is absent). The status read + reconcile code is kept INTACT (correct, unit-tested) for when this
        /// flag flips in a later increment — which requires a VETTED default-DENY allowlist
        /// (<see cref="TacticalActorStateDiff.IsSyncableStatusType"/>, empty in T1) AND 2-instance verification
        /// (MindControl + Stun are the must-test divergence cases: re-running a status OnApply on the client
        /// diverges — faction flip / AP reduction / state-machine / stat-mod double-add). Do NOT enable without
        /// that work.
        /// </summary>
        internal const bool SyncStatuses = false;

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

                if (!ReadActorState(actor, out float ap, out float wp, out var statuses))
                    continue;   // not a stats-bearing actor (turret/vehicle/destructible) → skip

                // GATED: with SyncStatuses off (T1), the signature is AP+WP ONLY (no status set → no chatty
                // duration-tick re-broadcasts) and the wire carries ONLY AP/WP (the STATUSES bit is never set).
                string sig = SyncStatuses
                    ? TacticalActorStateDiff.Signature(ap, wp, statuses)
                    : TacticalActorStateDiff.Signature(ap, wp, null);
                if (_lastSig.TryGetValue(netId, out var prev) && prev == sig)
                    continue;   // unchanged since last flush → skip (idle = 0 bytes)
                _lastSig[netId] = sig;

                ushort mask = (ushort)(TacticalLiveCodec.ActorFieldAp | TacticalLiveCodec.ActorFieldWp);
                if (SyncStatuses) mask |= TacticalLiveCodec.ActorFieldStatuses;
                var rec = new TacticalLiveCodec.ActorStateRecord
                {
                    NetId = netId,
                    FieldMask = mask,
                    Ap = ap,
                    Wp = wp,
                };
                if (SyncStatuses)
                    foreach (var s in statuses)
                        rec.Statuses.Add(new TacticalLiveCodec.ActorStatus(s.DefGuid, s.SourceNetId, s.Value));
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

            int applied = 0, apwp = 0, sAdd = 0, sRem = 0;
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

                        // APPLY ORDER (matters when statuses are later enabled): reconcile STATUSES FIRST, then
                        // set AP/WP ABSOLUTE LAST — so the host's authoritative absolute AP/WP always WINS over
                        // any stat change a status' OnApply makes (e.g. a stat-mod status re-adding its AP/WP
                        // delta). With SyncStatuses off (T1) the STATUSES bit is never set → reconcile is skipped.
                        if (rec.HasStatuses)
                        {
                            ReconcileStatuses(actor, rec.Statuses, ref sAdd, ref sRem);
                        }
                        if (rec.HasAp || rec.HasWp)
                        {
                            if (SetApWpAbsolute(actor, rec)) apwp++;
                        }
                    }
                }
                finally { _applyingRemote = false; }

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacActorState, batch.Seq);
                if (applied > 0 || sAdd > 0 || sRem > 0)
                    Debug.Log("[Multipleer][tac] CLIENT applied tac.actorstate seq=" + batch.Seq +
                              " actors=" + applied + " apwpSet=" + apwp + " status+" + sAdd + " status-" + sRem);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HandleActorState failed: " + ex); }
        }

        // ─── HOST read helpers (AP/WP + filtered status set) ────────────────────────────────────────────

        /// <summary>Read {AP, WP, filtered status set} for an actor. Returns FALSE when the actor has no
        /// CharacterStats (turret/vehicle/destructible) — the caller must NOT then ship 0/0 as authoritative.
        /// Statuses are filtered by <see cref="TacticalActorStateDiff.IsSyncableStatusType"/> (excludes
        /// Overwatch + DoT family). Source→netId via the registry (-1 when the source isn't a resolvable actor).</summary>
        private static bool ReadActorState(object actor, out float ap, out float wp,
            out List<TacticalActorStateDiff.StatusRec> statuses)
        {
            ap = 0f; wp = 0f;
            statuses = new List<TacticalActorStateDiff.StatusRec>();

            object stats = GetProp(actor, "CharacterStats");
            if (stats == null) return false;                       // not a stats-bearing TacticalActor
            object apStat = GetField(stats, "ActionPoints");
            object wpStat = GetField(stats, "WillPoints");
            if (apStat == null || wpStat == null) return false;
            ap = StatValue(apStat);
            wp = StatValue(wpStat);

            // GATED: skip the status enumeration entirely when status sync is off (T1) — the wire + signature
            // carry only AP/WP. The read code below stays intact for when SyncStatuses flips on.
            if (!SyncStatuses) return true;

            // Statuses: enumerate StatusComponent.Statuses, filter by policy, flatten {defGuid, sourceNetId, value}.
            object statusComponent = GetProp(actor, "Status");
            if (statusComponent != null && GetProp(statusComponent, "Statuses") is IEnumerable list)
            {
                foreach (var st in list)
                {
                    if (st == null) continue;
                    if (!TacticalActorStateDiff.IsSyncableStatusType(st.GetType().Name)) continue;
                    object def = GetProp(st, "Def");
                    string guid = DefReflection.GetGuid(def);
                    if (string.IsNullOrEmpty(guid)) continue;       // un-resolvable def → can't reconcile cross-side
                    int sourceNetId = ResolveSourceNetId(GetProp(st, "Source"));
                    float value = ReadStatusValue(st);
                    statuses.Add(new TacticalActorStateDiff.StatusRec(guid, sourceNetId, value));
                }
            }
            return true;
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

        // ─── CLIENT apply helpers ───────────────────────────────────────────────────────────────────────

        /// <summary>Set the actor's AP/WP to the host-absolute values via <c>BaseStat.Set(float,bool)</c> (only
        /// the fields whose bit is set). Returns true if at least one stat was set.</summary>
        private static bool SetApWpAbsolute(object actor, TacticalLiveCodec.ActorStateRecord rec)
        {
            object stats = GetProp(actor, "CharacterStats");
            if (stats == null) return false;
            bool any = false;
            if (rec.HasAp) { SetStat(GetField(stats, "ActionPoints"), rec.Ap); any = true; }
            if (rec.HasWp) { SetStat(GetField(stats, "WillPoints"), rec.Wp); any = true; }
            return any;
        }

        /// <summary>Reconcile the actor's status set to the incoming set: ApplyStatus the genuinely-MISSING ones
        /// (def resolved by guid; source = the resolved live actor or null), UnapplyStatus the absent ones. A
        /// status already present (by {defGuid, sourceNetId}) is left untouched so its <c>OnApply</c> never
        /// re-runs (spec risk #1). Only policy-syncable statuses on the actor participate in the "current" set,
        /// so a surface-owned status (Overwatch) is never seen as "extra" and removed.</summary>
        private static void ReconcileStatuses(object actor, List<TacticalLiveCodec.ActorStatus> incoming,
            ref int addCount, ref int removeCount)
        {
            object statusComponent = GetProp(actor, "Status");
            if (statusComponent == null) return;

            // Build the CLIENT's current syncable set + a lookup back to the live Status object for removal.
            var current = new List<TacticalActorStateDiff.StatusRec>();
            var liveByKey = new Dictionary<string, object>();
            if (GetProp(statusComponent, "Statuses") is IEnumerable list)
            {
                foreach (var st in list)
                {
                    if (st == null) continue;
                    if (!TacticalActorStateDiff.IsSyncableStatusType(st.GetType().Name)) continue;
                    string guid = DefReflection.GetGuid(GetProp(st, "Def"));
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

        private static bool InvokeApplyStatus(object statusComponent, object statusDef, object source, object target)
        {
            try
            {
                var statusDefType = AccessTools.TypeByName("Base.Entities.Statuses.StatusDef");
                if (statusDefType == null) return false;
                // public Status ApplyStatus(StatusDef def, object source = null, object target = null)
                var m = AccessTools.Method(statusComponent.GetType(), "ApplyStatus",
                    new[] { statusDefType, typeof(object), typeof(object) });
                if (m == null) { Debug.LogError("[Multipleer][tac] actorstate: ApplyStatus(StatusDef,object,object) not found"); return false; }
                object applied = m.Invoke(statusComponent, new[] { statusDef, source, target });
                return applied != null;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] actorstate ApplyStatus failed: " + ex); return false; }
        }

        private static bool InvokeUnapplyStatus(object statusComponent, object status)
        {
            try
            {
                var statusType = AccessTools.TypeByName("Base.Entities.Statuses.Status");
                if (statusType == null) return false;
                var m = AccessTools.Method(statusComponent.GetType(), "UnapplyStatus", new[] { statusType });
                if (m == null) { Debug.LogError("[Multipleer][tac] actorstate: UnapplyStatus(Status) not found"); return false; }
                m.Invoke(statusComponent, new[] { status });
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
