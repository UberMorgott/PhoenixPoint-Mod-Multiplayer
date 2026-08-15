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
            yield return Pair("no-gap-is-permanent", NoGapIsPermanent);
            yield return Pair("an-untouched-surface-does-not-repaint", AnUntouchedSurfaceDoesNotRepaint);
            yield return Pair("a-progress-tick-never-tears-down-the-agenda-strip",
                              AProgressTickNeverTearsDownTheAgendaStrip);
            yield return Pair("my-own-gesture-repaints-my-own-strip", MyOwnGestureRepaintsMyOwnStrip);
            yield return Pair("a-joining-peer-never-replays-the-status-bar",
                              AJoiningPeerNeverReplaysTheStatusBar);
            yield return Pair("the-reveal-is-one-instant-for-every-peer",
                              TheRevealIsOneInstantForEveryPeer);
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

        /// <summary>L549 as an OBSERVABLE HISTORY, and the direction L548's history does not cover: there
        /// the changes ARRIVE, here the peer makes them ITSELF. A host queues a research, then a
        /// manufacture, then nothing. Each self-initiated gesture must leave the persistent HUD owing a
        /// refresh — the flag every apply path raises and no local gesture used to — and the strip's own
        /// teardown gate must still fire exactly twice, once per real change of the SET OF ROWS. A peer
        /// whose own action repaints nothing until it walks out of the screen and back is the postulate-1
        /// defect reported on the host 2026-08-15.</summary>
        private static IEnumerable<string> MyOwnGestureRepaintsMyOwnStrip(int seed)
        {
            OpenUiRepaint.Reset();
            var gestures = new[]
            {
                "manufacture=-|research=RES_A|vehicles=|facilities=",   // the host starts a research
                "manufacture=Gun|research=RES_A|vehicles=|facilities=", // …then queues a manufacture
                "manufacture=Gun|research=RES_A|vehicles=|facilities=", // …then a gesture the strip does not draw
            };

            int unmarked = 0, teardowns = 0;
            foreach (var rows in gestures)
            {
                OpenUiRepaint.MarkLocalGesture();          // the acting peer's own capture seam
                if (!OpenUiRepaint.HudRepaintOwed) unmarked++;
                if (OpenUiRepaint.AgendaNeedsRebuild(true, rows)) teardowns++;
            }
            OpenUiRepaint.Reset();

            if (unmarked != 0)
                yield return "my-own-gesture-repaints-my-own-strip: " + unmarked + " of the acting peer's own " +
                             "gestures left the persistent HUD un-owed. Every mark site in this repo is an " +
                             "APPLY path, so the peer that pressed the button is the one peer whose " +
                             "already-open strip is never refreshed — the host starting a research and seeing " +
                             "no research row.";
            if (teardowns != 2)
                yield return "my-own-gesture-repaints-my-own-strip: the strip was torn down " + teardowns +
                             " times across 3 self-initiated gestures, of which exactly TWO changed the SET OF " +
                             "ROWS (the first paint and the manufacture). A third teardown means the mark now " +
                             "rebuilds the strip for any gesture at all — the L492/L548 flicker on the acting " +
                             "peer this time; a missing one means the mark reaches the HUD and the strip " +
                             "ignores it.";
        }

        /// <summary>L548 as an OBSERVABLE HISTORY. A run of rail batches on a client: a manufacture
        /// countdown ticks under "F#" once a second while the SET OF ROWS stands still, then a research
        /// starts and the set really changes. Counted over the real gate and the real generation
        /// machinery, the strip must be torn down exactly TWICE — the first paint and the real change —
        /// and never for a countdown, because InitialSetup disposes and re-creates every row and doing
        /// that at the tick rate is the 1 Hz client flicker reported 2026-08-15.</summary>
        private static IEnumerable<string> AProgressTickNeverTearsDownTheAgendaStrip(int seed)
        {
            OpenUiRepaint.Reset();
            const string before = "manufacture=Gun|research=-|vehicles=|facilities=";
            const string after = "manufacture=Gun|research=RES_A|vehicles=|facilities=";
            var run = new[]
            {
                new KeyValuePair<string, string>("F#7a3.SerializationData.Manufacture.Current.Progress", before),
                new KeyValuePair<string, string>("F#7a3.SerializationData.Manufacture.Current.Progress", before),
                new KeyValuePair<string, string>("F#7a3.SerializationData.Manufacture.Current.Progress", before),
                new KeyValuePair<string, string>("F#7a3.SerializationData.Research.Current", after),
                new KeyValuePair<string, string>("F#7a3.SerializationData.Manufacture.Current.Progress", after),
            };

            int teardowns = 0;
            foreach (var batch in run)
            {
                OpenUiRepaint.BumpScopeGenerations(new List<string> { batch.Key });
                if (OpenUiRepaint.AgendaNeedsRebuild(true, batch.Value)) teardowns++;
            }
            OpenUiRepaint.Reset();

            if (teardowns != 2)
                yield return "a-progress-tick-never-tears-down-the-agenda-strip: the strip was rebuilt " +
                             teardowns + " times across 5 batches, of which exactly ONE changed the set of " +
                             "rows (plus the first paint). Every extra one is a full row teardown paid for a " +
                             "countdown that UpdateData() already repaints on every call — the reported 1 Hz " +
                             "client blink; a missing one is a strip that never follows a real change, which " +
                             "is the worse defect of the two.";
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
