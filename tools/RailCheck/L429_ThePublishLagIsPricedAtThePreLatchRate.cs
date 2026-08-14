using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L429 — A COMPENSATION IS PRICED AT THE RATE THAT WAS RUNNING, NOT AT THE ONE THAT JUST REPLACED IT.
    ///
    /// THE REPORT (3-instance session 2026-08-12, all three on ONE machine, so nothing network): every host
    /// PAUSE rewound each client's geoscape clock by up to ~636 game-seconds — ten game-minutes, a SAWTOOTH
    /// rather than a drift, since the following resume yanked it forward again — and <c>TimeAnchor.Rebase</c>
    /// rescheduled every timed updateable backward with it (research and manufacture ETAs going with the
    /// jump). Measured latch→apply: 176.7/174.7/123.4/115.4/100.6/88.6/79.3/75.3/67.6/59.6/34.2/26.2 ms,
    /// with the two clients within 2-12 ms of EACH OTHER — so the cost is the host's own walk, and the ~3600
    /// game-s per real-s of geoscape speed is the amplifier that turns 176 ms into 636 game-seconds.
    ///
    /// THE DEFECT was one expression: <c>publishLag = t.EffectiveScale * lag</c>. The LAG was measured
    /// correctly (<c>DiffEngine.LastCycleSeconds</c>, L100 arm C) — the RATE it was priced at was the
    /// post-latch one. On a pause latch <c>EffectiveScale</c> is already 0, so the compensation came out
    /// zero, under a comment reasoning that "a paused clock does not advance in flight". True of the HOST's
    /// clock. It is the CLIENT's clock that is in flight, it has not been told about the pause yet, and M2
    /// deliberately does not freeze it (<c>ClientSimGate</c>) — so it keeps running at the PRE-pause rate,
    /// arrives at <c>Now + rate x lag</c>, and the uncompensated anchor drags it back by exactly that.
    ///
    /// THE FIX is <c>_hostDto.Scale</c>: the own scale of the LAST PUBLISHED anchor, read before
    /// <c>HostDto</c> overwrites it. Own scale and not <c>EffectiveScale</c> on purpose — Timing's Paused
    /// setter never touches <c>_scale</c> (decompile Base.Core/Timing.cs:99-129), so that field still holds
    /// the RUNNING rate all the way through a pause. That is what makes the RESUME latch keep pricing the
    /// same <c>rate x lag</c> it always priced (the half that already worked, and must not double-compensate
    /// or regress), and what makes a re-latch WHILE paused — a drift latch, a speed click — re-state the
    /// same anchor instead of fighting the offset the pause left behind.
    ///
    /// WHY A LAW AND NOT A COMMENT. Nothing observable distinguishes the two expressions until three
    /// instances are running at geoscape speed: both compile, both are dimensionally sound, both leave every
    /// log healthy, and the peer that suffers is not the peer that computes. The mis-priced form even READS
    /// more careful than the correct one. This is the second bug in this one expression (the first was
    /// pricing nothing at all, L100 arm C) and both were invisible for exactly the same reason.
    ///
    /// ARM A — the pre-latch rate is read. ARMS B/C are the mandatory guard, and they are not ceremony: arm
    /// A is worth nothing if <c>Canonical</c> has stopped being the site that prices the publish lag
    /// (nothing left to mis-price) or if <c>HostDto</c> has stopped calling it (then <c>_hostDto</c> is no
    /// longer the PREVIOUS latch at the moment Canonical reads it, which is the entire basis of arm A).
    ///
    /// NON-VACUITY: every subject must resolve or the law says so and asserts nothing else. Falsify: put
    /// <c>t.EffectiveScale * lag</c> back in <c>TimeAnchor.Canonical</c> → <c>lag-priced-post-latch</c>;
    /// drop the <c>LastCycleSeconds</c> term → <c>premise-unpriced</c>; make <c>HostDto</c> latch by some
    /// other route than <c>Canonical</c> → <c>premise-changed</c>.
    /// </summary>
    internal static class L429_ThePublishLagIsPricedAtThePreLatchRate
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
            var predict = anchor.GetMethod("PredictPublishedSeconds", AllMembers);
            var error = anchor.GetMethod("HostPredictionError", AllMembers);
            var monotone = anchor.GetMethod("MonotonePublishedSeconds", AllMembers);
            var refresh = anchor.GetMethod("RefreshForAuthoritativeReply", AllMembers);
            var scaleF = typeof(TimingInstanceData).GetField("Scale");
            var pausedF = typeof(TimingInstanceData).GetField("Paused");
            var lagGet = typeof(DiffEngine).GetProperty("LastCycleSeconds", AllMembers)?.GetGetMethod(nonPublic: true);
            if (canonical == null || hostDtoM == null || hostDtoF == null || hostTruthF == null || drifted == null ||
                predict == null || error == null || monotone == null || refresh == null ||
                scaleF == null || pausedF == null || lagGet == null)
            {
                yield return "L429 unresolved: TimeAnchor.Canonical / HostDto / _hostDto, " +
                             "TimingInstanceData.Scale or DiffEngine.LastCycleSeconds did not all resolve — " +
                             "nothing checks the rate the anchor's publish-lag compensation is priced at";
                yield break;
            }

            // ── arm B (guard): there must still BE a publish-lag compensation here to mis-price.
            if (!CallsMethod(canonical, lagGet))
            {
                yield return "L429 premise-unpriced: TimeAnchor.Canonical no longer reads " +
                             "DiffEngine.LastCycleSeconds, so it is not the site that prices the publish lag " +
                             "and arm A is guarding an empty road — find where the compensation is computed " +
                             "now and move this law's subject there before trusting its green (L100 arm C " +
                             "owns the compensation's existence)";
                yield break;
            }

            // ── arm C (guard): _hostDto is only the PRE-latch anchor while HostDto latches THROUGH Canonical.
            if (!CallsMethod(hostDtoM, canonical))
            {
                yield return "L429 premise-changed: TimeAnchor.HostDto no longer calls Canonical, so the " +
                             "_hostDto that Canonical reads is no longer guaranteed to be the PREVIOUS latch " +
                             "at the moment it is read — the ordering is the whole basis of arm A, and " +
                             "without it a green here means nothing";
                yield break;
            }
            if (!ReadsField(drifted, hostTruthF))
                yield return "L429 compensated-anchor-self-churns: Drifted compares host Now against the " +
                             "receiver-priced DTO StartTime instead of the uncompensated host truth; a pause " +
                             "then looks like drift and emits the backward half of the globe sawtooth.";
            if (!CallsMethod(canonical, predict) || !CallsMethod(canonical, monotone) ||
                !CallsMethod(drifted, error) || !CallsMethod(refresh, canonical))
                yield return "L429 pure-model-decorative: production Canonical/Drifted/fresh-reply do not call " +
                             "PredictPublishedSeconds, MonotonePublishedSeconds, HostPredictionError and " +
                             "Canonical respectively; numeric regressions are testing a second implementation.";

            // ── arm A: the lag is priced at the rate from BEFORE this latch.
            if (!ReadsField(canonical, hostDtoF) || !ReadsField(canonical, scaleF) || !ReadsField(canonical, pausedF))
                yield return "L429 lag-priced-post-latch: TimeAnchor.Canonical computes its publish-lag " +
                             "compensation without reading the previous anchor's own Scale (_hostDto.Scale), " +
                             "so it is pricing real seconds of transit at the rate that has ALREADY replaced " +
                             "the one the clock ran at. On a PAUSE latch that rate is 0 and the compensation " +
                             "vanishes — but the client's clock is not paused in flight (ClientSimGate does " +
                             "not freeze it) and keeps running at the pre-pause rate, so the anchor rewinds " +
                             "it by rate x lag and Rebase reschedules every timed updateable backward with " +
                             "it: measured up to ~636 game-s per pause, a sawtooth the resume undoes, " +
                             "3-instance session 2026-08-12. Timing's Paused setter never touches _scale " +
                             "(Base.Core/Timing.cs:99-129), which is why the LAST PUBLISHED DTO's own Scale " +
                             "is the running rate through the whole pause — and why pricing from it leaves " +
                             "the resume latch, the half that already worked, computing exactly what it did";

            // Numeric transition: all clients are block-first, so they share the prior host posture.
            double running = TimeAnchor.PriorEffectiveRate(true, false, 100.0, 1.0, 0.0);
            double pausePublished = TimeAnchor.PredictPublishedSeconds(1000.0, running, 0.1);
            double paused = TimeAnchor.PriorEffectiveRate(true, true, 100.0, 1.0, 0.0);
            double resumeCandidate = TimeAnchor.PredictPublishedSeconds(1000.0, paused, 0.1);
            double resumePublished = TimeAnchor.MonotonePublishedSeconds(resumeCandidate, pausePublished);
            double repeatedPausedError = TimeAnchor.HostPredictionError(1000.0, 1000.0, paused, 10.0);
            if (running != 100.0 || paused != 0.0 || pausePublished != 1010.0 ||
                repeatedPausedError != 0.0 || resumePublished != pausePublished)
                yield return "L429 pause-resume-oscillates: numeric prior-rate model is not running->100, " +
                             "paused->0 with repeated paused ticks producing zero drift and a non-backward " +
                             "resume publication.";

            // First/reset latch uses live fallback; an aged cached running anchor cannot win on no-op resume.
            if (TimeAnchor.PriorEffectiveRate(false, true, 999.0, 999.0, 7.0) != 7.0)
                yield return "L429 first-latch-uses-stale-prior: reset/first latch did not use live fallback.";
            double oldRunningAnchor = 1000.0;
            double freshNoopResume = TimeAnchor.MonotonePublishedSeconds(
                TimeAnchor.PredictPublishedSeconds(5000.0, 100.0, 0.1), oldRunningAnchor);
            double repeatedNoopResume = TimeAnchor.MonotonePublishedSeconds(
                TimeAnchor.PredictPublishedSeconds(5100.0, 100.0, 0.1), freshNoopResume);
            if (freshNoopResume != 5010.0 || repeatedNoopResume != 5110.0 ||
                repeatedNoopResume < freshNoopResume)
                yield return "L429 stale-running-anchor-reemitted: a no-op resume can reuse an aged running " +
                             "anchor instead of freshly pricing host Now, rewinding the requesting client.";
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
