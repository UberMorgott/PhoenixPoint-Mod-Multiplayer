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

        /// <summary>Client: queue a geoscape-event dialog (PauseGame=false). No-op on any failure.</summary>
        public static void Show(GeoRuntime rt, object geoEvent)
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
            }
            catch (Exception ex) { Debug.LogWarning("[Multipleer] EventDisplay.Show best-effort failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: close the open geoscape-event dialog. Guarded so it only fires when the current switch
        /// request is a geoscape-event state AND (when <paramref name="eventId"/> is supplied and the open
        /// event's id is readable) that state's event id MATCHES — so a dismiss for one event never closes a
        /// different dialog the client happens to have open. Falls back to closing the current event dialog
        /// only when the id can't be read (best-effort). No-op on any failure.
        /// </summary>
        public static void Dismiss(GeoRuntime rt, string eventId = null)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var view = GetView(rt);
                if (view == null) return;
                var state = GetCurrentEventState(view);
                if (state == null) return;   // current state isn't a geoscape-event dialog → nothing to close

                // If we know which event to dismiss and can read the open event's id, only close on a match.
                if (!string.IsNullOrEmpty(eventId))
                {
                    string openId = GetStateEventId(state);
                    if (!string.IsNullOrEmpty(openId) && !string.Equals(openId, eventId, StringComparison.Ordinal))
                        return; // a different event is open → don't close the wrong dialog
                }
                _finishQueriedState.Invoke(view, null);
            }
            catch (Exception ex) { Debug.LogWarning("[Multipleer] EventDisplay.Dismiss best-effort failed: " + ex.Message); }
        }

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
