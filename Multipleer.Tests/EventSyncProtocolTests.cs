using Multipleer.Network.Sync;
using Xunit;

public class EventSyncProtocolTests
{
    [Fact]
    public void EventRaised_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeEventRaised("PROG_EV_42", 1337);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var id, out var siteId));
        Assert.Equal("PROG_EV_42", id);
        Assert.Equal(1337, siteId);
    }

    [Fact]
    public void EventRaised_NoSite_RoundTripsNegativeOne()
    {
        var bytes = SyncProtocol.EncodeEventRaised("EV_NOSITE", -1);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var id, out var siteId));
        Assert.Equal("EV_NOSITE", id);
        Assert.Equal(-1, siteId);
    }

    [Fact]
    public void EventRaised_NullId_EncodesEmpty()
    {
        var bytes = SyncProtocol.EncodeEventRaised(null, 0);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var id, out var siteId));
        Assert.Equal("", id);
        Assert.Equal(0, siteId);
    }

    [Fact]
    public void EventDismiss_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeEventDismiss("PROG_EV_42");
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var id));
        Assert.Equal("PROG_EV_42", id);
    }

    [Fact]
    public void EventDismiss_NullId_EncodesEmpty()
    {
        var bytes = SyncProtocol.EncodeEventDismiss(null);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var id));
        Assert.Equal("", id);
    }
}
