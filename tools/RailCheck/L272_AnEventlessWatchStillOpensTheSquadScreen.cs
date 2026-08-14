using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L272 — A WATCH ARMED WITHOUT AN EVENT STILL OPENS THE SQUAD SCREEN, AND IS STILL BOUNDED.
    ///
    /// WHY THE ARM EXISTS. <see cref="MissionArrivalNav"/> was built for mission-start ENCOUNTERS: it waits
    /// for a <c>GeoscapeEventRecord</c> to read <c>Completed</c> with a real choice, then for the host's
    /// mission to land on this peer's own site. A HAVEN INFILTRATION has no event anywhere in it — the player
    /// pressed a button on the haven panel, <see cref="HavenMissionGate"/> blocked the local mint and asked
    /// the host, and there is no record and never will be. Armed as-is, that watch would sit forever waiting
    /// on <c>EventPopup.LiveRecord</c> for an id that names nothing, and the squad screen would simply never
    /// appear — silently, which is this repo's dominant bug class and the exact failure the whole seam exists
    /// to end.
    ///
    /// So a null <c>EventId</c> MEANS something: the answer term is this peer's own click, already true. Only
    /// that term differs; both arms still wait on the mission arriving, both are still bounded, and neither
    /// counts a peer or waits for anybody to press anything (P13 — a peer whose partners are all AFK opens its
    /// screen exactly as fast).
    ///
    ///   (a) <c>eventless-watch-waits-for-a-record</c> — <c>AnswerIsIn(null, false)</c> is TRUE. Make it false
    ///       (or make <c>Step</c> return early on that arm) and the haven screen never opens.
    ///   (b) <c>event-arm-withdrawn</c> — <c>AnswerIsIn("EV", false)</c> is FALSE. The eventless arm must be
    ///       ADDED, not swapped in: an event-backed watch that stops waiting for its record would open a squad
    ///       screen for a window still open on somebody else's screen.
    ///   (c) <c>gate-is-decorative</c> — <c>Step</c> actually calls the decision.
    ///   (d) <c>arrival-term-dropped</c> — <c>Step</c> still reaches <c>MissionHasArrived</c> AND
    ///       <c>GeoscapeView.LaunchMission</c>. Skipping the record term must not skip the MISSION term too;
    ///       that would open a deployment screen for a mission this peer does not have.
    ///   (e) <c>eventless-watch-unbounded</c> — the eventless arm stamps its clock when it is ARMED (there is
    ///       no record whose resolution could stamp it later), and <c>ArrivalGivenUp</c> still fires at the
    ///       bound. An unbounded wait is how "waiting on the host" quietly becomes "waiting forever".
    ///   (f) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam.AlwaysNeedsARecord"/> is the pre-fix decision;
    ///       arms (a)/(b) are run over it and MUST come back red on (a).
    ///
    /// Falsify (each verified RED, then restored): <c>AnswerIsIn</c> → <c>recordResolved</c> → (a) and (f);
    /// → <c>true</c> → (b); inline the decision in <c>Step</c> → (c); drop the <c>MissionHasArrived</c> call →
    /// (d); stop stamping <c>ResolvedAt</c> in <c>WatchHavenMission</c> → (e).
    /// </summary>
    internal static class L272_AnEventlessWatchStillOpensTheSquadScreen
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var nav = typeof(MissionArrivalNav);
            var mod = nav.Assembly;
            var answer = nav.GetMethod("AnswerIsIn", All);
            var step = nav.GetMethod("Step", All);
            var armHaven = nav.GetMethod("WatchHavenMission", All);
            var watched = nav.GetNestedType("Watched", All);
            if (answer == null || step == null || armHaven == null ||
                watched == null || watched.GetField("EventId", All) == null)
            {
                yield return "L272 premise-changed: MissionArrivalNav.AnswerIsIn / Step / WatchHavenMission / " +
                             "Watched.EventId no longer resolve whole. The eventless arm IS those four members; " +
                             "losing one un-asserts the only path by which a haven infiltration on a client ever " +
                             "reaches a squad screen, and its absence is silent by construction.";
                yield break;
            }

            foreach (var red in Table(MissionArrivalNav.AnswerIsIn, "L272")) yield return red;

            // ── (c) the live step still asks it ────────────────────────────────────────────────────
            var stepCalls = Program.Callees(step, mod).Where(c => c != null).ToList();
            if (!stepCalls.Any(c => c.Name == "AnswerIsIn"))
                yield return "L272 gate-is-decorative: MissionArrivalNav.Step no longer calls AnswerIsIn, so arms " +
                             "(a) and (b) are proved about a decision the live watch does not make. A Step that " +
                             "re-derives the answer term inline is exactly how the eventless arm gets dropped by " +
                             "somebody tidying the branch back into one shape.";

            // ── (d) the OTHER term is still there ──────────────────────────────────────────────────
            if (!stepCalls.Any(c => c.Name == "MissionHasArrived"))
                yield return "L272 arrival-term-dropped: Step no longer calls MissionHasArrived. The eventless arm " +
                             "skips the RECORD term only — it must still wait for the host's mission to land on " +
                             "this peer's own site, or the watch opens UIStateRosterDeployment around a mission " +
                             "this peer does not have.";
            if (!Program.CalleeSequence(step).Any(c => c != null && c.Name == "Announce" &&
                                                       c.DeclaringType != null &&
                                                       c.DeclaringType.Name == "DeployPrep"))
                yield return "L272 arrival-term-dropped: Step does not publish the arrived mission's exact " +
                             "durable preparation door.";

            // ── (e) the eventless arm is bounded, and its clock starts at the click ─────────────────
            if (!Program.CalleeSequence(armHaven).Any(c => c != null && c.Name == "get_realtimeSinceStartup"))
                yield return "L272 eventless-watch-unbounded: WatchHavenMission does not stamp " +
                             "Time.realtimeSinceStartup. An event-backed watch gets its clock when the record " +
                             "resolves; an eventless one has no record, so if it is armed with ResolvedAt = 0 " +
                             "then ArrivalGivenUp can never fire and 'waiting for the host' becomes waiting " +
                             "forever, with no line in the log to say so.";
            if (!MissionArrivalNav.ArrivalGivenUp(1f, 1f + MissionArrivalNav.ArrivalWindowSeconds))
                yield return "L272 eventless-watch-unbounded: ArrivalGivenUp no longer fires at " +
                             MissionArrivalNav.ArrivalWindowSeconds + "s past the stamp, so the bound both arms " +
                             "rest on does not exist.";

            // ── (f) POSITIVE CONTROL, executed ─────────────────────────────────────────────────────
            if (!Table(FakeSeam.AlwaysNeedsARecord, "control").Any())
                yield return "L272 control-not-red: FakeSeam.AlwaysNeedsARecord makes an eventless watch wait for " +
                             "a record that will never exist, and the table did not flag it. Arms (a)/(b) are " +
                             "decorative — they would stay green over the watch that never opens a screen.";
        }

        private static IEnumerable<string> Table(Func<string, bool, bool> answerIsIn, string id)
        {
            if (!answerIsIn(null, false))
                yield return id + " eventless-watch-waits-for-a-record: a watch with a NULL EventId is made to " +
                             "wait for a resolved record. It was armed from THIS PEER'S OWN CLICK on a haven " +
                             "(HavenMissionGate) — there is no GeoscapeEvent behind it and there never will be, " +
                             "so EventPopup.LiveRecord answers nothing forever, Step returns every tick, and the " +
                             "squad screen simply never opens. No exception, no log line: the silent swallow this " +
                             "repo's dominant bug class is made of.";
            if (id != "control" && answerIsIn("EV_HAVEN_TEST", false))
                yield return id + " event-arm-withdrawn: an EVENT-backed watch no longer waits for its record. " +
                             "The eventless arm is an ADDITION, not a replacement — a mission-start encounter may " +
                             "still be open on another peer's screen, and opening the squad screen before anybody " +
                             "answered it navigates this peer off a window it was shown.";
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the answer term as it stood before the eventless arm — a record
            /// is always required, so a haven watch waits for one that will never be written.</summary>
            internal static bool AlwaysNeedsARecord(string eventId, bool recordResolved) => recordResolved;
        }
    }
}
