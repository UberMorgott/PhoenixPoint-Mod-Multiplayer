using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

// Covers the EventRaised wire's NEW trailing-optional site-IDENTITY block (a GeoSiteState reused from the
// GeoSite channel): so a client whose site is ABSENT renders graceful subtitle text instead of "Точка
// Феникс" (StartingBase). The block is appended only when present; legacy 4-field payloads decode hasIdentity=false.
public class EventRaisedIdentityTests
{
    [Fact]
    public void EventRaised_WithIdentity_RoundTrips()
    {
        var id = new GeoSiteState(1337, "FAC-GUID", siteType: 30, state: 1, siteName: "KEY_SITE_42", encounterID: "ENC9");
        var bytes = SyncProtocol.EncodeEventRaised(occurrenceId: 4242, "PROG_EV_42", 1337, vehicleId: 7, identity: id);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var ev, out var siteId, out var veh, out var hasId, out var got));
        Assert.Equal(4242, occ);
        Assert.Equal("PROG_EV_42", ev);
        Assert.Equal(1337, siteId);
        Assert.Equal(7, veh);
        Assert.True(hasId);
        Assert.Equal(id, got);
    }

    [Fact]
    public void EventRaised_IdentityInspectedFlag_RoundTrips()
    {
        // The identity block's per-faction reveal flag rides the SAME wire as the GeoSite channel (symmetric byte).
        var id = new GeoSiteState(55, "FAC", siteType: 10, state: 1, siteName: "K", encounterID: "E", inspected: true);
        var bytes = SyncProtocol.EncodeEventRaised(1, "EV_INSP", 55, 2, identity: id);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _, out var hasId, out var got));
        Assert.True(hasId);
        Assert.True(got.Inspected);
        Assert.Equal(id, got);
    }

    [Fact]
    public void EventRaised_NoIdentity_DecodesHasIdentityFalse()
    {
        // Default (no identity) must NOT append the block → hasIdentity=false, and the 4-field prefix stays
        // byte-identical to the legacy raise wire.
        var bytes = SyncProtocol.EncodeEventRaised(9, "EV_NOID", 42, 5);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var ev, out var siteId, out var veh, out var hasId, out _));
        Assert.Equal(9, occ);
        Assert.Equal("EV_NOID", ev);
        Assert.Equal(42, siteId);
        Assert.Equal(5, veh);
        Assert.False(hasId);
    }

    [Fact]
    public void EventRaised_LegacyFourFieldPayload_DecodesHasIdentityFalse()
    {
        // Backward-compat: an OLD [occId][eventId][siteId][vehicleId] payload (no identity byte) decodes
        // WITHOUT throwing and yields hasIdentity=false.
        byte[] legacy;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)21); w.Write("EV_LEGACY"); w.Write(99); w.Write(3); // no trailing identity byte
            legacy = ms.ToArray();
        }
        Assert.True(SyncProtocol.TryDecodeEventRaised(legacy, out var occ, out var ev, out var siteId, out var veh, out var hasId, out _));
        Assert.Equal(21, occ);
        Assert.Equal("EV_LEGACY", ev);
        Assert.Equal(99, siteId);
        Assert.Equal(3, veh);
        Assert.False(hasId);
    }

    [Fact]
    public void EventRaised_SingleChoiceFlag_RoundTrips()
    {
        // The trailing flag bitmask carries singleChoice (HasSingleChoice, Choices.Count<=1) alongside identity.
        // singleChoice=true with NO identity → flag byte 0x02, no identity block.
        var bytes = SyncProtocol.EncodeEventRaised(11, "EV_SINGLE", 5, 6, identity: null, singleChoice: true);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var ev, out var siteId, out var veh, out var hasId, out _, out var single));
        Assert.Equal(11, occ);
        Assert.Equal("EV_SINGLE", ev);
        Assert.False(hasId);
        Assert.True(single);
    }

    [Fact]
    public void EventRaised_SingleChoiceAndIdentity_RoundTripTogether()
    {
        var id = new GeoSiteState(7, "G", siteType: 1, state: 0, siteName: "K", encounterID: "E");
        var bytes = SyncProtocol.EncodeEventRaised(12, "EV_BOTH", 7, 8, identity: id, singleChoice: true);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _, out var hasId, out var got, out var single));
        Assert.True(hasId);
        Assert.Equal(id, got);
        Assert.True(single);
    }

    [Fact]
    public void EventRaised_NoFlags_DecodesSingleChoiceFalse_AndStaysByteStable()
    {
        // No identity + multi-choice (singleChoice=false) → no flag byte at all → byte-identical to legacy wire.
        var with = SyncProtocol.EncodeEventRaised(13, "EV_NF", 1, 2, identity: null, singleChoice: false);
        var legacy = SyncProtocol.EncodeEventRaised(13, "EV_NF", 1, 2);
        Assert.Equal(legacy, with);
        Assert.True(SyncProtocol.TryDecodeEventRaised(with, out _, out _, out _, out _, out var hasId, out _, out var single));
        Assert.False(hasId);
        Assert.False(single);
    }

    [Fact]
    public void EventRaised_LegacyIdentityByte_DecodesSingleChoiceFalse()
    {
        // An OLD identity payload wrote flag byte == 1 (identity only). The bitmask decode must read
        // hasIdentity=true, singleChoice=false (bit1 unset) — back-compat with peers on the old flag format.
        var id = new GeoSiteState(3, "G2", siteType: 2, state: 1, siteName: "K2", encounterID: "E2");
        var bytes = SyncProtocol.EncodeEventRaised(14, "EV_OLDID", 3, 4, identity: id);   // identity-only, no singleChoice
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _, out var hasId, out var got, out var single));
        Assert.True(hasId);
        Assert.Equal(id, got);
        Assert.False(single);
    }

    [Fact]
    public void EventRaised_OneWindowFlag_RoundTrips()
    {
        // The 1-window bit (host IsSingleChoiceEncounter()==true → reward+narrative in ONE combined window) rides
        // bit2 alongside singleChoice (bit1). singleChoice+oneWindow, no identity → flag byte 0x06.
        var bytes = SyncProtocol.EncodeEventRaised(15, "EV_1W", 5, 6, identity: null, singleChoice: true, oneWindow: true);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var ev, out _, out _, out var hasId, out _, out var single, out var oneWin));
        Assert.Equal(15, occ);
        Assert.Equal("EV_1W", ev);
        Assert.False(hasId);
        Assert.True(single);
        Assert.True(oneWin);
    }

    [Fact]
    public void EventRaised_SingleChoiceWithoutOneWindow_DecodesOneWindowFalse()
    {
        // 2-window single-choice-WITH-outcome (singleChoice=true, oneWindow=false) → flag byte 0x02, oneWindow false.
        var bytes = SyncProtocol.EncodeEventRaised(16, "EV_2W", 1, 2, identity: null, singleChoice: true);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _, out _, out _, out var single, out var oneWin));
        Assert.True(single);
        Assert.False(oneWin);
    }

    [Fact]
    public void EventRaised_OneWindowAndIdentity_RoundTripTogether()
    {
        var id = new GeoSiteState(8, "G3", siteType: 4, state: 1, siteName: "K3", encounterID: "E3");
        var bytes = SyncProtocol.EncodeEventRaised(17, "EV_1WID", 8, 9, identity: id, singleChoice: true, oneWindow: true);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _, out var hasId, out var got, out var single, out var oneWin));
        Assert.True(hasId);
        Assert.Equal(id, got);
        Assert.True(single);
        Assert.True(oneWin);
    }

    [Fact]
    public void EventRaised_LegacyPayload_DecodesOneWindowFalse()
    {
        // An OLD peer's identity+singleChoice payload (flag 0x03, no bit2) must decode oneWindow=false (back-compat).
        var id = new GeoSiteState(2, "G4", siteType: 1, state: 0, siteName: "K4", encounterID: "E4");
        var bytes = SyncProtocol.EncodeEventRaised(18, "EV_OLD2", 2, 3, identity: id, singleChoice: true);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _, out var hasId, out _, out var single, out var oneWin));
        Assert.True(hasId);
        Assert.True(single);
        Assert.False(oneWin);
    }

    [Fact]
    public void EventRaised_ThreeArg_StillRoundTripsViaLegacyOverload()
    {
        // The legacy 4-out decode overload must still work (callers/tests that ignore the identity block).
        var bytes = SyncProtocol.EncodeEventRaised(1, "EV_3", -1, -1);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var ev, out var siteId, out var veh));
        Assert.Equal(1, occ);
        Assert.Equal("EV_3", ev);
        Assert.Equal(-1, siteId);
        Assert.Equal(-1, veh);
    }
}
