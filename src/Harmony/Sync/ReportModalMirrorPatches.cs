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
    /// 15/4/26/28 + the ActiveMissionBrief family 0/2/11/20/34/36, Batch-1 P2 of the 2026-07-05 popup-mirror
    /// spec). Geoscape EVENT windows are owned by the separate 0x65/0x66 event-replication channel and do
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
                // EXCEPTION — INTERCEPTION (32): it is blocking for the hide/notice rails but must NOT arm the
                // all-intent gate. During an air-combat interception clients keep a fully usable geoscape (they
                // relay research/manufacturing/roster intents throughout); ONLY time control is locked, via the
                // dedicated InterceptionTimeLock window (opened here, closed on disengage / outcome-modal close).
                if (modalType == ReportModalClassifier.InterceptionBrief)
                    InterceptionTimeLock.Open();
                else if (ReportModalClassifier.IsBlockingModal(modalType))
                    HostBlockingPromptGate.Arm(modalType);
                if (!ReportMirrorGate.Enabled) return;
                if (SyncApplyScope.IsApplying) return;        // never re-broadcast a reconstructed window
                if (!ReportModalClassifier.IsReportModal(modalType)) return;
                // Batch-3 P4: consume the display-order stamp recorded by ViewSwitchQueryStampPatch when the
                // native OpenModal(Persistent) body pushed its UIStateGeoModal — same call stack, exact
                // native-queue seq. Fallback (forceOnTop/replaceTop bypassed QueryStateSwitch, or the stamp
                // patch is unbound): a fresh seq at open time keeps a consistent order.
                uint displaySeq = 0;
                if (DisplaySequencerGate.Enabled
                    && !Multiplayer.Network.Sync.State.DisplayStamp.TryTake("UIStateGeoModal", out displaySeq, out _))
                    displaySeq = Multiplayer.Network.Sync.State.DisplaySequence.NextSeq();
                // Research variant: this Postfix runs INSIDE the Research.OnResearchCompleted dispatch, BEFORE
                // the requirement subscribers flip dependent elements to Revealed/Unlocked — a payload read here
                // ships a stale-false "new research available" nav flag (the 35e996e regression: client force-hid
                // a line the host natively showed). Defer the build+broadcast to the next engine tick, where the
                // read matches what the host's own bind renders (ReportModalClassifier.ShouldDeferHostBroadcast).
                // The P4 stamp is captured NOW (queue time = native fire-time) and rides the deferred tuple.
                if (ReportModalClassifier.ShouldDeferHostBroadcast(modalType))
                {
                    engine.Sync?.QueueDeferredReportModal(modalType, modalData, priority, displaySeq);
                    Debug.Log("[Multiplayer] HOST report modalType=" + modalType +
                              " deferred to next tick (post-cascade nav-flag read) displaySeq=" + displaySeq);
                    return;
                }
                if (!ReportModalReflection.TryBuildPayload(modalType, modalData, priority, out var payload)) return;
                payload.DisplaySeq = displaySeq;
                // Co-op brief-on-all fix: a mission brief that opened because a vehicle ARRIVED at its site is
                // player-initiated UI — mirror it to the INITIATING peer ONLY, never the whole session. The
                // initiator was tagged at the travel order (relayed client → SyncEngine.OnActionRequest; host-own
                // → MoveVehiclePatch). A world/story brief (haven attacked, infestation, …) opens for a site
                // nobody traveled to → no tag → broadcast-to-all, unchanged. Host-own travel → tag == HostSelf →
                // the host already shows it natively (S1 transparency), so mirror nothing. The HostBlockingPromptGate
                // arm above is untouched (host is modal-locked regardless of who sees the mirror).
                if (ReportModalClassifier.IsVehicleArrivalBrief(modalType)
                    && VehicleTravelInitiator.TryConsume(payload.SiteId, out ulong initiator))
                {
                    if (initiator == VehicleTravelInitiator.HostSelf)
                    {
                        Debug.Log("[Multiplayer] HOST brief modalType=" + modalType + " siteId=" + payload.SiteId +
                                  " host-initiated travel → shown locally, not mirrored");
                        return;
                    }
                    Debug.Log("[Multiplayer] HOST brief modalType=" + modalType + " variant=" + payload.Variant +
                              " siteId=" + payload.SiteId + " → 0x69 to initiator peer=" + initiator + " ONLY (player-initiated UI)");
                    engine.Sync?.SendReportModalTo(initiator, payload);
                    return;
                }
                Debug.Log("[Multiplayer] HOST BroadcastReportModal modalType=" + modalType + " variant=" + payload.Variant +
                          " siteId=" + payload.SiteId + " defId=" + payload.DefId + " extras=" + (payload.ExtraIds?.Count ?? 0) +
                          " shareLevel=" + payload.ShareLevel + " priority=" + payload.Priority + " displaySeq=" + displaySeq);
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

        // __0 = modalType (ModalType enum, boxed); __1 = ModalResult (boxed) — read only for the interception
        // disengage branch. For the blocking-gate release ANY resolve releases.
        public static void Postfix(object __0, object __1)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                int modalType = Convert.ToInt32(__0);
                // INTERCEPTION TIME-LOCK close (path a — DISENGAGE): the brief resolving to a NON-Confirm result
                // (ModalResult.Confirm == 0) launches no minigame → the interception is fully over now, close the
                // window. A Confirm launches the air-combat coroutine; the window stays locked until the outcome
                // modal (33) closes (path b, BlockingModalHideReleasePatch).
                if (modalType == ReportModalClassifier.InterceptionBrief && Convert.ToInt32(__1) != 0)
                    InterceptionTimeLock.Close();
                if (!ReportModalClassifier.IsBlockingModal(modalType)) return;
                HostBlockingPromptGate.Release(modalType);
                engine.Sync?.BroadcastReportModalHide((byte)modalType);
                Debug.Log("[Multiplayer] HOST blocking modal resolved modalType=" + modalType +
                          " → gate released + ReportModalHide broadcast");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BlockingModalReleasePatch failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// HOST release BELT on <c>UIModuleModal.Hide(ModalType)</c> — covers the blocking briefs whose opener does
    /// NOT route their resolve through <c>GeoscapeView.ModalResultCallback</c>. Every ShowMissionBriefing brief
    /// gets the ModalResultCallback handler (the primary release above), but the haven-details "Defend" path
    /// opens the SAME GeoHavenAttackBrief=0 with its own callback (<c>HavenFacilityController.cs:126 →
    /// OpenModal(GeoHavenAttackBrief, OnDefendZoneResult, site.ActiveMission)</c>) — without this belt that
    /// window's close would never release the gate nor the client's mirrored copy (permanent lock). EVERY modal
    /// close funnels through <c>UIStateGeoModal.ExitState → _modalModule.Hide(_modal)</c> (UIStateGeoModal.cs:
    /// 118-121), so Hide is the one guaranteed chokepoint. Double-fire with the primary is safe by design:
    /// <c>HostBlockingPromptGate.Release</c> is match-gated and the client's <c>CloseBlocking</c> is idempotent
    /// (a second hide with nothing current is a logged no-op). Host-only + active session; the client's own
    /// Hide (mirror close / lock restore) is untouched. Reflective target (Prepare false → skipped on rename).
    /// </summary>
    [HarmonyPatch]
    public static class BlockingModalHideReleasePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = BlockingModalClientLock.ResolveModuleMethod("Hide", withHandlerAndData: false);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 = modalType (ModalType enum, boxed).
        public static void Postfix(object __0)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                int modalType = Convert.ToInt32(__0);
                // INTERCEPTION TIME-LOCK close (path b — INTERCEPT / AUTO-RESOLVE): the air-battle OUTCOME modal
                // (33, non-blocking) closing is the end of the minigame path → close the window (the next host
                // anchor carries Locked=false, re-enabling + re-greying time control everywhere). Placed before
                // the blocking-gate belt below (33 is not IsBlockingModal, so that belt ignores it).
                if (modalType == ReportModalClassifier.InterceptionOutcome) InterceptionTimeLock.Close();
                if (!ReportModalClassifier.IsBlockingModal(modalType)) return;
                if (!HostBlockingPromptGate.IsArmed) return;   // already released by the primary → silent no-op
                HostBlockingPromptGate.Release(modalType);
                engine.Sync?.BroadcastReportModalHide((byte)modalType);
                Debug.Log("[Multiplayer] HOST blocking modal hidden (belt) modalType=" + modalType +
                          " → gate released + ReportModalHide broadcast");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BlockingModalHideReleasePatch failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// HOST interception time-lock CLOSE — primary/robust path. Postfix on
    /// <c>InterceptionGameController.GameStopped()</c> (PhoenixPoint.Geoscape.Interception), which the interception
    /// coroutine's <c>OnStop</c> delegate invokes on EVERY stop (GeoLevelController.cs:1210-1213) — the normal
    /// intercept/auto-resolve end AND the ABORT path where the START autosave throws and StartInterceptionCrt bails
    /// before <c>ShowInterceptionResult</c> (GeoLevelController.cs:1237 guard) so outcome modal 33 never shows.
    /// Without this the abort path would leave the shared clock locked for the whole session. The outcome-33 Hide
    /// close (BlockingModalHideReleasePatch) stays as a belt; both call Close() idempotently. Host-only + active
    /// session; reflective target (Prepare false → skipped on rename).
    /// </summary>
    [HarmonyPatch]
    public static class InterceptionGameStoppedTimeLockPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Interception.InterceptionGameController");
            if (t == null) return false;
            _target = AccessTools.Method(t, "GameStopped", Type.EmptyTypes);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                InterceptionTimeLock.Close();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] InterceptionGameStoppedTimeLockPatch failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// HOST interception time-lock CLOSE — ABORT belt for the first-interception null-field NRE (review of 48f50e8).
    /// The primary close (<see cref="InterceptionGameStoppedTimeLockPatch"/>) postfixes
    /// <c>InterceptionGameController.GameStopped</c>, but the coroutine invokes it as <c>_interceptionGame.GameStopped()</c>
    /// (GeoLevelController.cs:1213) on a FIELD that is still NULL on the FIRST interception of a session when it
    /// aborts via the start-autosave throw: <c>StartInterceptionCrt</c> bails at its <c>ex.Value</c> guard
    /// (GeoLevelController.cs:1243) BEFORE ever touching the lazy <c>InterceptionGame</c> property that populates
    /// <c>_interceptionGame</c> (:326-330), so the call NREs on the null receiver and the GameStopped postfix NEVER
    /// runs → the shared clock stays locked for the whole session (until a menu/reload reset belt).
    ///
    /// This closes the lock on the ONE call the abort path always makes:
    /// <c>PhoenixSaveManager.ShowAutosaveError(Exception)</c> (GeoLevelController.cs:1239). It cannot miss the
    /// null-field case (it fires from the coroutine BEFORE the OnStop NRE). Idempotent + host-only: the only
    /// autosave that can FAIL while the lock is OPEN is the interception-start one (the brief is modal until the
    /// player confirms, and saving is disabled through the minigame), so a ShowAutosaveError under an open lock is
    /// exactly the abort; every other ShowAutosaveError site just closes an already-closed lock (a no-op). The
    /// GameStopped postfix + the outcome-33 Hide close stay as belts (all call <c>Close()</c> idempotently).
    /// Reflective target (Prepare false → PatchAll skips on rename).
    /// </summary>
    [HarmonyPatch]
    public static class InterceptionAutosaveAbortTimeLockPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.Saves.PhoenixSaveManager");
            if (t == null) return false;
            _target = AccessTools.Method(t, "ShowAutosaveError", new[] { typeof(Exception) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                InterceptionTimeLock.Close();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] InterceptionAutosaveAbortTimeLockPatch failed: " + ex.Message); }
        }
    }
}
