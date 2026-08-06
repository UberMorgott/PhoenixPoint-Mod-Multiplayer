using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L141 — A RESOLVED MISSION-START ENCOUNTER LEAVES EVERY LIVE PEER HOLDING THE PRE-MISSION SQUAD SCREEN,
    /// WHICHEVER PEER'S CLICK RESOLVED IT.
    ///
    /// THE REPORT (2026-08-06, two-peer session). The host pressed the mission choice on a site encounter and
    /// was taken straight into the battle: no briefing, no squad edit, no deploy button. The client got the
    /// screen and deployed from it. Host log, one clock:
    ///   632,002 s `HOST answered 'PROG_NJ0_MISS' choice=0 … peer=1`   (the CLIENT's answer won the race)
    ///   636,127 s `click on 'PROG_NJ0_MISS' REPLAYED — the answer is not this peer's`
    ///   636,232 s `tac-entry: broadcasting EntryTransferBegin` + `HOST intent APPLIED op=launch S#213`
    ///
    /// THE DIVERGENCE IS ONE TERM. <c>MissionEncounterNav</c> — the postfix that re-issues the
    /// <c>UIModuleSiteEncounters.SelectChoice</c>:612 <c>LaunchMission</c> call for a peer that could not run
    /// it — bailed on <c>engine.IsHost</c>, commented "solo/host ran SelectChoice natively". That comment is
    /// FALSE for a host that LOSES the event race: <c>EventPopup.ResolutionIsNotOurs</c> →
    /// <c>ReplayResolution</c> → <c>return false</c>, so not one line of :598-613 ran there either. The
    /// predicate has to read "THIS PEER'S NATIVE SelectChoice DID NOT RUN", which is what
    /// <c>EventPopup.ClickWasReplayed</c> now records at the replay seam (<c>EventPopup.cs</c>, the REPLAYED
    /// log site) — nothing else knows it, because the arriving record delta names the CHOICE and never the
    /// chooser, and by the time the peer clicks through the replayed page the record reads Completed
    /// everywhere alike.
    ///
    /// AND THE WIDENED PREDICATE NEEDS ITS SECOND TERM, or it is a fresh bug. A losing host is USUALLY
    /// already headed for that screen: <c>EventSync.HandleAnswer</c>'s native tail runs
    /// <c>geo.View.LaunchMission</c> as it applies the winning answer, and <c>GeoscapeView
    /// .ToDeploymentState</c>:592 QUEUES the state (<c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>, priority
    /// int.MaxValue) instead of entering it — it is served only when this peer's own encounter window
    /// finishes. MEASURED in the same host log at 632,002 s: `Queuerd state switch
    /// …UIStateRosterDeployment with priority 2147483647`, four seconds before that host clicked its stale
    /// picker. Re-issuing blind would leave a SECOND request in a queue that is part of the SAVE
    /// (<c>GeoscapeViewSwitchQuery.GetRestorableData</c>:25) — a deployment screen for a finished mission,
    /// waiting for the player when he comes back from the battle. So the shipped verdict is
    /// <c>inSession &amp;&amp; !nativeSelectChoiceRan &amp;&amp; !alreadyHeaded</c>, and this law asserts BOTH
    /// halves: nobody is left behind, and nobody is sent twice.
    ///
    /// WHAT "THE SQUAD SCREEN" IS, AND WHY NO TFTV BINDING IS INVOLVED. With TFTV installed the pre-mission
    /// squad-edit screen the player sees IS the vanilla <c>UIStateRosterDeployment</c>: TFTV adds no view
    /// state of its own, it DECORATES that one — <c>TFTVUI/Geoscape/MissionDeployment.cs</c>:36/:101
    /// (EnterState/ExitState), <c>TFTVHarmonyGeoscapeUI.cs</c>:249 (OnDeploySquad),
    /// <c>TFTVAircraftRework/AircraftReworkMissionDeployment.cs</c>:26/:133
    /// (OnEnrollmentChanged/CheckForDeployment) — and otherwise only flips <c>SkipDeploymentSelection</c> on
    /// some mission defs. Navigating through the GAME's own <c>GeoscapeView.LaunchMission</c> therefore lands
    /// on TFTV's decorated screen when TFTV is loaded and on the stock one when it is not, with zero TFTV
    /// types named on our side and no <c>TftvLateBinder</c> arm to bind late. Arm (e) is what keeps that true.
    ///
    /// ARMS. (a) and (b) are the OUTCOME, EXECUTED against the shipped verdict over the five peer roles a
    /// resolved mission-start encounter can produce. (c) pins solo. (d)/(e) are structural and keep the pure
    /// verdict from becoming decorative: the shipped postfix must reach it, must read the replayed-click
    /// signal, must write that signal at the replay seam, and must navigate through the vanilla funnel.
    ///
    /// Falsify: restore the old bail (<c>inSession &amp;&amp; !isHost</c>) → <c>L141
    /// peer-never-reaches-the-squad-screen</c> for the losing host; drop the <c>!alreadyHeaded</c> term →
    /// <c>L141 squad-screen-queued-twice</c>.
    /// </summary>
    internal static class L141_EveryPeerReachesTheSquadScreen
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>One peer's situation at <c>FinishEncounter</c> on a resolved mission-start encounter.
        /// <c>NativeRan</c> = this peer's own <c>SelectChoice</c> body executed (so :612 already called
        /// <c>LaunchMission</c>); <c>AlreadyHeaded</c> = the squad screen is entered or queued here already.</summary>
        private struct Role
        {
            internal string Who;
            internal bool IsHost, Replayed, AlreadyHeaded;
        }

        private static readonly Role[] Roles =
        {
            new Role { Who = "host that WON the race (its SelectChoice:612 ran)",
                       IsHost = true,  Replayed = false, AlreadyHeaded = true  },
            new Role { Who = "host that LOST the race, HandleAnswer already queued the screen (the measured session)",
                       IsHost = true,  Replayed = true,  AlreadyHeaded = true  },
            new Role { Who = "host that LOST the race with nothing queued (HandleAnswer's no-view arm)",
                       IsHost = true,  Replayed = true,  AlreadyHeaded = false },
            new Role { Who = "client (answerer or observer — its click is always relayed)",
                       IsHost = false, Replayed = false, AlreadyHeaded = false },
            new Role { Who = "client already holding the screen (FinishEncounter reached twice)",
                       IsHost = false, Replayed = false, AlreadyHeaded = true  },
        };

        internal static IEnumerable<string> Check()
        {
            var nav = typeof(MissionSync).Assembly.GetType("Multiplayer.Network.Sync.MissionEncounterNav");
            var verdict = nav?.GetMethod("ShouldOpenDeployment", All);
            var postfix = nav?.GetMethod("Postfix", All);
            var probe = nav?.GetMethod("AlreadyHeadedForDeployment", All);
            var ran = nav?.GetMethod("NativeSelectChoiceRan", All);
            if (verdict == null || postfix == null || probe == null || ran == null)
            {
                yield return "L141 premise-changed: MissionEncounterNav.{ShouldOpenDeployment," +
                             "NativeSelectChoiceRan,Postfix,AlreadyHeadedForDeployment} no longer resolves. Nothing else in the rail decides " +
                             "whether a peer whose click was replayed still owes itself the LaunchMission call " +
                             "SelectChoice:612 makes, so the whole outcome is unasserted.";
                yield break;
            }

            // ─── (a)/(b) EXECUTED: the outcome, over every role ────────────────
            foreach (var role in Roles)
            {
                bool reissue = (bool)verdict.Invoke(null, new object[] { true, role.IsHost, role.Replayed, role.AlreadyHeaded });
                // The peer's own SelectChoice body ran only if it is the host AND its click was not replayed.
                bool nativeRan = role.IsHost && !role.Replayed;

                // (a) THE OUTCOME. "Ends up holding the squad screen" = its own native call made it, or it is
                //     already headed there, or this seam re-issues the call. One of the three, for EVERY peer.
                if (!(nativeRan || role.AlreadyHeaded || reissue))
                    yield return "L141 peer-never-reaches-the-squad-screen: the " + role.Who + " ends a resolved " +
                                 "mission-start encounter with no pre-mission squad screen — native call ran=" +
                                 nativeRan + ", already headed=" + role.AlreadyHeaded + ", re-issued=" + reissue +
                                 ". That is the reported bug verbatim: the player presses the mission choice and " +
                                 "the next thing he sees is the battle.";

                // (b) AND EXACTLY ONE OF THEM. ToDeploymentState QUEUES (priority int.MaxValue) and that queue
                //     is saved (GeoscapeViewSwitchQuery.GetRestorableData:25), so a second request is not a
                //     harmless duplicate — it is a squad screen for a finished mission, served after the battle.
                if (reissue && (nativeRan || role.AlreadyHeaded))
                    yield return "L141 squad-screen-queued-twice: the " + role.Who + " is sent to the squad screen " +
                                 "AGAIN although it is already headed there (native call ran=" + nativeRan +
                                 ", already headed=" + role.AlreadyHeaded + "). The state switch query is part of " +
                                 "the save, so the surplus request outlives the mission it was queued for.";
            }

            // ─── (c) EXECUTED: solo is untouched, and the host/replay term means what it says ──
            foreach (var host in new[] { true, false })
                foreach (var replayed in new[] { true, false })
                    foreach (var headed in new[] { true, false })
                    {
                        if ((bool)verdict.Invoke(null, new object[] { false, host, replayed, headed }))
                            yield return "L141 solo-gets-a-second-navigation: the verdict re-issues LaunchMission " +
                                         "outside a session (isHost=" + host + ", replayed=" + replayed +
                                         ", alreadyHeaded=" + headed + "). Single-player runs SelectChoice:598-613 " +
                                         "end to end; this seam exists only because a co-op peer cannot.";
                        if ((bool)ran.Invoke(null, new object[] { host, replayed }) != (host && !replayed))
                            yield return "L141 native-run-misjudged: NativeSelectChoiceRan(isHost=" + host +
                                         ", replayed=" + replayed + ") disagrees with the only definition there is — " +
                                         "the body executes on the host unless ResolutionIsNotOurs turned the click " +
                                         "into a replayed result page.";
                    }

            // ─── (d) STRUCTURAL: the verdict is actually the one the game asks ─
            var reached = Program.Callees(postfix, typeof(MissionSync).Assembly).ToList();
            if (!reached.Any(m => m.Name == "ShouldOpenDeployment"))
                yield return "L141 verdict-is-decorative: MissionEncounterNav.Postfix does not call " +
                             "ShouldOpenDeployment. Arms (a)-(c) would then be asserting a function nothing runs — " +
                             "which is exactly how the IsHost bail survived every existing law.";
            if (!reached.Any(m => m.Name == "ClickWasReplayed"))
                yield return "L141 replayed-click-not-read: MissionEncounterNav.Postfix does not read " +
                             "EventPopup.ClickWasReplayed. Without it the only term left is 'am I the host', and " +
                             "the host that lost the event race is silently assumed to have run SelectChoice.";

            var lockPrefix = typeof(MissionSync).Assembly.GetType("Multiplayer.Network.Sync.EventChoiceClientLock")
                                                ?.GetMethod("Prefix", All);
            if (lockPrefix == null ||
                !Program.Callees(lockPrefix, typeof(MissionSync).Assembly).Any(m => m.Name == "NoteReplayedClick"))
                yield return "L141 replayed-click-never-recorded: EventChoiceClientLock.Prefix does not call " +
                             "EventPopup.NoteReplayedClick at the seam where it decides to REPLAY the click " +
                             "instead of running it. That decision is the only moment 'my SelectChoice did not " +
                             "run' exists anywhere; unrecorded, ClickWasReplayed answers False forever and the " +
                             "read above is a no-op.";

            // ─── (e) STRUCTURAL: the vanilla funnel, which is what TFTV decorates
            var game = typeof(GeoscapeView).Assembly;
            if (!Program.Callees(postfix, game).Any(m => m.Name == "LaunchMission" && m.DeclaringType == typeof(GeoscapeView)))
                yield return "L141 navigates-off-the-native-funnel: MissionEncounterNav.Postfix does not reach " +
                             "GeoscapeView.LaunchMission. That funnel is the whole TFTV story — TFTV ships no " +
                             "deployment view state of its own, it patches the vanilla UIStateRosterDeployment " +
                             "(TFTVUI/Geoscape/MissionDeployment.cs:36/:101, TFTVHarmonyGeoscapeUI.cs:249), so " +
                             "going through the game's own call lands on the decorated screen with TFTV loaded " +
                             "and the stock one without it. Anything else would have to name a TFTV type and " +
                             "late-bind it (TftvLateBinder), which this seam deliberately does not do.";
            if (!Program.Callees(probe, game).Any(m => m.Name == "TryGetStateSwitchRequestForState" &&
                                                       m.DeclaringType == typeof(GeoscapeViewSwitchQuery)))
                yield return "L141 already-headed-answered-off-the-books: AlreadyHeadedForDeployment does not ask " +
                             "GeoscapeViewSwitchQuery.TryGetStateSwitchRequestForState. The screen is QUEUED, not " +
                             "entered (ToDeploymentState:592), so a probe that only looks at the current state " +
                             "misses the whole window HandleAnswer's queue is waiting in — and arm (b)'s duplicate " +
                             "comes straight back.";
            if (typeof(UIStateRosterDeployment).Assembly != game)
                yield return "L141 squad-screen-is-not-the-games: UIStateRosterDeployment no longer comes from the " +
                             "game assembly, so the state this law reasons about is not the one TFTV decorates.";
        }
    }
}
