using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) client-side UNIFIED DISPLAY QUEUE (Batch-3 P4) — ONE ordering surface for every
    /// host-stamped mirrored display (event raise 0x65 / report modal 0x69 / cutscene), reproducing the native
    /// <c>GeoscapeViewSwitchQuery</c> semantics the host's own displays obey (decompile-verified):
    ///   • <c>QueryStateSwitch</c> — sorted insert before the first STRICTLY-LOWER-priority request
    ///     (priority DESC, FIFO among equals);
    ///   • <c>ProcessQueriedStateSwitch</c> — one request at a time (next pops only when the current one
    ///     finished).
    /// Here "FIFO among equals" is keyed on the host-stamped monotonic <c>displaySeq</c> (host emission order),
    /// NOT client arrival order, so transport reordering can never swap two equal-priority displays. The host's
    /// native queue ordering IS the mandate's "same order" — this queue never re-derives one.
    ///
    /// The per-rail handlers stay the display EXECUTORS (EventCorrelator keeps its occId dedup/correlation and
    /// becomes a CONSUMER of this queue: raises reach it only when released here); this class owns only
    /// cross-rail ORDER + one-at-a-time release + a seq-level transport-dup belt. Reset at the save-transfer
    /// boundary with the rest of the event-mirror state (spec Batch-3 risk note).
    /// </summary>
    public sealed class UnifiedDisplayQueue
    {
        /// <summary>Display kinds riding the queue (which per-rail executor a released entry runs).</summary>
        public const byte KindEvent = 1;      // 0x65 event raise → EventCorrelator consumer path
        public const byte KindReport = 2;     // 0x69 report modal → GeoModalDisplay path
        public const byte KindCutscene = 3;   // PlayCutsceneAction → CutsceneReflection path

        private struct Entry
        {
            public uint Seq;
            public int Priority;
            public byte Kind;
        }

        /// <summary>Released (terminally shown/closed) seqs tracked for transport-dup dedup; hard-bounded FIFO.</summary>
        public const int MaxCompletedTracked = 64;

        // Pending displays, kept sorted (priority DESC, seq ASC) by sorted insert — the native model.
        private readonly List<Entry> _queue = new List<Entry>();
        // The single "shown" slot (native _currentStateSwitchRequest analogue). 0 = free.
        private uint _currentSeq;
        private byte _currentKind;
        // Seq-level dedup of transport double-sends (belt on top of the per-rail occId dedups).
        private readonly HashSet<uint> _completed = new HashSet<uint>();
        private readonly Queue<uint> _completedOrder = new Queue<uint>();

        /// <summary>True iff a released display is still occupying the single slot.</summary>
        public bool HasCurrent => _currentSeq != 0;
        /// <summary>The occupying display's seq (0 = free) / kind (undefined when free) — diagnostics + close-matching.</summary>
        public uint CurrentSeq => _currentSeq;
        public byte CurrentKind => _currentKind;
        /// <summary>Pending (not yet released) display count — diagnostics/tests.</summary>
        public int QueuedCount => _queue.Count;

        /// <summary>
        /// Insert a host-stamped display in native order (before the first strictly-lower priority; seq-ASC
        /// among equal priorities). False = duplicate seq (already pending, current, or completed) — the
        /// transport double-send belt; the caller drops the message.
        /// </summary>
        public bool Enqueue(uint seq, int priority, byte kind)
        {
            if (seq == 0) return false;   // unstamped/legacy never rides the queue
            if (seq == _currentSeq || _completed.Contains(seq)) return false;
            int insertAt = _queue.Count;
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Seq == seq) return false;   // duplicate pending
                // Native sorted insert (first strictly-lower priority) + displaySeq ASC among equals.
                if (insertAt == _queue.Count &&
                    (_queue[i].Priority < priority || (_queue[i].Priority == priority && _queue[i].Seq > seq)))
                    insertAt = i;
            }
            _queue.Insert(insertAt, new Entry { Seq = seq, Priority = priority, Kind = kind });
            return true;
        }

        /// <summary>
        /// Release the next display (queue head = highest priority, earliest seq) into the single slot — only
        /// when the slot is free (native one-at-a-time). The released entry OCCUPIES the slot; a caller whose
        /// executor turns out non-occupying (notice/deferred/terminal resolution) calls
        /// <see cref="NotifyClosed"/> immediately and keeps draining.
        /// </summary>
        public bool TryRelease(out uint seq, out byte kind)
        {
            seq = 0;
            kind = 0;
            if (_currentSeq != 0 || _queue.Count == 0) return false;
            var head = _queue[0];
            _queue.RemoveAt(0);
            _currentSeq = head.Seq;
            _currentKind = head.Kind;
            seq = head.Seq;
            kind = head.Kind;
            return true;
        }

        /// <summary>
        /// The occupying display closed — free the slot (match-guarded: a stray/late close for a different seq
        /// never frees someone else's slot) and record the seq as completed (dup belt).
        /// </summary>
        public void NotifyClosed(uint seq)
        {
            if (seq == 0 || seq != _currentSeq) return;
            MarkCompleted(seq);
            _currentSeq = 0;
            _currentKind = 0;
        }

        /// <summary>
        /// Force-free the slot regardless of who occupies it (geoscape-view teardown belt: the native
        /// view-switch requests died with the view, so a "current" display can never signal a close again).
        /// The occupant is marked completed so its duplicate can't re-ride the queue.
        /// </summary>
        public void ClearCurrent()
        {
            if (_currentSeq != 0) MarkCompleted(_currentSeq);
            _currentSeq = 0;
            _currentKind = 0;
        }

        /// <summary>Save-transfer / session boundary reset — pending, slot and dedup all restart.</summary>
        public void Reset()
        {
            _queue.Clear();
            _currentSeq = 0;
            _currentKind = 0;
            _completed.Clear();
            _completedOrder.Clear();
        }

        private void MarkCompleted(uint seq)
        {
            if (_completed.Add(seq))
            {
                _completedOrder.Enqueue(seq);
                while (_completedOrder.Count > MaxCompletedTracked)
                    _completed.Remove(_completedOrder.Dequeue());
            }
        }
    }
}
