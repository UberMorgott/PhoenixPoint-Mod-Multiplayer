using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Core;
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
    /// later, and the client anchors it ON RECEIPT — so the client's level clock lands on a host reading one
    /// publish lag old and STAYS there, because latches are events (pause / speed / drift) and not a
    /// heartbeat. Measured that session: a host apply reached the clients' appliers 112 ms later
    /// (23:28:48.714 → 23:28:48.826), i.e. ~5x the network leg. Every derived progress bar then renders
    /// behind, which is the spinner: site exploration is CLOSED-FORM on the clock
    /// (<c>GeoActorProgressionVisualController.Progression</c>, re-derived every frame from a mirrored
    /// start), while the "site explored" window rides the fast order channel — so the window beats the
    /// client's own fill by the whole bias. And NOTHING could see it: <c>EnforceDrift</c> compares the
    /// client against its own anchor derivation, never against the host, so all three logs of that session
    /// contain not one TimeAnchor line while the bias was present throughout. A silent swallow with no line
    /// to grep, which is this repo's dominant bug class and the reason it gets a law rather than a comment.
    /// Arms C-E hold the correction in place: the anchor must price the host's OWN measured publish lag
    /// (<c>DiffEngine.LastCycleSeconds</c>), at the LIVE rate (a paused clock does not advance in flight —
    /// compensating one would mint game time out of a pause), and the lag must actually be measured rather
    /// than left at whatever a reset put there.
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
    /// delete the publish-lag term from <c>TimeAnchor.Canonical</c> → <c>anchor-unpriced</c>; price it at a
    /// constant instead of <c>Timing.EffectiveScale</c> → <c>anchor-unrated</c>; stop assigning
    /// <c>LastCycleSeconds</c> in <c>RunSlice</c> → <c>lag-unmeasured</c>; raise
    /// <c>MaxPublishLagSeconds</c> to 0.5 → <c>compensation-outbids-drift</c>; delete <c>Drifted</c> or
    /// <c>EnforceDrift</c> → <c>premise-drift-check-gone</c>.
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
            var lagP = typeof(DiffEngine).GetProperty("LastCycleSeconds", AllMembers);
            var runSlice = typeof(DiffEngine).GetMethod("RunSlice", AllMembers);
            var canonical = typeof(TimeAnchor).GetMethod("Canonical", AllMembers);
            var maxLagF = typeof(TimeAnchor).GetField("MaxPublishLagSeconds", AllMembers);
            var effScale = typeof(Timing).GetProperty("EffectiveScale", BindingFlags.Public | BindingFlags.NonPublic |
                                                                       BindingFlags.Instance);
            if (tickF == null || lagP == null || runSlice == null || canonical == null || maxLagF == null)
            {
                yield return "L100 unresolved: DiffEngine.TickInterval / LastCycleSeconds / RunSlice / " +
                             "TimeAnchor.Canonical / MaxPublishLagSeconds did not all resolve — neither the " +
                             "rail's cadence nor the clock's publish lag is checked by anything";
                yield break;
            }

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

            // ── arm C: the anchor prices its own publish lag.
            var lagGet = lagP.GetGetMethod(nonPublic: true);
            if (lagGet == null || !CallsMethod(canonical, lagGet))
                yield return "L100 anchor-unpriced: TimeAnchor.Canonical does not read DiffEngine.LastCycleSeconds. " +
                             "The latch happens at BeginCycle and the delta ships a whole walk later (measured " +
                             "112 ms, 2026-08-04 23:28:48.714 host apply → 23:28:48.826 client apply), and the " +
                             "client anchors on RECEIPT — so without the correction every client's clock sits one " +
                             "publish lag in the past until the next rate change, and every progress bar derived " +
                             "from it renders behind. EnforceDrift cannot see this: it checks the client against " +
                             "its own anchor derivation, not against the host";

            // ── arm D: ...at the LIVE rate, which is what makes a pause cost nothing.
            var scaleGet = effScale?.GetGetMethod(nonPublic: true);
            if (scaleGet == null)
                yield return "L100 premise-effective-scale-gone: Base.Core.Timing.EffectiveScale did not resolve — " +
                             "the rate the publish-lag correction must be priced at no longer exists, so arm D " +
                             "asserts nothing";
            else if (!CallsMethod(canonical, scaleGet))
                yield return "L100 anchor-unrated: TimeAnchor.Canonical computes its publish-lag correction without " +
                             "reading Timing.EffectiveScale. The correction is game time, the lag is real time, and " +
                             "the only thing relating them is the live rate — priced at anything else it is wrong at " +
                             "every geoscape speed but one, and on a PAUSED clock (rate 0, so the host advanced not " +
                             "at all in flight) it MINTS game time the host never ran";

            // ── arm E: the lag is measured, not remembered.
            var lagSet = lagP.GetSetMethod(nonPublic: true);
            if (lagSet == null || !CallsMethod(runSlice, lagSet))
                yield return "L100 lag-unmeasured: DiffEngine.RunSlice does not assign LastCycleSeconds — the value " +
                             "the anchor prices is then frozen at whatever a reset left behind, so the correction " +
                             "is theatre that still passes arm C";

            // ── arm F: the correction may not outbid the drift threshold it biases.
            double maxLag = Convert.ToDouble(maxLagF.GetRawConstantValue());
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
