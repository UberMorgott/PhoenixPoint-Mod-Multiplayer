using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L176 — A WINDOW THE HOST RAISED IS ANSWERABLE WHILE IT IS STILL IN THE QUEUE, AND THE SCREEN THAT
    /// ANSWER OPENS ASKS THE GAME ITS OWN QUESTION AT OPEN TIME, NOT AT QUEUE TIME.
    ///
    /// THE REPORT (2026-08-07, owner, the ambush). A partner flew to a point of interest, scanned it, an
    /// ambush triggered — the host was correctly NOT yanked for the ambush itself — and then the
    /// soldier-deployment window WHICH SHOULD FOLLOW NEVER APPEARED FOR THE HOST AT ALL. When he reached it
    /// by other means, THERE WAS NO START MISSION BUTTON. Two failures, two roots, one arc.
    ///
    /// ROOT ONE, AND IT IS THE PRICE OF THE PREVIOUS DAY'S FIX. The ambush brief is
    /// <c>OpenModalPersistent(GeoAmbushBrief, mission, 0)</c> (<c>GeoscapeView.ShowMissionBriefing</c>:1903),
    /// i.e. a REVIEW-priority window — so <c>19af84c</c>'s <c>WindowOrder.HoldsForOpenScreen</c> correctly
    /// kept it in <c>_viewStateSwitchRequests</c> while the host sat in the research screen. The client
    /// answered ITS copy and the answer crossed as 0xB9 op 1 exactly as designed. And
    /// <c>WindowQueueSync.HandleAdvance</c> THREW IT AWAY: it reads <c>_currentStateSwitchRequest</c> and
    /// only that, so a window that was never ENTERED has no identity, <c>ValidateIdentity</c> answers "the
    /// host is not on a window any peer shares", and the intent is logged as not applied. No
    /// <c>FinishDialog</c>, so no <c>ModalResultCallback</c>:799, so no <c>LaunchMission</c>:1043, so NO
    /// DEPLOYMENT SCREEN — ever, on that peer. The hole predates the hold (a host with two windows queued
    /// always had one that was not current); the hold made it the normal case, and <c>WindowOrder</c>'s own
    /// doc had ruled it an acceptable exposure in writing.
    ///
    /// AND THE ANSWER MUST NOT BE <c>FinishDialog</c>. Its first act is <c>FinishQueriedState</c> →
    /// <c>FinishCurrentStateSwitch</c>:116, which nulls the current slot AND runs
    /// <c>_statesStack.SwitchToPreviousState()</c> — on a state that was never PUSHED. That pops the
    /// research screen the player is looking at, which is the very yank the window-history rule exists to
    /// stop, and it is the standing "never Enter (or pop) a state the stack does not hold" trap this repo
    /// has paid for before. Arm (c) is that, asserted as an ABSENCE in IL.
    ///
    /// ROOT TWO IS A SEPARATE RACE ON THE SAME SCREEN, and it is the shape L164 records one screen over.
    /// <c>UIStateRosterDeployment</c>'s CONSTRUCTOR (:74-82) computes both inputs the START MISSION button
    /// hangs on — <c>GetDeploymentSources</c> and <c>GetDefaultDeploymentSetup</c> — and the constructor runs
    /// inside <c>ToDeploymentState</c>:595, AT QUEUE TIME. <c>EnterState</c> feeds those frozen lists to the
    /// roster module and to <c>CheckForDeployment</c>:369-378, whose first term is <c>squad.Any()</c>: an
    /// empty snapshot is <c>DeployButton.SetInteractable(false)</c> over an empty roster, with nothing to
    /// tick and therefore no way back. Vanilla never pays for it (queue and entry are one frame in a solo
    /// game) and co-op just made the gap far longer on purpose, because L175 now HOLDS that screen. Arm (d)
    /// is the re-ask, and it asserts the same property L164 does: the gate asks the GAME'S OWN question, not
    /// a local re-implementation of what "who can deploy" means.
    ///
    /// Falsify (each verified RED, then restored): delete the <c>AnswerQueued</c> call from
    /// <c>HandleAdvance</c> → <c>queued-window-unanswerable</c>; make <c>MayAnswerQueued</c> ignore the
    /// current window → <c>current-window-race</c>; make it accept an empty or mismatched identity →
    /// <c>queued-answer-unaddressed</c>; call <c>FinishDialog</c> from <c>AnswerQueued</c> →
    /// <c>queued-answer-pops-a-screen</c>; drop the <c>_dialogHandler</c> read → <c>queued-answer-inert</c>;
    /// replace either game call in the EnterState prefix with a local helper →
    /// <c>snapshot-not-the-games</c>; rename a member → <c>premise-changed</c>.
    /// </summary>
    internal static class L176_AQueuedWindowIsStillAnswerable
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(WindowQueueSync);
            var mod = sync.Assembly;
            var game = typeof(UIStateGeoModal).Assembly;

            var mayAnswer = sync.GetMethod("MayAnswerQueued", All);
            var answerQueued = sync.GetMethod("AnswerQueued", All);
            var handleAdvance = sync.GetMethod("HandleAdvance", All);
            var handlerField = sync.GetField("DialogHandlerField", All);
            var requestsField = typeof(WindowOrder).GetField("RequestsField", All);
            var refresh = mod.GetType("Multiplayer.Network.Sync.DeploymentRosterRefresh");
            var prefix = refresh?.GetMethod("Prefix", All);

            if (mayAnswer == null || answerQueued == null || handleAdvance == null || handlerField == null ||
                requestsField == null || prefix == null)
            {
                yield return "L176 premise-changed: one of WindowQueueSync.{MayAnswerQueued,AnswerQueued," +
                             "HandleAdvance,DialogHandlerField}, WindowOrder.RequestsField or " +
                             "DeploymentRosterRefresh.Prefix no longer resolves. Both halves of the ambush " +
                             "report ride these members, and a window that is never answered fails the way " +
                             "this repo's worst bugs fail — one log line saying it did not apply, and nothing " +
                             "at all where the screen should have been.";
                yield break;
            }

            // ── (a) the pure gate, executed ────────────────────────────────────────────────────────
            const string want = "GeoAmbushBrief|EntityRef|S#293|";
            const string other = "GeoResearchComplete|ResearchComplete|F#1|PX_Rifle";

            if (!May(mayAnswer, null, want, want))
                yield return "L176 queued-window-unanswerable: a window that IS in this host's queue, named " +
                             "exactly, with nothing else on screen, may not be answered. That is the ambush " +
                             "report verbatim: the client's answer is dropped, the host's own DialogCallback " +
                             "never runs, and the deployment screen the brief was supposed to open never " +
                             "exists on that peer.";
            if (May(mayAnswer, want, want, want))
                yield return "L176 current-window-race: the queued arm accepts a window that is ALSO the " +
                             "current one. Two funnels would then answer the same modal — FinishDialog (which " +
                             "clears the queue slot and returns the stack) and the queued path (which does " +
                             "neither, because the state was never pushed) — and the one that wins decides " +
                             "whether the peer is left with a dead _currentStateSwitchRequest wedging its " +
                             "whole queue for the rest of the session.";
            foreach (var bad in new[] { (have: (string)null, queued: want, wanted: (string)null),
                                        (null, want, ""),
                                        (null, other, want),
                                        (null, (string)null, want),
                                        (null, "", want) })
                if (May(mayAnswer, bad.have, bad.queued, bad.wanted))
                    yield return "L176 queued-answer-unaddressed: the queued arm accepted want='" +
                                 (bad.wanted ?? "<null>") + "' against queued='" + (bad.queued ?? "<null>") +
                                 "'. The identity IS the safety property of this whole surface (0xB9's own " +
                                 "contract): a peer closing a tutorial of its own would otherwise Confirm a " +
                                 "mission brief on somebody else's host, and a window with no shared identity " +
                                 "is by definition one no other peer ever saw.";

            // ── (b) the advance actually reaches it ────────────────────────────────────────────────
            if (!Program.Callees(handleAdvance, mod).Any(c => Same(c, answerQueued)))
                yield return "L176 queued-window-unanswerable: HandleAdvance never calls AnswerQueued, so the " +
                             "gate in arm (a) is a pure function nothing consults. The intent still arrives, " +
                             "is still validated against the CURRENT window only, and is still logged as 'did " +
                             "NOT apply' — a correct-looking line for a window that will now never be answered " +
                             "at all, because WindowOrder holds it for as long as this peer stays in a screen.";

            // ── (c) it answers WITHOUT touching what is on screen ──────────────────────────────────
            foreach (var c in Program.Callees(answerQueued, game))
                if (c.Name == "FinishDialog" || c.Name == "FinishQueriedState" || c.Name == "FinishCurrentStateSwitch")
                    yield return "L176 queued-answer-pops-a-screen: AnswerQueued calls " +
                                 (c.DeclaringType?.Name ?? "?") + "." + c.Name + ". Every one of those runs " +
                                 "FinishCurrentStateSwitch:116 → SwitchToPreviousState on a state the stack " +
                                 "NEVER PUSHED, so answering a HELD window would pop the research screen the " +
                                 "player is actually in — the exact yank the window-history rule (L175/L163) " +
                                 "was written to stop, arriving through the fix for the opposite defect.";
            if (!Program.ReadsField(answerQueued, requestsField))
                yield return "L176 queued-answer-pops-a-screen: AnswerQueued never reads " +
                             "WindowOrder.RequestsField, so it cannot be taking the answered request OUT of " +
                             "the game's pending list. A window that has been decided for everyone would then " +
                             "still be served to this peer later, offering a choice that was already made.";
            if (!Program.ReadsField(answerQueued, handlerField))
                yield return "L176 queued-answer-inert: AnswerQueued never reads DialogHandlerField, so it " +
                             "removes the window and runs NOTHING. The consequence of the answer is the whole " +
                             "point — the brief's DialogCallback is ModalResultCallback:799, whose Confirm arm " +
                             "IS LaunchMission:1043 — and dropping it silently is the same missing deployment " +
                             "screen with a tidier log.";

            // ── (d) the screen asks the GAME's own question when it OPENS ──────────────────────────
            var gameCalls = Program.Callees(prefix, game).Select(c => c.Name).ToList();
            foreach (var q in new[] { "GetDeploymentSources", "GetDefaultDeploymentSetup" })
                if (!gameCalls.Contains(q))
                    yield return "L176 snapshot-not-the-games: DeploymentRosterRefresh.Prefix does not call " +
                                 "GeoMission." + q + ". The START MISSION button hangs on exactly these two " +
                                 "answers, taken in the state's CONSTRUCTOR at ToDeploymentState:595 — i.e. " +
                                 "when the screen was QUEUED, which since L175 can be minutes before anyone " +
                                 "looks at it. Re-asking through a local helper is the drift a1c11dd already " +
                                 "paid for on the resupply gate: the guarantee is that the peer's screen and " +
                                 "the game agree on who can deploy, and only the game's own method is that.";
        }

        private static bool May(MethodInfo m, string have, string queued, string want) =>
            (bool)m.Invoke(null, new object[] { have, queued, want });

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
