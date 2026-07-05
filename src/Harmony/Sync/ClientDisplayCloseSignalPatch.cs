using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// CLIENT close signal for the Batch-3 P4 unified display sequencer — a pure-observe Postfix on
    /// <c>UIModuleModal.Hide(ModalType)</c>, the ONE chokepoint EVERY modal close funnels through
    /// (<c>UIStateGeoModal.ExitState → _modalModule.Hide(_modal)</c>, the same guarantee
    /// <c>BlockingModalHideReleasePatch</c> relies on host-side). Both close origins of a mirrored report
    /// window land here: the host's 0x6C (<c>CloseBlocking → FinishQueriedState → ExitState → Hide</c>) AND a
    /// local OK on a non-blocking mirrored report (null DialogCallback → plain native close). The postfix
    /// forwards the hidden ModalType to <c>SyncEngine.OnClientModalClosed</c>, which frees the unified queue's
    /// single slot ONLY when the hidden type matches the Report display currently occupying it — an unrelated
    /// native window's close never releases someone else's slot. Client + active session + gate only;
    /// share-target with the existing Hide patches is safe (Harmony chains postfixes; all are read-only).
    /// </summary>
    [HarmonyPatch]
    public static class ClientDisplayCloseSignalPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = BlockingModalClientLock.ResolveModuleMethod("Hide", withHandlerAndData: false);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 = ModalType (boxed).
        public static void Postfix(object __0)
        {
            try
            {
                if (!DisplaySequencerGate.Enabled) return;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return;
                engine.Sync?.OnClientModalClosed(Convert.ToInt32(__0));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ClientDisplayCloseSignalPatch failed: " + ex.Message); }
        }
    }
}
