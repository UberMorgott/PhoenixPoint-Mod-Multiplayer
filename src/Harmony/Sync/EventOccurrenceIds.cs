using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Multipleer.Harmony.Sync
{
    /// <summary>
    /// HOST-side authority that assigns a unique, monotonic per-occurrence id to each <c>GeoscapeEvent</c>
    /// instance, so the wire can disambiguate two occurrences that share the same reusable
    /// <c>GeoscapeEvent.EventID</c> def-name. There is NO native stable per-occurrence id (verified against the
    /// decompile 2026-06-17: <c>GeoscapeEvent</c> exposes only <c>EventID</c> + a readonly <c>Context</c> + a
    /// <c>GeoscapeEventRecord</c> whose own <c>EventId</c> is the same def-name and whose trigger fields mutate
    /// across re-triggers), so the host synthesizes one here.
    ///
    /// The id maps across raise↔dismiss for the SAME occurrence because the SAME live <c>GeoscapeEvent</c>
    /// instance flows through both host chokepoints — and crucially in EITHER order. For a single-choice event
    /// the host AUTO-COMPLETES it at trigger time BEFORE raising it for display
    /// (<c>GeoscapeEventSystem.OnEventTriggered</c>: <c>new GeoscapeEvent(...)</c> → <c>CompleteEvent(...)</c>
    /// at :659 → <c>GeoscapeEventRaised.Invoke(...)</c> at :661, all on ONE instance), so the dismiss postfix
    /// can run BEFORE the raise postfix. Both call <see cref="GetOrAssign"/> keyed on that instance via a
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/>, so whichever runs first allocates the id and the other
    /// REUSES it — the id is stable per occurrence regardless of order. (A previous <c>Assign</c> that always
    /// overwrote on the raise produced a 1-vs-2 id split for exactly this auto-complete-then-raise case and is
    /// removed.) Each trigger builds a fresh instance, so one instance == one occurrence == one id; the CWT
    /// never roots the event (GC-friendly).
    ///
    /// Also tracks which occurrence ids the host already broadcast an authoritative dismiss for, so the
    /// <c>FinishEncounterHostDismissPatch</c> fallback (host clicks OK on its own modal) does not send a SECOND,
    /// bare close-only dismiss for an occurrence whose result-bearing dismiss already went out
    /// (<c>CompleteEventDismissPatch</c>) — avoiding a double-dismiss on the client.
    ///
    /// Pure C# (no Unity/Harmony types) → unit-testable. The counter is a plain monotonic field (deterministic;
    /// never time/Random based). It is a <see cref="ushort"/> on the wire: wraparound after 65535 occurrences in
    /// one session is harmless — the id only needs to disambiguate the few occurrences in flight/open at once,
    /// and the client's out-of-order buffer is hard-bounded, so a wrapped id can never alias a still-live one.
    /// </summary>
    public static class EventOccurrenceIds
    {
        private static readonly object _lock = new object();
        private static readonly ConditionalWeakTable<object, object> _ids = new ConditionalWeakTable<object, object>();
        private static ushort _counter;   // monotonic; 0 stays the "null/none" sentinel (Next skips it)

        // Occurrence ids that already had an authoritative dismiss broadcast (CompleteEventDismissPatch). FIFO,
        // hard-bounded so it can't leak. The FinishEncounter fallback checks this to avoid a double-dismiss.
        private const int MaxDismissedTracked = 64;
        private static readonly HashSet<ushort> _dismissed = new HashSet<ushort>();
        private static readonly Queue<ushort> _dismissedOrder = new Queue<ushort>();

        /// <summary>
        /// Return the occurrence id mapped to <paramref name="geoEvent"/>, allocating a fresh monotonic one the
        /// first time the instance is seen and REUSING it on every later call (order-independent: raise-first or
        /// dismiss-first both converge on the same id). Null event → 0 (never broadcast).
        /// </summary>
        public static ushort GetOrAssign(object geoEvent)
        {
            if (geoEvent == null) return 0;
            lock (_lock)
            {
                if (_ids.TryGetValue(geoEvent, out var boxed) && boxed is ushort existing)
                    return existing;
                ushort id = Next();
                _ids.Add(geoEvent, id);
                return id;
            }
        }

        /// <summary>Host: record that an authoritative dismiss was broadcast for <paramref name="occurrenceId"/>.</summary>
        public static void MarkDismissed(ushort occurrenceId)
        {
            if (occurrenceId == 0) return;
            lock (_lock)
            {
                if (_dismissed.Add(occurrenceId))
                {
                    _dismissedOrder.Enqueue(occurrenceId);
                    while (_dismissedOrder.Count > MaxDismissedTracked)
                        _dismissed.Remove(_dismissedOrder.Dequeue());
                }
            }
        }

        /// <summary>Host: true iff an authoritative dismiss was already broadcast for <paramref name="occurrenceId"/>.</summary>
        public static bool WasDismissed(ushort occurrenceId)
        {
            if (occurrenceId == 0) return false;
            lock (_lock) { return _dismissed.Contains(occurrenceId); }
        }

        /// <summary>Test/teardown hook: reset the counter + dismissed tracking (the CWT self-empties as events GC).</summary>
        public static void ResetForTests()
        {
            lock (_lock) { _counter = 0; _dismissed.Clear(); _dismissedOrder.Clear(); }
        }

        // Monotonic next id. Skips 0 so 0 stays the "null/none" sentinel used on a null event; wraps
        // ushort.MaxValue → 1 (never back to 0).
        private static ushort Next()
        {
            _counter = _counter == ushort.MaxValue ? (ushort)1 : (ushort)(_counter + 1);
            return _counter;
        }
    }
}
