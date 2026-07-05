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
    /// HOST TRANSPARENCY (S1 invariant — do not break): on the host the Prefix is pure-observe — it returns TRUE
    /// (native runs) because <c>ClientShouldSuppress</c> only suppresses when <c>!IsHost</c>; the Postfix runs AFTER
    /// native and only READS the already-shown modalData via reflection to broadcast it. Neither path mutates the
    /// modal, its DialogCallback, its priority, or the ResearchElement, so the host's window (incl. the native
    /// "new research available" line, whose visibility is <c>ResearchElement.UnlocksResearches</c> — deterministic
    /// per def) is identical with the gate ON or OFF. Never move host work into the Prefix or mutate any arg here.
    ///
    /// CHANNEL OWNERSHIP (S3 invariant): this channel carries ONLY GeoscapeView modal openers (the
    /// <see cref="ReportModalClassifier"/> whitelist: reports 6/14/25/38 + the mirrored mission briefs
    /// 15/4/26/28). Geoscape EVENT windows are owned by the separate 0x65/0x66 event-replication channel and do
    /// NOT flow through GeoscapeView.OpenModal/ModalType at all (they push a state-stack state —
    /// UIStateGeoscapeEvent — and have no ModalType entry), so 0x69 can never carry an event window and the two
    /// channels cannot double-show. The tight whitelist enforces this; keep event types out of it.
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
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalMirror.ClientShouldSuppress failed: " + ex.Message); }
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
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            try
            {
                int modalType = Convert.ToInt32(modalTypeBoxed);
                // ARM the authoritative intent gate for a BLOCKING prompt (ambush/site-mission brief) BEFORE any
                // mirror gating: the host is natively modal-locked the instant this window opens, so in-flight
                // client intents must reject even if the client mirror is off or its payload read degrades.
                // Released in BlockingModalReleasePatch (ModalResultCallback — every close path funnels there).
                if (ReportModalClassifier.IsBlockingModal(modalType)) HostBlockingPromptGate.Arm(modalType);
                if (!ReportMirrorGate.Enabled) return;
                if (SyncApplyScope.IsApplying) return;        // never re-broadcast a reconstructed window
                if (!ReportModalClassifier.IsReportModal(modalType)) return;
                if (!ReportModalReflection.TryBuildPayload(modalType, modalData, priority, out var payload)) return;
                Debug.Log("[Multiplayer] HOST BroadcastReportModal modalType=" + modalType + " variant=" + payload.Variant +
                          " siteId=" + payload.SiteId + " defId=" + payload.DefId + " extras=" + (payload.ExtraIds?.Count ?? 0) +
                          " shareLevel=" + payload.ShareLevel + " priority=" + payload.Priority);
                engine.Sync?.BroadcastReportModal(payload);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalMirror.HostBroadcast failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// HOST release of the blocking-prompt lock. Postfix on the native modal-resolve funnel
    /// <c>GeoscapeView.ModalResultCallback(ModalType, ModalResult, object)</c> (GeoscapeView.cs:799) — EVERY
    /// close of an OpenModalPersistent window lands there (<c>UIStateGeoModal.FinishDialog</c> invokes the
    /// opener's handler; <c>ExitState</c> falls back to handler(Close)). For a BLOCKING modal (ambush or
    /// site-mission brief) it: (1) releases <see cref="HostBlockingPromptGate"/> so client intents relay again,
    /// and (2) broadcasts <c>ReportModalHide</c> so every client closes its mirrored view-locked copy — on
    /// Confirm the native LaunchMission already ran inside the callback and the tactical co-op deploy flow takes
    /// over as today; on CANCEL the mission is cancelled host-side and the explicit hide (not an exclusion)
    /// guarantees the client is never left with a lingering prompt (the 9e80b24 goal, kept).
    /// Host-only + active session; the client's mirrored modal has a NULL DialogCallback so this never fires
    /// there for it. Non-blocking modals are untouched (pure observe). Best-effort; reflective target.
    /// </summary>
    [HarmonyPatch]
    public static class BlockingModalReleasePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var viewT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var modalTypeT = AccessTools.TypeByName("PhoenixPoint.Common.Utils.ModalType");
            var modalResultT = AccessTools.TypeByName("PhoenixPoint.Common.Utils.ModalResult");
            if (viewT == null || modalTypeT == null || modalResultT == null) return false;
            // EXACT param match (harmony-accesstools-exact-param-match): (ModalType, ModalResult, object).
            _target = AccessTools.Method(viewT, "ModalResultCallback", new[] { modalTypeT, modalResultT, typeof(object) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 = modalType (ModalType enum, boxed); result/modalData not needed — ANY resolve releases.
        public static void Postfix(object __0)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                int modalType = Convert.ToInt32(__0);
                if (!ReportModalClassifier.IsBlockingModal(modalType)) return;
                HostBlockingPromptGate.Release(modalType);
                engine.Sync?.BroadcastReportModalHide((byte)modalType);
                Debug.Log("[Multiplayer] HOST blocking modal resolved modalType=" + modalType +
                          " → gate released + ReportModalHide broadcast");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BlockingModalReleasePatch failed: " + ex.Message); }
        }
    }
}
