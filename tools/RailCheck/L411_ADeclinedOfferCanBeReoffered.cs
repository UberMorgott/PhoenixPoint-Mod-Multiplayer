using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Events;

namespace RailCheck
{
    /// <summary>
    /// L411 — A DECLINED MISSION OFFER CAN BE RE-OFFERED TO THE SAME PEER.
    ///
    /// L384 already pins that a CANCEL stays local. That must never harden into "the offer is retired for
    /// that peer": in the stock game a player who declines clicks the site again and the mission is offered
    /// again. Measured in the 2026-08-11 three-peer run it was dead — a client's click produced nothing
    /// (<c>PP-Instance2 7455 "[MP][events] client-local raise of 'PROG_SY0_MISS' BLOCKED"</c>) and the menu
    /// row was not even there, because our per-peer decline runs no <c>CompleteEvent</c> and therefore never
    /// reaches the game's own re-offer switch (<c>GeoscapeEvent.CompleteEvent</c>:103-106 →
    /// <c>EnableGeoscapeEvent</c>), which is the ONLY thing that puts a record back into the
    /// <c>Reset</c>/<c>IsFree</c> state <c>GeoSite.HasActiveEncounter</c> reads.
    ///
    /// A dead menu row looks like game design, so nothing else would ever catch this.
    ///
    ///   (a) <c>decline-retires-the-offer</c> — <c>ReofferRefusal</c> ACCEPTS an unanswered record
    ///       (<c>Triggered</c>, and <c>Reset</c> so a second decliner is not made to depend on the first
    ///       one's timing). A refusal there is the retirement this law exists to forbid.
    ///   (b) <c>reoffer-reopens-a-paid-offer</c> — it REFUSES an answered one
    ///       (SelectedChoice/Completed/MigratedCompleted) and an unknown event, never blankly. Walking an
    ///       answered record back to Reset would re-open the reward gate on an offer already paid out.
    ///   (c) <c>re-enabled-offer-is-unanswerable</c> — <c>EventSync.Validate</c> accepts <c>Reset</c>. The
    ///       re-enable parks the record there, so a Validate that still froze it would refuse every START
    ///       on an offer somebody had merely declined — the bug wearing the fix's clothes.
    ///   (d) <c>decline-is-not-wired-to-the-re-offer</c> — the live decline arm
    ///       (<c>EventPopup.EventChoiceClientLock.Prefix</c>) actually calls
    ///       <c>MissionReoffer.AfterDecline</c>.
    ///   (e) <c>re-entry-door-left-swallowed</c> — BOTH native doors are patched block-first and both
    ///       prefixes reach <c>MissionReoffer.RelayGesture</c>. On a client the native bodies are gated
    ///       twice over (MissionCancelGate + GeoscapeEventRaiseGate), so an unpatched door is a click that
    ///       does nothing.
    ///   (f) <c>host-re-offer-is-hand-rolled</c> — <c>HostReoffer</c> runs the GAME'S calls
    ///       (<c>GeoscapeView.ShowMissionBriefing</c> / <c>GeoscapeEventSystem.TriggerGeoscapeEvent</c>),
    ///       which is what makes every peer land in ONE stage off one raise instead of a per-peer window.
    ///   (g) <c>re-offer-waits-on-a-peer</c> (P13) — the decision is a pure function of ONE record state.
    ///       A peer count, roster or membership argument appearing here is a quorum.
    ///   (h) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam.RetireOnDecline"/> is the pre-fix behaviour;
    ///       the (a)/(b) table is run over it and MUST come back red on (a).
    ///
    /// Falsify (each verified RED, then restored): make <c>ReofferRefusal</c> return a reason for
    /// <c>Triggered</c> → (a) and (h); return null for <c>Completed</c> → (b); restore
    /// <c>Validate</c>'s <c>state != Triggered</c> → (c); drop the <c>AfterDecline</c> call → (d); delete
    /// either patch class → (e); replace a <c>HostReoffer</c> call with a local window → (f).
    /// </summary>
    internal static class L411_ADeclinedOfferCanBeReoffered
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var reoffer = typeof(MissionReoffer);
            var mod = reoffer.Assembly;
            var refusal = typeof(EventSync).GetMethod("ReofferRefusal", All);
            var afterDecline = reoffer.GetMethod("AfterDecline", All);
            var relay = reoffer.GetMethod("RelayGesture", All);
            var hostReoffer = reoffer.GetMethod("HostReoffer", All);
            var declineArm = mod.GetType("Multiplayer.Network.Sync.EventChoiceClientLock")
                                ?.GetMethod("Prefix", All);
            if (refusal == null || afterDecline == null || relay == null || hostReoffer == null ||
                declineArm == null)
            {
                yield return "L411 premise-changed: EventSync.ReofferRefusal / MissionReoffer.AfterDecline / " +
                             "RelayGesture / HostReoffer / EventChoiceClientLock.Prefix no longer resolve whole. " +
                             "Those five ARE the re-offer path; losing one turns a declined mission back into a " +
                             "permanently dead menu row, which reads as game design and is never reported.";
                yield break;
            }

            // ── (a)/(b) the decision table ─────────────────────────────────────────────────────────
            foreach (var red in Table(EventSync.ReofferRefusal, "L411")) yield return red;

            // ── (c) the re-enabled offer is still answerable ────────────────────────────────────────
            if (EventSync.Validate(GeoscapeEventRecordState.Reset, 0, 3) != null)
                yield return "L411 re-enabled-offer-is-unanswerable: EventSync.Validate refuses a Reset record. " +
                             "That is exactly where a declined-and-re-enabled offer sits (GeoscapeEventRecord." +
                             "IsFree is literally _state == Reset), so every START on a re-offered mission would " +
                             "be rejected with 'already answered' — the offer would come back and then refuse to " +
                             "be taken.";
            if (EventSync.Validate(GeoscapeEventRecordState.Completed, 0, 3) == null ||
                EventSync.Validate(GeoscapeEventRecordState.SelectedChoice, 0, 3) == null)
                yield return "L411 freeze-lost: Validate stopped freezing an ANSWERED record. Widening it to Reset " +
                             "must not widen it to the states that already carry a reward — that is L27's " +
                             "double-grant.";

            // ── (d) the live decline arm is wired to it ─────────────────────────────────────────────
            if (!Program.CalleeSequence(declineArm).Any(c => c != null && c.Name == "AfterDecline"))
                yield return "L411 decline-is-not-wired-to-the-re-offer: EventChoiceClientLock.Prefix's per-peer " +
                             "cancel arm no longer calls MissionReoffer.AfterDecline. The window still closes, so " +
                             "nothing looks broken — but the record is never re-enabled and neither menu row can " +
                             "come back on any peer.";

            // ── (e) both native doors are patched, block-first, and reach the relay ─────────────────
            foreach (var door in new[] { "EncounterReofferInput", "BriefReofferInput" })
            {
                var patch = mod.GetType("Multiplayer.Network.Sync." + door);
                var prefix = patch?.GetMethod("Prefix", All);
                if (prefix == null || prefix.ReturnType != typeof(bool) ||
                    patch.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false).Length == 0)
                {
                    yield return "L411 re-entry-door-left-swallowed: " + door + " is not a block-first Harmony " +
                                 "prefix. On a client the native body behind it is gated twice (MissionCancelGate " +
                                 "and GeoscapeEventRaiseGate), so leaving it unpatched is a click that does " +
                                 "nothing and logs nothing the player can see.";
                    continue;
                }
                if (!Program.CalleeSequence(prefix).Any(c => c != null && c.Name == "RelayGesture"))
                    yield return "L411 re-entry-door-left-swallowed: " + door + ".Prefix does not reach " +
                                 "MissionReoffer.RelayGesture, so the gesture is never relayed to the host.";
            }
            if (HarmonyLib.AccessTools.Method(
                    typeof(PhoenixPoint.Geoscape.Entities.Abilities.TriggerEncounterAbility),
                    "ActivateInternal",
                    new[] { typeof(PhoenixPoint.Geoscape.Entities.Abilities.GeoAbilityTarget) }) == null ||
                HarmonyLib.AccessTools.Method(typeof(PhoenixPoint.Geoscape.View.GeoscapeView),
                    "ShowMissionBriefing") == null)
                yield return "L411 premise-changed: a re-entry door the patches name no longer exists in the game " +
                             "assembly — an unbound HarmonyPatch is one swallowed warning (L23), not an error.";

            // ── (f) the host runs the GAME'S re-offer, not one of ours ─────────────────────────────
            var hostCalls = Program.CalleeSequence(hostReoffer).Where(c => c != null).Select(c => c.Name).ToList();
            if (!hostCalls.Contains("ShowMissionBriefing") || !hostCalls.Contains("TriggerGeoscapeEvent"))
                yield return "L411 host-re-offer-is-hand-rolled: MissionReoffer.HostReoffer no longer reaches both " +
                             "GeoscapeView.ShowMissionBriefing and GeoscapeEventSystem.TriggerGeoscapeEvent. Those " +
                             "two ARE the stock re-entry doors, and running them on the host is what makes the " +
                             "window reach every peer as ONE raise — a hand-rolled per-peer window is how two " +
                             "peers ended up in different stages off the same press.";

            // ── (g) no quorum: one record state in, one reason out ─────────────────────────────────
            var ps = refusal.GetParameters();
            if (ps.Length != 1 || ps[0].ParameterType != typeof(GeoscapeEventRecordState?))
                yield return "L411 re-offer-waits-on-a-peer: ReofferRefusal takes " + ps.Length + " argument(s). " +
                             "The re-offer decision must be a pure function of ONE record state — a peer count, " +
                             "roster or membership argument here is a quorum (P13), and re-entry would start " +
                             "depending on what another player does.";

            // ── (h) POSITIVE CONTROL, executed ─────────────────────────────────────────────────────
            if (!Table(FakeSeam.RetireOnDecline, "control").Any())
                yield return "L411 control-not-red: FakeSeam.RetireOnDecline retires the offer on the very first " +
                             "decline and the table did not flag it, so arms (a)/(b) are decorative.";
        }

        private static IEnumerable<string> Table(Func<GeoscapeEventRecordState?, string> refusal, string id)
        {
            foreach (var open in new[] { GeoscapeEventRecordState.Triggered, GeoscapeEventRecordState.Reset })
                if (refusal(open) != null)
                    yield return id + " decline-retires-the-offer: an UNANSWERED record (" + open + ") was refused " +
                                 "a re-offer. A cancel is 'not now', not 'never': the peer that declined can no " +
                                 "longer get the mission back from the site menu at all, and a missing menu row " +
                                 "is indistinguishable from game design.";
            if (id == "control") yield break;
            foreach (var answered in new[] { GeoscapeEventRecordState.SelectedChoice,
                                             GeoscapeEventRecordState.Completed,
                                             GeoscapeEventRecordState.MigratedCompleted })
            {
                var why = refusal(answered);
                if (why == null)
                    yield return id + " reoffer-reopens-a-paid-offer: state " + answered + " was allowed back to " +
                                 "Reset. That re-opens EventSync.Validate on an offer whose reward has already " +
                                 "been granted — L27's double grant, reached from the other side.";
                else if (why.Trim().Length == 0)
                    yield return id + " silent-reject: state " + answered + " was refused with a BLANK reason.";
            }
            if (string.IsNullOrWhiteSpace(refusal(null)))
                yield return id + " unknown-event-accepted: a re-offer for an event the host has no record of was " +
                             "accepted (or refused blankly) — EnableGeoscapeEvent would be a silent no-op and the " +
                             "player would keep clicking a row that never re-offers anything.";
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the pre-fix behaviour — a declined offer is retired, so no
            /// unanswered record is ever re-offerable.</summary>
            internal static string RetireOnDecline(GeoscapeEventRecordState? state) =>
                "a declined offer is retired for that peer";
        }
    }
}
