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
    /// the whitelisted <c>GeoscapeView.OpenModal</c>/<c>OpenModalPersistent</c> modals (reports 6/14/25/38 +
    /// the mirrored mission briefs 15/4/26/28 + the ActiveMissionBrief family 0/2/11/20/34/36 —
    /// <see cref="ReportModalClassifier"/>).
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
        // Host begin-mission relay (MissionStartRequestAction) — drive the host's OWN open brief natively:
        private static PropertyInfo _uiStateModalDataProp; // UIStateGeoModal.ModalData (public property, :61)
        private static MethodInfo _finishDialog;        // UIStateGeoModal.FinishDialog(ModalResult) (:84 — the button click path)
        private static Type _modalResultType;           // PhoenixPoint.Common.Utils.ModalResult (Confirm = 0)
        // Client-side squad pick (2026-07-13): open the native deployment window on the INITIATING client.
        private static ConstructorInfo _deployStateCtor; // UIStateRosterDeployment(GeoMission, GeoFaction, IGeoCharacterContainer, bool)
        private static FieldInfo _deployModeField;       // GeoscapeView.SetUiInDeploymentMode (public bool, GeoscapeView.cs:104)
        private static MethodInfo _resetViewState;       // GeoscapeView.ResetViewState(UIStateInitial.Params = null) (:414)

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
            // Best-effort (NOT part of _ready): host-side begin-mission relay resolve (MissionStartRequestAction).
            _uiStateModalDataProp = AccessTools.Property(uiStateType, "ModalData");
            _modalResultType = AccessTools.TypeByName("PhoenixPoint.Common.Utils.ModalResult");
            if (_modalResultType != null)
                // EXACT param match (harmony-accesstools-exact-param-match): FinishDialog(ModalResult).
                _finishDialog = AccessTools.Method(uiStateType, "FinishDialog", new[] { _modalResultType });
            // Best-effort (NOT part of _ready): client-side squad pick — native deployment window on the client.
            var deployStateT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateRosterDeployment");
            var missionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoMission");
            var factionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            var containerT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.IGeoCharacterContainer");
            if (deployStateT != null && missionT != null && factionT != null && containerT != null)
                // EXACT param match (harmony-accesstools-exact-param-match): the 4-arg ctor (UIStateRosterDeployment.cs:74).
                _deployStateCtor = AccessTools.Constructor(deployStateT, new[] { missionT, factionT, containerT, typeof(bool) });
            _deployModeField = AccessTools.Field(viewType, "SetUiInDeploymentMode");
            var initialParamsT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateInitial+Params");
            if (initialParamsT != null)
                _resetViewState = AccessTools.Method(viewType, "ResetViewState", new[] { initialParamsT });

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
        /// Client: true iff the geoscape view + its switch query are LIVE, i.e. <see cref="Show"/> would actually
        /// queue (not silently no-op). Drives the Batch-2 outcome-show deferral: a mission-outcome 0x69 can land
        /// while this client is still IN TACTICAL (the host re-enters geoscape first — its post-tac rail fires on
        /// re-entry), and a dropped outcome would lose the loot report — so SyncEngine queues it and drains once
        /// this turns true (spec Batch-2 risk: "queue, don't drop").
        /// </summary>
        public static bool CanShow(GeoRuntime rt)
        {
            try
            {
                Ensure();
                if (!_ready) return false;
                var view = GetView(rt);
                if (view == null) return false;
                return _switchQueryField.GetValue(view) != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Client: queue a report modal (<paramref name="modalType"/> = native ModalType byte) with the
        /// reconstructed <paramref name="modalData"/> at <paramref name="priority"/>, with PauseGame=false and a
        /// null DialogCallback. <paramref name="persistent"/> mirrors whether the host opened it via
        /// OpenModalPersistent. No-op on any failure. Returns TRUE iff the native view-switch push actually
        /// queued (Batch-3 P4: only a genuinely-queued window OCCUPIES the unified display queue — a silent
        /// no-op must release the slot immediately or every later display starves).
        /// </summary>
        public static bool Show(GeoRuntime rt, byte modalType, object modalData, int priority, bool persistent)
        {
            try
            {
                Ensure();
                if (!_ready) return false;
                var view = GetView(rt);
                if (view == null) return false;
                var query = _switchQueryField.GetValue(view);
                if (query == null) return false;

                // Idempotence guard for the BLOCKING modal (ambush brief): a duplicate Show for the SAME
                // uncloseable window would stack a second locked state the client could never leave. Report
                // modals stay unguarded (a second research popup for a different element is legitimate).
                if (ReportModalClassifier.IsBlockingModal(modalType) && IsCurrentGeoModalOfType(query, modalType))
                {
                    Debug.Log("[Multiplayer] GeoModalDisplay.Show modalType=" + modalType +
                              " skipped (same blocking modal already current — duplicate mirror push)");
                    return false;
                }

                object modalTypeEnum = Enum.ToObject(_modalTypeEnum, (int)modalType);
                object uiState = _uiStateCtor.Invoke(new object[] { modalTypeEnum, null, modalData });
                if (persistent && _persistentField != null) _persistentField.SetValue(uiState, true);

                object request = _requestCtor.Invoke(new object[] { uiState, priority });
                // pause arrives via time-sync; do NOT pause here (avoids a pause-relay loop).
                _requestPauseField?.SetValue(request, false);
                // ORIGIN TAG: only a MIRROR-shown MANDATORY brief is view-locked (BlockingModalClientLockPatches;
                // optional briefs keep their native CLOSE — the tag is still set for them but the lock decisions
                // ignore it). Tagged at queue time — the lock patches consult it when the window actually enters;
                // a ReportModalHide that lands first clears it (hide-before-show race → window enters unlocked).
                if (ReportModalClassifier.IsBlockingModal(modalType))
                    BlockingModalMirrorRegistry.MarkMirrorShown(modalType);
                _queryStateSwitch.Invoke(query, new[] { request });
                Debug.Log("[Multiplayer] GeoModalDisplay.Show modalType=" + modalType + " persistent=" + persistent +
                          " priority=" + priority + " hasData=" + (modalData != null) + " → queued");
                return true;
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoModalDisplay.Show best-effort failed: " + ex.Message); }
            return false;
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
                // ALWAYS drop the mirror-origin tag FIRST — including when no matching window is current yet.
                // That is exactly the hide-before-show race (the mirrored Show is a QUEUED state switch; a fast
                // host cancel can land the hide while the modal is still queued): with the tag cleared the
                // window then enters UNLOCKED (native buttons, null DialogCallback) and the user closes it
                // locally — previously it entered view-locked with no future hide → permanently dead Cancel.
                BlockingModalMirrorRegistry.ClearMirrorShown(modalType);
                Ensure();
                if (!_ready || _finishQueriedState == null) return;
                var view = GetView(rt);
                if (view == null) return;
                var query = _switchQueryField.GetValue(view);
                if (query == null) return;
                if (!IsCurrentGeoModalOfType(query, modalType))
                {
                    Debug.Log("[Multiplayer] GeoModalDisplay.CloseBlocking modalType=" + modalType +
                              " → no matching modal current (queued/closed) — mirror tag cleared; a queued copy enters unlocked");
                    return;
                }
                _finishQueriedState.Invoke(view, null);
                Debug.Log("[Multiplayer] GeoModalDisplay.CloseBlocking modalType=" + modalType +
                          " → FinishQueriedState (host resolved its blocking prompt)");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoModalDisplay.CloseBlocking best-effort failed: " + ex.Message); }
        }

        /// <summary>
        /// HOST: apply a client's relayed "begin mission" CONFIRM (<c>MissionStartRequestAction</c>) by driving
        /// the host's OWN currently-open brief through the exact native click path —
        /// <c>UIStateGeoModal.FinishDialog(ModalResult.Confirm)</c> (the method every modal button routes to,
        /// UIStateGeoModal.cs:84) → the opener's DialogCallback → <c>GeoscapeView.ModalResultCallback</c> →
        /// <c>LaunchMission</c> (+ the AncientSiteDefence handler branch, the mandatory-save toggle, the
        /// HostBlockingPromptGate release and the ReportModalHide broadcast — all native/patched behavior of a
        /// host click). VALIDATES first: the CURRENT queried state must be a <c>UIStateGeoModal</c> of the SAME
        /// <paramref name="modalType"/>, and when both site ids are readable its <c>ModalData</c> mission's
        /// <c>Site.SiteId</c> must equal <paramref name="siteId"/> (stale-request guard: the host may have
        /// resolved/cancelled meanwhile — the client re-mirrors on the normal hide). Returns TRUE iff the native
        /// confirm was invoked; FALSE = validated-reject (caller logs the no-op). Never throws.
        /// </summary>
        public static bool TryHostConfirmBlocking(GeoRuntime rt, byte modalType, int siteId)
        {
            try
            {
                Ensure();
                if (!_ready || _finishDialog == null || _modalResultType == null) return false;
                var view = GetView(rt);
                if (view == null) return false;
                var query = _switchQueryField.GetValue(view);
                if (query == null || _currentRequestField == null || _requestStateField == null
                    || _uiStateGeoModalType == null || _uiStateModalTypeProp == null) return false;
                var req = _currentRequestField.GetValue(query);
                var state = req != null ? _requestStateField.GetValue(req) : null;
                if (state == null || !_uiStateGeoModalType.IsInstanceOfType(state)) return false;   // no brief open (already resolved)
                if (Convert.ToInt32(_uiStateModalTypeProp.GetValue(state, null)) != modalType) return false;   // different window current
                // Site identity check (both readable → must match; -1 on either side degrades to type-match only).
                int hostSiteId = ReportModalReflection.GetMissionSiteId(_uiStateModalDataProp?.GetValue(state, null));
                if (siteId >= 0 && hostSiteId >= 0 && hostSiteId != siteId) return false;   // stale: another site's brief is up
                Debug.Log("[Multiplayer] HOST MissionStartRequest apply modalType=" + modalType + " siteId=" + siteId +
                          " hostSiteId=" + hostSiteId + " → FinishDialog(Confirm) (native launch path)");
                _finishDialog.Invoke(state, new[] { Enum.ToObject(_modalResultType, 0) });   // ModalResult.Confirm = 0
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoModalDisplay.TryHostConfirmBlocking failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// CLIENT (begin-mission squad pick, 2026-07-13): open the NATIVE deployment window
        /// (<c>UIStateRosterDeployment</c> — the exact state <c>GeoscapeView.ToDeploymentState</c> pushes after a
        /// host confirm, GeoscapeView.cs:592) on the INITIATING client, over its own mirrored geoscape.
        /// <paramref name="mission"/> is the mirror brief's rebuilt <c>ModalData</c> mission — it wraps the
        /// resolved LIVE site, so <c>GetDeploymentSources</c>/<c>MissionDef</c> read real mirror state. Deliberate
        /// deltas from native ToDeploymentState: PauseGame=false (pause is host-anchored — same reasoning as
        /// <see cref="Show"/>) — SetUiInDeploymentMode is still set to match native section-bar behavior.
        /// The window's Launch/Cancel exits are intercepted by DeploymentRelayPatches (client never launches
        /// locally). Returns false on any miss → caller degrades to the legacy immediate relay (host window).
        /// </summary>
        public static bool TryClientOpenDeployment(GeoRuntime rt, object mission)
        {
            try
            {
                Ensure();
                if (!_ready || _deployStateCtor == null || mission == null) return false;
                var view = GetView(rt);
                if (view == null) return false;
                var query = _switchQueryField.GetValue(view);
                if (query == null) return false;
                var faction = rt?.PhoenixFaction();
                if (faction == null) return false;

                object state = _deployStateCtor.Invoke(new object[] { mission, faction, null, true });
                object request = _requestCtor.Invoke(new object[] { state, int.MaxValue }); // native priority (GeoscapeView.cs:596)
                _requestPauseField?.SetValue(request, false);
                _deployModeField?.SetValue(view, true); // native ToDeploymentState sets this before the push
                _queryStateSwitch.Invoke(query, new[] { request });
                Debug.Log("[Multiplayer] GeoModalDisplay.TryClientOpenDeployment → UIStateRosterDeployment queued (client squad pick)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer] GeoModalDisplay.TryClientOpenDeployment best-effort failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// CLIENT: close the squad-pick deployment window after the launch relay was sent — the native
        /// ToPreviousScreen path MINUS <c>_mission.Cancel()</c> (UIStateRosterDeployment.cs:256-268:
        /// ResetViewState + FinishQueriedState; our push always uses shouldResetStateOnReturn=true).
        /// Best-effort: a miss leaves the window up until the host's tac-entry teardown covers it.
        /// </summary>
        public static void CloseDeployment(GeoRuntime rt)
        {
            try
            {
                Ensure();
                var view = GetView(rt);
                if (view == null) return;
                _resetViewState?.Invoke(view, new object[] { null });
                _finishQueriedState?.Invoke(view, null);
                Debug.Log("[Multiplayer] GeoModalDisplay.CloseDeployment → ResetViewState + FinishQueriedState");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoModalDisplay.CloseDeployment best-effort failed: " + ex.Message); }
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

        // ─── degraded mission-brief notice (ActiveMissionBrief rebuild failure) ──────────────────────────
        // Native simple text prompt via GameUtl.GetMessageBox().ShowSimplePrompt(...) — the SAME surface the
        // mod's session UI uses (MultiplayerUI.cs). Reflection-bound to keep this file's no-compile-time-game-
        // reference discipline. NOTIFY-ONLY by design (spec Batch-1): the client may dismiss it locally — the
        // protection against racing the host's pending decision is the HOST-side intent gate
        // (HostBlockingPromptGate, armed at the 0x69 SHOW), not this window.
        private static bool _mbEnsured;
        private static MethodInfo _getMessageBox;        // Base.Core.GameUtl.GetMessageBox() static
        private static MethodInfo _showSimplePrompt;     // MessageBox.ShowSimplePrompt(string, icon, buttons, callback, sender, userData)
        private static object _mbIconWarning;            // MessageBoxIcon.Warning
        private static object _mbButtonsOk;              // MessageBoxButtons.OK

        private static void EnsureMessageBox()
        {
            if (_mbEnsured) return;
            _mbEnsured = true;
            try
            {
                var gameUtlT = AccessTools.TypeByName("Base.Core.GameUtl");
                var mbT = AccessTools.TypeByName("Base.UI.MessageBox.MessageBox");
                var iconT = AccessTools.TypeByName("Base.UI.MessageBox.MessageBoxIcon");
                var buttonsT = AccessTools.TypeByName("Base.UI.MessageBox.MessageBoxButtons");
                var callbackT = AccessTools.TypeByName("Base.UI.MessageBox.MessageBoxCallback");
                if (gameUtlT == null || mbT == null || iconT == null || buttonsT == null || callbackT == null) return;
                _getMessageBox = AccessTools.Method(gameUtlT, "GetMessageBox", Type.EmptyTypes);
                // EXACT param match (harmony-accesstools-exact-param-match): the 6-arg simple overload
                // (content, icon, buttons, callback, sender, userData) — not the override-labels 7-arg one.
                _showSimplePrompt = AccessTools.Method(mbT, "ShowSimplePrompt",
                    new[] { typeof(string), iconT, buttonsT, callbackT, typeof(object), typeof(object) });
                _mbIconWarning = Enum.Parse(iconT, "Warning");
                _mbButtonsOk = Enum.Parse(buttonsT, "OK");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoModalDisplay.EnsureMessageBox failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: the mirrored ActiveMissionBrief could NOT be rebuilt (site/mission unresolved, class
        /// mismatch, or the always-degrading fallback-34 family) — show the DEGRADED notify-only native text
        /// prompt instead of silently dropping the host's blocking brief. The user learns WHY the session is
        /// paused; dismissing it is local-only (null-safe callback) and mutates nothing. The host intent gate
        /// stays armed until the host resolves (0x6C). Best-effort: a MessageBox miss just logs.
        /// </summary>
        public static void ShowDegradedBriefNotice(byte modalType)
            => ShowNoticePrompt(
                "The host is deciding on a mission briefing.\n" +
                "This briefing could not be displayed on your side; actions are paused until the host decides.",
                "ShowDegradedBriefNotice modalType=" + modalType + " (host gate stays armed)");

        /// <summary>
        /// Client: the host opened/resolved an aerial INTERCEPTION (WA-3 gap 5c; ModalType 32/33). The native
        /// windows are decompile-verified unbuildable client-side (live aircraft objects — see
        /// <see cref="ReportModalClassifier"/> INTERCEPTION FAMILY), so this is the SAME notify-only degrade
        /// path as the brief notice, with interception-specific text: <paramref name="pending"/> (brief 32) =
        /// host is choosing intercept/disengage while its geoscape is paused (the host intent gate is armed);
        /// resolved (outcome 33) = the air battle finished — loot rides the wallet rail, hull damage rides the
        /// 0xA6 HP tail. Dismissing is local-only either way. Best-effort: a MessageBox miss just logs.
        /// </summary>
        public static void ShowInterceptionNotice(byte modalType, bool pending)
            => ShowNoticePrompt(
                pending
                    ? "The host is resolving an aerial interception.\n" +
                      "Actions are paused until the host decides."
                    : "The host resolved an aerial interception.\n" +
                      "Any salvage and aircraft damage are already reflected on your side.",
                "ShowInterceptionNotice modalType=" + modalType + " pending=" + pending);

        /// <summary>
        /// Client: the host received the pandoran-evolution INTEL report (AlienResearchBrief 23, gap AC). The
        /// native window is decompile-verified unbuildable client-side (the bind reads the live
        /// <c>GeoscapeViewContext.Input</c> — AlienResearchBriefDataBind.cs:250, NRE on a null Context — plus
        /// live ALIEN-faction ResearchElements for its 3D mutation carousel, and the client's alien research
        /// sim is not mirrored), so this is the SAME notify-only degrade path as the interception pair.
        /// NON-blocking report: dismissing is local-only; the diplomacy penalty the report carries is already
        /// mirrored via the diplomacy channel (#4). Best-effort: a MessageBox miss just logs.
        /// </summary>
        public static void ShowIntelReportNotice(byte modalType)
            => ShowNoticePrompt(
                "The host received an intel report: the Pandoran threat is evolving.\n" +
                "Any diplomatic fallout is already reflected on your side.",
                "ShowIntelReportNotice modalType=" + modalType);

        /// <summary>
        /// Client: the HOST's campaign ended (feat-campaign-end) but the native outro replay FAILED —
        /// degrade to the notify-only prompt (ReportMirrorGate degrade-to-notify precedent) with the ending
        /// kind; the caller then returns to the main menu (notice ALWAYS precedes the teardown —
        /// <c>CampaignEndFlow.ClientSteps</c> pinned ordering). Best-effort: a MessageBox miss just logs.
        /// </summary>
        public static void ShowCampaignEndNotice(bool victory)
            => ShowNoticePrompt(
                victory
                    ? "The campaign has been WON.\n" +
                      "The co-op session is over — returning to the main menu."
                    : "The campaign has been LOST.\n" +
                      "The co-op session is over — returning to the main menu.",
                "ShowCampaignEndNotice victory=" + victory);

        /// <summary>Shared notify-only native text prompt (GameUtl.GetMessageBox().ShowSimplePrompt) — the
        /// degrade surface every unbuildable mirrored window funnels through. Never throws.</summary>
        private static void ShowNoticePrompt(string text, string logTag)
        {
            try
            {
                EnsureMessageBox();
                if (_getMessageBox == null || _showSimplePrompt == null) return;
                var mb = _getMessageBox.Invoke(null, null);
                if (mb == null) return;
                _showSimplePrompt.Invoke(mb, new object[] { text, _mbIconWarning, _mbButtonsOk, null, null, null });
                Debug.Log("[Multiplayer] GeoModalDisplay." + logTag + " → notify-only text prompt");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoModalDisplay notice (" + logTag + ") failed: " + ex.Message); }
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
