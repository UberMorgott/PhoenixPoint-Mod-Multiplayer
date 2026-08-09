using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PEER AUTONOMY for the geoscape WINDOW QUEUE (surface 0xB9, law 1 Intent) — the user mandate
    /// "if the host is AFK for an hour the remaining players must still open and resolve every window;
    /// if one player is left, they can play for everyone".
    ///
    /// THE BLOCK, from the game's own code. <c>GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch</c>:58-63
    /// dequeues ONLY while <c>_currentStateSwitchRequest == null</c>, and the single writer that clears that
    /// field is <c>FinishCurrentStateSwitch</c>:116 — i.e. a HOST CLICK, nothing else. So ONE un-dismissed
    /// window on an idle host wedges the host's window queue for the rest of the session — every later window
    /// piles up behind it forever. NARROWED 2026-08-04 by the pause rework: the CLOCK half of this argument is
    /// gone. <c>PauseGame = true</c> is now a ONE-SHOT pause the game issues on the peer that gets the window
    /// (<c>ProcessQueriedStateSwitch</c>:67-70 → <c>RequestGamePause</c>:1269) and ANY peer resumes it
    /// unconditionally, first-to-act-wins — an idle host no longer freezes the shared campaign. The QUEUE
    /// wedge is real and is the whole remaining reason this family exists.
    ///
    /// THE SEAM IS THE GAME'S OWN CHOKEPOINT, at ONE depth (it was two until 2026-08-01, see below):
    /// <c>UIStateGeoModal.FinishDialog(result)</c> (UIStateGeoModal.cs:82), which itself calls
    /// <c>FinishQueriedState</c> and THEN invokes the host's own <c>DialogCallback</c>. That closure is the
    /// one built at <c>GeoscapeView.OpenModalPersistent</c>:848 over the HOST's own <c>modalData</c>, so
    /// <c>ModalResultCallback</c>:799 runs on the host with host objects — a mission brief would resolve
    /// Confirm→<c>LaunchMission</c>:1043 / Cancel→<c>mission.Cancel()</c>,
    /// <c>FactionSoldierJoin</c>→<c>reward.Apply</c> (HavenMissionUtil.cs:59) — and the wire carries WHICH
    /// ANSWER and nothing else: no state, no objects, no callback (law 3, the client never executes game
    /// logic). The plain <c>GeoscapeView.FinishQueriedState()</c>:2164 arm for non-modal windows
    /// (<c>UIStateAssetDeployment</c>:66, <c>UIStateGeoCutscene</c>:92, <c>UIStateGeoscapeTutorial</c>:37,
    /// <c>UIStateReplenish</c>:64) is GONE: nothing on either side of it could be identified per-instance, so
    /// it was structurally incapable of naming a window rather than a kind — see <see cref="IdentityOf"/>.
    ///
    /// THE ANSWER IS ADDRESSED TO A WINDOW INSTANCE, and that is the entire safety argument. NARROWED
    /// 2026-08-01, same day it shipped, because the first version got this wrong and the wrongness was the
    /// bug: it named the view-state TYPE plus — for a modal — the <c>ModalType</c>, and a type is not a
    /// window. In the live 3-instance run a client closing its OWN <c>UIStateGeoscapeEvent</c> matched the
    /// host's unrelated <c>UIStateGeoscapeEvent</c> on the type alone and dismissed it (multiplayer.log
    /// 15:17:53 / 15:18:04 against multiplayer-2.log's two sends). A dismissed event picker is not merely a
    /// lost window either — <c>UIStateGeoscapeEvent.ExitState</c>:61-65 completes a still-Triggered event
    /// with <c>Choices.Last()</c> — so the host really was answered by the client's click, which is exactly
    /// what the identity check existed to make impossible. See <see cref="IdentityOf"/> for what replaced it.
    ///
    /// A mismatch is logged, never <see cref="IntentRail.Reject"/>ed — a reject exists to reconverge a client
    /// whose screen ran ahead of host state, and here NOTHING ran ahead: the closing peer's window was its
    /// own presentation and closing it locally was correct.
    ///
    /// FIRST-TO-ANSWER-WINS, the rail's standing arbitration (law 5). One peer's answer really does dismiss
    /// the host's copy — but only of the SAME window, the one the host itself raised to that peer. That is
    /// the same rule the tactical and event families already run on. <see cref="GeoModalMirror"/>'s "no
    /// dismiss message, on purpose" is not contradicted: that argues against the HOST hard-closing a client's
    /// open window, which still never happens.
    ///
    /// REACH TODAY, honestly stated, and it is exactly the set 0xB7 RAISES — never more: a peer can only
    /// answer a window the HOST RAISED TO IT. That is the mirrored notification modals (research complete
    /// above all, "the single most frequent window in a campaign"), the brief / soldier-join family since the
    /// generic EntityRef shape shipped, and — since 2026-08-05, through <see cref="OpDeploy"/> —
    /// <c>UIStateAssetDeployment</c>, which 0xB7 gained a non-modal arm for. It still does NOT reach the
    /// interception kinds (declared Gap: no rail identity for a GeoAirMission) or a cutscene (each peer holds
    /// its own copy and closes it): the ANSWER half is worthless without the RAISE half, and where the raise
    /// is missing <see cref="GeoWindowCoverage"/> names the hole rather than this file pretending to cover it.
    /// </summary>
    internal static class WindowQueueSync
    {
        [ThreadStatic] private static bool _durableSilentExit;
        [ThreadStatic] private static bool _durableRestoreActive;
        [ThreadStatic] private static bool _durableEnginePresentation;
        private static readonly Dictionary<OccurrenceId, GeoscapeViewStateSwitchRequest> _durableSuspended =
            new Dictionary<OccurrenceId, GeoscapeViewStateSwitchRequest>();
        private static readonly object _durableCarrierGate = new object();
        private static DurableInboxStore _durableCarrierStore;
        private static ulong _durableCarrierEpoch;
        private static readonly FieldInfo DurableEventStateField =
            AccessTools.Field(typeof(GeoscapeEventRecord), "_state");
        private static readonly FieldInfo DurableModalHandledField =
            AccessTools.Field(typeof(UIStateGeoModal), "_handledByDialog");

        internal static void ClearDurableRuntimeCarriers()
        { lock (_durableCarrierGate) { _durableSuspended.Clear(); _durableCarrierStore = null; _durableCarrierEpoch = 0; } }

        private static void EnsureDurableCarrierSession(DurableInboxStore store)
        {
            string local = Multiplayer.Network.ClientIdentity.PlayerGuid.ToString("D");
            ulong epoch = store?.Ledger.Members.Keys.Where(x => string.Equals(x.PlayerGuid, local,
                StringComparison.OrdinalIgnoreCase)).Select(x => x.Epoch).DefaultIfEmpty(0UL).Max() ?? 0;
            lock (_durableCarrierGate)
            {
                if (ReferenceEquals(_durableCarrierStore, store) && _durableCarrierEpoch == epoch) return;
                _durableSuspended.Clear(); _durableCarrierStore = store; _durableCarrierEpoch = epoch;
            }
        }

        internal static bool SuppressDurableCallback(bool durableSilentExit) => durableSilentExit;

        internal static GeoscapeEventRecordState? BeginSilentEventExit(GeoscapeEventRecord record, bool silent)
        {
            if (!SuppressDurableCallback(silent) || record == null || DurableEventStateField == null) return null;
            var original = record.State;
            DurableEventStateField.SetValue(record, GeoscapeEventRecordState.Completed); return original;
        }
        internal static void EndSilentEventExit(GeoscapeEventRecord record, GeoscapeEventRecordState? original)
        { if (original.HasValue && record != null && DurableEventStateField != null) DurableEventStateField.SetValue(record, original.Value); }
        internal static bool? BeginSilentModalExit(UIStateGeoModal state, bool silent)
        {
            if (!SuppressDurableCallback(silent) || state == null || DurableModalHandledField == null) return null;
            bool original = (bool)DurableModalHandledField.GetValue(state);
            DurableModalHandledField.SetValue(state, true); return original;
        }
        internal static void EndSilentModalExit(UIStateGeoModal state, bool? original)
        { if (original.HasValue && state != null && DurableModalHandledField != null) DurableModalHandledField.SetValue(state, original.Value); }

        internal static bool AlreadyCurrent(GeoscapeViewSwitchQuery query,
            GeoscapeViewStateSwitchRequest original, OccurrenceId occurrence)
        {
            if (original == null || !ReferenceEquals(WindowOrder.CurrentRequest(query), original)) return false;
            OccurrenceId bound;
            return WindowOrder.TryGetDurable(original, out bound) && bound.Equals(occurrence);
        }

        internal static bool TryDurablePriorityPreemption(GeoscapeViewSwitchQuery query,
            GeoscapeViewStateSwitchRequest current, GeoscapeViewStateSwitchRequest pending)
        {
            OccurrenceId ordinary, priority;
            if (!WindowOrder.TryGetDurable(current, out ordinary) || !WindowOrder.TryGetDurable(pending, out priority) ||
                DurableWindowRegistry.PriorityOf(ordinary) != DurableWindowPriority.Ordinary ||
                DurableWindowRegistry.PriorityOf(priority) == DurableWindowPriority.Ordinary) return false;
            var store = DurableInboxSaveBridge.ActiveStore;
            if (store == null) return false;
            EnsureDurableCarrierSession(store);
            string local = Multiplayer.Network.ClientIdentity.PlayerGuid.ToString("D");
            var memberships = store.Ledger.Members.Keys.Where(x =>
                string.Equals(x.PlayerGuid, local, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Epoch).ToArray();
            if (memberships.Length == 0) return false;
            var carrier = new NativeCarrier(query, ordinary, priority, current, pending);
            return new DurableInboxEngine(store, memberships[0], carrier).TryPreempt(ordinary, priority,
                true, typeof(UIStateNothingSelected));
        }

        internal static bool TryDurableResume(GeoscapeViewSwitchQuery query)
        {
            if (_durableRestoreActive) return false;
            var store = DurableInboxSaveBridge.ActiveStore; MembershipId member;
            if (store == null || !TryLocalMember(store, out member)) return false;
            EnsureDurableCarrierSession(store);
            return new DurableInboxEngine(store, member,
                new NativeCarrier(query, default(OccurrenceId), default(OccurrenceId), null, null))
                .TryResumeSuspended(true, typeof(UIStateNothingSelected));
        }

        internal static void ConfirmDurableNativeOpen(GeoscapeViewSwitchQuery query)
        {
            if (_durableEnginePresentation || _durableRestoreActive) return;
            OccurrenceId occurrence; var request = WindowOrder.CurrentRequest(query);
            if (!WindowOrder.TryGetDurable(request, out occurrence)) return;
            var store = DurableInboxSaveBridge.ActiveStore; MembershipId member;
            if (store == null || !TryLocalMember(store, out member)) return;
            EnsureDurableCarrierSession(store);
            new DurableInboxEngine(store, member,
                new NativeCarrier(query, default(OccurrenceId), default(OccurrenceId), null, null))
                .ConfirmNativePresented(occurrence);
        }

        private static bool TryLocalMember(DurableInboxStore store, out MembershipId member)
        {
            string local = Multiplayer.Network.ClientIdentity.PlayerGuid.ToString("D");
            var found = store.Ledger.Members.Keys.Where(x => string.Equals(x.PlayerGuid, local,
                StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Epoch).ToArray();
            member = found.Length == 0 ? default(MembershipId) : found[0]; return found.Length != 0;
        }

        private static void MarkDurableDismissed(GeoscapeView view)
        {
            if (_durableSilentExit) return;
            var query = AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery")?.GetValue(view) as GeoscapeViewSwitchQuery;
            OccurrenceId occurrence; var current = WindowOrder.CurrentRequest(query);
            if (!WindowOrder.TryGetDurable(current, out occurrence)) return;
            var store = DurableInboxSaveBridge.ActiveStore; MembershipId member;
            if (store == null || !TryLocalMember(store, out member)) return;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var expected = store.Ledger; InboxEntry entry;
                try { entry = expected.Get(occurrence, member); } catch { return; }
                if (entry.Lifecycle == InboxLifecycle.Dismissed || entry.Lifecycle == InboxLifecycle.Removed) return;
                var next = expected.Replace(entry.WithLifecycle(InboxLifecycle.Dismissed,
                    checked(entry.LifecycleRevision + 1))).WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
                if (store.Commit(expected, next)) return;
            }
        }

        private sealed class NativeCarrier : IDurableWindowCarrierAdapter
        {
            private readonly GeoscapeViewSwitchQuery _query; private readonly OccurrenceId _ordinary, _priority;
            private readonly GeoscapeViewStateSwitchRequest _current, _pending;
            internal NativeCarrier(GeoscapeViewSwitchQuery query, OccurrenceId ordinary, OccurrenceId priority,
                GeoscapeViewStateSwitchRequest current, GeoscapeViewStateSwitchRequest pending)
            { _query = query; _ordinary = ordinary; _priority = priority; _current = current; _pending = pending; }
            public InboxWindowCheckpoint Capture(OccurrenceId occurrence)
            {
                if (!occurrence.Equals(_ordinary)) return null;
                var view = GenericApplier.StartedGeoLevel()?.View;
                if (_current.State is UIStateGeoscapeEvent) return EventPopup.CaptureCheckpoint(_current.State as GeoscapeViewState, view);
                if (_current.State is UIStateGeoModal modal)
                    return GeoModalMirror.CaptureCheckpoint(modal);
                return null;
            }
            public bool Present(OccurrenceId occurrence)
            {
                if (!occurrence.Equals(_priority)) return false;
                var view = GenericApplier.StartedGeoLevel()?.View; if (view == null) return false;
                _durableSilentExit = true;
                try { view.FinishQueriedState(); } finally { _durableSilentExit = false; }
                if (ReferenceEquals(WindowOrder.CurrentRequest(_query), _current)) return false;
                lock (_durableCarrierGate) _durableSuspended[_ordinary] = _current;
                var pending = WindowOrder.RequestsField?.GetValue(_query) as IList<GeoscapeViewStateSwitchRequest>;
                if (pending == null || !pending.Contains(_pending)) return false;
                pending.Remove(_pending); pending.Insert(0, _pending);
                _durableEnginePresentation = true;
                try { _query.ProcessQueriedStateSwitch(); }
                finally { _durableEnginePresentation = false; }
                return ReferenceEquals(WindowOrder.CurrentRequest(_query), _pending);
            }
            public bool Restore(OccurrenceId occurrence, InboxWindowCheckpoint checkpoint)
            {
                if (occurrence.Equals(_ordinary) && AlreadyCurrent(_query, _current, occurrence)) return true;
                GeoscapeViewStateSwitchRequest request;
                lock (_durableCarrierGate) _durableSuspended.TryGetValue(occurrence, out request);
                if (request == null)
                    request = EventPopup.ReconstructCarrier(occurrence, checkpoint) ??
                              GeoModalMirror.ReconstructCarrier(occurrence, checkpoint);
                if (request == null) return false;
                var view = GenericApplier.StartedGeoLevel()?.View; if (view == null) return false;
                _durableRestoreActive = true;
                try { _query.QueryStateSwitch(request); _query.ProcessQueriedStateSwitch(); }
                finally { _durableRestoreActive = false; }
                bool restored = request.State is UIStateGeoscapeEvent
                    ? EventPopup.RestoreSuspended(request, view, checkpoint)
                    : request.State is UIStateGeoModal modal && GeoModalMirror.RestoreSuspended(checkpoint, () =>
                        ReferenceEquals(WindowOrder.CurrentRequest(_query), request) &&
                        checkpoint.ContentPhase == "modal" && checkpoint.Selection == ((int)modal.ModalType).ToString() &&
                        ReferenceEquals((WindowOrder.CurrentRequest(_query)?.State as UIStateGeoModal)?.ModalData,
                            modal.ModalData));
                return restored;
            }
            public void Abandon(OccurrenceId occurrence)
            {
                var view = GenericApplier.StartedGeoLevel()?.View; if (view == null) return;
                OccurrenceId currentOccurrence;
                if (!WindowOrder.TryGetDurable(WindowOrder.CurrentRequest(_query), out currentOccurrence) ||
                    !currentOccurrence.Equals(occurrence)) return;
                _durableSilentExit = true; try { view.FinishQueriedState(); } finally { _durableSilentExit = false; }
            }
            public void FinalizeRestore(OccurrenceId occurrence)
            { lock (_durableCarrierGate) _durableSuspended.Remove(occurrence); }
        }

        [HarmonyPatch(typeof(GeoLevelController), "OnLevelEnd")]
        private static class DurableCarrierLevelTeardownPatch
        { private static void Prefix() => ClearDurableRuntimeCarriers(); }

        [HarmonyPatch(typeof(UIStateGeoscapeEvent), "ExitState")]
        private static class DurableEventSilentExitPatch
        {
            private static void Prefix(UIStateGeoscapeEvent __instance, ref GeoscapeEventRecordState? __state)
            { __state = BeginSilentEventExit(__instance?.Event?.Record, _durableSilentExit); }
            private static Exception Finalizer(UIStateGeoscapeEvent __instance,
                GeoscapeEventRecordState? __state, Exception __exception)
            {
                EndSilentEventExit(__instance?.Event?.Record, __state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(UIStateGeoModal), "ExitState")]
        private static class DurableModalSilentExitPatch
        {
            private static void Prefix(UIStateGeoModal __instance, ref bool? __state)
            { __state = BeginSilentModalExit(__instance, _durableSilentExit); }
            private static Exception Finalizer(UIStateGeoModal __instance, bool? __state, Exception __exception)
            { EndSilentModalExit(__instance, __state); return __exception; }
        }
        internal const byte OpAdvance = 1;  // [identity:string][result:u8]

        /// <summary>THE SECOND ANSWER SHAPE — a window whose resolution is not "which button" but "which
        /// object": [identity:string][siteRef:string]. It exists because <c>UIStateAssetDeployment</c> gained a
        /// mirrored copy (0xB7's non-modal arm) and its ONLY exit is <c>DeployAtSite(GeoSite)</c>:69, which
        /// calls <c>GeoPhoenixFaction.DeployAsset</c> — host-authoritative, so a client's click may not run it
        /// (law 3). Not folded into <see cref="OpAdvance"/>: that op's whole body is a <c>ModalResult</c> and a
        /// window that answers with an ENTITY has nothing to put in it.</summary>
        internal const byte OpDeploy = 2;   // [identity:string][siteRef:string]

        /// <summary>No answer recorded. A LOCAL sentinel for <see cref="_pendingResult"/> only — it never
        /// rides the wire, because every window this family can name is a modal and therefore always carries
        /// a real answer. Not a <c>ModalResult</c> value: the enum has exactly Confirm/Cancel/Close
        /// (ModalResult.cs).</summary>
        internal const byte ResultNone = 255;

        // ONE bind of each, shared with PauseHold.IsCurrentQueuedWindow (all that class still is since the
        // 2026-08-04 pause rework — no hold set, no arbiter; it asks these same two fields only for "is this
        // screen a queued window") — a second AccessTools.Field pair is a second thing to drift when the game
        // renames one.
        internal static readonly FieldInfo SwitchQueryField =
            AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");                       // GeoscapeView.cs:138 (game typo)
        internal static readonly FieldInfo CurrentRequestField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_currentStateSwitchRequest"); // :17

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.GeoWindowIntent, "window",
                new Dictionary<byte, IntentRail.OpHandler>
                {
                    [OpAdvance] = HandleAdvance,
                    [OpDeploy] = HandleDeploy,
                });
        }

        // ─── WINDOW IDENTITY (pure; RailCheck L82 executes it) ─────────────

        /// <summary>
        /// The identity of a window BOTH peers provably hold, or null when this peer must not name it at all
        /// — which is the answer for everything a peer opened on its own, and therefore for almost every
        /// window closed in a session.
        ///
        /// THE ONLY WINDOWS A PEER MAY ANSWER FOR ANOTHER ARE THE ONES THE HOST RAISED TO IT. That is the
        /// Mirrored modal family and nothing else: the copy on this peer was built by
        /// <see cref="GeoModalMirror"/> out of the host's own 0xB7 payload, so "we are looking at the same
        /// window" is a fact on the wire rather than a coincidence of types. The identity therefore IS that
        /// payload — <see cref="GeoModalMirror.Describe"/> run over the live <c>ModalData</c>, which on the
        /// host describes its ORIGINAL object and on the client re-describes what
        /// <c>GeoModalMirror.BuildData</c> rebuilt from those very fields, yielding the same string on both
        /// sides for the same window and a different one for every other. <c>Raise.Num</c> is deliberately
        /// excluded: it is the presentation flag <c>SwitchToResearchState</c>, which the client's rebuild
        /// forces to false on purpose (GeoModalMirror.cs:363) and which names nothing.
        ///
        /// Two independent gates, because they refuse different things: the coverage rule refuses a window
        /// that is not SHARED (an ability confirmation, a soldier-edit picker, a haven infiltration brief —
        /// all LocalOnly, all opened by the clicking peer itself), and <c>Unsupported</c> refuses one whose
        /// data 0xB7 cannot describe and therefore never shipped (a mission brief's live <c>GeoMission</c>).
        ///
        /// A window with no identity is neither sent nor advanced. The blanket "is not a modal" arm that
        /// `81afe12` gave cutscenes and asset deployment stays DEAD and must not come back — it named a KIND,
        /// so it dismissed every peer's own tutorial and replenish screen too. What replaced it for asset
        /// deployment is the opposite property, and only for that one state: the host now RAISES it (0xB7's
        /// non-modal arm), so the peer's copy was built from the host's payload and re-describing the live
        /// bind names the INSTANCE. What it also drops is <c>UIStateGeoscapeEvent</c>, which needed no arm
        /// here at all: an event answer already crosses as the 0xB4 intent keyed on the event's own
        /// <c>EventID</c> (<see cref="EventSync"/>), so all this op ever added there was the wrong-instance
        /// dismissal that made it a regression.
        /// </summary>
        internal static string IdentityOf(object state)
        {
            switch (state)
            {
                case UIStateGeoModal modal:
                    if (GeoWindowCoverage.RuleForModal(modal.ModalType)?.Sync != WindowSync.Mirrored) return null;
                    return Identity(modal.ModalType.ToString(), GeoModalMirror.Describe(modal.ModalData));
                // The SAME argument, one state further: since 2026-08-05 the host raises this one to every
                // peer through 0xB7's non-modal arm, so a peer's copy really was built out of the host's own
                // payload and re-describing the live bind yields the same string on both sides. That is the
                // property this method has always required — it is not "non-modal windows are nameable now".
                case UIStateAssetDeployment deploy:
                    if (GeoWindowCoverage.RuleFor(typeof(UIStateAssetDeployment))?.Sync != WindowSync.Mirrored)
                        return null;
                    return Identity("AssetDeployment", GeoModalMirror.Describe(deploy.DeployBind));
                default:
                    return null;                                               // this peer's own local window
            }
        }

        /// <summary>The identity string itself: the window KIND plus the 0xB7 payload that built it, minus
        /// <c>Num</c> — that field is a presentation flag the client's rebuild deliberately overrides
        /// (SwitchToResearchState) or a pair of display bits, and it names nothing.</summary>
        private static string Identity(string kind, GeoModalMirror.Raise p) =>
            p.Shape == GeoModalMirror.DataShape.Unsupported ? null   // never rode the 0xB7 raise
                : kind + "|" + p.Shape + "|" + p.Ref + "|" + string.Join(",", p.Keys ?? new string[0]);

        // ─── THE VALIDATOR (pure — host facts only, law 3; RailCheck L82 executes it) ───

        /// <summary>May this intent advance the host's queue? null = yes, otherwise the human reason it did
        /// not. PURE so the arbitration is falsifiable headless: an identity check that only ever runs in a
        /// live session is exactly how a peer ends up answering a window it never saw — which is not a
        /// hypothetical, it is what this family did for one build on 2026-08-01.
        /// <paramref name="haveIdentity"/> is null/empty when the host's current window has no shared
        /// identity: no window up, or a window of the host's own that no peer ever saw.</summary>
        internal static string ValidateIdentity(string haveIdentity, string wantIdentity)
        {
            if (string.IsNullOrEmpty(wantIdentity))
                return "the answer names no window — only a host-RAISED mirrored window has an identity both " +
                       "peers can agree on, and nothing without one may advance anybody's queue";
            if (string.IsNullOrEmpty(haveIdentity))
                return "the host is not on a window any peer shares — either its queue is not blocked at all, " +
                       "or what it holds is its OWN window, which nobody else saw and nobody else may dismiss";
            if (!string.Equals(haveIdentity, wantIdentity, StringComparison.Ordinal))
                return "the host holds '" + haveIdentity + "' but the answer names '" + wantIdentity +
                       "' — a different window INSTANCE, so advancing would dismiss something nobody answered";
            return null;
        }

        /// <summary>PURE (RailCheck L176). May the host answer a window that is still IN THE QUEUE rather
        /// than on screen? The current-window gate above cannot: <see cref="ValidateIdentity"/> is asked of
        /// <c>_currentStateSwitchRequest</c>, and a window that was never ENTERED is not it.
        ///
        /// WHY THAT STOPPED BEING AN EDGE CASE. <c>19af84c</c> gave this repo <c>WindowOrder
        /// .HoldsForOpenScreen</c>, which deliberately keeps a raised window in <c>_viewStateSwitchRequests</c>
        /// while the local player is inside a screen he opened. On a HOST inside the research tree that makes
        /// "queued, not current" the NORMAL state of every mirrored window — and the 2026-08-07 report is the
        /// consequence: a client answered the ambush brief, the host's own copy sat held, nothing advanced it,
        /// so <c>UIStateGeoModal.FinishDialog</c> → <c>ModalResultCallback</c>:799 → <c>LaunchMission</c>:1043
        /// never ran there and the host never got a deployment screen AT ALL. The hole predates the hold (a
        /// host with two windows queued always had one that was not current); the hold made it the rule.
        ///
        /// THE ANSWER IS NOT <c>FinishDialog</c>. That method's first act is <c>Context.View
        /// .FinishQueriedState()</c> → <c>FinishCurrentStateSwitch</c>:116, which nulls the current slot and
        /// runs <c>_statesStack.SwitchToPreviousState()</c> — on a state that was never PUSHED, so it would
        /// pop the research screen the player is actually looking at. A queued window is answered by taking it
        /// OUT of the list and invoking the host's own <c>_dialogHandler</c>, and by nothing else.</summary>
        internal static bool MayAnswerQueued(string haveCurrentIdentity, string queuedIdentity, string wantIdentity)
            => !string.IsNullOrEmpty(wantIdentity) &&
               string.IsNullOrEmpty(ValidateIdentity(queuedIdentity, wantIdentity)) &&
               // The current window always wins: if it is the one being named, FinishDialog is the right
               // funnel and this arm must not race it with a half-answer that skips FinishQueriedState.
               !string.Equals(haveCurrentIdentity, wantIdentity, StringComparison.Ordinal);

        /// <summary><see cref="ValidateIdentity"/> plus the one question only a MODAL answer raises. Split so
        /// the deploy op can ask the identity half without inventing a <c>ModalResult</c> it does not have.</summary>
        internal static string Validate(string haveIdentity, string wantIdentity, byte result)
        {
            string why = ValidateIdentity(haveIdentity, wantIdentity);
            if (why != null) return why;
            if (result > (byte)ModalResult.Close)
                return "a modal is a DECISION window and the answer carries none (result=" + result + ") — " +
                       "its DialogCallback would run against an undefined ModalResult";
            return null;
        }

        // ─── HOST: advance through the game's own funnel ───────────────────

        private static void HandleAdvance(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string wantIdentity = r.ReadString();
            byte result = r.ReadByte();

            var view = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View;
            var query = SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
            if (view == null || query == null)
            {
                // Not a reject: the host is mid-load or in a battle, nothing on either peer ran ahead.
                Debug.Log("[MP][windows] advance of '" + wantIdentity + "' from peer=" + senderPeerId +
                          " ignored — this host has no live geoscape window queue right now");
                return;
            }

            var current = CurrentRequestField?.GetValue(query) as GeoscapeViewStateSwitchRequest;
            var modal = current?.State as UIStateGeoModal;
            string haveIdentity = IdentityOf(modal);

            string why = Validate(haveIdentity, wantIdentity, result);
            if (why != null && AnswerQueued(query, haveIdentity, wantIdentity, result, senderPeerId, nonce)) return;
            if (why != null)
            {
                // LOGGED, never rejected — see the class doc. A reject's forced re-emit exists to pull back a
                // client whose screen ran ahead of host state; a peer closing its own window ran ahead of
                // nothing, and rejecting the ordinary case would fire a full-graph resend at click rate.
                Debug.Log("[MP][windows] advance from peer=" + senderPeerId + " nonce=" + nonce +
                          " did NOT apply — " + why);
                return;
            }

            // The host's OWN DialogCallback runs here, over the host's OWN modalData: FinishDialog:82 clears
            // the queue slot and then invokes it — native, host-side, off host objects, on the very window
            // the host itself raised to the answering peer. The client contributed one byte.
            modal.FinishDialog((ModalResult)result);
            Debug.Log("[MP][windows] HOST advanced '" + haveIdentity + "' with " + (ModalResult)result +
                      " for peer=" + senderPeerId + " nonce=" + nonce);
        }

        /// <summary>The host's own <c>DialogCallback</c>, reached through <c>UIStateGeoModal</c>'s own field
        /// rather than through <c>FinishDialog</c> — see <see cref="MayAnswerQueued"/> for why that method
        /// cannot be used on a window the state stack never pushed.</summary>
        private static readonly FieldInfo DialogHandlerField =
            AccessTools.Field(typeof(UIStateGeoModal), "_dialogHandler");   // UIStateGeoModal.cs:42

        /// <summary>
        /// THE HELD WINDOW'S ANSWER. Returns true when this intent was consumed here, so the caller's
        /// "did NOT apply" line stays for the cases that really did not.
        ///
        /// Three things happen, in this order and no other: the request is REMOVED from the game's own
        /// pending list (an answered window must not be served to this peer later — it would offer a
        /// decision that has already been taken for everyone), the handler reference is cleared exactly as
        /// <c>FinishDialog</c>:83-85 clears it, and only then is the host's OWN callback invoked over the
        /// host's OWN <c>modalData</c>. Clearing before invoking is deliberate: the callback runs
        /// <c>LaunchMission</c>, which queues a NEW request into the very list being edited.
        ///
        /// It touches NOTHING that is on screen. No <c>FinishQueriedState</c>, no
        /// <c>SwitchToPreviousState</c>, no <c>_currentStateSwitchRequest</c> write — the peer keeps the
        /// screen he is in, and whatever the callback queues lands in the queue behind
        /// <c>WindowOrder.HoldsForOpenScreen</c> like every other window. That is the whole point: the
        /// deployment screen ARRIVES, in the history, instead of never arriving at all.
        /// </summary>
        private static bool AnswerQueued(GeoscapeViewSwitchQuery query, string haveIdentity, string wantIdentity,
                                         byte result, ulong senderPeerId, uint nonce)
        {
            if (result > (byte)ModalResult.Close) return false;      // Validate's other half, unchanged
            if (query == null || WindowOrder.RequestsField == null || DialogHandlerField == null) return false;
            if (!(WindowOrder.RequestsField.GetValue(query) is IList<GeoscapeViewStateSwitchRequest> pending))
                return false;

            for (int i = 0; i < pending.Count; i++)
            {
                var modal = pending[i].State as UIStateGeoModal;
                if (modal == null) continue;
                if (!MayAnswerQueued(haveIdentity, IdentityOf(modal), wantIdentity)) continue;

                pending.RemoveAt(i);
                var cb = DialogHandlerField.GetValue(modal) as DialogCallback;
                DialogHandlerField.SetValue(modal, null);
                Debug.Log("[MP][windows] HOST advanced QUEUED '" + wantIdentity + "' with " + (ModalResult)result +
                          " for peer=" + senderPeerId + " nonce=" + nonce + " — this peer is inside a screen of " +
                          "its own, so the window it was raised was still in _viewStateSwitchRequests and never " +
                          "became _currentStateSwitchRequest. Its own DialogCallback runs here; nothing on " +
                          "screen is popped" + (cb == null ? " (no callback: a mirrored copy carries none)" : ""));
                try { cb?.Invoke((ModalResult)result); }
                catch (Exception ex)
                {
                    // NEVER silent, and never fatal to the intent pump: the window is already out of the
                    // queue, so a throw here loses the CONSEQUENCE of the answer, not the answer.
                    Debug.LogError("[MP][windows] the host's own callback for '" + wantIdentity + "' threw — " +
                                   "the window is answered and gone, but whatever it was going to do (a mission " +
                                   "brief's LaunchMission, a reward's Apply) did NOT happen: " + ex);
                }
                return true;
            }
            return false;
        }

        /// <summary>The asset-deployment answer. Same identity gate as <see cref="HandleAdvance"/>, then the
        /// host runs the GAME'S OWN <c>UIStateAssetDeployment.DeployAtSite</c>:69 over its OWN prompt — which
        /// is <c>GeoPhoenixFaction.DeployAsset</c> plus <c>FinishQueriedState</c>, exactly what a host click
        /// does. The client contributed two strings. The site is re-resolved on the host's graph and checked
        /// against the prompt's OWN <c>DeploySites</c>, because a wire-named site is a client-supplied address
        /// and the game's own list is the only definition of where this asset may go.</summary>
        private static void HandleDeploy(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string wantIdentity = r.ReadString();
            string siteRef = r.ReadString();

            var geo = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>();
            var query = SwitchQueryField?.GetValue(geo?.View) as GeoscapeViewSwitchQuery;
            if (geo == null || query == null)
            {
                Debug.Log("[MP][windows] deploy of '" + wantIdentity + "' from peer=" + senderPeerId +
                          " ignored — this host has no live geoscape window queue right now");
                return;
            }

            var current = CurrentRequestField?.GetValue(query) as GeoscapeViewStateSwitchRequest;
            var deploy = current?.State as UIStateAssetDeployment;
            string haveIdentity = IdentityOf(deploy);

            string why = ValidateIdentity(haveIdentity, wantIdentity);
            if (why != null)
            {
                Debug.Log("[MP][windows] deploy from peer=" + senderPeerId + " nonce=" + nonce +
                          " did NOT apply — " + why);
                return;
            }

            var site = IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
            if (site == null || !deploy.DeploySites.Contains(site))
            {
                // LOUD, not a quiet return: the answering peer's own copy is already closed, so a swallow here
                // leaves the asset undeployed AND the host's prompt up — the exact wedge this arm ends.
                Debug.LogError("[MP][windows] deploy from peer=" + senderPeerId + " NOT applied — '" + siteRef +
                               "' " + (site == null ? "resolves to no site on this host"
                                                    : "is not one of the sites this prompt offers") +
                               ". The host's asset-deployment prompt stays up and the asset is undeployed");
                return;
            }

            deploy.DeployAtSite(site);
            Debug.Log("[MP][windows] HOST deployed '" + haveIdentity + "' at " + siteRef +
                      " for peer=" + senderPeerId + " nonce=" + nonce);
        }

        // ─── CLIENT: the capture seam (law 4a, presentation) ───────────────

        /// <summary>The answer <see cref="FinishDialogAnswer"/> saw one call frame ago. <c>FinishDialog</c>:82
        /// calls <c>FinishQueriedState</c> BEFORE it invokes the handler, so the result is only observable
        /// from a prefix on the modal and has to be carried the one frame down to the send site. Cleared by
        /// the send, so a later non-modal dismissal can never inherit somebody's Confirm.</summary>
        private static byte _pendingResult = ResultNone;

        /// <summary>Once per ModalType: a brief is answered at click rate all game long.</summary>
        private static readonly HashSet<string> _perPeerLogged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Called from the client's own <c>FinishQueriedState</c>, with the state it is closing.
        /// Non-blocking on purpose: closing this peer's own window is PRESENTATION and must happen locally
        /// whatever the host does with the intent (the block-first law governs STATE mutations, and this
        /// mutates none). What crosses is the answer.</summary>
        private static void SendAdvance(GeoscapeView view)
        {
            byte result = _pendingResult;
            _pendingResult = ResultNone;
            // ShouldRunNative is the ONE peer decision every capture seam asks (law: block-first). Here its
            // FALSE arm means only "this is a client outside an apply" — nothing is blocked.
            if (IntentRail.ShouldRunNative()) return;
            try
            {
                var query = SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
                var state = (CurrentRequestField?.GetValue(query) as GeoscapeViewStateSwitchRequest)?.State;
                // OpAdvance IS the modal answer — its whole body is a ModalResult. IdentityOf now also names
                // the asset-deployment prompt (for OpDeploy), and that window closes through this same
                // FinishQueriedState, so without this line every deploy would send a second, answerless
                // message the host could only log as a mismatch.
                if (!(state is UIStateGeoModal modal)) return;
                // THE PER-PEER ANSWER CLASS NEVER CROSSES. A mission brief / outcome is answered by each peer
                // for itself (GeoWindowCoverage.IsPerPeerAnswer): this peer's copy carries the game's OWN
                // callback now, so the answer already ran locally, and sending it on would make the HOST run
                // ModalResultCallback a second time for somebody else's click — whose Cancel arm is
                // GeoMission.Cancel:253, the very thing that deleted the shared mission when one player
                // declined. Declining is "I am busy", never "cancelled for everyone".
                if (GeoWindowCoverage.IsPerPeerAnswer(modal.ModalType, modal.ModalData))
                {
                    if (_perPeerLogged.Add(modal.ModalType.ToString()))
                        Debug.Log("[MP][windows] '" + modal.ModalType + "' answered LOCALLY — no 0x" +
                                  SurfaceIds.GeoWindowIntent.ToString("X2") +
                                  " advance crosses for a mission brief/outcome, because every peer answers " +
                                  "this window for itself and one peer's decline must not cancel the mission " +
                                  "for the others (logged once per ModalType)");
                    return;
                }
                string identity = IdentityOf(state);
                // THE ORDINARY CASE, and the whole of the 2026-08-01 fix: this peer closed a window of its
                // OWN — its event picker, its tutorial, its replenish screen, its ability prompt — or the
                // slot was already cleared by a re-entrant ExitState (UIStateAssetDeployment:66,
                // UIStateReplenish:69). Nothing crosses, and silently: it happens at click rate all game long.
                if (identity == null) return;
                // Esc / the close button reach FinishQueriedState through OnCancel:96 WITHOUT going through
                // FinishDialog, so no answer was recorded. That is not "no answer" — the game's own tail
                // resolves exactly that case as Close (ExitState:121-124), and sending anything else would
                // make a dismissal mean something the local click did not.
                if (result == ResultNone) result = (byte)ModalResult.Close;
                IntentRail.Send(SurfaceIds.GeoWindowIntent, OpAdvance,
                    "advance " + identity + " result=" + result,
                    w => { w.Write(identity); w.Write(result); });
            }
            catch (Exception ex)
            {
                // The host's copy of this window stays up and its clock stays paused for everyone — the exact
                // failure this family exists to end, so it is never swallowed.
                Debug.LogError("[MP][windows] CLIENT advance capture failed — the host's window queue was NOT " +
                               "advanced and stays blocked: " + ex);
            }
        }

        /// <summary>Half one of the client seam: the ANSWER. A prefix, because <c>FinishDialog</c>:82-86
        /// calls <c>FinishQueriedState</c> before it does anything else, so by the time the send site runs
        /// the result is gone. Records only — the native dialog is untouched.</summary>
        [HarmonyPatch(typeof(UIStateGeoModal), nameof(UIStateGeoModal.FinishDialog))]
        internal static class FinishDialogAnswer
        {
            private static void Prefix(ModalResult res) => _pendingResult = (byte)res;
        }

        /// <summary>Half two: the SEND, at the mirror image of the host's own chokepoint —
        /// <c>GeoscapeView.FinishQueriedState</c>:2164. A PREFIX, because the queue slot this reads is
        /// cleared by the very call it wraps (<c>FinishCurrentStateSwitch</c>:118), so a postfix would have
        /// nothing left to name. It sees every window kind and <see cref="IdentityOf"/> decides which of them
        /// may cross — a mirrored modal answered by a button or closed by Esc (<c>OnCancel</c>:96) does; a
        /// cutscene skipped (:92), a replenish screen finished (:64), an event picker answered and a tutorial
        /// stepped through do NOT, because they are this peer's own. Never blocks — it always returns
        /// true.</summary>
        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.FinishQueriedState))]
        internal static class FinishQueriedStateCapture
        {
            private static void Prefix(GeoscapeView __instance)
            { MarkDurableDismissed(__instance); SendAdvance(__instance); }
        }

        /// <summary>Half three, and the ONLY blocking seam in this family: the asset-deployment prompt's one
        /// exit. <c>DeployAtSite</c>:69 calls <c>GeoPhoenixFaction.DeployAsset</c> — a soldier joining a base,
        /// a vehicle being created — which is authoritative, so on a client it is BLOCKED (law 3, block-first)
        /// and becomes the 0xB9 deploy intent. This peer's own copy is then closed through the game's own
        /// <c>FinishQueriedState</c>, exactly as the native tail at :79 does: closing one's own window is
        /// presentation and must happen whatever the host makes of the intent. Host and solo run native.</summary>
        [HarmonyPatch(typeof(UIStateAssetDeployment), nameof(UIStateAssetDeployment.DeployAtSite))]
        internal static class DeployAtSiteCapture
        {
            private static bool Prefix(UIStateAssetDeployment __instance, GeoSite site)
            {
                if (IntentRail.ShouldRunNative()) return true;
                try
                {
                    string identity = IdentityOf(__instance);
                    string siteRef = IdentityResolver.RootRef(site);
                    if (identity == null || string.IsNullOrEmpty(siteRef))
                    {
                        // Never fall through to native: that would deploy on this peer alone. The prompt stays
                        // up here so the player can retry, and the reason is on the log.
                        Debug.LogError("[MP][windows] asset-deploy click DROPPED — " +
                                       (identity == null ? "this peer's prompt has no shared identity, so the host " +
                                                           "could not tell which window is being answered"
                                                         : "the chosen site has no rail root ref") +
                                       ". Nothing was deployed locally either");
                        return false;
                    }
                    IntentRail.Send(SurfaceIds.GeoWindowIntent, OpDeploy,
                        "deploy " + identity + " at " + siteRef,
                        w => { w.Write(identity); w.Write(siteRef); });
                    GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View?.FinishQueriedState();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[MP][windows] asset-deploy capture failed — the asset was NOT deployed on any " +
                                   "peer and this prompt stays open: " + ex);
                }
                return false;
            }
        }
    }

    /// <summary>
    /// A RESTORED WINDOW THIS PEER DID NOT PRODUCE, OR WHOSE SUBJECT HAS RESOLVED, IS NOT RESTORED.
    ///
    /// THE SECOND HALF, ADDED 2026-08-06 (the campaign-intro duplicate). Every TFTV intro popup
    /// (<c>IntroBetterGeo_0/1/2</c>) appeared on the host, was answered there, and then appeared AGAIN on
    /// every client. TWO PRODUCERS for one window, and the second one was THIS METHOD. Write-order, measured:
    /// the host raised the three events at 20:36:39.885-.887 (<c>EventPopup</c>:1258 → :196, surface 0xB6) and
    /// the new-campaign autosave ran 14 ms later at 20:36:39.899 (<c>SaveTransferCoordinator</c>:1137 →
    /// :1194), while the host answered them only at 20:36:53 — so the transferred blob carried three QUEUED
    /// <c>UIStateGeoscapeEvent</c>s (the window queue is part of the save: <c>GetRestorableData</c>:25 ←
    /// <c>GeoLevelController.RecordInstanceData</c>:415), and the same client also received the three as held
    /// 0xB6 raises, replayed at reveal (20:36:57.309-.444). Six teardowns against three raises per client, and
    /// <c>multiplayer.log</c>:751 caught it: a <c>UIStateGeoscapeEvent</c> for <c>IntroBetterGeo_0</c> torn
    /// down 146 ms BEFORE the first mod raise, which on a client is only reachable from
    /// <c>UIStateGeoscapeEvent.ExitState</c> (<c>EventSync</c>:191).
    ///
    /// WHY THE EXISTING RULES ALL MISSED IT, stated because three of them were green through the whole bug:
    /// L49 ("no ModalType a native raiser opens may also be Mirrored") is <c>ModalType</c>/0xB7-scoped and
    /// derives producers from the GAME's IL raisers, and the second producer here is the SAVEGAME RESTORE;
    /// L117 (the subject half below) examines only entries carrying a <c>GeoMission</c>, and an event-carrying
    /// entry has none; L93 arms F/H assert the carry EXISTS without ever asserting a COUNT.
    ///
    /// So the rule widens from "subject resolved" to "THIS PEER IS NOT THE PRODUCER", keyed on the coverage
    /// table that already names every window's producer — see <see cref="KindIsMirrored(Type, ModalType)"/>
    /// for the verdict and <see cref="RestoringAnotherPeersBlob"/> for the signal. RailCheck L135 pins it.
    ///
    /// THE FIRST HALF, unchanged:
    ///
    /// THE REPORT (2026-08-05). Coming out of a tactical mission, the whole pre-deployment window history
    /// came back correctly — including the START-MISSION window for the mission that had just been played.
    /// The player had already flown that mission; being asked to launch it again is an offer on a subject
    /// that no longer exists.
    ///
    /// WHY IT CAME BACK. A mission brief is a <c>UIStateGeoModal</c> opened through
    /// <c>GeoscapeView.OpenModalPersistent</c>:848, and <c>Persistent</c> is exactly what puts it in the
    /// save: <c>GenerateContext</c>:129-136 hands back a <c>RestoreContext(_modal, _modalData)</c>, which
    /// <c>GeoscapeViewSwitchQuery.GetRestorableData</c>:25-37 writes out and <c>RestoreData</c>:39-56 rebuilds
    /// by calling <c>RegenerateState</c>:30-40 on each entry. That rebuild tests exactly one thing —
    /// <c>_modalData == null</c> — and a completed <c>GeoMission</c> is not null, so the brief was rebuilt
    /// unconditionally.
    ///
    /// SO THE GAME ALREADY HAS THIS RULE AND IT IS SIMPLY TOO NARROW. "A restored window whose subject no
    /// longer EXISTS is not restored" is <c>RegenerateState</c>'s own null test, and <c>RestoreData</c>:46-50
    /// already knows how to skip such an entry. This widens "no longer exists" to "has RESOLVED", using the
    /// game's own verdict rather than a new one: <c>UIStateInitial.EnterState</c>:102 decides a mission is
    /// over with <c>IsCompleted || GetMissionOutcomeState() != TacFactionState.Playing</c>, and that is the
    /// predicate below, unchanged.
    ///
    /// GENERIC BY SUBJECT, NOT BY <c>ModalType</c>. The filter reads the SUBJECT out of whatever context it
    /// is handed — any instance field holding a <c>GeoMission</c> — so it covers all five
    /// <c>IGeoscapeRestorableViewStateContext</c> implementors at once (<c>UIStateGeoModal</c>,
    /// <c>UIStateAssetDeployment</c>, <c>UIStateGeoscapeEvent</c>, <c>UIStateMarketplaceGeoscapeEvent</c>,
    /// <c>UIStateBaseGeoscapeEvent</c>) and every one of the eleven brief <c>ModalType</c>s
    /// <c>GetMissionBriefModal</c>:1724-1798 can return. A blacklist of one enum member would have covered
    /// the reported window and left its ten siblings — and every future one — restoring dead offers.
    ///
    /// THE REWARD WINDOW IS UNAFFECTED, AND NOT BECAUSE IT IS EXEMPTED. It is never in the restored set at
    /// all: the post-mission outcome modal is raised FRESH on the way back in, by
    /// <c>UIStateInitial.EnterState</c>:112 <c>OpenModalPersistent(GetMissionOutcomeModal(lastMission),
    /// lastMission, int.MaxValue)</c>, which runs AFTER <c>GeoscapeView.RestoreState</c> has already rebuilt
    /// the queue. So "drop the finished mission's windows" cannot reach it — it was not restored, it was
    /// just created. RailCheck L117 pins that, because it is the load-bearing half of this fix.
    ///
    /// OTHER WINDOWS OF THE SAME SHAPE, stated rather than assumed: the INTERCEPTION brief/outcome carry an
    /// <c>InterceptionInfoData</c> with live aircraft lists and NO <c>GeoMission</c>
    /// (<c>GeoWindowCoverage</c> declares them Gap), so this filter does not reach them and they restore as
    /// before; <c>HavenInfiltrateBrief</c> is declared LocalOnly and likewise carries no mission. A haven
    /// mission brief that DOES carry its <c>GeoMission</c> rides this filter for free — that is the point of
    /// keying on the subject.
    ///
    /// KNOWN AND DELIBERATE SCOPE EDGE: a CANCELLED mission is not caught. <c>GeoMission.Cancel</c>:253-265
    /// clears <c>Site.ActiveMission</c> but never sets <c>IsCompleted</c>, so the game's own predicate reads
    /// it as still Playing. Testing site-detachment instead would catch it and would also risk dropping a
    /// LIVE brief whose mission is not its site's active one — an un-grounded false drop, traded for a case
    /// that closes its own window on the way out anyway.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), nameof(GeoscapeViewSwitchQuery.RestoreData))]
    internal static class RestoreDropsResolvedSubjects
    {
        private static void Prefix(List<GeoscapeViewStateSwitchRestorableData> data)
        {
            if (data == null) return;
            int seen = data.Count, deadSubject = 0, notMine = 0;
            bool foreign = false;
            try
            {
                foreign = RestoringAnotherPeersBlob();
                for (int i = data.Count - 1; i >= 0; i--)
                {
                    var context = data[i].State;
                    var mission = SubjectMission(context);
                    if (mission != null && HasResolved(mission))
                    {
                        data.RemoveAt(i);
                        deadSubject++;
                        // Never silent: a window the player expected back and did not get must say why.
                        Debug.Log("[MP][windows] restore DROPS a stacked window whose mission '" +
                                  (mission.MissionDef == null ? "?" : mission.MissionDef.name) + "' has already " +
                                  "resolved (completed=" + mission.IsCompleted + ") — the offer it carried is dead. " +
                                  "The post-mission reward window is not affected: it is raised fresh by " +
                                  "UIStateInitial after this restore, not carried through it.");
                        continue;
                    }
                    if (!DropsRestoredWindow(foreign, KindIsMirrored(context))) continue;
                    data.RemoveAt(i);
                    notMine++;
                }
            }
            catch (Exception ex)
            {
                // A filter that throws must not cost the player their whole window history — the native
                // restore below still runs on the untouched remainder.
                Debug.LogError("[MP][windows] filtering the restored window queue failed — the queue is " +
                               "restored unfiltered: " + ex);
            }
            // ONE line per restore, always, drops or none: the duplicate this filter closes left ZERO log
            // lines for a whole session because RestoreData and RegenerateState are silent and the old
            // filter only spoke when it dropped. A restore that says how many entries it saw and how many
            // it kept is the difference between reading this bug off the log and re-deriving it from
            // timestamps.
            // NAME WHAT SURVIVED. The counts alone cost a session: on 2026-08-08 the host logged "1 entries
            // in the save, 1 kept" and nothing anywhere said WHICH of the five restorable kinds it was, so
            // the stale window the player was looking at could not be identified from the log at all — it
            // had to be re-derived from the coverage table. A kept entry names its state and its subject.
            string kept = "";
            try
            {
                for (int i = 0; i < data.Count; i++)
                {
                    var ctx = data[i].State;
                    var kind = ctx == null ? null : (ctx.GetType().DeclaringType ?? ctx.GetType());
                    var subject = SubjectMission(ctx);
                    kept += (i == 0 ? " Kept: " : ", ") + (kind == null ? "?" : kind.Name) +
                            (subject == null
                                ? ""
                                : "(mission '" + (subject.MissionDef == null ? "?" : subject.MissionDef.name) +
                                  "', completed=" + subject.IsCompleted + ")");
                }
                if (data.Count > 0) kept += ".";
            }
            catch (Exception ex) { kept = " Kept: <naming failed: " + ex.Message + ">"; }
            Debug.Log("[MP][windows] window-queue restore: " + seen + " entries in the save, " + data.Count +
                      " kept — " + deadSubject + " dropped (subject already resolved), " + notMine +
                      " dropped (Mirrored kind, produced by another peer" +
                      (foreign ? "" : "; not applicable — this peer authored this save") + ")." +
                      (notMine > 0
                          ? " Those " + notMine + " are NOT lost: this peer receives the same windows as its " +
                            "own live raises (0xB6/0xB7/0xBA) and re-carries its unanswered ones through " +
                            "EventPopup.RequeueUnanswered, which is why the two peers' KEPT COUNTS legitimately " +
                            "differ — the host holds one copy, this peer holds the other."
                          : "") + kept);
        }

        /// <summary>PURE (RailCheck L191). Does this restored entry go?
        ///
        /// THE COUNTS ARE MEANT TO DIFFER, and a session was spent reading that as a divergence. MEASURED,
        /// 2026-08-07, one boundary, one save: the host restored `3 entries … 3 kept` (host multiplayer.log
        /// :1382) and the client `3 entries … 0 kept — 3 dropped` (client :367). Both ended with THREE
        /// windows. The host raised <c>IntroBetterGeo_0/1/2</c> itself at 23:09:06.43 and the autosave 11 s
        /// later carried them, so the restored copy is its ONLY one; the client had already received all
        /// three as 0xB6 raises at 23:09:13.96, HELD them (no geoscape yet) and replayed them at
        /// 23:09:28.6-29.1 — so for it the restored copy is a SECOND one, and keeping it is the duplicate
        /// <c>0616e26</c> measured as six teardowns against three raises.
        ///
        /// SO NEITHER PEER IS WRONG, AND "MAKE THEM AGREE" IS THE REGRESSION. Making the host drop too loses
        /// windows only it holds — the same mistake as telling the host to wait for state it authored
        /// (<see cref="MissionSync.NoDeploymentReason"/>). Making the client keep them brings the duplicate
        /// straight back. The invariant is ONE LIVE COPY PER PEER, never equal restore counts, and the two
        /// halves that deliver it are asserted by L135 (this filter and
        /// <c>ReplenishSync.CarryUnreadWindowsPatch</c> read the SAME producer signal, and the deferral
        /// re-carry is reached). Extracted as a pure function so L191 can execute the role split itself: the
        /// call site read the two conditions inline, so deleting the producer test while keeping the call for
        /// the log line would have left L135 green.</summary>
        internal static bool DropsRestoredWindow(bool foreign, bool kindIsMirrored) => foreign && kindIsMirrored;

        /// <summary>
        /// IS THE GEOSCAPE BEING RESTORED SOMEBODY ELSE'S? A restored window is only ever a SECOND copy when
        /// this peer is not the one that produced it, so this is the whole producer test.
        ///
        /// THE SIGNAL IS THE ONE THE MOD ALREADY RELIES ON, deliberately not a new one:
        /// <c>ReplenishSync.CarryUnreadWindowsPatch</c>:146-160 states it and is built on it — "the geoscape's
        /// window queue has just been rebuilt from the save; on a CLIENT that save is the HOST's, so this
        /// peer's own unread windows are not in it" — behind exactly this gate
        /// (<c>engine == null || !engine.IsActiveSession || engine.IsHost</c> → return). Every load boundary a
        /// client reaches in a session (join, new campaign, mission return) is a native save TRANSFER (law 1),
        /// so its restored queue is the host's queue, never its own.
        ///
        /// USING THE IDENTICAL PREDICATE IS THE POINT, not a coincidence: the peer that DROPS the restored
        /// mirrored windows here is exactly the peer that RE-CARRIES its own unanswered ones there. One peer
        /// cannot lose its deferred windows to this drop without the other half of the same predicate having
        /// failed too, and RailCheck L135 asserts that complementarity rather than trusting it.
        ///
        /// Host: restores its OWN blob — its queue really did persist — so nothing is dropped and its own
        /// deferral is the native save's, untouched. Solo: no session, no producer question, vanilla.
        /// </summary>
        private static bool RestoringAnotherPeersBlob()
        {
            var engine = NetworkEngine.Instance;
            return engine != null && engine.IsActiveSession && !engine.IsHost;
        }

        /// <summary>The RestoreContext's own declaring type IS the window kind. All four
        /// <c>IGeoscapeRestorableViewStateContext</c> implementors the game ships are a private nested
        /// <c>RestoreContext</c> inside the view state they rebuild (<c>UIStateGeoModal</c>:14,
        /// <c>UIStateGeoscapeEvent</c>:16, <c>UIStateMarketplaceGeoscapeEvent</c>:15,
        /// <c>UIStateAssetDeployment</c>:16), so <c>DeclaringType</c> keys straight into the EXISTING
        /// coverage table and no context ever needs an entry of its own.</summary>
        private static bool KindIsMirrored(IGeoscapeRestorableViewStateContext context)
        {
            var stateType = context?.GetType().DeclaringType;
            if (stateType == null) return false;                              // not a nested context: keep it
            if (stateType != typeof(UIStateGeoModal)) return KindIsMirrored(stateType, default(ModalType));
            // The modal family is 43 windows wearing one type and its verdict lives on the ModalType axis;
            // a modal whose kind cannot be read is KEPT, because the type-level "Mirrored" is a pointer to
            // that second table and not a verdict about this entry.
            return ModalKindField?.GetValue(context) is ModalType modal && KindIsMirrored(stateType, modal);
        }

        private static readonly FieldInfo ModalKindField =
            AccessTools.Field(AccessTools.Inner(typeof(UIStateGeoModal), "RestoreContext"), "_modal");

        /// <summary>
        /// THE VERDICT, table-only and pure so RailCheck L135 can EXECUTE it rather than read its call graph.
        ///
        /// A <c>Mirrored</c> window has EXACTLY ONE producer — the host's raise surface (0xB6 events, 0xB7
        /// modals and the non-modal arm, 0xBA cutscenes). A peer that did not author the save therefore
        /// already has, or is already holding, every mirrored window in it, and a restored copy is a second
        /// window for one raise: the campaign-intro duplicate of 2026-08-06, where the host's autosave ran
        /// 14 ms after it raised three <c>IntroBetterGeo</c> events and the blob carried all three as queued
        /// <c>UIStateGeoscapeEvent</c>s while the same three arrived again as held 0xB6 raises.
        ///
        /// <c>LocalOnly</c> and <c>Gap</c> kinds are KEPT, and not out of caution: they have no producer at
        /// all, so dropping them would be an uncompensated loss rather than a de-duplication. (That the
        /// host's LocalOnly windows arguably do not belong on a client's screen either is a separate
        /// question with a separate answer, and this filter does not pretend to have it.)
        /// </summary>
        internal static bool KindIsMirrored(Type stateType, ModalType modal)
        {
            if (stateType == null) return false;
            var rule = stateType == typeof(UIStateGeoModal)
                ? GeoWindowCoverage.RuleForModal(modal)
                : GeoWindowCoverage.RuleFor(stateType);
            return rule != null && rule.Sync == WindowSync.Mirrored;
        }

        /// <summary>NON-NULL = the thing handed in is a window whose SUBJECT MISSION has already resolved,
        /// and the string is that mission's name for the log line. The one verdict, shared by the two moments
        /// a window can be found stale, because a window is only ever stale for one reason:
        ///
        ///   • RESTORE (<see cref="Prefix"/>) — the save's queue, filtered as it is rebuilt.
        ///   • SERVE (<see cref="WindowOrder.DropResolvedSubjects"/>) — the live queue, filtered every frame
        ///     the game pumps its own drain.
        ///
        /// THE SECOND ONE IS WHY THIS EXISTS. Restore is a ONE-SHOT check and it runs TOO EARLY: measured
        /// 2026-08-08, host multiplayer.log:3378, the restore reported "1 entries in the save, 1 kept — 0
        /// dropped (subject already resolved)" at 02:49:11.734, and the mission it was about only resolved at
        /// 02:49:12.015 (`[MP][outcome] … activeMission=none`, structural destroy of
        /// `S#213.SerializationData.ActiveMission` at :12.254) — 281 ms LATER. The filter asked the right
        /// question at a moment when the honest answer was still "no". <c>RestoreData</c> only REBUILDS
        /// <c>_viewStateSwitchRequests</c>; the entries are popped one at a time later by
        /// <c>ProcessQueriedStateSwitch</c>, so validity has to be a property of SERVING a window, not of
        /// restoring one. The restore-time call stays: it is free and it drops a window one frame earlier
        /// when the subject is already dead by then.
        ///
        /// IT TAKES <c>object</c> ON PURPOSE. At restore the holder is an
        /// <c>IGeoscapeRestorableViewStateContext</c>; at serve it is the live
        /// <c>IState&lt;GeoscapeViewContext&gt;</c> itself. Neither shares an interface with the other and
        /// neither needs to: the subject is read by walking fields for a <c>GeoMission</c>, so the same
        /// verdict covers both without a type table. That is also what makes the serve-time filter reach
        /// windows the restore-time one CANNOT see at all — <c>UIStateRosterDeployment</c> (the "start
        /// mission" squad screen, <c>_mission</c>:29) is queued at <c>ToDeploymentState</c>:596 and is NOT an
        /// <c>IGeoscapeRestorableViewState</c>, so it never appears in a save's queue and was outside every
        /// restore-time filter ever written here.</summary>
        internal static string ResolvedSubjectName(object holder)
        {
            var mission = SubjectMission(holder);
            if (mission == null || !HasResolved(mission)) return null;
            return mission.MissionDef == null ? "?" : mission.MissionDef.name;
        }

        /// <summary>The game's OWN verdict that a mission is over (<c>UIStateInitial.EnterState</c>:102),
        /// not a second opinion. <c>GetMissionOutcomeState</c>:556 dereferences
        /// <c>Site.GeoLevel.ViewerFaction</c> whenever <c>Result</c> is set, so the site is checked first —
        /// a restored context can name a mission whose site is already gone.</summary>
        internal static bool HasResolved(GeoMission mission)
        {
            if (mission.IsCompleted) return true;
            if (mission.Site == null) return true;
            return mission.GetMissionOutcomeState() != PhoenixPoint.Tactical.Levels.TacFactionState.Playing;
        }

        /// <summary>THE SUBJECT, read off whatever context the save produced. Field-walked rather than
        /// type-switched on purpose: <c>UIStateGeoModal.RestoreContext._modalData</c> is typed <c>object</c>
        /// and holds a different class per <c>ModalType</c>, so there is no static table to key on — the
        /// object in hand is the only honest answer (the same argument <c>GeoModalMirror.DataShape</c> makes
        /// for deriving a shape from the runtime type). A context with no mission in it returns null and is
        /// restored exactly as before.
        ///
        /// MEASURED REACH, not assumed: of the five <c>IGeoscapeRestorableViewState</c> implementors only
        /// <c>UIStateGeoModal</c> can hold a mission at all (<c>UIStateGeoscapeEvent</c>,
        /// <c>UIStateBaseGeoscapeEvent</c>, <c>UIStateMarketplaceGeoscapeEvent</c> and
        /// <c>UIStateAssetDeployment</c> name no <c>GeoMission</c> anywhere in their decompiled source), so
        /// the post-mission event window — <c>PROG_AN0_WIN</c> and every sibling — CANNOT be reached by this
        /// filter even though it is raised seconds after the mission it celebrates. That is not luck and it
        /// is not a carve-out: it falls out of keying on the subject the window actually carries.</summary>
        internal static GeoMission SubjectMission(object context)
        {
            if (context == null) return null;
            for (var t = context.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.GetValue(context) is GeoMission m) return m;
            return null;
        }
    }
}
