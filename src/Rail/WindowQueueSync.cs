using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PEER AUTONOMY for the geoscape WINDOW QUEUE (surface 0xB9, law 1 Intent) — the user mandate
    /// "if the host is AFK for an hour the remaining players must still open and resolve every window;
    /// if one player is left, they can play for everyone".
    ///
    /// THE BLOCK, from the game's own code. <c>GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch</c>:58-63
    /// dequeues ONLY while <c>_currentStateSwitchRequest == null</c>, and the single writer that clears that
    /// field is <c>FinishCurrentStateSwitch</c>:116 — i.e. a HOST CLICK, nothing else. Every pushed window
    /// also carries <c>PauseGame = true</c> (GeoscapeView.cs:861/:881 and every other queueing raiser), which
    /// on the host is the AUTHORITATIVE clock (the TimeAnchor mirrors it to every peer). So ONE un-dismissed
    /// window on an idle host freezes the shared campaign AND wedges the host's window queue for the rest of
    /// the session — every later window piles up behind it forever. That is not a co-op inconvenience, it is
    /// the whole game stopped by one peer walking away.
    ///
    /// THE SEAM IS THE GAME'S OWN CHOKEPOINT, at two depths and no more:
    ///   • the current queued state is a <c>UIStateGeoModal</c> ⇒ <c>FinishDialog(result)</c>
    ///     (UIStateGeoModal.cs:82), which itself calls <c>FinishQueriedState</c> and THEN invokes the host's
    ///     own <c>DialogCallback</c>. That closure is the one built at <c>GeoscapeView.OpenModalPersistent</c>
    ///     :848 over the HOST's own <c>modalData</c>, so <c>ModalResultCallback</c>:799 runs on the host with
    ///     host objects: a mission brief resolves Confirm→<c>LaunchMission</c>:1043 / Cancel→
    ///     <c>mission.Cancel()</c>, <c>FactionSoldierJoin</c>→<c>reward.Apply</c> (HavenMissionUtil.cs:59),
    ///     <c>InterceptionBrief</c>→<c>InterceptionBriefCallback</c>. The wire carries WHICH ANSWER and
    ///     nothing else — no state, no objects, no callback (law 3: the client never executes game logic).
    ///   • anything else ⇒ <c>GeoscapeView.FinishQueriedState()</c>:2164 — the plain dismissal that
    ///     <c>UIStateAssetDeployment</c>:66/:79, <c>UIStateGeoCutscene</c>:92, <c>UIStateGeoscapeTutorial</c>
    ///     :37 and <c>UIStateReplenish</c>:64/:69 all reach on their own. One op unblocks the whole set.
    ///
    /// THE ANSWER IS ADDRESSED TO A WINDOW, not to "whatever you have up", and that identity check is the
    /// entire safety argument. A blind advance would let a peer closing its own tutorial Confirm a mission
    /// brief on the host. The intent therefore names the view-state TYPE plus — for a modal — the
    /// <c>ModalType</c>, exactly the two axes <see cref="GeoWindowCoverage"/> already declares on and exactly
    /// what the 0xB7 raise already ships as an int (law 10 mod parity makes both stable across peers). The
    /// host advances only when its OWN current window matches; a mismatch is the ORDINARY case (each peer
    /// closes windows of its own all game long) and is logged, never <see cref="IntentRail.Reject"/>ed —
    /// a reject exists to reconverge a client whose screen ran ahead of host state, and here NOTHING ran
    /// ahead: the closing peer's window was its own presentation and closing it locally was correct.
    ///
    /// FIRST-TO-ANSWER-WINS, the rail's standing arbitration (law 5). One peer's answer really does dismiss
    /// the host's copy — that is the point, and for a decision window it is the same rule the tactical and
    /// event families already run on. <see cref="GeoModalMirror"/>'s "no dismiss message, on purpose" is not
    /// contradicted: that argues against the HOST hard-closing a client's open window, which still never
    /// happens. This is the opposite direction, and it is the mandate.
    ///
    /// REACH TODAY: a client can only name a window it HAS, so this op resolves the host's copy of the
    /// mirrored notification modals (0xB7 — research complete above all, "the single most frequent window in
    /// a campaign"). The brief / soldier-join / interception / asset-deployment / cutscene kinds still need
    /// their host→all RAISE before a client has anything to answer from; the ANSWER half is what shipped
    /// here, and <see cref="GeoWindowCoverage"/>'s declarations are re-argued to say so.
    /// </summary>
    internal static class WindowQueueSync
    {
        internal const byte OpAdvance = 1;  // [stateTypeName:string][modalType:i32][result:u8]

        /// <summary>No answer: the named window is not a modal, so there is no button to convey. Not a
        /// <c>ModalResult</c> value — the enum has exactly Confirm/Cancel/Close (ModalResult.cs).</summary>
        internal const byte ResultNone = 255;

        /// <summary>Sentinel for "the named state is not a modal", on both sides of the wire. Every real
        /// <c>ModalType</c> is non-negative, so no live value can collide with it.</summary>
        internal const int NotAModal = -1;

        private static readonly FieldInfo SwitchQueryField =
            AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");                       // GeoscapeView.cs:138 (game typo)
        private static readonly FieldInfo CurrentRequestField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_currentStateSwitchRequest"); // :17

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.GeoWindowIntent, "window",
                new Dictionary<byte, IntentRail.OpHandler> { [OpAdvance] = HandleAdvance });
        }

        // ─── THE VALIDATOR (pure — host facts only, law 3; RailCheck L82 executes it) ───

        /// <summary>May this intent advance the host's queue? null = yes, otherwise the human reason it did
        /// not. PURE so the arbitration is falsifiable headless: an identity check that only ever runs in a
        /// live session is exactly how a peer ends up confirming a window it never saw.
        /// <paramref name="haveModal"/> / <paramref name="wantModal"/> are <see cref="NotAModal"/> when the
        /// state in question is not a <c>UIStateGeoModal</c>.</summary>
        internal static string Validate(bool haveWindow, string haveType, int haveModal,
                                        string wantType, int wantModal, byte result)
        {
            if (!haveWindow)
                return "the host has no window up — its queue is not blocked and there is nothing to advance";
            if (!string.Equals(haveType, wantType, StringComparison.Ordinal))
                return "the host is on '" + haveType + "' but the answer names '" + wantType +
                       "' — a different window, so advancing would dismiss something nobody answered";
            if (haveModal != wantModal)
                return "both peers are on '" + haveType + "' but the host's modal is " + haveModal +
                       " and the answer names " + wantModal + " — 43 ModalTypes ride the one view state, so " +
                       "the type alone does not identify the window";
            if (haveModal != NotAModal && result > (byte)ModalResult.Close)
                return "a modal is a DECISION window and the answer carries none (result=" + result + ") — " +
                       "its DialogCallback would run against an undefined ModalResult";
            return null;
        }

        // ─── HOST: advance through the game's own funnel ───────────────────

        private static void HandleAdvance(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string wantType = r.ReadString();
            int wantModal = r.ReadInt32();
            byte result = r.ReadByte();

            var view = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View;
            var query = SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
            if (view == null || query == null)
            {
                // Not a reject: the host is mid-load or in a battle, nothing on either peer ran ahead.
                Debug.Log("[MP][windows] advance of '" + wantType + "' from peer=" + senderPeerId +
                          " ignored — this host has no live geoscape window queue right now");
                return;
            }

            var current = CurrentRequestField?.GetValue(query) as GeoscapeViewStateSwitchRequest;
            var state = current?.State;
            var modal = state as UIStateGeoModal;
            string haveType = state == null ? "" : state.GetType().FullName;
            int haveModal = modal == null ? NotAModal : (int)modal.ModalType;

            string why = Validate(state != null, haveType, haveModal, wantType, wantModal, result);
            if (why != null)
            {
                // LOGGED, never rejected — see the class doc. A reject's forced re-emit exists to pull back a
                // client whose screen ran ahead of host state; a peer closing its own window ran ahead of
                // nothing, and rejecting the ordinary case would fire a full-graph resend at click rate.
                Debug.Log("[MP][windows] advance from peer=" + senderPeerId + " nonce=" + nonce +
                          " did NOT apply — " + why);
                return;
            }

            if (modal != null)
            {
                // The host's OWN DialogCallback runs here, over the host's OWN modalData: FinishDialog:82
                // clears the queue slot and then invokes it, so Confirm on a brief really is the host's
                // LaunchMission and a soldier-join really is the host's reward.Apply — native, host-side,
                // off host objects. The client contributed one byte.
                modal.FinishDialog((ModalResult)result);
                Debug.Log("[MP][windows] HOST advanced modal " + modal.ModalType + " with " +
                          (ModalResult)result + " for peer=" + senderPeerId + " nonce=" + nonce);
            }
            else
            {
                view.FinishQueriedState();
                Debug.Log("[MP][windows] HOST advanced '" + haveType + "' for peer=" + senderPeerId +
                          " nonce=" + nonce);
            }
        }

        // ─── CLIENT: the capture seam (law 4a, presentation) ───────────────

        /// <summary>The answer <see cref="FinishDialogAnswer"/> saw one call frame ago. <c>FinishDialog</c>:82
        /// calls <c>FinishQueriedState</c> BEFORE it invokes the handler, so the result is only observable
        /// from a prefix on the modal and has to be carried the one frame down to the send site. Cleared by
        /// the send, so a later non-modal dismissal can never inherit somebody's Confirm.</summary>
        private static byte _pendingResult = ResultNone;

        /// <summary>Called from the client's own <c>FinishQueriedState</c>, with the state it is closing.
        /// Non-blocking on purpose: closing this peer's own window is PRESENTATION and must happen locally
        /// whatever the host does with the intent (the block-first law governs STATE mutations, and this
        /// mutates none). What crosses is the answer.</summary>
        private static void SendAdvance(GeoscapeView view)
        {
            byte result = _pendingResult;
            _pendingResult = ResultNone;
            // ShouldRunNative is the ONE peer decision every capture seam asks (law: block-first). Here its
            // FALSE arm means only "this is a client outside an apply" — nothing is blocked.
            if (IntentRail.ShouldRunNative()) return;
            try
            {
                var query = SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
                var state = (CurrentRequestField?.GetValue(query) as GeoscapeViewStateSwitchRequest)?.State;
                // Null is the NORMAL second call: a state whose ExitState also calls FinishQueriedState
                // (UIStateAssetDeployment:66, UIStateReplenish:69) re-enters after the slot is already
                // cleared. Nothing to name, nothing to send.
                if (state == null) return;
                var modal = state as UIStateGeoModal;
                string type = state.GetType().FullName;
                int modalType = modal == null ? NotAModal : (int)modal.ModalType;
                // Esc / the close button reach FinishQueriedState through OnCancel:96 WITHOUT going through
                // FinishDialog, so no answer was recorded. That is not "no answer" — the game's own tail
                // resolves exactly that case as Close (ExitState:121-124), and sending anything else would
                // make a dismissal mean something the local click did not.
                if (modal != null && result == ResultNone) result = (byte)ModalResult.Close;
                IntentRail.Send(SurfaceIds.GeoWindowIntent, OpAdvance,
                    "advance " + (modal == null ? type : type + "/" + modal.ModalType) + " result=" + result,
                    w => { w.Write(type); w.Write(modalType); w.Write(result); });
            }
            catch (Exception ex)
            {
                // The host's window stays blocked and its clock stays paused for everyone — the exact failure
                // this family exists to end, so it is never swallowed.
                Debug.LogError("[MP][windows] CLIENT advance capture failed — the host's window queue was NOT " +
                               "advanced and stays blocked: " + ex);
            }
        }

        /// <summary>Half one of the client seam: the ANSWER. A prefix, because <c>FinishDialog</c>:82-86
        /// calls <c>FinishQueriedState</c> before it does anything else, so by the time the send site runs
        /// the result is gone. Records only — the native dialog is untouched.</summary>
        [HarmonyPatch(typeof(UIStateGeoModal), nameof(UIStateGeoModal.FinishDialog))]
        internal static class FinishDialogAnswer
        {
            private static void Prefix(ModalResult res) => _pendingResult = (byte)res;
        }

        /// <summary>Half two: the SEND, at the mirror image of the host's own chokepoint —
        /// <c>GeoscapeView.FinishQueriedState</c>:2164. A PREFIX, because the queue slot this reads is
        /// cleared by the very call it wraps (<c>FinishCurrentStateSwitch</c>:118), so a postfix would have
        /// nothing left to name. One seam covers every window kind: a modal answered by a button, a modal
        /// closed by Esc (<c>OnCancel</c>:96), a cutscene skipped (:92), a replenish screen finished (:64).
        /// Never blocks — it always returns true.</summary>
        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.FinishQueriedState))]
        internal static class FinishQueriedStateCapture
        {
            private static void Prefix(GeoscapeView __instance) => SendAdvance(__instance);
        }
    }
}
