using System.IO;
using System.Text;
using Multipleer.Network.Sync;
using Xunit;

public class EventSyncProtocolTests
{
    [Fact]
    public void EventRaised_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeEventRaised("PROG_EV_42", 1337, 7);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var id, out var siteId, out var vehicleId));
        Assert.Equal("PROG_EV_42", id);
        Assert.Equal(1337, siteId);
        Assert.Equal(7, vehicleId);
    }

    [Fact]
    public void EventRaised_NoSiteNoVehicle_RoundTripsNegativeOne()
    {
        var bytes = SyncProtocol.EncodeEventRaised("EV_NOSITE", -1, -1);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var id, out var siteId, out var vehicleId));
        Assert.Equal("EV_NOSITE", id);
        Assert.Equal(-1, siteId);
        Assert.Equal(-1, vehicleId);
    }

    [Fact]
    public void EventRaised_DefaultVehicle_IsNegativeOne()
    {
        // The vehicleId arg defaults to -1 so non-vehicle event types stay 2-field-equivalent.
        var bytes = SyncProtocol.EncodeEventRaised("EV_DEFV", 42);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var id, out var siteId, out var vehicleId));
        Assert.Equal("EV_DEFV", id);
        Assert.Equal(42, siteId);
        Assert.Equal(-1, vehicleId);
    }

    [Fact]
    public void EventRaised_NullId_EncodesEmpty()
    {
        var bytes = SyncProtocol.EncodeEventRaised(null, 0, 0);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var id, out var siteId, out var vehicleId));
        Assert.Equal("", id);
        Assert.Equal(0, siteId);
        Assert.Equal(0, vehicleId);
    }

    [Fact]
    public void EventRaised_LegacyTwoFieldPayload_DecodesVehicleNegativeOne()
    {
        // Forward/backward compat: a legacy [eventId][siteId] payload (no vehicleId) must still decode,
        // leaving vehicleId = -1 (so the client resolves a vehicle at the site, else null context).
        byte[] legacy;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write("EV_LEGACY");
            w.Write(99);           // siteId only — no trailing vehicleId
            legacy = ms.ToArray();
        }
        Assert.True(SyncProtocol.TryDecodeEventRaised(legacy, out var id, out var siteId, out var vehicleId));
        Assert.Equal("EV_LEGACY", id);
        Assert.Equal(99, siteId);
        Assert.Equal(-1, vehicleId);
    }

    [Fact]
    public void EventRaised_WireBytes_AreStable()
    {
        // Pin the exact on-wire layout: [len:7bit][utf8 eventId][siteId:i32 LE][vehicleId:i32 LE].
        // "AB" = 0x02 length prefix + 0x41 0x42; siteId 1 = 01 00 00 00; vehicleId 2 = 02 00 00 00.
        var bytes = SyncProtocol.EncodeEventRaised("AB", 1, 2);
        var expected = new byte[]
        {
            0x02, 0x41, 0x42,        // "AB"
            0x01, 0x00, 0x00, 0x00,  // siteId = 1
            0x02, 0x00, 0x00, 0x00,  // vehicleId = 2
        };
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void EventDismiss_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeEventDismiss("PROG_EV_42", 3);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var id, out var choiceIndex));
        Assert.Equal("PROG_EV_42", id);
        Assert.Equal(3, choiceIndex);
    }

    [Fact]
    public void EventDismiss_NullId_EncodesEmpty()
    {
        var bytes = SyncProtocol.EncodeEventDismiss(null);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var id, out var choiceIndex));
        Assert.Equal("", id);
        Assert.Equal(-1, choiceIndex);   // default close-only
    }

    [Fact]
    public void EventDismiss_DefaultChoiceIndex_IsNegativeOne()
    {
        // The choiceIndex arg defaults to -1 (close-only) for the pure-INFO host-OK / decline path.
        var bytes = SyncProtocol.EncodeEventDismiss("EV_CLOSE");
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var id, out var choiceIndex));
        Assert.Equal("EV_CLOSE", id);
        Assert.Equal(-1, choiceIndex);
    }

    [Fact]
    public void EventDismiss_LegacyOneFieldPayload_DecodesChoiceIndexNegativeOne()
    {
        // Forward/backward compat: a legacy [eventId] payload (no choiceIndex) must still decode,
        // leaving choiceIndex = -1 (close-only) so an old host's dismiss never throws / stays unstuck.
        byte[] legacy;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write("EV_LEGACY");   // eventId only — no trailing choiceIndex
            legacy = ms.ToArray();
        }
        Assert.True(SyncProtocol.TryDecodeEventDismiss(legacy, out var id, out var choiceIndex));
        Assert.Equal("EV_LEGACY", id);
        Assert.Equal(-1, choiceIndex);
    }

    [Fact]
    public void EventDismiss_WireBytes_AreStable()
    {
        // Pin the exact on-wire layout: [len:7bit][utf8 eventId][choiceIndex:i32 LE].
        // "AB" = 0x02 length prefix + 0x41 0x42; choiceIndex 2 = 02 00 00 00.
        var bytes = SyncProtocol.EncodeEventDismiss("AB", 2);
        var expected = new byte[]
        {
            0x02, 0x41, 0x42,        // "AB"
            0x02, 0x00, 0x00, 0x00,  // choiceIndex = 2
        };
        Assert.Equal(expected, bytes);
    }
}
