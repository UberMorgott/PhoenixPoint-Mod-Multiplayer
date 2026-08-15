using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L554 — NO PEER LIFTS BEFORE ITS OWN FIRST POST-LOAD FRAME HAS RENDERED, AND THE HOST IS NOT EXEMPT.
    ///
    /// READ THE NAME AS THE MECHANISM, NOT AS A WAIT ON PEOPLE. What this law makes every peer wait for is
    /// a FRAME — its own, and then each other peer's, reported automatically. It is not a ready-vote, not
    /// a confirmation, not an acknowledgement of anything a human did, and arm (f) below executes that
    /// distinction rather than asserting it in prose.
    ///
    /// WHAT THIS IS NOT. It is not a second copy of L551 and it does not weaken it. L551 owns "no peer
    /// lifts on its own packet or its own load" — the arrival ARMS the reveal, it does not consume it —
    /// and every one of its arms survives here. What L551 could not state, and what the measurement below
    /// proves it needs, is what the armed reveal is allowed to be RELEASED BY.
    ///
    /// THE MEASUREMENT THAT KILLED THE DEADLINE MODEL (live capture 2026-08-15 21:54, continue-save,
    /// three machines, POSTDATING the fix in 4670949 that L551 encodes):
    ///   host   reveal scheduled (AllDone) dueHostMs=55546 at 21:54:10.242 — lead 400 ms, because the
    ///          measured RTT was ~0 and LeadMs(0) is the floor;
    ///   s2/s3  "[MP][reveal] RevealAll: dueHostMs=55546 hostNow=55804 inMs=-258" — the common instant
    ///          was ALREADY OVERDUE when the packet landed;
    ///   s2/s3  then spent one post-load frame (frameMax 2038.5 ms, worst apply 799.5 ms) and lifted at
    ///          11.862 / 11.854, input live 12.105 / 12.080;
    ///   host   lifted 10.646, input 10.700.
    /// The host was 1.21 s ahead on screen and 1.40 s ahead on input — statistically the same 1.33–1.55 s
    /// it had before the fix. THE MODEL WAS WRONG: the lead was derived from RTT while the real cost is
    /// DELIVERY (~660 ms) plus the receiving peer's OWN first post-load frame (~950 ms), neither of which
    /// an RTT sample of ~0 can see. An instant chosen on the host's clock cannot express "everybody is
    /// actually ready"; only the peers can say that, and only about themselves.
    ///
    /// WHAT THIS LAW REQUIRES.
    ///   (b) The release predicate is not the deadline. The lift is reachable only through a predicate
    ///       that reads a LOCALLY OBSERVED ready-set over the live roster; a deadline may narrow that
    ///       predicate (a FLOOR: never lift before the common instant) but may never satisfy it alone.
    ///   (c) A peer is ready only after one of its OWN rendered frames has elapsed past the armed
    ///       boundary — a frame COUNT, never a clock and never "my load callback returned", because the
    ///       load callback returns in the middle of the two-second frame it is still inside.
    ///   (e) A peer that leaves shrinks the expected set, and a peer that stops talking is given up on
    ///       after a bounded local wait. Neither can hold anybody.
    ///
    /// THIS IS NOT A QUORUM, AND HERE IS THE WHOLE ARGUMENT (P13 / NO QUORUMS, E:\DEV\PhoenixPoint\CLAUDE.md).
    /// The mandate's line is between waiting on a human ACTION and waiting on a LOAD that ends by itself.
    ///   • READY IS DEFINED AS, EXACTLY: this peer's load finished AND one of this peer's own frames has
    ///     rendered past that point. Nothing else is an input. No keypress, no confirm button, no "I am
    ///     here" acknowledgement, no lobby ready toggle, no human decision of any kind participates —
    ///     arm (f) fails the law if the reveal path so much as reaches the lobby readiness surface.
    ///   • AN AFK PEER CANNOT WEDGE IT. Its machine keeps loading and keeps rendering with nobody at the
    ///     keyboard, so it reports ready on the same frame it would have with a player watching. There is
    ///     no state an idle human can leave this barrier in — which is precisely why the load barrier this
    ///     one sits behind was always allowed (the curtain/reveal barrier, CLAUDE.md's named ALLOWED case).
    ///   • A DEPARTED OR PAUSED PEER SHRINKS THE EXPECTED SET. The set is GetLiveRosterSlots, re-asked
    ///     every frame — the same set the load barrier already uses — so leaving or pausing REMOVES you
    ///     from the wait rather than extending it. Arm (e) executes both directions.
    ///   • A SILENT PEER HAS A BOUNDED FALLBACK. A peer that neither leaves nor reports (crashed process,
    ///     dead link that transport has not yet timed out) is dropped from the expected set
    ///     RevealSchedule.ReadyGiveUpMs after this peer armed its own reveal — measured on the LOCAL
    ///     clock, so it cannot be withheld by the very peer that went quiet. The cost is bounded and
    ///     one-sided: at worst everyone waits that long once, and then lifts. Nobody is ever stranded,
    ///     because the give-up is the only branch that does not need the missing peer to do anything.
    /// The residual wait is therefore "the slowest live machine's first frame", which is a load — never
    /// a person.
    ///
    /// Falsify (each verified RED, then restored): release the lift on RevealSchedule.Due alone →
    /// <c>the-lift-is-a-host-chosen-instant</c>; report ready from the arm instead of from a later frame
    /// → <c>readiness-is-not-locally-observed</c>; make MayLift ignore allReady → the truth table;
    /// let the give-up fire at zero → <c>a-silent-peer-is-given-up-on-too-soon</c>; ask the FULL roster
    /// instead of the live one → <c>a-departed-peer-still-holds-the-others</c>; drop the slot from the
    /// wire → <c>the-wire-carries-no-readiness</c>.
    /// </summary>
    internal static class L554_NoLiftBeforeThisPeersOwnFirstFrame
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var mod = coord.Assembly;
            var sched = typeof(RevealSchedule);
            var tracker = typeof(RosterProgressTracker);

            var update = coord.GetMethod("Update", All, null, Type.EmptyTypes, null);
            var lift = coord.GetMethod("PerformDeferredLift", All);
            var tick = coord.GetMethod("TickScheduledReveal", All);
            if (update == null || lift == null || tick == null)
            {
                yield return "L554 premise-changed: SaveTransferCoordinator.{Update,PerformDeferredLift," +
                             "TickScheduledReveal} no longer resolves. The reveal release seam this law is " +
                             "about has moved, and an assertion over a shape that no longer exists passes " +
                             "while saying nothing.";
                yield break;
            }

            // ── (b) THE LIFT IS NOT RELEASED BY A HOST-CHOSEN INSTANT ────────────────
            // Resolved BY NAME, and their ABSENCE is the violation rather than a premise-change: "there is
            // no ready-set at all" is precisely the state the 21:54 capture measured, and reporting it as a
            // moved premise is how a law goes quietly green through the defect it exists to catch.
            var mayLift = sched.GetMethod("MayLift", All);
            var allReady = tracker.GetMethod("AllReady", All);
            var tickCallees = Program.Callees(tick, mod).ToList();
            bool reachesMayLift = mayLift != null && tickCallees.Any(
                m => m.MetadataToken == mayLift.MetadataToken && m.Module == mayLift.Module);
            bool reachesAllReady = allReady != null && tickCallees.Any(
                m => m.MetadataToken == allReady.MetadataToken && m.Module == allReady.Module);

            if (mayLift == null)
                yield return "L554 the-lift-is-a-host-chosen-instant: there is no RevealSchedule.MayLift. " +
                             "The only release predicate in the repo is Due(hostNow, dueHostMs) — an instant " +
                             "the HOST picked, which the 21:54 capture showed arriving 258 ms already " +
                             "overdue while each client still owed one 2038 ms post-load frame. A deadline " +
                             "cannot express 'everybody is actually ready'.";
            else if (!reachesMayLift)
                yield return "L554 the-lift-is-a-host-chosen-instant: TickScheduledReveal does not reach " +
                             "RevealSchedule.MayLift, so whatever releases the lift on this peer is not the " +
                             "predicate that weighs the ready-set against the floor.";

            if (allReady == null)
                yield return "L554 the-lift-is-a-host-chosen-instant: RosterProgressTracker has no AllReady, " +
                             "so no peer can observe for itself that every live peer is ready. Readiness that " +
                             "only the host can observe is exactly the model that failed.";
            else if (!reachesAllReady)
                yield return "L554 the-lift-is-a-host-chosen-instant: the reveal tick never asks " +
                             "RosterProgressTracker.AllReady. The screen must stay down until THIS peer " +
                             "observes the condition — a number shipped from the host is not an observation.";

            // A deadline may narrow the release (floor) but never satisfy it. If the tick still calls Due
            // and does NOT call MayLift, the instant is the whole gate — the shape that shipped and failed.
            var due = sched.GetMethod("Due", All);
            if (due != null && !reachesMayLift &&
                tickCallees.Any(m => m.MetadataToken == due.MetadataToken && m.Module == due.Module))
                yield return "L554 the-lift-is-a-host-chosen-instant: RevealSchedule.Due is the only gate " +
                             "between TickScheduledReveal and the lift. Measured: the host lifted 1.21 s " +
                             "before its clients WITH that gate green, because the instant was already in " +
                             "the past when it reached them. Any deadline is a FLOOR, never a ceiling.";

            // ── (c) READINESS IS THIS PEER'S OWN RENDERED FRAME ──────────────────────
            var readyFrame = coord.GetField("_revealReadyDueFrame", All);
            var flushReady = coord.GetMethod("FlushRevealReady", All);
            if (readyFrame == null || flushReady == null)
                yield return "L554 readiness-is-not-locally-observed: SaveTransferCoordinator has no " +
                             "_revealReadyDueFrame / FlushRevealReady pair. A peer reaches Playing in the " +
                             "MIDDLE of one enormous blocking frame and every 'I am loaded' said from inside " +
                             "that frame is a lie about readiness — the only honest proof is a COMPLETED " +
                             "frame past the point being claimed, which is a frame count and nothing else.";
            else
            {
                if (!Program.Callees(tick, mod).Any(m => m.MetadataToken == flushReady.MetadataToken &&
                                                          m.Module == flushReady.Module) &&
                    !Program.CalleeSequence(update).Any(m => m.MetadataToken == flushReady.MetadataToken &&
                                                             m.Module == flushReady.Module))
                    yield return "L554 readiness-is-not-locally-observed: nothing on the per-frame path " +
                                 "reaches FlushRevealReady, so the armed readiness report is never made and " +
                                 "the barrier would hold on every peer forever.";
                // NOT module-filtered on purpose: Time.frameCount lives in UnityEngine, and the whole
                // claim is that the readiness seam reaches THAT counter rather than any clock of ours.
                bool readsFrameCount = Program.CalleeSequence(flushReady)
                    .Any(m => m.Name == "get_frameCount");
                if (!readsFrameCount)
                    yield return "L554 readiness-is-not-locally-observed: the readiness seam never reads " +
                                 "Time.frameCount. A clock cannot say a frame COMPLETED — that is the whole " +
                                 "difference between 'my load callback returned' and 'I can actually render', " +
                                 "and it is the 950 ms the 21:54 capture found between them.";
            }

            // ── (d) THE RELEASE PREDICATE, EXECUTED ──────────────────────────────────
            if (mayLift != null)
            {
                int giveUp = (int)(sched.GetField("ReadyGiveUpMs", All)?.GetRawConstantValue() ?? 0);
                Func<bool, long, long, long, bool> may = (ready, now, dueMs, since) =>
                    (bool)mayLift.Invoke(null, new object[] { ready, now, dueMs, since });

                if (giveUp <= 0)
                    yield return "L554 a-silent-peer-is-given-up-on-too-soon: RevealSchedule.ReadyGiveUpMs " +
                                 "is " + giveUp + ". A peer that stops talking without leaving must be " +
                                 "released from the expected set after a BOUNDED wait, or the barrier it is " +
                                 "in wedges — and a bound of zero is no barrier at all.";
                if (may(false, 1000L, 1000L, 0L))
                    yield return "L554 the-lift-is-a-host-chosen-instant: MayLift releases the curtain with " +
                                 "the ready-set INCOMPLETE the instant the deadline passes. That is the " +
                                 "deadline acting as a ceiling, which is the 1.21 s head start, measured.";
                if (may(true, 999L, 1000L, 0L))
                    yield return "L554 the-lift-is-a-host-chosen-instant: MayLift releases BEFORE the common " +
                                 "instant. The instant is a floor: everybody ready early still waits for it, " +
                                 "so a fast peer can never re-acquire a head start.";
                if (!may(true, 1000L, 1000L, 0L))
                    yield return "L554 the-lift-is-a-host-chosen-instant: MayLift refuses to lift with every " +
                                 "live peer ready and the floor passed. There is nothing left to wait for, " +
                                 "and a barrier that never opens is worse than the skew it replaced.";
                if (giveUp > 0)
                {
                    if (!may(false, 1000L + giveUp, 1000L, giveUp))
                        yield return "L554 a-silent-peer-can-wedge-the-barrier: a peer that never reports " +
                                     "ready holds every other peer behind the curtain forever. Silence must " +
                                     "shrink the expected set after " + giveUp + "ms — waiting on a LOAD is " +
                                     "allowed (P13), waiting on a peer that has stopped existing is not.";
                    if (may(false, 1000L + giveUp, 1000L, giveUp - 1))
                        yield return "L554 a-silent-peer-is-given-up-on-too-soon: the give-up fires before " +
                                     "its own bound has elapsed locally. A slow peer would be abandoned " +
                                     "mid-load and the barrier would be decorative.";
                }
            }

            // ── (e) A DEPARTED PEER SHRINKS THE SET, EXECUTED ────────────────────────
            if (allReady != null && tracker.GetMethod("MarkReady", All) != null)
            {
                var t = new RosterProgressTracker();
                var mark = tracker.GetMethod("MarkReady", All);
                mark.Invoke(t, new object[] { (byte)0 });
                mark.Invoke(t, new object[] { (byte)1 });
                bool withGhost = (bool)allReady.Invoke(t, new object[] { new List<byte> { 0, 1, 2 } });
                bool withoutGhost = (bool)allReady.Invoke(t, new object[] { new List<byte> { 0, 1 } });
                if (withGhost)
                    yield return "L554 a-departed-peer-still-holds-the-others: AllReady is true with slot 2 " +
                                 "expected and never marked. A peer that is still in the roster and not yet " +
                                 "ready must hold the reveal — that IS the invariant.";
                if (!withoutGhost)
                    yield return "L554 a-departed-peer-still-holds-the-others: AllReady is false once the " +
                                 "departed slot leaves the expected set. A peer that left must stop being " +
                                 "waited for the moment the roster stops listing it, or one quit hangs the " +
                                 "session — the roster is asked LIVE for exactly this reason.";
            }

            // ── (f) NO HUMAN IS EVER WAITED ON (P13) ─────────────────────────────────
            // A ready-set derived from FRAMES is allowed; a ready-set derived from a person pressing
            // something is the quorum the mandate forbids. The two are one identifier apart in this file,
            // so the reveal path may not mention the lobby readiness surface at all.
            var humanish = new[] { "IsReady", "get_IsReady", "set_IsReady", "AllClientsReady",
                                   "OnClientReady", "OnClientUnready", "EveryoneReady", "ReadyCount" };
            foreach (var seam in new[] { tick, flushReady, coord.GetMethod("OnRevealReady", All) })
            {
                if (seam == null) continue;
                foreach (var c in Program.Callees(seam, mod))
                    if (humanish.Contains(c.Name))
                        yield return "L554 the-barrier-waits-on-a-person: " + seam.Name + " reaches " +
                                     (c.DeclaringType?.Name ?? "?") + "." + c.Name + ". Readiness here means " +
                                     "'a frame of mine has rendered', which happens to an AFK player exactly " +
                                     "as fast as to an attentive one. The moment it means 'somebody pressed " +
                                     "something' this is a quorum and one AFK peer can hang the campaign.";
            }

            // ── (g) NON-VACUITY: the wire really carries the readiness ───────────────
            var ser = typeof(Multiplayer.Network.MessageLayer.MessageSerializer);
            var write = ser.GetMethod("SerializeRevealReady", All);
            var read = ser.GetMethod("DeserializeRevealReady", All);
            if (write == null || read == null)
                yield return "L554 the-wire-carries-no-readiness: there is no RevealReady codec. Every arm " +
                             "above orders peers against a ready-set that no peer could ever hear about, so " +
                             "this law would go green over a barrier that cannot exist.";
            else
            {
                var boundary = Guid.NewGuid();
                var back = read.Invoke(null, new object[] { write.Invoke(null, new object[] { (byte)3, boundary }) });
                // ValueTuple exposes Item1/Item2 as FIELDS, not properties.
                var slotBack = (byte)back.GetType().GetField("Item1").GetValue(back);
                var idBack = (Guid)back.GetType().GetField("Item2").GetValue(back);
                if (slotBack != 3 || idBack != boundary)
                    yield return "L554 the-wire-carries-no-readiness: RevealReady does not round-trip its " +
                                 "(slot, boundary) pair — got (" + slotBack + ", " +
                                 idBack.ToString("N").Substring(0, 8) + "). A readiness that names the wrong " +
                                 "peer or the wrong boundary releases a barrier nobody was standing at.";
            }
        }
    }
}
