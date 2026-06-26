using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.State;
using UnityEngine;

namespace Multipleer.Harmony.Sync
{
    /// <summary>
    /// Host-authoritative geoscape REPORT-WINDOW mirror (Phase-A; additive, behind <see cref="ReportMirrorGate"/>,
    /// default OFF). Two patch classes on the single chokepoint both report openers funnel through —
    /// <c>GeoscapeView.OpenModalPersistent(ModalType,object,int)</c> and
    /// <c>GeoscapeView.OpenModal(ModalType,DialogCallback,object,int,bool,bool)</c>. Mirrors
    /// <see cref="EventRaisedDisplayPatch"/> exactly:
    ///   • HOST Postfix → if the opened modal is a whitelisted report (<see cref="ReportModalClassifier"/>),
    ///     broadcast it (<c>SyncEngine.BroadcastReportModal</c>); clients reconstruct + show the SAME modal.
    ///   • CLIENT Prefix → suppress the LOCAL open of a whitelisted report (the client mirrors only the host's),
    ///     a belt to the existing <c>SuppressEvents</c>-gated openers.
    /// BOTH directions gate on <c>ReportMirrorGate.Enabled</c> + active co-op session + <c>!IsApplying</c>
    /// (engine replay is never blocked nor re-broadcast — the same re-entrancy contract as EventRaised). With the
    /// gate OFF the Postfix broadcasts nothing and the Prefix suppresses nothing → byte-for-byte unchanged.
    /// All best-effort try/catch; on any failure native runs (fail-open).
    ///
    /// Args are taken positionally as boxed objects (<c>__0</c> = ModalType enum, etc.) so the mod needs NO
    /// compile-time game-enum reference — the same boxing-injection pattern used by
    /// <c>SuppressedAbilityViewClearPatch</c> for its StateStackAction enum arg.
    /// </summary>
    [HarmonyPatch]
    public static class OpenModalPersistentMirrorPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var viewT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var modalTypeT = AccessTools.TypeByName("PhoenixPoint.Common.Utils.ModalType");
            if (viewT == null || modalTypeT == null) return false;
            // EXACT param match (harmony-accesstools-exact-param-match): (ModalType, object, int).
            _target = AccessTools.Method(viewT, "OpenModalPersistent", new[] { modalTypeT, typeof(object), typeof(int) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 = modalType (ModalType enum, boxed). CLIENT: suppress the local open of a whitelisted report.
        public static bool Prefix(object __0) => ReportModalMirror.ClientShouldSuppress(__0);

        // __0 = modalType, __1 = modalData, __2 = priority. HOST: broadcast a whitelisted report.
        public static void Postfix(object __0, object __1, int __2) => ReportModalMirror.HostBroadcast(__0, __1, __2);
    }

    [HarmonyPatch]
    public static class OpenModalMirrorPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var viewT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var modalTypeT = AccessTools.TypeByName("PhoenixPoint.Common.Utils.ModalType");
            var dialogCbT = AccessTools.TypeByName("PhoenixPoint.Common.Utils.DialogCallback");
            if (viewT == null || modalTypeT == null || dialogCbT == null) return false;
            // EXACT param match: (ModalType, DialogCallback, object, int, bool, bool).
            _target = AccessTools.Method(viewT, "OpenModal",
                new[] { modalTypeT, dialogCbT, typeof(object), typeof(int), typeof(bool), typeof(bool) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 = modalType. CLIENT: suppress the local open of a whitelisted report.
        public static bool Prefix(object __0) => ReportModalMirror.ClientShouldSuppress(__0);

        // __0 = modalType, __2 = modalData, __3 = priority (__1 callback / __4,__5 flags ignored). HOST: broadcast.
        public static void Postfix(object __0, object __2, int __3) => ReportModalMirror.HostBroadcast(__0, __2, __3);
    }

    /// <summary>Shared host-broadcast / client-suppress logic for both chokepoint openers.</summary>
    internal static class ReportModalMirror
    {
        /// <summary>
        /// CLIENT Prefix body: return false (skip native) only when the gate is on, we are a client in an active
        /// co-op session, this is NOT an engine replay, and <paramref name="modalTypeBoxed"/> is a whitelisted
        /// report — so the client never raises a report the host didn't. Host / gate-off / failure → native runs.
        /// </summary>
        public static bool ClientShouldSuppress(object modalTypeBoxed)
        {
            try
            {
                if (!ReportMirrorGate.Enabled) return true;
                if (SyncApplyScope.IsApplying) return true;   // engine-driven client reconstruction → never block
                var engine = NetworkEngine.Instance;
                if (engine != null && engine.IsActiveSession && !engine.IsHost)
                {
                    int modalType = Convert.ToInt32(modalTypeBoxed);
                    if (ReportModalClassifier.IsReportModal(modalType))
                        return false;                          // client: no local report window for a mirrored type
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ReportModalMirror.ClientShouldSuppress failed: " + ex.Message); }
            return true;                                        // host (and any failure / gate-off): native runs
        }

        /// <summary>
        /// HOST Postfix body: if the gate is on and this host (active session) just opened a whitelisted report,
        /// classify + broadcast it to clients. Skips engine replays (<c>IsApplying</c>) so a reconstructed window
        /// is never re-broadcast. Non-report / decision modals are ignored (never broadcast something a client
        /// can't safely mirror).
        /// </summary>
        public static void HostBroadcast(object modalTypeBoxed, object modalData, int priority)
        {
            if (!ReportMirrorGate.Enabled) return;
            if (SyncApplyScope.IsApplying) return;            // never re-broadcast a reconstructed window
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            try
            {
                int modalType = Convert.ToInt32(modalTypeBoxed);
                if (!ReportModalClassifier.IsReportModal(modalType)) return;
                if (!ReportModalReflection.TryBuildPayload(modalType, modalData, priority, out var payload)) return;
                Debug.Log("[Multipleer] HOST BroadcastReportModal modalType=" + modalType + " variant=" + payload.Variant +
                          " siteId=" + payload.SiteId + " defId=" + payload.DefId + " extras=" + (payload.ExtraIds?.Count ?? 0) +
                          " shareLevel=" + payload.ShareLevel + " priority=" + payload.Priority);
                engine.Sync?.BroadcastReportModal(payload);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ReportModalMirror.HostBroadcast failed: " + ex.Message); }
        }
    }
}
