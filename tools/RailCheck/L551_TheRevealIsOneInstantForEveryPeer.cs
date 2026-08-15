using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L551 — EVERY PEER LEAVES THE LOADING SCREEN AT THE SAME INSTANT, NOT ON ITS OWN PACKET.
    ///
    /// WHAT THIS IS NOT. It is not a second copy of L94/L143/L513/L433, and it does not weaken one of
    /// them. Those own the BARRIER — who may arm it, who may release it, that no peer lifts before its
    /// boundary releases, that the release packet is bound to the host's authority. All four were GREEN
    /// through this defect and correctly so: the barrier was doing its job. This law owns what happens
    /// AFTER the release, which nothing asserted at all.
    ///
    /// THE BUG IT ENCODES (measured 2026-08-15 20:43, three machines, reported ~15 times before it was
    /// ever measured). The host called <c>PerformDeferredLift</c> in the SAME frame of
    /// <c>SaveTransferCoordinator.Update</c> in which it broadcast <c>RevealAll</c>. So:
    ///   lift        host 52.897   s3 53.317 (+420 ms)   s2 53.544 (+647 ms)
    ///   input live  host 52.994   s3 54.325 (+1331 ms)  s2 54.546 (+1552 ms)
    /// The host had a 1.33–1.55 s head start on a shared geoscape, and the client order tracked their
    /// LOAD order — "whoever loads first gets in first". The reveal was one MESSAGE; it has to be one
    /// INSTANT (<see cref="RevealSchedule"/>).
    ///
    /// WHAT THE INSTANT TURNED OUT NOT TO BE — READ WITH L554. Re-measured 2026-08-15 21:54 with the
    /// deadline shipped: RTT sampled ~0, so the lead was the 400 ms floor, so both clients read the
    /// shipped instant as inMs=-258 (already overdue on arrival) and still owed one 2038 ms post-load
    /// frame each. Host 10.646 vs clients 11.862/11.854 — the head start came back with every arm below
    /// green, correctly, because none of them is about what RELEASES the reveal. The instant is now a
    /// FLOOR and L554 owns the release. Every arm here survives unchanged except (e), which was
    /// RE-EXPRESSED rather than weakened: see its comment.
    ///
    /// AND IT IS STILL NOT A QUORUM (P13 / NO QUORUMS). The distinction the mandate draws is between
    /// waiting on a human ACTION and waiting on something that ends by itself — a deadline, a load, a
    /// rendered frame. Arm (e) keeps the half that is this law's: the instant is MINTED from the clock
    /// and the measured link alone, never from who is present, so a departure cannot shift anyone's
    /// floor; and neither seam may touch human readiness. Arm (d) pins the other end — a peer whose
    /// instant already passed while the packet was in flight is never stranded by it.
    ///
    /// Falsify (each verified RED, then restored): call <c>PerformDeferredLift()</c> from
    /// <c>Update</c>'s AllDone branch again → <c>host-lifts-on-its-own-broadcast-frame</c>; make
    /// <c>OnRevealAll</c> lift directly instead of scheduling → <c>arrival-is-the-lift</c>; return a
    /// constant from <c>RevealSchedule.LeadMs</c> → <c>lead-is-not-measured</c>; make <c>Due</c> strict
    /// (<c>&gt;</c>) → <c>a-passed-instant-strands-its-peer</c>; have the tick ask the roster →
    /// <c>the-deadline-waits-on-a-person</c>; drop the instant from the wire → <c>the-wire-carries-no-instant</c>.
    /// </summary>
    internal static class L551_TheRevealIsOneInstantForEveryPeer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var mod = coord.Assembly;

            var update = coord.GetMethod("Update", All, null, Type.EmptyTypes, null);
            var lift = coord.GetMethod("PerformDeferredLift", All);
            var onReveal = coord.GetMethod("OnRevealAll", All);
            if (update == null || lift == null || onReveal == null)
            {
                yield return "L551 premise-changed: SaveTransferCoordinator.{Update,PerformDeferredLift," +
                             "OnRevealAll} no longer resolves. The reveal seam this law is about has moved, " +
                             "and an assertion over a shape that no longer exists passes while saying nothing.";
                yield break;
            }

            // The two seams the fix introduces. Resolved BY NAME rather than by type so their absence is
            // reported as the real failure it is (nothing schedules the reveal) instead of as a
            // premise-change, which is how this defect stayed invisible for the life of the mod.
            var schedule = coord.GetMethod("ScheduleReveal", All);
            var tick = coord.GetMethod("TickScheduledReveal", All);

            // ── (a) THE HOST DOES NOT LIFT ON THE FRAME IT BROADCASTS ────────────────
            // Update is where AllDone releases the barrier and broadcasts. A DIRECT call to the lift from
            // that method is, by construction, a lift on the broadcast frame: every other peer's lift is at
            // least one wire hop and one of its own frames away.
            var updateCallees = Program.CalleeSequence(update);
            if (updateCallees.Any(m => m.MetadataToken == lift.MetadataToken && m.Module == lift.Module))
                yield return "L551 host-lifts-on-its-own-broadcast-frame: SaveTransferCoordinator.Update " +
                             "calls PerformDeferredLift directly. That is the same frame as the RevealAll " +
                             "broadcast, so the host is on the geoscape while every client is still a hop and " +
                             "a post-load frame behind it — 1.33–1.55 s of head start, measured. The lift must " +
                             "be reached only through the scheduled common instant.";

            if (tick == null)
                yield return "L551 host-lifts-on-its-own-broadcast-frame: there is no " +
                             "SaveTransferCoordinator.TickScheduledReveal at all, so no peer has a common " +
                             "instant to lift at and every peer lifts when its own packet or its own load " +
                             "happens to land.";
            else if (!updateCallees.Any(m => m.MetadataToken == tick.MetadataToken && m.Module == tick.Module))
                yield return "L551 host-lifts-on-its-own-broadcast-frame: Update never reaches " +
                             "TickScheduledReveal, so a scheduled reveal instant is armed and never fires — " +
                             "the barrier would hold forever on every peer.";
            else if (!Program.Callees(tick, mod).Any(m => m.MetadataToken == lift.MetadataToken &&
                                                          m.Module == lift.Module))
                yield return "L551 host-lifts-on-its-own-broadcast-frame: TickScheduledReveal does not reach " +
                             "PerformDeferredLift, so the scheduler schedules nothing that reveals.";

            // ── (b) ARRIVAL IS NOT THE LIFT ──────────────────────────────────────────
            if (Program.Callees(onReveal, mod).Any(m => m.MetadataToken == lift.MetadataToken &&
                                                        m.Module == lift.Module))
                yield return "L551 arrival-is-the-lift: OnRevealAll reaches PerformDeferredLift. A client that " +
                             "lifts on ARRIVAL lifts at t0 + hop + a frame, which is precisely the skew this " +
                             "law exists to remove and which tracked each client's load order in the live " +
                             "measurement. The arrival must ARM the common instant, not consume it.";
            if (schedule == null)
                yield return "L551 arrival-is-the-lift: there is no SaveTransferCoordinator.ScheduleReveal, so " +
                             "the reveal has no common instant to be armed with on either role.";
            else if (!Program.Callees(onReveal, mod).Any(m => m.MetadataToken == schedule.MetadataToken &&
                                                              m.Module == schedule.Module))
                yield return "L551 arrival-is-the-lift: OnRevealAll does not reach ScheduleReveal, so the " +
                             "instant the host put on the wire is deserialized and discarded — which is " +
                             "exactly what happened to the send timestamp for the life of the mod.";
            else if (!Program.Callees(update, mod).Any(m => m.MetadataToken == schedule.MetadataToken &&
                                                            m.Module == schedule.Module))
                yield return "L551 arrival-is-the-lift: the host's own release does not reach ScheduleReveal, " +
                             "so the host is not bound by the instant it hands to everyone else — one peer " +
                             "outside the scheme is the whole defect.";

            // ── (c) THE LEAD IS MEASURED, NOT GUESSED — EXECUTED ─────────────────────
            int leadZero = RevealSchedule.LeadMs(0);
            int leadSlow = RevealSchedule.LeadMs(1200);
            int leadNeg = RevealSchedule.LeadMs(-1);
            int leadWild = RevealSchedule.LeadMs(int.MaxValue / 2);
            if (leadSlow <= leadZero)
                yield return "L551 lead-is-not-measured: RevealSchedule.LeadMs returns " + leadSlow +
                             "ms for a 1200ms link and " + leadZero + "ms for a 0ms one. A lead that does not " +
                             "grow with the MEASURED round trip is a guessed constant, and a guessed constant " +
                             "is either too small on a bad link (the head start comes back) or dead black " +
                             "screen on a good one.";
            if (leadZero < RevealSchedule.MinLeadMs)
                yield return "L551 lead-is-not-measured: a 0ms link yields a " + leadZero + "ms lead, under " +
                             "the " + RevealSchedule.MinLeadMs + "ms floor. Below one comfortable frame the " +
                             "host is back to lifting on its own broadcast frame.";
            if (leadWild > RevealSchedule.MaxLeadMs || leadNeg < RevealSchedule.MinLeadMs)
                yield return "L551 lead-is-not-measured: the lead is unclamped (pathological link → " +
                             leadWild + "ms, unmeasured link → " + leadNeg + "ms). The lead is black screen " +
                             "for EVERY peer, so it must be bounded at both ends; an unknown ping must fall " +
                             "back to the floor, never to zero.";

            // ── (d) A PASSED INSTANT LIFTS NOW — LATE, NEVER EARLY, NEVER BLOCKED ────
            if (!RevealSchedule.Due(1000L, 1000L))
                yield return "L551 a-passed-instant-strands-its-peer: Due(1000,1000) is false. The comparison " +
                             "must be inclusive: a peer whose instant elapsed while the packet was in flight " +
                             "has to lift on its very next frame, not wait for a second edge that never comes.";
            if (!RevealSchedule.Due(9999L, 1000L))
                yield return "L551 a-passed-instant-strands-its-peer: a long-passed instant (now=9999, " +
                             "due=1000) does not fire. A late arrival must never be able to hang behind the " +
                             "curtain — that would turn a deadline into the blocker P13 forbids.";
            if (RevealSchedule.Due(999L, 1000L))
                yield return "L551 a-passed-instant-strands-its-peer: Due(999,1000) is true, so a peer lifts " +
                             "BEFORE the common instant. Early is the defect; the whole scheme is that nobody " +
                             "is early.";

            // ── (e) THE INSTANT IS MINTED FROM THE CLOCK ALONE — NO QUORUM, NO PERSON ─
            // P13, RE-EXPRESSED 2026-08-15 AND NOT WEAKENED. This arm used to cover the ARM and the TICK
            // together and to forbid BOTH of them from consulting the roster or the progress tracker at
            // all. That was right while the instant was the whole release; it stopped being right the
            // moment the instant was demoted to a FLOOR. The 21:54 capture proved a host-clock instant
            // cannot express "everybody is actually ready" — it landed 258 ms overdue on peers that still
            // owed a 2038 ms frame each — so the RELEASE now also waits on every live peer's own rendered
            // frame (L554), and a tick that asks the live roster is asking about LOADS, which the mandate
            // names as the ALLOWED case, not about people.
            //
            // What this arm still owns, undiminished, is the MINTING: the instant the host puts on the
            // wire is a function of the clock and the measured link, never of who is present. If the
            // deadline itself moved with the roster, a peer that left could shift everybody's floor.
            foreach (var c in schedule == null ? Enumerable.Empty<MethodBase>() : Program.Callees(schedule, mod))
            {
                if (c.DeclaringType == null) continue;
                if (c.Name == "AllDone" || c.Name == "IsDone" || c.Name == "AllReady" ||
                    c.Name.StartsWith("GetRosterSlots") || c.Name == "GetLiveRosterSlots")
                    yield return "L551 the-deadline-waits-on-a-person: ScheduleReveal calls " +
                                 c.DeclaringType.Name + "." + c.Name + ". The common instant must be minted " +
                                 "from the clock and the measured link alone — a floor that moves with who " +
                                 "is present is not a floor, and a peer that leaves would shift everyone " +
                                 "else's.";
            }
            // And BOTH seams stay categorically clear of human readiness. This is the line the mandate
            // actually draws: waiting on a load or a frame is allowed because it ends by itself; waiting
            // on somebody to press something is the quorum, and it is one identifier away in this file.
            var humanReadiness = new[] { "IsReady", "get_IsReady", "set_IsReady", "AllClientsReady",
                                         "OnClientReady", "OnClientUnready", "EveryoneReady", "ReadyCount" };
            foreach (var seam in new[] { schedule, tick })
            {
                if (seam == null) continue;
                foreach (var c in Program.Callees(seam, mod))
                    if (humanReadiness.Contains(c.Name))
                        yield return "L551 the-deadline-waits-on-a-person: " + seam.Name + " consults " +
                                     (c.DeclaringType?.Name ?? "?") + "." + c.Name + ". Neither arming nor " +
                                     "firing the reveal may become a question about what a HUMAN did — an " +
                                     "AFK-but-connected peer would hang the campaign forever.";
            }

            // ── (f) NON-VACUITY: the wire really carries the instant ─────────────────
            var boundary = Guid.NewGuid();
            var payload = Multiplayer.Network.MessageLayer.MessageSerializer.SerializeRevealAll(boundary, 123456789L);
            var back = Multiplayer.Network.MessageLayer.MessageSerializer.DeserializeRevealAll(payload);
            if (back.Item1 != boundary || back.Item2 != 123456789L)
                yield return "L551 the-wire-carries-no-instant: RevealAll does not round-trip its (boundary, " +
                             "instant) pair — got (" + back.Item1.ToString("N").Substring(0, 8) + ", " +
                             back.Item2 + "). Every arm above orders peers against an instant only the host " +
                             "knows; if the wire drops it, they are all back to lifting on arrival and this " +
                             "law would go green through the very defect it encodes.";
        }
    }
}
