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
            var postfix = nav?.GetMethod("Postfix", All);
            var syncTick = typeof(SyncEngine).GetMethod("Tick", All, null, Type.EmptyTypes, null);
            var hostBroadcast = typeof(EventPopup).GetMethod("HostBroadcast", All);
            var raiseMirrored = typeof(EventPopup).GetMethod("RaiseMirrored", All);
            var eventAnswer = typeof(EventSync).GetMethod("HandleAnswer", All);
            var deployPrep = mod.GetType("Multiplayer.Network.Sync.DeployPrep");
            var publishMission = deployPrep?.GetMethod("PublishMission", All);
            var suppress = deployPrep?.GetMethod("SuppressNextEntitledEnter", All);
            var consume = deployPrep?.GetMethod("ConsumeEntitledEnter", All);
            var enterPatch = mod.GetType("Multiplayer.Network.Sync.DeployPrepEnterPatch")?.GetMethod("Postfix", All);

            if (hasArrived == null || givenUp == null || watch == null || tick == null || step == null ||
                postfix == null || syncTick == null ||
                hostBroadcast == null || raiseMirrored == null || eventAnswer == null || publishMission == null ||
                suppress == null || consume == null || enterPatch == null)
            {
                yield return "L197 premise-changed: MissionArrivalNav.{MissionHasArrived,ArrivalGivenUp,Watch," +
                             "Tick,Step}, MissionEncounterNav.Postfix, SyncEngine.Tick or " +
                             "EventPopup.{HostBroadcast,RaiseMirrored} no longer " +
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
            if (!Program.Callees(raiseMirrored, mod).Any(m => Same(m, watch)))
                yield return "L197 watch-is-never-armed: EventPopup.RaiseMirrored no longer arms the addressed " +
                             "client's MissionArrivalNav watch.";
            if (Program.Callees(hostBroadcast, mod).Any(m => Same(m, watch)))
                yield return "L197 host-claims-client-entitlement: EventPopup.HostBroadcast arms an arrival " +
                             "watch on the host for a window dispatched to another peer.";
            var stepCalls = Program.Callees(step, mod).ToList();
            if (!stepCalls.Any(m => m.Name == "MayAutoOpen") ||
                !Program.Callees(step, typeof(GeoscapeView).Assembly).Any(m => m.Name == "LaunchMission"))
                yield return "L197 entitled-arrival-opens-nothing: MissionArrivalNav.Step must gate and perform " +
                             "the addressed client's one local LaunchMission after exact ActiveMission arrival.";
            var mayAuto = arrival.GetMethod("MayAutoOpen", All);
            if (mayAuto == null || (bool)mayAuto.Invoke(null, new object[] { true, true }) ||
                !(bool)mayAuto.Invoke(null, new object[] { true, false }) ||
                (bool)mayAuto.Invoke(null, new object[] { false, false }))
                yield return "L197 entitlement-role-broken: only (entitled=true, host=false) may auto-open.";
            if (!stepCalls.Any(m => Same(m, suppress)) ||
                !Program.Callees(enterPatch, mod).Any(m => m.Name == "AnnounceFromEnter"))
                yield return "L197 entitlement-handoff-unwired: entitled arrival must arm an exact-key one-shot " +
                             "and deployment EnterState must consume it before ordinary Announce.";
            suppress.Invoke(null, new object[] { "mission:A" });
            if ((bool)consume.Invoke(null, new object[] { "mission:B" }) ||
                !(bool)consume.Invoke(null, new object[] { "mission:A" }) ||
                (bool)consume.Invoke(null, new object[] { "mission:A" }))
                yield return "L197 entitlement-handoff-not-exactly-once: the per-mission suppression crossed " +
                             "keys or survived its first matching EnterState.";
            var answerCalls = Program.Callees(eventAnswer, mod).ToList();
            if (!answerCalls.Any(m => Same(m, publishMission)))
                yield return "L197 host-publishes-zero: EventSync.HandleAnswer does not call host-only " +
                             "DeployPrep.PublishMission for the exact created mission.";
            var publishCalls = Program.Callees(publishMission, mod).ToList();
            if (publishCalls.Any(m => m.Name == "Announce") ||
                publishCalls.SelectMany(m => Program.Callees(m, mod)).Any(m => m.Name == "Send" &&
                    m.DeclaringType?.Name == "IntentRail"))
                yield return "L197 host-publish-reaches-intent: PublishMission reaches Announce/IntentRail.Send; " +
                             "a host-authoritative write must never masquerade as a client request.";

            // Universal producer audit: every mod callsite reaching native deployment navigation is named.
            var allowed = new HashSet<string> {
                "Multiplayer.Network.Sync.MissionArrivalNav.Step",
                "Multiplayer.Network.Sync.MissionEncounterNav.Postfix",
                "Multiplayer.Network.Sync.DeployPrep.Join"
            };
            foreach (var type in mod.GetTypes())
            foreach (var method in type.GetMethods(All))
            {
                bool navigates = Program.Callees(method, typeof(GeoscapeView).Assembly)
                    .Any(m => m.Name == "LaunchMission" || m.Name == "ToDeploymentState");
                if (navigates && !allowed.Contains(type.FullName + "." + method.Name))
                    yield return "L197 unclassified-deployment-producer: " + type.FullName + "." + method.Name +
                                 " reaches LaunchMission/ToDeploymentState outside PrepJoinButton, an entitled " +
                                 "local arrival, or the local dialog completion funnel.";
            }

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
