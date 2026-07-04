using System.Collections.Generic;
using Multipleer.Network.Sync.State;
using Xunit;

// Inc4 S2 exploration-progress mirror (0xA7) — pure wire codec + change-signature tests for the GeoVehicleExplore
// surface. The engine glue (GeoVehicleExploreMirror / GeoVehicleExploreReflection) is game-bound and in-game
// verified; these lock the wire round-trip and the host's whole-percent "unchanged → skip" progress signature.
public class GeoVehicleExploreSnapshotTests
{
    [Fact]
    public void RoundTrip_PreservesSeqAndEveryField()
    {
        var input = new List<GeoVehicleExploreMeta>
        {
            new GeoVehicleExploreMeta(11, 7, exploring: true, siteId: 116, progress: 0.5f),
            new GeoVehicleExploreMeta(-22, 42, exploring: false, siteId: -1, progress: 0f),
            new GeoVehicleExploreMeta(3, 9, exploring: true, siteId: 4, progress: 0.25f),
        };

        byte[] wire = GeoVehicleExploreSnapshot.Encode(123u, input);
        Assert.True(GeoVehicleExploreSnapshot.TryDecode(wire, out uint seq, out var outList));

        Assert.Equal(123u, seq);
        Assert.Equal(input.Count, outList.Count);
        for (int i = 0; i < input.Count; i++)
            Assert.Equal(input[i], outList[i]);   // structural equality incl. the f32 progress (bit-exact round-trip)
    }

    // Same VehicleID under two owners must stay distinct (per-faction VehicleID collision — the bug that broke the
    // 0xA5 mirror). The composite Key must differ so neither host sig cache nor client lookup collapses them.
    [Fact]
    public void RoundTrip_SameVehicleIdDifferentOwners_StayDistinct()
    {
        int ownerA = GeoVehiclePos.StableOwnerKey("PP_PhoenixFactionDef");
        int ownerB = GeoVehiclePos.StableOwnerKey("PP_SynedrionFactionDef");
        var input = new List<GeoVehicleExploreMeta>
        {
            new GeoVehicleExploreMeta(ownerA, 1, true, 3, 0.1f),
            new GeoVehicleExploreMeta(ownerB, 1, true, 3, 0.1f),
        };

        byte[] wire = GeoVehicleExploreSnapshot.Encode(1u, input);
        Assert.True(GeoVehicleExploreSnapshot.TryDecode(wire, out _, out var outList));

        Assert.Equal(2, outList.Count);
        Assert.NotEqual(outList[0].Key, outList[1].Key);   // shared key-space with GeoVehiclePos → composite key
        Assert.Equal(input[0], outList[0]);
        Assert.Equal(input[1], outList[1]);
    }

    [Fact]
    public void Encode_EmptyAndNullBatch_DecodeToZeroWithSeq()
    {
        Assert.True(GeoVehicleExploreSnapshot.TryDecode(
            GeoVehicleExploreSnapshot.Encode(9u, new List<GeoVehicleExploreMeta>()), out uint s1, out var l1));
        Assert.Equal(9u, s1);
        Assert.Empty(l1);

        Assert.True(GeoVehicleExploreSnapshot.TryDecode(
            GeoVehicleExploreSnapshot.Encode(4u, null), out uint s2, out var l2));
        Assert.Equal(4u, s2);
        Assert.Empty(l2);
    }

    [Fact]
    public void TryDecode_Truncated_ReturnsFalse_NoPartialAccept()
    {
        byte[] wire = GeoVehicleExploreSnapshot.Encode(5u, new List<GeoVehicleExploreMeta>
        {
            new GeoVehicleExploreMeta(3, 1, true, 7, 0.8f),
        });
        var chopped = new byte[wire.Length - 4];   // drop the last 4 bytes → the declared record no longer fits
        System.Array.Copy(wire, chopped, chopped.Length);
        Assert.False(GeoVehicleExploreSnapshot.TryDecode(chopped, out _, out _));
    }

    [Fact]
    public void TryDecode_Null_ReturnsFalse()
        => Assert.False(GeoVehicleExploreSnapshot.TryDecode(null, out _, out _));

    // ─── change signature: host skips a vehicle whose exploration state is unchanged (whole-percent quantized) ───

    [Fact]
    public void Signature_NotExploring_CollapsesToOff_RegardlessOfSiteOrProgress()
    {
        var a = new GeoVehicleExploreMeta(9, 1, false, -1, 0f);
        var b = new GeoVehicleExploreMeta(9, 1, false, 42, 0.9f);   // site/progress ignored while not exploring
        Assert.Equal("off", GeoVehicleExploreMeta.Signature(a));
        Assert.Equal(GeoVehicleExploreMeta.Signature(a), GeoVehicleExploreMeta.Signature(b));
    }

    [Fact]
    public void Signature_ChangesOnExploringFlag()
    {
        var idle = new GeoVehicleExploreMeta(9, 1, false, -1, 0f);
        var started = new GeoVehicleExploreMeta(9, 1, true, 5, 0f);   // start ships once to draw the bar
        Assert.NotEqual(GeoVehicleExploreMeta.Signature(idle), GeoVehicleExploreMeta.Signature(started));
    }

    [Fact]
    public void Signature_ChangesOnWholePercentProgress()
    {
        var at10 = new GeoVehicleExploreMeta(9, 1, true, 5, 0.10f);
        var at11 = new GeoVehicleExploreMeta(9, 1, true, 5, 0.11f);   // +1% → new signature (bar steps)
        Assert.NotEqual(GeoVehicleExploreMeta.Signature(at10), GeoVehicleExploreMeta.Signature(at11));
    }

    [Fact]
    public void Signature_SubPercentProgressDrift_DoesNotChange()
    {
        var a = new GeoVehicleExploreMeta(9, 1, true, 5, 0.500f);
        var b = new GeoVehicleExploreMeta(9, 1, true, 5, 0.503f);   // <0.5% → same rounded percent → 0 bytes shipped
        Assert.Equal(GeoVehicleExploreMeta.Signature(a), GeoVehicleExploreMeta.Signature(b));
    }

    [Fact]
    public void Signature_ChangesWhenExploredSiteDiffers()
    {
        var atSiteA = new GeoVehicleExploreMeta(9, 1, true, 5, 0.5f);
        var atSiteB = new GeoVehicleExploreMeta(9, 1, true, 6, 0.5f);
        Assert.NotEqual(GeoVehicleExploreMeta.Signature(atSiteA), GeoVehicleExploreMeta.Signature(atSiteB));
    }

    [Fact]
    public void Percent_ClampsAndRounds()
    {
        Assert.Equal(0, GeoVehicleExploreMeta.Percent(-0.3f));
        Assert.Equal(100, GeoVehicleExploreMeta.Percent(1.4f));
        Assert.Equal(50, GeoVehicleExploreMeta.Percent(0.5f));
        Assert.Equal(34, GeoVehicleExploreMeta.Percent(0.336f));
    }
}
