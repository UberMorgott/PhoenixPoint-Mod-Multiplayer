using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
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
            /// <summary>Replay mode (<c>EventReplayModeGate</c> ON): the decided signal arrived for an OPEN choice window this peer did NOT win → keep the window open, grey non-winning buttons + highlight the winner (<c>ChoiceIndex</c>), and wait for the local click. NOT a forced transition; the window is still live.</summary>
            ArmReplay,
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
        // FIX C belt: occIds whose single-choice prompt the LOCAL player already answered while KEEPING the modal
        // open (EncounterChoiceClientPatch greys the lone button instead of local-closing, so the host's later
        // EventAdvanceResult transitions the SAME window in place). Recorded so an advance that arrives for an
        // occId shown IN-ORDER (a plain ShowDialog, never entered _promptMirror) still resolves to the result page
        // in place — never buffered into a DropNoop that would leave the greyed modal waiting. Bounded FIFO so a
        // never-resolved mark can't leak; host-monotonic occId wraps only after 65535 so an evicted id can't alias.
        private const int MaxLocallyAnswered = 32;
        private readonly HashSet<ushort> _locallyAnswered = new HashSet<ushort>();
        private readonly Queue<ushort> _locallyAnsweredOrder = new Queue<ushort>();
        // Host advances that arrived BEFORE their prompt mirror was shown (the same-frame SetClosingEncounter /
        // advance-before-raise case, e.g. an empty-outcome single-choice). FIFO-bounded so it can never leak.
        private readonly HashSet<ushort> _pendingAdvance = new HashSet<ushort>();
        private readonly Queue<ushort> _pendingAdvanceOrder = new Queue<ushort>();

        // ── Replay mode (EventReplayModeGate ON) — only the OPEN-window branch of Dismissed uses these ──
        /// <summary>Max DECIDED occurrences tracked (occId → winning choice index) for replay-mode dedup + local
        /// click resolution; oldest evicted past this so the map can't leak. Cap ≥128 (was the 32/64 belts).</summary>
        public const int MaxDecidedTracked = 128;
        // occId → the authoritative WINNING choice index, recorded when the decided signal arrives for an OPEN
        // window this peer did NOT win (ArmReplay). Consumed by ReplayLocalClick when the player clicks the
        // highlighted winner; dropped on any terminal resolution of the occurrence and on Reset. Bounded FIFO.
        private readonly Dictionary<ushort, int> _decided = new Dictionary<ushort, int>();
        private readonly Queue<ushort> _decidedOrder = new Queue<ushort>();
        /// <summary>Max locally-picked multi-choice indices tracked (occId → this peer's clicked index), for the
        /// winner-vs-race-loser split at Dismissed time; oldest evicted past this so it can't leak.</summary>
        public const int MaxPickedTracked = 32;
        // occId → the multi-choice index THIS peer clicked (winner detection: picked == decided winning index →
        // auto in-place transition; else replay-arm). Bounded FIFO; dropped on the occurrence's terminal resolve.
        private readonly Dictionary<ushort, int> _pickedChoice = new Dictionary<ushort, int>();
        private readonly Queue<ushort> _pickedOrder = new Queue<ushort>();

        /// <summary>Currently-open dialog count (diagnostics/tests).</summary>
        public int OpenCount => _open.Count;
        /// <summary>Plain raises deferred behind the currently-shown dialog, awaiting its dismiss (diagnostics/tests).</summary>
        public int QueuedCount => _queue.Count;
        /// <summary>Buffered out-of-order dismiss count (diagnostics/tests).</summary>
        public int PendingCount => _pending.Count;
        /// <summary>Single-choice prompt mirrors awaiting a host advance (diagnostics/tests).</summary>
        public int PromptMirrorCount => _promptMirror.Count;
        /// <summary>Locally-answered single-choice prompts kept open awaiting the host advance (diagnostics/tests).</summary>
        public int LocallyAnsweredCount => _locallyAnswered.Count;
        /// <summary>True iff no dialog occupies the single client display slot. Batch-3 P4: the unified display
        /// queue treats an EVENT display as closed exactly when this slot frees (dismiss/advance) — the
        /// correlator is the queue's event-rail CONSUMER, keeping all its dedup/correlation logic.</summary>
        public bool ShownSlotFree => _shownSlot == 0;
        /// <summary>Buffered advances that beat their raise (diagnostics/tests).</summary>
        public int PendingAdvanceCount => _pendingAdvance.Count;
        /// <summary>Decided-and-replay-armed occurrences awaiting a local winner click (diagnostics/tests).</summary>
        public int DecidedCount => _decided.Count;

        /// <summary>Forget all open/pending state (call on session teardown so it never carries across sessions).</summary>
        public void Reset()
        {
            _open.Clear();
            _pending.Clear();
            _pendingOrder.Clear();
            _promptMirror.Clear();
            _locallyAnswered.Clear();
            _locallyAnsweredOrder.Clear();
            _pendingAdvance.Clear();
            _pendingAdvanceOrder.Clear();
            _decided.Clear();
            _decidedOrder.Clear();
            _pickedChoice.Clear();
            _pickedOrder.Clear();
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
            // DEDUP (duplicate/late advance): an advance for an occurrence ALREADY terminally resolved — the
            // client showed (and closed) its result page via a prior advance, or resolved it via an in-place /
            // buffered dismiss — is an idempotent no-op, mirroring the same _completed guard in Dismissed().
            // It must NOT fall through to BufferAdvance: that phantom _pendingAdvance entry could later pair
            // with a duplicate raise (after _completed FIFO eviction) into an orphan dialog no host signal
            // would ever close. The FIRST advance for a live prompt mirror is untouched (occurrence not yet
            // completed) — the normal click→advance→result flow always shows its result page once.
            if (_completed.Contains(occurrenceId))
                return new Decision(ActionKind.Ignore, occurrenceId, eventId, choiceIndex);

            if (_promptMirror.Remove(occurrenceId))
            {
                _locallyAnswered.Remove(occurrenceId);   // tidy the belt mark; the prompt-mirror path owns it here
                _open.Remove(occurrenceId);   // the result page replaces the mirrored prompt
                if (_shownSlot == occurrenceId) _shownSlot = 0;   // single slot freed → TryDequeueNext can release the next
                MarkCompleted(occurrenceId);
                return new Decision(ActionKind.ShowResultPage, occurrenceId, eventId, choiceIndex);
            }
            // BELT (FIX C): a locally-answered prompt whose modal was KEPT OPEN but never entered _promptMirror
            // (the raise arrived in-order → a plain ShowDialog). The host's advance still transitions THAT open
            // modal to the result page IN PLACE (openIsEventState=True) — resolve it here instead of buffering into
            // a DropNoop that would leave the greyed modal waiting. Only when the occurrence is still open.
            if (_locallyAnswered.Remove(occurrenceId) && _open.Remove(occurrenceId))
            {
                if (_shownSlot == occurrenceId) _shownSlot = 0;
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
        public Decision Dismissed(ushort occurrenceId, string eventId, int choiceIndex, bool replayMode = false)
        {
            // DEDUP (transport double-send): a dismiss for an occurrence already terminally resolved is an idempotent
            // no-op — it must NOT re-buffer as a phantom out-of-order dismiss that a later duplicate raise resolves.
            if (_completed.Contains(occurrenceId))
                return new Decision(ActionKind.Ignore, occurrenceId, eventId, choiceIndex);

            // REPLAY DEDUP: a duplicate decided signal for an occurrence already replay-armed (its window is still
            // OPEN awaiting the local winner click) is an idempotent no-op — never re-arm / re-record. This class is
            // BCL-only (no Unity logging); the Unity caller (SyncEngine.OnEventDismiss, Ignore case) logs the drop
            // with occId + the replay-armed reason (via TryGetDecided) so late/duplicate decided signals stay
            // diagnosable in field logs.
            if (replayMode && _decided.ContainsKey(occurrenceId))
                return new Decision(ActionKind.Ignore, occurrenceId, eventId, choiceIndex);

            // Dismiss of an occurrence still DEFERRED in the queue (its raise arrived but the single slot was busy, so
            // its prompt was never shown). The host already applied the answer → drop it from the queue and resolve it
            // terminally: a picked choice still surfaces its result page (so the player sees the outcome); a close-only
            // one has nothing to show. Never leave it in the queue (it would later pop as an orphan dialog).
            // (Legacy under replayMode too: the window was never SHOWN on this peer, so there is nothing to re-arm —
            // replay applies only to an actually-open window, the _open branch below.)
            if (_queue.Remove(occurrenceId))
            {
                MarkCompleted(occurrenceId);
                _pickedChoice.Remove(occurrenceId);
                if (choiceIndex >= 0)
                    return new Decision(ActionKind.ShowResultInPlace, occurrenceId, eventId, choiceIndex);
                return new Decision(ActionKind.CloseDialog, occurrenceId, eventId, choiceIndex);
            }

            if (_open.ContainsKey(occurrenceId))
            {
                // REPLAY MODE (gate ON): the window is OPEN on this peer and this is a result-bearing decided signal
                // (choiceIndex >= 0). If THIS peer did not win the occurrence (its locally-picked choice != the decided
                // winning index, or it never clicked), do NOT force the result page — keep the window OPEN, record the
                // winning index, and let the caller grey the non-winning buttons + highlight the winner. The local
                // click then resolves to the result page (ReplayLocalClick). The winner (picked == winning) falls
                // through to the legacy auto in-place transition below.
                if (replayMode && choiceIndex >= 0
                    && !(_pickedChoice.TryGetValue(occurrenceId, out var picked) && picked == choiceIndex))
                {
                    RecordDecided(occurrenceId, choiceIndex);
                    // Window stays live: _open / _shownSlot / _pickedChoice are intentionally left intact and the
                    // occurrence is NOT marked completed (it resolves terminally only on the local winner click).
                    return new Decision(ActionKind.ArmReplay, occurrenceId, eventId, choiceIndex);
                }

                _open.Remove(occurrenceId);
                _promptMirror.Remove(occurrenceId);   // a real dismiss closes a mirrored prompt too (keep the set clean)
                _locallyAnswered.Remove(occurrenceId);   // and drops any belt mark (the dismiss resolves it in place)
                _pickedChoice.Remove(occurrenceId);
                _decided.Remove(occurrenceId);   // belt: a real dismiss supersedes any prior replay arm for it
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

        /// <summary>
        /// The (Unity-bound) caller could NOT execute a ShowDialog decision for <paramref name="occurrenceId"/>
        /// (its build payload is missing — defensive, should never happen): un-show the occurrence — drop it from
        /// open/prompt tracking, free the single slot, and mark it terminally completed (dedup) so a late duplicate
        /// raise/dismiss can't resurrect it. Without this the slot would stay occupied by a dialog that never
        /// rendered (and whose dismiss the host will never re-send) → every later raise defers forever.
        /// </summary>
        public void AbortShow(ushort occurrenceId)
        {
            _open.Remove(occurrenceId);
            _promptMirror.Remove(occurrenceId);
            _locallyAnswered.Remove(occurrenceId);
            RemoveDecided(occurrenceId);
            _pickedChoice.Remove(occurrenceId);
            if (_shownSlot == occurrenceId) _shownSlot = 0;
            MarkCompleted(occurrenceId);
        }

        /// <summary>
        /// FIX C belt: the LOCAL player answered a single-choice prompt for <paramref name="occurrenceId"/> and
        /// the client kept its modal OPEN (greyed) awaiting the host's <see cref="Advanced"/> broadcast. Recording
        /// it lets that later advance resolve the SAME open modal to the result page in place even when the prompt
        /// was shown in-order (a plain ShowDialog that never entered <c>_promptMirror</c>). Idempotent, bounded
        /// FIFO. No effect on the dedup (<c>_completed</c>) or ordering paths — purely additive.
        /// </summary>
        public void MarkLocallyAnswered(ushort occurrenceId)
        {
            if (occurrenceId == 0) return;
            if (_locallyAnswered.Add(occurrenceId))
            {
                _locallyAnsweredOrder.Enqueue(occurrenceId);
                while (_locallyAnsweredOrder.Count > MaxLocallyAnswered)
                    _locallyAnswered.Remove(_locallyAnsweredOrder.Dequeue());
            }
        }

        /// <summary>
        /// REPLAY MODE: record THIS peer's locally-clicked MULTI-choice index for <paramref name="occurrenceId"/>
        /// (its answer relay is in flight). At <see cref="Dismissed"/> time this distinguishes the WINNER (picked ==
        /// the decided winning index → auto in-place transition) from a race-loser / non-winner (→ replay-arm).
        /// Idempotent, bounded FIFO; only meaningful when <c>EventReplayModeGate</c> is ON. No effect on any legacy
        /// (dedup / ordering / single-choice) path — purely additive.
        /// </summary>
        public void MarkPickedChoice(ushort occurrenceId, int choiceIndex)
        {
            if (occurrenceId == 0) return;
            if (!_pickedChoice.ContainsKey(occurrenceId))
            {
                _pickedOrder.Enqueue(occurrenceId);
                while (_pickedOrder.Count > MaxPickedTracked)
                    _pickedChoice.Remove(_pickedOrder.Dequeue());
            }
            _pickedChoice[occurrenceId] = choiceIndex;
        }

        /// <summary>REPLAY MODE: true iff <paramref name="occurrenceId"/> is decided-and-replay-armed (its window is
        /// still open awaiting the local winner click); <paramref name="winningChoiceIndex"/> = the winning index.</summary>
        public bool TryGetDecided(ushort occurrenceId, out int winningChoiceIndex)
            => _decided.TryGetValue(occurrenceId, out winningChoiceIndex);

        /// <summary>
        /// REPLAY MODE: the local player clicked the highlighted WINNER button on a replay-armed window for
        /// <paramref name="occurrenceId"/> → resolve it terminally to the authoritative result page. Drops the
        /// occurrence from all live/replay tracking, frees the single slot, and marks it completed (dedup). Returns
        /// <see cref="ActionKind.ShowResultPage"/> with the winning index; a no-op <see cref="ActionKind.Ignore"/>
        /// when the occurrence was not (or no longer) replay-armed (already resolved / superseded).
        /// </summary>
        public Decision ReplayLocalClick(ushort occurrenceId, string eventId)
        {
            if (!_decided.TryGetValue(occurrenceId, out var winningChoiceIndex))
                return new Decision(ActionKind.Ignore, occurrenceId, eventId, -1);
            RemoveDecided(occurrenceId);
            _open.Remove(occurrenceId);
            _promptMirror.Remove(occurrenceId);
            _locallyAnswered.Remove(occurrenceId);
            _pickedChoice.Remove(occurrenceId);
            if (_shownSlot == occurrenceId) _shownSlot = 0;   // single slot freed → the next deferred raise can release
            MarkCompleted(occurrenceId);
            return new Decision(ActionKind.ShowResultPage, occurrenceId, eventId, winningChoiceIndex);
        }

        // Record occId → winning choice index for a replay-armed occurrence. Hard-bounded FIFO so it can never leak.
        private void RecordDecided(ushort occurrenceId, int winningChoiceIndex)
        {
            if (!_decided.ContainsKey(occurrenceId))
            {
                _decidedOrder.Enqueue(occurrenceId);
                while (_decidedOrder.Count > MaxDecidedTracked)
                    _decided.Remove(_decidedOrder.Dequeue());
            }
            _decided[occurrenceId] = winningChoiceIndex;
        }

        // Drop a decided/replay-armed occurrence and prune its FIFO token (mirrors RemovePending).
        private void RemoveDecided(ushort occurrenceId)
        {
            if (!_decided.Remove(occurrenceId)) return;
            if (_decidedOrder.Count > 0)
            {
                int n = _decidedOrder.Count;
                for (int i = 0; i < n; i++)
                {
                    var id = _decidedOrder.Dequeue();
                    if (id != occurrenceId) _decidedOrder.Enqueue(id);
                }
            }
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
                // Evict the oldest buffered dismiss once at capacity so the buffer is hard-bounded — but NEVER
                // one whose raise is currently DEFERRED in _queue: that dismiss IS the queued raise's resolution,
                // and evicting it would make the released raise (TryDequeueNext → DecideForRaise) find no pending
                // → plain ShowDialog for an occurrence the host ALREADY resolved → its dismiss never comes again
                // → _shownSlot wedges forever and every later dialog starves. Evict the oldest NON-queued entry
                // instead; if EVERY buffered dismiss is queued-linked, REFUSE eviction and let the buffer exceed
                // the cap transiently: _queue is itself bounded by real host emissions and each released raise
                // consumes its pending entry, so the overshoot self-drains — a wedged slot never does. One full
                // FIFO rotation per insert keeps the eviction order intact for the surviving entries.
                if (_pendingOrder.Count >= MaxPendingDismiss)
                {
                    int n = _pendingOrder.Count;
                    int toEvict = n - (MaxPendingDismiss - 1);   // room for the new entry (usually 1)
                    for (int i = 0; i < n; i++)
                    {
                        var oldest = _pendingOrder.Dequeue();
                        if (toEvict > 0 && !_queue.ContainsKey(oldest))
                        {
                            _pending.Remove(oldest);
                            toEvict--;
                            continue;   // evicted — its FIFO token is not re-enqueued
                        }
                        _pendingOrder.Enqueue(oldest);
                    }
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
