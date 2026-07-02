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

        // Occurrence ids whose single-choice PROMPT already advanced to its RESULT page on the host
        // (SetClosingEncounter ran → SingleChoiceAdvancePatch marked + broadcast). The module keeps the SAME
        // _geoEvent on the result page, so this mark is the ONLY prompt-vs-result discriminator — it makes the
        // EventAdvanceRequest handler idempotent (host-click-vs-client-click race, transport double-send).
        // Same bounded-FIFO shape as the dismissed tracking.
        private static readonly HashSet<ushort> _advanced = new HashSet<ushort>();
        private static readonly Queue<ushort> _advancedOrder = new Queue<ushort>();

        // Reverse lookup occId → live event (WeakReference so it never roots the GeoscapeEvent). Bounded FIFO so
        // it can't leak across a session; an evicted/collected entry simply returns false (host falls back to a
        // close-only resolution, never stuck). Only the HOST populates this (it owns id assignment).
        private const int MaxReverseTracked = 256;
        private static readonly Dictionary<ushort, System.WeakReference> _byId = new Dictionary<ushort, System.WeakReference>();
        private static readonly Queue<ushort> _byIdOrder = new Queue<ushort>();

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
                _byId[id] = new System.WeakReference(geoEvent);
                _byIdOrder.Enqueue(id);
                while (_byIdOrder.Count > MaxReverseTracked)
                    _byId.Remove(_byIdOrder.Dequeue());
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

        /// <summary>Host: record that this occurrence's single-choice PROMPT advanced to its RESULT page
        /// (SetClosingEncounter ran — host click or a driven client advance-request; first wins).</summary>
        public static void MarkAdvanced(ushort occurrenceId)
        {
            if (occurrenceId == 0) return;
            lock (_lock)
            {
                if (_advanced.Add(occurrenceId))
                {
                    _advancedOrder.Enqueue(occurrenceId);
                    while (_advancedOrder.Count > MaxDismissedTracked)
                        _advanced.Remove(_advancedOrder.Dequeue());
                }
            }
        }

        /// <summary>Host: true iff this occurrence's prompt already advanced to its result page.</summary>
        public static bool WasAdvanced(ushort occurrenceId)
        {
            if (occurrenceId == 0) return false;
            lock (_lock) { return _advanced.Contains(occurrenceId); }
        }

        /// <summary>
        /// Host: the live event keyed to <paramref name="occurrenceId"/>, or null if unknown/collected. Used by
        /// the choice arbiter to resolve a claim against the REAL authoritative GeoscapeEvent instance.
        /// </summary>
        public static bool TryGetEvent(ushort occurrenceId, out object geoEvent)
        {
            geoEvent = null;
            if (occurrenceId == 0) return false;
            lock (_lock)
            {
                if (_byId.TryGetValue(occurrenceId, out var wr) && wr != null)
                {
                    var target = wr.Target;
                    if (target != null) { geoEvent = target; return true; }
                    _byId.Remove(occurrenceId);   // collected → drop
                }
                return false;
            }
        }

        /// <summary>Test/teardown hook: reset the counter + dismissed tracking (the CWT self-empties as events GC).</summary>
        public static void ResetForTests()
        {
            lock (_lock) { _counter = 0; _dismissed.Clear(); _dismissedOrder.Clear(); _advanced.Clear(); _advancedOrder.Clear(); _byId.Clear(); _byIdOrder.Clear(); }
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
