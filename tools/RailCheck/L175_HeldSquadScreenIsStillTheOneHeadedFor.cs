using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Base.UI;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L175 — THE HOLD IS ASKED ABOUT THE WINDOW IT IS ACTUALLY ABOUT TO SERVE, AND A SQUAD SCREEN SITTING
    /// IN THE QUEUE IS STILL A SQUAD SCREEN THIS PEER IS "HEADED FOR".
    ///
    /// THE REPORT (2026-08-07, owner). A partner flew to a point of interest, got the "start mission?"
    /// window, pressed start — and the HOST WAS RIPPED OUT OF THE RESEARCH SCREEN he was working in and
    /// dropped onto the mission-start window. The ruling: the deployment window belongs in the WINDOW
    /// HISTORY like every other window; it accumulates, and the player reaches it when HE leaves his screen.
    ///
    /// WHY <c>19af84c</c>'s HOLD DID NOT HOLD IT, AND WHY L163 WAS GREEN THROUGH THAT. <c>WindowOrder
    /// .HoldsForOpenScreen</c> was written the same day and read ONE axis — the request's priority. The
    /// squad screen is queued at <c>int.MaxValue</c> (<c>GeoscapeView.ToDeploymentState</c>:596), so it fell
    /// on the TRANSITION side of the line and was exempt BY DESIGN, and L163 arm (c) asserted that exemption
    /// in so many words ("int.MaxValue … DRAIN even on a screen"). The law was not silent about the yank; it
    /// REQUIRED it. Priority is the game's answer to "which of two queued windows goes first" and was never
    /// an answer to "may this window take a screen the player opened" — reading it for both is the whole
    /// defect. L163's own arm (c) has been REPAIRED rather than baselined (an ordinary window now carries
    /// arms (a)-(c); its new arm (e) executes the deployment carve-out in both directions), so THIS law does
    /// not repeat it. It asserts the two things a green L163 still cannot see.
    ///
    ///   (a) <c>hold-not-asked-of-the-window</c> — the predicate is PURE and L163 executes it over synthetic
    ///       arguments. Nothing there says the DRAIN GATE passes it the real head's real type.
    ///       <c>ReadyToDequeue</c> could keep calling it with a null queued state (or stop reading
    ///       <c>GeoscapeViewStateSwitchRequest.State</c> at all) and every L163 arm would stay green while
    ///       the yank came straight back in game. This is P14's rule applied to its own repair: assert the
    ///       executed OUTCOME, not that a correct function exists somewhere.
    ///
    ///   (b) <c>unknown-head-held-forever</c>, THE NON-VACUITY AND THE SAFETY HALF IN ONE. The carve-out
    ///       must be keyed on the STATE TYPE, not degenerate into "hold every transition". A head whose
    ///       type the carve-out does not name — including one with NO state object at all, which is what a
    ///       failed resolve looks like — must still DRAIN at <c>int.MaxValue</c>. Widen the set to
    ///       everything and this arm fails while (a) and L163's screen arms all pass, and the cutscene, the
    ///       game-over state and the mission-outcome modal are held behind a player reading a research tree
    ///       with nothing able to release them.
    ///
    ///   (c) <c>hold-reads-a-peer</c> — the no-quorum containment (P13, L84/L91). The gate around the
    ///       predicate is allowed to ask "are we in a session" and does; the PREDICATE must read this peer's
    ///       own screen and the request's own type and NOTHING about anybody else, or "the deployment window
    ///       waits" quietly becomes "the deployment window waits for a peer".
    ///
    /// THE HAZARD THIS FIX CREATES IS ALREADY OWNED, and no arm is added for it here. A held deployment
    /// request may now sit in the queue for minutes, so <c>MissionEncounterNav</c>'s re-issue of
    /// <c>LaunchMission</c> would double-queue the screen if <c>AlreadyHeadedForDeployment</c> ever narrowed
    /// to "am I IN it" — and <c>L141 already-headed-answered-off-the-books</c> asserts exactly that predicate
    /// still asks the game's own PENDING list. It was verified to fire on this change's own falsification
    /// pass. A second copy here would be the duplicate-law habit this repo is trying to break.
    ///
    /// Falsify (each verified RED, then restored): pass <c>null</c> for the queued type in
    /// <c>HoldsHead</c> → <c>hold-not-asked-of-the-window</c> (twice: the squad request stops being held and
    /// an ordinary one starts being held); make <c>HeldTransitionStates.Contains</c> unconditional (hold
    /// every transition) → <c>unknown-head-held-forever</c> ×3 and L163's own <c>transition-held</c>; have
    /// <c>HoldsForOpenScreen</c> consult <c>NetworkEngine</c> → <c>hold-reads-a-peer</c>; rename any member
    /// → <c>premise-changed</c>.
    /// </summary>
    internal static class L175_HeldSquadScreenIsStillTheOneHeadedFor
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var order = typeof(WindowOrder);
            var mod = order.Assembly;

            var predicate = order.GetMethod("HoldsForOpenScreen", All);
            var holdsHead = order.GetMethod("HoldsHead", All);
            var drain = order.GetMethod("ReadyToDequeue", All);
            var nav = mod.GetType("Multiplayer.Network.Sync.MissionEncounterNav");
            var headed = nav?.GetMethod("AlreadyHeadedForDeployment", All);

            if (predicate == null || holdsHead == null || drain == null || headed == null ||
                predicate.GetParameters().Length != 3)
            {
                yield return "L175 premise-changed: one of WindowOrder.HoldsForOpenScreen (3 parameters), " +
                             "WindowOrder.HoldsHead, WindowOrder.ReadyToDequeue or " +
                             "MissionEncounterNav.AlreadyHeadedForDeployment no longer resolves. The " +
                             "window-history path has been reshaped and every arm below would pass vacuously — " +
                             "which is exactly how the yank survived a law written the same day it landed.";
                yield break;
            }

            // ── (a) the DRAIN GATE asks about the real head, EXECUTED over a real request ──────────
            // The request is built by hand and its state is an UNINITIALIZED UIStateRosterDeployment: the
            // real constructor calls GeoMission.GetDeploymentSources against a live campaign, and the only
            // thing under test here is which TYPE the head carries.
            if (!Program.Callees(drain, mod).Any(c => Same(c, holdsHead)))
                yield return "L175 hold-not-asked-of-the-window: WindowOrder.ReadyToDequeue never calls " +
                             "HoldsHead. The predicate L163 executes is then a pure function nothing consults, " +
                             "the queue drains onto whatever screen the player has open, and every arm of L163 " +
                             "keeps passing over synthetic arguments — a green harness and the reported bug at " +
                             "the same time.";

            var squadRequest = Request(typeof(UIStateRosterDeployment), int.MaxValue);
            var otherRequest = Request(typeof(UIStateGeoModal), int.MaxValue);
            if (squadRequest == null || otherRequest == null)
                yield return "L175 premise-changed: a GeoscapeViewStateSwitchRequest could not be built around " +
                             "an uninitialized view state, so arm (a) cannot be executed at all.";
            else
            {
                if (!(bool)holdsHead.Invoke(null, new object[] { squadRequest, typeof(UIStateResearch) }))
                    yield return "L175 hold-not-asked-of-the-window: HoldsHead let a REAL queued " +
                                 "UIStateRosterDeployment request through while UIStateResearch is open. The " +
                                 "extraction request → (priority, state type) is the whole repair: pass the " +
                                 "priority alone (or a null type) and the carve-out cannot fire, L163 keeps " +
                                 "passing over synthetic arguments, and another peer's mission launch throws " +
                                 "this peer out of the screen he was working in exactly as reported.";
                if ((bool)holdsHead.Invoke(null, new object[] { otherRequest, typeof(UIStateResearch) }))
                    yield return "L175 hold-not-asked-of-the-window: HoldsHead HELD a real int.MaxValue request " +
                                 "that is NOT the squad screen. The extraction must carry the head's own type " +
                                 "through, not a constant — see arm (b) for what holding the rest of that band " +
                                 "costs.";
                if ((bool)holdsHead.Invoke(null, new object[] { null, typeof(UIStateResearch) }))
                    yield return "L175 hold-not-asked-of-the-window: HoldsHead held a NULL head. There is no " +
                                 "window to hold, so this can only be a predicate that stopped reading its " +
                                 "argument — and a queue gate that answers 'hold' without looking never drains.";
            }

            // ── (b) NON-VACUITY + SAFETY: an unnamed head still drains ─────────────────────────────
            var screen = typeof(UIStateResearch);
            if (!Hold(predicate, int.MaxValue, typeof(UIStateRosterDeployment), screen))
                yield return "L175 unknown-head-held-forever: the squad screen itself is NOT held on " +
                             screen.Name + ", so the carve-out this law's arms (a)/(c) are about does not " +
                             "exist and the yank is back. (L163 arm (e) is the primary assertion of this; it " +
                             "is repeated here only so arms (b)-(d) cannot pass while describing nothing.)";
            foreach (var head in new[] { (Type)null, typeof(UIStateGeoCutscene), typeof(UIStateGeoModal) })
                if (Hold(predicate, int.MaxValue, head, screen))
                    yield return "L175 unknown-head-held-forever: a queued " +
                                 (head == null ? "request with NO state object" : head.Name) +
                                 " at int.MaxValue is HELD while " + screen.Name + " is open. The carve-out " +
                                 "must widen the hold by NAMED STATE and by nothing else: a cutscene, the " +
                                 "game-over state and the mission-outcome modal have no player decision behind " +
                                 "them to release, so holding one behind a player who is reading the research " +
                                 "tree is a queue with nothing able to drain it — strictly worse than the yank.";

            // ── (c) the predicate reads this peer and nothing else ─────────────────────────────────
            var peerTypes = new[] { "NetworkEngine", "SessionManager", "PingTable", "PeerListEntry",
                                    "LobbyController" };
            foreach (var c in Program.Callees(predicate, mod))
                if (c.DeclaringType != null && peerTypes.Contains(c.DeclaringType.Name))
                    yield return "L175 hold-reads-a-peer: WindowOrder.HoldsForOpenScreen calls " +
                                 c.DeclaringType.Name + "." + c.Name + ". The moment this predicate reads " +
                                 "another peer, \"the deployment window waits until you leave your screen\" " +
                                 "becomes \"the deployment window waits for somebody else\" — a wait on a " +
                                 "PERSON, which P13 forbids outright (L84/L91). The session gate belongs in " +
                                 "ReadyToDequeue, which is co-op-only on purpose; the predicate must stay a " +
                                 "function of this peer's screen and the request's own type.";
        }

        /// <summary>A real <c>GeoscapeViewStateSwitchRequest</c> around an UNINITIALIZED view state. The game
        /// constructors reach a live campaign (<c>UIStateRosterDeployment</c>:74 calls
        /// <c>GetDeploymentSources</c>), and the only property under test is the type the request carries.</summary>
        private static GeoscapeViewStateSwitchRequest Request(Type stateType, int priority)
        {
            try
            {
                var state = FormatterServices.GetUninitializedObject(stateType) as IState<GeoscapeViewContext>;
                return state == null ? null : new GeoscapeViewStateSwitchRequest(state, priority);
            }
            catch (Exception) { return null; }
        }

        private static bool Hold(MethodInfo predicate, int priority, Type queued, Type state) =>
            (bool)predicate.Invoke(null, new object[] { priority, queued, state });

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
