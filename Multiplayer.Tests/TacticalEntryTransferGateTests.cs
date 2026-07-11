using Multiplayer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the HOST entry-via-save gate (Batch 1): should the host ship a byte-identical
// mid-tactical save so a client BUILDS its battle from the host's exact bytes (positions/loot/objectives/
// turn) instead of self-launching + reconciling? Only when the feature flag is on AND this peer is the
// co-op host AND a co-op session is live AND the current level is tactical (a geoscape save here would be
// the wrong save) AND no other transfer is already in flight (never stack a 2nd over an open barrier).
// Pre-fix these FAIL by not compiling — TacticalEntryTransferGate did not exist (canonical failing test).
public class TacticalEntryTransferGateTests
{
    [Fact]
    public void AllConditions_SendsSave()
        => Assert.True(TacticalEntryTransferGate.ShouldSendTacticalSave(
            isHost: true, sessionActive: true, isTactical: true, transferActive: false, flagOn: true));

    [Fact]
    public void FlagOff_DoesNotSend()
        => Assert.False(TacticalEntryTransferGate.ShouldSendTacticalSave(
            isHost: true, sessionActive: true, isTactical: true, transferActive: false, flagOn: false));

    [Fact]
    public void NonHost_DoesNotSend()
        // Only the host authors + ships the authoritative save; a client never does.
        => Assert.False(TacticalEntryTransferGate.ShouldSendTacticalSave(
            isHost: false, sessionActive: true, isTactical: true, transferActive: false, flagOn: true));

    [Fact]
    public void NoSession_DoesNotSend()
        // No live co-op session → nobody to ship the save to.
        => Assert.False(TacticalEntryTransferGate.ShouldSendTacticalSave(
            isHost: true, sessionActive: false, isTactical: true, transferActive: false, flagOn: true));

    [Fact]
    public void NotTactical_DoesNotSend()
        // A geoscape save at this seam would be the WRONG save — only fire mid-tactical.
        => Assert.False(TacticalEntryTransferGate.ShouldSendTacticalSave(
            isHost: true, sessionActive: true, isTactical: false, transferActive: false, flagOn: true));

    [Fact]
    public void TransferInFlight_DoesNotSend()
        // Never stack a 2nd transfer over an already-open barrier.
        => Assert.False(TacticalEntryTransferGate.ShouldSendTacticalSave(
            isHost: true, sessionActive: true, isTactical: true, transferActive: true, flagOn: true));

    // Full truth-table pin: flip each precondition off from the all-true baseline → never sends.
    [Theory]
    [InlineData(true,  true,  true,  false, true,  true)]   // baseline: all preconditions met
    [InlineData(false, true,  true,  false, true,  false)]  // not host
    [InlineData(true,  false, true,  false, true,  false)]  // no session
    [InlineData(true,  true,  false, false, true,  false)]  // not tactical
    [InlineData(true,  true,  true,  true,  true,  false)]  // transfer in flight
    [InlineData(true,  true,  true,  false, false, false)]  // flag off
    public void TruthTable(bool isHost, bool sessionActive, bool isTactical, bool transferActive, bool flagOn, bool expected)
        => Assert.Equal(expected, TacticalEntryTransferGate.ShouldSendTacticalSave(
            isHost, sessionActive, isTactical, transferActive, flagOn));
}
