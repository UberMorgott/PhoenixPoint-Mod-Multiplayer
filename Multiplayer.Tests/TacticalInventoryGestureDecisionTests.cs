using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE decision tests for the per-gesture inventory re-baseline guard (dead-rail RCA 2026-07-13).
/// Pins the swallow fix: an EMPTY gesture diff must NOT re-baseline the UI lists — <c>_initialItems</c>
/// is the exact diff the native deferred close-commit (AttemptMoveItems) and the mod's close-rail
/// CaptureMoves consume; wiping it on an empty diff makes the move vanish on host AND clients.
/// The Harmony/reflection glue (TacticalInventorySync.OnTacticalGesture) is in-game verified.
/// </summary>
public class TacticalInventoryGestureDecisionTests
{
    [Fact]
    public void EmptyDiff_DoesNotReBaseline()
    {
        // 0 moves + 0 unsynced: the gesture diff saw nothing — the native baseline must survive so the
        // native close-commit / close-rail fallback still commit the move the diff missed.
        Assert.False(TacticalInventoryGestureDecision.ShouldReBaseline(moveCount: 0, unsyncedCount: 0));
    }

    [Theory]
    [InlineData(1, 0)]   // relayed move(s) → re-baseline (double-relay prevention)
    [InlineData(0, 1)]   // unsynced drag → re-baseline (the revert-repaint resets the view from truth)
    [InlineData(2, 3)]   // mixed batch
    public void NonEmptyDiff_ReBaselines(int moves, int unsynced)
    {
        Assert.True(TacticalInventoryGestureDecision.ShouldReBaseline(moves, unsynced));
    }
}
