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
            /// <summary>Idempotent no-op: a transport-duplicated raise/dismiss for an occurrence already shown/queued/resolved → do nothing (kills the double-send dialog).</summary>
            Ignore,
            /// <summary>Raise that arrived while another dialog is already shown on the single-slot client → DEFER it (occId-ordered); it is shown when the current dialog is dismissed.</summary>
            Enqueue,
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

        // A raise DEFERRED behind the busy single slot. Carries the single-choice context (singleChoice/oneWindow)
        // that arrived WITH the raise so a released raise re-runs the SAME branch it would have taken had the slot
        // been free (DecideForRaise) — the buffered-dismiss linkage stays in _pending and is re-consulted on release.
        private readonly struct QueuedRaiseInfo
        {
            public readonly string EventId;
            public readonly bool SingleChoice;
            public readonly bool OneWindow;
            public QueuedRaiseInfo(string eventId, bool singleChoice, bool oneWindow)
            {
                EventId = eventId;
                SingleChoice = singleChoice;
                OneWindow = oneWindow;
            }
        }

        /// <summary>Max buffered out-of-order dismisses; oldest is evicted past this so the buffer can't leak.</summary>
        public const int MaxPendingDismiss = 16;

        // occId → def-name of the currently-shown dialog (def-name is informational; the key is the occ id).
        private readonly Dictionary<ushort, string> _open = new Dictionary<ushort, string>();

        // Single-slot client display model: occId of the dialog that currently OCCUPIES the client's one modal slot
        // (0 = none) — a plain in-order dialog OR a single-choice prompt mirror, i.e. ANY raise still awaiting a
        // future host signal (Dismissed/Advanced) to resolve. The native GeoscapeViewSwitchQuery serializes display
        // one-at-a-time, so ANY raise arriving while this is non-zero is DEFERRED into _queue (occId-ordered) instead
        // of being pushed in arrival order — the queued raises are released in host emission order on the current
        // dialog's dismiss/advance. A TERMINAL resolution (buffered-dismiss ShowResultPage / DropNoop) does NOT
        // occupy the slot: it is completed at once and the native view-switch query serializes its actual display.
        private ushort _shownSlot;
        // Raises deferred while the slot is busy, keyed+ORDERED by host-monotonic occId (lowest = earliest emitted).
        // SortedDictionary keeps the FIFO in occId order so transport reordering can't swap display order. The value
        // carries the raise's single-choice context so a released raise re-runs the CORRECT branch (DecideForRaise).
        private readonly SortedDictionary<ushort, QueuedRaiseInfo> _queue = new SortedDictionary<ushort, QueuedRaiseInfo>();
        // Occurrences that reached a TERMINAL resolved state (shown+dismissed, or buffered-dismiss-then-resolved), for
        // idempotent dedup of transport-duplicated raises/dismisses. Hard-bounded FIFO so it can never leak; the
        // counter is host-monotonic and wraps only after 65535 occurrences, so an evicted id can never alias a live one.
        private const int MaxCompletedTracked = 64;
        private readonly HashSet<ushort> _completed = new HashSet<ushort>();
        private readonly Queue<ushort> _completedOrder = new Queue<ushort>();
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
        /// <summary>Plain raises deferred behind the currently-shown dialog, awaiting its dismiss (diagnostics/tests).</summary>
        public int QueuedCount => _queue.Count;
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
            _shownSlot = 0;
            _queue.Clear();
            _completed.Clear();
            _completedOrder.Clear();
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
        ///
        /// <paramref name="oneWindow"/> (a SUBSET of <paramref name="singleChoice"/>: the host's
        /// <c>IsSingleChoiceEncounter()</c>==true — the lone choice has EMPTY outcome text so the host shows
        /// reward+narrative in ONE combined window) SKIPS the prompt-mirror entirely and resolves STRAIGHT to the
        /// result page (reusing the stashed reward), matching the host's single window and curing the client's
        /// reward-less "phantom prompt". The prompt-mirror+advance lockstep is kept ONLY for the genuine 2-window
        /// (non-empty outcome-text) single-choice case (<paramref name="oneWindow"/>=false).
        /// </summary>
        public Decision Raised(ushort occurrenceId, string eventId, bool singleChoice = false, bool oneWindow = false)
        {
            // DEDUP (transport double-send): a raise for an occurrence already shown, already queued, or already
            // terminally resolved is an idempotent no-op — never a second dialog. (_pending is intentionally NOT in
            // this set: a buffered out-of-order dismiss is resolved by the FIRST matching raise in DecideForRaise.)
            if (_open.ContainsKey(occurrenceId) || _queue.ContainsKey(occurrenceId) || _completed.Contains(occurrenceId))
                return new Decision(ActionKind.Ignore, occurrenceId, eventId, -1);

            // SINGLE-SLOT gate — now covering ALL raise kinds (moved ABOVE the buffered-dismiss branch). The native
            // view-switch query shows one modal at a time, so if the slot is busy DEFER this raise in occId order,
            // carrying its single-choice context so the release re-runs the CORRECT branch (DecideForRaise). This
            // completes b0e20a0's invariant: previously only the plain in-order tail consulted the slot, so a
            // single-choice raise took the buffered-dismiss branch and opened a SECOND dialog simultaneously — the
            // two then resolved in advance-arrival order (host order 2,3 seen as 3,2).
            if (_shownSlot != 0)
            {
                _queue[occurrenceId] = new QueuedRaiseInfo(eventId, singleChoice, oneWindow);
                return new Decision(ActionKind.Enqueue, occurrenceId, eventId, -1);
            }

            return DecideForRaise(occurrenceId, eventId, singleChoice, oneWindow);
        }

        /// <summary>
        /// Decide the UI action for a raise WHEN THE SINGLE SLOT IS FREE — guaranteed by every caller: <see cref="Raised"/>
        /// after its busy-check, and <see cref="TryDequeueNext"/> when it releases a deferred raise. Consults a buffered
        /// out-of-order dismiss (<c>_pending</c>) exactly as before. A slot-OCCUPYING outcome (plain dialog or single-choice
        /// prompt mirror) marks the slot busy so the next raise defers behind it; a TERMINAL outcome (ShowResultPage /
        /// DropNoop) leaves the slot free (the native view-switch query serializes its actual on-screen display).
        /// </summary>
        private Decision DecideForRaise(ushort occurrenceId, string eventId, bool singleChoice, bool oneWindow)
        {
            if (_pending.TryGetValue(occurrenceId, out var buffered))
            {
                // The dismiss for this occurrence already arrived (out of order). Resolve it now.
                RemovePending(occurrenceId);
                if (buffered.ChoiceIndex >= 0)
                {
                    if (singleChoice)
                    {
                        // 1-WINDOW single-choice (host IsSingleChoiceEncounter()==true → one combined window WITH
                        // reward): skip the phantom reward-less prompt and resolve STRAIGHT to the result page,
                        // matching the host. The host's empty-outcome SetClosingEncounter may also emit an advance —
                        // consume any buffered one so it can't linger.
                        if (oneWindow)
                        {
                            RemovePendingAdvance(occurrenceId);
                            MarkCompleted(occurrenceId);
                            return new Decision(ActionKind.ShowResultPage, occurrenceId, eventId ?? buffered.EventId, buffered.ChoiceIndex);
                        }
                        // SINGLE-CHOICE prompt-mirror (gate ON, 2-window: non-empty outcome text). If the host already advanced to its result page
                        // (advance beat this raise — the same-frame SetClosingEncounter of an empty-outcome
                        // single-choice), jump straight to the result page; the host is already on window 2.
                        if (RemovePendingAdvance(occurrenceId))
                        {
                            MarkCompleted(occurrenceId);
                            return new Decision(ActionKind.ShowResultPage, occurrenceId, eventId ?? buffered.EventId, buffered.ChoiceIndex);
                        }
                        // Otherwise mirror the host's window-1 PROMPT page and wait for Advanced(). Track it open
                        // + awaiting-advance and OCCUPY the single slot (this is a shown dialog awaiting a future host
                        // signal, so the next raise must defer behind it — the missing gate that let two single-choice
                        // dialogs coexist). The reward stays stashed by the caller (ShowDialog never drains it) until
                        // the advance resolves it. ChoiceIndex>=0 distinguishes this mirror from a plain in-order raise
                        // (ChoiceIndex==-1) for the caller.
                        _open[occurrenceId] = eventId ?? buffered.EventId;
                        _promptMirror.Add(occurrenceId);
                        _shownSlot = occurrenceId;
                        return new Decision(ActionKind.ShowDialog, occurrenceId, eventId ?? buffered.EventId, buffered.ChoiceIndex);
                    }
                    // Multi-choice / gate-OFF: a completed event whose result-bearing dismiss beat its raise →
                    // resolve STRAIGHT to the result page (host advanced to window 2 on the click). Whether a
                    // window 2 actually renders is decided DOWNSTREAM (SyncEngine.ResolveToResultPage →
                    // EventReflection.BuildResultEvent): a buildable page → show it; a null page → clean close.
                    MarkCompleted(occurrenceId);
                    return new Decision(ActionKind.ShowResultPage, occurrenceId, eventId ?? buffered.EventId, buffered.ChoiceIndex);
                }
                // Close-only dismiss that beat its raise: the player never saw the dialog → nothing to show.
                MarkCompleted(occurrenceId);
                return new Decision(ActionKind.DropNoop, occurrenceId, eventId ?? buffered.EventId, buffered.ChoiceIndex);
            }

            // Normal in-order raise, slot guaranteed free here (Raised gated the busy case above) → show it now and
            // mark the single slot occupied so the next raise defers behind it (released on this dialog's dismiss).
            _shownSlot = occurrenceId;
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
                if (_shownSlot == occurrenceId) _shownSlot = 0;   // single slot freed → TryDequeueNext can release the next
                MarkCompleted(occurrenceId);
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
            // DEDUP (transport double-send): a dismiss for an occurrence already terminally resolved is an idempotent
            // no-op — it must NOT re-buffer as a phantom out-of-order dismiss that a later duplicate raise resolves.
            if (_completed.Contains(occurrenceId))
                return new Decision(ActionKind.Ignore, occurrenceId, eventId, choiceIndex);

            // Dismiss of an occurrence still DEFERRED in the queue (its raise arrived but the single slot was busy, so
            // its prompt was never shown). The host already applied the answer → drop it from the queue and resolve it
            // terminally: a picked choice still surfaces its result page (so the player sees the outcome); a close-only
            // one has nothing to show. Never leave it in the queue (it would later pop as an orphan dialog).
            if (_queue.Remove(occurrenceId))
            {
                MarkCompleted(occurrenceId);
                if (choiceIndex >= 0)
                    return new Decision(ActionKind.ShowResultInPlace, occurrenceId, eventId, choiceIndex);
                return new Decision(ActionKind.CloseDialog, occurrenceId, eventId, choiceIndex);
            }

            if (_open.ContainsKey(occurrenceId))
            {
                _open.Remove(occurrenceId);
                _promptMirror.Remove(occurrenceId);   // a real dismiss closes a mirrored prompt too (keep the set clean)
                if (_shownSlot == occurrenceId) _shownSlot = 0;   // single slot freed → TryDequeueNext can release the next
                MarkCompleted(occurrenceId);
                if (choiceIndex >= 0)
                    return new Decision(ActionKind.ShowResultInPlace, occurrenceId, eventId, choiceIndex);
                return new Decision(ActionKind.CloseDialog, occurrenceId, eventId, choiceIndex);
            }

            // Dismiss arrived before its raise → buffer (bounded) and wait for the raise to resolve it.
            BufferPending(occurrenceId, new PendingDismiss(eventId, choiceIndex));
            return new Decision(ActionKind.BufferDismiss, occurrenceId, eventId, choiceIndex);
        }

        /// <summary>
        /// Pop the next deferred raise (lowest host-monotonic occId = earliest emitted) when the single client slot is
        /// free, and re-decide its UI action via <see cref="DecideForRaise"/> using the single-choice context stashed
        /// at defer time. Returns false when the slot is still occupied or the queue is empty. The returned Decision can
        /// be ShowDialog (plain OR single-choice prompt mirror — re-occupies the slot, so the caller stops draining),
        /// or a TERMINAL ShowResultPage/DropNoop (buffered-dismiss single-choice — does NOT occupy the slot, so the
        /// caller keeps draining the next deferred raise). The (Unity-bound) caller looks up the stashed raise payload
        /// for the returned occId to build/show it — deferred events surface in occId (host emission) order.
        /// </summary>
        public bool TryDequeueNext(out Decision next)
        {
            next = default(Decision);
            if (_shownSlot != 0) return false;   // single slot still occupied → keep waiting
            if (_queue.Count == 0) return false;
            // SortedDictionary enumerates by ascending key → the lowest occId (earliest host emission) pops first.
            ushort occId = 0; QueuedRaiseInfo info = default(QueuedRaiseInfo);
            foreach (var kv in _queue) { occId = kv.Key; info = kv.Value; break; }
            _queue.Remove(occId);
            // Re-run the SAME branch this raise would have taken had the slot been free when it arrived. DecideForRaise
            // sets _shownSlot for a slot-occupying show (plain/prompt-mirror) and leaves it clear for a terminal result.
            next = DecideForRaise(occId, info.EventId, info.SingleChoice, info.OneWindow);
            return true;
        }

        // Record an occurrence as terminally resolved for idempotent dedup of transport-duplicated raises/dismisses.
        // Hard-bounded FIFO: the oldest tracked id is evicted past capacity so this can never leak.
        private void MarkCompleted(ushort occurrenceId)
        {
            if (_completed.Add(occurrenceId))
            {
                _completedOrder.Enqueue(occurrenceId);
                while (_completedOrder.Count > MaxCompletedTracked)
                    _completed.Remove(_completedOrder.Dequeue());
            }
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
