using Multiplayer.UI;
using Xunit;

// Pure, Unity-free decision behind LoadOverlayController's per-frame Update. The live controller builds the
// snapshot from engine.SaveTransfer (LoadPhaseStarted / InPhase2 / tracker per-peer done) every frame and
// calls ShouldShow; the boolean rule is extracted here so the "is the native load actually started and not
// everyone finished?" decision is asserted directly, independent of Unity.
//
// VISIBLE  ⇔  (loadStarted || inPhase2)            // the NATIVE loading curtain has started the load
//             && !allPeersDone                       // …and not every participating peer has reported done
//
// THE BUG THIS PINS: the gate keys on loadStarted (curtain entered "Loading" = LoadPhaseStarted), NOT on the
// command-time TransferActive. TransferActive is true the instant the host presses PLAY while still in the
// LOBBY (barrier open) — gating on it popped the overlay too early. loadStarted is true only once the real
// mission-loading curtain has dropped, so the overlay appears exactly at load-start, never in the lobby.
public class LoadOverlayVisibilityTests
{
    [Fact]
    public void LobbyAfterPlay_LoadNotStarted_Hides()
    {
        // THE BUG: host pressed PLAY → barrier open (TransferActive would be true) but the native loading
        // curtain has NOT dropped yet (loadStarted false) and phase-2 has not begun (inPhase2 false).
        // The overlay MUST stay hidden in the lobby regardless of any peer counts.
        Assert.False(LoadOverlayVisibility.ShouldShow(
            loadStarted: false, inPhase2: false, expectedPeers: 1, donePeers: 0));
        Assert.False(LoadOverlayVisibility.ShouldShow(
            loadStarted: false, inPhase2: false, expectedPeers: 2, donePeers: 0));
    }

    [Fact]
    public void LoadStarted_PeerNotComplete_Shows()
    {
        // Native loading curtain has dropped (loadStarted), two participating peers, only one done →
        // still loading → SHOW.
        Assert.True(LoadOverlayVisibility.ShouldShow(
            loadStarted: true, inPhase2: false, expectedPeers: 2, donePeers: 1));
    }

    [Fact]
    public void LoadStarted_SinglePeerNotDone_Shows()
    {
        // Load curtain dropped, one participating peer, not done yet → SHOW (overlay appears AT load-start).
        Assert.True(LoadOverlayVisibility.ShouldShow(
            loadStarted: true, inPhase2: false, expectedPeers: 1, donePeers: 0));
    }

    [Fact]
    public void AllPeersComplete_Hides()
    {
        // Genuine load window but every participating peer reported done → nothing left to wait on → HIDE.
        Assert.False(LoadOverlayVisibility.ShouldShow(
            loadStarted: true, inPhase2: true, expectedPeers: 2, donePeers: 2));
    }

    [Fact]
    public void NoLoadNoPhase2_Hides_EvenWithStaleDoneCounts()
    {
        // No load curtain + not in phase-2 → no genuine co-op load is happening. Must be FALSE regardless of
        // any leftover done/expected counts (the stale-flag case the old event-driven show got stuck on).
        Assert.False(LoadOverlayVisibility.ShouldShow(
            loadStarted: false, inPhase2: false, expectedPeers: 2, donePeers: 0));
        Assert.False(LoadOverlayVisibility.ShouldShow(
            loadStarted: false, inPhase2: false, expectedPeers: 0, donePeers: 0));
    }

    [Fact]
    public void InPhase2_Shows()
    {
        // This peer is in phase-2 world-load (loadStarted may already be false on the client once the curtain
        // cleared its captured level, but the barrier/world-load is still in flight) → SHOW.
        Assert.True(LoadOverlayVisibility.ShouldShow(
            loadStarted: false, inPhase2: true, expectedPeers: 1, donePeers: 0));
    }
}
