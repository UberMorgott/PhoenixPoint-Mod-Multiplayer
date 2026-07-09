using Multiplayer.Network.Sync.State;
using Xunit;
using Outcome = Multiplayer.Network.Sync.State.ProgressionRepaintDecision.Outcome;

// Progression-panel repaint gating (stat-sync reactivity RCA 2026-07-10): the panel re-drive
// (SetCharacterProgression) re-snapshots the local edit buffer from the model, so it must fire for every
// apply that changed what the panel SHOWS (viewed soldier state or the shared faction-SP pool), defer —
// never skip forever — behind a pending local allocation, and leave the buffer alone on unrelated traffic.
public class ProgressionRepaintDecisionTests
{
    private const long Viewed = 42;

    [Fact]
    public void ViewedSoldierStamped_NoPending_Repaints()
    {
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, false, Viewed, new long[] { 7, Viewed }, false));
    }

    [Fact]
    public void FactionSpOnly_NoPending_Repaints()
    {
        // The shared SP pool total is displayed on the panel for every soldier — a pool-only apply
        // (e.g. the other player bought an ability on a DIFFERENT soldier) must still repaint.
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, false, Viewed, new long[0], true));
    }

    [Fact]
    public void UnknownStamp_NoPending_RepaintsConservatively()
    {
        // Null stamp = caller cannot say what changed (ctx-less fan-out) → legacy conservative repaint;
        // a missed repaint is a stale mirror forever, an extra one is an idempotent re-read.
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, false, Viewed, null, false));
    }

    [Fact]
    public void UnrelatedSoldierStamped_NoPending_Skips()
    {
        // The hourly bulk sweep / another soldier's edit must not reset the viewed panel's edit buffer.
        Assert.Equal(Outcome.Skip,
            ProgressionRepaintDecision.Decide(false, false, Viewed, new long[] { 7, 9 }, false));
    }

    [Fact]
    public void ViewedSoldierStamped_PendingLocalEdit_Defers()
    {
        // Mid-edit the repaint would eat the user's uncommitted +/- clicks → defer, owed a drain.
        Assert.Equal(Outcome.Defer,
            ProgressionRepaintDecision.Decide(true, false, Viewed, new long[] { Viewed }, false));
    }

    [Fact]
    public void FactionSpOnly_PendingLocalEdit_Defers()
    {
        Assert.Equal(Outcome.Defer,
            ProgressionRepaintDecision.Decide(true, false, Viewed, new long[0], true));
    }

    [Fact]
    public void UnknownStamp_PendingLocalEdit_Defers()
    {
        Assert.Equal(Outcome.Defer,
            ProgressionRepaintDecision.Decide(true, false, Viewed, null, false));
    }

    [Fact]
    public void UnrelatedSoldierStamped_PendingLocalEdit_Skips_DoesNotArmDrain()
    {
        Assert.Equal(Outcome.Skip,
            ProgressionRepaintDecision.Decide(true, false, Viewed, new long[] { 7 }, false));
    }

    [Fact]
    public void OwedDrain_NoPending_RepaintsEvenOnUnrelatedApply()
    {
        // A deferred repaint drains on the first idle pass — it must never wait for a relevant apply.
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, true, Viewed, new long[] { 7 }, false));
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, true, Viewed, new long[0], false));
    }

    [Fact]
    public void OwedDrain_StillPending_StaysDeferred()
    {
        // Pending edit still in progress: a relevant apply keeps deferring, unrelated stays hands-off.
        Assert.Equal(Outcome.Defer,
            ProgressionRepaintDecision.Decide(true, true, Viewed, new long[] { Viewed }, false));
        Assert.Equal(Outcome.Skip,
            ProgressionRepaintDecision.Decide(true, true, Viewed, new long[] { 7 }, false));
    }

    [Fact]
    public void UnresolvedViewedId_AnythingStamped_TreatedRelevant()
    {
        // Id read miss (0) inherits AugmentRepaintDecision's conservative contract.
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, false, 0, new long[] { 7 }, false));
        Assert.Equal(Outcome.Skip,
            ProgressionRepaintDecision.Decide(false, false, 0, new long[0], false));
    }
}
