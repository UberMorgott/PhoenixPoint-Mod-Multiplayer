using Multipleer.UI;
using Xunit;

// Pure, Unity-free decision behind LoadOverlayController's per-frame Refresh. The live controller builds
// the snapshot from engine.SaveTransfer (TransferActive / InPhase2 / tracker per-peer done) every frame and
// calls ShouldShow; the boolean rule is extracted here so the "is a genuine co-op load in progress and not
// everyone finished?" decision is asserted directly, independent of Unity.
//
// VISIBLE  ⇔  (transferActive || inPhase2)            // a genuine co-op save-transfer/load window is open
//             && !allPeersDone                          // …and not every participating peer has reported done
// FALSE when no transfer/phase-2 is active (must NOT stick visible on leftover/stale flags), and FALSE the
// instant the LAST peer reports done. The authoritative completion is the per-peer tracker done-set
// (expectedDone >= expectedTotal), NOT the volatile _begun/_loadCompleteSent, so the overlay can't get stuck.
public class LoadOverlayVisibilityTests
{
    [Fact]
    public void MidTransfer_PeerNotComplete_Shows()
    {
        // Transfer in flight, two participating peers, only one done → still loading → SHOW.
        Assert.True(LoadOverlayVisibility.ShouldShow(
            transferActive: true, inPhase2: false, expectedPeers: 2, donePeers: 1));
    }

    [Fact]
    public void AllPeersComplete_Hides()
    {
        // Genuine load window but every participating peer reported done → nothing left to wait on → HIDE.
        Assert.False(LoadOverlayVisibility.ShouldShow(
            transferActive: true, inPhase2: true, expectedPeers: 2, donePeers: 2));
    }

    [Fact]
    public void NoTransferNoPhase2_Hides_EvenWithStaleDoneCounts()
    {
        // No transfer + not in phase-2 → no genuine co-op load is happening. Must be FALSE regardless of any
        // leftover done/expected counts (the stale-flag case the old event-driven show got stuck on).
        Assert.False(LoadOverlayVisibility.ShouldShow(
            transferActive: false, inPhase2: false, expectedPeers: 2, donePeers: 0));
        Assert.False(LoadOverlayVisibility.ShouldShow(
            transferActive: false, inPhase2: false, expectedPeers: 0, donePeers: 0));
    }

    [Fact]
    public void TransferActive_BeforePhase2_Shows()
    {
        // Host/lobby transfer is live but phase-2 has not begun yet (inPhase2 false) → still SHOW.
        Assert.True(LoadOverlayVisibility.ShouldShow(
            transferActive: true, inPhase2: false, expectedPeers: 1, donePeers: 0));
    }

    [Fact]
    public void InPhase2_Shows()
    {
        // This peer is in phase-2 world-load (transferActive may already be false on the client) → SHOW.
        Assert.True(LoadOverlayVisibility.ShouldShow(
            transferActive: false, inPhase2: true, expectedPeers: 1, donePeers: 0));
    }
}
