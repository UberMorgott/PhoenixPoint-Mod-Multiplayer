using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Entities;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;

namespace RailCheck
{
    /// <summary>
    /// L290 — A CLIENT'S EXPLORATION TIMER CAN ONLY EVER FINISH AFTER THE HOST'S, AND A CLIENT NEVER
    /// STARTS ONE THE HOST HAS NOT ORDERED.
    ///
    /// THE REPORT (3 instances, 2026-08-08). Both clients printed, at 02:50:22.69:
    ///   "exploration NOT re-seeded for V#1@8be7e872… parked at S#79 — the mirrored start 01:06:09.4280000
    ///    + the site's def-fixed ExplorationTime ends at 05:06:09.4280000, which the clock
    ///    (06:26:33.4280000) is already past — a STALE start"
    /// Fifteen seconds later ONE client alone repainted S#79 with an activeMission the host had never
    /// authored, sent a launch intent for it, was rejected ("that site has no runnable mission any more"),
    /// and produced the session's ONLY CRC divergence on that very root.
    ///
    /// THE ROOT IS AN EPOCH, NOT A LATENCY. <c>StartedExplorationAt = base.Timing.Now</c>
    /// (GeoVehicle.cs:423) reads the PER-ACTOR clock. Every <c>ActorComponent</c> gets its own
    /// <c>new Timing()</c> (ActorComponent.cs:85) and NOTHING ever sets an actor's <c>StartTime</c> — the
    /// only <c>Timing.StartTime</c> writer in the game is <c>GeoLevelController</c>:571, the LEVEL clock.
    /// So <c>Now = StartTime + OwnNow</c> (Timing.cs:55) is that actor's AGE, zeroed when its
    /// <c>ParentTime</c> was assigned (Timing.cs:148) and rooted in that PROCESS's <c>Time.time</c>
    /// (Timing.cs:176). The rail mirrors the LEAF (rail-baseline.txt:727/:753 "StartExplorationTime -> live
    /// StartedExplorationAt") and DELIBERATELY EXCLUDES the clock it is measured in (rail-baseline.txt:33,
    /// "per-actor clock … client clocks tick locally, the level clock rides the TimeAnchor 'TA' root").
    /// A mirrored number in an unmirrored epoch: the five hours between 01:06:09 and 06:26:33 are not lag,
    /// they are two different zeros. The old test <c>now &lt; start + ExplorationTime</c> was therefore
    /// never meaningful — it was the SAME bug the level-vs-actor comment in <c>ReseedExploration</c>
    /// already fixed once, one level up, and it fails in BOTH directions: past the skew a client silently
    /// mirrors no exploration at all and would tear down a running one, and short of it a client starts a
    /// timer that can expire BEFORE the host's.
    ///
    /// WHAT IS ASSERTED IS THE OUTCOME — which epoch the client's own timer is anchored in — not the calls:
    ///   (b) with no host edge and nothing running, NO start is manufactured. A vehicle that explored once
    ///       keeps a non-zero mirrored leaf forever (neither EndExploreCurrentSite:460 nor
    ///       SiteExplorationCompleted:474 clears it), so a fallback to the mirrored value is exactly how a
    ///       client would mint a phantom exploration at the next site it parks at.
    ///   (c) on the host's edge the anchor is THIS peer's <c>Now</c>, never anything earlier. That single
    ///       equality is the whole safety property: the client's timer starts after the host's by the wire
    ///       delay and runs the identical def-fixed duration, so it CANNOT complete first.
    ///   (d) an unrelated order delta does not re-anchor a running fill.
    ///   (g) but a LANDED host start OUTRANKS an existing stamp — the precedence (d) hid. The stamp is
    ///       cleared in one place only, while the client's own timer usually expires by itself, so a dead
    ///       stamp routinely outlives the exploration it belonged to; anchoring the NEXT order on it dates
    ///       `end` into the past and no spinner is ever seeded again (2026-08-14: second and later
    ///       explorations by the same aircraft showed no ring on any client).
    ///   (e) a running handle with no stamp (the join case — a mid-exploration aircraft arrives through the
    ///       save transfer, law 1b, never as a delta) is ADOPTED, not torn down.
    ///   (f) the decision is REACHED, and the local epoch is actually RECORDED — or (b)-(e) are correct
    ///       answers nobody ever asks for, which is the state the bug was found in.
    ///   (a) holds the premise: the day the game stops writing that field off a per-actor clock, the whole
    ///       argument changes and this law should be re-read rather than quietly kept alive.
    ///
    /// Falsify: return the mirrored value when no edge landed → <c>phantom-exploration</c>; anchor the edge
    /// on the mirrored value instead of <c>localNow</c> → <c>client-can-finish-first</c>; drop the
    /// <c>haveStamp</c> short-circuit → <c>fill-restarted</c>; consult <c>haveStamp</c> BEFORE
    /// <c>hostStartLanded</c> → <c>stale-stamp-wins</c>; return Zero for a running stamp-less handle →
    /// <c>join-spinner-killed</c>; stop calling <c>ExplorationStartInLocalEpoch</c> from
    /// <c>ReseedExploration</c> → <c>decision-unreached</c>; stop writing <c>_localExplorationStart</c> →
    /// <c>no-local-epoch-recorded</c>; give actors a shared clock → <c>premise-epoch-shared</c>.
    /// </summary>
    internal static class L290_TheClientsExplorationCannotFinishFirst
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>"started at hour 1" is the HOST's number; "it is hour 5 here" is OURS. The gap is the
        /// epoch skew the live logs measured, written small enough to read.</summary>
        private static readonly TimeUnit HostStart = TimeUnit.FromHours(1f);
        private static readonly TimeUnit LocalNow = TimeUnit.FromHours(5f);
        private static readonly TimeUnit Duration = TimeUnit.FromHours(10f);

        internal static IEnumerable<string> Check(Assembly game)
        {
            var applier = typeof(GenericApplier);
            var mod = applier.Assembly;

            var anchor = applier.GetMethod("ExplorationStartInLocalEpoch", All);
            var decide = applier.GetMethod("ShouldBeExploring", All);
            var reseed = applier.GetMethod("ReseedExploration", All);
            var flush = applier.GetMethod("FlushOrderReseed", All);
            var stampMap = applier.GetField("_localExplorationStart", All);
            var edgeSet = applier.GetField("_hostExplorationEdge", All);

            var startedAt = typeof(GeoVehicle).GetField("StartedExplorationAt", All);
            var startExploring = typeof(GeoVehicle).GetMethod("StartExploringCurrentSite", All);
            var actorInit = typeof(ActorComponent).GetMethod("Initialize", All);
            var timingNow = typeof(Timing).GetProperty("Now", All)?.GetGetMethod(true);

            if (anchor == null || decide == null || reseed == null || flush == null || stampMap == null ||
                edgeSet == null || startedAt == null || startExploring == null || actorInit == null ||
                timingNow == null)
            {
                yield return "L290 premise-changed: one of GenericApplier.{ExplorationStartInLocalEpoch," +
                             "ShouldBeExploring,ReseedExploration,FlushOrderReseed,_localExplorationStart," +
                             "_hostExplorationEdge} or GeoVehicle.{StartedExplorationAt,StartExploringCurrentSite} " +
                             "or ActorComponent.Initialize / Timing.Now no longer resolves. The exploration " +
                             "anchoring seam has moved and this law is asserting something about a shape neither " +
                             "the game nor the rail still has — re-read it before trusting the green.";
                yield break;
            }

            // ── (a) premise: the leaf really is written off a PER-ACTOR clock ──────────────────────────
            // If the game ever gave actors a shared epoch (an actor StartTime, or the level's Timing
            // directly), the mirrored number would be comparable and this whole seam becomes removable
            // rather than subtly wrong. Two halves: the writer reads a clock, and the actor still owns one.
            if (!Program.Callees(startExploring, game).Any(c => Same(c, timingNow)))
                yield return "L290 premise-epoch-shared: GeoVehicle.StartExploringCurrentSite no longer reads " +
                             "Timing.Now, so StartedExplorationAt is not a clock reading any more. The anchoring " +
                             "this law protects exists only because that value is per-actor and per-process; if " +
                             "the game now writes something mirrorable there, mirror it instead of stamping.";
            if (!Program.Callees(actorInit, game).Any(c => c.DeclaringType == typeof(Timing) && c.IsConstructor))
                yield return "L290 premise-epoch-shared: ActorComponent.Initialize no longer constructs its own " +
                             "Timing (ActorComponent.cs:85). If actors now share the level's clock — the one the " +
                             "rail DOES mirror via the TimeAnchor 'TA' root — then StartedExplorationAt is " +
                             "directly comparable across peers and the local stamp is dead weight.";

            // ── (b)-(e) THE OUTCOME, executed case by case ────────────────────────────────────────────
            foreach (var v in Arms(Ask(anchor), Ask5(decide), "GenericApplier")) yield return v;

            // ── POSITIVE CONTROL ──────────────────────────────────────────────────────────────────────
            // The pre-fix rule, run through the identical arms: it anchored on whatever the rail carried.
            // If the arms cannot turn it red they are decorative, and this law would pass over the very
            // 2026-08-08 defect it was written for.
            if (!Arms(FakeSeam.AnchorOnTheMirroredValue, Ask5(decide), "FakeSeam").Any())
                yield return "L290 positive-control-dead: the arms above do not flag FakeSeam, which anchors the " +
                             "client's timer on the MIRRORED StartExplorationTime exactly as the code did before " +
                             "2026-08-08. An arm set that cannot fail the known-broken rule proves nothing about " +
                             "the fixed one.";

            // ── (f) the decision is REACHED and the epoch is RECORDED ─────────────────────────────────
            if (!Program.Callees(reseed, mod).Any(c => Same(c, anchor)))
                yield return "L290 decision-unreached: GenericApplier.ReseedExploration does not call " +
                             "ExplorationStartInLocalEpoch, so the anchor the arms above verify is never the one " +
                             "the applier uses. That is the exact failure shape this repo keeps paying for — a " +
                             "correct decider nobody consults.";
            if (!Program.Callees(reseed, mod).Any(c => Same(c, decide)))
                yield return "L290 decision-unreached: ReseedExploration no longer asks ShouldBeExploring, so the " +
                             "anchored start decides nothing and the re-seed answers off some other test.";
            if (!Program.ReadsField(reseed, stampMap))
                yield return "L290 no-local-epoch-recorded: ReseedExploration does not touch " +
                             "_localExplorationStart. Without a recorded stamp the anchor is recomputed from " +
                             "scratch on every delta, which re-anchors a running fill at each unrelated order " +
                             "leaf — the bar restarts, and past the def duration it would be torn down and " +
                             "re-started forever.";
            if (!Program.ReadsField(flush, edgeSet))
                yield return "L290 decision-unreached: FlushOrderReseed does not read _hostExplorationEdge, so " +
                             "the host's 'explore now' edge never reaches ReseedExploration and no exploration " +
                             "is ever anchored at all — every client would sit on the idle case (b) forever.";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        /// <summary>The four executable cases, run against ANY implementation of the anchor — the production
        /// one and the positive control alike. <paramref name="who"/> names which, so a red line says whose
        /// rule broke.</summary>
        private static IEnumerable<string> Arms(Func<bool, bool, bool, TimeUnit, TimeUnit, TimeUnit> anchor,
                                                Func<bool, bool, TimeUnit, TimeUnit, TimeUnit, bool> exploring,
                                                string who)
        {
            // (b) no host edge, nothing running here: no start is manufactured, so nothing is explored.
            var idle = anchor(false, false, false, HostStart, LocalNow);
            if (!(idle <= TimeUnit.Zero) || exploring(false, false, idle, Duration, LocalNow))
                yield return "L290 phantom-exploration: " + who + " anchors an exploration start (" + idle +
                             ") for an aircraft the host has issued no exploration order for in this batch. " +
                             "StartedExplorationAt is never cleared by the game — not by EndExploreCurrentSite:460, " +
                             "not by SiteExplorationCompleted:474 — so any fallback to the mirrored leaf makes " +
                             "every aircraft that ever explored start a phantom spinner at the next explorable " +
                             "site it parks at, and that spinner is state the host never authored.";

            // (c) THE SAFETY PROPERTY: on the host's edge the anchor is OUR clock, never anything earlier.
            var onEdge = anchor(true, false, false, HostStart, LocalNow);
            if (!(onEdge == LocalNow))
                yield return "L290 client-can-finish-first: " + who + " anchors the host's exploration order at " +
                             onEdge + " instead of this peer's own " + LocalNow + ". An anchor in the HOST's " +
                             "per-actor epoch (rail-baseline.txt:33 excludes that clock from the rail) is an " +
                             "arbitrary offset — live 2026-08-08 it was five hours — so the client's timer can " +
                             "expire BEFORE the host's and complete an exploration the host has not. Anchoring on " +
                             "delta ARRIVAL is what makes the client structurally late and therefore never the author.";

            // (d) a running fill is not re-anchored by an unrelated order delta.
            var kept = anchor(false, false, true, HostStart, LocalNow);
            if (!(kept == HostStart))
                yield return "L290 fill-restarted: " + who + " discards this peer's existing stamp and re-anchors " +
                             "at " + kept + ". Every unrelated order leaf on that aircraft (RangeRemaining moves " +
                             "continuously mid-flight) would then restart the progress bar from zero.";

            // (g) A NEW HOST ORDER OUTRANKS AN EXISTING STAMP — the case (d) was silently swallowing, and
            // the reason the client's SECOND exploration of a session never drew a ring (2026-08-14).
            var reordered = anchor(true, false, true, HostStart, LocalNow);
            if (!(reordered == LocalNow))
                yield return "L290 stale-stamp-wins: " + who + " answers " + reordered + " for an aircraft whose " +
                             "StartExplorationTime delta JUST LANDED while a stamp from an EARLIER exploration is " +
                             "still in _localExplorationStart. That stamp is dropped in exactly one place (the " +
                             "teardown branch of ReseedExploration), but a client's own timer normally expires by " +
                             "itself — SiteExplorationCompleted:474 -> EndExploreCurrentSite:460 — leaving " +
                             "IsExploringSite false with the stamp alive, after which `should == IsExploringSite` " +
                             "returns early and never clears it. Anchoring the new order on that dead stamp puts " +
                             "`end` in the past, ShouldBeExploring says false, ExploreCurrentSite is never invoked, " +
                             "and every exploration after the first is invisible on this peer. The arrival edge is " +
                             "recorded only on a real leaf delta (GenericApplier:1258), so it is a NEW order by " +
                             "construction and must re-anchor; case (d) still holds because an unrelated order leaf " +
                             "lands with no edge at all.";

            // (e) the join case: a running handle with no stamp is adopted, never torn down.
            var adopted = anchor(false, true, false, HostStart, LocalNow);
            if (!(adopted == LocalNow))
                yield return "L290 join-spinner-killed: " + who + " answers " + adopted + " for an aircraft that " +
                             "IS exploring here but carries no stamp — the peer that joined mid-exploration, whose " +
                             "handle came from the save transfer (law 1b) and never from a delta. A zero anchor " +
                             "makes ShouldBeExploring say 'not exploring' while the live handle says it is, and " +
                             "the symmetric branch then forces EndExploreCurrentSite on a legitimate spinner.";
        }

        /// <summary>The rule as it stood before 2026-08-08: the anchor IS the mirrored leaf, whatever epoch
        /// it happens to be in. Present so the arms are proven able to fail.</summary>
        private static class FakeSeam
        {
            internal static TimeUnit AnchorOnTheMirroredValue(bool hostStartLanded, bool alreadyExploring,
                                                              bool haveStamp, TimeUnit mirrored, TimeUnit localNow)
                => mirrored;
        }

        private static Func<bool, bool, bool, TimeUnit, TimeUnit, TimeUnit> Ask(MethodInfo m) =>
            (a, b, c, d, e) => (TimeUnit)m.Invoke(null, new object[] { a, b, c, d, e });

        private static Func<bool, bool, TimeUnit, TimeUnit, TimeUnit, bool> Ask5(MethodInfo m) =>
            (a, b, c, d, e) => (bool)m.Invoke(null, new object[] { a, b, c, d, e });
    }
}
