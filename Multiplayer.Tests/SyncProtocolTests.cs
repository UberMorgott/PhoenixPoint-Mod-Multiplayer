using System.Collections.Generic;
using Multiplayer.Network.Sync;
using Xunit;

public class SyncProtocolTests
{
    [Fact]
    public void Scope_NestsAndRestores()
    {
        Assert.False(SyncApplyScope.IsApplying);
        using (SyncApplyScope.Enter())
        {
            Assert.True(SyncApplyScope.IsApplying);
            using (SyncApplyScope.Enter()) { Assert.True(SyncApplyScope.IsApplying); }
            Assert.True(SyncApplyScope.IsApplying); // still in outer
        }
        Assert.False(SyncApplyScope.IsApplying);
    }

    [Fact]
    public void ActionRequest_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3 };
        var bytes = SyncProtocol.EncodeActionRequest(SyncedActionIds.StartResearch, 0xABCDu, payload);
        Assert.True(SyncProtocol.TryDecodeActionRequest(bytes, out var id, out var nonce, out var pl));
        Assert.Equal(SyncedActionIds.StartResearch, id);
        Assert.Equal(0xABCDu, nonce);
        Assert.Equal(payload, pl);
    }

    [Fact]
    public void ActionApply_RoundTrips()
    {
        var payload = new byte[] { 9 };
        var bytes = SyncProtocol.EncodeActionApply(SyncedActionIds.ConstructFacility, 777UL, payload);
        Assert.True(SyncProtocol.TryDecodeActionApply(bytes, out var id, out var seq, out var pl));
        Assert.Equal(SyncedActionIds.ConstructFacility, id);
        Assert.Equal(777UL, seq);
        Assert.Equal(payload, pl);
    }

    [Fact]
    public void ActionReject_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeActionReject(0x1234u, 7, "no funds");
        Assert.True(SyncProtocol.TryDecodeActionReject(bytes, out var nonce, out var code, out var reason));
        Assert.Equal(0x1234u, nonce);
        Assert.Equal((byte)7, code);
        Assert.Equal("no funds", reason);
    }

    [Fact]
    public void WalletSync_RoundTrips()
    {
        var slots = new List<(int, float)> { (1, 100f), (0x40, 12.5f), (0x800, -3f) };
        var bytes = SyncProtocol.EncodeWalletSync(55UL, slots);
        Assert.True(SyncProtocol.TryDecodeWalletSync(bytes, out var ver, out var outSlots));
        Assert.Equal(55UL, ver);
        Assert.Equal(slots, outSlots);
    }
}
