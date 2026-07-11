using Multiplayer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the HOST reveal-hold ARM gate (Batch 2): should the host arm the synchronized-
// reveal barrier at tactical LAUNCH, so it holds behind its loading screen until every client reports
// load-complete? Only when the feature flag is on AND this peer is the co-op host AND a co-op session is
// live AND the host is already in a started co-op level (SessionStarted — the curtain hold requires it,
// so arming without it would be a no-op hold). The hold itself (SaveTransferMath.HoldCurtain), the LOADED
// release (SaveTransferMath.BarrierReleased) and the all-done release (RosterProgressTracker.AllDone) are
// existing, separately-tested pure predicates — this gate only pins the NEW launch-time arm decision.
// Pre-fix these FAIL by not compiling — TacticalEntryBarrierGate did not exist (canonical failing test).
public class TacticalEntryBarrierGateTests
{
    [Fact]
    public void AllConditions_Arms()
        => Assert.True(TacticalEntryBarrierGate.ShouldArmHostReveal(
            isHost: true, sessionActive: true, sessionStarted: true, flagOn: true));

    [Fact]
    public void FlagOff_DoesNotArm()
        => Assert.False(TacticalEntryBarrierGate.ShouldArmHostReveal(
            isHost: true, sessionActive: true, sessionStarted: true, flagOn: false));

    [Fact]
    public void NonHost_DoesNotArm()
        // A client holds via its own save-transfer flow, never a host reveal barrier.
        => Assert.False(TacticalEntryBarrierGate.ShouldArmHostReveal(
            isHost: false, sessionActive: true, sessionStarted: true, flagOn: true));

    [Fact]
    public void NoSession_DoesNotArm()
        // No live co-op session → nobody to wait for.
        => Assert.False(TacticalEntryBarrierGate.ShouldArmHostReveal(
            isHost: true, sessionActive: false, sessionStarted: true, flagOn: true));

    [Fact]
    public void NotStarted_DoesNotArm()
        // The curtain hold requires SessionStarted; arming without it would set _revealed=false yet never hold.
        => Assert.False(TacticalEntryBarrierGate.ShouldArmHostReveal(
            isHost: true, sessionActive: true, sessionStarted: false, flagOn: true));

    // Full truth-table pin: flip each precondition off from the all-true baseline → never arms.
    [Theory]
    [InlineData(true,  true,  true,  true,  true)]   // baseline: all preconditions met
    [InlineData(false, true,  true,  true,  false)]  // not host
    [InlineData(true,  false, true,  true,  false)]  // no session
    [InlineData(true,  true,  false, true,  false)]  // not started
    [InlineData(true,  true,  true,  false, false)]  // flag off
    public void TruthTable(bool isHost, bool sessionActive, bool sessionStarted, bool flagOn, bool expected)
        => Assert.Equal(expected, TacticalEntryBarrierGate.ShouldArmHostReveal(
            isHost, sessionActive, sessionStarted, flagOn));
}
