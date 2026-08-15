using System;
using System.Collections.Generic;
using System.Linq;
using Base.Serialization;
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
