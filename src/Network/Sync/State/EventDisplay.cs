using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Client-side reflection bridge that SHOWS / DISMISSES a geoscape event dialog the way the game
    /// itself does. The client never simulates the geoscape, so it never raises the event locally
    /// (<c>GeoscapeView.OnGeoscapeEventRaised</c> GeoscapeView.cs:2109 never runs). The host broadcasts
    /// <c>EventRaised</c>/<c>EventDismiss</c>; here we drive the same UI state-switch query.
    ///
    /// Verified against the decompile (2026-06-16):
    ///   • <c>GeoLevelController.View</c> (public field, GeoLevelController.cs:101) → <c>GeoscapeView</c>.
    ///   • <c>GeoscapeView._viewSwichQuery</c> (private field, :131) → <c>GeoscapeViewSwitchQuery</c> (sic — game typo).
    ///   • <c>GeoscapeViewSwitchQuery.QueryStateSwitch(GeoscapeViewStateSwitchRequest)</c> (:75) — enqueues a
    ///     request; <c>GeoscapeView</c>'s update pops it (ProcessQueriedStateSwitch :58) and pushes the state.
    ///   • <c>GeoscapeViewStateSwitchRequest(IState&lt;GeoscapeViewContext&gt; state, int priority = 0)</c>
    ///     ctor (GeoscapeViewStateSwitchRequest.cs:13); public field <c>PauseGame</c> (:11).
    ///   • <c>UIStateGeoscapeEvent(GeoscapeEvent)</c> public ctor (UIStateGeoscapeEvent.cs:42), a
    ///     <c>UIStateBaseGeoscapeEvent&lt;T&gt;</c> (the dialog state pushed at GeoscapeView.cs:2131).
    ///   • dismiss = <c>GeoscapeView.FinishQueriedState()</c> (public, :2239 → _viewSwichQuery.FinishCurrentStateSwitch
    ///     :116) — the SAME call the dialog's own OnCancel/FinishEncounter use (UIStateBaseGeoscapeEvent.cs:24-30).
    ///     We dismiss only when the CURRENT switch request's State is a UIStateBaseGeoscapeEvent&lt;&gt; so we never
    ///     close an unrelated geoscape state.
    ///
    /// IMPORTANT: <c>PauseGame = false</c> on the client push. Pause already arrives via the time-sync anchor;
    /// forcing a pause here would relay back through <c>TimeControlPatches</c> and risk a pause loop.
    /// Every path is wrapped in try/catch — best-effort, never throws into game code.
    /// </summary>
    public static class EventDisplay
    {
        private static bool _ready;
        private static FieldInfo _viewField;            // GeoLevelController.View
        private static FieldInfo _switchQueryField;     // GeoscapeView._viewSwichQuery
        private static MethodInfo _queryStateSwitch;    // GeoscapeViewSwitchQuery.QueryStateSwitch(GeoscapeViewStateSwitchRequest)
        private static FieldInfo _currentRequestField;  // GeoscapeViewSwitchQuery._currentStateSwitchRequest
        private static FieldInfo _requestStateField;    // GeoscapeViewStateSwitchRequest.State
        private static FieldInfo _requestPauseField;    // GeoscapeViewStateSwitchRequest.PauseGame
        private static ConstructorInfo _requestCtor;    // GeoscapeViewStateSwitchRequest(IState<>, int)
        private static ConstructorInfo _uiStateCtor;    // UIStateGeoscapeEvent(GeoscapeEvent)
        private static MethodInfo _finishQueriedState;  // GeoscapeView.FinishQueriedState()
        private static Type _eventStateBaseType;        // UIStateBaseGeoscapeEvent<> (open generic)
        private static FieldInfo _eventIdField;         // GeoscapeEvent.EventID (string, public field :18)

        // The occurrence id of the dialog THIS client currently has open (0 = none). The native UIState carries
        // no occurrence id, so Show() records it here and Dismiss()/ShowResult() match on it — the authoritative
        // per-occurrence correlation that the reusable def-name (eventId) could not provide. The def-name is
        // still read for logging. Only one host-broadcast geoscape-event dialog is ever open at a time (modal).
        private static ushort _openOccurrenceId;

        /// <summary>The occurrence id of the dialog this client currently has open (0 = none / synthetic page).</summary>
        public static ushort OpenOccurrenceId => _openOccurrenceId;

        private static void Ensure()
        {
            if (_ready) return;
            var geoType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            var viewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var queryType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeViewSwitchQuery");
            var requestType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeViewStateSwitchRequest");
            var uiStateType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoscapeEvent");
            var eventType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");
            _eventStateBaseType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateBaseGeoscapeEvent`1");
            if (geoType == null || viewType == null || queryType == null || requestType == null
                || uiStateType == null || eventType == null) return;

            _viewField = AccessTools.Field(geoType, "View");
            _switchQueryField = AccessTools.Field(viewType, "_viewSwichQuery");
            _queryStateSwitch = AccessTools.Method(queryType, "QueryStateSwitch", new[] { requestType });
            _currentRequestField = AccessTools.Field(queryType, "_currentStateSwitchRequest");
            _requestStateField = AccessTools.Field(requestType, "State");
            _requestPauseField = AccessTools.Field(requestType, "PauseGame");
            _requestCtor = AccessTools.Constructor(requestType, new[] { _requestStateField?.FieldType, typeof(int) });
            _uiStateCtor = AccessTools.Constructor(uiStateType, new[] { eventType });
            _finishQueriedState = AccessTools.Method(viewType, "FinishQueriedState");
            _eventIdField = AccessTools.Field(eventType, "EventID");   // for id-matched dismiss

            _ready = _viewField != null && _switchQueryField != null && _queryStateSwitch != null
                     && _requestCtor != null && _uiStateCtor != null && _finishQueriedState != null;
        }

        private static object GetView(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null) return null;
            return _viewField.GetValue(geo);
        }

        /// <summary>
        /// Client: queue a geoscape-event dialog (PauseGame=false) and record its <paramref name="occurrenceId"/>
        /// as the currently-open dialog so a later <see cref="Dismiss"/>/<see cref="ShowResult"/> closes the right
        /// occurrence. <paramref name="eventId"/> is the def-name (logging only). No-op on any failure.
        /// </summary>
        public static void Show(GeoRuntime rt, object geoEvent, ushort occurrenceId = 0, string eventId = null)
        {
            try
            {
                Ensure();
                if (!_ready || geoEvent == null) return;
                var view = GetView(rt);
                if (view == null) return;
                var query = _switchQueryField.GetValue(view);
                if (query == null) return;

                object uiState = _uiStateCtor.Invoke(new[] { geoEvent });
                object request = _requestCtor.Invoke(new object[] { uiState, 0 });
                // pause arrives via time-sync; do NOT pause here (avoids a pause-relay loop).
                _requestPauseField?.SetValue(request, false);
                _queryStateSwitch.Invoke(query, new[] { request });
                // Record the now-open occurrence (only when a real id was supplied; a synthetic result page is
                // pushed with occurrenceId=0 since it is locally dismissed and never host-correlated).
                if (occurrenceId != 0) _openOccurrenceId = occurrenceId;
                Debug.Log("[Multipleer] EventDisplay.Show occId=" + occurrenceId + " eventId=" + eventId + " → queued");
            }
            catch (Exception ex) { Debug.LogWarning("[Multipleer] EventDisplay.Show best-effort failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: replace the open (locked) choice modal for occurrence <paramref name="occurrenceId"/> with the
        /// host's follow-up RESULT/OUTCOME page, reusing the SAME native push as <see cref="Show"/>. This mirrors
        /// the native in-place second page of <c>UIModuleSiteEncounters</c> (SetClosingEncounter): we first close
        /// the open choice dialog for that occurrence (the host already applied the answer) then push the synthetic
        /// result event as a fresh <c>UIStateGeoscapeEvent</c>. The result event is single-choice with EventID=""
        /// → unlocked → the client can locally dismiss it with OK. No custom UI; no reward apply (text-only).
        /// <paramref name="eventId"/> is the def-name (logging only). No-op on failure.
        /// </summary>
        public static void ShowResult(GeoRuntime rt, object resultEvent, ushort occurrenceId = 0, string eventId = null)
        {
            try
            {
                Ensure();
                if (!_ready || resultEvent == null) return;
                var view0 = GetView(rt);
                var state0 = view0 != null ? GetCurrentEventState(view0) : null;
                Debug.Log("[Multipleer] EventDisplay.ShowResult occId=" + occurrenceId + " eventId=" + eventId +
                          " openOccId=" + _openOccurrenceId + " openIsEventState=" + (state0 != null) +
                          " openEventId=" + GetStateEventId(state0) + " → Dismiss+Show result page");
                // Close the open locked choice modal for THIS occurrence first (host answer applied) so the result
                // page replaces it in-place rather than stacking on top of a still-open dialog.
                Dismiss(rt, occurrenceId, eventId);
                // The synthetic result page is locally dismissible and never host-correlated → push with occId 0.
                Show(rt, resultEvent, 0, eventId);
            }
            catch (Exception ex) { Debug.LogWarning("[Multipleer] EventDisplay.ShowResult best-effort failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: close the open geoscape-event dialog for occurrence <paramref name="occurrenceId"/>. Guarded so
        /// it only fires when the current switch request is a geoscape-event state AND (when a non-zero occurrence
        /// id is supplied) it MATCHES the occurrence this client recorded as open — so a dismiss for one occurrence
        /// never closes a different dialog, even when two share the reusable def-name. Falls back to closing the
        /// current event dialog when no occurrence was recorded (best-effort). <paramref name="eventId"/> is the
        /// def-name (logging only). No-op on any failure.
        /// </summary>
        public static void Dismiss(GeoRuntime rt, ushort occurrenceId = 0, string eventId = null)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var view = GetView(rt);
                if (view == null) return;
                var state = GetCurrentEventState(view);
                if (state == null)
                {
                    Debug.Log("[Multipleer] EventDisplay.Dismiss occId=" + occurrenceId + " eventId=" + eventId +
                              " stateMatched=false (current state is NOT a UIStateBaseGeoscapeEvent<> → nothing to close)");
                    return;   // current state isn't a geoscape-event dialog → nothing to close
                }

                // Occurrence-id correlation: only close when the recorded open occurrence matches the requested
                // one. _openOccurrenceId == 0 means we never recorded one (e.g. result page) → fall back to closing
                // the current dialog. A non-zero mismatch means a DIFFERENT occurrence is open → don't close it.
                if (occurrenceId != 0 && _openOccurrenceId != 0 && _openOccurrenceId != occurrenceId)
                {
                    Debug.Log("[Multipleer] EventDisplay.Dismiss occId=" + occurrenceId + " eventId=" + eventId +
                              " openOccId=" + _openOccurrenceId + " openEventId=" + GetStateEventId(state) +
                              " stateMatched=true occMatch=false (different occurrence open → not closing)");
                    return;
                }
                Debug.Log("[Multipleer] EventDisplay.Dismiss occId=" + occurrenceId + " eventId=" + eventId +
                          " openOccId=" + _openOccurrenceId + " openEventId=" + GetStateEventId(state) +
                          " stateMatched=true occMatch=true → FinishQueriedState");
                _finishQueriedState.Invoke(view, null);
                // Clear the recorded open occurrence when we close the one it referred to (or when closing the
                // current dialog with no recorded occurrence).
                if (occurrenceId == 0 || _openOccurrenceId == occurrenceId) _openOccurrenceId = 0;
            }
            catch (Exception ex) { Debug.LogWarning("[Multipleer] EventDisplay.Dismiss best-effort failed: " + ex.Message); }
        }

        // (Removed CloseHostEventModal: the host never needs a force-close-to-"show" path. Host-WIN lets native
        // render+close its result page; host-resolves-remote-claim renders the synthetic result page via
        // SyncEngine.ResolveToResultPage (and its OK click closes natively via ShouldLocalClose). Both the host's
        // own losing click and the synthetic result-page OK dismiss are handled by the native FinishEncounter path.)

        // The current GeoscapeViewSwitchQuery._currentStateSwitchRequest.State if it is a
        // UIStateBaseGeoscapeEvent<>, else null.
        private static object GetCurrentEventState(object view)
        {
            try
            {
                if (_switchQueryField == null || _currentRequestField == null || _requestStateField == null) return null;
                var query = _switchQueryField.GetValue(view);
                if (query == null) return null;
                var req = _currentRequestField.GetValue(query);
                if (req == null) return null;
                var state = _requestStateField.GetValue(req);
                if (state == null) return null;
                return IsGeoscapeEventState(state.GetType()) ? state : null;
            }
            catch { return null; }
        }

        // Read the EventID off a UIStateBaseGeoscapeEvent<>.Event (public GeoscapeEvent property :10), or null.
        private static string GetStateEventId(object eventState)
        {
            try
            {
                if (eventState == null || _eventIdField == null) return null;
                // Event is declared on the open generic base; resolve via the concrete instance's type chain.
                var prop = AccessTools.Property(eventState.GetType(), "Event");
                var geoEvent = prop?.GetValue(eventState, null);
                if (geoEvent == null) return null;
                return _eventIdField.GetValue(geoEvent) as string;
            }
            catch { return null; }
        }

        private static bool IsGeoscapeEventState(Type t)
        {
            if (_eventStateBaseType == null) return false;
            // Walk the base chain; the event states derive from UIStateBaseGeoscapeEvent<TModule> (closed generic).
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                if (cur.IsGenericType && cur.GetGenericTypeDefinition() == _eventStateBaseType) return true;
            }
            return false;
        }
    }
}
