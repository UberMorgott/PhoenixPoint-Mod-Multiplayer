using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L144 — A PEER THAT IS "HEADED FOR" THE SQUAD SCREEN MUST HAVE NOTHING OF ITS OWN STANDING IN FRONT OF
    /// THE QUEUE. QUEUEING IT BEHIND THIS PEER'S OWN OPEN ENCOUNTER WINDOW IS NOT NAVIGATION, IT IS A STALL.
    ///
    /// THE REPORT (2026-08-06, second session, DLL 998912 B @22:22:30 — the SAME bug L141 was written for,
    /// still reproducing after L141 shipped GREEN). The client pressed the mission choice, got the squad
    /// screen, deployed. The host got the encounter window, pressed its mission choice, and was pulled
    /// straight into the battle with no squad screen. Host log, one clock (Player.log, frame/second):
    ///   6369 / 115,230 s  `Queuerd state switch …UIStateGeoscapeEvent with priority 0`   (the host's window)
    ///   6379 / 115,409 s  `Entering Geoscape UI state: UIStateGeoscapeEvent`
    ///   6547 / 118,239 s  `HOST answered 'PROG_SY0_MISS' choice=0 … peer=1`               (the CLIENT won)
    ///   6547 / 118,239 s  `Queuerd state switch …UIStateRosterDeployment with priority 2147483647`
    ///   — and then NOT ONE view-state line for 169 frames —
    ///   6716 / 121,075 s  `Entering Geoscape UI state: UIStateLoading` + `HOST intent APPLIED op=launch S#68`
    /// The client's own log, same encounter, offset ≈ 3,64 s: `6533 Queuerd …UIStateRosterDeployment` →
    /// `6542 Entering Geoscape UI state: UIStateRosterDeployment`. NINE FRAMES. The host: never.
    ///
    /// WHY L141 COULD NOT SEE IT, AND WHY IT WAS GREEN. L141's whole apparatus —
    /// <c>EventPopup.ClickWasReplayed</c>, <c>MissionEncounterNav.ShouldOpenDeployment</c>,
    /// <c>NativeSelectChoiceRan</c> — hangs off a POSTFIX ON <c>UIModuleSiteEncounters.FinishEncounter</c>:618.
    /// On the losing host <c>FinishEncounter</c> IS NEVER CALLED AT ALL: its click never reached
    /// <c>OnChoiceSelected</c> (no `REPLAYED` line anywhere in that session's host log, and no
    /// `CompleteEvent … skipped` either — <c>SelectChoice</c>:598 skips <c>CompleteEvent</c> outright on an
    /// instance <c>EventSync.HandleAnswer</c> already completed), so every term L141 asserts is downstream of
    /// a call that does not happen. Worse, L141's second term is ACTIVELY the wrong sign here:
    /// <c>AlreadyHeadedForDeployment</c> answered TRUE — correctly, the request really was in the queue — and
    /// suppressed the navigation. L141's own STATED LIMIT wrote the hole down verbatim ("the arms assert that
    /// every peer is HEADED for it (entered, queued, or re-issued)") and then mis-attributed the one case
    /// that matters to another peer's launch. It was not torn down. It was never servable.
    ///
    /// THE MECHANISM, FROM THE GAME'S OWN CODE. <c>GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch</c>
    /// returns on its FIRST line while <c>_currentStateSwitchRequest != null</c>, and the encounter window is
    /// exactly that request — it was pushed by the same queue at priority 0. The one thing that clears it is
    /// <c>FinishCurrentStateSwitch</c>, reached only from <c>GeoscapeView.FinishQueriedState</c>:2164, reached
    /// here only from <c>UIStateBaseGeoscapeEvent.FinishEncounter</c> ← <c>EncounterFinished</c> ←
    /// <c>UIModuleSiteEncounters.FinishEncounter</c>:618. Priority is irrelevant: <c>int.MaxValue</c> orders
    /// the LIST, it does not preempt the current request. So NATIVE pairs the two in one breath —
    /// <c>SelectChoice</c>:611-612 is <c>FinishEncounter(); Context.View.LaunchMission(startMission, …)</c> —
    /// and <c>EventSync.HandleAnswer</c>'s native tail had copied :612 and not :611.
    ///
    /// HYPOTHESES REFUTED BY THE SAME LOG, recorded so they are not re-derived: the mission's
    /// <c>SkipDeploymentSelection</c> was NOT set (that arm calls <c>mission.Launch</c> and never logs a
    /// `Queuerd state switch …UIStateRosterDeployment`, which the host log carries verbatim), and the host's
    /// press did NOT become a 0xB8 launch intent (the only one in the session is `nonce=10 peer=1`, the
    /// client's, and it arrives 2,8 s after the host's window went stale).
    ///
    /// THE OUTCOME THIS LAW ASSERTS, in terms that survive whatever the game does with the screen: after any
    /// path that queues the squad screen, THIS PEER'S OWN ENCOUNTER WINDOW FOR THAT EVENT IS NO LONGER THE
    /// CURRENT QUERIED STATE SWITCH. Nothing about who launches, nothing about quorums (there are none, and
    /// one peer's deploy pulling everyone in is intended), nothing about whether the screen is ultimately
    /// reached — only that this peer is not the thing blocking its own queue.
    ///
    /// ARMS. (a)/(b) EXECUTE the shipped verdict <c>MissionEncounterNav.MustFinishOwnEncounterWindow</c> over
    /// the roles a resolved mission-start encounter produces, and assert the outcome (nothing stranded) AND
    /// its anti-over-correction (nothing popped that this peer does not own — <c>FinishCurrentStateSwitch</c>
    /// also runs <c>SwitchToPreviousState</c>, so a blind call pops one screen too many). (c)/(d) are
    /// structural: <c>EventSync.HandleAnswer</c> must reach BOTH halves of the native tail, and the closer
    /// must run the decision rather than decorate it. (e) pins the two native anchors the whole reading rests
    /// on, including the AccessTools binding — an unbound handle here fails SILENTLY (memory
    /// harmony-accesstools-exact-param-match) and would restore the exact bug.
    ///
    /// PREMISE AMENDED 2026-08-09, OUTCOME UNCHANGED. This law was written about a squad screen queued by
    /// ANOTHER peer's answer crossing the rail (EventSync's native tail). Since L351/L352 a mission brief is
    /// answered by each peer for itself, so the squad screen now arrives from THIS peer's own Confirm as
    /// well — and the outcome asserted here is exactly as load-bearing either way, because the thing that
    /// stalls it is unchanged: <c>ProcessQueriedStateSwitch</c> serves nothing while this peer's own window
    /// holds <c>_currentStateSwitchRequest</c>, whoever queued what is behind it. L354 adds the other half
    /// this law does not claim — that a screen which IS served has something in it.
    ///
    /// Falsify: <c>MustFinishOwnEncounterWindow</c> → false → <c>L144 squad-screen-queued-behind-our-own-
    /// window</c>; → true → <c>L144 pops-a-window-we-do-not-own</c>; delete the
    /// <c>FinishOwnEncounterWindow</c> call from <c>HandleAnswer</c> → <c>L144 native-tail-half-copied</c>.
    /// </summary>
    internal static class L144_QueuedSquadScreenIsServable
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>One peer's situation at the moment a resolution that STARTS A MISSION is applied to it.
        /// <c>OwnWindowIsCurrent</c> = this peer's own <c>UIStateGeoscapeEvent</c> for that very event is the
        /// queue's current request, i.e. the thing <c>ProcessQueriedStateSwitch</c> is parked behind.</summary>
        private struct Role
        {
            internal string Who;
            internal bool MissionStarted, OwnWindowIsCurrent;
        }

        private static readonly Role[] Roles =
        {
            new Role { Who = "host that LOST the race with its own encounter window still up (the measured session)",
                       MissionStarted = true,  OwnWindowIsCurrent = true  },
            new Role { Who = "host that answered for an event it never showed a dialog for (HandleAnswer synthesises the instance)",
                       MissionStarted = true,  OwnWindowIsCurrent = false },
            new Role { Who = "host whose window is up but whose choice started no mission",
                       MissionStarted = false, OwnWindowIsCurrent = true  },
            new Role { Who = "host with neither a window nor a mission",
                       MissionStarted = false, OwnWindowIsCurrent = false },
        };

        internal static IEnumerable<string> Check()
        {
            var nav = typeof(MissionSync).Assembly.GetType("Multiplayer.Network.Sync.MissionEncounterNav");
            var verdict = nav?.GetMethod("MustFinishOwnEncounterWindow", All);
            var closer = nav?.GetMethod("FinishOwnEncounterWindow", All);
            var handle = typeof(EventSync).GetMethod("HandleAnswer", All);
            if (verdict == null || closer == null || handle == null)
            {
                yield return "L144 premise-changed: MissionEncounterNav.{MustFinishOwnEncounterWindow," +
                             "FinishOwnEncounterWindow} or EventSync.HandleAnswer no longer resolves. Nothing " +
                             "else in the rail closes the encounter window a rail-applied resolution leaves " +
                             "standing, and GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch serves NOTHING " +
                             "while it stands — so the squad screen is unasserted again.";
                yield break;
            }

            // ─── (a)/(b) EXECUTED: the outcome, and its anti-over-correction ───
            foreach (var role in Roles)
            {
                bool closes = (bool)verdict.Invoke(null, new object[] { role.MissionStarted, role.OwnWindowIsCurrent });

                // (a) THE OUTCOME. A mission was queued for this peer (LaunchMission → ToDeploymentState:596)
                //     and this peer's own window is still the current request => the queue can never run.
                if (role.MissionStarted && role.OwnWindowIsCurrent && !closes)
                    yield return "L144 squad-screen-queued-behind-our-own-window: the " + role.Who + " queues " +
                                 "UIStateRosterDeployment (priority int.MaxValue) and leaves its own " +
                                 "UIStateGeoscapeEvent as the current queried state switch. " +
                                 "ProcessQueriedStateSwitch returns on its first line while that request is " +
                                 "live, so the screen is not 'queued', it is stranded — measured 169 frames of " +
                                 "nothing, then the battle. Native never does this: SelectChoice:611 calls " +
                                 "FinishEncounter BEFORE :612's LaunchMission.";

                // (b) ANTI-OVER-CORRECTION. FinishQueriedState → FinishCurrentStateSwitch also runs
                //     SwitchToPreviousState, so closing a window this peer does not own pops one screen too
                //     many — the vehicle-selected state underneath, on a peer that was reading something else.
                if (closes && !role.OwnWindowIsCurrent)
                    yield return "L144 pops-a-window-we-do-not-own: the " + role.Who + " closes an encounter " +
                                 "window that is not this peer's current one for this event. " +
                                 "FinishCurrentStateSwitch pops the state stack as well as clearing the " +
                                 "request, so a blind call takes a screen the player is actually using.";

                // (c) AND ONLY FOR A MISSION. A resolution with no StartMission queues nothing, so there is
                //     nothing to unblock and the window is the player's to click through (the REPLAY design).
                if (closes && !role.MissionStarted)
                    yield return "L144 closes-a-window-with-no-mission-behind-it: the " + role.Who + " has its " +
                                 "encounter window closed although the resolution started no mission. That is " +
                                 "the client-side face of the old bug — a decided picker with a clickable " +
                                 "winner must stay up (EventPopup.DismissOnResolution), not vanish under the " +
                                 "player.";
            }

            // ─── (d) STRUCTURAL: BOTH halves of the native tail, from the one caller ─
            var reachedOwn = Program.Callees(handle, typeof(MissionSync).Assembly).ToList();
            if (!reachedOwn.Any(m => m.Name == "FinishOwnEncounterWindow"))
                yield return "L144 native-tail-half-copied: EventSync.HandleAnswer does not reach " +
                             "MissionEncounterNav.FinishOwnEncounterWindow. That is the shipped bug verbatim — " +
                             "the tail runs LaunchMission (SelectChoice:612) without FinishEncounter (:611), so " +
                             "the peer that did NOT click queues a screen behind a window nothing will close.";
            if (!Program.Callees(handle, typeof(GeoscapeView).Assembly)
                        .Any(m => m.Name == "LaunchMission" && m.DeclaringType == typeof(GeoscapeView)))
                yield return "L144 navigates-off-the-native-funnel: EventSync.HandleAnswer does not reach " +
                             "GeoscapeView.LaunchMission. Closing the window without queueing the screen leaves " +
                             "the peer on a bare geoscape — the other half of the same pair.";
            if (!Program.Callees(closer, typeof(MissionSync).Assembly).Any(m => m.Name == "MustFinishOwnEncounterWindow"))
                yield return "L144 verdict-is-decorative: FinishOwnEncounterWindow does not call " +
                             "MustFinishOwnEncounterWindow, so arms (a)-(c) assert a function nothing runs — " +
                             "which is exactly how L141 stayed green through this defect.";

            // ─── (e) STRUCTURAL: the two native anchors, and the binding ────────
            if (AccessTools.Method(typeof(UIModuleSiteEncounters), "FinishEncounter") == null)
                yield return "L144 finish-encounter-unbound: AccessTools cannot resolve " +
                             "UIModuleSiteEncounters.FinishEncounter. An unbound handle fails SILENTLY here — " +
                             "the closer would return without a word and restore the stall exactly.";
            if (AccessTools.Method(typeof(GeoscapeView), "FinishQueriedState") == null ||
                AccessTools.Method(typeof(GeoscapeViewSwitchQuery), "ProcessQueriedStateSwitch") == null ||
                AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_currentStateSwitchRequest") == null)
                yield return "L144 queue-model-changed: GeoscapeView.FinishQueriedState, " +
                             "GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch or its " +
                             "_currentStateSwitchRequest field is gone. The whole reason this law exists is " +
                             "that the queue is parked on ONE current request and only FinishQueriedState " +
                             "clears it; if that is no longer true the fix needs re-deriving, not keeping.";
            if (typeof(UIStateGeoscapeEvent).Assembly != typeof(GeoscapeView).Assembly ||
                typeof(UIStateRosterDeployment).Assembly != typeof(GeoscapeView).Assembly)
                yield return "L144 states-are-not-the-games: UIStateGeoscapeEvent or UIStateRosterDeployment no " +
                             "longer comes from the game assembly, so the two screens this law reasons about " +
                             "are not the ones the player sees.";
        }
    }
}
