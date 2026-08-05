using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// SHARED intent de-duplicator (unified backbone spec §2.2, "ONE intent Dedup"). The reliable transport
    /// can double-send a client intent envelope; a double-applied intent would mutate twice. Keyed by the
    /// intent's (peerId, surfaceId, nonce) — the peer discriminator handles that client nonces are client-LOCAL
    /// monotonic counters, so with 2+ clients both emit nonce 1,2,3… on the same surface and a (surfaceId, nonce)-
    /// only key would silently drop the later client's intents. A bounded ring drops the oldest so memory stays
    /// flat over a long session. PURE (no engine types) → unit-tested.
    ///
    /// Lifted verbatim from the tactical-only TacticalIntentDedup (capacity floor 16); TacticalIntentDedup now
    /// derives from this. ALL geoscape intent surfaces share the ONE instance owned by <see cref="IntentRail"/>,
    /// keyed per-peer (the surfaceId in the key keeps families apart).
    /// </summary>
    public class IntentDedup
    {
        private readonly int _capacity;
        // ONE RING PER PEER (N=50 mandate). The ring used to be global: 512 entries shared by everyone,
        // which at 50 players is a ~10-intent memory per peer. A retransmit older than ten of that peer's
        // own clicks then read as NEW and re-applied — a double-spend of a click, on the reliable
        // transport's own doing, and the busier the session the shorter the window got. Per-peer rings
        // make the window a property of the peer instead of the roster: every player keeps a full
        // _capacity of history no matter how many others are talking.
        private readonly Dictionary<ulong, Ring> _peers = new Dictionary<ulong, Ring>();

        private sealed class Ring
        {
            internal readonly HashSet<ulong> Seen = new HashSet<ulong>();
            internal readonly Queue<ulong> Order = new Queue<ulong>();
        }

        public IntentDedup(int capacity = 512) { _capacity = capacity < 16 ? 16 : capacity; }

        private static ulong Key(ushort surfaceId, uint nonce) => ((ulong)surfaceId << 32) | nonce;

        /// <summary>True the FIRST time a (peer,surface,nonce) is offered; false on any repeat (drop it).</summary>
        public bool IsNew(ulong peerId, ushort surfaceId, uint nonce)
        {
            if (!_peers.TryGetValue(peerId, out var ring)) _peers[peerId] = ring = new Ring();
            var k = Key(surfaceId, nonce);
            if (ring.Seen.Contains(k)) return false;
            ring.Seen.Add(k);
            ring.Order.Enqueue(k);
            if (ring.Order.Count > _capacity) ring.Seen.Remove(ring.Order.Dequeue());
            return true;
        }

        public void Reset() => _peers.Clear();

        /// <summary>
        /// Drop ONE peer's remembered window, leaving every other peer's intact. Rejoin case (rca-3 audit b):
        /// the peer id is the STABLE Steam id, so a client that disconnects and rejoins mid-session comes back
        /// with the SAME peerId but a FRESH engine whose client-local nonce counter restarts at 1 — without
        /// this, its own pre-rejoin (peer, surface, nonce) entries silently eat its first post-join intents.
        /// Per-peer (not <see cref="Reset"/>) so a straddling reliable double-send from a still-connected
        /// OTHER client can never re-apply.
        /// </summary>
        public void ResetPeer(ulong peerId) => _peers.Remove(peerId);
    }
}
