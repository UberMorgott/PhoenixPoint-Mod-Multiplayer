using Multiplayer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the CLIENT entry-via-save stall watchdog (Batch 1): a client that suppressed its
// self-launch and is waiting on the host save-transfer must fall back to the legacy self-launch ONLY when
// the host clearly never sent (deadline passed, deploy still un-hydrated, no transfer arrived, no level
// built). Any sign of progress (chunk arrived / level built / already hydrated) must cancel the fallback.
public class TacticalEntryStallGateTests
{
    [Fact]
    public void DeadlinePassed_NoTransfer_FallsBack()
        => Assert.True(TacticalEntryStallGate.ShouldFallbackToSelfLaunch(
            deadlinePassed: true, stillPending: true, transferArrived: false, liveTacticalLevel: false));

    [Fact]
    public void BeforeDeadline_KeepsWaiting()
        => Assert.False(TacticalEntryStallGate.ShouldFallbackToSelfLaunch(
            deadlinePassed: false, stillPending: true, transferArrived: false, liveTacticalLevel: false));

    [Fact]
    public void TransferArrived_NoFallback()
        // Chunks are flowing → the level will build; never yank it into a self-launch.
        => Assert.False(TacticalEntryStallGate.ShouldFallbackToSelfLaunch(
            deadlinePassed: true, stillPending: true, transferArrived: true, liveTacticalLevel: false));

    [Fact]
    public void LiveTacticalLevel_NoFallback()
        // The save-built level already exists (hydrate pending) → no fallback.
        => Assert.False(TacticalEntryStallGate.ShouldFallbackToSelfLaunch(
            deadlinePassed: true, stillPending: true, transferArrived: false, liveTacticalLevel: true));

    [Fact]
    public void NotPending_NoFallback()
        // Deploy already hydrated/cleared → nothing to fall back for.
        => Assert.False(TacticalEntryStallGate.ShouldFallbackToSelfLaunch(
            deadlinePassed: true, stillPending: false, transferArrived: false, liveTacticalLevel: false));
}
