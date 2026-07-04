using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// LIVE host→client VISION replication (surface <c>tac.vision</c> 0x89). Vision in PP is per-faction,
    /// raycast-derived, and never replicated — so the client mirror (which SUPPRESSES local perception) leaves
    /// the shared player faction's <c>TacticalFactionVision.KnownActors</c> empty → every vision consumer (the
    /// spotted-enemy icons, the RED/GREY target markers, the <c>TacticalAbility.TargetFilterPredicate</c> shoot
    /// gate, auto-target) reads empty. This module replicates the HOST player-faction vision to the client so it
    /// perceives enemies exactly as the host does.
    ///
    /// HOST capture: a Harmony Postfix on <c>TacticalLevelController.FactionKnowledgeChanged(TacticalFaction)</c>
    /// (TLC.cs:821, the method that raises <c>FactionKnowledgeChangedEvent</c>) — see
    /// <c>VisionBroadcastPatch</c>. The postfix is always registered and gated at runtime (host + co-op active +
    /// the changed faction is the shared player faction), so there is NO reflective event bind that could silently
    /// fail and kill the whole feature. On a player-faction change it snapshots that faction's <c>KnownActors</c>
    /// — for each (actor, counters) with a resolvable netId: <c>IsRevealed → 2</c>, else <c>IsLocated → 1</c>, else
    /// skip — and broadcasts <c>tac.vision</c> with the next monotonic seq. Sent PER EVENT (no long-lived host
    /// tick exists here to drive a coalescer), but a broadcast whose snapshot equals the last one is SKIPPED
    /// (<c>_lastBroadcastSig</c>) — the dominant source of redundant traffic — so the rail stays light.
    ///
    /// CLIENT apply: <see cref="HandleVision"/> resolves the viewer faction by index, builds the current
    /// {netId→state} map of its <c>KnownActors</c>, computes the minimal reconcile (<see cref="TacticalVisionDiff"/>)
    /// to the snapshot — SET listed actors to Revealed/Located, FORGET any known actor absent from the snapshot —
    /// applies it to <c>KnownActors</c>, and fires <c>FactionKnowledgeChanged(faction)</c> ONCE so the UI
    /// re-renders. Idempotent (re-applying the same snapshot is a no-op via the diff + the per-surface seq guard).
    ///
    /// SINGLE-WRITER on the client: the client's local raycast recompute (Vision.OnFactionStartTurn +
    /// Vision.OnActorMoved) is suppressed on the mirror (MirrorVisionRecomputeSuppressPatches), so this host
    /// push is the ONLY writer of the player faction's KnownActors on the client.
    /// </summary>
    public static class TacticalVisionSync
    {
        // Host chattiness guard: the signature (ordered netId|state pairs) of the LAST snapshot we broadcast.
        // FactionKnowledgeChanged fires several times per host move/turn; many produce an IDENTICAL player-faction
        // known set (e.g. a re-raycast that changed nothing). We send PER EVENT (no long-lived host tick exists in
        // this module to drive a deferred flush), but SKIP a broadcast whose snapshot equals the last one — the
        // dominant source of redundant traffic — so the rail stays light without a coalescer. Reset on capture.
        private static string _lastBroadcastSig;

        /// <summary>HOST: called from the deploy capture to (re)seed the chattiness guard so the first broadcast
        /// of a fresh mission always goes out (a stale signature from a prior mission must never suppress it).</summary>
        public static void HostResetBroadcastGuard() => _lastBroadcastSig = null;

        /// <summary>HOST inbound (from the <c>FactionKnowledgeChanged</c> postfix): the changed faction's knowledge
        /// changed. If it's the shared PLAYER faction, broadcast the current vision snapshot (skipped inside if
        /// identical to the last). No-op off-host / when mirroring / for non-player factions.</summary>
        public static void HostOnFactionKnowledgeChanged(object faction)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (faction == null) return;
            try
            {
                if (!ToBool(GetProp(faction, "IsControlledByPlayer"))) return;   // only the shared player faction
                HostBroadcastVision();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnFactionKnowledgeChanged failed: " + ex); }
        }

        // ─── HOST: snapshot the player faction's KnownActors → broadcast tac.vision ──────────────────
        /// <summary>HOST: snapshot the player faction's <c>Vision.KnownActors</c> and broadcast <c>tac.vision</c>
        /// to all peers. For each known actor with a resolvable netId: Revealed→2, else Located→1, else skip.</summary>
        public static void HostBroadcastVision()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;

            try
            {
                object tlc = TacticalDeploySync.LiveTlc;
                if (tlc == null) { Debug.LogError("[Multiplayer][tac] vision: no live TLC to snapshot"); return; }
                object playerFaction = ResolvePlayerFaction(tlc, out int factionIndex);
                if (playerFaction == null || factionIndex < 0) { Debug.LogError("[Multiplayer][tac] vision: no player faction"); return; }

                object vision = GetProp(playerFaction, "Vision");
                if (vision == null) { Debug.LogError("[Multiplayer][tac] vision: player faction has no Vision"); return; }
                var knownActors = GetKnownActorsDict(vision);
                if (knownActors == null) { Debug.LogError("[Multiplayer][tac] vision: KnownActors unreadable"); return; }

                var entries = new List<TacticalLiveCodec.VisionEntry>(knownActors.Count);
                foreach (DictionaryEntry e in knownActors)
                {
                    object actor = e.Key;
                    if (actor == null) continue;
                    int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                    if (netId < 0) continue;   // unregistered (e.g. phantom) → skip
                    int state = VisionStateForActor(vision, actor);
                    if (state == 0) continue;  // neither revealed nor located → skip
                    entries.Add(new TacticalLiveCodec.VisionEntry(netId, state));
                }

                // Chattiness guard: skip a redundant broadcast whose snapshot equals the last one (a re-raycast
                // that changed nothing fires the event but produces the same known set). Signature = factionIndex
                // + sorted netId:state pairs (order-stable so it compares by CONTENT, not enumeration order).
                string sig = BuildSignature(factionIndex, entries);
                if (sig == _lastBroadcastSig)
                {
                    Debug.Log("[Multiplayer][tac] HOST vision unchanged (count=" + entries.Count + ") — skip broadcast");
                    return;
                }

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacVision);
                byte[] payload = TacticalLiveCodec.EncodeVision(seq, factionIndex, entries);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacVision, payload);
                _lastBroadcastSig = sig;
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.vision seq=" + seq + " factionIdx=" + factionIndex +
                          " count=" + entries.Count);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastVision failed: " + ex); }
        }

        // ─── CLIENT: apply the host vision snapshot → reconcile KnownActors ──────────────────────────
        /// <summary>CLIENT inbound (<c>tac.vision</c>): reconcile the viewer faction's <c>KnownActors</c> to the
        /// host snapshot — set listed actors to Revealed/Located, forget any known actor absent from the snapshot
        /// — then fire <c>FactionKnowledgeChanged(faction)</c> once so the UI re-renders. Idempotent.</summary>
        public static void HandleVision(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeVision(payload, out var snap)) { Debug.LogError("[Multiplayer][tac] tac.vision decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacVision, snap.Seq)) return;

            object tlc = TacticalDeploySync.LiveTlc;
            if (tlc == null) { Debug.LogError("[Multiplayer][tac] tac.vision: no live TLC — dropping"); return; }

            try
            {
                object faction = ResolveFactionByIndex(tlc, snap.ViewerFactionIndex);
                if (faction == null) { Debug.LogError("[Multiplayer][tac] tac.vision: faction idx " + snap.ViewerFactionIndex + " not found"); return; }
                object vision = GetProp(faction, "Vision");
                if (vision == null) { Debug.LogError("[Multiplayer][tac] tac.vision: faction has no Vision"); return; }
                var knownActors = GetKnownActorsDict(vision);
                if (knownActors == null) { Debug.LogError("[Multiplayer][tac] tac.vision: KnownActors unreadable"); return; }

                // Current {netId→state} on the client (only entries we can map back to a netId participate).
                var current = new Dictionary<int, int>();
                var actorByNetId = new Dictionary<int, object>();
                foreach (DictionaryEntry e in knownActors)
                {
                    object actor = e.Key;
                    if (actor == null) continue;
                    int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                    if (netId < 0) continue;
                    current[netId] = VisionStateForActor(vision, actor);
                    actorByNetId[netId] = actor;
                }

                // Incoming snapshot {netId→state}.
                var incoming = new Dictionary<int, int>(snap.Entries.Count);
                foreach (var en in snap.Entries) incoming[en.NetId] = en.KnownState;

                var diff = TacticalVisionDiff.Compute(current, incoming);
                if (!diff.HasChanges)
                {
                    TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacVision, snap.Seq);
                    Debug.Log("[Multiplayer][tac] CLIENT applied tac.vision seq=" + snap.Seq + " (no change, idempotent)");
                    return;
                }

                int setN = 0, forgetN = 0;
                // FORGET first (removals), then SET (additions/changes).
                foreach (int netId in diff.ToForget)
                {
                    object actor = actorByNetId.TryGetValue(netId, out var a) ? a : TacticalDeploySync.ResolveLiveActor(netId);
                    if (actor == null) continue;
                    if (ForgetActor(knownActors, actor)) forgetN++;
                }
                foreach (var kv in diff.ToSet)
                {
                    object actor = TacticalDeploySync.ResolveLiveActor(kv.Key);
                    if (actor == null) continue;   // not deployed/registered on the client yet → skip (re-push heals)
                    if (SetActorState(knownActors, actor, kv.Value)) setN++;
                }

                // Fire the UI re-render hook ONCE (public TacticalLevelController.FactionKnowledgeChanged).
                FireFactionKnowledgeChanged(tlc, faction);

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacVision, snap.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.vision seq=" + snap.Seq + " factionIdx=" +
                          snap.ViewerFactionIndex + " set=" + setN + " forget=" + forgetN +
                          " (snapshotCount=" + snap.Entries.Count + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleVision failed: " + ex); }
        }

        // ─── KnownActors engine helpers ─────────────────────────────────────────────────────────────

        private static FieldInfo _knownActorsField;

        /// <summary>The <c>public readonly Dictionary&lt;TacticalActorBase, KnownCounters&gt; KnownActors</c>
        /// (TacticalFactionVision.cs:115) as an <see cref="IDictionary"/> (readonly = the field ref; the dict is
        /// mutable — mirrors NullFactionKnownActorsScrubPatch).</summary>
        private static IDictionary GetKnownActorsDict(object vision)
        {
            if (vision == null) return null;
            if (_knownActorsField == null || !_knownActorsField.DeclaringType.IsInstanceOfType(vision))
                _knownActorsField = AccessTools.Field(vision.GetType(), "KnownActors");
            return _knownActorsField != null ? _knownActorsField.GetValue(vision) as IDictionary : null;
        }

        /// <summary>Read the current vision state of an actor: 2 (Revealed) &gt; 1 (Located) &gt; 0 (neither).
        /// Uses the public <c>IsRevealed</c>/<c>IsLocated</c> on the vision (TacticalFactionVision.cs:235-243).</summary>
        private static int VisionStateForActor(object vision, object actor)
        {
            try
            {
                if (InvokeBool(vision, "IsRevealed", actor)) return TacticalLiveCodec.VisionStateRevealed;   // 2
                if (InvokeBool(vision, "IsLocated", actor)) return TacticalLiveCodec.VisionStateLocated;     // 1
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] VisionStateForActor failed: " + ex); }
            return 0;
        }

        /// <summary>Set an actor's known state to <paramref name="state"/> (2=Revealed,1=Located) on the given
        /// KnownActors dict by manipulating its <c>KnownCounters</c> directly: ensure an entry exists, set the
        /// target counter positive, and clear the other so a downgrade (Revealed→Located) really drops the higher
        /// state. notifyChange is deferred — the single FactionKnowledgeChanged at the end drives the UI. Returns
        /// true if the entry now reflects the target state.</summary>
        private static bool SetActorState(IDictionary knownActors, object actor, int state)
        {
            try
            {
                object counters = knownActors.Contains(actor) ? knownActors[actor] : null;
                if (counters == null)
                {
                    counters = NewKnownCounters(actor);
                    if (counters == null) return false;
                    knownActors[actor] = counters;
                }
                object revealedEnum = KnownStateValue("Revealed");
                object locatedEnum = KnownStateValue("Located");
                if (revealedEnum == null || locatedEnum == null) return false;

                if (state == TacticalLiveCodec.VisionStateRevealed)
                {
                    // Revealed (red): set Revealed positive AND keep/raise Located (a revealed actor is also located).
                    IncrementCounterTo(counters, revealedEnum, 1);
                    IncrementCounterTo(counters, locatedEnum, 1);
                }
                else
                {
                    // Located (grey): clear Revealed (downgrade), set Located positive.
                    ResetCounter(counters, revealedEnum);
                    IncrementCounterTo(counters, locatedEnum, 1);
                }
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] SetActorState failed: " + ex); return false; }
        }

        /// <summary>Forget an actor on the client: remove it from KnownActors (mirrors the native
        /// <c>ForgetForAll</c> body TacticalFactionVision.cs:527 — <c>KnownActors.Remove(actor)</c>). Returns
        /// true if it was present and removed.</summary>
        private static bool ForgetActor(IDictionary knownActors, object actor)
        {
            try
            {
                if (!knownActors.Contains(actor)) return false;
                knownActors.Remove(actor);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ForgetActor failed: " + ex); return false; }
        }

        // KnownCounters reflection: the nested public class TacticalFactionVision+KnownCounters with public
        // IncrementCounterTo(KnownState,int) / ResetCounter(KnownState) (TacticalFactionVision.cs:55/74).
        private static Type _knownCountersType;
        private static Type _knownStateType;
        private static MethodInfo _incrementCounterTo;
        private static MethodInfo _resetCounter;

        private static object NewKnownCounters(object actor)
        {
            try
            {
                if (_knownCountersType == null)
                {
                    var visionType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
                    _knownCountersType = visionType != null
                        ? visionType.GetNestedType("KnownCounters", BindingFlags.Public | BindingFlags.NonPublic)
                        : null;
                }
                return _knownCountersType != null ? Activator.CreateInstance(_knownCountersType) : null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] NewKnownCounters failed: " + ex); return null; }
        }

        private static object KnownStateValue(string name)
        {
            try
            {
                if (_knownStateType == null) _knownStateType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.KnownState");
                return _knownStateType != null ? Enum.Parse(_knownStateType, name) : null;
            }
            catch { return null; }
        }

        private static void IncrementCounterTo(object counters, object stateEnum, int value)
        {
            if (_incrementCounterTo == null || !_incrementCounterTo.DeclaringType.IsInstanceOfType(counters))
                _incrementCounterTo = AccessTools.Method(counters.GetType(), "IncrementCounterTo");
            _incrementCounterTo?.Invoke(counters, new object[] { stateEnum, value });
        }

        private static void ResetCounter(object counters, object stateEnum)
        {
            if (_resetCounter == null || !_resetCounter.DeclaringType.IsInstanceOfType(counters))
                _resetCounter = AccessTools.Method(counters.GetType(), "ResetCounter");
            _resetCounter?.Invoke(counters, new object[] { stateEnum });
        }

        // ─── Faction / TLC resolution ───────────────────────────────────────────────────────────────

        /// <summary>The shared player faction (<c>IsControlledByPlayer</c>) + its index in <c>Factions</c>.</summary>
        private static object ResolvePlayerFaction(object tlc, out int index)
        {
            index = -1;
            var factions = GetProp(tlc, "Factions") as IEnumerable;
            if (factions == null) return null;
            int i = 0;
            foreach (var f in factions)
            {
                if (f != null && ToBool(GetProp(f, "IsControlledByPlayer"))) { index = i; return f; }
                i++;
            }
            return null;
        }

        private static object ResolveFactionByIndex(object tlc, int index)
        {
            if (index < 0) return null;
            var factions = GetProp(tlc, "Factions") as IList;
            if (factions != null) return index < factions.Count ? factions[index] : null;
            // Fallback: enumerate.
            var en = GetProp(tlc, "Factions") as IEnumerable;
            if (en == null) return null;
            int i = 0;
            foreach (var f in en) { if (i == index) return f; i++; }
            return null;
        }

        private static void FireFactionKnowledgeChanged(object tlc, object faction)
        {
            try
            {
                var m = AccessTools.Method(tlc.GetType(), "FactionKnowledgeChanged");
                if (m != null) m.Invoke(tlc, new[] { faction });
                else Debug.LogError("[Multiplayer][tac] vision: FactionKnowledgeChanged not found");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] FireFactionKnowledgeChanged failed: " + ex); }
        }

        /// <summary>Order-stable content signature of a snapshot (factionIndex + sorted netId:state pairs) so two
        /// snapshots with the same known set compare equal regardless of KnownActors enumeration order.</summary>
        private static string BuildSignature(int factionIndex, List<TacticalLiveCodec.VisionEntry> entries)
        {
            var copy = new List<TacticalLiveCodec.VisionEntry>(entries);
            copy.Sort((a, b) => a.NetId != b.NetId ? a.NetId.CompareTo(b.NetId) : a.KnownState.CompareTo(b.KnownState));
            var sb = new System.Text.StringBuilder();
            sb.Append(factionIndex).Append('#');
            foreach (var e in copy) sb.Append(e.NetId).Append(':').Append(e.KnownState).Append(';');
            return sb.ToString();
        }

        // ─── small reflection helpers ───────────────────────────────────────────────────────────────
        private static bool InvokeBool(object obj, string method, object arg)
        {
            var m = AccessTools.Method(obj.GetType(), method);
            if (m == null) return false;
            object r = m.Invoke(obj, new[] { arg });
            return r is bool b && b;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        private static bool ToBool(object o) => o is bool b && b;
    }
}
