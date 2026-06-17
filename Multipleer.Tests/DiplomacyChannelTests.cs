using Multipleer.Network.Sync;
using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip tests for the faction-DIPLOMACY state channel (#4) snapshot codec: per-relation
/// reputation ints keyed by (ownerFactionDef guid, withPartyDef guid). Only the pure encode/decode path
/// is exercised; Snapshot/Apply bind live game types and are not unit-testable. Mirrors
/// <see cref="ResearchChannelTests"/>.
/// </summary>
public class DiplomacyChannelTests
{
    private static DiplomacySnapshot RoundTrip(DiplomacySnapshot snap)
        => DiplomacySnapshot.Decode(DiplomacySnapshot.Encode(snap));

    [Fact]
    public void Snapshot_RoundTrips_Relations()
    {
        var snap = new DiplomacySnapshot();
        snap.Relations.Add(("PX_FactionDef", "SY_FactionDef", -3));
        snap.Relations.Add(("PX_FactionDef", "AN_FactionDef", 5));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(2, rt.Relations.Count);
        Assert.Equal(("PX_FactionDef", "SY_FactionDef", -3), rt.Relations[0]);
        Assert.Equal(("PX_FactionDef", "AN_FactionDef", 5), rt.Relations[1]);
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new DiplomacySnapshot());
        Assert.NotNull(rt);
        Assert.Empty(rt.Relations);
    }

    [Fact]
    public void Snapshot_PreservesOrder_AndNegativeValues()
    {
        var snap = new DiplomacySnapshot();
        snap.Relations.Add(("o", "a", -2147483647)); // min diplomacy
        snap.Relations.Add(("o", "b", 0));
        snap.Relations.Add(("o", "c", 100));

        var rt = RoundTrip(snap);

        Assert.Equal(new[] { "a", "b", "c" }, rt.Relations.ConvertAll(x => x.with));
        Assert.Equal(-2147483647, rt.Relations[0].value);
        Assert.Equal(100, rt.Relations[2].value);
    }

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
    {
        Assert.Null(DiplomacySnapshot.Decode(new byte[] { 0xFF }));
    }

    [Fact]
    public void Decode_RejectsTruncatedString_ReturnsNull()
    {
        // count=1, ownerLen=4 but no bytes follow → rejected.
        var truncated = new byte[]
        {
            0x01, 0x00,             // count = 1
            0x04, 0x00,             // ownerLen = 4
                                    // (no owner bytes) — truncated
        };
        Assert.Null(DiplomacySnapshot.Decode(truncated));
    }

    [Fact]
    public void Encode_NullSnapshot_ReturnsNull() => Assert.Null(DiplomacySnapshot.Encode(null));

    [Fact]
    public void Encode_StableWireBytes_Pinned()
    {
        // Pin the EXACT wire layout. One relation: owner "A", with "B", value 7.
        var snap = new DiplomacySnapshot();
        snap.Relations.Add(("A", "B", 7));

        var bytes = DiplomacySnapshot.Encode(snap);

        var expected = new byte[]
        {
            0x01, 0x00,                 // count = 1
            0x01, 0x00, 0x41,           // ownerLen=1, "A"
            0x01, 0x00, 0x42,           // withLen=1, "B"
            0x07, 0x00, 0x00, 0x00,     // value 7 (i32 LE)
        };
        Assert.Equal(expected, bytes);
    }

    // ─── registration: the diplomacy channel claims a distinct, stable surface/channel id ────
    [Fact]
    public void ChannelId_Is4_AndDistinctFromOtherChannels()
    {
        Assert.Equal((byte)4, SurfaceIds.DiplomacyChannel);
        var ids = new[] { SurfaceIds.InventoryChannel, SurfaceIds.ResearchChannel,
                          SurfaceIds.UnlockChannel, SurfaceIds.DiplomacyChannel };
        Assert.Equal(ids.Length, new System.Collections.Generic.HashSet<byte>(ids).Count);
    }
}
