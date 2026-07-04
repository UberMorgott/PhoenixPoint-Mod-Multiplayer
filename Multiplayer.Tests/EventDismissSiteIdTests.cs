using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Xunit;

// Covers the EventDismiss wire's NEW trailing-optional siteId (GeoSite.SiteId, int, -1 = none): so the
// client result/dismiss card resolves the REAL event site instead of falling back to StartingBase
// ("Точка Феникс"). The host stamps the live event's site id at dismiss time; the client just reads it.
public class EventDismissSiteIdTests
{
    [Fact]
    public void EventDismiss_SiteId_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeEventDismiss(occurrenceId: 555, "PROG_EV_42", choiceIndex: 3, rewardBlob: null, siteId: 1337);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex, out var blob, out var siteId));
        Assert.Equal(555, occ);
        Assert.Equal("PROG_EV_42", id);
        Assert.Equal(3, choiceIndex);
        Assert.Empty(blob);
        Assert.Equal(1337, siteId);
    }

    [Fact]
    public void EventDismiss_SiteId_WithRewardBlob_RoundTrips()
    {
        // siteId must survive ALONGSIDE the trailing reward blob (both trailing-optional, reward first).
        var reward = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var bytes = SyncProtocol.EncodeEventDismiss(77, "EV_REWARD", choiceIndex: 2, rewardBlob: reward, siteId: 99);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex, out var blob, out var siteId));
        Assert.Equal(77, occ);
        Assert.Equal("EV_REWARD", id);
        Assert.Equal(2, choiceIndex);
        Assert.Equal(reward, blob);
        Assert.Equal(99, siteId);
    }

    [Fact]
    public void EventDismiss_NoSite_DecodesSiteIdNegativeOne()
    {
        // Default siteId (-1 = none) must NOT be appended → decodes back to -1 (BuildResultEvent falls
        // back to StartingBase), and the wire stays byte-identical to the legacy reward-only encode.
        var bytes = SyncProtocol.EncodeEventDismiss(8, "EV_NOSITE", choiceIndex: 2, rewardBlob: null);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex, out var blob, out var siteId));
        Assert.Equal(8, occ);
        Assert.Equal("EV_NOSITE", id);
        Assert.Equal(2, choiceIndex);
        Assert.Empty(blob);
        Assert.Equal(-1, siteId);
    }

    [Fact]
    public void EventDismiss_LegacyPayloadWithoutSiteId_DecodesSiteIdNegativeOne()
    {
        // Backward-compat: an OLD on-wire payload that lacks the trailing siteId (built here by hand as the
        // legacy 3-field [occId][eventId][choiceIndex] wire) must decode WITHOUT throwing and yield siteId = -1.
        byte[] legacyPayload;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)21);    // occId
            w.Write("EV_LEGACY");   // eventId
            w.Write(2);             // choiceIndex — no trailing reward, no trailing siteId
            legacyPayload = ms.ToArray();
        }
        Assert.True(SyncProtocol.TryDecodeEventDismiss(legacyPayload, out var occ, out var id, out var choiceIndex, out var blob, out var siteId));
        Assert.Equal(21, occ);
        Assert.Equal("EV_LEGACY", id);
        Assert.Equal(2, choiceIndex);
        Assert.Empty(blob);
        Assert.Equal(-1, siteId);   // absent trailing siteId → no-site sentinel, no throw
    }

    [Fact]
    public void EventDismiss_LegacyPayloadWithRewardNoSiteId_DecodesSiteIdNegativeOne()
    {
        // Backward-compat with the reward-carrying wire: [occId][eventId][choiceIndex][u16 len][blob] and NO
        // trailing siteId must decode the reward AND leave siteId = -1 (reward fully consumes its bytes).
        var reward = new byte[] { 0x01, 0x02, 0x03 };
        byte[] legacyPayload;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)22);            // occId
            w.Write("EV_LEGACY_R");         // eventId
            w.Write(1);                     // choiceIndex
            w.Write((ushort)reward.Length); // rewardLen
            w.Write(reward);                // rewardBlob — no trailing siteId
            legacyPayload = ms.ToArray();
        }
        Assert.True(SyncProtocol.TryDecodeEventDismiss(legacyPayload, out var occ, out var id, out var choiceIndex, out var blob, out var siteId));
        Assert.Equal(22, occ);
        Assert.Equal("EV_LEGACY_R", id);
        Assert.Equal(1, choiceIndex);
        Assert.Equal(reward, blob);
        Assert.Equal(-1, siteId);
    }
}
