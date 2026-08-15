using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L429 — THE ANCHOR PUBLISHES HOST TRUTH, AND IT CAN ALWAYS BE UNDONE.
    ///
    /// THE ORIGINAL REPORT (3-instance session 2026-08-12, one machine, so nothing network): every host PAUSE
    /// rewound each client's geoscape clock by up to ~636 game-seconds — a SAWTOOTH, since the resume yanked
    /// it forward again — and <c>TimeAnchor.Rebase</c> rescheduled every timed updateable backward with it
    /// (research and manufacture ETAs going with the jump). ~3600 game-s per real-s is the amplifier that
    /// turns 176 ms of publish lag into 636 game-seconds. The law was first written against ONE expression,
    /// <c>publishLag = t.EffectiveScale * lag</c>, and demanded the PRE-latch rate instead.
    ///
    /// THAT WAS THE WRONG SUBJECT, and this rewrite says so (2026-08-15). Ten fixes tuned that compensation
    /// term and every one of them failed, because THE COMPENSATION WAS THE BIAS: <c>Canonical</c> published
    /// <c>hostNow + rate x lag</c>, pricing a whole publish cycle (the 0.4 s cap) against a real flight of
    /// ~20 ms at 3600x amplification. The live evidence is arithmetic, not judgement — a constant client lead
    /// of <c>dGame=1440.000</c>, exactly <c>3600.00 x 0.4</c>, held 102 s across repeated re-latches, with the
    /// 714.551 re-anchor the same product against an uncapped ~0.1985 s cycle. And it NEVER CONVERGED,
    /// because the published value was floored at the previous one (<c>Math.Max(candidate,
    /// previousPublished)</c>): a one-way ratchet cannot move a client backward, so the largest lead ever
    /// published was permanent. The same log carries the sawtooth this law was written to forbid, alive
    /// again in the ratchet's own arm: <c>anchor apply moved this peer's level clock by 1101.6 s</c> then
    /// <c>-573.9 s</c>, 0.3 s apart.
    ///
    /// WHAT THE LAW ASSERTS NOW is the same intent — no sawtooth, no correction the anchor must later undo —
    /// stated where it is enforceable, and it is STRONGER than the old wording rather than weaker, because
    /// the old one policed WHICH RATE a host-side estimate used while permitting the estimate itself, i.e. it
    /// was green throughout the 1440-second lead and green throughout the 1101.6/-573.9 sawtooth.
    ///   ARM A — the host mints NO lead: <c>Canonical</c> does not price a flight it cannot observe, and does
    ///     not read <c>_hostDto</c> at all, which is the ratchet's only possible input.
    ///   ARM B — there is no ratchet left anywhere: <c>MonotonePublishedSeconds</c> is gone, and the
    ///     compensation is a PURE function of its arguments, so a later anchor that reads LOWER lands lower.
    ///     Executed, not inspected: a smaller anchor must produce a smaller result.
    ///   ARM C — the receiver's compensation is itself PRICED, never a constant and never free: zero measured
    ///     delay costs zero, and the correction is linear in rate and in delay (L100 arms C-E own the rest of
    ///     that seam — this arm exists so a re-introduced constant cannot pass here while L100 is edited).
    ///   ARM D (guard) — the seam still exists and still moves the clock: <c>HostDto</c> latches through
    ///     <c>Canonical</c>, the fresh-reply path re-latches through it, <c>ApplyIfTouched</c> still calls
    ///     <c>Rebase</c> (L190's subject) and <c>Drifted</c> still reads the uncompensated host truth.
    ///
    /// NON-VACUITY: every subject must resolve or the law says so and asserts nothing else. Falsify (verified
    /// 2026-08-15): put <c>MonotonePublishedSeconds</c>/<c>_hostDto</c> back into <c>Canonical</c> →
    /// <c>lead-minted-at-source</c> RED, restored GREEN; add a <c>Math.Max</c> against a previous value in
    /// <c>CompensatedAnchorSeconds</c> → <c>ratchet-back</c>; make the compensation a constant →
    /// <c>compensation-unpriced</c>; make <c>HostDto</c> latch by some other route than <c>Canonical</c> →
    /// <c>premise-changed</c>.
    /// </summary>
    internal static class L429_TheAnchorPublishesHostTruth
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var anchor = typeof(TimeAnchor);
            var canonical = anchor.GetMethod("Canonical", AllMembers);
            var hostDtoM = anchor.GetMethod("HostDto", AllMembers);
            var hostDtoF = anchor.GetField("_hostDto", AllMembers);
            var hostTruthF = anchor.GetField("_hostTruthAtLatch", AllMembers);
            var drifted = anchor.GetMethod("Drifted", AllMembers);
            var error = anchor.GetMethod("HostPredictionError", AllMembers);
            var compensate = anchor.GetMethod("CompensatedAnchorSeconds", AllMembers);
            var apply = anchor.GetMethod("ApplyIfTouched", AllMembers);
            var rebase = anchor.GetMethod("Rebase", AllMembers);
            var refresh = anchor.GetMethod("RefreshForAuthoritativeReply", AllMembers);
            if (canonical == null || hostDtoM == null || hostDtoF == null || hostTruthF == null ||
                drifted == null || error == null || compensate == null || apply == null || rebase == null ||
                refresh == null)
            {
                yield return "L429 unresolved: TimeAnchor.Canonical / HostDto / _hostDto / _hostTruthAtLatch / " +
                             "Drifted / HostPredictionError / CompensatedAnchorSeconds / ApplyIfTouched / " +
                             "Rebase / RefreshForAuthoritativeReply did not all resolve — nothing checks where " +
                             "the clock's flight compensation is priced or whether it can be undone";
                yield break;
            }
            Func<double, double, double, double> comp =
                (a, r, d) => (double)compensate.Invoke(null, new object[] { a, r, d });

            // ── arm D (guard) first: the seam has to still be the seam, or every arm below guards a road
            // nobody drives.
            if (!CallsMethod(hostDtoM, canonical) || !CallsMethod(refresh, canonical))
            {
                yield return "L429 premise-changed: TimeAnchor.HostDto or RefreshForAuthoritativeReply no longer " +
                             "latches through Canonical, so Canonical is not the site that authors the published " +
                             "anchor and arm A is guarding an empty road — find where the anchor is minted now " +
                             "and move this law's subject there before trusting its green";
                yield break;
            }
            if (!CallsMethod(apply, rebase) || !CallsMethod(apply, compensate))
                yield return "L429 premise-changed: ApplyIfTouched no longer calls Rebase (L190's subject) or no " +
                             "longer prices through CompensatedAnchorSeconds — the receiver is then not where " +
                             "the flight is paid, and arms B/C are checking a function with no caller";
            if (!ReadsField(drifted, hostTruthF))
                yield return "L429 compensated-anchor-self-churns: Drifted compares host Now against the published " +
                             "DTO StartTime instead of the uncompensated host truth; a pause then looks like " +
                             "drift and emits the backward half of the globe sawtooth.";

            // ── arm A: the HOST mints no lead. It cannot observe the flight, and its estimate of it was the
            // constant 1440.000 game-second client lead.
            if (ReadsField(canonical, hostDtoF))
                yield return "L429 lead-minted-at-source: TimeAnchor.Canonical reads _hostDto, the PREVIOUS " +
                             "published anchor — the only input a ratchet or a carried-forward lead can have. " +
                             "The anchor must publish the host's RAW Now: pricing a publish cycle (0.4 s cap) " +
                             "against a ~20 ms flight at ~3600 game-s per real-s minted dGame=1440.000 " +
                             "(= 3600.00 x 0.4) and the Math.Max floor made the largest lead ever published " +
                             "permanent — 102 s across repeated re-latches, live 2026-08-15";
            if (anchor.GetMethod("MonotonePublishedSeconds", AllMembers) != null ||
                anchor.GetMethod("PredictPublishedSeconds", AllMembers) != null)
                yield return "L429 lead-minted-at-source: TimeAnchor.MonotonePublishedSeconds / " +
                             "PredictPublishedSeconds is back. Those ARE the lead and the ratchet: the first " +
                             "publishes hostNow + rate x lag, the second floors it at the previous publication " +
                             "so no client can ever be moved back down again";

            // ── arm B: nothing ratchets. A lower anchor lands lower, and the compensation has no memory.
            // Ascending then descending through the SAME function, in the order a live session produces them:
            // a pause anchor, a settle anchor that reads lower, and the pause anchor re-stated.
            double a1 = comp(1000.0, 3600.0, 0.01), a2 = comp(900.0, 3600.0, 0.01), a3 = comp(1000.0, 3600.0, 0.01);
            if (!(a2 < a1) || a2 != 936.0 || a3 != a1)
                yield return "L429 ratchet-back: CompensatedAnchorSeconds does not answer a LOWER anchor with a " +
                             "lower clock, or carries state between calls. A one-way " +
                             "floor cannot correct a client downward, so the largest error ever published " +
                             "becomes permanent — and the settle latch that follows turns it into the " +
                             "+1101.6 s / -573.9 s sawtooth this law's first version was written against";

            // ── arm C: the receiver's correction is priced, never constant and never free.
            if (comp(1000.0, 3600.0, 0.0) != 1000.0 ||
                comp(1000.0, 3600.0, 0.01) != 1036.0 ||
                comp(1000.0, 1800.0, 0.01) != 1018.0 ||
                comp(1000.0, 3600.0, 0.02) != 1072.0)
                yield return "L429 compensation-unpriced: CompensatedAnchorSeconds is not rate x measured delay — " +
                             "a zero delay must cost exactly zero, and halving the rate or the delay must halve " +
                             "the correction. An unpriced compensation is how a constant becomes a permanent " +
                             "clock offset that every log reads as healthy";

            // ── the host's own drift prediction still prices a rate against elapsed real time.
            if (!CallsMethod(drifted, error) ||
                TimeAnchor.HostPredictionError(1000.0, 1000.0, 0.0, 10.0) != 0.0 ||
                TimeAnchor.HostPredictionError(1360.0, 1000.0, 3600.0, 0.1) != 0.0)
                yield return "L429 pure-model-decorative: Drifted does not call HostPredictionError, or the model " +
                             "does not answer 0 for a paused clock that did not move and for a running clock " +
                             "exactly where its rate says it should be — numeric regressions would then be " +
                             "testing a second implementation of a thing production does not use";
        }

        /// <summary>Does this method's IL load the given field, static or instance? Same naive linear token
        /// scan L190 and L100 use — a byte inside an operand could in principle be mistaken for an opcode,
        /// which is sound enough here because a false positive would have to resolve to this exact field
        /// token.</summary>
        private static bool ReadsField(MethodBase m, FieldInfo target)
        {
            foreach (var tok in TokensAfter(m, 0x7B, 0x7C, 0x7E, 0x7F))   // ldfld/ldflda/ldsfld/ldsflda
            {
                FieldInfo f = null;
                try { f = m.Module.ResolveField(tok); } catch { }
                if (f != null && f.MetadataToken == target.MetadataToken && f.Module == target.Module) return true;
            }
            return false;
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
