using Multiplayer.Network.Sync;
using Xunit;

public class StateSyncProtocolTests
{
    [Fact]
    public void StateSync_RoundTrips()
    {
        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var bytes = SyncProtocol.EncodeStateSync(channelId: 1, version: 0xDEADBEEFUL, payload: payload);
        Assert.True(SyncProtocol.TryDecodeStateSync(bytes, out var id, out var ver, out var pl));
        Assert.Equal((byte)1, id);
        Assert.Equal(0xDEADBEEFUL, ver);
        Assert.Equal(payload, pl);
    }

    [Fact]
    public void StateSync_RoundTrips_EmptyPayload()
    {
        var bytes = SyncProtocol.EncodeStateSync(channelId: 7, version: 0UL, payload: null);
        Assert.True(SyncProtocol.TryDecodeStateSync(bytes, out var id, out var ver, out var pl));
        Assert.Equal((byte)7, id);
        Assert.Equal(0UL, ver);
        Assert.Empty(pl);
    }

    [Fact]
    public void StateSync_Decode_RejectsGarbage()
    {
        Assert.False(SyncProtocol.TryDecodeStateSync(new byte[] { 0x01 }, out _, out _, out _));
    }

    [Fact]
    public void Channel_DropsStaleDuplicateAndAcrossChannelsIndependent()
    {
        var t = new SequenceTracker();

        // Channel 1 monotonic series.
        Assert.True(t.ShouldApplyChannel(1, 1)); t.MarkChannel(1, 1);
        Assert.True(t.ShouldApplyChannel(1, 2)); t.MarkChannel(1, 2);
        Assert.False(t.ShouldApplyChannel(1, 2)); // duplicate
        Assert.False(t.ShouldApplyChannel(1, 1)); // stale
        Assert.True(t.ShouldApplyChannel(1, 3));

        // Channel 2 is an INDEPENDENT series — version 1 still applies even though ch1 is at 2.
        Assert.True(t.ShouldApplyChannel(2, 1)); t.MarkChannel(2, 1);
        Assert.False(t.ShouldApplyChannel(2, 1)); // ch2 duplicate
        Assert.True(t.ShouldApplyChannel(1, 3));  // ch1 unaffected
    }
}
