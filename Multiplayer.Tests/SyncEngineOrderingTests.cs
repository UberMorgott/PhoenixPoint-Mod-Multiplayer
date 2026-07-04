using Multiplayer.Network.Sync;
using Xunit;

public class SyncEngineOrderingTests
{
    [Fact]
    public void Apply_DropsStaleOrDuplicateSequence()
    {
        var t = new SequenceTracker();
        Assert.True(t.ShouldApply(1)); t.Mark(1);
        Assert.True(t.ShouldApply(2)); t.Mark(2);
        Assert.False(t.ShouldApply(2));  // duplicate
        Assert.False(t.ShouldApply(1));  // stale
        Assert.True(t.ShouldApply(3));
    }

    [Fact]
    public void Wallet_DropsOlderVersion()
    {
        var t = new SequenceTracker();
        Assert.True(t.ShouldApplyWallet(10)); t.MarkWallet(10);
        Assert.False(t.ShouldApplyWallet(10));
        Assert.False(t.ShouldApplyWallet(9));
        Assert.True(t.ShouldApplyWallet(11));
    }
}
