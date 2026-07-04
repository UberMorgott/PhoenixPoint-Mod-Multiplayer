using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip tests for the research-UNLOCK state channel (#3) snapshot codec: the three monotonic
/// def-id sets (facilities / manufacture / augmentations). Only the pure encode/decode path is exercised;
/// Snapshot/Apply bind live game types and are not unit-testable. Mirrors <see cref="ResearchChannelTests"/>.
/// </summary>
public class UnlockChannelTests
{
    private static UnlockSnapshot RoundTrip(UnlockSnapshot snap)
        => UnlockSnapshot.Decode(UnlockSnapshot.Encode(snap));

    [Fact]
    public void Snapshot_RoundTrips_AllThreeSets()
    {
        var snap = new UnlockSnapshot();
        snap.Facilities.Add("FAC_Lab_Guid");
        snap.Facilities.Add("FAC_Workshop_Guid");
        snap.Manufacture.Add("ITM_Laser_Guid");
        snap.Augmentations.Add("AUG_Mutation_Guid");
        snap.Augmentations.Add("AUG_Bionic_Guid");

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(new[] { "FAC_Lab_Guid", "FAC_Workshop_Guid" }, rt.Facilities);
        Assert.Equal(new[] { "ITM_Laser_Guid" }, rt.Manufacture);
        Assert.Equal(new[] { "AUG_Mutation_Guid", "AUG_Bionic_Guid" }, rt.Augmentations);
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new UnlockSnapshot());
        Assert.NotNull(rt);
        Assert.Empty(rt.Facilities);
        Assert.Empty(rt.Manufacture);
        Assert.Empty(rt.Augmentations);
    }

    [Fact]
    public void Snapshot_PreservesOrder()
    {
        var snap = new UnlockSnapshot();
        snap.Manufacture.Add("a");
        snap.Manufacture.Add("b");
        snap.Manufacture.Add("c");
        Assert.Equal(new[] { "a", "b", "c" }, RoundTrip(snap).Manufacture);
    }

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
    {
        // Claims a facilities count but no payload follows.
        Assert.Null(UnlockSnapshot.Decode(new byte[] { 0xFF }));
    }

    [Fact]
    public void Decode_RejectsTruncatedString_ReturnsNull()
    {
        // facCount=1, idLen=5, but only 2 of the 5 string bytes follow → rejected (not garbage).
        var truncated = new byte[]
        {
            0x01, 0x00,             // facCount = 1
            0x05, 0x00,             // idLen = 5
            0x41, 0x42,             // only "AB" — 3 short
        };
        Assert.Null(UnlockSnapshot.Decode(truncated));
    }

    [Fact]
    public void Encode_NullSnapshot_ReturnsNull() => Assert.Null(UnlockSnapshot.Encode(null));

    [Fact]
    public void Encode_StableWireBytes_Pinned()
    {
        // Pin the EXACT wire layout so an accidental format change is caught. Snapshot:
        //   Facilities=["AB"]; Manufacture=["C"]; Augmentations=["D"].
        var snap = new UnlockSnapshot();
        snap.Facilities.Add("AB");
        snap.Manufacture.Add("C");
        snap.Augmentations.Add("D");

        var bytes = UnlockSnapshot.Encode(snap);

        var expected = new byte[]
        {
            0x01, 0x00,                 // facCount = 1
            0x02, 0x00, 0x41, 0x42,     // idLen=2, "AB"
            0x01, 0x00,                 // manuCount = 1
            0x01, 0x00, 0x43,           // idLen=1, "C"
            0x01, 0x00,                 // augCount = 1
            0x01, 0x00, 0x44,           // idLen=1, "D"
        };
        Assert.Equal(expected, bytes);
    }

    // ─── registration: the unlock channel claims a distinct, stable surface/channel id ────
    [Fact]
    public void ChannelId_Is3_AndDistinctFromOtherChannels()
    {
        Assert.Equal((byte)3, SurfaceIds.UnlockChannel);
        // All four live state-channel ids are distinct (1 inventory, 2 research, 3 unlock, 4 diplomacy).
        var ids = new[] { SurfaceIds.InventoryChannel, SurfaceIds.ResearchChannel,
                          SurfaceIds.UnlockChannel, SurfaceIds.DiplomacyChannel };
        Assert.Equal(ids.Length, new System.Collections.Generic.HashSet<byte>(ids).Count);
    }
}
