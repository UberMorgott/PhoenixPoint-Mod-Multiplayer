using System.Collections.Generic;
using Multipleer.Network.CommandSync;
using Xunit;

public class GeoStateDiffCodecTests
{
    [Fact]
    public void ChangedMaskBits_AreStable()
    {
        // changedMask bit values are serialized on the wire (0x35 GeoStateDiff, scope Vehicle) — never renumber.
        Assert.Equal(1, GeoStateMask.SurfacePos);
        Assert.Equal(2, GeoStateMask.SurfaceRot);
        Assert.Equal(4, GeoStateMask.RangeRemaining);
        Assert.Equal(8, GeoStateMask.Travelling);
        Assert.Equal(16, GeoStateMask.CurrentSite);
        Assert.Equal(32, GeoStateMask.DestinationSites);
        Assert.Equal(64, GeoStateMask.HitPoints);
    }

    [Fact]
    public void DefaultVehicleStateRecord_HasZeroChangedMask()
    {
        var r = new GeoVehicleStateRecord();
        Assert.Equal(0, r.ChangedMask);
    }

    private static GeoStateDiff DiffOf(params GeoVehicleStateRecord[] records)
    {
        return new GeoStateDiff { Records = new List<GeoVehicleStateRecord>(records) };
    }

    [Fact]
    public void FullMaskVehicleRecord_RoundTrips_AllFields()
    {
        var src = new GeoVehicleStateRecord
        {
            FactionGuid = "FAC_NJ_GUID",
            VehicleID = 7,
            Seq = 12345UL,
            ChangedMask = GeoStateMask.SurfacePos | GeoStateMask.SurfaceRot | GeoStateMask.RangeRemaining
                          | GeoStateMask.Travelling | GeoStateMask.CurrentSite | GeoStateMask.DestinationSites
                          | GeoStateMask.HitPoints,
            PosX = 1.5f, PosY = -2.25f, PosZ = 3.75f,
            RotX = 0.1f, RotY = 0.2f, RotZ = 0.3f, RotW = 0.927f,
            RangeRemaining = 4200.5f,
            Travelling = true,
            CurrentSiteId = 42,
            DestinationSiteIds = new[] { 13, 8, 21 },
            HitPoints = 88.5f
        };

        var back = GeoStateDiffCodec.Decode(GeoStateDiffCodec.Encode(DiffOf(src)));

        Assert.Single(back.Records);
        var r = back.Records[0];
        Assert.Equal("FAC_NJ_GUID", r.FactionGuid);
        Assert.Equal(7, r.VehicleID);
        Assert.Equal(12345UL, r.Seq);
        Assert.Equal(src.ChangedMask, r.ChangedMask);
        Assert.Equal(1.5f, r.PosX);
        Assert.Equal(-2.25f, r.PosY);
        Assert.Equal(3.75f, r.PosZ);
        Assert.Equal(0.1f, r.RotX);
        Assert.Equal(0.2f, r.RotY);
        Assert.Equal(0.3f, r.RotZ);
        Assert.Equal(0.927f, r.RotW);
        Assert.Equal(4200.5f, r.RangeRemaining);
        Assert.True(r.Travelling);
        Assert.Equal(42, r.CurrentSiteId);
        Assert.Equal(new[] { 13, 8, 21 }, r.DestinationSiteIds); // ordered
        Assert.Equal(88.5f, r.HitPoints);
    }

    [Fact]
    public void PartialMask_WritesAndReads_OnlySetBits()
    {
        // Only bit0 SurfacePos + bit3 Travelling set: every other field must come back default.
        var src = new GeoVehicleStateRecord
        {
            FactionGuid = "FAC_PHX_GUID",
            VehicleID = 3,
            Seq = 9UL,
            ChangedMask = GeoStateMask.SurfacePos | GeoStateMask.Travelling,
            PosX = 10f, PosY = 20f, PosZ = 30f,
            // Fields below are set on the source but NOT flagged -> must NOT be serialized.
            RotX = 9f, RotY = 9f, RotZ = 9f, RotW = 9f,
            RangeRemaining = 999f,
            Travelling = true,
            CurrentSiteId = 99,
            DestinationSiteIds = new[] { 1, 2 },
            HitPoints = 77f
        };

        var back = GeoStateDiffCodec.Decode(GeoStateDiffCodec.Encode(DiffOf(src)));

        var r = back.Records[0];
        Assert.Equal("FAC_PHX_GUID", r.FactionGuid);
        Assert.Equal(3, r.VehicleID);
        Assert.Equal(9UL, r.Seq);
        Assert.Equal(GeoStateMask.SurfacePos | GeoStateMask.Travelling, r.ChangedMask);
        // Set bits round-tripped:
        Assert.Equal(10f, r.PosX);
        Assert.Equal(20f, r.PosY);
        Assert.Equal(30f, r.PosZ);
        Assert.True(r.Travelling);
        // Unset bits default:
        Assert.Equal(0f, r.RotX);
        Assert.Equal(0f, r.RotY);
        Assert.Equal(0f, r.RotZ);
        Assert.Equal(0f, r.RotW);
        Assert.Equal(0f, r.RangeRemaining);
        Assert.Equal(0, r.CurrentSiteId);
        Assert.Null(r.DestinationSiteIds);
        Assert.Equal(0f, r.HitPoints);
    }

    [Fact]
    public void ThreeRecordEnvelope_RoundTrips_InOrder()
    {
        var a = new GeoVehicleStateRecord { FactionGuid = "A", VehicleID = 1, Seq = 1UL, ChangedMask = GeoStateMask.SurfacePos, PosX = 1f };
        var b = new GeoVehicleStateRecord { FactionGuid = "B", VehicleID = 2, Seq = 2UL, ChangedMask = GeoStateMask.SurfacePos, PosX = 2f };
        var c = new GeoVehicleStateRecord { FactionGuid = "C", VehicleID = 3, Seq = 3UL, ChangedMask = GeoStateMask.SurfacePos, PosX = 3f };

        var back = GeoStateDiffCodec.Decode(GeoStateDiffCodec.Encode(DiffOf(a, b, c)));

        Assert.Equal(3, back.Records.Count);
        Assert.Equal("A", back.Records[0].FactionGuid);
        Assert.Equal(1f, back.Records[0].PosX);
        Assert.Equal("B", back.Records[1].FactionGuid);
        Assert.Equal(2f, back.Records[1].PosX);
        Assert.Equal("C", back.Records[2].FactionGuid);
        Assert.Equal(3f, back.Records[2].PosX);
    }

    [Fact]
    public void EmptyEnvelope_RoundTrips()
    {
        var back = GeoStateDiffCodec.Decode(GeoStateDiffCodec.Encode(DiffOf()));
        Assert.NotNull(back.Records);
        Assert.Empty(back.Records);
    }

    [Fact]
    public void NullFactionGuid_AndNullDestinationSites_EncodeAsEmpty_NoThrow()
    {
        var src = new GeoVehicleStateRecord
        {
            // FactionGuid left null on purpose.
            VehicleID = 5,
            Seq = 1UL,
            ChangedMask = GeoStateMask.DestinationSites, // flagged but DestinationSiteIds is null
            DestinationSiteIds = null
        };

        var back = GeoStateDiffCodec.Decode(GeoStateDiffCodec.Encode(DiffOf(src)));

        var r = back.Records[0];
        Assert.Equal("", r.FactionGuid);
        Assert.NotNull(r.DestinationSiteIds);
        Assert.Empty(r.DestinationSiteIds);
    }

    [Fact]
    public void FormatVersionByte_IsPresent_AndStable()
    {
        // First byte of the envelope is the stable formatVersion = 1.
        var bytes = GeoStateDiffCodec.Encode(DiffOf());
        Assert.True(bytes.Length >= 1);
        Assert.Equal((byte)1, bytes[0]);
    }
}
