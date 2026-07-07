using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// HOST-side buffer for CLIENT single-choice PROMPT advance requests (<c>EventAdvanceRequest</c> 0x6B) that
    /// arrived BEFORE the host actually displayed that occurrence's window-1 prompt.
    ///
    /// Root cause it closes (log-confirmed 2026-07-07, occId=1 PROG_PX12): the host broadcasts the event RAISE at
    /// trigger time, but its OWN native dialog is queued behind a higher-priority display (a cutscene / report
    /// modal) in the native view-switch queue, so <c>UIModuleSiteEncounters._geoEvent</c> is still null when the
    /// client — which mirrors + shows the prompt sooner — clicks and relays the advance.
    /// <c>TryHostNativeAdvanceSingleChoice</c> then finds nothing showing (<c>showingThisOcc=False</c>) and drops
    /// the request; the client already local-closed its prompt, so window-2 never appears unless the host player
    /// independently clicks. Buffering the request and replaying it when the host finally shows the prompt makes
    /// the client's click advance the flow for everyone (host player click no longer required).
    ///
    /// Symmetric to the client-side <c>EventCorrelator._pendingAdvance</c> (an advance that beats its raise).
    /// Bounded FIFO so an occurrence the host never shows (collected / dismissed another way) can never leak.
    /// Pure C# (no Unity/Harmony types) → unit-testable; HOST-only in production.
    /// </summary>
    public static class PendingHostAdvance
    {
        /// <summary>Max buffered advances; the oldest is evicted past this so the buffer can't leak.</summary>
        public const int MaxTracked = 16;

        private static readonly object _lock = new object();
        private static readonly Dictionary<ushort, string> _pending = new Dictionary<ushort, string>();
        private static readonly Queue<ushort> _order = new Queue<ushort>();

        /// <summary>Buffer (or refresh) a client advance for <paramref name="occId"/> to replay when the host shows it.</summary>
        public static void Buffer(ushort occId, string eventId)
        {
            if (occId == 0) return;
            lock (_lock)
            {
                if (!_pending.ContainsKey(occId))
                {
                    _order.Enqueue(occId);
                    while (_order.Count > MaxTracked)
                        _pending.Remove(_order.Dequeue());
                }
                _pending[occId] = eventId ?? "";
            }
        }

        /// <summary>True iff a client advance is buffered for <paramref name="occId"/> (returns its eventId).</summary>
        public static bool TryGet(ushort occId, out string eventId)
        {
            eventId = null;
            if (occId == 0) return false;
            lock (_lock) { return _pending.TryGetValue(occId, out eventId); }
        }

        /// <summary>Drop the buffered advance for <paramref name="occId"/> (resolved or given up).</summary>
        public static void Remove(ushort occId)
        {
            if (occId == 0) return;
            lock (_lock)
            {
                if (!_pending.Remove(occId)) return;
                // Lazily prune the FIFO token so the queue stays tight (stale tokens are also skipped on eviction).
                int n = _order.Count;
                for (int i = 0; i < n; i++)
                {
                    var id = _order.Dequeue();
                    if (id != occId) _order.Enqueue(id);
                }
            }
        }

        /// <summary>Buffered count (diagnostics/tests).</summary>
        public static int Count { get { lock (_lock) return _pending.Count; } }

        /// <summary>Forget all buffered advances (session teardown / reload boundary).</summary>
        public static void Reset()
        {
            lock (_lock) { _pending.Clear(); _order.Clear(); }
        }
    }
}
