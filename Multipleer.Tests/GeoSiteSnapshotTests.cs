using Multipleer.Network.Sync;
using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip tests for the GeoSite state-replication channel (#5) snapshot codec: per-site identity
/// records (SiteId, OwnerFactionDefGuid, SiteType, State, SiteName loc-key, EncounterID). Only the pure
/// encode/decode path is exercised; Snapshot/Apply bind live game types (GeoMap site events + GeoSite
/// fields by reflection) and are not unit-testable. Mirrors <see cref="DiplomacyChannelTests"/> /
/// <see cref="ResearchChannelTests"/>.
/// </summary>
public class GeoSiteSnapshotTests
{
    private static GeoSiteSnapshot RoundTrip(GeoSiteSnapshot snap)
        => GeoSiteSnapshot.Decode(GeoSiteSnapshot.Encode(snap));

    [Fact]
    public void Snapshot_RoundTrips_Sites()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(42, "PX_FactionDef", 10, 1, "KEY_PHOENIX_BASE", "ENC_intro"));
        snap.Sites.Add(new GeoSiteState(7, "AN_FactionDef", 20, 4, "KEY_HAVEN", ""));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(2, rt.Sites.Count);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
        Assert.Equal(snap.Sites[1], rt.Sites[1]);
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new GeoSiteSnapshot());
        Assert.NotNull(rt);
        Assert.Empty(rt.Sites);
    }

    [Fact]
    public void Snapshot_RoundTrips_NullAndEmptyStrings()
    {
        var snap = new GeoSiteSnapshot();
        // null owner-guid / null name / null encounter must round-trip to empty strings (never throw).
        snap.Sites.Add(new GeoSiteState(1, null, 0, 0, null, null));
        snap.Sites.Add(new GeoSiteState(2, "", 110, 2, "", ""));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(2, rt.Sites.Count);
        // null encodes as "" — decode yields "".
        Assert.Equal(new GeoSiteState(1, "", 0, 0, "", ""), rt.Sites[0]);
        Assert.Equal(new GeoSiteState(2, "", 110, 2, "", ""), rt.Sites[1]);
    }

    [Fact]
    public void Snapshot_PreservesOrder_AndEnumByteValues()
    {
        var snap = new GeoSiteSnapshot();
        // Type/State carry the raw ENUM integer value (NOT an ordinal): Type up to 110, State up to 4.
        snap.Sites.Add(new GeoSiteState(100, "a", 110, 4, "n0", "e0")); // Marketplace / Abandoned
        snap.Sites.Add(new GeoSiteState(101, "b", 0, 0, "n1", "e1"));   // None / None
        snap.Sites.Add(new GeoSiteState(102, "c", 40, 1, "n2", "e2"));  // AlienBase / Functioning

        var rt = RoundTrip(snap);

        Assert.Equal(new[] { 100, 101, 102 }, rt.Sites.ConvertAll(x => x.SiteId).ToArray());
        Assert.Equal((byte)110, rt.Sites[0].SiteType);
        Assert.Equal((byte)4, rt.Sites[0].State);
        Assert.Equal((byte)40, rt.Sites[2].SiteType);
    }

    [Fact]
    public void Snapshot_RoundTrips_NegativeSiteId()
    {
        // Unassigned sites carry SiteId = -1 (GeoSite default); must survive the i32 round-trip.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(-1, "o", 10, 1, "n", "e"));

        var rt = RoundTrip(snap);
        Assert.Equal(-1, rt.Sites[0].SiteId);
    }

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
        => Assert.Null(GeoSiteSnapshot.Decode(new byte[] { 0xFF }));

    [Fact]
    public void Decode_RejectsTruncatedString_ReturnsNull()
    {
        // count=1, siteId=1, ownerLen=4 but no owner bytes follow → rejected (null, not garbage).
        var truncated = new byte[]
        {
            0x01, 0x00,                 // count = 1
            0x01, 0x00, 0x00, 0x00,     // SiteId = 1 (i32 LE)
            0x04, 0x00,                 // ownerLen = 4
                                        // (no owner bytes) — truncated
        };
        Assert.Null(GeoSiteSnapshot.Decode(truncated));
    }

    [Fact]
    public void Encode_NullSnapshot_ReturnsNull() => Assert.Null(GeoSiteSnapshot.Encode(null));

    [Fact]
    public void Encode_StableWireBytes_Pinned()
    {
        // Pin the EXACT wire layout. One site: id 1, owner "A", type 10, state 1, name "B", encounter "C".
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 10, 1, "B", "C"));

        var bytes = GeoSiteSnapshot.Encode(snap);

        var expected = new byte[]
        {
            0x01, 0x00,                 // count = 1
            0x01, 0x00, 0x00, 0x00,     // SiteId = 1 (i32 LE)
            0x01, 0x00, 0x41,           // ownerLen=1, "A"
            0x0A,                       // SiteType = 10
            0x01,                       // State = 1
            0x01, 0x00, 0x42,           // nameLen=1, "B"
            0x01, 0x00, 0x43,           // encLen=1, "C"
            0x00,                       // Inspected = false
        };
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Snapshot_RoundTrips_InspectedFlag()
    {
        // The per-faction reveal flag (exploration outcome) must survive the round-trip both ways.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e", inspected: true));
        snap.Sites.Add(new GeoSiteState(2, "o", 10, 1, "n", "e", inspected: false));

        var rt = RoundTrip(snap);

        Assert.True(rt.Sites[0].Inspected);
        Assert.False(rt.Sites[1].Inspected);
        // Inspected participates in structural equality (distinguishes an otherwise-identical revealed site).
        Assert.NotEqual(snap.Sites[0], snap.Sites[1]);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    // ─── registration: the GeoSite channel claims a distinct, stable surface/channel id ────
    [Fact]
    public void ChannelId_Is5_AndDistinctFromOtherChannels()
    {
        Assert.Equal((byte)5, SurfaceIds.GeoSiteChannel);
        var ids = new[] { SurfaceIds.InventoryChannel, SurfaceIds.ResearchChannel,
                          SurfaceIds.UnlockChannel, SurfaceIds.DiplomacyChannel, SurfaceIds.GeoSiteChannel };
        Assert.Equal(ids.Length, new System.Collections.Generic.HashSet<byte>(ids).Count);
    }
}
