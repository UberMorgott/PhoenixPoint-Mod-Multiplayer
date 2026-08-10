using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L163 — A NOTIFICATION IS REVIEWED ON THE MAP; IT NEVER TAKES A SCREEN THE PLAYER OPENED.
    ///
    /// THE REPORT (2026-08-06, owner, verbatim in substance): "popups now YANK the host out of whatever menu
    /// he is in and BLOCK him until he dismisses them. The design is the opposite: popups ACCUMULATE and are
    /// reviewed when the player reaches the geoscape."
    ///
    /// THE ROOT, and it is a rule this repo DELETED rather than a line it broke. In vanilla the drain is
    /// unguarded — <c>GeoscapeView.Update</c>:1358 → <c>ProcessQueriedStateSwitch</c>:71 pushes the head on
    /// top of whatever state is current, every frame — and that is harmless for one reason only: every
    /// full-screen section STOPS THE CLOCK on entry (<c>UIModuleGeoSectionBar</c>:119/:135/:148/:160,
    /// <c>UIStateResearch</c>:22, <c>UIStateManufacturing</c>:51, <c>UIStateDiplomacy</c>:27,
    /// <c>UIStatePhoenixBaseLayout</c>:43, <c>UIStateGeoscapeLog</c>:18), so a solo player's sim raises
    /// nothing while they read and there is never anything queued to push. Co-op removed that guarantee ON
    /// PURPOSE in the 2026-08-04 pause rework: resume is unconditional from any peer (<c>PauseHold</c>'s
    /// whole class doc), so another player's play button restarts this peer's sim while they are inside the
    /// research tree, and every event the sim then raises lands on top of them. The clock used to hold the
    /// queue. Nothing replaced it until <see cref="WindowOrder.HoldsForOpenScreen"/>.
    ///
    /// WHY NO EXISTING LAW SAW IT. L48 asserts that every window the game pushes has a REVIEWED ANSWER and
    /// L49 that the modal family is covered — both about WHICH windows cross, neither about WHEN one is
    /// allowed to take the screen. L93/L124 assert ORDER among queued windows, which is a different axis
    /// entirely and stays true either way. L91 asserts nothing waits on another peer, and this hold does
    /// not: it reads this peer's own view state and the request's own priority, and it is released by the
    /// local player's own next click.
    ///
    /// THE ARMS, all EXECUTED over the real pure predicate and the real game types:
    ///   (a) <c>screen-interrupted</c> — an event-family priority (0 / 10 / 15, the three
    ///       <c>GeoscapeView.OnGeoscapeEventRaised</c>:2044-2059 mints) and the resupply rank are HELD while
    ///       a player-opened screen is current.
    ///   (b) <c>map-held</c>, THE NON-VACUITY HALF — the same priorities DRAIN on every declared map state
    ///       and on a null state. A predicate that answered "hold" everywhere would satisfy (a) and would
    ///       mean no window ever opens again, which is strictly worse than the bug.
    ///   (c) <c>transition-held</c> — <c>int.MaxValue</c> (the squad screen, L144; the mission-outcome
    ///       modal) and <c>TransitionPriority</c> itself (a cutscene) DRAIN even on a screen. A transition
    ///       is not a notification, and holding one would break L144 while every arm of L144 stayed green:
    ///       L144 asks whether the squad screen is SERVABLE, not whether this peer's own drain let it out.
    ///   (e) <c>deployment-yanks</c> — the CARVE-OUT (owner's ruling 2026-08-07): the squad screen is held
    ///       on a screen despite its <c>int.MaxValue</c>, and is NOT held on the map. Arms (a)-(c) all pass
    ///       an ordinary window, so every one of them stays green if the carve-out is dropped — this is the
    ///       only arm that fails when another peer's mission launch starts yanking again.
    ///   (d) <c>review-family-misranked</c> — <c>UIStateReplenish</c>, AT ITS REAL RANK, is held on a screen
    ///       and not on the map. Arm (a) passes an ordinary window, so it would keep passing for the event
    ///       family while the one window the owner reports by name silently went back to interrupting.
    ///       Rewritten 2026-08-10 from "rank &lt; TransitionPriority" (shape) to this (outcome) when the rank
    ///       became <c>int.MaxValue</c> and the hold moved to the <c>HeldTransitionStates</c> carve-out.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • drop the <c>priority &lt; TransitionPriority</c> clause → <c>transition-held</c>
    ///   • drop the <c>!MapStates.Contains</c> clause → <c>map-held</c>
    ///   • return <c>false</c> unconditionally (i.e. delete the hold) → <c>screen-interrupted</c>
    ///   • add <c>UIStateResearch</c> to <c>MapStates</c> → <c>screen-interrupted</c>
    ///   • remove <c>"UIStateReplenish"</c> from <c>HeldTransitionStates</c> → <c>review-family-misranked</c>
    ///   • empty <c>HeldTransitionStates</c>, or make it match every state → <c>deployment-yanks</c>
    /// </summary>
    internal static class L163_NotificationWaitsForTheMap
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>The three priorities <c>OnGeoscapeEventRaised</c> actually mints (:2044 ordinary / :2044
        /// triggered-by-event / :2051 superseding) plus the resupply rank — the whole review family.</summary>
        /// (The resupply rank left this list on 2026-08-10: it is int.MaxValue now, i.e. transition band,
        /// and arm (d) below asserts its hold over the real UIStateReplenish rather than by band.)
        private static readonly int[] ReviewPriorities = { 0, 10, 15 };

        internal static IEnumerable<string> Check()
        {
            var predicate = typeof(WindowOrder).GetMethod("HoldsForOpenScreen", AllMembers);
            var mapStates = typeof(WindowOrder).GetField("MapStates", AllMembers);
            var heldTransitions = typeof(WindowOrder).GetField("HeldTransitionStates", AllMembers);
            if (predicate == null || mapStates == null || heldTransitions == null)
            {
                yield return "L163 premise-changed: WindowOrder.HoldsForOpenScreen / WindowOrder.MapStates / " +
                             "WindowOrder.HeldTransitionStates did not resolve. The open-screen hold has been renamed or removed, so every arm below " +
                             "would pass vacuously — and the failure it defends against is the silent kind: the " +
                             "queue simply goes back to draining onto whatever screen the player has open, with " +
                             "no log line anywhere saying so.";
                yield break;
            }

            // The screens the report is about — all real game types, so a rename in the game breaks this law
            // rather than quietly narrowing it.
            var screens = new[]
            {
                typeof(UIStateResearch), typeof(UIStateManufacturing), typeof(UIStatePhoenixBaseLayout),
                typeof(UIStateDiplomacy), typeof(UIStateGeoscapeLog), typeof(UIStateEditSoldier),
                typeof(UIStateGeoRoster), typeof(UIStateInterception),
            };
            var map = new[] { typeof(UIStateNothingSelected), typeof(UIStateVehicleSelected),
                              typeof(UIStateInitial), typeof(UIStateLoading) };
            // An ORDINARY queued window — not one the deployment carve-out names — so arms (a)-(c) keep
            // testing the PRIORITY rule and cannot pass on the carve-out by accident.
            var ordinary = typeof(UIStateGeoscapeEvent);

            // ── (a) the review family is HELD on a screen ───────────────────────────────────────────
            foreach (var screen in screens)
                foreach (var priority in ReviewPriorities)
                    if (!Hold(predicate, priority, ordinary, screen))
                        yield return "L163 screen-interrupted: a queued window at priority " + priority +
                                     " drains while " + screen.Name + " is the current view state. That is the " +
                                     "reported defect verbatim — the player is thrown out of a screen they " +
                                     "opened by a notification the design says accumulates until they are back " +
                                     "on the map. In vanilla this state pauses the clock and nothing can queue " +
                                     "behind it; in co-op any peer resumes that clock unconditionally.";

            // ── (b) NON-VACUITY: the same family DRAINS on the map, and with no screen at all ────────
            foreach (var mapState in map)
                foreach (var priority in ReviewPriorities)
                    if (Hold(predicate, priority, ordinary, mapState))
                        yield return "L163 map-held: a queued window at priority " + priority + " is held while " +
                                     mapState.Name + " is current — i.e. on the geoscape itself, which is the " +
                                     "one place the design says a notification IS reviewed. A predicate that " +
                                     "holds everywhere makes arm (a) meaningless and loses every window in the " +
                                     "session; " + mapState.Name + " is also where the queue would have to " +
                                     "release, so nothing would ever release it.";

            if (Hold(predicate, 0, ordinary, null))
                yield return "L163 map-held: the head is held while there is NO current view state at all. " +
                             "There is no screen to protect and nothing to return to, so a hold there is a " +
                             "queue that never drains — the failure mode this law's own fix must not become.";

            // ── (c) a TRANSITION is not a notification ───────────────────────────────────────────────
            foreach (var screen in screens)
                foreach (var t in new[] { (WindowOrder.TransitionPriority, (Type)typeof(UIStateGeoCutscene)),
                                          (int.MaxValue, typeof(UIStateGeoModal)) })
                    if (Hold(predicate, t.Item1, t.Item2, screen))
                        yield return "L163 transition-held: " + t.Item2.Name + " at priority " + t.Item1 +
                                     " is held while " + screen.Name + " is open. These are the transitions the " +
                                     "carve-out does NOT name — a cutscene and the mission-outcome modal are the " +
                                     "game ENDING something rather than offering it, and neither may wait on a " +
                                     "player's navigation. Only UIStateRosterDeployment is held up here, and arm " +
                                     "(e) is what says so.";

            // ── (e) THE CARVE-OUT: the squad screen is held too, and only it ────────────────────────
            // Owner's ruling 2026-08-07 — another peer's "start mission" pulled this peer off the research
            // screen, because ToDeploymentState:596 queues UIStateRosterDeployment at int.MaxValue and the
            // priority band alone exempted it. Asserted here rather than left to the predicate's own doc:
            // arms (a)-(c) all pass an ORDINARY window, so every one of them stays green if the carve-out
            // is dropped, and the yank comes straight back on the one window the owner named.
            foreach (var screen in screens)
                if (!Hold(predicate, int.MaxValue, typeof(UIStateRosterDeployment), screen))
                    yield return "L163 deployment-yanks: UIStateRosterDeployment drains while " + screen.Name +
                                 " is open. Priority answers \"which queued window goes first\", never \"may " +
                                 "this window take a screen the player opened\" — and reading it for both is " +
                                 "exactly what let another peer's mission launch throw this peer out of the " +
                                 "screen he was working in. Holding it costs him nothing: the battle is " +
                                 "launched through the host's save transfer, which curtains every peer whatever " +
                                 "its queue holds.";
            foreach (var mapState in map)
                if (Hold(predicate, int.MaxValue, typeof(UIStateRosterDeployment), mapState))
                    yield return "L163 deployment-yanks: UIStateRosterDeployment is held while " +
                                 mapState.Name + " is current — on the map, where the player has no screen to " +
                                 "protect and the squad screen is exactly what he is waiting for. The carve-out " +
                                 "must widen WHICH windows the hold covers, never WHERE it engages (L144: the " +
                                 "squad screen still has to be reachable).";

            // ── (d) the resupply screen still waits for the map ─────────────────────────────────────
            // OUTCOME, NOT SHAPE (rewritten 2026-08-10). This arm used to read "ReplenishRank <
            // TransitionPriority" as metadata, i.e. it asserted the MECHANISM by which the resupply screen
            // is held. The owner then ranked that screen FIRST ("первым после миссии", ReplenishRank =
            // int.MaxValue) and the mechanism changed to the HeldTransitionStates carve-out the squad screen
            // already used — a CORRECT change that failed the law until the law was edited, the L127(d)
            // trap. What the 2026-08-07 report actually demands is the behaviour below, and it is now driven
            // over the real predicate at the real rank.
            int replenishRank = ConstOf(typeof(ReplenishSync), "ReplenishRank");
            var replenishState = typeof(UIStateReplenish);
            foreach (var screen in screens)
                if (!Hold(predicate, replenishRank, replenishState, screen))
                    yield return "L163 review-family-misranked: UIStateReplenish ranked " + replenishRank +
                                 " is NOT held while " + screen.Name + " is open. The resupply screen is a " +
                                 "REVIEW window — the owner names it in the 2026-08-07 report — so it must " +
                                 "wait for the map however it is ranked; a promotion that also promotes the " +
                                 "YANK puts it back to pulling the player off a screen he opened.";
            foreach (var mapState in map)
                if (Hold(predicate, replenishRank, replenishState, mapState))
                    yield return "L163 review-family-misranked: UIStateReplenish is held while " +
                                 mapState.Name + " is current — on the map there is no screen to protect and " +
                                 "the resupply screen is the whole point of the arrival.";
        }

        private static int ConstOf(Type owner, string name)
        {
            var f = owner.GetField(name, AllMembers);
            return f == null ? int.MinValue : Convert.ToInt32(f.GetRawConstantValue());
        }

        /// <summary><paramref name="queued"/> is the window being offered, <paramref name="state"/> the
        /// screen the player is on — the predicate reads BOTH since the deployment carve-out went in.</summary>
        private static bool Hold(MethodInfo predicate, int priority, Type queued, Type state) =>
            (bool)predicate.Invoke(null, new object[] { priority, queued, state });
    }
}
