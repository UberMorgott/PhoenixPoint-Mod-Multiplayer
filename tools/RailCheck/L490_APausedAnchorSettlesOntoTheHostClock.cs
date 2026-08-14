using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L490 — A CLIENT MAY NOT SIT PERMANENTLY AHEAD OF A HOST CLOCK THAT HAS STOPPED.
    ///
    /// THE REPORT (3-instance session 2026-08-13, BOTH clients, 23:36-23:39):
    /// <c>[MP][clockphase] host=64566043930.800 client=64566045067.226 dGame=1136.426 dReal=paused scale=0.00
    /// sinceLatch=59.8 hostLatchAge=59.9</c> — the client's geoscape clock 1136 GAME seconds AHEAD of the
    /// host's and FROZEN at that value for a minute while both clocks were paused; a second stretch held +792
    /// for 33 s; an earlier session peaked at +671/+714. Constant, not accumulating, with the client not
    /// advancing at all — <see cref="ClockPhaseDiag"/> shape (a): a constant REAL-time lead of the publish lag
    /// (1136 / 0.4 ≈ 2841 game-s per real-s) amplified by the geoscape rate. The visible consequence was on
    /// the client's own log: an exploration leg "ends at &lt;t&gt; which the local clock is already past — a
    /// STALE start", i.e. every "is this interval still open" test the shared clock feeds, answered against a
    /// clock up to ~19 game-minutes early.
    ///
    /// THE DEFECT was an unbounded LIFETIME on a correct compensation. <c>TimeAnchor.Canonical</c> publishes
    /// <c>host Now + priorRate x lag</c> and prices a PAUSE latch at the PRE-pause rate on purpose (L429): the
    /// client's clock is not frozen in flight, so it really does arrive at <c>Now + rate x lag</c> and an
    /// uncompensated anchor would rewind it. Right for the flight; wrong for the minute after it, because the
    /// host is stopped and will never reach the published value. Nothing could take it back: <c>Drifted</c>
    /// reads zero error on a stopped clock, <c>EnforceDrift</c> compares the client against that same
    /// compensated anchor, and a re-latch while paused prices rate 0 but was RATCHETED straight back up by
    /// <c>MonotonePublishedSeconds</c> — the ratchet that exists to stop an in-flight rewind was also holding
    /// a compensation whose flight was long over.
    ///
    /// THE FIX is an expiry on the RATCHET, never on the compensation:
    /// <c>TimeAnchor.CompensationExpired</c> answers true only once a pause has held longer than any publish
    /// lag can be, and <c>PublishFloorSeconds</c> then hands <c>MonotonePublishedSeconds</c> the candidate
    /// itself as its floor, so the anchor comes back DOWN onto the host's honest <c>Now</c>. One backward step
    /// per pause, taken while both clocks are stopped; after it the anchor equals the host clock, the
    /// predicate answers false, and there is no sawtooth and no churn.
    ///
    /// WHY A LAW. The whole failure is invisible on one machine and invisible in one log: both peers agree
    /// with the anchor while disagreeing with each other (the sentence <see cref="ClockPhaseDiag"/> was
    /// written for), and the compensation term is DIMENSIONALLY correct in every direction — the only thing
    /// wrong with it was how long it lived. This is the third bug in this one expression (L100 arm C priced
    /// nothing, L429 priced the wrong rate, this priced forever) and all three read healthy.
    ///
    /// ARMS. Guards first: the subjects must resolve, <c>HostDto</c> must actually CONSULT the expiry (a
    /// predicate no latch asks is decoration), and <c>Canonical</c> must take the decision and still reach the
    /// ratchet through <c>PublishFloorSeconds</c> + <c>MonotonePublishedSeconds</c> (bypassing it elsewhere
    /// would leave this law guarding an unused road, exactly as L429 arm B warns). Then the predicate is
    /// DRIVEN BOTH WAYS over the real functions: it must settle a stale paused anchor, and it must refuse to
    /// settle in every case where settling would rewind a client that is still in flight or contradict L429.
    ///
    /// NON-VACUITY: every subject resolves or the law says so and asserts nothing else. Falsify: drop the
    /// <c>expired</c> term from <c>HostDto</c>'s latch condition → <c>expiry-never-latched</c>; make
    /// <c>PublishFloorSeconds</c> return the previous publication unconditionally → <c>ratchet-never-releases</c>;
    /// make <c>CompensationExpired</c> ignore <c>sinceLatchSeconds</c> → <c>settles-mid-flight</c>; make it
    /// ignore <c>hostPaused</c> → <c>settles-while-running</c>.
    /// </summary>
    internal static class L490_APausedAnchorSettlesOntoTheHostClock
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>Longer than <c>TimeAnchor.MaxPublishLagSeconds</c> (0.4), which is the clamp the flight
        /// itself can never exceed — so a settle at this age is provably past every client's apply.</summary>
        private const double InFlightSeconds = 0.4;

        internal static IEnumerable<string> Check()
        {
            var anchor = typeof(TimeAnchor);
            var expiredM = anchor.GetMethod("CompensationExpired", AllMembers);
            var floorM = anchor.GetMethod("PublishFloorSeconds", AllMembers);
            var monotoneM = anchor.GetMethod("MonotonePublishedSeconds", AllMembers);
            var canonical = anchor.GetMethod("Canonical", AllMembers);
            var hostDtoM = anchor.GetMethod("HostDto", AllMembers);
            var shouldLatchM = anchor.GetMethod("ShouldLatch", AllMembers);
            if (expiredM == null || floorM == null || monotoneM == null || canonical == null ||
                hostDtoM == null || shouldLatchM == null)
            {
                yield return "L490 unresolved: TimeAnchor.CompensationExpired / PublishFloorSeconds / " +
                             "MonotonePublishedSeconds / ShouldLatch / Canonical / HostDto did not all " +
                             "resolve — nothing " +
                             "checks that a paused anchor ever gives back its publish-lag compensation, and " +
                             "every client can sit permanently ahead of a stopped host clock";
                yield break;
            }

            // ── guard: the expiry is CONSULTED BY THE LATCH, and the latch DECISION is the driven predicate
            //    rather than an inline disjunction. Both halves are needed and the second is not ceremony:
            //    an `if` that computes CompensationExpired and then discards it calls the method exactly as
            //    an honest one does, so an IL scan alone cannot tell them apart — this law was written that
            //    way first and its own falsification run passed against a mutation that had removed the term.
            if (!CallsMethod(hostDtoM, expiredM) || !CallsMethod(hostDtoM, shouldLatchM))
            {
                yield return "L490 expiry-never-latched: TimeAnchor.HostDto does not reach both " +
                             "CompensationExpired and ShouldLatch, so a pause that has outlived the flight " +
                             "its compensation paid for never produces a new latch — the stale anchor stays " +
                             "on the wire and every client keeps the +1136 game-second lead the 2026-08-13 " +
                             "session measured";
                yield break;
            }

            // ── arm 0: the expiry is a LATCH TERM. Everything below prices a publication that only happens
            //    if a latch happens at all; this is the arm a dropped `|| expired` turns red.
            if (!TimeAnchor.ShouldLatch(true, false, false, false, true))
                yield return "L490 expiry-not-a-latch-term: TimeAnchor.ShouldLatch answers NO to a settled " +
                             "pause — nothing about the clock has changed except that the compensation has " +
                             "expired, and that is exactly the moment a new anchor has to go on the wire. " +
                             "CompensationExpired is then computed and thrown away";
            if (TimeAnchor.ShouldLatch(true, false, false, false, false))
                yield return "L490 latches-on-nothing: TimeAnchor.ShouldLatch answers YES with no change, no " +
                             "drift and no expiry — the anchor is a heartbeat rather than an event, which is " +
                             "the churn TimeAnchor.ChurnThreshold exists to confess (law 6)";

            // ── guard: the decision reaches the PUBLICATION, through the ratchet it is meant to release.
            if (canonical.GetParameters().Length < 2 || !CallsMethod(canonical, floorM) ||
                !CallsMethod(canonical, monotoneM))
            {
                yield return "L490 premise-changed: TimeAnchor.Canonical no longer takes the expiry decision " +
                             "and publishes through PublishFloorSeconds + MonotonePublishedSeconds, so the " +
                             "monotone ratchet this law releases is no longer the thing that holds the " +
                             "compensation — find where the publication is decided now and move the subject " +
                             "there before trusting this green (L429 arm B owns the compensation itself)";
                yield break;
            }

            // ── arm A: a pause that has outlived the flight SETTLES, and settling really does hand the host's
            //    own clock back to the clients (the published value drops onto hostNow, not merely lower).
            const double hostNow = 64566043930.800;
            const double stale = hostNow + 1136.426;          // the measured anchor, verbatim
            bool settles = TimeAnchor.CompensationExpired(true, true, true, stale, hostNow, 59.9);
            double settled = TimeAnchor.MonotonePublishedSeconds(
                hostNow, TimeAnchor.PublishFloorSeconds(true, settles, stale, hostNow));
            if (!settles || settled != hostNow)
                yield return "L490 ratchet-never-releases: a host that has been PAUSED for 59.9 s still " +
                             "publishes an anchor 1136 game-seconds above its own clock. The compensation is " +
                             "priced for a flight that ended 59.5 s ago, the host will never reach that value, " +
                             "and neither Drifted (zero error on a stopped clock) nor EnforceDrift (compares " +
                             "the client against this same anchor) can see it — the client's every game-time " +
                             "interval test answers against a clock ~19 game-minutes early";

            // ── arm B: it must NOT settle while the flight could still be in progress. This is L429's
            //    contract and the 2026-08-12 sawtooth: a rewind delivered to a client whose clock is still
            //    running at the pre-pause rate is exactly the bug the compensation exists to prevent.
            bool midFlight = TimeAnchor.CompensationExpired(true, true, true, stale, hostNow, InFlightSeconds);
            double held = TimeAnchor.MonotonePublishedSeconds(
                hostNow, TimeAnchor.PublishFloorSeconds(true, midFlight, stale, hostNow));
            if (midFlight || held != stale)
                yield return "L490 settles-mid-flight: the anchor gives its compensation back within " +
                             InFlightSeconds.ToString("F1") + " s of the latch, which is inside the publish " +
                             "lag itself (TimeAnchor.MaxPublishLagSeconds) — the client's clock has not been " +
                             "told about the pause yet, is still running at the pre-pause rate, and this " +
                             "rewinds it by rate x lag with Rebase dragging every timed updateable backward: " +
                             "the ~636 game-second sawtooth of the 2026-08-12 session, which L429 exists to " +
                             "forbid";

            // ── arm C: never while the host is RUNNING. A running host is going to reach the published value;
            //    that prediction is the whole point of the compensation and releasing it there would make
            //    every client trail the host permanently instead.
            if (TimeAnchor.CompensationExpired(true, false, false, stale, hostNow, 600.0))
                yield return "L490 settles-while-running: the compensation is released against a host whose " +
                             "clock is still advancing, so the anchor stops predicting where the host WILL be " +
                             "at publish and each client is left a full publish lag behind instead";

            // ── arm D: the anchor a PAUSE LATCH has just taken still says 'running' (Timing's Paused setter
            //    never touches _scale, L429) — that latch is the one carrying the compensation and must keep
            //    it. Releasing there would zero the compensation at its only source.
            if (TimeAnchor.CompensationExpired(true, true, false, stale, hostNow, 600.0))
                yield return "L490 releases-at-the-pause-latch: the expiry fires against a PRIOR anchor that " +
                             "was still running, i.e. at the very latch that prices the compensation — the " +
                             "compensation would never exist to expire and L429's fix is undone";

            // ── arm E: idempotent. Once the anchor is AT (or below) the host clock there is nothing left to
            //    give back, and answering true would re-latch on every walk cycle forever — the churn alarm's
            //    exact pathology (TimeAnchor.ChurnThreshold).
            if (TimeAnchor.CompensationExpired(true, true, true, hostNow, hostNow, 600.0) ||
                TimeAnchor.CompensationExpired(true, true, true, hostNow - 5.0, hostNow, 600.0))
                yield return "L490 settled-anchor-re-settles: the expiry still answers true once the anchor " +
                             "has already come down to the host clock, so every walk cycle re-latches and " +
                             "every client is jerked to a fresh anchor forever";

            // ── arm F: a FIRST latch has no prior publication and therefore no compensation to release; the
            //    floor must be the candidate either way rather than a stale number from before a reset.
            if (TimeAnchor.CompensationExpired(false, true, true, stale, hostNow, 600.0))
                yield return "L490 expires-without-a-prior: the expiry fires with no previous anchor at all " +
                             "(first latch, or the one after TimeAnchor.Reset), where the value it claims to " +
                             "release does not exist";
            if (TimeAnchor.PublishFloorSeconds(false, false, stale, hostNow) != hostNow)
                yield return "L490 first-latch-floored-by-a-stale-anchor: PublishFloorSeconds holds a previous " +
                             "publication that a first latch (or a post-Reset one) does not have, seeding every " +
                             "client's clock from a number the host has already left behind";
        }

        /// <summary>Same naive linear token scan L429/L190/L100 use — a byte inside an operand could in
        /// principle be read as an opcode, which is sound enough here because a false positive would have to
        /// resolve to this exact method token.</summary>
        private static bool CallsMethod(MethodBase caller, MethodBase target)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
