using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Client-side reflection bridge that SHOWS a geoscape REPORT modal the way the game itself does, mirroring
    /// <see cref="EventDisplay"/>. The host opens its report windows via <c>GeoscapeView.OpenModalPersistent</c> /
    /// <c>OpenModal</c>; here we drive the SAME <c>_viewSwichQuery.QueryStateSwitch</c> push with a freshly built
    /// <c>UIStateGeoModal</c>, reconstructing the modalData from synced ids (<see cref="ReportModalReflection"/>).
    ///
    /// CHANNEL OWNERSHIP (do not blur): this is the REPORT-WINDOW channel (0x69 ReportModalShow). It carries ONLY
    /// the whitelisted <c>GeoscapeView.OpenModal</c>/<c>OpenModalPersistent</c> report modals (ModalType 6/14/25/38).
    /// Geoscape EVENT windows (<c>UIStateGeoscapeEvent : UIStateBaseGeoscapeEvent&lt;&gt;</c>) are owned by a SEPARATE
    /// channel (0x65/0x66 EventRaised/Dismiss, occurrence-id FIFO deduped) and are NEVER routed here — they do not
    /// even go through the OpenModal chokepoint (they push a state-stack state via
    /// <c>MainUILayer.SetActiveState("GeoscapeEvent")</c>) and have NO ModalType entry, so the two channels cannot
    /// overlap. Keep it that way: never add an event ModalType to the report whitelist.
    ///
    /// Two deliberate differences from the native openers (both proven by <see cref="EventDisplay"/>):
    ///   • <c>PauseGame = false</c> on the client push — pause already arrives via the time-sync anchor; forcing a
    ///     pause here would relay back through TimeControlPatches and risk a pause loop.
    ///   • <c>DialogCallback = null</c> — the native <c>OpenModalPersistent</c> builds a callback that routes to
    ///     <c>GeoscapeView.ModalResultCallback</c>; for GeoResearchComplete that reaches
    ///     <c>ResearchCompleteModalHandler</c> which can fire <c>ToCutsceneState</c>/<c>ToResearchState</c> on
    ///     confirm (UNSAFE on a client, GeoscapeView.cs:2108-2121). A null handler is null-safe everywhere
    ///     (<c>UIStateGeoModal.FinishDialog</c> :86 / <c>ExitState</c> :121 both null-guard) — the native
    ///     DiplomacyResearchBrief opener already passes null — so the client closes the report with a plain OK
    ///     and never mutates host-only state.
    ///
    /// Verified against the decompile (2026-06-26):
    ///   • <c>GeoLevelController.View</c> (public field) → <c>GeoscapeView</c>.
    ///   • <c>GeoscapeView._viewSwichQuery</c> (private field) → <c>GeoscapeViewSwitchQuery.QueryStateSwitch(
    ///     GeoscapeViewStateSwitchRequest)</c>; <c>GeoscapeViewStateSwitchRequest(IState&lt;GeoscapeViewContext&gt;,
    ///     int priority)</c> ctor + public field <c>PauseGame</c>.
    ///   • <c>UIStateGeoModal(ModalType modal, DialogCallback dialogHandler = null, object modalData = null)</c>
    ///     ctor (UIStateGeoModal.cs:65) + public field <c>Persistent</c> (:49). NullData/SiteOnly modals are
    ///     opened persistent natively, so the client sets Persistent to match.
    /// Every path is wrapped in try/catch — best-effort, never throws into game code.
    /// </summary>
    public static class GeoModalDisplay
    {
        private static bool _ready;
        private static FieldInfo _viewField;            // GeoLevelController.View
        private static FieldInfo _switchQueryField;     // GeoscapeView._viewSwichQuery
        private static MethodInfo _queryStateSwitch;    // GeoscapeViewSwitchQuery.QueryStateSwitch(request)
        private static FieldInfo _requestStateField;    // GeoscapeViewStateSwitchRequest.State
        private static FieldInfo _requestPauseField;    // GeoscapeViewStateSwitchRequest.PauseGame
        private static ConstructorInfo _requestCtor;    // GeoscapeViewStateSwitchRequest(IState<>, int)
        private static ConstructorInfo _uiStateCtor;    // UIStateGeoModal(ModalType, DialogCallback, object)
        private static FieldInfo _persistentField;      // UIStateGeoModal.Persistent (bool)
        private static Type _modalTypeEnum;             // PhoenixPoint.Common.Utils.ModalType
        private static MethodInfo _toResearchState;     // GeoscapeView.ToResearchState() — client "new research available" nav
        // Blocking-modal (ambush brief) close path — all best-effort, mirrors EventDisplay.Dismiss:
        private static Type _uiStateGeoModalType;       // UIStateGeoModal (current-state type match)
        private static PropertyInfo _uiStateModalTypeProp; // UIStateGeoModal.ModalType (public property, :63)
        private static FieldInfo _currentRequestField;  // GeoscapeViewSwitchQuery._currentStateSwitchRequest
        private static MethodInfo _finishQueriedState;  // GeoscapeView.FinishQueriedState()

        private static void Ensure()
        {
            if (_ready) return;
            var geoType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            var viewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var queryType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeViewSwitchQuery");
            var requestType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeViewStateSwitchRequest");
            var uiStateType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoModal");
            _modalTypeEnum = AccessTools.TypeByName("PhoenixPoint.Common.Utils.ModalType");
            var dialogCallbackType = AccessTools.TypeByName("PhoenixPoint.Common.Utils.DialogCallback");
            if (geoType == null || viewType == null || queryType == null || requestType == null
                || uiStateType == null || _modalTypeEnum == null || dialogCallbackType == null) return;

            _viewField = AccessTools.Field(geoType, "View");
            _switchQueryField = AccessTools.Field(viewType, "_viewSwichQuery");
            _queryStateSwitch = AccessTools.Method(queryType, "QueryStateSwitch", new[] { requestType });
            _requestStateField = AccessTools.Field(requestType, "State");
            _requestPauseField = AccessTools.Field(requestType, "PauseGame");
            _requestCtor = AccessTools.Constructor(requestType, new[] { _requestStateField?.FieldType, typeof(int) });
            // EXACT param match (harmony-accesstools-exact-param-match): (ModalType, DialogCallback, object).
            _uiStateCtor = AccessTools.Constructor(uiStateType, new[] { _modalTypeEnum, dialogCallbackType, typeof(object) });
            _persistentField = AccessTools.Field(uiStateType, "Persistent");
            // Best-effort (NOT part of _ready): the client's "new research available" click nav (GeoscapeView.cs:696).
            _toResearchState = AccessTools.Method(viewType, "ToResearchState", Type.EmptyTypes);
            // Best-effort (NOT part of _ready): blocking-modal (ambush) duplicate-show guard + host-driven close.
            _uiStateGeoModalType = uiStateType;
            _uiStateModalTypeProp = AccessTools.Property(uiStateType, "ModalType");
            _currentRequestField = AccessTools.Field(queryType, "_currentStateSwitchRequest");
            _finishQueriedState = AccessTools.Method(viewType, "FinishQueriedState", Type.EmptyTypes);

            _ready = _viewField != null && _switchQueryField != null && _queryStateSwitch != null
                     && _requestStateField != null && _requestCtor != null && _uiStateCtor != null;
        }

        private static object GetView(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null || _viewField == null) return null;
            return _viewField.GetValue(geo);
        }

        /// <summary>
        /// Client: queue a report modal (<paramref name="modalType"/> = native ModalType byte) with the
        /// reconstructed <paramref name="modalData"/> at <paramref name="priority"/>, with PauseGame=false and a
        /// null DialogCallback. <paramref name="persistent"/> mirrors whether the host opened it via
        /// OpenModalPersistent. No-op on any failure.
        /// </summary>
        public static void Show(GeoRuntime rt, byte modalType, object modalData, int priority, bool persistent)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var view = GetView(rt);
                if (view == null) return;
                var query = _switchQueryField.GetValue(view);
                if (query == null) return;

                // Idempotence guard for the BLOCKING modal (ambush brief): a duplicate Show for the SAME
                // uncloseable window would stack a second locked state the client could never leave. Report
                // modals stay unguarded (a second research popup for a different element is legitimate).
                if (ReportModalClassifier.IsBlockingModal(modalType) && IsCurrentGeoModalOfType(query, modalType))
                {
                    Debug.Log("[Multiplayer] GeoModalDisplay.Show modalType=" + modalType +
                              " skipped (same blocking modal already current — duplicate mirror push)");
                    return;
                }

                object modalTypeEnum = Enum.ToObject(_modalTypeEnum, (int)modalType);
                object uiState = _uiStateCtor.Invoke(new object[] { modalTypeEnum, null, modalData });
                if (persistent && _persistentField != null) _persistentField.SetValue(uiState, true);

                object request = _requestCtor.Invoke(new object[] { uiState, priority });
                // pause arrives via time-sync; do NOT pause here (avoids a pause-relay loop).
                _requestPauseField?.SetValue(request, false);
                _queryStateSwitch.Invoke(query, new[] { request });
                Debug.Log("[Multiplayer] GeoModalDisplay.Show modalType=" + modalType + " persistent=" + persistent +
                          " priority=" + priority + " hasData=" + (modalData != null) + " → queued");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoModalDisplay.Show best-effort failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: the host RESOLVED its blocking modal (ambush brief) → close the mirrored view-locked copy.
        /// Type-matched against the CURRENT queried state (same request-read as <see cref="EventDisplay"/>'s
        /// Dismiss): only when it is a <c>UIStateGeoModal</c> whose <c>ModalType</c> equals
        /// <paramref name="modalType"/> do we drive the native <c>GeoscapeView.FinishQueriedState()</c> — the
        /// exact pop <c>UIStateGeoModal.FinishDialog</c> performs (its null client DialogCallback makes ExitState
        /// side-effect free; the modal module hides + button lock restores via the Hide postfix). A stray or late
        /// hide with a different/absent window is a logged no-op (idempotent).
        /// </summary>
        public static void CloseBlocking(GeoRuntime rt, byte modalType)
        {
            try
            {
                Ensure();
                if (!_ready || _finishQueriedState == null) return;
                var view = GetView(rt);
                if (view == null) return;
                var query = _switchQueryField.GetValue(view);
                if (query == null) return;
                if (!IsCurrentGeoModalOfType(query, modalType))
                {
                    Debug.Log("[Multiplayer] GeoModalDisplay.CloseBlocking modalType=" + modalType +
                              " → no matching modal current (already closed / never shown) — no-op");
                    return;
                }
                _finishQueriedState.Invoke(view, null);
                Debug.Log("[Multiplayer] GeoModalDisplay.CloseBlocking modalType=" + modalType +
                          " → FinishQueriedState (host resolved its blocking prompt)");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoModalDisplay.CloseBlocking best-effort failed: " + ex.Message); }
        }

        /// <summary>True iff the switch query's CURRENT state is a <c>UIStateGeoModal</c> showing <paramref name="modalType"/>.</summary>
        private static bool IsCurrentGeoModalOfType(object query, byte modalType)
        {
            try
            {
                if (_currentRequestField == null || _requestStateField == null
                    || _uiStateGeoModalType == null || _uiStateModalTypeProp == null) return false;
                var req = _currentRequestField.GetValue(query);
                if (req == null) return false;
                var state = _requestStateField.GetValue(req);
                if (state == null || !_uiStateGeoModalType.IsInstanceOfType(state)) return false;
                return Convert.ToInt32(_uiStateModalTypeProp.GetValue(state, null)) == modalType;
            }
            catch { return false; }
        }

        /// <summary>
        /// Client: navigate to the native research screen — the SAME entry the Research tab button uses
        /// (<c>GeoscapeView.ToResearchState()</c> → <c>_statesStack.SwitchToState(new UIStateResearch(),
        /// ClearStackAndPush)</c>, GeoscapeView.cs:696). Pure local UI navigation, no sim mutation, so it is safe on
        /// a frozen client. Used to reproduce the "new research available" line's effect after the mirrored
        /// GeoResearchComplete modal (whose DialogCallback is null on the client) closes without navigating.
        /// No-op / best-effort on any failure (the modal simply stays closed — no regression).
        /// </summary>
        public static void NavigateToResearch(GeoRuntime rt)
        {
            try
            {
                Ensure();
                var view = GetView(rt);
                if (view == null || _toResearchState == null) return;
                _toResearchState.Invoke(view, null);
                Debug.Log("[Multiplayer] GeoModalDisplay.NavigateToResearch → ToResearchState() (client new-research-available line)");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoModalDisplay.NavigateToResearch best-effort failed: " + ex.Message); }
        }
    }
}
