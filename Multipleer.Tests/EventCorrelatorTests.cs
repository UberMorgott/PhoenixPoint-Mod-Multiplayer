using Multipleer.Network.Sync.State;
using Xunit;

using Kind = Multipleer.Network.Sync.State.EventCorrelator.ActionKind;

/// <summary>
/// Pure ordering/collision guards for the client-side <see cref="EventCorrelator"/> — the brain that fixes
/// the def-id "EX20" collision (two occurrences sharing a reusable def-name) and the out-of-order
/// Dismiss-before-Raise broadcast. Unity-free: drives the decision machine directly with occurrence ids.
/// </summary>
public class EventCorrelatorTests
{
    [Fact]
    public void InOrder_RaiseThenDismissWithResult_ShowsResultInPlace()
    {
        var c = new EventCorrelator();

        var raised = c.Raised(1, "EX20");
        Assert.Equal(Kind.ShowDialog, raised.Kind);
        Assert.Equal(1, c.OpenCount);

        var dismissed = c.Dismissed(1, "EX20", choiceIndex: 0);
        Assert.Equal(Kind.ShowResultInPlace, dismissed.Kind);
        Assert.Equal(0, dismissed.ChoiceIndex);
        Assert.Equal(0, c.OpenCount);   // resolved → no longer open
        Assert.Equal(0, c.PendingCount);
    }

    [Fact]
    public void InOrder_RaiseThenCloseOnlyDismiss_ClosesDialog()
    {
        var c = new EventCorrelator();
        c.Raised(7, "EX39");

        var dismissed = c.Dismissed(7, "EX39", choiceIndex: -1);
        Assert.Equal(Kind.CloseDialog, dismissed.Kind);
        Assert.Equal(0, c.OpenCount);
    }

    [Fact]
    public void OutOfOrder_DismissBeforeRaise_BuffersThenResolvesToResultPage()
    {
        var c = new EventCorrelator();

        // The exact EX20 wire order: Dismiss(occ=5, choice=0) arrives 18ms BEFORE Raise(occ=5).
        var dismissed = c.Dismissed(5, "EX20", choiceIndex: 0);
        Assert.Equal(Kind.BufferDismiss, dismissed.Kind);
        Assert.Equal(1, c.PendingCount);
        Assert.Equal(0, c.OpenCount);   // nothing open yet

        var raised = c.Raised(5, "EX20");
        Assert.Equal(Kind.ShowResultPage, raised.Kind);   // jump straight to the result page, no orphan choice dialog
        Assert.Equal(0, raised.ChoiceIndex);
        Assert.Equal(0, c.PendingCount);  // buffer drained
        Assert.Equal(0, c.OpenCount);     // already resolved → not left open
    }

    [Fact]
    public void OutOfOrder_SingleChoiceDismissBeforeRaise_MirrorsPromptThenAdvancesOnHostAdvance()
    {
        var c = new EventCorrelator();

        // Single-choice-WITH-OUTCOME site exploration ("Мост"): the host auto-completes the event at trigger so
        // its result-bearing Dismiss(occ=1, choice=0) beats the Raise — BUT the host stays on its window-1 PROMPT
        // page (native IsSingleChoiceEncounter()==false), advancing to window 2 only when the player clicks the
        // lone prompt button. So under the gated single-choice branch (caller passes singleChoice=true ONLY when
        // EventMirrorFixGate is ON) the client must MIRROR the prompt and WAIT for the host's explicit advance —
        // not jump to the result page (the old unconditional ShowResultPage made the client show window 2 while
        // the host showed window 1).
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(1, "EX99", choiceIndex: 0).Kind);
        Assert.Equal(1, c.PendingCount);

        var raised = c.Raised(1, "EX99", singleChoice: true);
        Assert.Equal(Kind.ShowDialog, raised.Kind);        // mirror the host PROMPT, do NOT jump to result
        Assert.Equal(0, raised.ChoiceIndex);               // carries the picked index (mirror discriminator, >=0)
        Assert.Equal(1, c.OpenCount);                      // prompt is open
        Assert.Equal(1, c.PromptMirrorCount);              // awaiting the host's advance
        Assert.Equal(0, c.PendingCount);                   // dismiss buffer drained

        // Host clicks its prompt → SetClosingEncounter → EventAdvanceResult → client advances to the result page.
        var advanced = c.Advanced(1, "EX99", choiceIndex: 0);
        Assert.Equal(Kind.ShowResultPage, advanced.Kind);
        Assert.Equal(0, advanced.ChoiceIndex);
        Assert.Equal(0, c.OpenCount);                      // result page replaces the mirrored prompt
        Assert.Equal(0, c.PromptMirrorCount);
    }

    [Fact]
    public void OutOfOrder_OneWindowSingleChoiceDismissBeforeRaise_ResolvesStraightToResultPage_NoPrompt()
    {
        var c = new EventCorrelator();

        // 1-WINDOW single-choice ("МЕСТНОСТЬ РАЗВЕДКИ" / empty outcome text → host IsSingleChoiceEncounter()==true →
        // reward+narrative in ONE combined window). The host auto-completes at trigger so its result-bearing
        // Dismiss(occ=3, choice=0) beats the Raise. With oneWindow=true the client must SKIP the phantom reward-less
        // prompt and resolve STRAIGHT to the result page (reusing the stashed reward), matching the host's single
        // window — NOT mirror a prompt (the 2-window path) and NOT wait for an advance.
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(3, "EX1W", choiceIndex: 0).Kind);
        Assert.Equal(1, c.PendingCount);

        var raised = c.Raised(3, "EX1W", singleChoice: true, oneWindow: true);
        Assert.Equal(Kind.ShowResultPage, raised.Kind);   // straight to result, no prompt mirror
        Assert.Equal(0, raised.ChoiceIndex);
        Assert.Equal(0, c.PromptMirrorCount);             // never mirrored a prompt
        Assert.Equal(0, c.OpenCount);
        Assert.Equal(0, c.PendingCount);                  // dismiss buffer drained

        // The host's empty-outcome SetClosingEncounter also emits an advance; arriving AFTER the result page is
        // already shown it is a harmless no-op (no prompt mirror to advance).
        Assert.Equal(Kind.DropNoop, c.Advanced(3, "EX1W", choiceIndex: 0).Kind);
    }

    [Fact]
    public void OutOfOrder_OneWindowAdvanceBeforeRaise_StillResolvesStraightToResultPage_AdvanceConsumed()
    {
        var c = new EventCorrelator();

        // 1-window event where the wire order is Dismiss → Advance → Raise. The buffered advance is consumed by the
        // oneWindow raise (so it can't linger), and the raise still resolves straight to the result page.
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(4, "EX1W2", choiceIndex: 0).Kind);
        Assert.Equal(Kind.DropNoop, c.Advanced(4, "EX1W2", choiceIndex: 0).Kind);
        Assert.Equal(1, c.PendingAdvanceCount);

        var raised = c.Raised(4, "EX1W2", singleChoice: true, oneWindow: true);
        Assert.Equal(Kind.ShowResultPage, raised.Kind);
        Assert.Equal(0, c.PendingAdvanceCount);           // buffered advance consumed (no leak)
        Assert.Equal(0, c.PromptMirrorCount);
        Assert.Equal(0, c.OpenCount);
    }

    [Fact]
    public void OutOfOrder_SingleChoiceAdvanceBeforeRaise_ResolvesStraightToResultPage()
    {
        var c = new EventCorrelator();

        // Empty-outcome single-choice: the host shows the result IMMEDIATELY (SetSingleChoiceEncounter →
        // SetClosingEncounter during ShowEncounter), so the advance can beat the raise. Wire order:
        // Dismiss → Advance → Raise. The buffered advance makes the raise resolve straight to the result page
        // (no prompt flicker), reusing the reward stashed from the dismiss.
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(2, "EXI", choiceIndex: 0).Kind);
        Assert.Equal(Kind.DropNoop, c.Advanced(2, "EXI", choiceIndex: 0).Kind);   // no prompt mirror yet → buffered
        Assert.Equal(1, c.PendingAdvanceCount);

        var raised = c.Raised(2, "EXI", singleChoice: true);
        Assert.Equal(Kind.ShowResultPage, raised.Kind);    // pending advance → jump to result, not the prompt
        Assert.Equal(0, raised.ChoiceIndex);
        Assert.Equal(0, c.PromptMirrorCount);
        Assert.Equal(0, c.PendingAdvanceCount);            // consumed
        Assert.Equal(0, c.OpenCount);
    }

    [Fact]
    public void Advance_ForUnmirroredOccurrence_BuffersBounded_AndResetClears()
    {
        var c = new EventCorrelator();

        // An advance with no matching prompt mirror (and no buffered dismiss/raise yet) is BUFFERED, not acted on.
        Assert.Equal(Kind.DropNoop, c.Advanced(9, "Z", choiceIndex: 0).Kind);
        Assert.Equal(1, c.PendingAdvanceCount);

        c.Reset();
        Assert.Equal(0, c.PendingAdvanceCount);
        Assert.Equal(0, c.PromptMirrorCount);
    }

    [Fact]
    public void OutOfOrder_MultiChoiceDismissBeforeRaise_StillResolvesToResultPage()
    {
        var c = new EventCorrelator();

        // Regression guard: a MULTI-choice buffered dismiss (singleChoice == false) must STILL resolve straight
        // to the result page (unchanged) — only single-choice events mirror the host modal.
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(5, "EX20", choiceIndex: 0).Kind);

        var raised = c.Raised(5, "EX20", singleChoice: false);
        Assert.Equal(Kind.ShowResultPage, raised.Kind);
        Assert.Equal(0, raised.ChoiceIndex);
        Assert.Equal(0, c.PendingCount);
        Assert.Equal(0, c.OpenCount);                      // resolved → not left open (unchanged)
    }

    [Fact]
    public void OutOfOrder_CloseOnlyDismissBeforeRaise_DropsNoop()
    {
        var c = new EventCorrelator();

        // A close-only (choiceIndex < 0) dismiss that beat its raise: the player never saw the dialog,
        // so when the raise lands there is nothing to display → DropNoop.
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(9, "EX_INFO", choiceIndex: -1).Kind);

        var raised = c.Raised(9, "EX_INFO");
        Assert.Equal(Kind.DropNoop, raised.Kind);
        Assert.Equal(0, c.PendingCount);
        Assert.Equal(0, c.OpenCount);
    }

    [Fact]
    public void TwoSameDefId_DistinctOccurrenceIds_DoNotCollide()
    {
        var c = new EventCorrelator();

        // Two occurrences of the SAME def-id "EX20", distinct occurrence ids → fully independent. On the single-slot
        // client the first shows and the second is DEFERRED in occId order (not a second simultaneous dialog), then
        // released when the first is dismissed — they never collide or cross-talk despite the reusable def-name.
        Assert.Equal(Kind.ShowDialog, c.Raised(1, "EX20").Kind);
        Assert.Equal(Kind.Enqueue, c.Raised(2, "EX20").Kind);
        Assert.Equal(1, c.OpenCount);
        Assert.Equal(1, c.QueuedCount);

        // Dismiss the FIRST (shown) occurrence — keyed to occId 1; the deferred occId 2 is untouched.
        var d1 = c.Dismissed(1, "EX20", choiceIndex: 0);
        Assert.Equal(Kind.ShowResultInPlace, d1.Kind);
        Assert.Equal(1, d1.OccurrenceId);

        // Slot freed → the second occurrence is released, still keyed to its OWN occId 2 (no cross-talk).
        Assert.True(c.TryDequeueNext(out var next2));
        Assert.Equal(2, next2.OccurrenceId);
        Assert.Equal(1, c.OpenCount);
        Assert.Equal(0, c.QueuedCount);

        var d2 = c.Dismissed(2, "EX20", choiceIndex: 1);
        Assert.Equal(Kind.ShowResultInPlace, d2.Kind);
        Assert.Equal(2, d2.OccurrenceId);
        Assert.Equal(0, c.OpenCount);
    }

    [Fact]
    public void UnknownDismiss_WhenBufferFull_DropsOldest()
    {
        var c = new EventCorrelator();

        // Fill the pending buffer past capacity with out-of-order dismisses; the oldest must be evicted
        // (bounded → no leak). Use ids 1..Max+overflow so they never collide.
        int overflow = 5;
        for (int i = 1; i <= EventCorrelator.MaxPendingDismiss + overflow; i++)
            Assert.Equal(Kind.BufferDismiss, c.Dismissed((ushort)i, "EX_X", choiceIndex: 0).Kind);

        Assert.Equal(EventCorrelator.MaxPendingDismiss, c.PendingCount);   // hard-bounded

        // A still-buffered recent one (the last inserted) resolves straight to its result page. Raised FIRST while
        // the single slot is free: a buffered-dismiss ShowResultPage is TERMINAL and does NOT occupy the slot.
        ushort recent = (ushort)(EventCorrelator.MaxPendingDismiss + overflow);
        var raisedRecent = c.Raised(recent, "EX_X");
        Assert.Equal(Kind.ShowResultPage, raisedRecent.Kind);

        // The OLDEST (occ=1) was evicted: its late raise finds no buffered dismiss → plain ShowDialog (slot still
        // free — the recent ShowResultPage above was terminal).
        var raisedOldest = c.Raised(1, "EX_X");
        Assert.Equal(Kind.ShowDialog, raisedOldest.Kind);
    }

    [Fact]
    public void Reset_ClearsOpenAndPending()
    {
        var c = new EventCorrelator();
        c.Raised(1, "A");
        c.Dismissed(2, "B", choiceIndex: 0);  // buffered (no raise)
        Assert.True(c.OpenCount > 0 && c.PendingCount > 0);

        c.Reset();
        Assert.Equal(0, c.OpenCount);
        Assert.Equal(0, c.PendingCount);
    }

    [Fact]
    public void Reset_OnSaveLoad_ClearsStarvedSlotQueueAndCompleted_LetsReusedOccIdShowAgain()
    {
        // Regression (mid-session save/load starvation): the client SyncEngine — hence this correlator — is NOT
        // recreated on a mid-session reload (only on full session teardown), and the host REUSES occurrence ids
        // across the reload. Reproduce the exact stale state a reload would leave behind — a BUSY single slot (a
        // shown dialog), a raise DEFERRED behind it, and a COMPLETED (dedup) occId — and prove the boundary
        // Reset() (what SyncEngine.ResetEventMirror drives) clears all three so post-reload raises are no longer
        // starved. Mirrors the ChoiceArbiter save-load reset precedent (ChoiceArbiterTests.Reset_OnSaveLoad_...).
        var c = new EventCorrelator();

        c.Raised(10, "A");                                                            // shown → occupies the single slot
        Assert.Equal(Kind.CloseDialog, c.Dismissed(10, "A", choiceIndex: -1).Kind);   // resolved → occId 10 COMPLETED, slot freed
        Assert.Equal(Kind.ShowDialog, c.Raised(20, "B").Kind);                        // shown again → single slot BUSY
        Assert.Equal(Kind.Enqueue, c.Raised(30, "C").Kind);                           // deferred behind the busy slot
        Assert.Equal(1, c.QueuedCount);

        // The starvation, with the STALE (un-reset) state: the reused occId 10 is dedup-Ignored (stale _completed),
        // and every fresh raise defers forever behind the still-busy slot (stale _shownSlot) — the client shows nothing.
        Assert.Equal(Kind.Ignore, c.Raised(10, "A").Kind);    // reused occId → silently dropped (completed dedup)
        Assert.Equal(Kind.Enqueue, c.Raised(40, "D").Kind);   // busy slot → deferred (never shows)

        // Mid-session save/load boundary reset.
        c.Reset();

        // Post-reload: slot free, queue empty, dedup set forgot the reused occIds → a fresh raise for the REUSED
        // occId 10 shows IMMEDIATELY (not Ignore, not Enqueue).
        Assert.Equal(0, c.QueuedCount);
        Assert.Equal(0, c.OpenCount);
        Assert.Equal(Kind.ShowDialog, c.Raised(10, "A").Kind);
        Assert.Equal(1, c.OpenCount);
    }

    // ─── DEDUP + client FIFO-order mirror (transport double-send + burst out-of-order) ───────────

    [Fact]  // (a) duplicate Raised for the same occId → only ONE show decision (transport double-send dedup)
    public void DuplicateRaise_SameOccId_IgnoresSecond()
    {
        var c = new EventCorrelator();

        Assert.Equal(Kind.ShowDialog, c.Raised(1, "EX20").Kind);
        Assert.Equal(1, c.OpenCount);

        // The transport double-sends the reliable EventRaised → a SECOND raise for the same occurrence arrives.
        // It must be an idempotent no-op (no second dialog), not another ShowDialog.
        Assert.Equal(Kind.Ignore, c.Raised(1, "EX20").Kind);
        Assert.Equal(1, c.OpenCount);     // still exactly one open
        Assert.Equal(0, c.QueuedCount);
    }

    [Fact]  // (b) two distinct events while one is open → second is QUEUED, not shown immediately
    public void SecondDistinctRaise_WhileOneShown_IsQueued()
    {
        var c = new EventCorrelator();

        Assert.Equal(Kind.ShowDialog, c.Raised(1, "A").Kind);   // first shows
        var second = c.Raised(2, "B");                          // arrives while #1 is up
        Assert.Equal(Kind.Enqueue, second.Kind);               // deferred, NOT a single-slot overwrite
        Assert.Equal(1, c.OpenCount);                          // only the first is shown
        Assert.Equal(1, c.QueuedCount);                        // the second waits
    }

    [Fact]  // (c) on Dismiss of the current → the next queued event is released to be shown
    public void OnDismissOfCurrent_NextQueuedIsShown()
    {
        var c = new EventCorrelator();
        c.Raised(1, "A");                 // shown
        c.Raised(2, "B");                 // queued
        Assert.Equal(1, c.QueuedCount);

        Assert.Equal(Kind.CloseDialog, c.Dismissed(1, "A", choiceIndex: -1).Kind);

        // Slot freed → the queued #2 pops and becomes the shown dialog.
        Assert.True(c.TryDequeueNext(out var next));
        Assert.Equal(Kind.ShowDialog, next.Kind);
        Assert.Equal(2, next.OccurrenceId);
        Assert.Equal(0, c.QueuedCount);
        Assert.Equal(1, c.OpenCount);     // #2 now shown

        // Nothing left to pop.
        Assert.False(c.TryDequeueNext(out _));
    }

    [Fact]  // (d) out-of-order arrival (higher occId before lower) → released in occId (host-emission) order
    public void QueuedEvents_ReleasedInOccIdOrder_NotArrivalOrder()
    {
        var c = new EventCorrelator();
        c.Raised(5, "A");                 // shown first (earliest arrival)
        Assert.Equal(Kind.Enqueue, c.Raised(7, "C").Kind);   // arrives before 6 (transport reorder)
        Assert.Equal(Kind.Enqueue, c.Raised(6, "B").Kind);
        Assert.Equal(2, c.QueuedCount);

        // Dismiss the shown #5 → next must be the LOWEST occId (6), not the first-arrived (7).
        c.Dismissed(5, "A", choiceIndex: -1);
        Assert.True(c.TryDequeueNext(out var first));
        Assert.Equal(6, first.OccurrenceId);

        // Dismiss #6 → then #7.
        c.Dismissed(6, "B", choiceIndex: -1);
        Assert.True(c.TryDequeueNext(out var secondPop));
        Assert.Equal(7, secondPop.OccurrenceId);
    }

    [Fact]  // (e) duplicate / already-dismissed dismiss → idempotent no-op, no throw
    public void DuplicateDismiss_AfterResolved_IsNoOp()
    {
        var c = new EventCorrelator();
        c.Raised(1, "A");
        Assert.Equal(Kind.CloseDialog, c.Dismissed(1, "A", choiceIndex: -1).Kind);

        // Transport double-sends the EventDismiss → the second dismiss for the now-resolved occurrence must be a
        // harmless no-op (it must NOT re-buffer as a phantom out-of-order dismiss that a later raise would resolve).
        Assert.Equal(Kind.Ignore, c.Dismissed(1, "A", choiceIndex: -1).Kind);
        Assert.Equal(0, c.PendingCount);
        Assert.Equal(0, c.OpenCount);
    }

    // ─── single-slot serialization must cover ALL raise kinds (single-choice bypass fix) ─────────

    [Fact]  // single-choice raise arriving while a dialog is shown MUST defer through the ONE slot (was: bypassed)
    public void SingleChoiceRaise_WhileOneShown_IsQueued_NotShownSimultaneously()
    {
        var c = new EventCorrelator();

        // Event #2 (2-window single-choice): its result-bearing dismiss beat the raise → a prompt mirror is shown,
        // which now OCCUPIES the single client slot (awaiting the host's advance).
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(2, "SDI_07", choiceIndex: 0).Kind);
        var r2 = c.Raised(2, "SDI_07", singleChoice: true, oneWindow: false);
        Assert.Equal(Kind.ShowDialog, r2.Kind);   // prompt mirror shown → slot busy
        Assert.Equal(1, c.OpenCount);

        // Event #3 (1-window single-choice) arrives while #2's prompt is up. TODAY it takes the buffered-dismiss
        // branch (which never consults the slot) and returns ShowResultPage → a SECOND window opens simultaneously
        // and the two resolve in advance-arrival order (host order 2,3 seen as 3,2 — THE BUG). It must DEFER instead.
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(3, "VoidOmen_7", choiceIndex: 0).Kind);
        var r3 = c.Raised(3, "VoidOmen_7", singleChoice: true, oneWindow: true);
        Assert.Equal(Kind.Enqueue, r3.Kind);      // ← FAILS today (returns ShowResultPage)
        Assert.Equal(1, c.OpenCount);             // still only #2 shown
        Assert.Equal(1, c.QueuedCount);           // #3 deferred behind the slot

        // Host advances #2 to its result page → slot frees → #3 is released in occId order, resolving straight to
        // its own result page (1-window). The two never coexist and surface in host emission order (2 then 3).
        Assert.Equal(Kind.ShowResultPage, c.Advanced(2, "SDI_07", choiceIndex: 0).Kind);
        Assert.True(c.TryDequeueNext(out var next3));
        Assert.Equal(3, next3.OccurrenceId);
        Assert.Equal(Kind.ShowResultPage, next3.Kind);
    }

    // ─── pending-dismiss eviction must never starve a DEFERRED raise (slot-wedge hardening) ─────────

    [Fact]  // eviction skips an occId whose raise is deferred in the queue; its release still resolves to the result
    public void PendingEviction_SkipsQueuedOccId_ReleasedRaiseStillResolvesToResult()
    {
        var c = new EventCorrelator();

        // A dialog is shown (slot busy); event 50's result-bearing dismiss lands, then its raise is DEFERRED.
        Assert.Equal(Kind.ShowDialog, c.Raised(100, "SHOWN").Kind);
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(50, "Q", choiceIndex: 0).Kind);
        Assert.Equal(Kind.Enqueue, c.Raised(50, "Q").Kind);

        // Flood the pending buffer to capacity with unrelated out-of-order dismisses. Eviction must SKIP
        // occ 50 (its raise is queued — evicting its dismiss would make the released raise pop as a plain
        // ShowDialog for an event the host ALREADY resolved → its dismiss never comes again → slot wedged
        // forever, all later dialogs starved) and evict the oldest NON-queued entry instead.
        for (int i = 0; i < EventCorrelator.MaxPendingDismiss; i++)
            Assert.Equal(Kind.BufferDismiss, c.Dismissed((ushort)(200 + i), "F", choiceIndex: 0).Kind);
        Assert.Equal(EventCorrelator.MaxPendingDismiss, c.PendingCount);   // still hard-bounded

        // Shown dialog closes → slot frees → occ 50 is released and MUST resolve to its result page
        // (its buffered dismiss survived eviction), not pop as an orphan ShowDialog.
        Assert.Equal(Kind.CloseDialog, c.Dismissed(100, "SHOWN", choiceIndex: -1).Kind);
        Assert.True(c.TryDequeueNext(out var released));
        Assert.Equal(50, released.OccurrenceId);
        Assert.Equal(Kind.ShowResultPage, released.Kind);
        Assert.Equal(0, released.ChoiceIndex);

        // The evicted entry was the oldest NON-queued flood id (200): its late raise finds no buffered
        // dismiss → plain ShowDialog (slot free — occ 50's release above was terminal).
        Assert.Equal(Kind.ShowDialog, c.Raised(200, "F").Kind);
    }

    [Fact]  // every buffered dismiss is queued-linked → eviction is refused (transient overshoot) and self-drains
    public void PendingEviction_AllCandidatesQueued_RefusesEviction_AndOvershootSelfDrains()
    {
        var c = new EventCorrelator();

        Assert.Equal(Kind.ShowDialog, c.Raised(1, "SHOWN").Kind);   // slot busy

        // Fill the buffer to capacity where EVERY pending dismiss belongs to a raise deferred in the queue.
        for (int i = 0; i < EventCorrelator.MaxPendingDismiss; i++)
        {
            ushort occ = (ushort)(10 + i);
            Assert.Equal(Kind.BufferDismiss, c.Dismissed(occ, "E", choiceIndex: 0).Kind);
            Assert.Equal(Kind.Enqueue, c.Raised(occ, "E").Kind);
        }
        Assert.Equal(EventCorrelator.MaxPendingDismiss, c.PendingCount);

        // One more unrelated out-of-order dismiss: every eviction candidate is queued-linked → REFUSE
        // eviction (evicting any would wedge the slot); the buffer exceeds the cap transiently.
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(999, "X", choiceIndex: 0).Kind);
        Assert.Equal(EventCorrelator.MaxPendingDismiss + 1, c.PendingCount);

        // The overshoot self-drains: closing the shown dialog releases each deferred raise terminally
        // (ShowResultPage consumes its pending entry; terminal → slot stays free → the drain continues).
        Assert.Equal(Kind.CloseDialog, c.Dismissed(1, "SHOWN", choiceIndex: -1).Kind);
        for (int i = 0; i < EventCorrelator.MaxPendingDismiss; i++)
        {
            Assert.True(c.TryDequeueNext(out var released));
            Assert.Equal(10 + i, released.OccurrenceId);
            Assert.Equal(Kind.ShowResultPage, released.Kind);
        }
        Assert.False(c.TryDequeueNext(out _));
        Assert.Equal(0, c.QueuedCount);
        Assert.Equal(1, c.PendingCount);   // only the unrelated 999 remains buffered — back under the cap
    }

    // ─── released raise whose build stash is missing (defensive): abort the show, never wedge the slot ────

    [Fact]
    public void AbortShow_OnReleasedRaiseWithMissingStash_FreesSlotAndDedups()
    {
        var c = new EventCorrelator();

        Assert.Equal(Kind.ShowDialog, c.Raised(1, "A").Kind);
        Assert.Equal(Kind.Enqueue, c.Raised(2, "B").Kind);
        Assert.Equal(Kind.CloseDialog, c.Dismissed(1, "A", choiceIndex: -1).Kind);

        // Occ 2 is released and decided ShowDialog → the slot is occupied for it...
        Assert.True(c.TryDequeueNext(out var released));
        Assert.Equal(Kind.ShowDialog, released.Kind);
        Assert.Equal(2, released.OccurrenceId);
        Assert.Equal(1, c.OpenCount);

        // ...but the (Unity-bound) caller finds NO build stash for it (defensive: should never happen) →
        // it must NOT show a null dialog; aborting frees the slot instead of wedging it forever.
        c.AbortShow(2);
        Assert.Equal(0, c.OpenCount);

        // Slot is free: the next raise shows immediately (no starvation)…
        Assert.Equal(Kind.ShowDialog, c.Raised(3, "C").Kind);
        // …and the aborted occurrence is terminally deduped (a late duplicate raise can't resurrect it).
        Assert.Equal(Kind.Ignore, c.Raised(2, "B").Kind);
    }

    [Fact]  // a burst mixing a plain raise and single-choice raises all route through the ONE slot, in occId order
    public void MixedPlainAndSingleChoiceBurst_ReleasedInOccIdOrder()
    {
        var c = new EventCorrelator();

        // #1 plain shows first and occupies the slot. #2 (2-window single-choice) and #3 (1-window single-choice)
        // arrive while #1 is up — their result-bearing dismisses beat their raises, but the busy slot DEFERS both.
        Assert.Equal(Kind.ShowDialog, c.Raised(1, "A").Kind);
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(2, "B", choiceIndex: 0).Kind);
        Assert.Equal(Kind.Enqueue, c.Raised(2, "B", singleChoice: true, oneWindow: false).Kind);
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(3, "C", choiceIndex: 0).Kind);
        Assert.Equal(Kind.Enqueue, c.Raised(3, "C", singleChoice: true, oneWindow: true).Kind);
        Assert.Equal(1, c.OpenCount);
        Assert.Equal(2, c.QueuedCount);

        // Close #1 → slot frees → the LOWEST occId (2) is released first, as its 2-window prompt mirror, which
        // re-occupies the slot; #3 stays deferred (draining stops behind the busy slot).
        Assert.Equal(Kind.CloseDialog, c.Dismissed(1, "A", choiceIndex: -1).Kind);
        Assert.True(c.TryDequeueNext(out var first));
        Assert.Equal(2, first.OccurrenceId);
        Assert.Equal(Kind.ShowDialog, first.Kind);        // #2 prompt mirror (occupies the slot)
        Assert.Equal(1, c.QueuedCount);                   // #3 still deferred
        Assert.False(c.TryDequeueNext(out _));            // slot busy again → #3 held back

        // Advance #2 → slot frees → #3 released (1-window → straight to its result page). Host order 2 → 3 preserved,
        // and the two never coexisted on screen.
        Assert.Equal(Kind.ShowResultPage, c.Advanced(2, "B", choiceIndex: 0).Kind);
        Assert.True(c.TryDequeueNext(out var second));
        Assert.Equal(3, second.OccurrenceId);
        Assert.Equal(Kind.ShowResultPage, second.Kind);
        Assert.Equal(0, c.QueuedCount);
    }
}
