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
}
