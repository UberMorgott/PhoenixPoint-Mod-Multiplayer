using Multipleer.Network.Sync;
using Xunit;

public class IntentDedupTests
{
    [Fact]
    public void IsNew_FirstTrueRepeatFalse()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(100, 1u));
        Assert.False(d.IsNew(100, 1u));   // reliable-transport double-send
        Assert.True(d.IsNew(100, 2u));
    }

    [Fact]
    public void IsNew_SurfaceNamespaced()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(100, 1u));
        Assert.True(d.IsNew(101, 1u));    // same nonce, different surface → distinct
    }

    [Fact]
    public void IsNew_EvictsOldestPastCapacity_FloorIs16()
    {
        var d = new IntentDedup(capacity: 16);
        for (uint n = 1; n <= 16; n++) Assert.True(d.IsNew(100, n));
        Assert.True(d.IsNew(100, 17u));   // overflow evicts nonce 1
        Assert.True(d.IsNew(100, 1u));    // 1 was evicted → seen as new again
        Assert.False(d.IsNew(100, 17u));  // a recent one is still deduped
    }

    [Fact]
    public void Reset_Clears()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(100, 1u));
        d.Reset();
        Assert.True(d.IsNew(100, 1u));    // after reset the same key is new again
    }
}
