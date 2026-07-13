using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// CLIENT handling of a mirrored blocking mission brief (report-mirror rail, 0x69). Two layers:
    ///
    /// VIEW-LOCK (MANDATORY briefs only — ambush 15 / base defense 11 / ancient defence 28,
    /// <c>ReportModalClassifier.IsMandatoryBrief</c>; native single-player semantics: the fullscreen mandatory
    /// prompt blocks everything until the mission starts). OPTIONAL mirrored briefs (scavenge 4, ancient
    /// attack 26, ActiveMissionBrief family) are NOT locked — their native CLOSE performs a pure local dismiss
    /// (null mirror DialogCallback; the HOST intent gate, armed for ALL blocking briefs, alone prevents racing
    /// the host's decision — soak 2026-07-05: the wider Batch-1 lock left the scavenge brief's CLOSE dead).
    ///   • <see cref="BlockingModalFinishDialogLockPatch"/> — swallow <c>UIStateGeoModal.FinishDialog</c>
    ///     (every modal button — Confirm/Close/Cancel — routes there via <c>UIModal._handler</c>) for a
    ///     mandatory mirror: no local close/skip (release is host-driven).
    ///   • <see cref="BlockingModalCancelLockPatch"/> — swallow <c>UIStateGeoModal.OnCancel</c> (the Esc/back
    ///     path calls <c>FinishQueriedState</c> DIRECTLY, bypassing FinishDialog) so the window is not skippable.
    ///
    /// BEGIN-MISSION RELAY (2026-07-13, ALL mirrored mission briefs): a client CONFIRM in FinishDialog no
    /// longer dead-ends — <see cref="BlockingModalClientLock.TryRelayBeginMission"/> sends
    /// <c>MissionStartRequestAction(modalType, siteId)</c> to the host, which drives its OWN open brief through
    /// the native FinishDialog(Confirm) → LaunchMission path; the geo→tac co-op deploy flow then carries every
    /// peer in. On a MANDATORY mirror the window stays open (native swallowed) until the host's hide/transition
    /// lands; on an OPTIONAL mirror the native local pop still runs after the relay (simple, idempotent — the
    /// host's later ReportModalHide finds it already closed). The former CanvasGroup button-GREY
    /// (BlockingModalButtonGreyPatch/RestorePatch) is DELETED: it made the confirm click physically impossible.
    ///
    /// RELEASE: only the engine closes a mandatory mirror — host resolve → <c>ReportModalHide</c> →
    /// <c>GeoModalDisplay.CloseBlocking</c> → <c>FinishQueriedState</c> (bypasses both swallowed methods), or
    /// the geoscape→tactical transition tears the whole view down (host/relayed confirm → co-op deploy flow).
    /// ORIGIN CONTRACT (2026-07-05): lock AND relay apply ONLY to a window the MIRROR showed
    /// (<see cref="Multiplayer.Network.Sync.State.BlockingModalMirrorRegistry"/>, tagged by
    /// <c>GeoModalDisplay.Show</c>, cleared by every <c>ReportModalHide</c>): a client-NATIVE blocking-type
    /// window keeps fully native buttons and never relays a phantom start, and a mirror window whose hide
    /// landed before it entered (queued-show race) comes up unlocked and locally closeable.
    /// Decisions are pure (<see cref="BlockingModalLockDecision"/>, unit-tested); the HOST is NEVER touched
    /// (host transparency) and every patch fails OPEN (unreadable state → native runs). Reflective targets
    /// (Prepare false → PatchAll skips) so an engine rename never bombs.
    /// </summary>
    [HarmonyPatch]
    public static class BlockingModalFinishDialogLockPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var stateT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoModal");
            var modalResultT = AccessTools.TypeByName("PhoenixPoint.Common.Utils.ModalResult");
            if (stateT == null || modalResultT == null) return false;
            // EXACT param match (harmony-accesstools-exact-param-match): FinishDialog(ModalResult).
            _target = AccessTools.Method(stateT, "FinishDialog", new[] { modalResultT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the UIStateGeoModal; __0 = the clicked ModalResult (boxed; Confirm = 0).
        // A client CONFIRM on a mirrored mission brief relays the begin-mission intent to the host (works for
        // both locked-mandatory and optional mirrors); the native body is skipped only per the pure lock
        // decision (mandatory mirror → stays open awaiting the host; optional mirror → native local pop runs).
        public static bool Prefix(object __instance, object __0)
        {
            BlockingModalClientLock.TryRelayBeginMission(__instance, __0);
            return !BlockingModalClientLock.ShouldBlock(__instance);
        }
    }

    [HarmonyPatch]
    public static class BlockingModalCancelLockPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var stateT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoModal");
            if (stateT == null) return false;
            _target = AccessTools.Method(stateT, "OnCancel", Type.EmptyTypes);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Esc/back on the client's mirrored blocking modal → inert (native would FinishQueriedState).
        public static bool Prefix(object __instance)
            => !BlockingModalClientLock.ShouldBlock(__instance);
    }

    /// <summary>Shared reflection glue for the client blocking-modal lock + begin-mission relay
    /// (game-bound; decisions are pure).</summary>
    internal static class BlockingModalClientLock
    {
        private static bool _ensured;
        private static PropertyInfo _stateModalTypeProp;   // UIStateGeoModal.ModalType (public property, :63)
        private static PropertyInfo _stateModalDataProp;   // UIStateGeoModal.ModalData (public property, :61)

        private static void Ensure()
        {
            if (_ensured) return;
            _ensured = true;
            var stateT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoModal");
            if (stateT != null)
            {
                _stateModalTypeProp = AccessTools.Property(stateT, "ModalType");
                _stateModalDataProp = AccessTools.Property(stateT, "ModalData");
            }
        }

        /// <summary>Resolve <c>UIModuleModal.Show(ModalType, DialogCallback, object)</c> / <c>Hide(ModalType)</c>
        /// with EXACT param matches (shared by the report-mirror Show/Hide belts). Null → callers self-skip
        /// (Prepare false).</summary>
        internal static MethodBase ResolveModuleMethod(string name, bool withHandlerAndData)
        {
            var moduleT = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleModal");
            var modalTypeT = AccessTools.TypeByName("PhoenixPoint.Common.Utils.ModalType");
            var dialogCbT = AccessTools.TypeByName("PhoenixPoint.Common.Utils.DialogCallback");
            if (moduleT == null || modalTypeT == null || (withHandlerAndData && dialogCbT == null)) return null;
            return withHandlerAndData
                ? AccessTools.Method(moduleT, name, new[] { modalTypeT, dialogCbT, typeof(object) })
                : AccessTools.Method(moduleT, name, new[] { modalTypeT });
        }

        /// <summary>
        /// The live gate for both close-path prefixes: read the state's ModalType and feed the PURE
        /// <see cref="BlockingModalLockDecision.ShouldBlockLocalClose"/>. Fail OPEN (false → native runs) on any
        /// read failure — never brick a non-blocking window.
        /// </summary>
        internal static bool ShouldBlock(object uiStateGeoModal)
        {
            try
            {
                Ensure();
                if (uiStateGeoModal == null || _stateModalTypeProp == null) return false;
                int modalType = Convert.ToInt32(_stateModalTypeProp.GetValue(uiStateGeoModal, null));
                var engine = NetworkEngine.Instance;
                return BlockingModalLockDecision.ShouldBlockLocalClose(
                    engine != null && engine.IsHost,
                    engine != null && engine.IsActiveSession,
                    SyncApplyScope.IsApplying,
                    ReportModalClassifier.IsMandatoryBrief(modalType),   // optional brief → native CLOSE stays live
                    BlockingModalMirrorRegistry.IsMirrorShown(modalType));   // native-origin / already-hidden → native close runs
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] BlockingModalClientLock.ShouldBlock failed: " + ex.Message);
                return false;   // fail OPEN: native close runs (never a stuck window from a read failure)
            }
        }

        /// <summary>
        /// CLIENT begin-mission relay: if this FinishDialog invocation is a CONFIRM click on a MIRROR-shown
        /// mission brief (pure <see cref="BlockingModalLockDecision.ShouldRelayBeginMission"/>), send
        /// <c>MissionStartRequestAction(modalType, siteId)</c> to the host — the host validates against ITS
        /// open brief and drives the native FinishDialog(Confirm) → LaunchMission path. The site identity is
        /// read off the mirror's rebuilt <c>ModalData</c> mission (<c>Site.SiteId</c> — the same stable id the
        /// 0x69 mirror shipped); an unreadable site degrades to -1 (host then matches on modalType alone).
        /// Purely additive observe — never blocks/alters the caller's own flow; best-effort (never throws).
        /// </summary>
        internal static void TryRelayBeginMission(object uiStateGeoModal, object modalResultBoxed)
        {
            try
            {
                Ensure();
                if (uiStateGeoModal == null || _stateModalTypeProp == null || modalResultBoxed == null) return;
                int modalType = Convert.ToInt32(_stateModalTypeProp.GetValue(uiStateGeoModal, null));
                var engine = NetworkEngine.Instance;
                if (!BlockingModalLockDecision.ShouldRelayBeginMission(
                        engine != null && engine.IsHost,
                        engine != null && engine.IsActiveSession,
                        SyncApplyScope.IsApplying,
                        Convert.ToInt32(modalResultBoxed) == 0,                    // ModalResult.Confirm = 0
                        ReportModalClassifier.IsMissionBrief(modalType),
                        BlockingModalMirrorRegistry.IsMirrorShown(modalType))) return;
                object mission = _stateModalDataProp?.GetValue(uiStateGeoModal, null);
                int siteId = Multiplayer.Network.Sync.State.ReportModalReflection.GetMissionSiteId(mission);

                // SQUAD PICK ON THE INITIATOR (2026-07-13): open the NATIVE deployment window on THIS client
                // instead of relaying immediately — the relay (now carrying the picked GeoUnitIds) is sent by
                // DeploySquadRelayPatch when the user clicks DEPLOY. Clearing the mirror tag here releases the
                // mandatory-mirror lock, so the native FinishDialog pop runs (null mirror DialogCallback =
                // side-effect-free) and the queued deployment state becomes current.
                if (mission != null && ClientDeployRelay.TryBeginLocalSquadPick((byte)modalType, siteId, mission))
                {
                    BlockingModalMirrorRegistry.ClearMirrorShown(modalType);
                    Debug.Log("[Multiplayer] CLIENT begin-mission confirm modalType=" + modalType + " siteId=" + siteId +
                              " → local squad pick opened (relay deferred to DEPLOY)");
                    return;
                }

                // Degraded fallback (window open failed / no mission readable): legacy immediate relay —
                // the deployment window then opens on the HOST, exactly the pre-feature behavior.
                Debug.Log("[Multiplayer] CLIENT begin-mission relay modalType=" + modalType + " siteId=" + siteId +
                          " → MissionStartRequest sent to host");
                engine.Sync?.SendActionRequest(
                    new Multiplayer.Network.Sync.Actions.MissionStartRequestAction((byte)modalType, siteId));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BlockingModalClientLock.TryRelayBeginMission failed: " + ex.Message); }
        }
    }
}
