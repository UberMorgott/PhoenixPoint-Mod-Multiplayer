using System.Collections.Generic;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// HOST-side first-claim-wins arbitration for geoscape-event choices. NOTE: currently NOT wired into the live
    /// event-choice path (user directive: choices are un-gated, last-write-wins; the native CompleteEvent self-guards
    /// on IsCompleted so a double-resolve is a safe no-op). This class + its tests are KEPT for when the
    /// permission/turn system is re-enabled. When wired, the host accepts the FIRST claim per occurrence id (runs the
    /// authoritative <c>CompleteEvent</c> + broadcasts the outcome) and IGNORES every later claim — so a
    /// near-simultaneous double-click from
    /// both instances converges on a single roll/apply. The client never applies an outcome itself.
    ///
    /// Pure C# (no Unity/Harmony types) → unit-testable. The resolved set is FIFO-bounded so it can never leak
    /// across a long session; occurrence id 0 is the "null/none" sentinel (see <c>EventOccurrenceIds</c>) and
    /// is never a valid claim. Aging out an old resolved id is harmless: that occurrence's event is long gone,
    /// and the host id allocator never reissues a live id while it is still in flight.
    /// </summary>
    public sealed class ChoiceArbiter
    {
        /// <summary>Max resolved occurrence ids tracked; the oldest is evicted past this so the set can't leak.</summary>
        public const int MaxResolvedTracked = 256;

        private readonly object _lock = new object();
        private readonly HashSet<ushort> _resolved = new HashSet<ushort>();
        private readonly Queue<ushort> _order = new Queue<ushort>();

        /// <summary>Resolved-occurrence count (diagnostics/tests).</summary>
        public int ResolvedCount { get { lock (_lock) { return _resolved.Count; } } }

        /// <summary>
        /// Register a claim for <paramref name="occurrenceId"/>. Returns true the FIRST time an occurrence is
        /// claimed (caller resolves + broadcasts), false for any later claim of an already-resolved occurrence
        /// (caller ignores it). occId 0 (null sentinel) is never accepted.
        /// </summary>
        public bool Claim(ushort occurrenceId)
        {
            if (occurrenceId == 0) return false;
            lock (_lock)
            {
                if (_resolved.Contains(occurrenceId)) return false;
                _resolved.Add(occurrenceId);
                _order.Enqueue(occurrenceId);
                while (_order.Count > MaxResolvedTracked)
                    _resolved.Remove(_order.Dequeue());
                return true;
            }
        }

        /// <summary>Test/teardown hook: forget all resolved occurrences.</summary>
        public void ResetForTests()
        {
            lock (_lock) { _resolved.Clear(); _order.Clear(); }
        }
    }
}
