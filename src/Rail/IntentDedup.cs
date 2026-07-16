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
    /// derives from this. The geoscape action-intent surface (GeoIntent 0xA2) also uses this shared dedup after
    /// the envelope cutover retired the legacy (peerId, nonce)-keyed RequestDedup it previously used.
    /// </summary>
    public class IntentDedup
    {
        private readonly int _capacity;
        private readonly HashSet<(ulong, ulong)> _seen = new HashSet<(ulong, ulong)>();
        private readonly Queue<(ulong, ulong)> _order = new Queue<(ulong, ulong)>();

        public IntentDedup(int capacity = 512) { _capacity = capacity < 16 ? 16 : capacity; }

        private static (ulong, ulong) Key(ulong peerId, ushort surfaceId, uint nonce)
            => (peerId, ((ulong)surfaceId << 32) | nonce);

        /// <summary>True the FIRST time a (peer,surface,nonce) is offered; false on any repeat (drop it).</summary>
        public bool IsNew(ulong peerId, ushort surfaceId, uint nonce)
        {
            var k = Key(peerId, surfaceId, nonce);
            if (_seen.Contains(k)) return false;
            _seen.Add(k);
            _order.Enqueue(k);
            if (_order.Count > _capacity) _seen.Remove(_order.Dequeue());
            return true;
        }

        public void Reset()
        {
            _seen.Clear();
            _order.Clear();
        }

        /// <summary>
        /// Drop ONE peer's remembered window, leaving every other peer's intact. Rejoin case (rca-3 audit b):
        /// the peer id is the STABLE Steam id, so a client that disconnects and rejoins mid-session comes back
        /// with the SAME peerId but a FRESH engine whose client-local nonce counter restarts at 1 — without
        /// this, its own pre-rejoin (peer, surface, nonce) entries silently eat its first post-join intents.
        /// Per-peer (not <see cref="Reset"/>) so a straddling reliable double-send from a still-connected
        /// OTHER client can never re-apply.
        /// </summary>
        public void ResetPeer(ulong peerId)
        {
            if (_seen.RemoveWhere(k => k.Item1 == peerId) == 0) return;
            var kept = new List<(ulong, ulong)>(_order.Count);
            foreach (var k in _order)
                if (k.Item1 != peerId) kept.Add(k);
            _order.Clear();
            foreach (var k in kept) _order.Enqueue(k);
        }
    }
}
