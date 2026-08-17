using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Serialization;
using PhoenixPoint.Geoscape.Levels;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

namespace RailSim
{
    internal static class Scenarios
    {
        /// <summary>Every scenario, by name. A scenario returns zero strings when it holds and one
        /// human-readable failure per broken assertion otherwise.</summary>
        internal static IEnumerable<KeyValuePair<string, Func<int, IEnumerable<string>>>> All()
        {
            yield return Pair("seeded-transport-is-reproducible", SeededTransportIsReproducible);
            yield return Pair("every-peer-presents-in-the-same-order", EveryPeerPresentsInTheSameOrder);
            yield return Pair("the-backlog-is-never-trimmed", TheBacklogIsNeverTrimmed);
            yield return Pair("one-peers-backlog-never-blocks-another", OnePeersBacklogNeverBlocksAnother);
            yield return Pair("a-local-dismissal-removes-only-mine", ALocalDismissalRemovesOnlyMine);
            yield return Pair("a-global-dismissal-removes-it-everywhere", AGlobalDismissalRemovesItEverywhere);
            yield return Pair("an-answered-shared-window-closes-everywhere",
                              AnAnsweredSharedWindowClosesEverywhere);
            yield return Pair("no-gap-is-permanent", NoGapIsPermanent);
            yield return Pair("a-hole-is-earned-not-declared", AHoleIsEarnedNotDeclared);
            yield return Pair("the-hole-predicate-classifies-anything", TheHolePredicateClassifiesAnything);
            yield return Pair("an-untouched-surface-does-not-repaint", AnUntouchedSurfaceDoesNotRepaint);
            yield return Pair("my-own-gesture-repaints-my-own-strip", MyOwnGestureRepaintsMyOwnStrip);
            yield return Pair("a-joining-peer-never-replays-the-status-bar",
                              AJoiningPeerNeverReplaysTheStatusBar);
            yield return Pair("the-reveal-is-one-instant-for-every-peer",
                              TheRevealIsOneInstantForEveryPeer);
            yield return Pair("two-raises-never-share-an-identity", TwoRaisesNeverShareAnIdentity);
            yield return Pair("every-channel-answers-a-held-window", EveryChannelAnswersAHeldWindow);
            yield return Pair("no-lift-before-this-peers-own-first-frame",
                              NoLiftBeforeThisPeersOwnFirstFrame);
            yield return Pair("a-gated-peers-rollup-becomes-computable",
                              AGatedPeersRollupBecomesComputable);
            yield return Pair("an-unarmed-rollup-is-never-rebuilt", AnUnarmedRollupIsNeverRebuilt);
        }

        /// <summary>The row the two properties below drive: production income, the rollup the reported
        /// defect was measured on. Built through the REAL <c>DerivedAggregateRefresh.Row</c> so the
        /// properties execute the shipped arming decision rather than a copy of it.</summary>
        private static DerivedAggregateRefresh.Row ProductionRow() =>
            new DerivedAggregateRefresh.Row(
                typeof(PhoenixPoint.Geoscape.Levels.GeoFaction), "OnSiteProductionChanged",
                DerivedAggregateRefresh.Kind.Recompute, "UpdateProduction", new[] { "S#" },
                "railsim probe");

        /// <summary>
        /// L557 AS AN OBSERVABLE RUN. A GATED PEER — one whose whole hourly sim is refused by
        /// <c>ClientSimGate</c> and whose model state arrives as direct FIELD writes, so the game's own
        /// <c>site.ProductionChanged</c> event never fires locally.
        ///
        /// The run compares the SHIPPED-BEFORE policy (no rebuild ever) with the engine's, over the same
        /// batches, so it cannot go green by testing nothing: the old policy must still reproduce the
        /// measured defect — production income pinned at zero, therefore
        /// <c>ItemManufacturing.GetTotalTime</c> returning <c>TimeUnit.Invalid</c>, therefore
        /// <c>UIModuleFactionAgendaTracker.UpdateData</c> destroying the manufacture row it had just built,
        /// on every single batch.
        ///
        /// <c>Invalid</c> is modelled as a NEGATIVE duration rather than as a separate flag, because that
        /// is exactly the trap in the real code: <c>TimeUnit.Invalid</c> IS <c>TimeSpan.MinValue</c>, so
        /// the strip's "already elapsed" test (<c>&lt;= TimeUnit.Zero</c>) swallows the uncomputable case
        /// silently. A model that gave it its own flag would test a bug the game does not have.
        ///
        /// Nothing here waits on a peer: every step is this peer's own batch and its own model.
        /// </summary>
        private static IEnumerable<string> AGatedPeersRollupBecomesComputable(int seed)
        {
            var rng = new Random(seed);
            var row = ProductionRow();
            const double manufactureCost = 240.0;   // manufacture points the queued item still owes

            // A run of rail batches as a real session produces them: soldier churn and clock ticks with
            // genuine site-production writes interleaved. The site writes land as FIELDS, which is the
            // whole premise — the mirrored per-site production below is ALREADY correct on this peer.
            var batches = new[]
            {
                new[] { "U#a.Progression" },
                new[] { "S#7.SiteProduction", "S#7.State" },
                new[] { "T.Now" },
                new[] { "U#b.Statuses", "V#3.Travel" },
                new[] { "S#9.SiteProduction" },
                new[] { "T.Now" },
            };
            // Per-batch mirrored truth: what the host's own rollup would have produced from the same
            // sites. Jittered by seed so a hard-coded expectation cannot pass.
            double income = 0.0;
            var mirrored = new double[batches.Length];
            for (int i = 0; i < batches.Length; i++)
            {
                if (batches[i].Any(p => p.StartsWith("S#", StringComparison.Ordinal)))
                    income += 6.0 + rng.Next(0, 5);
                mirrored[i] = income;
            }
            if (mirrored[mirrored.Length - 1] <= 0.0)
                yield return "a-gated-peers-rollup-becomes-computable: the run itself produced no " +
                             "production income at all, so it would pass without testing anything.";

            // ── OLD: the rollup is never rebuilt on the gated peer ───────────
            double oldIncome = 0.0;
            int oldRowsDestroyed = 0;
            foreach (var batch in batches)
            {
                double time = oldIncome <= 0.0 ? -1.0 : manufactureCost / oldIncome; // Invalid == negative
                if (time <= 0.0) oldRowsDestroyed++;
            }
            if (oldRowsDestroyed != batches.Length)
                yield return "a-gated-peers-rollup-becomes-computable: the pre-fix policy did NOT " +
                             "reproduce the reported defect (" + oldRowsDestroyed + " of " + batches.Length +
                             " batches destroyed the row, expected all of them). The comparison below is " +
                             "then measuring nothing.";

            // ── NEW: an armed row is rebuilt from state this peer already holds ──
            double newIncome = 0.0;
            int rebuilds = 0, survived = 0, destroyedAfterFirstInput = 0;
            bool sawInput = false;
            for (int i = 0; i < batches.Length; i++)
            {
                // THE SHIPPED DECISION, executed — not a re-implementation of it.
                if (DerivedAggregateRefresh.Arms(row, batches[i]))
                {
                    newIncome = mirrored[i];   // the parameterless rebuild is a pure function of mirrored state
                    rebuilds++;
                    sawInput = true;
                }
                double time = newIncome <= 0.0 ? -1.0 : manufactureCost / newIncome;
                if (time > 0.0) survived++;
                else if (sawInput) destroyedAfterFirstInput++;
            }

            if (rebuilds != 2)
                yield return "a-gated-peers-rollup-becomes-computable: expected exactly 2 rebuilds (the " +
                             "two batches that touched a site), got " + rebuilds + ". Arming is BY INPUT: " +
                             "rebuilding more often is wasted work, rebuilding less often is the stale zero.";
            if (destroyedAfterFirstInput != 0)
                yield return "a-gated-peers-rollup-becomes-computable: the manufacture row was still " +
                             "destroyed " + destroyedAfterFirstInput + " time(s) AFTER the peer had " +
                             "everything needed to compute its duration. A rollup the peer can rebuild " +
                             "from mirrored state must never leave a derived duration uncomputable.";
            if (survived == 0)
                yield return "a-gated-peers-rollup-becomes-computable: the row never survived a single " +
                             "batch, so the engine changed nothing.";
            if (Math.Abs(newIncome - mirrored[mirrored.Length - 1]) > 1e-9)
                yield return "a-gated-peers-rollup-becomes-computable: the peer's rebuilt income (" +
                             newIncome + ") does not equal what the same inputs give the host (" +
                             mirrored[mirrored.Length - 1] + "). A recompute that is not EXACT is a second " +
                             "source of truth, which is the divergence the carry-vs-recompute choice rests " +
                             "on not creating.";
        }

        /// <summary>The other direction, and the one that keeps the engine cheap enough to sit in the rail
        /// frame: a batch that touched nothing the row DECLARED must not rebuild it, while every UNCERTAIN
        /// answer must. The conservative direction is not symmetric on purpose — an unnecessary rebuild
        /// costs one pure rollup, a missed one is the stale zero this whole file exists to remove.</summary>
        private static IEnumerable<string> AnUnarmedRollupIsNeverRebuilt(int seed)
        {
            var row = ProductionRow();
            var carried = new DerivedAggregateRefresh.Row(
                typeof(PhoenixPoint.Geoscape.Levels.GeoFaction), "OnSiteAdded",
                DerivedAggregateRefresh.Kind.Carried, null, new string[0], "railsim probe");

            if (DerivedAggregateRefresh.Arms(row, new[] { "U#a.Progression", "T.Now", "V#3.Travel" }))
                yield return "an-unarmed-rollup-is-never-rebuilt: a batch of soldier, clock and vehicle " +
                             "paths armed a row that declared only sites. Arming on everything puts a " +
                             "reflective rebuild of every rollup into every rail batch.";
            if (!DerivedAggregateRefresh.Arms(row, new[] { "U#a.Progression", "S#7.SiteProduction" }))
                yield return "an-unarmed-rollup-is-never-rebuilt: a batch that DID touch a site failed to " +
                             "arm the site-declaring row.";
            if (!DerivedAggregateRefresh.Arms(row, new string[] { null }))
                yield return "an-unarmed-rollup-is-never-rebuilt: a null (unknown) path did not arm. Every " +
                             "uncertain answer must be 'rebuild' — the same conservative direction " +
                             "OpenUiRepaint.SurfaceRepaints takes for the same reason.";
            if (DerivedAggregateRefresh.Arms(row, new string[0]))
                yield return "an-unarmed-rollup-is-never-rebuilt: an EMPTY batch armed a rebuild. Nothing " +
                             "changed at all, so nothing can have staled.";
            if (DerivedAggregateRefresh.Arms(carried, new[] { "S#7.SiteProduction" }))
                yield return "an-unarmed-rollup-is-never-rebuilt: a CARRIED row armed. A carried handler " +
                             "mints information this peer cannot derive — running it locally is precisely " +
                             "the client-side simulation ClientSimGate exists to refuse.";
            if (DerivedAggregateRefresh.Arms(null, new[] { "S#7.SiteProduction" }))
                yield return "an-unarmed-rollup-is-never-rebuilt: a null row armed.";
        }

        /// <summary>L554 as an OBSERVABLE RUN, over the skew the 21:54 capture actually measured — which is
        /// the skew the 16 ms frame grid in <see cref="TheRevealIsOneInstantForEveryPeer"/> cannot see.
        /// Three peers, host at the origin; delivery is hundreds of ms and each CLIENT is inside one
        /// enormous first post-load frame (2038 ms, measured) when the reveal reaches it, while the host is
        /// not. That combination is what made a host-clock deadline useless: it expired 258 ms before the
        /// packet even landed.
        ///
        /// The run compares the DEADLINE-ONLY policy (what shipped) with the READY-SET policy over the same
        /// numbers, so it cannot go green by testing nothing — the old one must still reproduce a spread of
        /// seconds. The property that matters is not the spread though, it is this: NO PEER, AND ESPECIALLY
        /// NOT THE HOST, LIFTS BEFORE THE LAST PEER WAS READY. Also run: a packet delayed far beyond
        /// anything the RTT sample could predict, a peer that departs mid-barrier, and a peer that simply
        /// stops talking.</summary>
        private static IEnumerable<string> NoLiftBeforeThisPeersOwnFirstFrame(int seed)
        {
            var rng = new Random(seed);
            const long releaseMs = 100000;      // the host observes AllDone on its own clock
            const int cheapFrameMs = 16;        // a peer that is already rendering
            // Host index 0. Hops and first-frame costs from the 2026-08-15 21:54 capture, jittered by seed.
            int[] hopMs = { 0, 180 + rng.Next(-20, 21), 420 + rng.Next(-20, 21) };
            // The host does NOT pay a post-load frame on the new-campaign path — it never re-entered the
            // level it already is (SaveTransferCoordinator.cs:714). That asymmetry IS the defect, so it is
            // modelled rather than smoothed away.
            int[] firstFrameMs = { cheapFrameMs, 2038, 1900 };
            // A LATE PACKET: s2's RevealAll is held up well past anything the RTT sample could predict.
            int[] extraDelayMs = { 0, 0, 600 };

            // The lead is minted from the MEASURED link, and the capture measured ~0 — that is precisely how
            // a 400 ms floor came to be shipped as the whole answer.
            long due = releaseMs + RevealSchedule.LeadMs(0);

            var arrival = new long[3];
            var ready = new long[3];
            for (int p = 0; p < 3; p++)
            {
                arrival[p] = releaseMs + hopMs[p] + extraDelayMs[p];
                // READY = one of THIS peer's own frames completed past the arm. Not the arrival and not the
                // load callback: the peer is inside a frame when the packet lands and cannot act until it ends.
                ready[p] = arrival[p] + firstFrameMs[p];
            }

            // ── OLD: the deadline is the whole release ───────────────────────────────
            var oldLift = new long[3];
            for (int p = 0; p < 3; p++)
                oldLift[p] = Math.Max(arrival[p], due) + (p == 0 ? cheapFrameMs : firstFrameMs[p]);

            // ── NEW: the deadline is a floor, the ready-set is the release ───────────
            // Each peer observes its own readiness immediately, the host's after one hop, and another
            // client's after two (the host relays). Nobody is consulted: this is what LANDED locally.
            var newLift = new long[3];
            for (int q = 0; q < 3; q++)
            {
                long observed = ready[q];
                for (int p = 0; p < 3; p++)
                {
                    if (p == q) continue;
                    long hops = hopMs[p] + (q == 0 || p == 0 ? 0 : hopMs[q]);
                    observed = Math.Max(observed, ready[p] + hops);
                }
                newLift[q] = NextFrame(Math.Max(observed, due), rng.Next(cheapFrameMs), cheapFrameMs);
            }

            long oldSpread = oldLift.Max() - oldLift.Min();
            long newSpread = newLift.Max() - newLift.Min();

            if (oldLift[0] >= ready.Max())
                yield return "no-lift-before-this-peers-own-first-frame: the deadline-only policy did NOT " +
                             "put the host on the geoscape ahead of the last peer's readiness in this run, " +
                             "so the scenario is not reproducing the defect it exists to measure and would " +
                             "stay green through it.";
            if (oldSpread < 1000)
                yield return "no-lift-before-this-peers-own-first-frame: the deadline-only policy spread " +
                             "only " + oldSpread + "ms. The measured run spread 1210ms; a model that cannot " +
                             "reproduce that is not modelling the 21:54 capture.";

            // THE INVARIANT. Everything else in this scenario is context for this line.
            for (int p = 0; p < 3; p++)
                if (newLift[p] < ready.Max())
                    yield return "no-lift-before-this-peers-own-first-frame: peer " + p + " lifted at " +
                                 newLift[p] + ", before the LAST peer was ready at " + ready.Max() + ". A " +
                                 "screen may only come up on a condition its own peer observed to be " +
                                 "satisfied — being early is the entire defect, and the host being early is " +
                                 "the reported one.";
            if (newSpread >= oldSpread)
                yield return "no-lift-before-this-peers-own-first-frame: the ready-set policy spread " +
                             newSpread + "ms against the deadline's " + oldSpread + "ms. It is supposed to " +
                             "collapse the spread, not preserve it.";
            if (newSpread > 2 * hopMs.Max() + cheapFrameMs)
                yield return "no-lift-before-this-peers-own-first-frame: peers left the loading screen " +
                             newSpread + "ms apart. The irreducible residual is the relay — one hop out and " +
                             "one back — plus a frame; beyond that the ready-set is not reaching everybody " +
                             "the same way.";

            // ── A DEPARTED PEER SHRINKS THE WAIT, NEVER EXTENDS IT (P13) ────────────
            long twoPeerReady = Math.Max(ready[0], ready[1]);
            if (twoPeerReady > ready.Max())
                yield return "no-lift-before-this-peers-own-first-frame: dropping a peer from the live " +
                             "roster made the remaining peers wait LONGER. A departure must only ever " +
                             "shrink the expected set.";
            var tracker = new RosterProgressTracker();
            tracker.MarkReady(0);
            tracker.MarkReady(1);
            if (tracker.AllReady(new byte[] { 0, 1, 2 }))
                yield return "no-lift-before-this-peers-own-first-frame: AllReady is true while slot 2 is " +
                             "still expected and has never rendered. The barrier would be decorative.";
            if (!tracker.AllReady(new byte[] { 0, 1 }))
                yield return "no-lift-before-this-peers-own-first-frame: AllReady is false once the " +
                             "departed slot leaves the expected set, so one player quitting would hang " +
                             "everyone else behind a black screen forever.";

            // ── A SILENT PEER IS GIVEN UP ON: BOUNDED, AND ON THE WAITER'S OWN CLOCK ─
            // Not a wait on a person: nothing here needs the missing peer to DO anything, and the timer runs
            // locally so the peer that went quiet cannot withhold the release from it.
            if (RevealSchedule.MayLift(false, due, due, RevealSchedule.ReadyGiveUpMs - 1))
                yield return "no-lift-before-this-peers-own-first-frame: the give-up fired before its bound " +
                             "elapsed, so a merely slow peer is abandoned mid-load.";
            if (!RevealSchedule.MayLift(false, due, due, RevealSchedule.ReadyGiveUpMs))
                yield return "no-lift-before-this-peers-own-first-frame: a peer that never reports and never " +
                             "leaves strands every other peer behind the curtain forever. Silence must be " +
                             "bounded — that is the difference between waiting on a LOAD and waiting on a " +
                             "peer that has stopped existing.";
            if (RevealSchedule.MayLift(true, due - 1, due, 0))
                yield return "no-lift-before-this-peers-own-first-frame: everybody-ready lifted BEFORE the " +
                             "common instant. The instant is a floor: it may delay a fast peer, never " +
                             "release an early one.";
        }

        /// <summary>L553 as an OBSERVABLE RUN: a session that raises the SAME window many times. The payload
        /// is the measured one — a manufactured aircraft, whose entity ref is EMPTY by construction — so the
        /// OLD identity (kind plus payload) collides on every raise and the NEW one (payload plus the host
        /// journal position of the raise) cannot. The old policy is run too, and MUST still collide: a
        /// scenario that only exercises the fix goes green while proving nothing.</summary>
        private static IEnumerable<string> TwoRaisesNeverShareAnIdentity(int seed)
        {
            var rng = new Random(seed);
            var payload = new GeoModalMirror.Raise
            {
                Shape = GeoModalMirror.DataShape.AssetDeploy,
                Ref = "",                                   // no GeoCharacter: the aircraft case
                Keys = new[] { "aircraft-guid", "item-guid" },
            };

            // Every raise in this run gets the host's next journal position, exactly as the mint seam hands
            // them out; the run starts at an arbitrary position so nothing depends on being first.
            uint position = (uint)rng.Next(1, 500);
            var live = new List<string>();
            var payloadOnly = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 8; i++)
            {
                live.Add(WindowQueueSync.Identity("AssetDeployment", payload,
                                                  WindowQueueSync.RaiseTagFor(position + (uint)i)));
                payloadOnly.Add("AssetDeployment|" + payload.Shape + "|" + payload.Ref + "|" +
                                string.Join(",", payload.Keys));
            }

            if (payloadOnly.Count != 1)
                yield return "two-raises-never-share-an-identity: the payload-only identity no longer " +
                             "collides for eight identical raises, so this scenario is not reproducing the " +
                             "defect it exists to measure and would stay green through it.";
            if (live.Distinct(StringComparer.Ordinal).Count() != live.Count || live.Any(string.IsNullOrEmpty))
                yield return "two-raises-never-share-an-identity: " +
                             (live.Count - live.Distinct(StringComparer.Ordinal).Count()) + " of " + live.Count +
                             " concurrently live windows share an identity string. That is the wrong-asset " +
                             "defect: a stale copy answered later validates against a NEWER window and the " +
                             "host deploys something nobody chose.";

            // AND THE STALE ANSWER IS REFUSED IN WORDS, never re-aimed: the peers keep their own copies (an
            // ordinary window is per-peer and is not force-closed), so a copy of raise #1 CAN be answered
            // after raise #2 has taken the screen. It must land nowhere.
            if (WindowQueueSync.ValidateIdentity(live[1], live[0]) == null)
                yield return "two-raises-never-share-an-identity: an answer naming the FIRST raise was " +
                             "accepted against the second. A stale answer must be refused with a reason, " +
                             "never applied to whatever window happens to be up.";
            if (WindowQueueSync.ValidateIdentity(live[0], live[0]) != null)
                yield return "two-raises-never-share-an-identity: an answer naming the window that IS up was " +
                             "refused, so no window could ever be answered at all.";
        }

        /// <summary>L553's second half as an OBSERVABLE RUN: a host that spends the session INSIDE screens of
        /// its own (manufacturing, research), where <c>WindowOrder.HoldsForOpenScreen</c> keeps every raised
        /// window queued and never current. Both answer channels are driven over the same history — the OLD
        /// per-channel policy (read <c>_currentStateSwitchRequest</c> only) must still lose the answers, the
        /// shared resolution must land every one of them, and an answer already taken must not apply
        /// twice.</summary>
        private static IEnumerable<string> EveryChannelAnswersAHeldWindow(int seed)
        {
            var rng = new Random(seed);
            var payload = new GeoModalMirror.Raise
            {
                Shape = GeoModalMirror.DataShape.AssetDeploy,
                Ref = "",
                Keys = new[] { "aircraft-guid", "item-guid" },
            };

            // The host is inside a screen: nothing is current, everything the game raised is held.
            var queued = new List<string>();
            for (uint pos = 1; pos <= 5; pos++)
                queued.Add(WindowQueueSync.Identity("AssetDeployment", payload,
                                                    WindowQueueSync.RaiseTagFor(pos)));
            string currentIdentity = null;

            int oldPolicyApplied = 0, applied = 0;
            foreach (var answer in queued.OrderBy(_ => rng.Next()).ToArray())
            {
                // OLD: the deploy channel's whole body — validate against the current slot, or drop it.
                if (WindowQueueSync.ValidateIdentity(currentIdentity, answer) == null) oldPolicyApplied++;

                int at = WindowQueueSync.ResolveAnswerTarget(currentIdentity, queued, answer);
                if (at < 0) continue;
                queued.RemoveAt(at);           // TakeQueued: out of the queue BEFORE the consequence runs
                applied++;

                // ANSWER-ONCE: the same answer arriving again from a second peer must find nothing.
                if (WindowQueueSync.ResolveAnswerTarget(currentIdentity, queued, answer) !=
                    WindowQueueSync.TargetNone)
                    yield return "every-channel-answers-a-held-window: the answer '" + answer + "' resolved " +
                                 "to a target a second time after it had been applied. Two peers answering " +
                                 "one window would deploy the asset twice, or launch one mission twice.";
            }

            if (oldPolicyApplied != 0)
                yield return "every-channel-answers-a-held-window: the current-slot-only policy applied " +
                             oldPolicyApplied + " answers, so this scenario is no longer reproducing the " +
                             "defect it exists to measure and would stay green through it.";
            if (applied != 5 || queued.Count != 0)
                yield return "every-channel-answers-a-held-window: " + applied + " of 5 answers landed and " +
                             queued.Count + " windows are still queued. A host inside a screen of its own is " +
                             "the NORMAL state for an asset-deployment prompt (it is raised from one), so a " +
                             "channel that cannot answer a held window never answers at all — the transport " +
                             "is never placed and the prompt wedges on every peer.";
        }

        /// <summary>L551 as an OBSERVABLE RUN, and the one property the law's IL arms cannot state: with a
        /// real hop skew and real per-peer frame grids, does everybody actually leave the loading screen
        /// together? Three peers, the measured 2026-08-15 shape — host at the origin, s3 a short hop out,
        /// s2 a long one, each on its own frame boundary and its own clock offset. The scenario runs the
        /// OLD policy (lift on arrival) and the NEW one (lift at the shipped instant) over the same skews
        /// and compares their spreads, so it can never go green by testing nothing: the old policy MUST
        /// still reproduce a spread of hundreds of milliseconds, and the new one must collapse it to at
        /// most one frame — the residual nobody can remove, because a peer can only act on a frame.</summary>
        private static IEnumerable<string> TheRevealIsOneInstantForEveryPeer(int seed)
        {
            const int frameMs = 16;
            var rng = new Random(seed);

            // Per peer: one-way hop, the clock offset PingTable.ObserveHostClock leaves behind (a few ms of
            // sampling error either way), and where its frame grid happens to sit. Host is index 0.
            int[] hopMs = { 0, 180, 420 };
            int[] clockErrMs = { 0, rng.Next(-6, 7), rng.Next(-6, 7) };
            int[] framePhase = { rng.Next(frameMs), rng.Next(frameMs), rng.Next(frameMs) };

            // The host releases the barrier at host-clock 100000 and picks the common instant from the
            // worst MEASURED round trip (2 x the worst one-way hop above).
            const long releaseMs = 100000;
            long due = releaseMs + RevealSchedule.LeadMs(hopMs.Max() * 2);

            var scheduled = new long[3];
            var onArrival = new long[3];
            for (int p = 0; p < 3; p++)
            {
                // The packet lands here; the host "receives" its own release instantly.
                long arrivalHostMs = releaseMs + hopMs[p];

                // OLD POLICY: lift on the frame after the packet lands. This is what shipped.
                onArrival[p] = NextFrame(arrivalHostMs, framePhase[p], frameMs);

                // NEW POLICY: lift on the first frame at or after the common instant, decided against a
                // host clock this peer only knows to within clockErrMs. A peer whose instant already
                // passed on arrival lifts on its next frame — never earlier than anyone, never blocked.
                long visible = Math.Max(arrivalHostMs, due + clockErrMs[p]);
                scheduled[p] = NextFrame(visible, framePhase[p], frameMs);
            }

            long oldSpread = onArrival.Max() - onArrival.Min();
            long newSpread = scheduled.Max() - scheduled.Min();

            if (oldSpread < 300)
                yield return "the-reveal-is-one-instant-for-every-peer: the lift-on-arrival policy spread " +
                             "only " + oldSpread + "ms over a 420ms hop skew, so this scenario is not " +
                             "reproducing the defect it exists to measure and would stay green through it.";
            if (newSpread > frameMs + 2 * 6)
                yield return "the-reveal-is-one-instant-for-every-peer: peers left the loading screen " +
                             newSpread + "ms apart on a common instant. Anything beyond one frame plus the " +
                             "clock-sampling error means the deadline is not actually shared — one peer is " +
                             "on the geoscape while another still holds a curtain, which is the report.";
            if (scheduled[0] < due)
                yield return "the-reveal-is-one-instant-for-every-peer: the HOST lifted at " + scheduled[0] +
                             ", before the instant " + due + " it handed to everyone else. The host is " +
                             "inside the scheme, not exempt from it — being exempt is the entire defect.";

            // A DEADLINE, NOT A QUORUM (P13): the instant does not move when a peer stops existing. Re-run
            // the two live peers with the third gone and the surviving instants must be the same numbers.
            long dueWithoutS2 = releaseMs + RevealSchedule.LeadMs(hopMs.Max() * 2);
            if (dueWithoutS2 != due)
                yield return "the-reveal-is-one-instant-for-every-peer: the common instant moved when a peer " +
                             "left. Nothing about this deadline may depend on who is still here — a peer " +
                             "that goes silent must not postpone anyone's lift by one millisecond.";
            if (!RevealSchedule.Due(due + 5000, due))
                yield return "the-reveal-is-one-instant-for-every-peer: a peer arriving 5s late does not fire " +
                             "at all. Late must mean 'lift now', never 'wait behind the curtain forever'.";
        }

        /// <summary>The first tick of this peer's own frame grid at or after <paramref name="atMs"/>. A peer
        /// cannot act between frames, so this is the floor on any simultaneity claim.</summary>
        private static long NextFrame(long atMs, int phaseMs, int frameMs)
        {
            long since = atMs - phaseMs;
            long frames = (since + frameMs - 1) / frameMs;
            return phaseMs + frames * frameMs;
        }

        /// <summary>L550 as an OBSERVABLE HISTORY, and the one property the law's per-call arms cannot
        /// state: a WHOLE SESSION of status-bar notices, presented exactly once each across a join, a
        /// reload and the 50-entry cap. The geoscape log rides as ONE blob that REBUILDS the entire list
        /// on every apply, so a peer joining mid-campaign receives the whole history at once and every
        /// later batch hands it that history again. The run below is that history: seed (join), three
        /// idle re-applies of the identical log, three real events, a front-trim at the cap, and a batch
        /// with nothing new. Total presented must equal the number of entries that were genuinely APPENDED
        /// after the join — 4 — and never the 30 the join carried, which is the avalanche.</summary>
        private static IEnumerable<string> AJoiningPeerNeverReplaysTheStatusBar(int seed)
        {
            long ticks = long.MinValue;
            int atLast = 0;
            var shown = new List<GeoscapeLogEntry>();

            // The campaign this peer joins into: 30 entries, three per geoscape instant.
            var log = new List<GeoscapeLogEntry>();
            for (int i = 0; i < 30; i++) log.Add(Logged(1000 + i / 3));

            int total = 0, onJoin = 0;
            onJoin = GeoLogNotice.SelectNew(log, false, ref ticks, ref atLast, shown);  // the seed
            bool seeded = true;

            for (int idle = 0; idle < 3; idle++)                                        // identical rebuilds
                total += GeoLogNotice.SelectNew(Rebuild(log), seeded, ref ticks, ref atLast, shown);

            for (int ev = 0; ev < 3; ev++)                                              // three real events
            {
                log = Rebuild(log);
                log.Add(Logged(2000 + ev));
                total += GeoLogNotice.SelectNew(log, seeded, ref ticks, ref atLast, shown);
            }

            log = Rebuild(log).Skip(5).ToList();                                        // the 50-cap trim…
            log.Add(Logged(3000));                                                      // …plus one event
            total += GeoLogNotice.SelectNew(log, seeded, ref ticks, ref atLast, shown);

            total += GeoLogNotice.SelectNew(Rebuild(log), seeded, ref ticks, ref atLast, shown); // quiet batch

            if (onJoin != 0)
                yield return "a-joining-peer-never-replays-the-status-bar: the join itself presented " +
                             onJoin + " notice(s). The first sync after a join or a load must present " +
                             "NOTHING — the blob it carries is history, not news.";
            if (total != 4)
                yield return "a-joining-peer-never-replays-the-status-bar: " + total + " notice(s) were " +
                             "presented across the session where exactly 4 entries were appended after the " +
                             "join. More means the rebuilt blob refired old entries (the centre-top bar " +
                             "replaying the campaign); fewer means a real event went unseen, which is the " +
                             "defect this channel was added to fix.";
        }

        private static List<GeoscapeLogEntry> Rebuild(List<GeoscapeLogEntry> src)
        {
            // Every apply rebuilds each entry with Activator + table fields, so object identity is worthless.
            return src.Select(e => Logged(e.EventDate.TimeSpan.Ticks)).ToList();
        }

        private static GeoscapeLogEntry Logged(long ticks)
        {
            return new GeoscapeLogEntry { EventDate = TimeUnit.FromTimeSpan(TimeSpan.FromTicks(ticks)) };
        }

        /// <summary>L549 as an OBSERVABLE HISTORY: the peer makes the changes ITSELF instead of receiving
        /// them. A host queues a research, then a manufacture, then a gesture the strip does not draw. Each
        /// self-initiated gesture must leave the persistent HUD owing a refresh — the flag every apply path
        /// raises and no local gesture used to. A peer whose own action repaints nothing until it walks out
        /// of the screen and back is the postulate-1 defect reported on the host 2026-08-15.
        ///
        /// The teardown-count half went with the agenda strip's rebuild gate (2026-08-17): the strip now
        /// takes the module's own native Init(context) rebuild once per flushed batch, so there is no
        /// per-gesture teardown decision left to observe.</summary>
        private static IEnumerable<string> MyOwnGestureRepaintsMyOwnStrip(int seed)
        {
            OpenUiRepaint.Reset();

            int unmarked = 0;
            for (int gesture = 0; gesture < 3; gesture++)
            {
                OpenUiRepaint.MarkLocalGesture();          // the acting peer's own capture seam
                if (!OpenUiRepaint.HudRepaintOwed) unmarked++;
            }
            OpenUiRepaint.Reset();

            if (unmarked != 0)
                yield return "my-own-gesture-repaints-my-own-strip: " + unmarked + " of the acting peer's own " +
                             "gestures left the persistent HUD un-owed. Every mark site in this repo is an " +
                             "APPLY path, so the peer that pressed the button is the one peer whose " +
                             "already-open strip is never refreshed — the host starting a research and seeing " +
                             "no research row.";
        }

        /// <summary>§C.1 property 3 — the property no existing law can express, and the acceptance
        /// criterion for the whole of section B. An OBSERVABLE HISTORY: a run of rail batches produces a
        /// list of repaint decisions per surface, and the surface whose declared prefixes were never
        /// touched must appear with ZERO repaints while the surface that was touched appears with some.
        ///
        /// The reported defect in one line: an unrelated peer's MANUFACTURING tick repainted the
        /// soldier-edit screen, and that screen's repaint routes to a DESTRUCTIVE native refresh which
        /// resets the soldier model and restarts its animation.</summary>
        private static IEnumerable<string> AnUntouchedSurfaceDoesNotRepaint(int seed)
        {
            const string editSoldier = "UIStateEditSoldier";
            const string probeOther = "UIStateRailSimProbeOther";

            string[] savedEdit, savedOther;
            UiNativeRepaint.DeclaredPrefixes.TryGetValue(editSoldier, out savedEdit);
            UiNativeRepaint.DeclaredPrefixes.TryGetValue(probeOther, out savedOther);
            UiNativeRepaint.DeclaredPrefixes[editSoldier] = new[] { "U#" };
            UiNativeRepaint.DeclaredPrefixes[probeOther] = new[] { "S#" };

            // A run of batches, in the order and shape a real session produces them: a manufacturing tick
            // and a site tick, neither of which is a soldier, plus one genuine soldier progression change.
            var batches = new[]
            {
                new[] { "S#76.SerializationData.HavenData.AssignedResearchId" },
                new[] { "S#76.SerializationData.HavenData.Population" },
                new[] { "S#12.SerializationData.Manufacture.Current" },
                new[] { "U#4.SerializationData.Progression.Experience" },
                new[] { "S#12.SerializationData.Manufacture.Current" },
            };

            int editRepaints = 0, otherRepaints = 0;
            foreach (var batch in batches)
            {
                if (OpenUiRepaint.SurfaceRepaints(editSoldier, batch)) editRepaints++;
                if (OpenUiRepaint.SurfaceRepaints(probeOther, batch)) otherRepaints++;
            }

            if (savedEdit == null) UiNativeRepaint.DeclaredPrefixes.Remove(editSoldier);
            else UiNativeRepaint.DeclaredPrefixes[editSoldier] = savedEdit;
            if (savedOther == null) UiNativeRepaint.DeclaredPrefixes.Remove(probeOther);
            else UiNativeRepaint.DeclaredPrefixes[probeOther] = savedOther;

            if (editRepaints != 1)
                yield return "an-untouched-surface-does-not-repaint: the soldier-edit surface repainted " +
                             editRepaints + " times across 5 batches, of which exactly ONE touched a 'U#' " +
                             "path. Four of those batches were another peer's site and manufacturing " +
                             "ticks, and repainting that screen for them is what resets the soldier model " +
                             "and restarts its animation.";

            if (otherRepaints != 4)
                yield return "an-untouched-surface-does-not-repaint: the 'S#'-declaring surface repainted " +
                             otherRepaints + " times, not 4. Scoping must not have become a blanket " +
                             "refusal — a surface whose declared prefixes WERE touched must repaint every " +
                             "time, and a stale screen is a defect, not a cosmetic issue.";
        }

        /// <summary>§C.1 property 2: NO GAP IS PERMANENT. A gap self-releases on an armed timer AND is
        /// resolved by an explicit host-minted void record. Both halves are asserted, because the timer
        /// alone would let two peers time out differently and diverge (FIX gap-fill, §2.5), and the void
        /// alone would let a lost void hold a peer forever — which would be a wait on another peer.</summary>
        private static IEnumerable<string> NoGapIsPermanent(int seed)
        {
            WindowGap.Reset();
            double t = 1000.0;
            if (WindowGap.SelfReleasedAt(5, t))
                yield return "no-gap-is-permanent: the gap released on first sight, so the host's order is " +
                             "abandoned the instant a raise is a frame late.";

            // Half the interval: still holding. The hold is what makes the host's order authoritative.
            if (WindowGap.SelfReleasedAt(5, t + WindowGap.SelfReleaseSeconds / 2))
                yield return "no-gap-is-permanent: the gap released after half its armed interval.";

            // Past the interval: released, by itself, with no peer having done anything.
            if (!WindowGap.SelfReleasedAt(5, t + WindowGap.SelfReleaseSeconds + 0.001))
                yield return "no-gap-is-permanent: the gap did NOT self-release after its armed interval. " +
                             "A drain gate that can hold forever is a wait on another peer — one player " +
                             "must be able to drive the whole game while every other peer is AFK.";

            // And the AUTHORITATIVE resolution: a void clears the position outright, timer or no timer.
            WindowJournal.Reset();
            WindowJournal.Append(5, "UIStateRosterDeployment", new byte[] { 5 });
            WindowJournal.ApplyVoid(5);
            WindowGap.Forget(5);
            if (WindowJournal.PeekHead() != null)
                yield return "no-gap-is-permanent: a host-minted void did not clear the gapped position. " +
                             "The timer is the safety net; the void is the resolution, and it must be " +
                             "explicit so two peers cannot resolve the same gap differently.";
            WindowGap.Reset();
            WindowJournal.Reset();
        }

        /// <summary>§C.1 property 4: a dismissal marked LOCAL never removed another peer's entry. Two peers,
        /// same journal position; peer A reads (= dismisses) it; peer B must still hold it.</summary>
        private static IEnumerable<string> ALocalDismissalRemovesOnlyMine(int seed)
        {
            // Peer A.
            WindowJournal.Reset();
            WindowJournal.Append(1, "UIStateGeoModal", new byte[] { 1 });
            JournalEntry read;
            WindowJournal.TryRead(out read);
            int aRemaining = WindowJournal.UnreadCount;

            // Peer B — a separate journal generation, and NOTHING peer A did reaches it. That isolation IS
            // the property: a LOCAL dismissal is a per-peer delete with no wire form at all.
            WindowJournal.Reset();
            WindowJournal.Append(1, "UIStateGeoModal", new byte[] { 1 });
            int bRemaining = WindowJournal.UnreadCount;

            if (aRemaining != 0)
                yield return "a-local-dismissal-removes-only-mine: peer A still holds " + aRemaining +
                             " entries after reading its only one. Read ⇒ deleted, locally.";
            if (bRemaining != 1)
                yield return "a-local-dismissal-removes-only-mine: peer B holds " + bRemaining +
                             " entries, not 1. Peer A's dismissal reached peer B — the default scope is " +
                             "LOCAL and only the mission family is GLOBAL, so an ordinary window a peer " +
                             "closes must remain for everyone else.";

            if (WindowJournal.ScopeOf("UIStateGeoModal") != DismissScope.Local)
                yield return "a-local-dismissal-removes-only-mine: UIStateGeoModal is not declared LOCAL. " +
                             "Default is LOCAL and an UNDECLARED family IS local — a new window family " +
                             "needs no code at all (§A.5).";

            // THE OTHER PROPAGATION SYSTEM. Removing the entry locally is only half of "only mine": the
            // ANSWER RELAY carries a dismissal too — the host runs modal.FinishDialog off it — and until
            // 2026-08-15 it never asked the table (measured: a client dismissing the research window closed
            // the host's copy). L547 owns the invariant; this is the same property in the harness.
            if (WindowQueueSync.MayRelayAnswer("UIStateGeoModal", false))
                yield return "a-local-dismissal-removes-only-mine: the answer relay would still send peer " +
                             "A's dismissal of a LOCAL family to the host, which answers it with " +
                             "modal.FinishDialog and loses its own copy. A LOCAL dismissal removes only the " +
                             "dismissing peer's window, by BOTH mechanisms.";
            if (!WindowQueueSync.MayRelayAnswer("UIStateRosterDeployment", false))
                yield return "a-local-dismissal-removes-only-mine: the answer relay refuses the GLOBAL " +
                             "mission family too, so the gate above is a constant and the mission flow the " +
                             "relay was written for no longer crosses at all.";

            // THE SPLIT (L552, 2026-08-15). Dismissal scope and answer authority are two derivations, and
            // this is the property that says so: the SAME LOCAL family must not travel on a dismissal and
            // MUST travel on an authoritative answer. Fused, the gate was constant-false for every modal —
            // no client emitted an advance at all and a client's Accept on FactionSoldierJoin (reward.Apply,
            // host-side) did nothing. My copy still closes only on me; only the ANSWER crosses.
            if (!WindowQueueSync.MayRelayAnswer("UIStateGeoModal", true))
                yield return "a-local-dismissal-removes-only-mine: the answer relay refuses an AUTHORITATIVE " +
                             "answer of the LOCAL family UIStateGeoModal. Dismissing my own copy is local, " +
                             "but an answer that mutates shared campaign state is the HOST's to run — " +
                             "refusing it makes the click a silent no-op.";
            // DERIVED, NOT LISTED. The authority verdict comes off the window's PAYLOAD SHAPE — a window
            // that names a replicated entity is a decision about an object the campaign shares — so the
            // harness exercises the shape space, never a set of ModalTypes that would rot on the next
            // window anyone adds.
            if (!GeoModalMirror.NamesEntity(new GeoModalMirror.Raise
                    { Shape = GeoModalMirror.DataShape.EntityRef, Ref = "U#1" }))
                yield return "a-local-dismissal-removes-only-mine: a payload that NAMES a rail entity is " +
                             "not classed as authoritative, so no answer can ever reach the host and a " +
                             "client's Accept on a window that grants a unit is a silent no-op.";
            if (GeoModalMirror.NamesEntity(new GeoModalMirror.Raise
                    { Shape = GeoModalMirror.DataShape.ResearchComplete, Ref = "F#1" }))
                yield return "a-local-dismissal-removes-only-mine: an informational payload is classed as " +
                             "authoritative, so dismissing it relays and the host runs FinishDialog and " +
                             "loses its own copy — the defect L547 closed.";
            if (GeoModalMirror.AnswerMutatesSharedState(new object()))
                yield return "a-local-dismissal-removes-only-mine: a payload type this build has never " +
                             "seen was classed as authoritative. An unclassifiable window must get the " +
                             "SAFE verdict, announced, never authority on the strength of ignorance.";
            WindowJournal.Reset();
        }

        /// <summary>§C.1 property 5: a GLOBAL dismissal removed it everywhere. Modelled as the host-minted
        /// void applied to a peer that had NOT read it — the only mechanism that can remove an unread
        /// entry.</summary>
        private static IEnumerable<string> AGlobalDismissalRemovesItEverywhere(int seed)
        {
            if (WindowJournal.ScopeOf("UIStateRosterDeployment") != DismissScope.Global)
                yield return "a-global-dismissal-removes-it-everywhere: the mission family is not declared " +
                             "GLOBAL. It is the ONE global family: once anyone has acted on a mission the " +
                             "decision to deploy is taken, and it is meaningless for the others to accept " +
                             "or refuse.";

            WindowJournal.Reset();
            WindowJournal.Append(1, "UIStateRosterDeployment", new byte[] { 1 });
            WindowJournal.Append(2, "UIStateGeoModal", new byte[] { 2 });
            bool removed = WindowJournal.ApplyVoid(1);
            if (!removed || WindowJournal.UnreadCount != 1)
                yield return "a-global-dismissal-removes-it-everywhere: the void left " +
                             WindowJournal.UnreadCount + " entries and reported removed=" + removed +
                             ". A host-minted void removes an entry a peer has NOT read — that is the only " +
                             "mechanism that can, and it is explicit precisely because an implicit per-peer " +
                             "timeout makes two peers diverge.";
            var head = WindowJournal.PeekHead();
            if (head == null || head.Pos != 2)
                yield return "a-global-dismissal-removes-it-everywhere: after voiding position 1 the head " +
                             "is " + (head == null ? "<null>" : head.Pos.ToString()) + ", not 2. A void " +
                             "must remove exactly the named position and disturb no other.";

            if (WindowJournal.ApplyVoid(999))
                yield return "a-global-dismissal-removes-it-everywhere: a void for a position this peer " +
                             "never held reported success. It must be a no-op — a reconnecting peer " +
                             "legitimately receives voids for entries it never got (§A.2b).";

            // §A.10: what the remaining peers are OWED after that void — the centre-of-screen door in.
            if (!WindowJournal.VoidOwesDeploymentPrompt("UIStateRosterDeployment", wasStillUnread: true))
                yield return "a-global-dismissal-removes-it-everywhere: a global void of an UNREAD mission " +
                             "entry did not owe the centre-of-screen prompt. The remaining peers must get " +
                             "a way into deployment preparation — the decision to deploy is already taken, " +
                             "so the alternative is a mission they can see and cannot join.";
            if (WindowJournal.VoidOwesDeploymentPrompt("UIStateRosterDeployment", wasStillUnread: false))
                yield return "a-global-dismissal-removes-it-everywhere: a peer that had ALREADY read the " +
                             "entry was offered the prompt again. It is offered once, to the peers whose " +
                             "entry the void removed.";
            if (WindowJournal.VoidOwesDeploymentPrompt("UIStateGeoModal", wasStillUnread: true))
                yield return "a-global-dismissal-removes-it-everywhere: a LOCAL family owed the deployment " +
                             "prompt. Only the mission family is GLOBAL and only a global dismissal owes " +
                             "the prompt.";

            // The family the prompt decision is taken on is read from the entry BEFORE the void removes it.
            WindowJournal.Reset();
            WindowJournal.Append(7, "UIStateRosterDeployment", new byte[] { 7 });
            if (WindowJournal.FamilyAt(7) != "UIStateRosterDeployment")
                yield return "a-global-dismissal-removes-it-everywhere: the journal could not name the " +
                             "family at a held position. A void record carries only a position, so a peer " +
                             "that cannot ask its own journal what that position was about can never " +
                             "decide whether the dismissal was global.";
            WindowJournal.ApplyVoid(7);
            if (WindowJournal.FamilyAt(7) != null)
                yield return "a-global-dismissal-removes-it-everywhere: a voided position still named a " +
                             "family, so the entry outlived its removal.";
            WindowJournal.Reset();
        }

        /// <summary>L556: a window whose answer DISPOSES OF A SHARED ASSET closes on every peer the moment
        /// any peer answers it; an informational one closes only on the peer who dismissed it. Two
        /// properties the harness owns rather than the law: the void must name the ANSWERED RAISE when a
        /// peer holds two unread entries of ONE family, and an informational window must SURVIVE another
        /// peer's dismissal.</summary>
        private static IEnumerable<string> AnAnsweredSharedWindowClosesEverywhere(int seed)
        {
            string character = typeof(PhoenixPoint.Geoscape.Entities.GeoCharacter).FullName;
            string site = typeof(PhoenixPoint.Geoscape.Entities.GeoSite).FullName;
            GeoModalMirror.Raise Raise(GeoModalMirror.DataShape shape, string @ref, string cls) =>
                new GeoModalMirror.Raise
                {
                    Shape = shape,
                    Ref = @ref,
                    Keys = cls == null ? new string[0] : new[] { cls },
                };

            // ── THE DERIVATION, over the payload space rather than over a set of windows ──
            var soldierOffer = Raise(GeoModalMirror.DataShape.EntityRef, "U#7", character);
            var aircraftPrompt = Raise(GeoModalMirror.DataShape.AssetDeploy, "", null);
            var pandoranReveal = Raise(GeoModalMirror.DataShape.EntityRef, "S#3", site);

            if (!GeoModalMirror.DismissalIsGlobal("UIStateGeoModal", soldierOffer, false))
                yield return "an-answered-shared-window-closes-everywhere: the haven soldier offer is not " +
                             "globally dismissed. Accepting it runs reward.Apply on the host for the whole " +
                             "campaign, so every other peer's copy offers a unit that has already joined.";
            if (!GeoModalMirror.DismissalIsGlobal("UIStateGeoModal", aircraftPrompt, false))
                yield return "an-answered-shared-window-closes-everywhere: the asset-distribution prompt " +
                             "for an AIRCRAFT — which names no GeoCharacter at all — is not globally " +
                             "dismissed, so NamesEntity has quietly become the whole derivation again.";
            if (GeoModalMirror.DismissalIsGlobal("UIStateGeoModal", pandoranReveal, false))
                yield return "an-answered-shared-window-closes-everywhere: the Pandoran reconnaissance " +
                             "window is globally dismissed. Its payload is a GeoSite — a map feature the " +
                             "world owns anyway — and answering it runs nothing, so one peer clicking OK " +
                             "must not take it off anybody else's screen.";
            if (GeoModalMirror.DismissalIsGlobal("UIStateGeoModal", soldierOffer, true))
                yield return "an-answered-shared-window-closes-everywhere: a window of the PER-PEER answer " +
                             "class was globally dismissed. Every peer answers a mission brief for itself; " +
                             "one player declining means 'I am busy', not 'cancelled for everyone'.";
            if (!GeoModalMirror.DismissalIsGlobal("UIStateRosterDeployment",
                    Raise(GeoModalMirror.DataShape.None, "", null), false))
                yield return "an-answered-shared-window-closes-everywhere: the deployment-preparation " +
                             "screen stopped being globally dismissed. It carries no describable payload, " +
                             "so the declaration table is the only arm that can classify it and the new " +
                             "derivation must ADD to that table, never replace it.";

            // ── TWO UNREAD ENTRIES OF ONE FAMILY: only the ANSWERED raise is voided ──
            // Every modal in this game is the single family "UIStateGeoModal", so this is the ordinary
            // case and not an edge one: WindowJournal.FindUnread would return position 4 for BOTH.
            WindowJournal.Reset();
            WindowJournal.Append(4, "UIStateGeoModal", new byte[] { 4 });
            WindowJournal.Append(5, "UIStateGeoModal", new byte[] { 5 });
            if (WindowJournal.FindUnread("UIStateGeoModal") != 4)
                yield return "an-answered-shared-window-closes-everywhere: FindUnread stopped returning " +
                             "the FIRST unread entry of a family, so the hazard this property exists for " +
                             "has moved and the property no longer proves anything.";
            // The answered raise is the SECOND one, named by its own per-raise tag and never by the family.
            const uint answered = 5;
            if (WindowQueueSync.RaiseTagFor(answered) != "j5")
                yield return "an-answered-shared-window-closes-everywhere: the per-raise tag of position 5 " +
                             "is not 'j5'. The void names a raise through this derivation and nothing else.";
            if (!WindowJournal.ApplyVoid(answered))
                yield return "an-answered-shared-window-closes-everywhere: the void of the answered raise " +
                             "removed nothing.";
            var survivor = WindowJournal.PeekHead();
            if (survivor == null || survivor.Pos != 4 || WindowJournal.UnreadCount != 1)
                yield return "an-answered-shared-window-closes-everywhere: after answering raise 5 the " +
                             "backlog holds " + WindowJournal.UnreadCount + " entries headed by " +
                             (survivor == null ? "<none>" : survivor.Pos.ToString()) + ", not exactly " +
                             "position 4. A family NAME cannot pick between two unread windows of that " +
                             "family — voiding by name would take the one nobody answered.";

            // ── AN INFORMATIONAL WINDOW SURVIVES ANOTHER PEER'S DISMISSAL ──
            // Peer A dismisses its own copy: read ⇒ deleted, locally, and NO void is minted because the
            // derivation says the dismissal is LOCAL. Peer B's entry is therefore untouched.
            WindowJournal.Reset();
            WindowJournal.Append(6, "UIStateGeoModal", new byte[] { 6 });
            JournalEntry mine;
            WindowJournal.TryRead(out mine);
            int aRemaining = WindowJournal.UnreadCount;
            WindowJournal.Reset();                       // peer B — a separate journal generation
            WindowJournal.Append(6, "UIStateGeoModal", new byte[] { 6 });
            if (aRemaining != 0 || WindowJournal.UnreadCount != 1)
                yield return "an-answered-shared-window-closes-everywhere: peer A holds " + aRemaining +
                             " and peer B holds " + WindowJournal.UnreadCount + " after peer A dismissed " +
                             "an informational window. A LOCAL dismissal is a per-peer delete with no wire " +
                             "form at all — nothing may reach peer B.";
            if (GeoModalMirror.DismissalIsGlobal("UIStateGeoModal", pandoranReveal, false))
                yield return "an-answered-shared-window-closes-everywhere: peer A's dismissal of the " +
                             "informational window would mint a void, and peer B would lose a window it " +
                             "was still reading.";
            WindowJournal.Reset();
        }

        /// <summary>§A.2b / R8: the save gate reads ONLY the local cursor. Peer B sits on a backlog it has
        /// not read; peer A, with an empty journal, must still be able to save. This is the property that
        /// keeps the gate from being a quorum — an AFK peer blocks only their own save.</summary>
        private static IEnumerable<string> OnePeersBacklogNeverBlocksAnother(int seed)
        {
            // Peer B: a fat unread backlog.
            WindowJournal.Reset();
            for (uint i = 1; i <= 25; i++) WindowJournal.Append(i, "UIStateGeoModal", new byte[] { 1 });
            if (JournalSaveGate.MaySave(SaveType.ManualSave, WindowJournal.LocalJournalEmpty))
                yield return "one-peers-backlog-never-blocks-another: peer B saved with 25 unread " +
                             "windows. The gate is the whole reason the journal needs no persistence — " +
                             "if it does not hold, a save can carry state the journal will not restore.";

            // Peer A: its own journal, drained. Nothing about peer B is readable from here, and that is
            // the point — the gate takes only (SaveType, localJournalEmpty).
            WindowJournal.Reset();
            if (!JournalSaveGate.MaySave(SaveType.ManualSave, WindowJournal.LocalJournalEmpty))
                yield return "one-peers-backlog-never-blocks-another: peer A could not save with an EMPTY " +
                             "journal. A gate that consults anything but the local cursor is a quorum, " +
                             "which the no-blockers rule forbids outright.";

            if (!JournalSaveGate.MaySave(SaveType.Autosave, false))
                yield return "one-peers-backlog-never-blocks-another: an AUTOSAVE was refused with a " +
                             "non-empty journal. An autosave always proceeds — never blocked, never " +
                             "deferred, never draining first — and whatever is unread is lost, exactly " +
                             "as on any ordinary session exit (§A.2c).";
            WindowJournal.Reset();
        }

        /// <summary>§C.1 property 1: EVERY PEER'S PRESENTATION ORDER IS IDENTICAL. The measured P1 shape,
        /// reproduced: the host raises research then event; the transport delivers them to each peer in
        /// whatever seeded order it likes (the field skew was 363 ms, longer than the old 150 ms settle);
        /// every peer must still present them in the host's order.</summary>
        private static IEnumerable<string> EveryPeerPresentsInTheSameOrder(int seed)
        {
            var histories = new List<List<string>>();
            for (int peer = 0; peer < 3; peer++)
            {
                var clock = new SimClock();
                var net = new SimNet(seed + peer, clock);
                WindowJournal.Reset();

                uint researchPos = WindowJournal.MintHostPosition();
                uint eventPos = WindowJournal.MintHostPosition();
                net.Send(peer, Frame(researchPos, "UIStateGeoModal"));
                net.Send(peer, Frame(eventPos, "UIStateGeoscapeEvent"));

                clock.Advance(1.0f);
                foreach (var msg in net.Drain())
                {
                    uint pos = BitConverter.ToUInt32(msg.Value, 0);
                    string family = System.Text.Encoding.UTF8.GetString(msg.Value, 4, msg.Value.Length - 4);
                    WindowJournal.Append(pos, family, msg.Value);
                }

                var presented = new List<string>();
                JournalEntry e;
                while (WindowJournal.TryRead(out e)) presented.Add(e.Family);
                histories.Add(presented);
            }
            WindowJournal.Reset();

            var reference = histories[0];
            for (int p = 1; p < histories.Count; p++)
                if (!histories[p].SequenceEqual(reference))
                    yield return "every-peer-presents-in-the-same-order: peer 0 presented [" +
                                 string.Join(",", reference) + "] and peer " + p + " presented [" +
                                 string.Join(",", histories[p]) + "]. This is P1 verbatim — the " +
                                 "2026-08-15 session had the host queue research→event while both clients " +
                                 "presented event→research, 363 ms apart, exactly one diff cycle.";

            if (reference.Count != 2 || reference[0] != "UIStateGeoModal")
                yield return "every-peer-presents-in-the-same-order: the presented order was [" +
                             string.Join(",", reference) + "], not [UIStateGeoModal,UIStateGeoscapeEvent]. " +
                             "The HOST was the wrong peer in the field, so the host's own order is " +
                             "asserted explicitly and not merely compared with the clients'.";
        }

        private static byte[] Frame(uint pos, string family)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(family);
            var frame = new byte[4 + name.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(pos), 0, frame, 0, 4);
            Buffer.BlockCopy(name, 0, frame, 4, name.Length);
            return frame;
        }

        private static KeyValuePair<string, Func<int, IEnumerable<string>>> Pair(
            string name, Func<int, IEnumerable<string>> body) =>
            new KeyValuePair<string, Func<int, IEnumerable<string>>>(name, body);

        /// <summary>C-requirement: "seeded runs, reproducible from the seed alone". Two SimNets built from
        /// the same seed must deliver the same messages in the same order, and a different seed must be
        /// able to produce a different one — otherwise the harness is not simulating reordering at all and
        /// every later ordering property would be vacuously true.</summary>
        private static IEnumerable<string> SeededTransportIsReproducible(int seed)
        {
            var a = DeliveryOrder(seed);
            var b = DeliveryOrder(seed);
            if (!a.SequenceEqual(b))
                yield return "seeded-transport-is-reproducible: two runs of seed " + seed +
                             " delivered different orders (" + string.Join(",", a) + " vs " +
                             string.Join(",", b) + "). A run that is not a pure function of its seed " +
                             "cannot reproduce a failure from the seed alone.";

            bool anyDifferent = false;
            for (int other = seed + 1; other <= seed + 32 && !anyDifferent; other++)
                anyDifferent = !DeliveryOrder(other).SequenceEqual(a);
            if (!anyDifferent)
                yield return "seeded-transport-is-reproducible: 32 different seeds all produced the " +
                             "delivery order " + string.Join(",", a) + ". The transport is not reordering, " +
                             "so every ordering scenario in this harness would pass without proving " +
                             "anything.";
        }

        /// <summary>§A.6: the journal has NO cap and NO trim of any kind. Append past the runaway canary
        /// and assert that every single entry is still there and the append kept working. The canary is a
        /// LOG LINE, never a policy — the old QueueCap = 64 trimmed from the TAIL, i.e. dropped the
        /// NEWEST, which is the exact opposite of accumulating what the player has not looked at.</summary>
        private static IEnumerable<string> TheBacklogIsNeverTrimmed(int seed)
        {
            WindowJournal.Reset();
            const int n = WindowJournal.RunawayCanaryAt + 64;
            for (uint i = 1; i <= n; i++) WindowJournal.Append(i, "UIStateGeoModal", new byte[] { 1 });

            if (WindowJournal.UnreadCount != n)
                yield return "the-backlog-is-never-trimmed: appended " + n + " entries and the journal " +
                             "holds " + WindowJournal.UnreadCount + ". An entry is dropped ONLY by being " +
                             "read or by a host-minted void — never by a cap, a trim, a staleness sweep " +
                             "or an LRU. The " + WindowJournal.RunawayCanaryAt + " canary logs once and " +
                             "KEEPS APPENDING.";

            var head = WindowJournal.PeekHead();
            if (head == null || head.Pos != 1)
                yield return "the-backlog-is-never-trimmed: the head is " +
                             (head == null ? "<null>" : head.Pos.ToString()) + ", not 1. A trim that " +
                             "removed from the FRONT would drop the oldest unread window, which is the " +
                             "one the player is owed next.";

            uint last = 0;
            JournalEntry e;
            while (WindowJournal.TryRead(out e)) last = e.Pos;
            if (last != n)
                yield return "the-backlog-is-never-trimmed: the last entry drained was " + last +
                             ", not " + n + ". The shipped QueueCap = 64 trimmed from the TAIL — it " +
                             "dropped the NEWEST window, i.e. exactly the one that had just been raised.";
            WindowJournal.Reset();
        }

        /// <summary>PROPERTY (L555): every HOLE in the coverage tables is EARNED. Walk the real tables and
        /// assert that a Gap always names the payload class it rests on, that
        /// <c>GeoModalMirror.CanDescribeClass</c> refuses that class, and that no other verdict carries a
        /// witness. The reported defect was the opposite of all three at once: a Gap over a GeoSite with
        /// nothing but a paragraph behind it.</summary>
        private static IEnumerable<string> AHoleIsEarnedNotDeclared(int seed)
        {
            foreach (var kv in GeoWindowCoverage.DeclaredModals)
            {
                var rule = kv.Value;
                if (rule == null) { yield return "a-hole-is-earned-not-declared: modal '" + kv.Key + "' has a null rule."; continue; }
                if (rule.Sync != WindowSync.Gap)
                {
                    if (rule.GapDataClass != null)
                        yield return "a-hole-is-earned-not-declared: modal '" + kv.Key + "' is " + rule.Sync +
                                     " and still carries the hole witness '" + rule.GapDataClass.FullName +
                                     "'. A witness is evidence for a HOLE and reads as one wherever it sits.";
                    continue;
                }
                if (rule.GapDataClass == null)
                {
                    yield return "a-hole-is-earned-not-declared: modal '" + kv.Key + "' is a HOLE with no " +
                                 "payload class, so 'this cannot travel' is a sentence nothing re-checks.";
                    continue;
                }
                if (GeoModalMirror.CanDescribeClass(rule.GapDataClass))
                    yield return "a-hole-is-earned-not-declared: modal '" + kv.Key + "' is a HOLE over a '" +
                                 rule.GapDataClass.FullName + "', which the rail CAN name and Describe CAN " +
                                 "put on the wire. The window is being withheld by declaration alone — the " +
                                 "Pandoran-reveal defect.";
                if (GeoWindowCoverage.HoleAnnouncement(kv.Key.ToString(), rule)
                                     .IndexOf(rule.GapDataClass.Name, StringComparison.Ordinal) < 0 &&
                    rule.GapDataClass != typeof(void))
                    yield return "a-hole-is-earned-not-declared: the announcement for modal '" + kv.Key +
                                 "' never names its own witness, so the log says a window is missing without " +
                                 "saying what it is missing over.";
            }
        }

        /// <summary>PROPERTY (L555): the hole predicate is TOTAL and SAFE over the whole payload space, not
        /// a list of the windows this build happens to know. Every type in the two game assemblies plus a
        /// few synthetic ones must get a verdict without throwing; a rail ROOT must be describable and an
        /// unknown third-party class must not — the loud default, since an undescribable window is announced
        /// as a hole rather than mirrored into an empty prefab.</summary>
        private static IEnumerable<string> TheHolePredicateClassifiesAnything(int seed)
        {
            var probes = new List<Type>
            {
                null, typeof(void), typeof(object), typeof(string), typeof(int), typeof(Scenarios),
                typeof(PhoenixPoint.Geoscape.Entities.GeoSite),
                typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle),
                typeof(PhoenixPoint.Geoscape.Entities.GeoCharacter),
                typeof(PhoenixPoint.Geoscape.Entities.GeoMission),
            };
            var yes = new HashSet<Type> { probes[6], probes[7], probes[8], probes[9] };
            foreach (var t in probes)
            {
                bool verdict = false;
                string threw = null;
                try { verdict = GeoModalMirror.CanDescribeClass(t); }
                catch (Exception ex) { threw = ex.GetType().Name; }
                if (threw != null)
                {
                    yield return "the-hole-predicate-classifies-anything: CanDescribeClass threw on '" +
                                 (t == null ? "<null>" : t.FullName) + "' (" + threw + "). A class this " +
                                 "build has never seen must get a VERDICT, not an exception.";
                    continue;
                }
                if (verdict != yes.Contains(t))
                    yield return "the-hole-predicate-classifies-anything: CanDescribeClass('" +
                                 (t == null ? "<null>" : t.FullName) + "') = " + verdict + ", expected " +
                                 yes.Contains(t) + ". A predicate that has gone constant makes every hole " +
                                 "legal (constant-true) or every hole illegal (constant-false), and both " +
                                 "read as a green law.";
            }
            foreach (var t in GeoModalMirror.DescribedClasses)
                if (!GeoModalMirror.CanDescribeClass(t))
                    yield return "the-hole-predicate-classifies-anything: Describe has a case for '" +
                                 t.FullName + "' but CanDescribeClass refuses it, so the host can ship a " +
                                 "payload the hole predicate believes cannot travel.";
        }

        /// <summary>Send 8 numbered messages to peer 1 at t=0, let the clock run past the delay ceiling,
        /// and report the order they came out in.</summary>
        private static List<int> DeliveryOrder(int seed)
        {
            var clock = new SimClock();
            var net = new SimNet(seed, clock);
            for (int i = 0; i < 8; i++) net.Send(1, new[] { (byte)i });
            clock.Advance(1.0f);
            return net.Drain().Select(kv => (int)kv.Value[0]).ToList();
        }
    }
}
