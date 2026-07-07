using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// LIVE mission-objective mirror (surface <c>tac.objective</c> 0x99, host→all, RELIABLE). Closes the
    /// tactical audit gap D21 "in-battle objectives do not sync": scripted/custom missions (story, TFTV) flip
    /// <c>FactionObjective</c> state MID-battle (kill target, reach zone, defend N turns, activate console) —
    /// TS4 (0x95) repaints objectives only at mission END, so the client objective HUD stayed stale for the
    /// whole battle and scripted sequences looked broken.
    ///
    /// HOST authority (<see cref="HostOnObjectivesChanged"/>, postfix on <c>ObjectivesManager.Evaluate</c> —
    /// the ONE method every state write funnels through (the <c>State</c> private setter is only reachable from
    /// <c>FactionObjective.Evaluate</c>) — and on <c>ObjectivesManager.Add</c>, the mid-mission scripted-add
    /// chokepoint): snapshot the PLAYER faction's objective list (class / state / progress ints), diff against
    /// the last broadcast (pure <see cref="TacticalObjectiveGate.BuildRecords"/>) and broadcast only changes.
    /// <see cref="HostSeedAfterDeploy"/> re-seeds the FULL state set with the actor seed on every deploy
    /// broadcast — which includes the reload-into-tactical path (rca-6), where saved objective states are
    /// mid-mission values.
    ///
    /// CLIENT apply (<see cref="HandleObjective"/>, display-only under <see cref="SyncApplyScope"/>): STATE
    /// records value-stamp state (shared <see cref="FactionObjectiveReflect"/> private-setter boundary, exactly
    /// the TS4 handles) + progress ints on the index-keyed, CLASS-NAME-checked local objective; ADD records
    /// mirror-append the local instance resolved from the shared NextOnSuccess/NextOnFail def graph (private
    /// <c>_objectives</c> list — NEVER the native <c>Add()</c>, whose OnAdded/effects are completion logic);
    /// then ONE native <c>OnObjectivesChanged</c> kick repaints the objectives HUD. Completion logic NEVER runs
    /// client-side: the client-mirror suppress guards (<see cref="Multiplayer.Harmony.Tactical.ObjectiveSyncPatches"/>)
    /// skip local <c>ObjectivesManager.Evaluate</c> / <c>GameOverCondition.EvaluateObjectives</c>, so host
    /// stamps are never overwritten and mission END stays owned by TS4 (0x95).
    ///
    /// A 0x99 arriving BEFORE the client hydrates (the deploy seed always does — the client is still loading)
    /// is QUEUED and drained by <see cref="ClientApplyPending"/> at mirror-arm. Unknown TFTV subclass / index
    /// drift degrades to a per-record skip with a log-once notice (never a mis-stamp). Pure wire + decisions
    /// live in the engine-free, unit-tested <see cref="TacticalObjectiveCodec"/> /
    /// <see cref="TacticalObjectiveGate"/>; this class + <see cref="FactionObjectiveReflect"/> are the only
    /// reflection boundary.
    /// </summary>
    public static class TacticalObjectiveSync
    {
        // Host: last-broadcast snapshot of the player faction's objective list (the diff base).
        private static List<TacticalObjectiveGate.ObjSnap> _lastSent = new List<TacticalObjectiveGate.ObjSnap>();
        // Client: payloads that arrived before hydration (deploy seed race) — drained at mirror-arm, in order.
        private static readonly List<byte[]> _pendingClient = new List<byte[]>();
        private const int MaxPendingClient = 16;
        // Client: log-once guard for the unknown-subclass / index-drift degrade notice.
        private static bool _warnedSkip;

        /// <summary>Clear per-mission state (mission exit / re-deploy). Idempotent.</summary>
        public static void Reset()
        {
            _lastSent = new List<TacticalObjectiveGate.ObjSnap>();
            _pendingClient.Clear();
            _warnedSkip = false;
        }

        // ─── HOST: broadcast ─────────────────────────────────────────────────────────────────────────

        /// <summary>HOST: seed the FULL objective state set right after the deploy broadcast (turn-0 AND the
        /// reload-into-tactical re-seed, rca-6 — both sides rebuilt the SAME list from the shared def/save, so
        /// index-keyed state stamps reconcile the client baseline).</summary>
        public static void HostSeedAfterDeploy(object tlc)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (tlc == null) return;
            try
            {
                object mgr = ResolvePlayerObjectivesManager(tlc);
                if (mgr == null) return;
                _lastSent = new List<TacticalObjectiveGate.ObjSnap>();   // fresh mission → fresh diff base
                Flush(engine, mgr, seedAll: true);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostSeedAfterDeploy failed: " + ex); }
        }

        /// <summary>HOST postfix hook on <c>ObjectivesManager.Evaluate</c> / <c>ObjectivesManager.Add</c>:
        /// diff + broadcast the PLAYER faction's objective changes. Gates: host + active session + not
        /// client-mirroring + deploy already captured (mission-SETUP adds are pre-deploy → covered by the seed)
        /// + player-controlled faction only (the objective HUD mirrors the shared player faction). Change-
        /// detected → an idle Evaluate poll is a 0-byte no-op. Never throws into the game loop.</summary>
        public static void HostOnObjectivesChanged(object objectivesManager)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (!TacticalDeploySync.HostHasBroadcastDeploy) return;   // pre-deploy setup rides the seed
            if (objectivesManager == null) return;
            try
            {
                object faction = GetProp(objectivesManager, "Faction");
                if (!(faction != null && GetProp(faction, "IsControlledByPlayer") is bool b && b)) return;
                Flush(engine, objectivesManager, seedAll: false);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnObjectivesChanged failed: " + ex); }
        }

        private static void Flush(NetworkEngine engine, object objectivesManager, bool seedAll)
        {
            var current = Snapshot(objectivesManager);
            var records = TacticalObjectiveGate.BuildRecords(current, _lastSent, seedAll);
            _lastSent = current;
            if (records.Count == 0) return;

            uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacObjective);
            byte[] payload = TacticalObjectiveCodec.Encode(new TacticalObjectiveCodec.ObjectiveBatch(seq, records));
            TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacObjective, payload);
            Debug.Log("[Multiplayer][tac] HOST broadcast tac.objective seq=" + seq +
                      " recs=" + records.Count + (seedAll ? " (seed)" : ""));
        }

        /// <summary>Snapshot the manager's objective list (class / descKey / state / progress) — the diff unit.
        /// Per-item try/catch: a read miss degrades that objective to defaults, never drops the flush.</summary>
        private static List<TacticalObjectiveGate.ObjSnap> Snapshot(object objectivesManager)
        {
            var list = new List<TacticalObjectiveGate.ObjSnap>();
            if (!(objectivesManager is IEnumerable en)) return list;
            foreach (var o in en)
            {
                if (o == null) continue;
                try
                {
                    list.Add(new TacticalObjectiveGate.ObjSnap(
                        FactionObjectiveReflect.ClassNameOf(o),
                        FactionObjectiveReflect.DescKeyOf(o),
                        FactionObjectiveReflect.ReadState(o),
                        FactionObjectiveReflect.ReadProgress(o)));
                }
                catch { list.Add(new TacticalObjectiveGate.ObjSnap()); }
            }
            return list;
        }

        // ─── CLIENT: apply ───────────────────────────────────────────────────────────────────────────

        /// <summary>CLIENT inbound (<c>tac.objective</c> 0x99): seq-guard, then under the remote-apply scope
        /// value-stamp STATE records (class-checked) + mirror-append ADD records, then kick ONE native
        /// objectives-HUD refresh. Not hydrated yet (the deploy-seed race) → QUEUE the payload (unmarked; the
        /// hydrate path drains it via <see cref="ClientApplyPending"/>). No-op on host.</summary>
        public static void HandleObjective(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalObjectiveCodec.TryDecode(payload, out var batch))
            { Debug.LogError("[Multiplayer][tac] tac.objective decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacObjective, batch.Seq)) return;

            try
            {
                object mgr = TacticalDeploySync.IsClientMirroring ? ResolvePlayerObjectivesManager(ResolveTlc()) : null;
                if (mgr == null)
                {
                    // Not hydrated yet (deploy seed always races the scene load) — queue for mirror-arm, do
                    // NOT mark the seq (the drain re-runs the full guard chain).
                    if (_pendingClient.Count < MaxPendingClient) _pendingClient.Add(payload);
                    Debug.Log("[Multiplayer][tac] tac.objective queued pre-hydrate seq=" + batch.Seq +
                              " pending=" + _pendingClient.Count);
                    return;
                }

                int stamped, added, skipped;
                using (SyncApplyScope.Enter())
                using (TacticalActorStateSync.EnterApplyScope())
                {
                    Apply(mgr, batch, out stamped, out added, out skipped);
                    if (stamped + added > 0) FireObjectivesChanged(mgr);   // ONE native HUD kick per batch
                }

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacObjective, batch.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.objective seq=" + batch.Seq +
                          " stamped=" + stamped + " added=" + added + " skipped=" + skipped);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleObjective failed: " + ex); }
        }

        /// <summary>CLIENT: drain payloads that arrived before hydration (called at mirror-arm, after the
        /// per-mission LiveSeq reset — the queued seqs re-apply in arrival order).</summary>
        public static void ClientApplyPending()
        {
            if (_pendingClient.Count == 0) return;
            var drained = new List<byte[]>(_pendingClient);
            _pendingClient.Clear();
            foreach (var p in drained) HandleObjective(p);
        }

        private static void Apply(object mgr, TacticalObjectiveCodec.ObjectiveBatch batch,
            out int stamped, out int added, out int skipped)
        {
            stamped = added = skipped = 0;
            var objList = ObjectiveList(mgr);

            // STATE records first (BuildRecords emits them before the appended-tail ADD records).
            var classNames = new List<string>(objList.Count);
            foreach (var o in objList) classNames.Add(FactionObjectiveReflect.ClassNameOf(o));
            var skippedRecs = new List<TacticalObjectiveCodec.ObjectiveRec>();
            foreach (var kv in TacticalObjectiveGate.ResolveStateApplies(batch.Records, classNames, skippedRecs))
            {
                try
                {
                    Stamp(objList[kv.Key], kv.Value);
                    stamped++;
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] objective stamp failed idx=" + kv.Key + ": " + ex); }
            }
            skipped += skippedRecs.Count;

            // ADD records in order (each may extend the list; chained adds resolve against the grown list).
            foreach (var rec in batch.Records)
            {
                if (rec == null || rec.Kind != TacticalObjectiveCodec.KindAdd) continue;
                try
                {
                    var candidates = new List<TacticalObjectiveGate.AddCandidate>();
                    var instances = new List<object>();
                    CollectAddCandidates(objList, candidates, instances);
                    int match = TacticalObjectiveGate.ResolveAddMatch(rec, candidates);
                    if (match < 0) { skipped++; WarnSkipOnce(rec); continue; }

                    object child = instances[match];
                    if (!MirrorAppend(mgr, objList, child)) { skipped++; WarnSkipOnce(rec); continue; }
                    Stamp(child, rec);
                    added++;
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] objective add failed: " + ex); skipped++; }
            }

            if (skippedRecs.Count > 0) WarnSkipOnce(skippedRecs[0]);
        }

        private static void Stamp(object objective, TacticalObjectiveCodec.ObjectiveRec rec)
        {
            FactionObjectiveReflect.SetState(objective, rec.State);        // value-only, the TS4 private-setter handles
            FactionObjectiveReflect.WriteProgress(objective, rec.Progress);
        }

        private static void WarnSkipOnce(TacticalObjectiveCodec.ObjectiveRec rec)
        {
            if (_warnedSkip) return;
            _warnedSkip = true;
            Debug.Log("[Multiplayer][tac] tac.objective: record(s) skipped (unknown subclass / index drift" +
                      " / unresolvable scripted add) — degrade, first=" + (rec != null ? rec.ClassName : "?") +
                      " idx=" + (rec != null ? rec.Index : -1) + " (logged once)");
        }

        // ─── Reflection boundary ─────────────────────────────────────────────────────────────────────

        private static Type _tlcType;              // PhoenixPoint.Tactical.Levels.TacticalLevelController
        private static FieldInfo _objectivesField;  // ObjectivesManager._objectives (List<FactionObjective>)
        private static FieldInfo _changedEventField;// ObjectivesManager.OnObjectivesChanged (event backing field)

        private static Type TlcType()
            => _tlcType ?? (_tlcType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController"));

        /// <summary>The live TacticalLevelController: the cached live-rail handle, else the TS3/TS4
        /// <c>GeoRuntime.CurrentLevel() → GetComponent</c> resolve. Null if not in a tactical level.</summary>
        private static object ResolveTlc()
        {
            object tlc = TacticalDeploySync.LiveTlc;
            if (tlc != null) return tlc;
            var t = TlcType();
            if (t == null) return null;
            try
            {
                var level = GeoRuntime.Instance.CurrentLevel();
                if (level is Component comp) return comp.GetComponent(t);
                return null;
            }
            catch { return null; }
        }

        /// <summary>The PLAYER-controlled faction's <c>ObjectivesManager</c> (the shared Phoenix faction whose
        /// objectives the tactical HUD shows). Null on any miss.</summary>
        private static object ResolvePlayerObjectivesManager(object tlc)
        {
            if (tlc == null) return null;
            try
            {
                if (GetProp(tlc, "Factions") is IEnumerable en)
                    foreach (var f in en)
                        if (f != null && GetProp(f, "IsControlledByPlayer") is bool b && b)
                            return GetProp(f, "Objectives");
            }
            catch { }
            return null;
        }

        private static List<object> ObjectiveList(object mgr)
        {
            var list = new List<object>();
            if (mgr is IEnumerable en)
                foreach (var o in en)
                    if (o != null) list.Add(o);
            return list;
        }

        /// <summary>Candidates for an ADD record: every child in every current objective's
        /// NextOnSuccess/NextOnFail array (the shared def graph — present identically on both sides), with an
        /// already-present flag (reference identity) so a child never mirrors twice.</summary>
        private static void CollectAddCandidates(List<object> objList,
            List<TacticalObjectiveGate.AddCandidate> candidates, List<object> instances)
        {
            var present = new HashSet<object>(objList);
            foreach (var o in objList)
            {
                foreach (var arrayName in new[] { "NextOnSuccess", "NextOnFail" })
                {
                    if (!(AccessTools.Field(o.GetType(), arrayName)?.GetValue(o) is IEnumerable children)) continue;
                    foreach (var child in children)
                    {
                        if (child == null) continue;
                        candidates.Add(new TacticalObjectiveGate.AddCandidate(
                            FactionObjectiveReflect.ClassNameOf(child),
                            FactionObjectiveReflect.DescKeyOf(child),
                            present.Contains(child)));
                        instances.Add(child);
                    }
                }
            }
        }

        /// <summary>Mirror-append a resolved child into the manager's private <c>_objectives</c> list + bind its
        /// <c>Faction</c> (public setter) — the DISPLAY-ONLY shape of <c>ObjectivesManager.Add</c>: no OnAdded, no
        /// deployment-point/effect side effects (completion logic stays host-owned). The appended instance then
        /// grows <paramref name="objList"/> so chained adds + subsequent index-keyed stamps line up.</summary>
        private static bool MirrorAppend(object mgr, List<object> objList, object child)
        {
            if (_objectivesField == null || !_objectivesField.DeclaringType.IsInstanceOfType(mgr))
                _objectivesField = AccessTools.Field(mgr.GetType(), "_objectives");
            if (!(_objectivesField?.GetValue(mgr) is IList raw)) return false;
            if (raw.Contains(child)) return false;   // defensive double-add guard
            raw.Add(child);
            objList.Add(child);
            try { AccessTools.Property(child.GetType(), "Faction")?.SetValue(child, GetProp(mgr, "Faction"), null); }
            catch { }
            return true;
        }

        /// <summary>Kick the NATIVE objectives-HUD refresh: invoke the manager's <c>OnObjectivesChanged</c>
        /// event delegate (field-like event, initialized non-null) — exactly what the native mid-battle flip
        /// path fires, so UIStateCharacterSelected.UpdateObjectives/UpdateActionMarkers repaint.</summary>
        private static void FireObjectivesChanged(object mgr)
        {
            try
            {
                if (_changedEventField == null || !_changedEventField.DeclaringType.IsInstanceOfType(mgr))
                    _changedEventField = AccessTools.Field(mgr.GetType(), "OnObjectivesChanged");
                if (_changedEventField?.GetValue(mgr) is Delegate d) d.DynamicInvoke();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] FireObjectivesChanged failed: " + ex); }
        }

        private static readonly Dictionary<string, PropertyInfo> _propCache = new Dictionary<string, PropertyInfo>();
        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            string key = obj.GetType().FullName + "::" + name;
            if (!_propCache.TryGetValue(key, out var pi))
            {
                pi = AccessTools.Property(obj.GetType(), name);
                _propCache[key] = pi;
            }
            return pi?.GetValue(obj, null);
        }
    }
}
