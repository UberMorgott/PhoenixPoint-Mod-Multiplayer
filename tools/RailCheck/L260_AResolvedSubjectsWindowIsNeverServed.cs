using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Base.UI;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L260 — A QUEUED WINDOW WHOSE SUBJECT HAS ALREADY RESOLVED IS NEVER SERVED.
    ///
    /// THE REPORT (2026-08-08). Coming back from a battle, the host was met by the START-MISSION window for
    /// the mission it had just finished. Host multiplayer.log:3378, one clock: the restore filter reported
    /// "1 entries in the save, 1 kept — 0 dropped (subject already resolved)" at 02:49:11.734, and that same
    /// mission only resolved at 02:49:12.015 (`[MP][outcome] … activeMission=none`; structural destroy of
    /// `S#213.SerializationData.ActiveMission` at :12.254). The filter asked the right question 281 ms before
    /// the answer existed, and asked it exactly once.
    ///
    /// WHY L117 WAS GREEN THROUGH ALL OF IT, which is the reason this law exists rather than a new arm there.
    /// L117 asserts that the filter is a Prefix on <c>RestoreData</c>, that <c>HasResolved</c> reads
    /// <c>IsCompleted</c> and <c>GetMissionOutcomeState</c>, and that the filter calls <c>SubjectMission</c>
    /// rather than switching on <c>ModalType</c>. Every one of those was true while the stale window was on
    /// screen: it is a law about the CALL and the SHAPE, and the defect was in the MOMENT. Nothing in L117
    /// can distinguish "filtered correctly" from "filtered before the fact".
    ///
    /// SO THIS LAW ASSERTS THE OUTCOME AT THE MOMENT THAT DECIDES IT — the SERVE.
    /// <c>GeoscapeViewSwitchQuery.RestoreData</c> only REBUILDS <c>_viewStateSwitchRequests</c>; entries are
    /// popped one at a time later, by <c>ProcessQueriedStateSwitch</c>:57-70. Arms (a)-(d) drive the REAL
    /// <see cref="WindowOrder.DropResolvedSubjects"/> over a REAL
    /// <c>List&lt;GeoscapeViewStateSwitchRequest&gt;</c>, and (c) does it end to end through the REAL
    /// production verdict over a REAL <c>GeoMission</c> whose <c>IsCompleted</c> is set — no stand-in for the
    /// thing under test.
    ///
    /// (c) ALSO EXECUTES THE NO-QUORUM AND CONVERGENCE PROPERTY, for free and not by assertion: RailCheck
    /// runs with no <c>NetworkEngine</c>, no session, no peer and no roster, and the resolved window is
    /// dropped anyway. A verdict that still lands with nothing peer-shaped in reach cannot be waiting on a
    /// peer to agree, and cannot answer differently on the host than on a client.
    ///
    ///   (a) <c>stale-window-is-served</c> — the marked entry is REMOVED and the others survive, in order.
    ///   (b) <c>live-window-is-dropped</c> — nothing marked, nothing removed. A pruner that eats the queue is
    ///       worse than the bug it fixes.
    ///   (c) <c>resolved-subject-survives-the-drain</c> / <c>unknown-subject-is-dropped</c> — the REAL
    ///       <c>ResolvedSubjectName</c> over a real completed mission carried in a real
    ///       <c>UIStateGeoModal._modalData</c>: dropped, and its no-subject twin KEPT.
    ///   (d) <c>prune-off-the-drain-seam</c> — the pruner is reached from the game's ONE drain, so it is
    ///       asked on every frame the queue is pumped rather than once at restore.
    ///   (e) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> is a pruner that removes nothing; arm (a)'s
    ///       assertions are run against it and MUST come back red.
    ///
    /// Falsify (each verified RED, then restored): make <c>DropResolvedSubjects</c> return without removing
    /// → (a) + (e)'s counterpart; make it remove every entry → (b); make <c>ResolvedSubjectName</c> return
    /// null always → (c) first half; make it return a name for a mission-less holder → (c) second half; drop
    /// the <c>DropResolvedSubjects</c> call out of <c>ReadyToDequeue</c> → (d); give <see cref="FakeSeam"/> a
    /// working body → (e).
    /// </summary>
    internal static class L260_AResolvedSubjectsWindowIsNeverServed
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var prune = typeof(WindowOrder).GetMethod("DropResolvedSubjects", All);
            var restore = typeof(MissionSync).Assembly.GetType("Multiplayer.Network.Sync.RestoreDropsResolvedSubjects");
            var verdict = restore?.GetMethod("ResolvedSubjectName", All);
            var gate = typeof(WindowOrder).GetMethod("ReadyToDequeue", All);
            if (prune == null || verdict == null || gate == null)
            {
                yield return "L260 premise-changed: WindowOrder.DropResolvedSubjects, " +
                             "RestoreDropsResolvedSubjects.ResolvedSubjectName or WindowOrder.ReadyToDequeue no " +
                             "longer resolves. The staleness verdict was split out of the restore call site " +
                             "precisely so the SERVE moment could be executed rather than read; folding it back " +
                             "puts every arm below out of reach and takes the 2026-08-08 stale-brief bug with it.";
                yield break;
            }

            // ── (a)+(b) the real pruner over a real queue ──────────────────────────────────────────
            foreach (var red in PrunesTheMarkedEntry(
                         (list, namer) => (List<string>)prune.Invoke(null, new object[] { list, namer }), "L260"))
                yield return red;

            // ── (e) POSITIVE CONTROL, executed: the same assertions over a pruner that does nothing ──
            var control = PrunesTheMarkedEntry(FakeSeam.DropResolvedSubjects, "control").ToList();
            if (control.Count == 0)
                yield return "L260 control-not-red: FakeSeam.DropResolvedSubjects removes NOTHING and arm (a) " +
                             "still passed. The arm is decorative — it would stay green over a pruner that " +
                             "never drops the stale window, which is the whole defect.";

            // ── (c) the REAL production verdict, end to end over a REAL completed GeoMission ────────
            var mission = CompletedMission();
            if (mission == null)
            {
                yield return "L260 premise-changed: no concrete GeoMission could be built with IsCompleted set " +
                             "(the '<IsCompleted>k__BackingField' auto-property backing field, GeoMission:149). " +
                             "Arm (c) is the only one that executes the production verdict rather than a stand-in, " +
                             "so it cannot be quietly skipped.";
            }
            else
            {
                var namer = (Func<object, string>)Delegate.CreateDelegate(typeof(Func<object, string>), verdict);

                var carrying = new List<GeoscapeViewStateSwitchRequest> { ModalRequest(mission) };
                if (carrying[0] == null)
                    yield return "L260 premise-changed: a UIStateGeoModal carrying a mission in _modalData " +
                                 "could not be built (UIStateGeoModal:46). That field IS the mission brief's " +
                                 "subject — GeoWindowCoverage declares the ten brief ModalTypes Mirrored with " +
                                 "'modalData = the live GeoMission'.";
                else
                {
                    prune.Invoke(null, new object[] { carrying, namer });
                    if (carrying.Count != 0)
                        yield return "L260 resolved-subject-survives-the-drain: a queued UIStateGeoModal whose " +
                                     "_modalData is a COMPLETED GeoMission was still in the queue after the " +
                                     "drain filter ran. That is the 2026-08-08 report exactly: the host came " +
                                     "back from the battle and was shown the start-mission window for the " +
                                     "mission it had just won. Note what this arm did NOT need: a session, a " +
                                     "host, a peer, a roster. The verdict has to land on state this peer holds " +
                                     "alone, or peers cannot converge without negotiating.";
                }

                var blank = new List<GeoscapeViewStateSwitchRequest> { ModalRequest(null) };
                if (blank[0] != null)
                {
                    prune.Invoke(null, new object[] { blank, namer });
                    if (blank.Count != 1)
                        yield return "L260 unknown-subject-is-dropped: a queued window carrying NO mission was " +
                                     "removed. Only a window whose subject can be READ and found resolved may " +
                                     "go; a filter that drops what it cannot name deletes the player's window " +
                                     "history — and every event window in the game (UIStateGeoscapeEvent and " +
                                     "its three siblings hold no GeoMission at all) falls in that class, " +
                                     "including the post-mission reward events raised seconds after the very " +
                                     "mission this filter is about.";
                }
            }

            // ── (d) the pruner is reached from the game's ONE drain ────────────────────────────────
            var mod = typeof(WindowOrder).Assembly;
            if (!Program.Callees(gate, mod).Any(c => c is MethodInfo mi && mi.Name == "DropResolvedSubjects"))
                yield return "L260 prune-off-the-drain-seam: ReadyToDequeue does not reach " +
                             "DropResolvedSubjects. ReadyToDequeue is the prefix on the game's single drain " +
                             "(GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch, pumped by " +
                             "GeoscapeView.Update:1358); anywhere else and the question is asked once, at a " +
                             "moment that measured 281 ms too early on 2026-08-08, instead of on every frame " +
                             "between queueing a window and showing it.";

            var patch = typeof(WindowOrder).GetNestedType("QueueSettlePatch", All);
            var target = patch?.GetCustomAttributes(typeof(HarmonyPatch), false)
                              .OfType<HarmonyPatch>().FirstOrDefault();
            if (target?.info?.declaringType != typeof(GeoscapeViewSwitchQuery) ||
                target?.info?.methodName != nameof(GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch))
                yield return "L260 prune-off-the-drain-seam: WindowOrder.QueueSettlePatch no longer patches " +
                             "GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch. That method is where a queued " +
                             "window is actually SERVED — RestoreData only rebuilds the list — so a filter that " +
                             "is not on it cannot be a serve-time filter no matter what it checks.";
        }

        /// <summary>THE OUTCOME, run against whatever pruner is handed in — the production one in arm (a) and
        /// the deliberately-inert <see cref="FakeSeam"/> in arm (e). Same assertions both times, which is what
        /// makes the control a control rather than a second law.</summary>
        private static IEnumerable<string> PrunesTheMarkedEntry(
            Func<IList<GeoscapeViewStateSwitchRequest>, Func<object, string>, List<string>> prune, string id)
        {
            // Three DISTINCT states, so the namer can key on identity rather than on call order — the pruner
            // walks the list backwards and a position-keyed marker would silently follow that walk instead of
            // testing it.
            var keepA = ModalRequest(null);
            var stale = ModalRequest(null);
            var keepB = ModalRequest(null);
            if (keepA == null || stale == null || keepB == null)
            {
                yield return id + " premise-changed: three distinct GeoscapeViewStateSwitchRequests around " +
                             "UIStateGeoModal could not be built, so the outcome arms cannot run at all.";
                yield break;
            }
            Func<object, string> marks = holder => ReferenceEquals(holder, stale.State) ? "PROG_AN0" : null;

            var list = new List<GeoscapeViewStateSwitchRequest> { keepA, stale, keepB };
            var dropped = prune(list, marks);

            if (list.Count != 2)
                yield return id + " stale-window-is-served: the pruner left " + list.Count + " of 3 entries " +
                             "when exactly one was marked resolved. A window for a battle that is already over " +
                             "must not reach the screen — that is the whole 2026-08-08 report.";
            else if (!ReferenceEquals(list[0], keepA) || !ReferenceEquals(list[1], keepB))
                yield return id + " stale-window-is-served: the pruner removed the wrong entry or reordered the " +
                             "survivors. Order is the queue's own (priority, then rail ordinal, WindowOrder" +
                             ".Compare); a filter that shuffles it re-opens the 2026-08-05 window-order report " +
                             "while pretending to fix a different one.";
            if (dropped == null || dropped.Count != 1)
                yield return id + " stale-window-is-served: the pruner reported " +
                             (dropped == null ? "null" : dropped.Count.ToString()) + " dropped windows for one " +
                             "removal. The caller logs from this list, and a silent drop is exactly as bad as a " +
                             "silent replay — the player is owed the reason a window they expected never came.";

            var untouched = new List<GeoscapeViewStateSwitchRequest> { keepA, stale, keepB };
            var none = prune(untouched, holder => null);
            if (untouched.Count != 3 || (none != null && none.Count != 0))
                yield return id + " live-window-is-dropped: with NO entry marked resolved the pruner still " +
                             "removed " + (3 - untouched.Count) + ". Every window whose subject cannot be read " +
                             "as resolved stays: the filter's whole licence is that it can name the dead " +
                             "mission it is dropping.";
        }

        /// <summary>A real request around an UNINITIALIZED <c>UIStateGeoModal</c> whose <c>_modalData</c> is
        /// the given subject — the live shape of a mission brief (GeoWindowCoverage:260-280). The game's own
        /// constructor is skipped for the same reason L175 skips it: it reaches a live campaign, and the only
        /// property under test is the subject the state carries.</summary>
        private static GeoscapeViewStateSwitchRequest ModalRequest(object subject)
        {
            try
            {
                var state = FormatterServices.GetUninitializedObject(typeof(UIStateGeoModal));
                var data = typeof(UIStateGeoModal).GetField("_modalData", All);
                if (data == null) return null;
                data.SetValue(state, subject);
                return state is IState<GeoscapeViewContext> s ? new GeoscapeViewStateSwitchRequest(s, 0) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>A REAL <c>GeoMission</c> (first concrete subclass the game ships) with the game's own
        /// completion flag set. <c>HasResolved</c> reads <c>IsCompleted</c> FIRST, so nothing else on the
        /// object is touched and no live level is needed.</summary>
        private static GeoMission CompletedMission()
        {
            try
            {
                var concrete = typeof(GeoMission).Assembly.GetTypes()
                    .FirstOrDefault(t => !t.IsAbstract && typeof(GeoMission).IsAssignableFrom(t));
                var flag = typeof(GeoMission).GetField("<IsCompleted>k__BackingField", All);
                if (concrete == null || flag == null) return null;
                var mission = (GeoMission)FormatterServices.GetUninitializedObject(concrete);
                flag.SetValue(mission, true);
                return mission.IsCompleted ? mission : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>THE POSITIVE CONTROL. A pruner that answers the same shape and does nothing — the exact
        /// regression arm (a) exists to catch. If arm (a) passes over THIS, arm (a) proves nothing.</summary>
        private static List<string> DropNothing(IList<GeoscapeViewStateSwitchRequest> pending,
                                                Func<object, string> resolvedSubjectName) => new List<string>();

        private static class FakeSeam
        {
            internal static readonly Func<IList<GeoscapeViewStateSwitchRequest>, Func<object, string>, List<string>>
                DropResolvedSubjects = DropNothing;
        }
    }
}
