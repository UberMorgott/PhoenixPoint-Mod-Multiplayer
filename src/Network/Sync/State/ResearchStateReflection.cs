using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the host-authoritative RESEARCH state channel (channel #2). The mod has
    /// NO compile-time game references, so every member is resolved by name and cached. This complements
    /// <see cref="Multipleer.Network.Sync.ResearchReflection"/> (which already binds AddResearchToQueue /
    /// CompleteResearch / GetResearchById and the id field) with the snapshot/reconcile + change-event
    /// surface the channel needs.
    ///
    /// Verified against the decompile (2026-06-16, <c>PhoenixPoint.Geoscape.Entities.Research</c>):
    ///   • path: <c>GeoRuntime.PhoenixFaction()</c> → <c>GeoFaction.Research</c> (public FIELD,
    ///     GeoFaction.cs:79 <c>public Research Research;</c> — bound via AccessTools.Field, mirroring
    ///     <c>ItemStorageReflection.GetStorage</c>) → the <c>Research</c> instance.
    ///   • completed set: <c>Research.Completed : IEnumerable&lt;ResearchElement&gt;</c> (Research.cs:123,
    ///     LINQ over <c>AllResearchesArray</c> where <c>IsCompleted</c>). Each → <c>ResearchID</c>.
    ///   • queue: <c>Research.ResearchQueue : List&lt;ResearchElement&gt;</c> (public property, Research.cs:51).
    ///     Index 0 == <c>Current</c>; order is the live queue order.
    ///   • progress: <c>ResearchElement.ResearchProgress</c> is a PUBLIC <c>float</c> FIELD
    ///     (ResearchElement.cs:139) — read AND written directly (no setter method). The game itself
    ///     assigns it directly (Research.cs:823, :1255).
    ///   • resolver: reuse <c>Research.GetResearchById(string)</c> (Research.cs:763).
    ///   • complete (client apply): do NOT call <c>Research.CompleteResearch</c>. Its
    ///     <c>research.State = Completed</c> assignment runs the <c>ResearchElement.State</c> setter →
    ///     <c>OnStateChanged(prev,curr)</c> which walks <c>i=prev+1..curr</c> firing
    ///     Reveal()/Unlock()/<c>Complete()</c> (ResearchElement.cs:417-438). <c>Complete()</c>
    ///     (ResearchElement.cs:574-586) runs the host-only REWARD side-effects: <c>ApplyRewards()</c> →
    ///     <c>GiveReward(Faction)</c> + <c>Faction.Wallet.Give(ResearchDef.Resources)</c> +
    ///     <c>OnCompleted</c>. On the host-authoritative CLIENT those rewards (wallet, unlocks) already
    ///     arrive via the wallet echo / inventory channel, so re-running them = double-apply + desync.
    ///     Instead we set the private <c>_state</c> backing field (ResearchElement.cs:133
    ///     <c>[SerializeMember] private ResearchState _state</c>) DIRECTLY to <c>Completed</c>, which
    ///     bypasses the entire setter cascade (no rewards, no events, no out-of-order reveals), and set
    ///     <c>ResearchProgress = ResearchCost</c> + clear <c>IsInProgress</c> so the UI reads "done"
    ///     (<c>IsCompleted</c>/<c>Progress01</c>, ResearchElement.cs:166/174). Each completed element is
    ///     driven independently from the snapshot, so dependents reach their own authoritative state from
    ///     their own snapshot entries — no reliance on prereq-topological cascade ordering.
    ///   • add to queue: <c>Research.AddResearchToQueue(ResearchElement)</c> (Research.cs:370).
    ///   • cancel/remove from queue: <c>Research.Cancel(ResearchElement)</c> (Research.cs:461). This is the
    ///     exact method the UI cancel button calls (UIModuleResearch.cs:435 <c>Research.Cancel(...)</c>) —
    ///     used both by the reconcile and the client cancel intent below.
    ///   • host change events (faction-level): <c>GeoFaction.ResearchStartedEventHandler</c> and
    ///     <c>GeoFaction.ResearchCompletedEventHandler</c>, both <c>event Action&lt;GeoFaction, ResearchElement&gt;</c>
    ///     (GeoFaction.cs:309-311). The delegate's params are reference types, but a fully-<c>object</c>
    ///     adapter is still not delegate-compatible, so we emit a <see cref="DynamicMethod"/> matching the
    ///     exact signature (mirrors <c>WalletReflection.SubscribeResourcesChanged</c>). No faction-level
    ///     cancel event exists; cancels are propagated by marking the channel dirty in the cancel
    ///     interceptor (host branch) + the next start/complete/HourTick snapshot — both converge.
    /// </summary>
    public static class ResearchStateReflection
    {
        private static bool _ready;
        private static Type _researchType;        // ...Research.Research
        private static Type _researchElementType; // ...Research.ResearchElement
        private static FieldInfo _factionResearchField;   // GeoFaction.Research (public field, GeoFaction.cs:79)
        private static PropertyInfo _completedProp;       // Research.Completed (IEnumerable<ResearchElement>)
        private static PropertyInfo _queueProp;           // Research.ResearchQueue (List<ResearchElement>)
        private static PropertyInfo _visibleProp;         // Research.Visible (IEnumerable<ResearchElement>): (IsUnlocked||IsRevealed)&&!IsCurrentlyResearched
        private static MethodInfo _getById;               // Research.GetResearchById(string)
        private static MethodInfo _addToQueue;            // Research.AddResearchToQueue(ResearchElement)
        private static MethodInfo _cancel;                // Research.Cancel(ResearchElement)
        private static MethodInfo _putToTop;              // Research.PutInFromOfQueue(ResearchElement)
        private static MethodInfo _putUp;                 // Research.PutUpInQueue(ResearchElement)
        private static MethodInfo _putDown;               // Research.PutDownInQueue(ResearchElement)
        private static MethodInfo _insertAt;              // Research.InsertAtPosition(ResearchElement, int)
        private static FieldInfo _researchIdField;        // ResearchElement.ResearchID
        private static FieldInfo _progressField;          // ResearchElement.ResearchProgress (float)
        private static FieldInfo _stateField;             // ResearchElement._state (private ResearchState backing field)
        private static FieldInfo _inProgressBackingField; // ResearchElement.<IsInProgress>k__BackingField (bool)
        private static PropertyInfo _researchCostProp;    // ResearchElement.ResearchCost (int => ResearchDef.ResearchCost)
        private static object _completedStateValue;       // ResearchState.Completed enum value (boxed)
        private static object _unlockedStateValue;         // ResearchState.Unlocked enum value (boxed)
        private static object _revealedStateValue;         // ResearchState.Revealed enum value (boxed)
        private static Type _researchStateType;            // PhoenixPoint...Research.ResearchState (enum), for byte<->enum

        private static void Ensure()
        {
            if (_ready) return;
            _researchType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.Research");
            _researchElementType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchElement");
            if (_researchType == null || _researchElementType == null) return;

            _completedProp = AccessTools.Property(_researchType, "Completed");
            _queueProp = AccessTools.Property(_researchType, "ResearchQueue");
            // Research.Visible (Research.cs:95): (IsUnlocked||IsRevealed)&&!IsCurrentlyResearched — the EXACT
            // set the game's own Available (left) list iterates. Reused to snapshot the host's revealed/
            // unlocked, non-queued elements so the client mirrors that list. NOT in the _ready gate (FIX#2 is
            // additive; a future rename must never regress the core start/cancel/converge path).
            _visibleProp = AccessTools.Property(_researchType, "Visible");
            _getById = AccessTools.Method(_researchType, "GetResearchById", new[] { typeof(string) });
            _addToQueue = AccessTools.Method(_researchType, "AddResearchToQueue", new[] { _researchElementType });
            _cancel = AccessTools.Method(_researchType, "Cancel", new[] { _researchElementType });
            // Queue-REORDER ops (Research.cs:414/424/435/446). Bound but NOT part of the _ready gate so a
            // future rename of a reorder method can never regress the start/cancel/converge path; each
            // consumer (Reorder / EnforceQueueOrder) null-checks its own method.
            _putToTop = AccessTools.Method(_researchType, "PutInFromOfQueue", new[] { _researchElementType });
            _putUp = AccessTools.Method(_researchType, "PutUpInQueue", new[] { _researchElementType });
            _putDown = AccessTools.Method(_researchType, "PutDownInQueue", new[] { _researchElementType });
            _insertAt = AccessTools.Method(_researchType, "InsertAtPosition", new[] { _researchElementType, typeof(int) });
            _researchIdField = AccessTools.Field(_researchElementType, "ResearchID");
            _progressField = AccessTools.Field(_researchElementType, "ResearchProgress");
            // (1) Reward-free completion: write the private _state backing field directly (bypasses the
            // ResearchElement.State setter → OnStateChanged → Complete() reward cascade). AccessTools.Field
            // finds private backing fields; <IsInProgress>k__BackingField is the auto-prop backing field.
            _stateField = AccessTools.Field(_researchElementType, "_state");
            _inProgressBackingField = AccessTools.Field(_researchElementType, "<IsInProgress>k__BackingField");
            _researchCostProp = AccessTools.Property(_researchElementType, "ResearchCost");
            _researchStateType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchState");
            if (_researchStateType != null && _researchStateType.IsEnum)
            {
                try { _completedStateValue = Enum.Parse(_researchStateType, "Completed"); } catch { _completedStateValue = null; }
                // ResearchState { Hidden, Revealed, Unlocked, Completed } (decompile). AddResearchToQueue
                // (Research.cs:380) THROWS unless State == Unlocked; the snapshot never carries the host
                // Revealed->Unlocked transition, so the client element can still be Revealed. We force it to
                // Unlocked via the _state backing field (same bypass as CompleteEchoOnly) before the add.
                try { _unlockedStateValue = Enum.Parse(_researchStateType, "Unlocked"); } catch { _unlockedStateValue = null; }
                // Revealed value: needed to mirror the host's Revealed-but-not-yet-Unlocked elements onto the
                // client (FIX#2 state block). Not gated by _ready — best-effort additive mirror.
                try { _revealedStateValue = Enum.Parse(_researchStateType, "Revealed"); } catch { _revealedStateValue = null; }
            }

            _ready = _completedProp != null && _queueProp != null && _getById != null
                     && _addToQueue != null && _cancel != null
                     && _researchIdField != null && _progressField != null
                     && _stateField != null && _completedStateValue != null
                     && _unlockedStateValue != null;
        }

        /// <summary>The live player-faction <c>Research</c> instance, or null.</summary>
        public static object GetResearch(GeoRuntime rt)
        {
            var fac = rt?.PhoenixFaction();
            if (fac == null) return null;
            try
            {
                if (_factionResearchField == null || _factionResearchField.DeclaringType == null
                    || !_factionResearchField.DeclaringType.IsInstanceOfType(fac))
                    _factionResearchField = AccessTools.Field(fac.GetType(), "Research");
                return _factionResearchField?.GetValue(fac);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.GetResearch failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Host: snapshot the authoritative research state — completed research ids + the ordered queue of
        /// (id, progress). Null if unavailable.
        /// </summary>
        public static ResearchSnapshot Snapshot(GeoRuntime rt)
        {
            try
            {
                Ensure();
                if (!_ready) return null;
                var research = GetResearch(rt);
                if (research == null) return null;

                var snap = new ResearchSnapshot();

                if (_completedProp.GetValue(research, null) is IEnumerable completed)
                    foreach (var el in completed)
                    {
                        string id = _researchIdField.GetValue(el) as string;
                        if (!string.IsNullOrEmpty(id)) snap.Completed.Add(id);
                    }

                if (_queueProp.GetValue(research, null) is IEnumerable queue)
                    foreach (var el in queue)
                    {
                        string id = _researchIdField.GetValue(el) as string;
                        if (string.IsNullOrEmpty(id)) continue;
                        float progress = 0f;
                        try { progress = (float)_progressField.GetValue(el); } catch { progress = 0f; }
                        snap.Queue.Add((id, progress));
                    }

                // FIX#2: snapshot the host's Available (left-list) set — the non-completed, non-queued
                // elements the host has Revealed/Unlocked — each with its authoritative ResearchState byte,
                // so the client mirrors that list reactively (a reveal/unlock of OTHER research from a
                // completion now reaches the client). Research.Visible is the game's own predicate for that
                // exact set: (IsUnlocked||IsRevealed)&&!IsCurrentlyResearched (Research.cs:95). Best-effort:
                // if Visible is unavailable, the queue/completed sync is unaffected (additive).
                if (_visibleProp != null)
                {
                    try
                    {
                        if (_visibleProp.GetValue(research, null) is IEnumerable visible)
                            foreach (var el in visible)
                            {
                                string id = _researchIdField.GetValue(el) as string;
                                if (string.IsNullOrEmpty(id)) continue;
                                byte state;
                                try { state = (byte)Convert.ToInt32(_stateField.GetValue(el)); } catch { continue; }
                                snap.States.Add((id, state));
                            }
                    }
                    catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Snapshot Visible failed (skipped): " + ex.Message); }
                }

                return snap;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Snapshot failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Client: reconcile the live research to EXACTLY match <paramref name="target"/> (idempotent).
        /// Research is monotonic — completed ids that the client hasn't completed are completed (never
        /// un-completed) via a REWARD-FREE state echo (<see cref="CompleteEchoOnly"/>): the client is a
        /// pure mirror and must NOT re-run the host reward cascade. The queue is then made to match the
        /// snapshot: cancel any queued element absent from the snapshot, add any missing element in order,
        /// and set each element's progress to the snapshot value (direct field write, gated so a requeue
        /// can never side-effect-complete an item).
        /// </summary>
        public static void Apply(GeoRuntime rt, ResearchSnapshot target)
        {
            if (target == null) return;
            try
            {
                Ensure();
                if (!_ready) return;
                var research = GetResearch(rt);
                if (research == null) return;

                // (1) Complete newly-completed research (monotonic) WITHOUT side-effects. The client is a
                // pure mirror; rewards (wallet/unlocks) arrive via the wallet echo + inventory channel, so
                // we must NOT run CompleteResearch's reward cascade. CompleteEchoOnly writes the private
                // _state backing field directly to Completed (bypassing OnStateChanged). Driving each id
                // independently from the snapshot also removes any reliance on prereq-topological ordering:
                // dependents reach their own state from their own snapshot entries.
                foreach (var id in target.Completed)
                {
                    var el = Resolve(research, id);
                    if (el == null) continue;
                    if (IsCompleted(el)) continue;
                    CompleteEchoOnly(el);
                }

                // (2) Cancel queued elements that are NOT in the snapshot. Snapshot a copy of the live
                // queue first — Cancel mutates the underlying list.
                var wanted = new HashSet<string>();
                foreach (var (id, _) in target.Queue) wanted.Add(id);

                var live = new List<object>();
                if (_queueProp.GetValue(research, null) is IEnumerable q)
                    foreach (var el in q) live.Add(el);
                foreach (var el in live)
                {
                    string id = _researchIdField.GetValue(el) as string;
                    if (string.IsNullOrEmpty(id) || !wanted.Contains(id))
                        _cancel.Invoke(research, new[] { el });
                }

                // (3) Add missing elements (in snapshot order) and set each element's progress. Each
                // element is reconciled in its OWN try/catch: AddResearchToQueue throws on already-
                // completed / wrong-state / unmet-requirements elements (Research.cs:374-386), and one
                // bad element must NOT abort reconciling the rest of the queue.
                foreach (var (id, progress) in target.Queue)
                {
                    try
                    {
                        var el = Resolve(research, id);
                        if (el == null) continue;
                        if (IsCompleted(el)) continue;           // already done → not a queue item
                        // (3) GATE the requeue so a queue item the host meant as IN-PROGRESS can NEVER
                        // instant-complete on the client. AddResearchToQueue (Research.cs:393) calls the
                        // reward-bearing CompleteResearch whenever ResearchProgress >= ResearchCost. A
                        // snapshot whose progress >= cost is contradictory (it would be in Completed, not
                        // the queue) — but rounding / a transient host value could trip it. We therefore
                        // set a SUB-COST progress before the requeue (so the instant-complete branch is
                        // never taken), then write the true snapshot progress ONLY AFTER a successful add,
                        // when no further completion check runs. The element is never side-effect-completed
                        // during reconcile, and a failed add never leaves progress on a non-queued element.
                        bool wasInQueue = InQueue(research, el);
                        if (!wasInQueue)
                        {
                            // The snapshot carries only (id, progress); it never carries the host
                            // Revealed->Unlocked transition. AddResearchToQueue (Research.cs:380) THROWS
                            // unless State == Unlocked, and a swallowed throw would leave progress on a
                            // NON-QUEUED element → the bug: it then renders in the Available (left) list
                            // with a partial progress chunk (queue membership, not State, is the
                            // available/queued discriminator). Force State=Unlocked via the _state backing
                            // field FIRST (same reflection bypass as CompleteEchoOnly), so the add can't
                            // throw on a Revealed element.
                            float original = 0f;
                            try { original = (float)_progressField.GetValue(el); } catch { original = 0f; }
                            // Capture the ORIGINAL state too: if the add throws we must restore it. Forcing
                            // State=Unlocked and leaving it on a NON-QUEUED element makes the element render
                            // in the Available (left) list with the active "can research now" affordance
                            // (ResearchListItem IsUnlocked branch) even though CanResearch() is false — a
                            // visually-misleading marker the host never showed (the false-marker bug). Only a
                            // queued element is excluded from the Available list, so an Unlocked element that
                            // failed to queue MUST be rolled back to its host-authoritative state.
                            object originalState = null;
                            try { originalState = _stateField.GetValue(el); } catch { originalState = null; }
                            try { _stateField.SetValue(el, _unlockedStateValue); } catch { /* best-effort */ }
                            float safe = SafeRequeueProgress(el, progress);
                            try { _progressField.SetValue(el, safe); } catch { /* best-effort */ }
                            try
                            {
                                _addToQueue.Invoke(research, new[] { el });
                            }
                            catch
                            {
                                // Add failed → element is NOT in the queue. Restore BOTH its original progress
                                // AND its original state so we never leave a stray (sub-cost) value or a stray
                                // Unlocked (false marker) on a non-queued element, then rethrow into the
                                // per-element catch below for logging.
                                try { _progressField.SetValue(el, original); } catch { /* best-effort */ }
                                if (originalState != null) { try { _stateField.SetValue(el, originalState); } catch { /* best-effort */ } }
                                throw;
                            }
                        }
                        // Affirm the authoritative progress ONLY after the element is confirmed in the queue.
                        // BeginResearching() (ResearchElement.cs:282) does not reset ResearchProgress, and no
                        // completion check runs here, so the true snapshot value lands safely (no reward
                        // cascade). Skip if the add somehow did not land it in the queue.
                        if (InQueue(research, el))
                        {
                            try { _progressField.SetValue(el, progress); } catch { /* affirm */ }
                        }
                    }
                    catch (Exception elEx)
                    {
                        Debug.LogError("[Multipleer] ResearchStateReflection.Apply requeue '" + id + "' failed (skipped): " + elEx.Message);
                    }
                }

                // (4) ENFORCE the snapshot queue ORDER. A pure REORDER (host moved a queued item up/down/
                // to-top with no add/cancel) changes only the order: every element is already present in the
                // client queue, so steps (2)/(3) above leave it untouched and the client order stays stale.
                // The snapshot's Queue list IS ordered (it iterates the live ResearchQueue, index 0 ==
                // Current), so drive each snapshot entry to its target index via Research.InsertAtPosition
                // (Research.cs:446 — Remove + Insert(Clamp(pos)), the same primitive the native reorder uses).
                // Placing each element in turn at its target index deterministically rebuilds the full target
                // order. Idempotent: an already-correctly-positioned element re-inserts at its own index, a
                // no-op. Best-effort + null-checked so a missing InsertAtPosition never regresses convergence.
                EnforceQueueOrder(research, target);

                // (5) FIX#2: MIRROR the host's Available-list state. For each element the host reports as
                // Revealed/Unlocked AND non-queued (target.States, built from Research.Visible), drive the
                // client element's _state to the host value DIRECTLY (bypassing the State setter → no reveal/
                // unlock event cascade, no reward). This makes a reveal/unlock of OTHER research from a host
                // completion appear in the client's Available (left) list reactively, and makes the marker
                // AUTHORITATIVE (host-driven) — Unlocked shows the "research now" affordance, Revealed does
                // not. Disjoint from step (3) by construction: Visible excludes queued elements, so a States
                // entry is never also a Queue entry. Guards: skip completed (monotonic — never downgrade a
                // client Completed) and skip queued (those get their state from the add path). Best-effort.
                MirrorStates(research, target);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Apply failed: " + ex.Message); }
        }

        /// <summary>
        /// Reorder the live queue to match <paramref name="target"/>'s ordered queue exactly, using
        /// <c>Research.InsertAtPosition(element, index)</c>. Only elements present in BOTH the snapshot and
        /// the live queue are positioned (add/cancel already reconciled membership in <see cref="Apply"/>),
        /// so this never adds or removes — it only moves. No-op if <c>InsertAtPosition</c> is unavailable.
        /// </summary>
        private static void EnforceQueueOrder(object research, ResearchSnapshot target)
        {
            if (_insertAt == null || target == null) return;
            try
            {
                int idx = 0;
                foreach (var (id, _) in target.Queue)
                {
                    var el = Resolve(research, id);
                    if (el == null) continue;
                    if (!InQueue(research, el)) continue;       // membership is reconciled elsewhere; only move present items
                    try { _insertAt.Invoke(research, new object[] { el, idx }); }
                    catch (Exception elEx) { Debug.LogError("[Multipleer] ResearchStateReflection.EnforceQueueOrder '" + id + "' failed (skipped): " + elEx.Message); }
                    idx++;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.EnforceQueueOrder failed: " + ex.Message); }
        }

        /// <summary>
        /// FIX#2 (client): mirror the host's Available-list element states. For each <c>(id, stateByte)</c>
        /// in <paramref name="target"/>.States — the host's Revealed/Unlocked, non-queued elements — set the
        /// matching client element's private <c>_state</c> backing field to the host value (bypassing the
        /// <c>State</c> setter → no Reveal()/Unlock() event cascade, no rewards: the client is a pure mirror).
        /// Skips elements the client already has Completed (research is monotonic — never downgrade) and
        /// elements currently in the queue (their state is driven by the add path). Only Revealed/Unlocked
        /// values are honored (defensive); an unknown byte is ignored. Best-effort + per-element guarded so a
        /// single bad entry never aborts the rest. No-op if the Revealed/Unlocked enum values are unavailable.
        /// </summary>
        private static void MirrorStates(object research, ResearchSnapshot target)
        {
            if (target == null || target.States.Count == 0) return;
            if (_revealedStateValue == null && _unlockedStateValue == null) return;
            byte revealedByte = 1, unlockedByte = 2; // ResearchState.Revealed=1, Unlocked=2 (decompile-verified)
            foreach (var (id, stateByte) in target.States)
            {
                try
                {
                    // Only Revealed/Unlocked are mirrored. Completed lives in target.Completed (handled in step
                    // (1)); Hidden is the default and is never sent. An unexpected byte → skip.
                    object stateValue;
                    if (stateByte == unlockedByte) stateValue = _unlockedStateValue;
                    else if (stateByte == revealedByte) stateValue = _revealedStateValue;
                    else continue;
                    if (stateValue == null) continue;

                    var el = Resolve(research, id);
                    if (el == null) continue;
                    if (IsCompleted(el)) continue;             // monotonic: never downgrade a completed element
                    if (InQueue(research, el)) continue;       // queued elements get their state from the add path
                    _stateField.SetValue(el, stateValue);
                }
                catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.MirrorStates '" + id + "' failed (skipped): " + ex.Message); }
            }
        }

        /// <summary>Host apply for the client cancel intent: <c>Research.Cancel(resolve(id))</c>.</summary>
        public static void Cancel(GeoRuntime rt, string researchId)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var research = GetResearch(rt);
                var el = Resolve(research, researchId);
                if (el == null) return;
                _cancel.Invoke(research, new[] { el });
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Cancel failed: " + ex.Message); }
        }

        /// <summary>
        /// Host apply for the client REORDER intent: replay the exact native relative move
        /// (<c>PutInFromOfQueue</c> / <c>PutUpInQueue</c> / <c>PutDownInQueue</c>) on the authoritative
        /// queue. The host and client queues are kept identical by ch2, so a relative move yields the same
        /// result on both; the new order then echoes to every peer via the research channel (which carries
        /// the ordered queue and whose client reconcile enforces that order). No-op if the matching native
        /// method is unavailable (best-effort, like the rest of this bridge).
        /// </summary>
        public static void Reorder(GeoRuntime rt, string researchId, Actions.ResearchReorderKind kind)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var research = GetResearch(rt);
                var el = Resolve(research, researchId);
                if (el == null) return;
                MethodInfo m;
                switch (kind)
                {
                    case Actions.ResearchReorderKind.ToTop: m = _putToTop; break;
                    case Actions.ResearchReorderKind.Up: m = _putUp; break;
                    case Actions.ResearchReorderKind.Down: m = _putDown; break;
                    default: m = null; break;
                }
                if (m == null) return;
                m.Invoke(research, new[] { el });
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Reorder failed: " + ex.Message); }
        }

        /// <summary>Read <c>ResearchElement.ResearchID</c> off a live element (interceptor side).</summary>
        public static string GetId(object researchElement)
        {
            if (researchElement == null) return null;
            try { Ensure(); return _researchIdField?.GetValue(researchElement) as string; }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.GetId failed: " + ex.Message); return null; }
        }

        // ─── host change-event subscription (faction-level Start/Complete) ───

        /// <summary>
        /// Subscribe a no-arg callback to BOTH <c>GeoFaction.ResearchStartedEventHandler</c> and
        /// <c>ResearchCompletedEventHandler</c> on the player faction. Returns an opaque token to pass to
        /// <see cref="Unsubscribe"/>, or null. The events are <c>Action&lt;GeoFaction, ResearchElement&gt;</c>;
        /// we emit a DynamicMethod adapter matching that exact signature (mirrors WalletReflection).
        /// </summary>
        public static object SubscribeFactionResearchEvents(GeoRuntime rt, Action onChanged)
        {
            if (onChanged == null) return null;
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;
                var facType = fac.GetType();
                var startEvt = facType.GetEvent("ResearchStartedEventHandler", BindingFlags.Public | BindingFlags.Instance);
                var doneEvt = facType.GetEvent("ResearchCompletedEventHandler", BindingFlags.Public | BindingFlags.Instance);
                if (startEvt == null && doneEvt == null) return null;

                var token = new FactionEventToken { Faction = fac };
                token.StartHandler = MakeAdapter(startEvt, onChanged);
                if (token.StartHandler != null) { startEvt.AddEventHandler(fac, token.StartHandler); token.StartEvt = startEvt; }
                token.DoneHandler = MakeAdapter(doneEvt, onChanged);
                if (token.DoneHandler != null) { doneEvt.AddEventHandler(fac, token.DoneHandler); token.DoneEvt = doneEvt; }
                return token;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.SubscribeFactionResearchEvents failed: " + ex.Message); return null; }
        }

        public static void Unsubscribe(object token)
        {
            if (token is HourlyTickToken h) { UnsubscribeHourly(h); return; }
            if (!(token is FactionEventToken t) || t.Faction == null) return;
            try
            {
                if (t.StartEvt != null && t.StartHandler != null) t.StartEvt.RemoveEventHandler(t.Faction, t.StartHandler);
                if (t.DoneEvt != null && t.DoneHandler != null) t.DoneEvt.RemoveEventHandler(t.Faction, t.DoneHandler);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Unsubscribe failed: " + ex.Message); }
        }

        // ─── host hourly-tick subscription (research progress heartbeat, FIX#1) ───

        /// <summary>
        /// FIX#1: subscribe a no-arg callback to <c>GeoLevelController.HourTicked</c>
        /// (<c>public event Action&lt;int&gt;</c>, GeoLevelController.cs:340), fired once per in-game hour at
        /// the END of <c>LevelHourlyUpdateCrt</c> — AFTER <c>faction.UpdateResearch()</c> accrues that hour's
        /// research progress (GeoLevelController.cs:808, tick at :832). Re-marking ch2 dirty here re-sends the
        /// ordered queue snapshot (which already carries per-element progress) every in-game hour, so the
        /// client progress bar tracks the host at the host's own update granularity (no client-local sim — the
        /// time-sync clock overwrite suppresses it; snapshot Apply OVERWRITES progress, never accumulates).
        /// The event type is the concrete <c>Action&lt;int&gt;</c>, so we bind a typed handler directly (no
        /// DynamicMethod needed). Returns an opaque token for <see cref="Unsubscribe"/>, or null.
        /// </summary>
        public static object SubscribeHourlyTick(GeoRuntime rt, Action onChanged)
        {
            if (onChanged == null) return null;
            try
            {
                var geo = rt?.GeoLevel();
                if (geo == null) return null;
                var evt = geo.GetType().GetEvent("HourTicked", BindingFlags.Public | BindingFlags.Instance);
                if (evt == null) return null;
                // HourTicked is Action<int>; ignore the elapsed-hours arg and just re-snapshot.
                Action<int> handler = _ => onChanged();
                evt.AddEventHandler(geo, handler);
                return new HourlyTickToken { Level = geo, Evt = evt, Handler = handler };
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.SubscribeHourlyTick failed: " + ex.Message); return null; }
        }

        private static void UnsubscribeHourly(HourlyTickToken t)
        {
            if (t == null || t.Level == null || t.Evt == null || t.Handler == null) return;
            try { t.Evt.RemoveEventHandler(t.Level, t.Handler); }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.UnsubscribeHourly failed: " + ex.Message); }
        }

        private sealed class HourlyTickToken
        {
            public object Level;
            public EventInfo Evt;
            public Delegate Handler;
        }

        private sealed class FactionEventToken
        {
            public object Faction;
            public EventInfo StartEvt;
            public EventInfo DoneEvt;
            public Delegate StartHandler;
            public Delegate DoneHandler;
        }

        // ─── helpers ──────────────────────────────────────────────────────

        private static object Resolve(object research, string id)
        {
            if (research == null || string.IsNullOrEmpty(id)) return null;
            try { return _getById.Invoke(research, new object[] { id }); }
            catch { return null; }
        }

        private static bool IsCompleted(object element)
        {
            // ResearchElement.IsCompleted (bool property). Resolve lazily off the live element type.
            try
            {
                var p = AccessTools.Property(element.GetType(), "IsCompleted");
                return p != null && (bool)p.GetValue(element, null);
            }
            catch { return false; }
        }

        /// <summary>
        /// Bring <paramref name="element"/> to <c>Completed</c> as a PURE STATE ECHO — write the private
        /// <c>_state</c> backing field directly (bypassing the <c>ResearchElement.State</c> setter →
        /// <c>OnStateChanged</c> → <c>Complete()</c> reward cascade), then mirror the cosmetic completion
        /// flags the UI reads: <c>ResearchProgress = ResearchCost</c> (Progress01 → 100%) and clear
        /// <c>IsInProgress</c>. NO rewards, wallet, unlocks, or events fire — those arrive via the wallet
        /// echo / inventory channel. <see cref="_completedStateValue"/> and <see cref="_stateField"/> are
        /// guaranteed non-null when <c>_ready</c> (see <see cref="Ensure"/>).
        /// </summary>
        private static void CompleteEchoOnly(object element)
        {
            try { _stateField.SetValue(element, _completedStateValue); }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.CompleteEchoOnly _state write failed: " + ex.Message); }

            // Cosmetic flags the UI reads off a completed element. Best-effort: a missing optional field
            // must not abort the echo (state is already authoritative).
            try
            {
                int cost = GetResearchCost(element);
                if (cost > 0) _progressField.SetValue(element, (float)cost);
            }
            catch { /* progress is cosmetic for a Completed element */ }
            try { _inProgressBackingField?.SetValue(element, false); } catch { /* optional */ }
        }

        /// <summary>Read <c>ResearchElement.ResearchCost</c> (int =&gt; ResearchDef.ResearchCost), or 0.</summary>
        private static int GetResearchCost(object element)
        {
            try
            {
                if (_researchCostProp == null) return 0;
                var v = _researchCostProp.GetValue(element, null);
                return v is int i ? i : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Progress to set on <paramref name="element"/> JUST BEFORE <c>AddResearchToQueue</c> so its
        /// instant-complete branch (Research.cs:393, <c>progress &gt;= cost</c> → reward-bearing
        /// <c>CompleteResearch</c>) can never fire for an item the host meant as in-progress. Returns the
        /// requested <paramref name="progress"/> when it is safely below cost; otherwise a value strictly
        /// below cost (the true value is re-affirmed after the requeue, when no completion check runs).
        /// </summary>
        private static float SafeRequeueProgress(object element, float progress)
        {
            int cost = GetResearchCost(element);
            if (cost <= 0) return 0f;                 // unknown/zero cost → start at 0, never instant-complete
            float ceiling = (float)cost - 0.001f;     // strictly below cost
            if (ceiling < 0f) ceiling = 0f;
            return progress < ceiling ? progress : ceiling;
        }

        private static bool InQueue(object research, object element)
        {
            try
            {
                if (_queueProp.GetValue(research, null) is IEnumerable q)
                    foreach (var el in q) if (ReferenceEquals(el, element)) return true;
            }
            catch { /* fall through */ }
            return false;
        }

        /// <summary>Emit a DynamicMethod delegate matching <paramref name="evt"/>'s signature that ignores
        /// its args and calls <paramref name="onChanged"/>. Null if the event is null.</summary>
        private static Delegate MakeAdapter(EventInfo evt, Action onChanged)
        {
            if (evt == null) return null;
            try
            {
                Type handlerType = evt.EventHandlerType;
                MethodInfo invoke = handlerType.GetMethod("Invoke");
                if (invoke == null) return null;
                ParameterInfo[] ps = invoke.GetParameters();

                Type[] dmSig = new Type[ps.Length + 1];
                dmSig[0] = typeof(Action);
                for (int i = 0; i < ps.Length; i++) dmSig[i + 1] = ps[i].ParameterType;

                var dm = new DynamicMethod("Faction_Research_Adapter", typeof(void), dmSig,
                    typeof(ResearchStateReflection).Module, skipVisibility: true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke"));
                il.Emit(OpCodes.Ret);

                return dm.CreateDelegate(handlerType, onChanged);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.MakeAdapter failed: " + ex.Message); return null; }
        }
    }
}
