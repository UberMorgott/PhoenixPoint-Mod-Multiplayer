using System.Collections.Generic;
using Multiplayer.Network.Sync.State;
using Xunit;

// Inc4 S2 route-line metadata mirror (0xA6) — pure wire codec + change-signature tests for the
// GeoVehicleTravel surface. The engine glue (GeoVehicleTravelMirror) is game-bound and in-game verified; these
// lock the wire round-trip and the host's "unchanged → skip" travel-transition signature.
public class GeoVehicleTravelSnapshotTests
{
    [Fact]
    public void RoundTrip_PreservesSeqAndEveryField()
    {
        var input = new List<GeoVehicleTravelMeta>
        {
            new GeoVehicleTravelMeta(11, 7, travelling: true, currentSiteId: -1, destSiteIds: new[] { 5, 9, 2 }),
            new GeoVehicleTravelMeta(-22, 42, travelling: false, currentSiteId: 116, destSiteIds: new int[0]),
        };

        byte[] wire = GeoVehicleTravelSnapshot.Encode(123u, input);
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(wire, out uint seq, out var outList));

        Assert.Equal(123u, seq);
        Assert.Equal(input.Count, outList.Count);
        for (int i = 0; i < input.Count; i++)
            Assert.Equal(input[i], outList[i]);   // structural equality incl. the dest-id array
    }

    // Same VehicleID under two owners must stay distinct (per-faction VehicleID collision — the bug that broke
    // the 0xA5 mirror). The composite Key must differ so neither host sig cache nor client lookup collapses them.
    [Fact]
    public void RoundTrip_SameVehicleIdDifferentOwners_StayDistinct()
    {
        int ownerA = GeoVehiclePos.StableOwnerKey("PP_PhoenixFactionDef");
        int ownerB = GeoVehiclePos.StableOwnerKey("PP_SynedrionFactionDef");
        var input = new List<GeoVehicleTravelMeta>
        {
            new GeoVehicleTravelMeta(ownerA, 1, true, -1, new[] { 3 }),
            new GeoVehicleTravelMeta(ownerB, 1, true, -1, new[] { 4 }),
        };

        byte[] wire = GeoVehicleTravelSnapshot.Encode(1u, input);
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(wire, out _, out var outList));

        Assert.Equal(2, outList.Count);
        Assert.NotEqual(outList[0], outList[1]);
        Assert.NotEqual(outList[0].Key, outList[1].Key);   // shared key-space with GeoVehiclePos → composite key
        Assert.Equal(input[0], outList[0]);
        Assert.Equal(input[1], outList[1]);
    }

    [Fact]
    public void Encode_NullDestList_TreatedAsEmpty()
    {
        var input = new List<GeoVehicleTravelMeta> { new GeoVehicleTravelMeta(1, 1, false, -1, null) };
        byte[] wire = GeoVehicleTravelSnapshot.Encode(2u, input);
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(wire, out _, out var outList));
        Assert.Empty(outList[0].DestSiteIds);
    }

    [Fact]
    public void Encode_EmptyAndNullBatch_DecodeToZeroWithSeq()
    {
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(
            GeoVehicleTravelSnapshot.Encode(9u, new List<GeoVehicleTravelMeta>()), out uint s1, out var l1));
        Assert.Equal(9u, s1);
        Assert.Empty(l1);

        Assert.True(GeoVehicleTravelSnapshot.TryDecode(
            GeoVehicleTravelSnapshot.Encode(4u, null), out uint s2, out var l2));
        Assert.Equal(4u, s2);
        Assert.Empty(l2);
    }

    [Fact]
    public void TryDecode_Truncated_ReturnsFalse_NoPartialAccept()
    {
        byte[] wire = GeoVehicleTravelSnapshot.Encode(5u, new List<GeoVehicleTravelMeta>
        {
            new GeoVehicleTravelMeta(3, 1, true, -1, new[] { 7, 8 }),
        });
        var chopped = new byte[wire.Length - 4];   // drop the last dest id → declared count no longer fits
        System.Array.Copy(wire, chopped, chopped.Length);
        Assert.False(GeoVehicleTravelSnapshot.TryDecode(chopped, out _, out _));
    }

    [Fact]
    public void TryDecode_Null_ReturnsFalse()
        => Assert.False(GeoVehicleTravelSnapshot.TryDecode(null, out _, out _));

    // ─── change signature: host skips a vehicle whose travel metadata is unchanged ───

    [Fact]
    public void Signature_SameMeta_Equal()
    {
        var a = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 5, 6 });
        var b = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 5, 6 });
        Assert.Equal(GeoVehicleTravelMeta.Signature(a), GeoVehicleTravelMeta.Signature(b));
    }

    [Fact]
    public void Signature_ChangesOnTravellingFlag()
    {
        var moving = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 5 });
        var stopped = new GeoVehicleTravelMeta(9, 1, false, 5, new int[0]);   // arrived → stop ships once to clear line
        Assert.NotEqual(GeoVehicleTravelMeta.Signature(moving), GeoVehicleTravelMeta.Signature(stopped));
    }

    [Fact]
    public void Signature_ChangesWhenWaypointPassed()
    {
        var before = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 5, 6, 7 });
        var after = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 6, 7 });   // first waypoint popped
        Assert.NotEqual(GeoVehicleTravelMeta.Signature(before), GeoVehicleTravelMeta.Signature(after));
    }

    [Fact]
    public void Signature_ChangesOnCurrentSite()
    {
        var inTransit = new GeoVehicleTravelMeta(9, 1, false, -1, new int[0]);
        var atSite = new GeoVehicleTravelMeta(9, 1, false, 42, new int[0]);
        Assert.NotEqual(GeoVehicleTravelMeta.Signature(inTransit), GeoVehicleTravelMeta.Signature(atSite));
    }
}
