using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// CLIENT view-lock for a mirrored BLOCKING report modal (the mandatory ambush brief, GeoAmbushBrief=15 —
    /// <c>ReportModalClassifier.IsBlockingModal</c>). Native single-player semantics: the fullscreen ambush
    /// prompt blocks EVERYTHING until the host starts the mission. The client renders the SAME native modal
    /// (report-mirror rail, 0x69) but as a mirror of a HOST-pending decision it must be fully inert:
    ///   • <see cref="BlockingModalFinishDialogLockPatch"/> — swallow <c>UIStateGeoModal.FinishDialog</c>
    ///     (every modal button — Confirm/Close/Cancel — routes there via <c>UIModal._handler</c>): the client's
    ///     "begin mission" click does NOTHING (mission start is host-authoritative).
    ///   • <see cref="BlockingModalCancelLockPatch"/> — swallow <c>UIStateGeoModal.OnCancel</c> (the Esc/back
    ///     path calls <c>FinishQueriedState</c> DIRECTLY, bypassing FinishDialog) so the window is not skippable.
    ///   • <see cref="BlockingModalButtonGreyPatch"/> / <see cref="BlockingModalButtonRestorePatch"/> — visual
    ///     inertness: CanvasGroup.interactable=false on the shown UIModal (the same one-toggle grey the event-
    ///     dialog client lock uses), restored on Hide because the modal prefab instance is REUSED across shows
    ///     (a host-migrated ex-client must not inherit a dead button).
    /// RELEASE: only the engine closes this window — host resolve → <c>ReportModalHide</c> →
    /// <c>GeoModalDisplay.CloseBlocking</c> → <c>FinishQueriedState</c> (bypasses both swallowed methods), or
    /// the geoscape→tactical transition tears the whole view down (host confirm → co-op deploy flow).
    /// ORIGIN CONTRACT (2026-07-05): the lock applies ONLY to a window the MIRROR showed
    /// (<see cref="Multiplayer.Network.Sync.State.BlockingModalMirrorRegistry"/>, tagged by
    /// <c>GeoModalDisplay.Show</c>, cleared by every <c>ReportModalHide</c>): a client-NATIVE blocking-type
    /// window keeps fully native buttons (no host hide would ever release it), and a mirror window whose hide
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

        // __instance = the UIStateGeoModal; skip native (return false) only per the pure lock decision.
        public static bool Prefix(object __instance)
            => !BlockingModalClientLock.ShouldBlock(__instance);
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

    /// <summary>
    /// Visual inertness: when the CLIENT's modal module shows a blocking modal, grey + input-block its buttons
    /// via a CanvasGroup on the UIModal root (interactable=false; alpha/blocksRaycasts untouched — the window
    /// itself stays fully readable and keeps swallowing clicks). Postfix on
    /// <c>UIModuleModal.Show(ModalType, DialogCallback, object)</c> (the single entry
    /// <c>UIStateGeoModal.EnterState</c> uses, UIModuleModal.cs:47).
    /// </summary>
    [HarmonyPatch]
    public static class BlockingModalButtonGreyPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = BlockingModalClientLock.ResolveModuleMethod("Show", withHandlerAndData: true);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = UIModuleModal, __0 = ModalType (boxed).
        public static void Postfix(object __instance, object __0)
        {
            try
            {
                int modalType = Convert.ToInt32(__0);
                var engine = NetworkEngine.Instance;
                if (!BlockingModalLockDecision.ShouldGreyButtons(
                        engine != null && engine.IsHost,
                        engine != null && engine.IsActiveSession,
                        ReportModalClassifier.IsBlockingModal(modalType),
                        BlockingModalMirrorRegistry.IsMirrorShown(modalType))) return;   // native-origin / already-hidden → never greyed
                BlockingModalClientLock.SetModalInteractable(__instance, modalType, false);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BlockingModalButtonGreyPatch failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Symmetric restore on <c>UIModuleModal.Hide(ModalType)</c>: the modal GameObject is POOLED (one instance
    /// per ModalType, re-Shown next time), so a CanvasGroup left non-interactable would permanently kill the
    /// window for a later HOST use (host migration / session end → native ambush). Restores UNCONDITIONALLY for
    /// a blocking type (host or client, in or out of session) — interactable=true is the native default.
    /// </summary>
    [HarmonyPatch]
    public static class BlockingModalButtonRestorePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = BlockingModalClientLock.ResolveModuleMethod("Hide", withHandlerAndData: false);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = UIModuleModal, __0 = ModalType (boxed).
        public static void Postfix(object __instance, object __0)
        {
            try
            {
                int modalType = Convert.ToInt32(__0);
                if (!ReportModalClassifier.IsBlockingModal(modalType)) return;
                BlockingModalClientLock.SetModalInteractable(__instance, modalType, true);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BlockingModalButtonRestorePatch failed: " + ex.Message); }
        }
    }

    /// <summary>Shared reflection glue for the client blocking-modal lock (game-bound; decisions are pure).</summary>
    internal static class BlockingModalClientLock
    {
        private static bool _ensured;
        private static PropertyInfo _stateModalTypeProp;   // UIStateGeoModal.ModalType (public property, :63)
        private static FieldInfo _availableModalsField;    // UIModuleModal.AvailableModals (List<ModalData>)
        private static FieldInfo _modalDataTypeField;      // ModalData.Type (ModalType)
        private static FieldInfo _modalDataModalField;     // ModalData.Modal (UIModal : MonoBehaviour)

        private static void Ensure()
        {
            if (_ensured) return;
            _ensured = true;
            var stateT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoModal");
            if (stateT != null) _stateModalTypeProp = AccessTools.Property(stateT, "ModalType");
            var moduleT = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleModal");
            if (moduleT != null) _availableModalsField = AccessTools.Field(moduleT, "AvailableModals");
            var dataT = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleModal+ModalData");
            if (dataT == null && moduleT != null) dataT = AccessTools.Inner(moduleT, "ModalData");
            if (dataT != null)
            {
                _modalDataTypeField = AccessTools.Field(dataT, "Type");
                _modalDataModalField = AccessTools.Field(dataT, "Modal");
            }
        }

        /// <summary>Resolve <c>UIModuleModal.Show(ModalType, DialogCallback, object)</c> / <c>Hide(ModalType)</c>
        /// with EXACT param matches. Null → the visual patches self-skip (Prepare false).</summary>
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
                    ReportModalClassifier.IsBlockingModal(modalType),
                    BlockingModalMirrorRegistry.IsMirrorShown(modalType));   // native-origin / already-hidden → native close runs
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] BlockingModalClientLock.ShouldBlock failed: " + ex.Message);
                return false;   // fail OPEN: native close runs (never a stuck window from a read failure)
            }
        }

        /// <summary>
        /// Toggle the pooled UIModal instance for <paramref name="modalType"/>: CanvasGroup (get-or-add on the
        /// modal root) interactable flag only — child Selectables render their native disabled grey; alpha and
        /// blocksRaycasts stay native (window readable, clicks still land on the modal, not through it).
        /// </summary>
        internal static void SetModalInteractable(object module, int modalType, bool interactable)
        {
            try
            {
                Ensure();
                if (module == null || _availableModalsField == null
                    || _modalDataTypeField == null || _modalDataModalField == null) return;
                if (!(_availableModalsField.GetValue(module) is IEnumerable modals)) return;
                foreach (var entry in modals)
                {
                    if (entry == null || Convert.ToInt32(_modalDataTypeField.GetValue(entry)) != modalType) continue;
                    var modal = _modalDataModalField.GetValue(entry) as Component;
                    if (modal == null) return;
                    // Grey (false) get-or-adds the group; restore (true) is GetComponent-only — if we never
                    // greyed, the host's pristine modal is left byte-for-byte untouched (host transparency).
                    var cg = modal.gameObject.GetComponent<CanvasGroup>();
                    if (cg == null)
                    {
                        if (interactable) return;
                        cg = modal.gameObject.AddComponent<CanvasGroup>();
                    }
                    cg.interactable = interactable;
                    Debug.Log("[Multiplayer] BlockingModalClientLock modalType=" + modalType +
                              " interactable=" + interactable + " (mirror view-lock)");
                    return;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BlockingModalClientLock.SetModalInteractable failed: " + ex.Message); }
        }
    }
}
