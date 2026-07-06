using Multiplayer.Network.Sync;
using Xunit;

public class IntentDedupTests
{
    [Fact]
    public void IsNew_FirstTrueRepeatFalse()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(7UL, 100, 1u));
        Assert.False(d.IsNew(7UL, 100, 1u));   // reliable-transport double-send
        Assert.True(d.IsNew(7UL, 100, 2u));
    }

    [Fact]
    public void IsNew_SurfaceNamespaced()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(7UL, 100, 1u));
        Assert.True(d.IsNew(7UL, 101, 1u));    // same nonce, different surface → distinct
    }

    [Fact]
    public void IsNew_PeerNamespaced_TwoClientsSameNonceBothAccepted()
    {
        // 3+ player regression: client nonces are client-LOCAL monotonic, so two clients both emit
        // nonce 1 on the same surface. Without the peer in the key the 2nd client's intent was
        // silently dropped as a "duplicate".
        var d = new IntentDedup();
        Assert.True(d.IsNew(7UL, 100, 1u));    // client A, first move
        Assert.True(d.IsNew(8UL, 100, 1u));    // client B, same surface+nonce → MUST be accepted
        Assert.False(d.IsNew(7UL, 100, 1u));   // client A double-send → still dropped
        Assert.False(d.IsNew(8UL, 100, 1u));   // client B double-send → still dropped
    }

    [Fact]
    public void IsNew_EvictsOldestPastCapacity_FloorIs16()
    {
        var d = new IntentDedup(capacity: 16);
        for (uint n = 1; n <= 16; n++) Assert.True(d.IsNew(7UL, 100, n));
        Assert.True(d.IsNew(7UL, 100, 17u));   // overflow evicts nonce 1
        Assert.True(d.IsNew(7UL, 100, 1u));    // 1 was evicted → seen as new again
        Assert.False(d.IsNew(7UL, 100, 17u));  // a recent one is still deduped
    }

    [Fact]
    public void Reset_Clears()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(7UL, 100, 1u));
        d.Reset();
        Assert.True(d.IsNew(7UL, 100, 1u));    // after reset the same key is new again
    }

    [Fact]
    public void ResetPeer_ClearsOnlyThatPeersWindow()
    {
        // Rejoin case (rca-3 audit b): a rejoining client keeps its STABLE Steam id but restarts its
        // client-local nonce counter at 1 — without the per-peer reset its own pre-rejoin window
        // silently eats its first post-join intents.
        var d = new IntentDedup();
        Assert.True(d.IsNew(7UL, 100, 1u));
        Assert.True(d.IsNew(7UL, 100, 2u));
        Assert.True(d.IsNew(8UL, 100, 1u));

        d.ResetPeer(7UL);

        Assert.True(d.IsNew(7UL, 100, 1u));    // rejoiner's restarted nonces accepted again
        Assert.True(d.IsNew(7UL, 100, 2u));
        Assert.False(d.IsNew(8UL, 100, 1u));   // other peer's window INTACT (its double-send still drops)
    }

    [Fact]
    public void ResetPeer_UnknownPeerIsNoOp()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(7UL, 100, 1u));
        d.ResetPeer(42UL);
        Assert.False(d.IsNew(7UL, 100, 1u));   // existing window untouched
    }

    [Fact]
    public void ResetPeer_EvictionRingStaysConsistent()
    {
        var d = new IntentDedup(capacity: 16);
        for (uint n = 1; n <= 8; n++) Assert.True(d.IsNew(7UL, 100, n));   // peer 7: 8 entries
        for (uint n = 1; n <= 8; n++) Assert.True(d.IsNew(8UL, 100, n));   // peer 8: 8 entries (ring full)

        d.ResetPeer(7UL);                                                   // ring now holds only peer 8's 8

        for (uint n = 1; n <= 8; n++) Assert.True(d.IsNew(7UL, 100, n));   // refill to capacity (16)
        Assert.True(d.IsNew(7UL, 100, 9u));                                 // 17th entry evicts the OLDEST kept: (8, nonce 1)
        Assert.True(d.IsNew(8UL, 100, 1u));                                 // evicted → new again (ring consistent; its re-add evicts (8, nonce 2))
        Assert.False(d.IsNew(8UL, 100, 3u));                                // a younger kept entry still dedupes
    }
}
