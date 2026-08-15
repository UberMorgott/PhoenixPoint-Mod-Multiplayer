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
    /// AND IT IS STILL NOT A QUORUM (P13 / NO QUORUMS). The distinction the mandate draws is between
    /// waiting on a human ACTION and waiting on something that ends by itself. A deadline ends by
    /// itself: arm (e) below forbids the scheduler and its tick from consulting the roster, the
    /// progress tracker or any peer at all, so a silent, AFK, paused or departed peer cannot postpone
    /// the reveal by one millisecond. Arm (d) pins the other end — a peer whose instant already passed
    /// while the packet was in flight lifts immediately rather than being stranded.
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

            // ── (e) THE DEADLINE ELAPSES BY ITSELF — NO QUORUM, NO PERSON ────────────
            // P13. A time deadline is allowed precisely because nothing human can hold it. The moment the
            // scheduler or its tick consults the roster, the progress tracker or a peer, it stops being a
            // deadline and becomes a wait on somebody, and an AFK-but-connected peer can hang it forever.
            var personish = new[] { "RosterProgressTracker", "SessionManager", "PeerListEntry",
                                    "LobbyController", "NetworkEngine" };
            foreach (var seam in new[] { schedule, tick })
            {
                if (seam == null) continue;
                foreach (var c in Program.Callees(seam, mod))
                {
                    if (c.DeclaringType == null) continue;
                    if (c.Name == "AllDone" || c.Name == "IsDone" || c.Name.StartsWith("GetRosterSlots") ||
                        c.Name == "GetLiveRosterSlots")
                        yield return "L551 the-deadline-waits-on-a-person: " + seam.Name + " calls " +
                                     c.DeclaringType.Name + "." + c.Name + ". The reveal instant must elapse " +
                                     "on its own — a peer that goes silent between the release and the " +
                                     "instant cannot be allowed to postpone anyone's lift. That is the " +
                                     "quorum P13 forbids, arriving through the clock.";
                    if (personish.Contains(c.DeclaringType.Name) && c.Name != "get_IsHost")
                        yield return "L551 the-deadline-waits-on-a-person: " + seam.Name + " consults " +
                                     c.DeclaringType.Name + "." + c.Name + ". Firing this peer's own " +
                                     "deadline must never become a question about another peer.";
                }
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
