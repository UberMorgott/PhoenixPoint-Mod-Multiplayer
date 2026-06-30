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

        // The OLDEST (occ=1) was evicted: its late raise finds no buffered dismiss → plain ShowDialog.
        var raisedOldest = c.Raised(1, "EX_X");
        Assert.Equal(Kind.ShowDialog, raisedOldest.Kind);

        // A still-buffered recent one (the last inserted) resolves to its result page.
        ushort recent = (ushort)(EventCorrelator.MaxPendingDismiss + overflow);
        var raisedRecent = c.Raised(recent, "EX_X");
        Assert.Equal(Kind.ShowResultPage, raisedRecent.Kind);
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
}
