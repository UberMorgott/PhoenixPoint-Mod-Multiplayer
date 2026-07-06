using Multiplayer.Network.Sync;
using Xunit;

public class SyncEngineOrderingTests
{
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
