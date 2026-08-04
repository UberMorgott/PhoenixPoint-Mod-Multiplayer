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
    /// field is <c>FinishCurrentStateSwitch</c>:116 — i.e. a HOST CLICK, nothing else. So ONE un-dismissed
    /// window on an idle host wedges the host's window queue for the rest of the session — every later window
    /// piles up behind it forever. NARROWED 2026-08-04 by the pause rework: the CLOCK half of this argument is
    /// gone. <c>PauseGame = true</c> is now a ONE-SHOT pause the game issues on the peer that gets the window
    /// (<c>ProcessQueriedStateSwitch</c>:67-70 → <c>RequestGamePause</c>:1269) and ANY peer resumes it
    /// unconditionally, first-to-act-wins — an idle host no longer freezes the shared campaign. The QUEUE
    /// wedge is real and is the whole remaining reason this family exists.
    ///
    /// THE SEAM IS THE GAME'S OWN CHOKEPOINT, at ONE depth (it was two until 2026-08-01, see below):
    /// <c>UIStateGeoModal.FinishDialog(result)</c> (UIStateGeoModal.cs:82), which itself calls
    /// <c>FinishQueriedState</c> and THEN invokes the host's own <c>DialogCallback</c>. That closure is the
    /// one built at <c>GeoscapeView.OpenModalPersistent</c>:848 over the HOST's own <c>modalData</c>, so
    /// <c>ModalResultCallback</c>:799 runs on the host with host objects — a mission brief would resolve
    /// Confirm→<c>LaunchMission</c>:1043 / Cancel→<c>mission.Cancel()</c>,
    /// <c>FactionSoldierJoin</c>→<c>reward.Apply</c> (HavenMissionUtil.cs:59) — and the wire carries WHICH
    /// ANSWER and nothing else: no state, no objects, no callback (law 3, the client never executes game
    /// logic). The plain <c>GeoscapeView.FinishQueriedState()</c>:2164 arm for non-modal windows
    /// (<c>UIStateAssetDeployment</c>:66, <c>UIStateGeoCutscene</c>:92, <c>UIStateGeoscapeTutorial</c>:37,
    /// <c>UIStateReplenish</c>:64) is GONE: nothing on either side of it could be identified per-instance, so
    /// it was structurally incapable of naming a window rather than a kind — see <see cref="IdentityOf"/>.
    ///
    /// THE ANSWER IS ADDRESSED TO A WINDOW INSTANCE, and that is the entire safety argument. NARROWED
    /// 2026-08-01, same day it shipped, because the first version got this wrong and the wrongness was the
    /// bug: it named the view-state TYPE plus — for a modal — the <c>ModalType</c>, and a type is not a
    /// window. In the live 3-instance run a client closing its OWN <c>UIStateGeoscapeEvent</c> matched the
    /// host's unrelated <c>UIStateGeoscapeEvent</c> on the type alone and dismissed it (multiplayer.log
    /// 15:17:53 / 15:18:04 against multiplayer-2.log's two sends). A dismissed event picker is not merely a
    /// lost window either — <c>UIStateGeoscapeEvent.ExitState</c>:61-65 completes a still-Triggered event
    /// with <c>Choices.Last()</c> — so the host really was answered by the client's click, which is exactly
    /// what the identity check existed to make impossible. See <see cref="IdentityOf"/> for what replaced it.
    ///
    /// A mismatch is logged, never <see cref="IntentRail.Reject"/>ed — a reject exists to reconverge a client
    /// whose screen ran ahead of host state, and here NOTHING ran ahead: the closing peer's window was its
    /// own presentation and closing it locally was correct.
    ///
    /// FIRST-TO-ANSWER-WINS, the rail's standing arbitration (law 5). One peer's answer really does dismiss
    /// the host's copy — but only of the SAME window, the one the host itself raised to that peer. That is
    /// the same rule the tactical and event families already run on. <see cref="GeoModalMirror"/>'s "no
    /// dismiss message, on purpose" is not contradicted: that argues against the HOST hard-closing a client's
    /// open window, which still never happens.
    ///
    /// REACH TODAY, honestly stated: a peer can only answer a window the HOST RAISED TO IT, so this op
    /// resolves exactly the mirrored notification modals of 0xB7 (research complete above all, "the single
    /// most frequent window in a campaign"). It does NOT reach the brief / soldier-join / interception /
    /// asset-deployment / cutscene kinds and never did: those are declared Gap or LocalOnly, they exist on
    /// ONE screen, and a peer that does not have a window cannot close it — the ANSWER half is worthless
    /// without the RAISE half, which is the hole <see cref="GeoWindowCoverage"/> already names.
    /// </summary>
    internal static class WindowQueueSync
    {
        internal const byte OpAdvance = 1;  // [identity:string][result:u8]

        /// <summary>No answer recorded. A LOCAL sentinel for <see cref="_pendingResult"/> only — it never
        /// rides the wire, because every window this family can name is a modal and therefore always carries
        /// a real answer. Not a <c>ModalResult</c> value: the enum has exactly Confirm/Cancel/Close
        /// (ModalResult.cs).</summary>
        internal const byte ResultNone = 255;

        // ONE bind of each, shared with PauseHold.IsCurrentQueuedWindow (all that class still is since the
        // 2026-08-04 pause rework — no hold set, no arbiter; it asks these same two fields only for "is this
        // screen a queued window") — a second AccessTools.Field pair is a second thing to drift when the game
        // renames one.
        internal static readonly FieldInfo SwitchQueryField =
            AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");                       // GeoscapeView.cs:138 (game typo)
        internal static readonly FieldInfo CurrentRequestField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_currentStateSwitchRequest"); // :17

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.GeoWindowIntent, "window",
                new Dictionary<byte, IntentRail.OpHandler> { [OpAdvance] = HandleAdvance });
        }

        // ─── WINDOW IDENTITY (pure; RailCheck L82 executes it) ─────────────

        /// <summary>
        /// The identity of a window BOTH peers provably hold, or null when this peer must not name it at all
        /// — which is the answer for everything a peer opened on its own, and therefore for almost every
        /// window closed in a session.
        ///
        /// THE ONLY WINDOWS A PEER MAY ANSWER FOR ANOTHER ARE THE ONES THE HOST RAISED TO IT. That is the
        /// Mirrored modal family and nothing else: the copy on this peer was built by
        /// <see cref="GeoModalMirror"/> out of the host's own 0xB7 payload, so "we are looking at the same
        /// window" is a fact on the wire rather than a coincidence of types. The identity therefore IS that
        /// payload — <see cref="GeoModalMirror.Describe"/> run over the live <c>ModalData</c>, which on the
        /// host describes its ORIGINAL object and on the client re-describes what
        /// <c>GeoModalMirror.BuildData</c> rebuilt from those very fields, yielding the same string on both
        /// sides for the same window and a different one for every other. <c>Raise.Num</c> is deliberately
        /// excluded: it is the presentation flag <c>SwitchToResearchState</c>, which the client's rebuild
        /// forces to false on purpose (GeoModalMirror.cs:363) and which names nothing.
        ///
        /// Two independent gates, because they refuse different things: the coverage rule refuses a window
        /// that is not SHARED (an ability confirmation, a soldier-edit picker, a haven infiltration brief —
        /// all LocalOnly, all opened by the clicking peer itself), and <c>Unsupported</c> refuses one whose
        /// data 0xB7 cannot describe and therefore never shipped (a mission brief's live <c>GeoMission</c>).
        ///
        /// A window with no identity is neither sent nor advanced, and there is no non-modal arm any more.
        /// That drops the plain-dismissal reach `81afe12` claimed for cutscenes and asset deployment; it was
        /// never real, because those are declared Gap / LocalOnly, reach ONE screen, and a peer cannot close
        /// a window it does not have. What it also drops is <c>UIStateGeoscapeEvent</c>, which needed no arm
        /// here at all: an event answer already crosses as the 0xB4 intent keyed on the event's own
        /// <c>EventID</c> (<see cref="EventSync"/>), so all this op ever added there was the wrong-instance
        /// dismissal that made it a regression.
        /// </summary>
        internal static string IdentityOf(object state)
        {
            var modal = state as UIStateGeoModal;
            if (modal == null) return null;                                    // this peer's own local window
            if (GeoWindowCoverage.RuleForModal(modal.ModalType)?.Sync != WindowSync.Mirrored) return null;
            var p = GeoModalMirror.Describe(modal.ModalData);
            if (p.Shape == GeoModalMirror.DataShape.Unsupported) return null;  // never rode the 0xB7 raise
            return modal.ModalType + "|" + p.Shape + "|" + p.Ref + "|" +
                   string.Join(",", p.Keys ?? new string[0]);
        }

        // ─── THE VALIDATOR (pure — host facts only, law 3; RailCheck L82 executes it) ───

        /// <summary>May this intent advance the host's queue? null = yes, otherwise the human reason it did
        /// not. PURE so the arbitration is falsifiable headless: an identity check that only ever runs in a
        /// live session is exactly how a peer ends up answering a window it never saw — which is not a
        /// hypothetical, it is what this family did for one build on 2026-08-01.
        /// <paramref name="haveIdentity"/> is null/empty when the host's current window has no shared
        /// identity: no window up, or a window of the host's own that no peer ever saw.</summary>
        internal static string Validate(string haveIdentity, string wantIdentity, byte result)
        {
            if (string.IsNullOrEmpty(wantIdentity))
                return "the answer names no window — only a host-RAISED mirrored modal has an identity both " +
                       "peers can agree on, and nothing without one may advance anybody's queue";
            if (string.IsNullOrEmpty(haveIdentity))
                return "the host is not on a window any peer shares — either its queue is not blocked at all, " +
                       "or what it holds is its OWN window, which nobody else saw and nobody else may dismiss";
            if (!string.Equals(haveIdentity, wantIdentity, StringComparison.Ordinal))
                return "the host holds '" + haveIdentity + "' but the answer names '" + wantIdentity +
                       "' — a different window INSTANCE, so advancing would dismiss something nobody answered";
            if (result > (byte)ModalResult.Close)
                return "a modal is a DECISION window and the answer carries none (result=" + result + ") — " +
                       "its DialogCallback would run against an undefined ModalResult";
            return null;
        }

        // ─── HOST: advance through the game's own funnel ───────────────────

        private static void HandleAdvance(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string wantIdentity = r.ReadString();
            byte result = r.ReadByte();

            var view = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View;
            var query = SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
            if (view == null || query == null)
            {
                // Not a reject: the host is mid-load or in a battle, nothing on either peer ran ahead.
                Debug.Log("[MP][windows] advance of '" + wantIdentity + "' from peer=" + senderPeerId +
                          " ignored — this host has no live geoscape window queue right now");
                return;
            }

            var current = CurrentRequestField?.GetValue(query) as GeoscapeViewStateSwitchRequest;
            var modal = current?.State as UIStateGeoModal;
            string haveIdentity = IdentityOf(modal);

            string why = Validate(haveIdentity, wantIdentity, result);
            if (why != null)
            {
                // LOGGED, never rejected — see the class doc. A reject's forced re-emit exists to pull back a
                // client whose screen ran ahead of host state; a peer closing its own window ran ahead of
                // nothing, and rejecting the ordinary case would fire a full-graph resend at click rate.
                Debug.Log("[MP][windows] advance from peer=" + senderPeerId + " nonce=" + nonce +
                          " did NOT apply — " + why);
                return;
            }

            // The host's OWN DialogCallback runs here, over the host's OWN modalData: FinishDialog:82 clears
            // the queue slot and then invokes it — native, host-side, off host objects, on the very window
            // the host itself raised to the answering peer. The client contributed one byte.
            modal.FinishDialog((ModalResult)result);
            Debug.Log("[MP][windows] HOST advanced '" + haveIdentity + "' with " + (ModalResult)result +
                      " for peer=" + senderPeerId + " nonce=" + nonce);
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
                string identity = IdentityOf(state);
                // THE ORDINARY CASE, and the whole of the 2026-08-01 fix: this peer closed a window of its
                // OWN — its event picker, its tutorial, its replenish screen, its ability prompt — or the
                // slot was already cleared by a re-entrant ExitState (UIStateAssetDeployment:66,
                // UIStateReplenish:69). Nothing crosses, and silently: it happens at click rate all game long.
                if (identity == null) return;
                // Esc / the close button reach FinishQueriedState through OnCancel:96 WITHOUT going through
                // FinishDialog, so no answer was recorded. That is not "no answer" — the game's own tail
                // resolves exactly that case as Close (ExitState:121-124), and sending anything else would
                // make a dismissal mean something the local click did not.
                if (result == ResultNone) result = (byte)ModalResult.Close;
                IntentRail.Send(SurfaceIds.GeoWindowIntent, OpAdvance,
                    "advance " + identity + " result=" + result,
                    w => { w.Write(identity); w.Write(result); });
            }
            catch (Exception ex)
            {
                // The host's copy of this window stays up and its clock stays paused for everyone — the exact
                // failure this family exists to end, so it is never swallowed.
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
        /// nothing left to name. It sees every window kind and <see cref="IdentityOf"/> decides which of them
        /// may cross — a mirrored modal answered by a button or closed by Esc (<c>OnCancel</c>:96) does; a
        /// cutscene skipped (:92), a replenish screen finished (:64), an event picker answered and a tutorial
        /// stepped through do NOT, because they are this peer's own. Never blocks — it always returns
        /// true.</summary>
        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.FinishQueriedState))]
        internal static class FinishQueriedStateCapture
        {
            private static void Prefix(GeoscapeView __instance) => SendAdvance(__instance);
        }
    }
}
