using System;
using System.Collections.Generic;
using Multiplayer.Network;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>
    /// Inc5 part 2 — returning-peer reconnect: pins the pure returning-vs-new decision
    /// (SessionLifecycle.StaleRejoinPeers) the host's JOIN handler uses to prune a dead connection's
    /// session residue (roster entry, intent-dedup window, save-transfer per-peer state) before the
    /// returning peer rides the normal on-demand join path. The dedup-window drop itself is pinned by
    /// IntentDedupTests.ResetPeer_*; the roster removal by the SessionManager wiring (in-game path).
    /// </summary>
    public class StaleRejoinPeersTests
    {
        private static readonly Guid GuidA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid GuidB = Guid.Parse("22222222-2222-2222-2222-222222222222");

        private static List<KeyValuePair<ulong, Guid>> Roster(params (ulong id, Guid guid)[] peers)
        {
            var list = new List<KeyValuePair<ulong, Guid>>();
            foreach (var (id, guid) in peers)
                list.Add(new KeyValuePair<ulong, Guid>(id, guid));
            return list;
        }

        // ─── NEW peer: nothing to prune ──────────────────────────────────────

        [Fact]
        public void NewPeer_EmptyRoster_NothingToPrune()
        {
            Assert.Empty(SessionLifecycle.StaleRejoinPeers(Roster(), GuidA));
        }

        [Fact]
        public void NewPeer_OtherIdentitiesConnected_NothingToPrune()
        {
            var stale = SessionLifecycle.StaleRejoinPeers(Roster((8UL, GuidB)), GuidA);
            Assert.Empty(stale);
        }

        [Fact]
        public void NewPeer_OwnPreJoinPlaceholderEntry_NotMatched()
        {
            // OnPeerConnected adds the joiner to the roster BEFORE its JOIN arrives, with an unbound
            // (empty) identity — a first-time joiner must never prune its own placeholder.
            var stale = SessionLifecycle.StaleRejoinPeers(Roster((7UL, Guid.Empty)), GuidA);
            Assert.Empty(stale);
        }

        [Fact]
        public void EmptyJoiningGuid_NeverMatches_EvenEmptyBoundEntries()
        {
            // Defense-in-depth: an empty identity is rejected upstream; even if it reached the
            // decision it must match nothing (incl. other peers' unbound placeholders).
            var stale = SessionLifecycle.StaleRejoinPeers(
                Roster((7UL, Guid.Empty), (8UL, GuidB)), Guid.Empty);
            Assert.Empty(stale);
        }

        [Fact]
        public void NullRoster_NothingToPrune()
        {
            Assert.Empty(SessionLifecycle.StaleRejoinPeers(null, GuidA));
        }

        // ─── RETURNING peer: dead residue pruned ─────────────────────────────

        [Fact]
        public void ReturningPeer_DifferentTransportId_OldEntryPruned()
        {
            // Crash + reconnect over a new address: old peer 7 (same identity) is dead residue;
            // the unrelated peer 8 is untouched (others keep playing).
            var stale = SessionLifecycle.StaleRejoinPeers(
                Roster((7UL, GuidA), (8UL, GuidB)), GuidA);
            Assert.Equal(new[] { 7UL }, stale);
        }

        [Fact]
        public void ReturningPeer_SameTransportId_OwnOldEntryPruned()
        {
            // Steam reconnect reuses the stable peer id: the OLD entry under the SAME id is still
            // dead-session residue (stale ready flag/heartbeat) — pruned, then re-added fresh by JOIN.
            var stale = SessionLifecycle.StaleRejoinPeers(Roster((7UL, GuidA)), GuidA);
            Assert.Equal(new[] { 7UL }, stale);
        }

        [Fact]
        public void DoubleReconnectRace_AllStaleEntriesPruned()
        {
            // Two rapid reconnects can momentarily leave TWO dead entries bound to one identity —
            // both are residue.
            var stale = SessionLifecycle.StaleRejoinPeers(
                Roster((7UL, GuidA), (9UL, GuidA), (8UL, GuidB)), GuidA);
            Assert.Equal(new[] { 7UL, 9UL }, stale);
        }

        [Fact]
        public void PruneIsIdempotent_SecondPassAfterRemovalIsEmpty()
        {
            // Idempotence: once the caller removed the returned ids, re-deciding yields nothing —
            // a double-reconnect race prunes cleanly twice.
            var roster = Roster((7UL, GuidA), (8UL, GuidB));
            var first = SessionLifecycle.StaleRejoinPeers(roster, GuidA);
            Assert.Equal(new[] { 7UL }, first);

            roster.RemoveAll(p => first.Contains(p.Key));
            Assert.Empty(SessionLifecycle.StaleRejoinPeers(roster, GuidA));
        }
    }
}
