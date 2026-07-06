using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) client-side occurrence-id dedup for the REPORT rail (Batch-3 P5): the host stamps
    /// every 0x69 ReportModalShow / 0x6C ReportModalHide with a monotonic occId
    /// (<see cref="DisplaySequence.NextReportOccId"/>, the same pattern as <c>EventOccurrenceIds</c> gives
    /// 0x65/0x66), and this set makes the STUN reliable transport's deliberate send-twice an idempotent no-op —
    /// closing the last un-deduped display rail. occId 0 is the LEGACY/no-id sentinel and is never deduped
    /// (fail-open for an unstamped wire); the byte-level <see cref="ReportOutcomeDedup"/> stays as the belt for
    /// exactly that legacy case. Hard-bounded FIFO (same shape as EventCorrelator's completed tracking) so it
    /// can never leak; reset at the save-transfer boundary (<c>SyncEngine.ResetEventMirror</c>).
    /// </summary>
    public sealed class ReportOccurrenceDedup
    {
        /// <summary>Max tracked ids; the oldest is evicted past this. Wraparound can't alias a live in-flight id.</summary>
        public const int MaxTracked = 64;

        private readonly HashSet<ushort> _seen = new HashSet<ushort>();
        private readonly Queue<ushort> _seenOrder = new Queue<ushort>();

        /// <summary>
        /// True iff <paramref name="occId"/> was already delivered (→ caller drops the duplicate). A first
        /// delivery records the id and returns false. occId 0 (legacy unstamped wire) is never deduped.
        /// </summary>
        public bool SeenBefore(ushort occId)
        {
            if (occId == 0) return false;
            if (!_seen.Add(occId)) return true;
            _seenOrder.Enqueue(occId);
            while (_seenOrder.Count > MaxTracked)
                _seen.Remove(_seenOrder.Dequeue());
            return false;
        }

        /// <summary>Boundary reset (save-transfer / reload): forget every delivered id.</summary>
        public void Reset()
        {
            _seen.Clear();
            _seenOrder.Clear();
        }
    }
}
