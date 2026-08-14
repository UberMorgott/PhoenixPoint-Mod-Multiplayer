using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L197 — A PEER THAT DID NOT ANSWER THE MISSION EVENT STILL REACHES THE SQUAD SCREEN.
    ///
    /// THE REPORT (2026-08-07). Two peers were shown the same mission-start encounter. One answered and
    /// reached <c>UIStateRosterDeployment</c>. The other never did — and its <c>MissionEncounterNav.Postfix</c>
    /// emitted NO LINE AT ALL: not the success, not the "NOT opened" warning, not the catch. It bailed at one
    /// of FOUR UNLOGGED returns, and which one was unknowable from the log.
    ///
    /// WHY THE FUNNEL WAS THE WRONG THING TO HANG THE SCREEN ON. <c>UIModuleSiteEncounters.FinishEncounter</c>
    /// is a LOCAL DIALOG event: it fires only if this peer's own window was built, clicked through and torn
    /// down, and it reads whatever that window happens to be holding at that instant — including a
    /// <c>Record</c> that on a mirroring peer can still be the placeholder <c>RaiseMirrored</c>:568 minted,
    /// whose <c>SelectedChoice</c> is nobody's answer. Every one of those is a race, and each one loses the
    /// entire screen with no way to get it back except the aircraft's Launch button.
    ///
    /// THE TRIGGER IS NOW THE STATE, WHICH ARRIVES ON EVERY PEER BY ITSELF: the event record RESOLVING
    /// (whoever answered it) plus the host's <c>S#&lt;id&gt;…ActiveMission</c> structural create landing on
    /// this peer's own site. <c>MissionArrivalNav</c> is armed from the raise (so only windows this peer was
    /// actually SHOWN can ever navigate it — the geoscape sprouts missions all game long and none of them may
    /// yank anybody) and polled from <c>SyncEngine.Tick</c>, the driver <c>DrainHeldRaises</c> already uses.
    ///
    /// IT IS NOT A QUORUM (P13), and arm (d) holds that line. Every term is this peer's own record, its own
    /// site graph and its own view queue. It counts no peers, waits for no acknowledgement and asks nobody to
    /// press anything: a peer whose partners are all AFK opens its squad screen just as fast.
    ///
    /// AND IT DOES NOT DOUBLE-QUEUE — arm (c). <c>ToDeploymentState</c>:596 QUEUES the screen into a list that
    /// is part of the SAVE, so a second request is a deployment screen for a finished mission waiting for the
    /// player after the battle. The arrival path REUSES <c>ShouldOpenDeployment</c> and
    /// <c>AlreadyHeadedForDeployment</c> rather than re-deriving them, which is what keeps the peer that DID
    /// answer from being queued twice.
    ///
    /// ARM (e) IS THE SILENCE ITSELF. The four returns now each say why. Silent swallow is this repo's
    /// dominant bug class, and the whole reason this report cost a re-run is that not one of them spoke.
    ///
    /// Falsify (each verified RED, then restored): unwire <c>MissionArrivalNav.Tick</c> from
    /// <c>SyncEngine.Tick</c> → <c>arrival-drives-nothing</c>; stop arming from the raise paths →
    /// <c>watch-is-never-armed</c>; drop the <c>AlreadyHeadedForDeployment</c> term →
    /// <c>arrival-double-queues</c>; invert <c>MissionHasArrived</c> / <c>ArrivalGivenUp</c> → (a)/(b);
    /// delete any one of the bail lines from <c>MissionEncounterNav.Postfix</c> → <c>exit-without-a-reason</c>.
    /// </summary>
    internal static class L197_TheSquadScreenOpensOnTheMissionsArrival
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>Four bails + the not-runnable warning + the success + the catch. Every one of them was a
        /// decision the 2026-08-07 log could not show, and the count is the only thing a headless harness can
        /// hold against "somebody deleted a log line to quieten the console".</summary>
        private const int ExplicitOutcomes = 7;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(EventPopup).Assembly;
            var arrival = mod.GetType("Multiplayer.Network.Sync.MissionArrivalNav");
            var nav = mod.GetType("Multiplayer.Network.Sync.MissionEncounterNav");

            var hasArrived = arrival?.GetMethod("MissionHasArrived", All);
            var givenUp = arrival?.GetMethod("ArrivalGivenUp", All);
            var watch = arrival?.GetMethod("Watch", All);
            var tick = arrival?.GetMethod("Tick", All);
            var step = arrival?.GetMethod("Step", All);
            var should = nav?.GetMethod("ShouldOpenDeployment", All);
            var headed = nav?.GetMethod("AlreadyHeadedForDeployment", All);
            var postfix = nav?.GetMethod("Postfix", All);
            var syncTick = typeof(SyncEngine).GetMethod("Tick", All, null, Type.EmptyTypes, null);
            var hostBroadcast = typeof(EventPopup).GetMethod("HostBroadcast", All);
            var raiseMirrored = typeof(EventPopup).GetMethod("RaiseMirrored", All);

            if (hasArrived == null || givenUp == null || watch == null || tick == null || step == null ||
                should == null || headed == null || postfix == null || syncTick == null ||
                hostBroadcast == null || raiseMirrored == null)
            {
                yield return "L197 premise-changed: MissionArrivalNav.{MissionHasArrived,ArrivalGivenUp,Watch," +
                             "Tick,Step}, MissionEncounterNav.{ShouldOpenDeployment,AlreadyHeadedForDeployment," +
                             "Postfix}, SyncEngine.Tick or EventPopup.{HostBroadcast,RaiseMirrored} no longer " +
                             "resolves. The squad screen's arrival trigger has been reshaped and every arm below " +
                             "would pass vacuously — which is how a peer silently kept not reaching it.";
                yield break;
            }

            // ── (a) THE ARRIVAL DECISION, EXECUTED ───────────────────────────────────
            Func<bool, bool, bool, bool> arrived = (a, c, m) =>
                (bool)hasArrived.Invoke(null, new object[] { a, c, m });

            if (!arrived(true, true, true))
                yield return "L197 arrival-never-fires: MissionHasArrived answers FALSE with the answer " +
                             "resolved, the chosen choice starting a mission and this peer's own site holding a " +
                             "runnable ActiveMission. Those three ARE the arrival; a peer for whom they all hold " +
                             "and who still gets no screen is the 2026-08-07 report unchanged.";
            foreach (var missing in new[] { 0, 1, 2 })
                if (arrived(missing != 0, missing != 1, missing != 2))
                    yield return "L197 arrival-fires-early: MissionHasArrived answers TRUE while " +
                                 (missing == 0 ? "the event is still unanswered"
                                  : missing == 1 ? "the answered choice starts no mission"
                                  : "the site has no runnable ActiveMission") +
                                 ". Opening the squad screen there queues a deployment for a mission that does " +
                                 "not exist into a list that is part of the SAVE.";

            // ── (b) THE WAIT IS BOUNDED, and cannot start before the answer ──────────
            Func<float, float, bool> gaveUp = (r, n) => (bool)givenUp.Invoke(null, new object[] { r, n });
            if (gaveUp(0f, 10000f))
                yield return "L197 unarmed-watch-times-out: ArrivalGivenUp answers TRUE for a watch whose event " +
                             "has NOT resolved yet. The player may sit on that window for as long as he likes; " +
                             "retiring the watch while it is open loses the screen for everybody who reads slowly.";
            if (gaveUp(100f, 100f))
                yield return "L197 unarmed-watch-times-out: ArrivalGivenUp answers TRUE in the same instant the " +
                             "answer resolved, so the mission's structural create — which rides a later diff " +
                             "cycle by construction — can never land in time.";
            if (!gaveUp(100f, 100f + 10000f))
                yield return "L197 wait-is-unbounded: ArrivalGivenUp never expires. A mission that is never " +
                             "coming (launched by another peer, cancelled, expired) then leaves a watch polling " +
                             "this peer's site graph for the rest of the session, silently.";

            // ── (c) THE SEAMS: armed at the raise, driven by the tick, de-duped ──────
            if (!Program.Callees(syncTick, mod).Any(m => Same(m, tick)))
                yield return "L197 arrival-drives-nothing: SyncEngine.Tick no longer reaches " +
                             "MissionArrivalNav.Tick. The whole point is a trigger that does NOT depend on this " +
                             "peer's own dialog teardown — unwired, the squad screen is back to being whatever " +
                             "FinishEncounter happened to see.";
            if (!Program.Callees(hostBroadcast, mod).Any(m => Same(m, watch)) ||
                !Program.Callees(raiseMirrored, mod).Any(m => Same(m, watch)))
                yield return "L197 watch-is-never-armed: one of EventPopup.HostBroadcast / RaiseMirrored no " +
                             "longer arms MissionArrivalNav.Watch. BOTH are needed: a client's window is " +
                             "mirrored, and a HOST that loses the answer race never ran its own SelectChoice:612 " +
                             "either, so it owes itself exactly the same screen.";
            var stepCalls = Program.Callees(step, mod).ToList();
            if (!stepCalls.Any(m => Same(m, should)) || !stepCalls.Any(m => Same(m, headed)))
                yield return "L197 arrival-double-queues: MissionArrivalNav.Step opens the screen without " +
                             "asking ShouldOpenDeployment AND AlreadyHeadedForDeployment. ToDeploymentState:596 " +
                             "QUEUES at int.MaxValue into a list GeoscapeViewSwitchQuery.GetRestorableData puts " +
                             "in the SAVE, so a second request is a deployment screen for a finished mission, " +
                             "waiting for the player after the battle.";
            if (!Program.Callees(step, typeof(MissionArrivalNav).Assembly)
                        .Any(m => m.Name == "EnqueuePriorityOccurrence"))
                yield return "L197 arrival-records-nothing: MissionArrivalNav.Step does not record the durable " +
                             "priority occurrence for the Geoscape-gated scheduler.";
            // RESTORED 2026-08-10, after being replaced by the arm above in ae2099d. Recording the occurrence
            // is NOT presenting it: DurableInboxEngine.TryPresentNext has no production caller, so the queue
            // the hand-off relied on is drained by nothing. With only the record arm in place the harness
            // stayed green while BOTH clients sat on the geoscape after answering a mission start ("priority
            // occurrence ready … presentation remains behind DurableWindowRegistry.MayPresent", live
            // 2026-08-10) — this arm is exactly the "green harness, missing screen" it was written against,
            // and it must be held TOGETHER with the record arm, never traded for it.
            if (!Program.Callees(step, typeof(GeoscapeView).Assembly).Any(m => m.Name == "LaunchMission"))
                yield return "L197 arrival-opens-nothing: MissionArrivalNav.Step never reaches " +
                             "GeoscapeView.LaunchMission, so every arm above is about a watch that decides " +
                             "correctly and then does nothing — green harness, missing screen.";
            if (!Program.Callees(step, typeof(MissionArrivalNav).Assembly).Any(m => m.Name == "MayPresent"))
                yield return "L197 arrival-yanks-a-peer: MissionArrivalNav.Step opens the screen without asking " +
                             "DurableWindowRegistry.MayPresent. A peer reading Research or queueing " +
                             "Manufacturing must not be dragged to the squad screen by somebody else's answer; " +
                             "the gate is also what lets the watch stay armed until this peer is back on the map.";

            // ── (d) NO QUORUM: the pure decisions read no peer at all ────────────────
            var peerish = new[] { "NetworkEngine", "SessionManager", "PingTable", "PeerListEntry",
                                  "LobbyController", "RosterProgressTracker" };
            foreach (var m in new[] { hasArrived, givenUp })
                foreach (var c in Program.Callees(m, mod))
                    if (c.DeclaringType != null && peerish.Contains(c.DeclaringType.Name))
                        yield return "L197 arrival-reads-a-peer: " + m.Name + " calls " + c.DeclaringType.Name +
                                     "." + c.Name + ". The moment the arrival decision reads another peer, " +
                                     "\"the screen opens when the mission lands\" becomes \"the screen opens when " +
                                     "somebody else does something\" — a wait on a PERSON, which P13 forbids " +
                                     "outright (L84/L91).";

            // ── (e) NO EXIT WITHOUT A REASON ─────────────────────────────────────────
            int reasons = Program.CalleeSequence(postfix)
                                 .Count(m => m.DeclaringType != null &&
                                             m.DeclaringType == typeof(Multiplayer.MpLog) &&
                                             (m.Name == "Log" || m.Name == "LogWarning" || m.Name == "LogError"));
            if (reasons < ExplicitOutcomes)
                yield return "L197 exit-without-a-reason: MissionEncounterNav.Postfix carries only " + reasons +
                             " explicit outcomes, fewer than the " + ExplicitOutcomes + " ways out it has (not " +
                             "in a session / no answered choice to read / the choice starts no mission / this " +
                             "peer does not owe itself the call / the mission is gone / the screen opened / it " +
                             "threw). On 2026-08-07 this method produced NOT ONE LINE on the peer that never " +
                             "reached the squad screen, and the whole investigation had to guess which of four " +
                             "silent returns it took.";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
