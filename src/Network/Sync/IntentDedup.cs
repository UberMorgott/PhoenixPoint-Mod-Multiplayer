using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// SHARED intent de-duplicator (unified backbone spec §2.2, "ONE intent Dedup"). The reliable transport
    /// can double-send a client intent envelope; a double-applied intent would mutate twice. Keyed by the
    /// intent's (surfaceId, nonce); a bounded ring drops the oldest so memory stays flat over a long session.
    /// PURE (no engine types) → unit-tested.
    ///
    /// Lifted verbatim from the tactical-only TacticalIntentDedup (capacity floor 16); TacticalIntentDedup now
    /// derives from this. NOTE: the geoscape RequestDedup is keyed by (peerId, nonce) — a DIFFERENT abstraction
    /// tied to the legacy action relay — and is left untouched here; it is retired in a later slice when the
    /// geoscape intent surface adopts this shared (surfaceId, nonce) dedup.
    /// </summary>
    public class IntentDedup
    {
        private readonly int _capacity;
        private readonly HashSet<ulong> _seen = new HashSet<ulong>();
        private readonly Queue<ulong> _order = new Queue<ulong>();

        public IntentDedup(int capacity = 512) { _capacity = capacity < 16 ? 16 : capacity; }

        private static ulong Key(ushort surfaceId, uint nonce) => ((ulong)surfaceId << 32) | nonce;

        /// <summary>True the FIRST time a (surface,nonce) is offered; false on any repeat (drop it).</summary>
        public bool IsNew(ushort surfaceId, uint nonce)
        {
            ulong k = Key(surfaceId, nonce);
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
    }
}
