using System.Collections.Generic;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) client-side correlation/ordering brain for host→client geoscape-event
    /// raise/dismiss broadcasts. It keys every raise/dismiss on a per-occurrence id (synthesized
    /// host-side, see <c>EventOccurrenceIds</c>) instead of the reusable <c>GeoscapeEvent.EventID</c>
    /// def-name, so two occurrences of the SAME def-id never collide.
    ///
    /// The bug it fixes (log-confirmed, def-id "EX20"): the host fired two events sharing def-id "EX20"
    /// in rapid succession and broadcast them out of order — <c>Dismiss(EX20)</c> 18 ms BEFORE
    /// <c>Raise(EX20)</c>. Keyed by def-name the client could not correlate: the dismiss ran with no open
    /// dialog (<c>stateMatched=false</c>) so the result was lost, then the raise opened an orphaned dialog.
    ///
    /// This helper is a deterministic state machine that, given a sequence of raise/dismiss events with
    /// occurrence ids, decides the right UI action; the Unity-bound <c>SyncEngine</c> executes it via
    /// <c>EventDisplay</c>/<c>EventReflection</c>. It is decoupled from all game types so the
    /// ordering/collision logic is unit-testable without the game.
    ///
    /// State (per active co-op session, reset on teardown):
    ///   • <c>_open</c>     — occurrence ids whose dialog is currently shown on the client.
    ///   • <c>_pending</c>  — dismisses that arrived BEFORE their raise (occId → choiceIndex + hasReward),
    ///                        FIFO-bounded so it can never leak; resolved the instant the matching raise lands.
    /// </summary>
    public sealed class EventCorrelator
    {
        /// <summary>The UI action the (Unity-bound) caller must perform for a correlated raise/dismiss.</summary>
        public enum ActionKind
        {
            /// <summary>Raise: no buffered dismiss → build + show the dialog; record the open occurrence.</summary>
            ShowDialog,
            /// <summary>Dismiss of an OPEN dialog, picked choice produced a follow-up page → show its result in-place.</summary>
            ShowResultInPlace,
            /// <summary>Dismiss of an OPEN dialog, close-only (choiceIndex &lt; 0) → just close it.</summary>
            CloseDialog,
            /// <summary>Raise that matched a BUFFERED out-of-order dismiss with a result page → resolve straight to the result page (no orphan choice dialog).</summary>
            ShowResultPage,
            /// <summary>Dismiss arrived before its raise → buffer it until the raise lands.</summary>
            BufferDismiss,
            /// <summary>Nothing to display (e.g. a close-only dismiss that arrived before a raise that never showed) → no-op.</summary>
            DropNoop,
        }

        /// <summary>The decided action plus the fields the caller needs to execute it.</summary>
        public readonly struct Decision
        {
            public readonly ActionKind Kind;
            public readonly ushort OccurrenceId;
            public readonly string EventId;     // def-name, for the native rebuild + logging
            public readonly int ChoiceIndex;    // >= 0 → result page; < 0 → close-only

            public Decision(ActionKind kind, ushort occurrenceId, string eventId, int choiceIndex)
            {
                Kind = kind;
                OccurrenceId = occurrenceId;
                EventId = eventId;
                ChoiceIndex = choiceIndex;
            }
        }

        // A dismiss that landed before its raise. eventId is carried so the late raise can still rebuild
        // the right native page (the raise also carries it, but a buffered dismiss already knows the def).
        private readonly struct PendingDismiss
        {
            public readonly string EventId;
            public readonly int ChoiceIndex;
            public PendingDismiss(string eventId, int choiceIndex)
            {
                EventId = eventId;
                ChoiceIndex = choiceIndex;
            }
        }

        /// <summary>Max buffered out-of-order dismisses; oldest is evicted past this so the buffer can't leak.</summary>
        public const int MaxPendingDismiss = 16;

        // occId → def-name of the currently-shown dialog (def-name is informational; the key is the occ id).
        private readonly Dictionary<ushort, string> _open = new Dictionary<ushort, string>();
        // occId → buffered out-of-order dismiss. _pendingOrder preserves FIFO for bounded eviction.
        private readonly Dictionary<ushort, PendingDismiss> _pending = new Dictionary<ushort, PendingDismiss>();
        private readonly Queue<ushort> _pendingOrder = new Queue<ushort>();

        // occIds currently MIRRORING the host's single-choice window-1 PROMPT page (gated single-choice branch),
        // awaiting the host's explicit PROMPT→RESULT advance (Advanced). A subset of _open.
        private readonly HashSet<ushort> _promptMirror = new HashSet<ushort>();
        // Host advances that arrived BEFORE their prompt mirror was shown (the same-frame SetClosingEncounter /
        // advance-before-raise case, e.g. an empty-outcome single-choice). FIFO-bounded so it can never leak.
        private readonly HashSet<ushort> _pendingAdvance = new HashSet<ushort>();
        private readonly Queue<ushort> _pendingAdvanceOrder = new Queue<ushort>();

        /// <summary>Currently-open dialog count (diagnostics/tests).</summary>
        public int OpenCount => _open.Count;
        /// <summary>Buffered out-of-order dismiss count (diagnostics/tests).</summary>
        public int PendingCount => _pending.Count;
        /// <summary>Single-choice prompt mirrors awaiting a host advance (diagnostics/tests).</summary>
        public int PromptMirrorCount => _promptMirror.Count;
        /// <summary>Buffered advances that beat their raise (diagnostics/tests).</summary>
        public int PendingAdvanceCount => _pendingAdvance.Count;

        /// <summary>Forget all open/pending state (call on session teardown so it never carries across sessions).</summary>
        public void Reset()
        {
            _open.Clear();
            _pending.Clear();
            _pendingOrder.Clear();
            _promptMirror.Clear();
            _pendingAdvance.Clear();
            _pendingAdvanceOrder.Clear();
        }

        /// <summary>
        /// Correlate a host raise for <paramref name="occurrenceId"/>. If a buffered out-of-order dismiss
        /// matches, resolve it directly (skip leaving an orphan choice dialog); otherwise show the dialog and
        /// record the open occurrence.
        ///
        /// A buffered out-of-order dismiss with a picked choice (<c>ChoiceIndex &gt;= 0</c>) normally resolves
        /// STRAIGHT to the result page (the host advanced to its window-2 page on the single click that produced
        /// the dismiss). The ONE exception is a SINGLE-CHOICE-with-outcome encounter: the host auto-completes it
        /// at trigger (so its result-bearing dismiss beats this raise) yet stays on its window-1 PROMPT page
        /// (native <c>IsSingleChoiceEncounter()</c> is false), advancing to window 2 only when the player later
        /// clicks the lone prompt button. For that case the caller passes <paramref name="singleChoice"/>=true
        /// (ONLY when <c>EventMirrorFixGate</c> is ON) and the client MIRRORS the prompt (<c>ShowDialog</c>),
        /// waiting for the host's explicit <see cref="Advanced"/> signal — unless that advance already arrived
        /// (the same-frame empty-outcome case), in which case it jumps straight to the result page. When the gate
        /// is OFF the caller passes singleChoice=false → the legacy unconditional <c>ShowResultPage</c> below
        /// (byte-for-byte unchanged).
        /// </summary>
        public Decision Raised(ushort occurrenceId, string eventId, bool singleChoice = false)
        {
            if (_pending.TryGetValue(occurrenceId, out var buffered))
            {
                // The dismiss for this occurrence already arrived (out of order). Resolve it now.
                RemovePending(occurrenceId);
                if (buffered.ChoiceIndex >= 0)
                {
                    if (singleChoice)
                    {
                        // SINGLE-CHOICE prompt-mirror (gate ON). If the host already advanced to its result page
                        // (advance beat this raise — the same-frame SetClosingEncounter of an empty-outcome
                        // single-choice), jump straight to the result page; the host is already on window 2.
                        if (RemovePendingAdvance(occurrenceId))
                            return new Decision(ActionKind.ShowResultPage, occurrenceId, eventId ?? buffered.EventId, buffered.ChoiceIndex);
                        // Otherwise mirror the host's window-1 PROMPT page and wait for Advanced(). Track it open
                        // + awaiting-advance; the reward stays stashed by the caller (ShowDialog never drains it)
                        // until the advance resolves it. ChoiceIndex>=0 distinguishes this mirror from a plain
                        // in-order raise (ChoiceIndex==-1) for the caller.
                        _open[occurrenceId] = eventId ?? buffered.EventId;
                        _promptMirror.Add(occurrenceId);
                        return new Decision(ActionKind.ShowDialog, occurrenceId, eventId ?? buffered.EventId, buffered.ChoiceIndex);
                    }
                    // Multi-choice / gate-OFF: a completed event whose result-bearing dismiss beat its raise →
                    // resolve STRAIGHT to the result page (host advanced to window 2 on the click). Whether a
                    // window 2 actually renders is decided DOWNSTREAM (SyncEngine.ResolveToResultPage →
                    // EventReflection.BuildResultEvent): a buildable page → show it; a null page → clean close.
                    return new Decision(ActionKind.ShowResultPage, occurrenceId, eventId ?? buffered.EventId, buffered.ChoiceIndex);
                }
                // Close-only dismiss that beat its raise: the player never saw the dialog → nothing to show.
                return new Decision(ActionKind.DropNoop, occurrenceId, eventId ?? buffered.EventId, buffered.ChoiceIndex);
            }

            // Normal in-order raise: show + track as open.
            _open[occurrenceId] = eventId;
            return new Decision(ActionKind.ShowDialog, occurrenceId, eventId, -1);
        }

        /// <summary>
        /// Correlate the host's PROMPT→RESULT advance for a SINGLE-CHOICE occurrence — the host player clicked the
        /// lone window-1 prompt button and the host showed its window-2 result page. Because the event was
        /// auto-completed at trigger, that click runs NO native <c>CompleteEvent</c> (and thus emits no
        /// <c>EventDismiss</c>), so this dedicated signal is the only way the client learns the host advanced.
        /// If this occurrence is currently mirroring the host prompt (<see cref="Raised"/> returned ShowDialog for
        /// it) → advance it to the result page; otherwise the advance arrived BEFORE the prompt was shown (the
        /// same-frame empty-outcome case) → BUFFER it (FIFO-bounded) so the upcoming raise resolves straight to
        /// the result page. Only reached when <c>EventMirrorFixGate</c> is ON (the host emits no advance off-gate).
        /// </summary>
        public Decision Advanced(ushort occurrenceId, string eventId, int choiceIndex)
        {
            if (_promptMirror.Remove(occurrenceId))
            {
                _open.Remove(occurrenceId);   // the result page replaces the mirrored prompt
                return new Decision(ActionKind.ShowResultPage, occurrenceId, eventId, choiceIndex);
            }
            // Advance beat the prompt mirror → buffer it (bounded) until Raised() lands and consumes it.
            BufferAdvance(occurrenceId);
            return new Decision(ActionKind.DropNoop, occurrenceId, eventId, choiceIndex);
        }

        /// <summary>
        /// Correlate a host dismiss for <paramref name="occurrenceId"/>. If its dialog is open, resolve it in
        /// place (result page or close-only). If the raise hasn't arrived yet, BUFFER the dismiss (FIFO-bounded)
        /// so the upcoming raise resolves straight to the result page.
        /// </summary>
        public Decision Dismissed(ushort occurrenceId, string eventId, int choiceIndex)
        {
            if (_open.ContainsKey(occurrenceId))
            {
                _open.Remove(occurrenceId);
                _promptMirror.Remove(occurrenceId);   // a real dismiss closes a mirrored prompt too (keep the set clean)
                if (choiceIndex >= 0)
                    return new Decision(ActionKind.ShowResultInPlace, occurrenceId, eventId, choiceIndex);
                return new Decision(ActionKind.CloseDialog, occurrenceId, eventId, choiceIndex);
            }

            // Dismiss arrived before its raise → buffer (bounded) and wait for the raise to resolve it.
            BufferPending(occurrenceId, new PendingDismiss(eventId, choiceIndex));
            return new Decision(ActionKind.BufferDismiss, occurrenceId, eventId, choiceIndex);
        }

        private void BufferPending(ushort occurrenceId, PendingDismiss dismiss)
        {
            if (!_pending.ContainsKey(occurrenceId))
            {
                // Evict the oldest buffered dismiss once at capacity so the buffer is hard-bounded.
                while (_pendingOrder.Count >= MaxPendingDismiss && _pendingOrder.Count > 0)
                {
                    var oldest = _pendingOrder.Dequeue();
                    _pending.Remove(oldest);
                }
                _pendingOrder.Enqueue(occurrenceId);
            }
            // A duplicate dismiss for the same occId overwrites in place (keeps its existing FIFO position).
            _pending[occurrenceId] = dismiss;
        }

        private void RemovePending(ushort occurrenceId)
        {
            _pending.Remove(occurrenceId);
            // _pendingOrder is lazily pruned (stale entries are skipped on eviction since _pending lacks them),
            // but drop the matching token now to keep the queue tight.
            if (_pendingOrder.Count > 0)
            {
                int n = _pendingOrder.Count;
                for (int i = 0; i < n; i++)
                {
                    var id = _pendingOrder.Dequeue();
                    if (id != occurrenceId) _pendingOrder.Enqueue(id);
                }
            }
        }

        // Buffer a host advance that beat its raise. Hard-bounded (same cap as the dismiss buffer) so a never-
        // consumed advance can never leak; the oldest is evicted past capacity. A duplicate is a no-op.
        private void BufferAdvance(ushort occurrenceId)
        {
            if (_pendingAdvance.Contains(occurrenceId)) return;
            while (_pendingAdvanceOrder.Count >= MaxPendingDismiss && _pendingAdvanceOrder.Count > 0)
            {
                var oldest = _pendingAdvanceOrder.Dequeue();
                _pendingAdvance.Remove(oldest);
            }
            _pendingAdvance.Add(occurrenceId);
            _pendingAdvanceOrder.Enqueue(occurrenceId);
        }

        // Consume a buffered advance for this occurrence (returns true if one was present). Prunes the FIFO token
        // now to keep the queue tight (mirrors RemovePending).
        private bool RemovePendingAdvance(ushort occurrenceId)
        {
            if (!_pendingAdvance.Remove(occurrenceId)) return false;
            if (_pendingAdvanceOrder.Count > 0)
            {
                int n = _pendingAdvanceOrder.Count;
                for (int i = 0; i < n; i++)
                {
                    var id = _pendingAdvanceOrder.Dequeue();
                    if (id != occurrenceId) _pendingAdvanceOrder.Enqueue(id);
                }
            }
            return true;
        }
    }
}
