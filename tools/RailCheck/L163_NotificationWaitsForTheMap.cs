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
    ///   (d) <c>review-family-misranked</c> — <c>ReplenishSync.ReplenishRank</c> is strictly below
    ///       <c>TransitionPriority</c>, so the resupply screen is in the family this law holds. If a later
    ///       re-rank pushed it over the line, arm (a) would keep passing for the event family while the one
    ///       window the owner reports by name silently went back to interrupting.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • drop the <c>priority &lt; TransitionPriority</c> clause → <c>transition-held</c>
    ///   • drop the <c>!MapStates.Contains</c> clause → <c>map-held</c>
    ///   • return <c>false</c> unconditionally (i.e. delete the hold) → <c>screen-interrupted</c>
    ///   • add <c>UIStateResearch</c> to <c>MapStates</c> → <c>screen-interrupted</c>
    ///   • raise <c>ReplenishSync.ReplenishRank</c> to 100 → <c>review-family-misranked</c>
    /// </summary>
    internal static class L163_NotificationWaitsForTheMap
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>The three priorities <c>OnGeoscapeEventRaised</c> actually mints (:2044 ordinary / :2044
        /// triggered-by-event / :2051 superseding) plus the resupply rank — the whole review family.</summary>
        private static readonly int[] ReviewPriorities = { 0, 10, 15, ReplenishSync.ReplenishRank };

        internal static IEnumerable<string> Check()
        {
            var predicate = typeof(WindowOrder).GetMethod("HoldsForOpenScreen", AllMembers);
            var mapStates = typeof(WindowOrder).GetField("MapStates", AllMembers);
            if (predicate == null || mapStates == null)
            {
                yield return "L163 premise-changed: WindowOrder.HoldsForOpenScreen / WindowOrder.MapStates did " +
                             "not resolve. The open-screen hold has been renamed or removed, so every arm below " +
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

            // ── (a) the review family is HELD on a screen ───────────────────────────────────────────
            foreach (var screen in screens)
                foreach (var priority in ReviewPriorities)
                    if (!Hold(predicate, priority, screen))
                        yield return "L163 screen-interrupted: a queued window at priority " + priority +
                                     " drains while " + screen.Name + " is the current view state. That is the " +
                                     "reported defect verbatim — the player is thrown out of a screen they " +
                                     "opened by a notification the design says accumulates until they are back " +
                                     "on the map. In vanilla this state pauses the clock and nothing can queue " +
                                     "behind it; in co-op any peer resumes that clock unconditionally.";

            // ── (b) NON-VACUITY: the same family DRAINS on the map, and with no screen at all ────────
            foreach (var mapState in map)
                foreach (var priority in ReviewPriorities)
                    if (Hold(predicate, priority, mapState))
                        yield return "L163 map-held: a queued window at priority " + priority + " is held while " +
                                     mapState.Name + " is current — i.e. on the geoscape itself, which is the " +
                                     "one place the design says a notification IS reviewed. A predicate that " +
                                     "holds everywhere makes arm (a) meaningless and loses every window in the " +
                                     "session; " + mapState.Name + " is also where the queue would have to " +
                                     "release, so nothing would ever release it.";

            if (Hold(predicate, 0, null))
                yield return "L163 map-held: the head is held while there is NO current view state at all. " +
                             "There is no screen to protect and nothing to return to, so a hold there is a " +
                             "queue that never drains — the failure mode this law's own fix must not become.";

            // ── (c) a TRANSITION is not a notification ───────────────────────────────────────────────
            foreach (var screen in screens)
                foreach (var priority in new[] { WindowOrder.TransitionPriority, int.MaxValue })
                    if (Hold(predicate, priority, screen))
                        yield return "L163 transition-held: priority " + priority + " is held while " +
                                     screen.Name + " is open. That band is not the review family — it is the " +
                                     "squad screen (int.MaxValue, L144), the mission-outcome modal " +
                                     "(int.MaxValue) and the cutscene (100). Holding a mission launch behind a " +
                                     "player who happens to be in the base layout is a peer waiting on another " +
                                     "peer's navigation, which is the one thing this hold must never become.";

            // ── (d) the resupply screen is in the family this law holds ─────────────────────────────
            // Read as METADATA, not as the compile-time literals: two consts compared in source fold to a
            // constant and the compiler drops the arm, which is a law that cannot fail by construction.
            int replenishRank = ConstOf(typeof(ReplenishSync), "ReplenishRank");
            int transition = ConstOf(typeof(WindowOrder), "TransitionPriority");
            if (replenishRank >= transition)
                yield return "L163 review-family-misranked: ReplenishSync.ReplenishRank is " +
                             replenishRank + ", at or above WindowOrder.TransitionPriority (" +
                             transition + "). The resupply screen is a REVIEW window — the " +
                             "owner names it in the same report — so a rank above the line puts it back to " +
                             "interrupting an open screen while every other arm here stays green.";
        }

        private static int ConstOf(Type owner, string name)
        {
            var f = owner.GetField(name, AllMembers);
            return f == null ? int.MinValue : Convert.ToInt32(f.GetRawConstantValue());
        }

        private static bool Hold(MethodInfo predicate, int priority, Type state) =>
            (bool)predicate.Invoke(null, new object[] { priority, state });
    }
}
