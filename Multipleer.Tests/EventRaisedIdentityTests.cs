using System.IO;
using System.Text;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.State;
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
