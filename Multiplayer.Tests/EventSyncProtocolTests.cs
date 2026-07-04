using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Xunit;

public class EventSyncProtocolTests
{
    [Fact]
    public void EventRaised_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeEventRaised(occurrenceId: 4242, "PROG_EV_42", 1337, 7);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var id, out var siteId, out var vehicleId));
        Assert.Equal(4242, occ);
        Assert.Equal("PROG_EV_42", id);
        Assert.Equal(1337, siteId);
        Assert.Equal(7, vehicleId);
    }

    [Fact]
    public void EventRaised_NoSiteNoVehicle_RoundTripsNegativeOne()
    {
        var bytes = SyncProtocol.EncodeEventRaised(1, "EV_NOSITE", -1, -1);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var id, out var siteId, out var vehicleId));
        Assert.Equal(1, occ);
        Assert.Equal("EV_NOSITE", id);
        Assert.Equal(-1, siteId);
        Assert.Equal(-1, vehicleId);
    }

    [Fact]
    public void EventRaised_DefaultVehicle_IsNegativeOne()
    {
        // The vehicleId arg defaults to -1 so non-vehicle event types stay 3-field-equivalent.
        var bytes = SyncProtocol.EncodeEventRaised(9, "EV_DEFV", 42);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var id, out var siteId, out var vehicleId));
        Assert.Equal(9, occ);
        Assert.Equal("EV_DEFV", id);
        Assert.Equal(42, siteId);
        Assert.Equal(-1, vehicleId);
    }

    [Fact]
    public void EventRaised_NullId_EncodesEmpty()
    {
        var bytes = SyncProtocol.EncodeEventRaised(3, null, 0, 0);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var id, out var siteId, out var vehicleId));
        Assert.Equal(3, occ);
        Assert.Equal("", id);
        Assert.Equal(0, siteId);
        Assert.Equal(0, vehicleId);
    }

    [Fact]
    public void EventRaised_NoVehiclePayload_DecodesVehicleNegativeOne()
    {
        // In-build short payload: [occId][eventId][siteId] with no trailing vehicleId must still decode,
        // leaving vehicleId = -1 (so the client resolves a vehicle at the site, else null context).
        byte[] shortPayload;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)11);   // occId
            w.Write("EV_SHORT");
            w.Write(99);           // siteId only — no trailing vehicleId
            shortPayload = ms.ToArray();
        }
        Assert.True(SyncProtocol.TryDecodeEventRaised(shortPayload, out var occ, out var id, out var siteId, out var vehicleId));
        Assert.Equal(11, occ);
        Assert.Equal("EV_SHORT", id);
        Assert.Equal(99, siteId);
        Assert.Equal(-1, vehicleId);
    }

    [Fact]
    public void EventRaised_WireBytes_AreStable()
    {
        // Pin the exact on-wire layout: [occId:u16 LE][len:7bit][utf8 eventId][siteId:i32 LE][vehicleId:i32 LE].
        // occId 7 = 07 00; "AB" = 0x02 length prefix + 0x41 0x42; siteId 1 = 01 00 00 00; vehicleId 2 = 02 00 00 00.
        var bytes = SyncProtocol.EncodeEventRaised(7, "AB", 1, 2);
        var expected = new byte[]
        {
            0x07, 0x00,              // occurrenceId = 7
            0x02, 0x41, 0x42,        // "AB"
            0x01, 0x00, 0x00, 0x00,  // siteId = 1
            0x02, 0x00, 0x00, 0x00,  // vehicleId = 2
        };
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void EventDismiss_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeEventDismiss(occurrenceId: 555, "PROG_EV_42", 3);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex));
        Assert.Equal(555, occ);
        Assert.Equal("PROG_EV_42", id);
        Assert.Equal(3, choiceIndex);
    }

    [Fact]
    public void EventDismiss_NullId_EncodesEmpty()
    {
        var bytes = SyncProtocol.EncodeEventDismiss(2, null);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex));
        Assert.Equal(2, occ);
        Assert.Equal("", id);
        Assert.Equal(-1, choiceIndex);   // default close-only
    }

    [Fact]
    public void EventDismiss_DefaultChoiceIndex_IsNegativeOne()
    {
        // The choiceIndex arg defaults to -1 (close-only) for the pure-INFO host-OK / decline path.
        var bytes = SyncProtocol.EncodeEventDismiss(8, "EV_CLOSE");
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex));
        Assert.Equal(8, occ);
        Assert.Equal("EV_CLOSE", id);
        Assert.Equal(-1, choiceIndex);
    }

    [Fact]
    public void EventDismiss_NoChoicePayload_DecodesChoiceIndexNegativeOne()
    {
        // In-build short payload: [occId][eventId] with no trailing choiceIndex must still decode,
        // leaving choiceIndex = -1 (close-only) so a close-only dismiss never throws / stays unstuck.
        byte[] shortPayload;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)13);    // occId
            w.Write("EV_SHORT");    // eventId only — no trailing choiceIndex
            shortPayload = ms.ToArray();
        }
        Assert.True(SyncProtocol.TryDecodeEventDismiss(shortPayload, out var occ, out var id, out var choiceIndex));
        Assert.Equal(13, occ);
        Assert.Equal("EV_SHORT", id);
        Assert.Equal(-1, choiceIndex);
    }

    [Fact]
    public void EventDismiss_WithRewardBlob_RoundTrips()
    {
        // The Task-2 trailing reward blob must survive alongside the new leading occurrence id.
        var reward = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var bytes = SyncProtocol.EncodeEventDismiss(77, "EV_REWARD", 2, reward);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex, out var blob));
        Assert.Equal(77, occ);
        Assert.Equal("EV_REWARD", id);
        Assert.Equal(2, choiceIndex);
        Assert.Equal(reward, blob);
    }

    [Fact]
    public void EventDismiss_NoReward_EmitsNoTrailingLength()
    {
        // A null reward yields the EXACT 3-field bytes (no trailing length) → the with-blob 3-out decode sees
        // an empty blob. Confirms the reward blob is appended ONLY when non-empty.
        var bytes = SyncProtocol.EncodeEventDismiss(5, "AB", 2, null);
        var expected = new byte[]
        {
            0x05, 0x00,              // occurrenceId = 5
            0x02, 0x41, 0x42,        // "AB"
            0x02, 0x00, 0x00, 0x00,  // choiceIndex = 2
        };
        Assert.Equal(expected, bytes);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out _, out _, out _, out var blob));
        Assert.Empty(blob);
    }

    [Fact]
    public void EventDismiss_WireBytes_AreStable()
    {
        // Pin the exact on-wire layout: [occId:u16 LE][len:7bit][utf8 eventId][choiceIndex:i32 LE].
        // occId 5 = 05 00; "AB" = 0x02 length prefix + 0x41 0x42; choiceIndex 2 = 02 00 00 00.
        var bytes = SyncProtocol.EncodeEventDismiss(5, "AB", 2);
        var expected = new byte[]
        {
            0x05, 0x00,              // occurrenceId = 5
            0x02, 0x41, 0x42,        // "AB"
            0x02, 0x00, 0x00, 0x00,  // choiceIndex = 2
        };
        Assert.Equal(expected, bytes);
    }
}
