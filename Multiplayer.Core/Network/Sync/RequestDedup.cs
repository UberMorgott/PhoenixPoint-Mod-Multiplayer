using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Host-side inbound-request dedup: the reliable transport deliberately sends every reliable packet
    /// TWICE (StunTransport.Send/Broadcast duplicate for reliability), so each client ActionRequest arrives
    /// twice and would otherwise be applied twice on the authority (double manufacture / answer / construct).
    /// We key by (peerId, nonce) — the client's per-request correlation id — and report the first sighting
    /// as new and every repeat as a duplicate. Bounded FIFO so the set can't grow without limit over a long
    /// session; the oldest key is evicted once the cap is exceeded.
    /// Not thread-safe — driven from the single packet-routing thread, same as the rest of SyncEngine.
    /// </summary>
    public sealed class RequestDedup
    {
        private readonly int _capacity;
        private readonly HashSet<long> _seen;
        private readonly Queue<long> _order;

        public RequestDedup(int capacity = 512)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _seen = new HashSet<long>();
            _order = new Queue<long>(_capacity);
        }

        /// <summary>True if this (peerId, nonce) was already seen; otherwise records it and returns false.</summary>
        public bool IsDuplicate(ulong peerId, uint nonce)
        {
            long key = unchecked((long)((peerId << 32) ^ nonce));
            if (!_seen.Add(key)) return true;   // already present → duplicate
            _order.Enqueue(key);
            while (_order.Count > _capacity)
                _seen.Remove(_order.Dequeue());
            return false;
        }
    }
}
