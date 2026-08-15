using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L513 — NO PEER LIFTS BEFORE ITS BOUNDARY RELEASES, AND AN ARM THAT DOES NOT HAPPEN SAYS SO.
    ///
    /// THE RECURRENCE (~8 reports): coming back from a mission, one peer's curtain comes up while the
    /// others are still loading. <c>L94_LoadBarrier</c> (67 KB), <c>L143</c>, <c>L86</c>, <c>L338</c> and
    /// <c>L151</c> assert that the seam EXISTS, is reached from the funnel, and resets its latches.
    ///
    /// MEASURED CORRECTION TO THE RCA (2026-08-15): it is NOT true that no existing law executes the
    /// ordering property — mutating <c>RosterProgressTracker.AllDone</c> to release on the first reporting
    /// slot turns <c>L94 releases-early</c> (three lines) and <c>L174 barrier-stops-waiting</c> RED as well
    /// as this law's arm (c). So arms (c)-(f) below are a SECOND, independent statement of a property L94
    /// already executes, kept because they are cheap and say it in this law's own words. THE PART THAT WAS
    /// GENUINELY UNPROVEN IS ARMS (a) AND (b), and it is the recurrence mechanism: the two early returns
    /// that skip the arm returned in SILENCE.
    ///
    /// THE BARRIER ITSELF IS CORRECT AND STAYS. It waits on a LOAD that ends by itself, and a departed peer
    /// SHRINKS the expected set (arm (e) executes exactly that) — it is not a quorum and never waits on a
    /// human ACTION. Removing it is not the fix and must not be attempted.
    ///
    /// WHAT WAS ACTUALLY UNPROVABLE: the two early returns that skip the arm returned in SILENCE
    /// (<c>SaveTransferCoordinator.OpenReturnBarrier</c> on <c>!_revealed || _barrierOpen</c>,
    /// <c>ArmSelfLoadBarrier</c> on <c>!IsActive || !SessionStarted</c>). A peer that was never armed then
    /// lifts on its own native Loaded→Playing, which is INDISTINGUISHABLE from a barrier that failed — so
    /// eight recurrences produced zero evidence. Both refusals now route through a pure named predicate,
    /// and this law executes the whole truth table: a refusal without a reason is unrepresentable.
    ///
    /// THE ARMS:
    ///   (a) <c>silent-arm-refusal</c>, EXECUTED over the full truth table — every input on which either
    ///       arm refuses must yield a NON-EMPTY reason, and the one arming input must yield null.
    ///   (b) <c>refusal-unlogged</c> — IL: each arm site reaches its own predicate AND MpLog, so the reason
    ///       computed in (a) actually reaches the log instead of being computed and dropped.
    ///   (c) <c>peer-lifts-before-the-roster</c>, EXECUTED — the REAL release predicate driven as a real
    ///       3-slot boundary: arm, then slots report one at a time; <c>AllDone</c> must stay FALSE until
    ///       the third reports. This is the ordering property itself; L94 executes it too (see above).
    ///   (d) <c>arm-does-not-clear-the-last-boundary</c>, EXECUTED — a re-arm (<c>Reset</c>) must drop the
    ///       PREVIOUS boundary's done-marks, or the next boundary is already "all done" at its first frame
    ///       and every peer lifts immediately: the recurrence, exactly.
    ///   (e) <c>departed-peer-holds-forever</c>, NON-VACUITY and the anti-quorum arm, EXECUTED — with the
    ///       expected set shrunk to the peers that are still here, the release fires. Nothing waits on a
    ///       human; a peer that left cannot hold the rest.
    ///   (f) <c>unauthenticated-reveal-lifts</c>, EXECUTED — the other release path
    ///       (<c>LoadBarrierAuthority.AcceptRevealAll</c>) refuses a peer that is not in a load phase, one
    ///       that already revealed, a foreign sender and a stale boundary id, and accepts the authentic one.
    ///
    /// WHAT THIS LAW DOES NOT PROVE, stated plainly:
    ///   • It does NOT execute <c>SaveTransferCoordinator.Update</c> / <c>OnReachedPlaying</c> /
    ///     <c>PerformDeferredLift</c>. Those touch the Unity curtain, the overlay and the transport, none of
    ///     which exists in RailCheck; the coordinator cannot be instantiated here. The release DECISION is
    ///     executed, the widget teardown is not.
    ///   • It does NOT prove the arm sites are CALLED at every boundary — that is L143's and L94's job.
    ///   • It does NOT prove the log line reaches a file, only that the refusal has a reason and the site
    ///     reaches the logger.
    ///
    /// Falsify:
    ///   • return null (arm) from <c>ReturnBarrierRefusal</c> for a refusing input → (a)
    ///   • return "" for any refusing input → (a)
    ///     [VERIFIED RED 2026-08-15, restored GREEN]
    ///   • drop the <c>MpLog</c> call from either refusal branch → (b)
    ///   • make <c>RosterProgressTracker.AllDone</c> true on the FIRST done slot → (c)+(e)
    ///     [VERIFIED RED 2026-08-15, restored GREEN — and it also turns L94/L174 red, see above]
    ///   • make <c>Reset</c> keep the done set → (d)
    /// </summary>
    internal static class L513_NoPeerLiftsBeforeItsBoundaryReleases
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(RosterProgressTracker).Assembly;
            var coord = mod.GetType("Multiplayer.Network.SaveTransferCoordinator");
            var returnRefusal = coord?.GetMethod("ReturnBarrierRefusal", All);
            var selfRefusal = coord?.GetMethod("SelfLoadArmRefusal", All);
            var openReturn = coord?.GetMethod("OpenReturnBarrier", All);
            var armSelf = coord?.GetMethod("ArmSelfLoadBarrier", All);
            var authority = mod.GetType("Multiplayer.Network.LoadBarrierAuthority");
            var accept = authority?.GetMethod("AcceptRevealAll", All);
            if (returnRefusal == null || selfRefusal == null || openReturn == null || armSelf == null ||
                accept == null)
            {
                yield return "L513 premise-changed: SaveTransferCoordinator.ReturnBarrierRefusal / " +
                             ".SelfLoadArmRefusal / .OpenReturnBarrier / .ArmSelfLoadBarrier or " +
                             "LoadBarrierAuthority.AcceptRevealAll did not resolve. The load barrier's arm " +
                             "decision has moved; re-point this law before believing the barrier is proven — " +
                             "its failure mode is a peer lifting alone with NOTHING in any log.";
                yield break;
            }

            // ── (a) EXECUTED: no refusal is silent, and the arming input still arms ─────────────────────
            foreach (var row in new[]
            {
                new object[] { false, false }, new object[] { false, true }, new object[] { true, true },
            })
            {
                var reason = returnRefusal.Invoke(null, row) as string;
                if (!string.IsNullOrEmpty(reason)) continue;
                yield return "L513 silent-arm-refusal: OpenReturnBarrier refuses to arm for revealed=" +
                             row[0] + " barrierOpen=" + row[1] + " with no reason to log. That is the whole " +
                             "recurrence mechanism: the peer is not armed, it lifts on its own native " +
                             "Loaded→Playing, and nothing anywhere says the barrier was skipped — eight " +
                             "reports, zero evidence.";
            }
            if (returnRefusal.Invoke(null, new object[] { true, false }) != null)
                yield return "L513 arm-never-happens: OpenReturnBarrier refuses even the one input it exists " +
                             "to arm on (revealed, no entry transfer). A barrier that never arms holds nobody " +
                             "and every peer lifts alone — arm (a) would stay green while the feature is dead.";

            foreach (var row in new[]
            {
                new object[] { false, false, false }, new object[] { true, false, false },
                new object[] { true, true, false }, new object[] { false, true, true },
            })
            {
                var reason = selfRefusal.Invoke(null, row) as string;
                if (!string.IsNullOrEmpty(reason)) continue;
                yield return "L513 silent-arm-refusal: ArmSelfLoadBarrier refuses to arm for haveEngine=" +
                             row[0] + " engineActive=" + row[1] + " sessionStarted=" + row[2] + " with no " +
                             "reason to log — the same unobservable skip as above, on the tac→geo return and " +
                             "the native tactical entry.";
            }
            if (selfRefusal.Invoke(null, new object[] { true, true, true }) != null)
                yield return "L513 arm-never-happens: ArmSelfLoadBarrier refuses a live, started co-op " +
                             "session, so the self-load boundary never holds anybody at all.";

            // ── (b) and the reason actually reaches the log ─────────────────────────────────────────────
            foreach (var pair in new[]
            {
                new { Site = openReturn, Predicate = returnRefusal, Name = "OpenReturnBarrier" },
                new { Site = armSelf, Predicate = selfRefusal, Name = "ArmSelfLoadBarrier" },
            })
            {
                var callees = Program.Callees(pair.Site, mod).ToList();
                bool asks = callees.Any(c => c.MetadataToken == pair.Predicate.MetadataToken &&
                                             c.Module == pair.Predicate.Module);
                bool logs = callees.Any(c => c != null && c.DeclaringType != null &&
                                             c.DeclaringType.Name == "MpLog");
                if (asks && logs) continue;
                yield return "L513 refusal-unlogged: " + pair.Name +
                             (asks ? " does not reach MpLog" : " does not ask its own refusal predicate") +
                             ". A reason computed and dropped is the silent early return with extra steps, " +
                             "and it is the reason eight recurrences of 'one peer's curtain came up early' " +
                             "produced no evidence at all.";
            }

            // ── (c) EXECUTED: the ordering property, over the REAL release predicate ────────────────────
            var slots = new byte[] { 0, 1, 2 };
            var tracker = new RosterProgressTracker();
            tracker.Reset();                                  // the arm
            if (tracker.AllDone(slots))
                yield return "L513 peer-lifts-before-the-roster: a freshly armed 3-slot boundary reports " +
                             "ALL DONE before a single peer has reported anything, so the reveal fires on the " +
                             "first frame of the load and every peer lifts immediately.";
            for (int i = 0; i < slots.Length; i++)
            {
                tracker.MarkDone(slots[i]);
                bool done = tracker.AllDone(slots);
                bool last = i == slots.Length - 1;
                if (done == last) continue;
                yield return "L513 peer-lifts-before-the-roster: with " + (i + 1) + " of 3 roster slots " +
                             "reporting load-complete the boundary says " + (done ? "RELEASE" : "HOLD") +
                             ". The curtain must stay down until the LAST live peer is loaded — this is the " +
                             "property the eight previous fixes asserted statically and never executed.";
            }

            // ── (d) EXECUTED: a re-arm forgets the previous boundary ────────────────────────────────────
            tracker.Reset();
            if (tracker.AllDone(slots))
                yield return "L513 arm-does-not-clear-the-last-boundary: after re-arming, the PREVIOUS " +
                             "boundary's done-marks still satisfy the release, so the next mission return is " +
                             "'all loaded' at its first frame and every peer lifts at once — the recurrence, " +
                             "reproduced from the last boundary's leftovers.";

            // ── (e) NON-VACUITY + ANTI-QUORUM: a peer that LEFT shrinks the expected set ────────────────
            tracker.MarkDone(0);
            tracker.MarkDone(1);
            if (!tracker.AllDone(new byte[] { 0, 1 }))
                yield return "L513 departed-peer-holds-forever: with slot 2 gone from the roster and both " +
                             "remaining peers loaded, the boundary still HOLDS. The barrier waits on a LOAD " +
                             "that ends by itself and a departed peer must shrink the expected set; a hold " +
                             "that outlives the peer it waits for is the one shape that would make this a " +
                             "wait on a person (P13) instead of on a load.";
            if (tracker.AllDone(new byte[] { 0, 1, 2 }))
                yield return "L513 departed-peer-holds-forever: the release also fires for a slot that never " +
                             "reported, so arm (e) proves nothing — the expected set is being ignored rather " +
                             "than shrunk.";

            // ── (f) EXECUTED: the authenticated release path refuses everything else ────────────────────
            var boundary = Guid.NewGuid();
            var other = Guid.NewGuid();
            const ulong host = 77UL, stranger = 78UL;
            if (!Accept(accept, false, true, false, boundary, boundary, host, host))
                yield return "L513 unauthenticated-reveal-lifts: the authentic host RevealAll for the CURRENT " +
                             "boundary is refused, so the normal release path is dead and every peer waits for " +
                             "a fallback instead of lifting together.";
            foreach (var bad in new[]
            {
                new { What = "a peer that is not in a load phase", Ok = Accept(accept, false, false, false, boundary, boundary, host, host) },
                new { What = "a peer that already revealed", Ok = Accept(accept, false, true, true, boundary, boundary, host, host) },
                new { What = "a sender that is not the host", Ok = Accept(accept, false, true, false, boundary, boundary, host, stranger) },
                new { What = "a stale boundary id", Ok = Accept(accept, false, true, false, boundary, other, host, host) },
                new { What = "an unset boundary id", Ok = Accept(accept, false, true, false, Guid.Empty, Guid.Empty, host, host) },
            })
            {
                if (!bad.Ok) continue;
                yield return "L513 unauthenticated-reveal-lifts: the reveal is accepted from " + bad.What +
                             ". Every one of these lifts a peer whose own boundary has not released — the " +
                             "curtain comes up early and the peer is looking at a world the others are still " +
                             "loading.";
            }
        }

        private static bool Accept(MethodInfo accept, bool isHost, bool phaseActive, bool revealed,
                                   Guid current, Guid claimed, ulong? hostPeer, ulong sender) =>
            (bool)accept.Invoke(null, new object[] { isHost, phaseActive, revealed, current, claimed,
                                                     hostPeer, sender });
    }
}
