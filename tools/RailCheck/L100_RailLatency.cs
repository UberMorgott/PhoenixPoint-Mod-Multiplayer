using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L100 — THE RAIL MAY NOT IDLE, AND THE CLOCK MAY NOT ARRIVE STALE.
    ///
    /// THE REPORT (3 instances on one machine over 127.0.0.1, DLL 842240 B, 2026-08-04 23:22): "a constant
    /// lag of about half a second in everything. The aircraft does not start flying the moment I click on a
    /// client. The site's exploration spinner has not finished filling when the host already completes the
    /// exploration." Loopback, so none of it was the network — and the peer logs say so numerically:
    /// client→host intent 22 ms (23:28:48.692 send → 23:28:48.714 host apply), a host→all order-channel
    /// broadcast 16-24 ms (23:28:49.899 host → 23:28:49.915 / .923 clients). What cost half a second was
    /// OURS, in two different places that had to be measured apart before either could be fixed.
    ///
    /// HALF 1 — THE IDLE PERIOD. A cycle is time-sliced at <c>SliceBudgetMs</c>, so the real 625-root walk
    /// takes ~0.25 s of wall clock; <c>TickInterval</c> then held the rail idle for another ~0.25 s before
    /// the next one could start. A gesture escapes that (IntentRail's dispatch <c>FlushNow</c> — measured
    /// end-to-end at 141 ms), but NOTHING the sim produces does: an exploration finishing, resources
    /// ticking, a mission spawning, an aircraft arriving. Those waited a whole idle period PLUS a whole
    /// walk — 0.25-0.75 s, mean ~0.5 s, which is the reported number exactly. The idle bought nothing: the
    /// per-frame cost governor is <c>SliceBudgetMs</c> and always was, so the walk spends the same ~3 ms of
    /// a frame whether cycles run back-to-back or half the time. Arm A pins the ceiling; arm B pins the
    /// floor, because the floor is NOT redundant — a geoscape small enough to walk inside one slice would
    /// otherwise run a full walk and a 22k-entry diff EVERY frame at 60 Hz.
    ///
    /// HALF 2 — THE STALE ANCHOR, and it is the half that hides. <c>TimeAnchor</c> latches the host clock at
    /// <c>DiffEngine.BeginCycle</c> (IdentityResolver.Roots asks <c>HostDto</c>), ships it a whole walk
    /// later, and the client anchors it ON RECEIPT — so an uncompensated client's level clock lands on a
    /// host reading one flight old and STAYS there, because latches are events (pause / speed / drift) and
    /// not a heartbeat. Measured 2026-08-04: a host apply reached the clients' appliers 112 ms later
    /// (23:28:48.714 → 23:28:48.826), i.e. ~5x the network leg. Every derived progress bar then renders
    /// behind, which is the spinner: site exploration is CLOSED-FORM on the clock
    /// (<c>GeoActorProgressionVisualController.Progression</c>, re-derived every frame from a mirrored
    /// start), while the "site explored" window rides the fast order channel — so the window beats the
    /// client's own fill by the whole bias. And NOTHING could see it: <c>EnforceDrift</c> compares the
    /// client against its own anchor derivation, never against the host, so all three logs of that session
    /// contain not one TimeAnchor line while the bias was present throughout. A silent swallow with no line
    /// to grep, which is this repo's dominant bug class and the reason it gets a law rather than a comment.
    ///
    /// WHERE THE COMPENSATION LIVES CHANGED, AND THE ARMS MOVED WITH IT (2026-08-15). Arms C-D used to say
    /// "<c>TimeAnchor.Canonical</c> prices the host's own publish lag (<c>DiffEngine.LastCycleSeconds</c>) at
    /// a rate". That correction was HOST-AUTHORED, and the host cannot observe the flight: pricing a whole
    /// publish cycle (the 0.4 s cap) against a real flight of ~20 ms at ~3600 game-s/real-s minted a
    /// CONSTANT client lead of <c>3600.00 x 0.4 = 1440.000</c> game seconds, held 102 s across repeated
    /// re-latches in a live session — the same client-vs-host clock error this law exists to forbid, with
    /// the sign flipped. So the anchor now publishes RAW host <c>Now</c> and the receiver pays for its own
    /// flight: <c>ApplyIfTouched</c> prices the delay IT measures (network drain → end of the batch that
    /// carried the anchor, <c>TimeAnchor.NoteBatchReceived</c>) at the rate ITS clock ran at
    /// (<c>ClientRate</c>). The arms are NOT weaker for moving — they now EXECUTE the compensation instead
    /// of only witnessing an IL call: arm C demands the receiver read a MEASURED delay and that a delay of
    /// zero cost exactly zero correction, arm D demands the correction scale with both the rate and the
    /// delay (a constant is wrong at every geoscape speed but one), and arm E demands the clamp bind. The
    /// old arm E ("the lag is measured, not remembered") is retired with its subject: <c>LastCycleSeconds</c>
    /// had no consumer left and is deleted rather than left as a number a law pretends to guard.
    ///
    /// Arm F is the one that keeps the cure from becoming the disease. The compensation biases the host's
    /// own drift prediction by <c>rate x lag</c>, and BOTH drift checks fire above <c>max(5 s, rate x 0.5)</c>
    /// — so a lag ceiling at or past 0.5 lets the anchor re-latch against its own correction forever, on
    /// every cycle, while every log stays healthy (precisely what L73's churn alarm exists to confess, and
    /// it would be confessing OUR doing). Arm G keeps F honest: if either drift check disappears, F is
    /// asserting about a threshold nothing enforces.
    ///
    /// NON-VACUITY: every subject must resolve or the law says so and asserts nothing else. Falsify:
    /// put <c>TickInterval</c> back to 0.5 f → <c>cadence-idle</c>; set it to 0 → <c>cadence-unfloored</c>;
    /// make <c>ApplyIfTouched</c> rebase on the raw anchor instead of <c>CompensatedAnchorSeconds</c> →
    /// <c>anchor-unpriced</c> (verified RED 2026-08-15, restored GREEN); stop stamping the receipt in
    /// <c>ApplyDelta</c> → <c>anchor-unpriced</c>; drop the rate factor from
    /// <c>CompensatedAnchorSeconds</c> → <c>anchor-unrated</c>; drop its clamp → <c>clamp-gone</c>; raise
    /// <c>MaxPublishLagSeconds</c> to 0.5 → <c>compensation-outbids-drift</c>; delete <c>Drifted</c> or
    /// <c>EnforceDrift</c> → <c>premise-drift-check-gone</c>; stop calling <c>Rebase</c> from
    /// <c>ApplyIfTouched</c> → <c>premise-changed</c>.
    /// </summary>
    internal static class L100_RailLatency
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>The idle ceiling, in seconds, and it is a MEASURED bound rather than a preference: the
        /// mean latency of a non-gesture change is one idle period plus one walk, the walk is ~0.25 s at
        /// SliceBudgetMs, and the 2026-08-04 report puts the tolerable total well under the ~0.5 s that
        /// TickInterval=0.5 produced. Anything at or above this re-introduces an idle period comparable to
        /// the walk itself, i.e. the reported bug.</summary>
        private const double CadenceCeilingSeconds = 0.2;

        /// <summary>The rate coefficient of the drift threshold both TimeAnchor.Drifted and
        /// TimeAnchor.EnforceDrift use — <c>max(5.0, rate * 0.5)</c>. The publish-lag ceiling has to stay
        /// strictly below it; see arm F.</summary>
        private const double DriftRateCoefficient = 0.5;

        internal static IEnumerable<string> Check()
        {
            var tickF = typeof(DiffEngine).GetField("TickInterval", AllMembers);
            var apply = typeof(TimeAnchor).GetMethod("ApplyIfTouched", AllMembers);
            var compensate = typeof(TimeAnchor).GetMethod("CompensatedAnchorSeconds", AllMembers);
            var noteRecv = typeof(TimeAnchor).GetMethod("NoteBatchReceived", AllMembers);
            var clientRate = typeof(TimeAnchor).GetMethod("ClientRate", AllMembers);
            var applyDelta = typeof(GenericApplier).GetMethod("ApplyDelta", AllMembers);
            var maxLagF = typeof(TimeAnchor).GetField("MaxPublishLagSeconds", AllMembers);
            if (tickF == null || apply == null || compensate == null || noteRecv == null || clientRate == null ||
                applyDelta == null || maxLagF == null)
            {
                yield return "L100 unresolved: DiffEngine.TickInterval / TimeAnchor.ApplyIfTouched / " +
                             "CompensatedAnchorSeconds / NoteBatchReceived / ClientRate / " +
                             "GenericApplier.ApplyDelta / MaxPublishLagSeconds did not all resolve — neither " +
                             "the rail's cadence nor the clock's flight compensation is checked by anything";
                yield break;
            }
            double maxLag = Convert.ToDouble(maxLagF.GetRawConstantValue());
            Func<double, double, double, double> comp =
                (a, r, d) => (double)compensate.Invoke(null, new object[] { a, r, d });

            // ── arm A: the rail may not park between cycles.
            double tick = Convert.ToDouble(tickF.GetRawConstantValue());
            if (tick > CadenceCeilingSeconds)
                yield return "L100 cadence-idle: DiffEngine.TickInterval is " + tick + " s, above the " +
                             CadenceCeilingSeconds + " s ceiling. A sliced 625-root cycle already costs ~0.25 s of " +
                             "wall clock, so this parks the rail for a comparable stretch doing nothing and every " +
                             "change the SIM produces — no gesture flushes those — waits an idle period PLUS a walk. " +
                             "That is the measured 0.25-0.75 s (mean ~0.5 s) of the 2026-08-04 report, and the idle " +
                             "buys no frame time: SliceBudgetMs is the cost governor, not this";

            // ── arm B: ...and it may not walk unconditionally either.
            if (!(tick > 0))
                yield return "L100 cadence-unfloored: DiffEngine.TickInterval is " + tick + " — with no floor a " +
                             "geoscape small enough to walk inside a single slice runs a FULL walk, a 22k-entry " +
                             "diff and a snapshot swap on every frame at 60 Hz. The floor is what makes the " +
                             "back-to-back cadence safe on a degenerate graph, so removing it is not the same " +
                             "change as lowering it";

            // ── the guard the arms below are worth nothing without: the correction has to reach the clock.
            var rebase = typeof(TimeAnchor).GetMethod("Rebase", AllMembers);
            if (rebase == null || !CallsMethod(apply, rebase))
            {
                yield return "L100 premise-changed: TimeAnchor.ApplyIfTouched no longer moves the level clock " +
                             "through Rebase, so whatever it computes lands nowhere and arms C-E would pass over " +
                             "a correction with no consumer. Find where the anchor is applied now and move this " +
                             "law's subject there before trusting its green (L190 owns the same call for its own " +
                             "reason — the jump has to be visible on the peer it jerks)";
                yield break;
            }

            // ── arm C: the RECEIVER prices its own flight, off a delay it MEASURED.
            if (!CallsMethod(apply, compensate) || !CallsMethod(applyDelta, noteRecv))
                yield return "L100 anchor-unpriced: TimeAnchor.ApplyIfTouched does not rebase through " +
                             "CompensatedAnchorSeconds, or GenericApplier.ApplyDelta never stamps the receipt " +
                             "(NoteBatchReceived) it is priced from. The latch happens at BeginCycle, the delta " +
                             "ships a whole walk later (measured 112 ms, 2026-08-04 23:28:48.714 host apply → " +
                             ".826 client apply) and the client anchors on RECEIPT — uncorrected, every client's " +
                             "clock sits one flight in the past until the next rate change and every derived " +
                             "progress bar renders behind. EnforceDrift cannot see it: it checks the client " +
                             "against its own anchor derivation, not against the host. The HOST must not pay it " +
                             "instead — it cannot observe the flight, and its estimate of it was the constant " +
                             "1440.000 game-second lead this arm was moved to prevent";
            if (comp(1000.0, 3600.0, 0.0) != 1000.0)
                yield return "L100 anchor-unpriced: CompensatedAnchorSeconds moves the clock on a ZERO measured " +
                             "delay, i.e. it mints a correction rather than pricing one. A peer that applied the " +
                             "anchor the instant it arrived owes exactly nothing";

            // ── arm D: ...at a RATE, and proportionally to the measured delay.
            if (!CallsMethod(apply, clientRate))
                yield return "L100 anchor-unrated: ApplyIfTouched does not read ClientRate, so the correction is " +
                             "not priced at the rate THIS clock ran at while it waited. The delay is real time, " +
                             "the correction is game time, and the only thing relating them is a rate — priced " +
                             "at a constant it is wrong at every geoscape speed but one";
            if (comp(1000.0, 3600.0, 0.01) != 1036.0 || comp(1000.0, 1800.0, 0.01) != 1018.0 ||
                comp(1000.0, 3600.0, 0.02) != 1072.0)
                yield return "L100 anchor-unrated: CompensatedAnchorSeconds is not rate x delay — doubling the " +
                             "rate or the delay did not double the correction. Executed rather than inferred from " +
                             "an IL call, because an IL call proves only that a number was read";

            // ── arm E: the clamp binds, so a hitch cannot become a clock jump.
            if (comp(1000.0, 3600.0, 10.0) != 1000.0 + 3600.0 * maxLag || comp(1000.0, 3600.0, -5.0) != 1000.0)
                yield return "L100 clamp-gone: CompensatedAnchorSeconds does not clamp the measured delay to " +
                             "MaxPublishLagSeconds (and to 0 below). A GC hitch or a stalled drain would then " +
                             "ride into the level clock at ~3600 game-s per real-s, and arm F's whole margin " +
                             "argument is about a bound that no longer exists";

            // ── arm F: the correction may not outbid the drift threshold it biases.
            if (!(maxLag < DriftRateCoefficient))
                yield return "L100 compensation-outbids-drift: TimeAnchor.MaxPublishLagSeconds is " + maxLag +
                             ", not below the drift threshold's rate coefficient " + DriftRateCoefficient +
                             ". The compensation biases the host's own prediction by rate x lag, so at or past the " +
                             "coefficient Drifted() answers true against the correction itself and the anchor " +
                             "re-latches every cycle forever — the pathology L73's churn alarm exists to confess, " +
                             "authored by the fix meant to help";

            // ── arm G: both halves of that threshold still exist.
            foreach (var name in new[] { "Drifted", "EnforceDrift" })
                if (typeof(TimeAnchor).GetMethod(name, AllMembers) == null)
                    yield return "L100 premise-drift-check-gone: TimeAnchor." + name + " no longer exists, so the " +
                                 "max(5 s, rate x " + DriftRateCoefficient + ") threshold arm F holds " +
                                 "MaxPublishLagSeconds under is not enforced by anything and arm F is decoration";
        }

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
