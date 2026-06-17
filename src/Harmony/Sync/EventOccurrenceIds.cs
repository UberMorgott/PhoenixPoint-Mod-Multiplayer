using System.Runtime.CompilerServices;

namespace Multipleer.Harmony.Sync
{
    /// <summary>
    /// HOST-side authority that assigns a unique, monotonic per-occurrence id to each raised
    /// <c>GeoscapeEvent</c> instance, so the wire can disambiguate two occurrences that share the same
    /// reusable <c>GeoscapeEvent.EventID</c> def-name. There is NO native stable per-occurrence id (verified
    /// against the decompile 2026-06-17: <c>GeoscapeEvent</c> exposes only <c>EventID</c> + a readonly
    /// <c>Context</c> + a <c>GeoscapeEventRecord</c> whose own <c>EventId</c> is the same def-name and whose
    /// trigger fields mutate across re-triggers), so the host synthesizes one here.
    ///
    /// The id maps across raise→dismiss for the SAME occurrence because the SAME live <c>GeoscapeEvent</c>
    /// instance flows through both host chokepoints: the raise postfix patches
    /// <c>GeoscapeView.OnGeoscapeEventRaised(GeoscapeEvent)</c> (the raised instance) and the dismiss postfix
    /// patches <c>GeoscapeEvent.CompleteEvent(...)</c> (<c>__instance</c> = that same instance). We key a
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> on that instance so the dismiss retrieves the exact id
    /// the raise assigned. The CWT never roots the event (GC-friendly: a never-dismissed event's entry is
    /// collected with the event).
    ///
    /// Pure C# (no Unity/Harmony types) → unit-testable. The counter is a plain monotonic field (deterministic;
    /// never time/Random based). It is a <see cref="ushort"/> on the wire: wraparound after 65535 raises in one
    /// session is harmless — the id only needs to disambiguate the few occurrences in flight/open at once, and
    /// the client's out-of-order buffer is hard-bounded, so a wrapped id can never alias a still-live one.
    /// </summary>
    public static class EventOccurrenceIds
    {
        private static readonly object _lock = new object();
        private static readonly ConditionalWeakTable<object, object> _ids = new ConditionalWeakTable<object, object>();
        private static ushort _counter;   // monotonic; 0 is a valid id (first Assign returns 1 after pre-increment? — see Next)

        /// <summary>
        /// Assign a FRESH occurrence id to <paramref name="geoEvent"/> (called from the host raise postfix),
        /// overwriting any prior mapping for that instance. Returns the new id. Null event → 0 (never broadcast).
        /// </summary>
        public static ushort Assign(object geoEvent)
        {
            if (geoEvent == null) return 0;
            lock (_lock)
            {
                ushort id = Next();
                // Overwrite any stale mapping for a re-used instance (CWT has no indexer set; remove+add).
                _ids.Remove(geoEvent);
                _ids.Add(geoEvent, id);
                return id;
            }
        }

        /// <summary>
        /// Return the occurrence id previously assigned to <paramref name="geoEvent"/> (called from the host
        /// dismiss postfix); if none was assigned (e.g. an answer applied for an instance we never saw raised),
        /// assign a fresh one so the dismiss still carries a unique id. Null event → 0.
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

        /// <summary>Test/teardown hook: reset the monotonic counter (the CWT self-empties as events are GC'd).</summary>
        public static void ResetForTests()
        {
            lock (_lock) { _counter = 0; }
        }

        // Monotonic next id. Skips 0 so 0 stays the "null/none" sentinel used by Assign/GetOrAssign on a null
        // event; wraps ushort.MaxValue → 1 (never back to 0).
        private static ushort Next()
        {
            _counter = _counter == ushort.MaxValue ? (ushort)1 : (ushort)(_counter + 1);
            return _counter;
        }
    }
}
