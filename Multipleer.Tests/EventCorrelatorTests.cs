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
    public void OutOfOrder_SingleChoiceDismissBeforeRaise_MirrorsModalNotResultPage()
    {
        var c = new EventCorrelator();

        // Single-choice geoscape event: the host auto-completes the lone choice at trigger and broadcasts the
        // result-bearing Dismiss(occ=5, choice=0) BEFORE the Raise. The CLIENT must MIRROR the host's native
        // flavor modal (ShowDialog) — NOT jump to a synthetic result page, which is a different dialog STAGE
        // than the host is still showing.
        var dismissed = c.Dismissed(5, "EX20", choiceIndex: 0);
        Assert.Equal(Kind.BufferDismiss, dismissed.Kind);
        Assert.Equal(1, c.PendingCount);

        var raised = c.Raised(5, "EX20", singleChoice: true);
        Assert.Equal(Kind.ShowDialog, raised.Kind);        // mirror the host's native modal, not the result page
        Assert.Equal(0, c.PendingCount);                   // buffer drained
        Assert.Equal(1, c.OpenCount);                      // tracked as open → closes locally on the player's OK
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

        // Two occurrences of the SAME def-id "EX20", distinct occurrence ids → fully independent.
        Assert.Equal(Kind.ShowDialog, c.Raised(1, "EX20").Kind);
        Assert.Equal(Kind.ShowDialog, c.Raised(2, "EX20").Kind);
        Assert.Equal(2, c.OpenCount);

        // Dismiss the SECOND one — the first stays open, no cross-talk.
        var d2 = c.Dismissed(2, "EX20", choiceIndex: 1);
        Assert.Equal(Kind.ShowResultInPlace, d2.Kind);
        Assert.Equal(2, d2.OccurrenceId);
        Assert.Equal(1, c.OpenCount);   // occurrence 1 still open

        var d1 = c.Dismissed(1, "EX20", choiceIndex: 0);
        Assert.Equal(Kind.ShowResultInPlace, d1.Kind);
        Assert.Equal(1, d1.OccurrenceId);
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
}
