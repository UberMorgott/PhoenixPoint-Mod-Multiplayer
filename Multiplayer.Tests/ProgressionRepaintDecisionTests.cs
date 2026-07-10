using Multiplayer.Network.Sync.State;
using Xunit;
using Outcome = Multiplayer.Network.Sync.State.ProgressionRepaintDecision.Outcome;

// Progression-panel repaint gating (stat-sync reactivity RCA 2026-07-10): a FULL re-drive
// (SetCharacterProgression) re-snapshots the local edit buffer from the model, so it must fire for every
// apply that changed what the panel SHOWS — and it must NEVER defer forever. Once a local +/- / ability
// edit is pending: a positive hit on the OPEN soldier is a ConflictRepaint (remote wins, discard local); a
// different soldier's change that moved the shared faction-SP pool (or an unknown apply) is a PartialRepaint
// (refresh the shared label, keep the buffer); anything else is a Skip (hands off the buffer).
public class ProgressionRepaintDecisionTests
{
    private const long Viewed = 42;

    // ── No pending edit: full re-drive when relevant, Skip otherwise ──

    [Fact]
    public void ViewedSoldierStamped_NoPending_Repaints()
    {
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, Viewed, new long[] { 7, Viewed }, false));
    }

    [Fact]
    public void FactionSpOnly_NoPending_Repaints()
    {
        // The shared SP pool total is displayed on the panel for every soldier — a pool-only apply
        // (e.g. the other player bought an ability on a DIFFERENT soldier) must still repaint.
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, Viewed, new long[0], true));
    }

    [Fact]
    public void UnknownStamp_NoPending_RepaintsConservatively()
    {
        // Null stamp = caller cannot say what changed (ctx-less fan-out) → legacy conservative repaint;
        // a missed repaint is a stale mirror forever, an extra one is an idempotent re-read.
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, Viewed, null, false));
    }

    [Fact]
    public void UnrelatedSoldierStamped_NoPending_Skips()
    {
        // Another soldier's edit with no pool change must not reset the viewed panel.
        Assert.Equal(Outcome.Skip,
            ProgressionRepaintDecision.Decide(false, Viewed, new long[] { 7, 9 }, false));
    }

    // ── Pending edit: ConflictRepaint (open soldier hit) / PartialRepaint (shared pool) / Skip ──

    [Fact]
    public void ViewedSoldierStamped_PendingLocalEdit_ConflictRepaints()
    {
        // Remote changed the very soldier being edited → full re-drive, remote wins, local clicks discarded.
        Assert.Equal(Outcome.ConflictRepaint,
            ProgressionRepaintDecision.Decide(true, Viewed, new long[] { Viewed }, false));
    }

    [Fact]
    public void ViewedSoldierStamped_PendingLocalEdit_ConflictWins_EvenWithFactionSp()
    {
        // A same-unit hit is a conflict regardless of the pool tail — the open soldier changed remotely.
        Assert.Equal(Outcome.ConflictRepaint,
            ProgressionRepaintDecision.Decide(true, Viewed, new long[] { 7, Viewed }, true));
    }

    [Fact]
    public void FactionSpOnly_DifferentSoldier_PendingLocalEdit_PartialRepaints()
    {
        // Another soldier's spend moved the shared pool → refresh the pool label, keep the local buffer.
        Assert.Equal(Outcome.PartialRepaint,
            ProgressionRepaintDecision.Decide(true, Viewed, new long[] { 7 }, true));
    }

    [Fact]
    public void FactionSpOnly_EmptyStamp_PendingLocalEdit_PartialRepaints()
    {
        Assert.Equal(Outcome.PartialRepaint,
            ProgressionRepaintDecision.Decide(true, Viewed, new long[0], true));
    }

    [Fact]
    public void UnknownStamp_PendingLocalEdit_PartialRepaints_NeverDiscardsBuffer()
    {
        // Unknown apply must not eat uncommitted clicks — refresh the shared label at most, keep the buffer.
        Assert.Equal(Outcome.PartialRepaint,
            ProgressionRepaintDecision.Decide(true, Viewed, null, false));
    }

    [Fact]
    public void UnrelatedSoldierStamped_PendingLocalEdit_Skips()
    {
        // Different soldier, no pool change → nothing on the panel is stale, keep the buffer.
        Assert.Equal(Outcome.Skip,
            ProgressionRepaintDecision.Decide(true, Viewed, new long[] { 7 }, false));
    }

    // ── Unresolved viewed id (read miss = 0) ──

    [Fact]
    public void UnresolvedViewedId_NoPending_InheritsConservativeContract()
    {
        Assert.Equal(Outcome.Repaint,
            ProgressionRepaintDecision.Decide(false, 0, new long[] { 7 }, false));
        Assert.Equal(Outcome.Skip,
            ProgressionRepaintDecision.Decide(false, 0, new long[0], false));
    }

    [Fact]
    public void UnresolvedViewedId_PendingLocalEdit_NeverConflicts()
    {
        // id==0 is never a POSITIVE hit → never discard the buffer; pool tail → Partial, else Skip.
        Assert.Equal(Outcome.PartialRepaint,
            ProgressionRepaintDecision.Decide(true, 0, new long[] { 7 }, true));
        Assert.Equal(Outcome.Skip,
            ProgressionRepaintDecision.Decide(true, 0, new long[] { 7 }, false));
    }

    // ── FIX 1: over-committed shared faction-SP pool guard (CanPartialShiftFactionSp) ──
    // A PartialRepaint shifts the panel's SP baseline to the live pool while preserving the local pending draw
    // (starting − current). If the live pool can no longer cover that draw the shift would push
    // _currentFactionPoints negative — the guard returns false so the caller escalates to a ConflictRepaint.

    [Fact]
    public void PartialShift_LivePoolCoversPendingDraw_Affordable()
    {
        // starting 10, current 4 → local pending draw 6; remote spent 2 → live 8 ≥ 6 → shift OK, current → 2.
        Assert.True(ProgressionRepaintDecision.CanPartialShiftFactionSp(8, 10, 4, out int cur));
        Assert.Equal(2, cur);
        Assert.True(cur >= 0);
    }

    [Fact]
    public void PartialShift_LivePoolBelowPendingDraw_OverCommitted()
    {
        // Live pool 5 < pending draw 6 → shifting would set current = -1 → not affordable.
        Assert.False(ProgressionRepaintDecision.CanPartialShiftFactionSp(5, 10, 4, out int cur));
        Assert.True(cur < 0);
    }

    [Fact]
    public void PartialShift_LiveExactlyCoversDraw_AffordableAtZero()
    {
        // Boundary: live 6 == pending draw 6 → current shifts to exactly 0 (never below).
        Assert.True(ProgressionRepaintDecision.CanPartialShiftFactionSp(6, 10, 4, out int cur));
        Assert.Equal(0, cur);
    }

    [Fact]
    public void PartialShift_NoPendingDraw_TracksLivePool()
    {
        // No local spend (starting == current) → any non-negative live pool is affordable; current = live.
        Assert.True(ProgressionRepaintDecision.CanPartialShiftFactionSp(3, 10, 10, out int cur));
        Assert.Equal(3, cur);
    }

    [Fact]
    public void PartialShift_PoolUnchanged_PreservesCurrent()
    {
        // live == starting (no remote move) → current is preserved exactly (shift is a no-op).
        Assert.True(ProgressionRepaintDecision.CanPartialShiftFactionSp(10, 10, 4, out int cur));
        Assert.Equal(4, cur);
    }

    [Fact]
    public void Defer_IsUnreachable_NoInputProducesIt()
    {
        // Reactivity mandate: exhaustively, no combination returns a defer-style "do nothing but owe a drain".
        foreach (bool pending in new[] { false, true })
            foreach (bool sp in new[] { false, true })
                foreach (var stamp in new[] { null, new long[0], new long[] { Viewed }, new long[] { 7 } })
                {
                    var o = ProgressionRepaintDecision.Decide(pending, Viewed, stamp, sp);
                    Assert.Contains(o, new[] { Outcome.Repaint, Outcome.PartialRepaint, Outcome.ConflictRepaint, Outcome.Skip });
                }
    }
}
