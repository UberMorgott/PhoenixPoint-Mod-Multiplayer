using Multipleer.Network.CommandSync;
using Xunit;

public class CommandCodecTests
{
    [Fact]
    public void StartTravelPayload_RoundTrips()
    {
        var src = new StartTravelPayload
        {
            VehicleId = "veh-7",
            // INC-3a: client->host input relay now carries the owning faction guid so the host
            // resolves a client-originated vehicle by (factionGuid, VehicleID), not Phoenix-only.
            OwnerFactionGuid = "njf-guid",
            SiteIds = new[] { "site-a", "site-b", "site-c" },
            // PIVOT Step A start-time alignment fields.
            StartGameTime = 64123456789.5,
            StartRangeRemaining = 1234.5f
        };

        var bytes = CommandCodec.EncodeStartTravel(src);
        var back = CommandCodec.DecodeStartTravel(bytes);

        Assert.Equal("veh-7", back.VehicleId);
        Assert.Equal("njf-guid", back.OwnerFactionGuid);
        Assert.Equal(new[] { "site-a", "site-b", "site-c" }, back.SiteIds);
        Assert.Equal(64123456789.5, back.StartGameTime);
        Assert.Equal(1234.5f, back.StartRangeRemaining);
    }

    [Fact]
    public void StartTravelPayload_StartGameTime_DoublePrecisionPreserved()
    {
        // The geoscape clock reaches ~6.4e10 game-seconds. A float32 round-trip there loses ~8192 s of
        // resolution (one ULP), collapsing distinct sample times -> no interpolation/lockstep. Assert the
        // DOUBLE wire field preserves a sub-second delta at full geoscape magnitude that a float cannot.
        var t0 = 64000000000.0;       // ~2030 years of game-seconds
        var t1 = 64000000000.25;      // +250 ms — within a float32 ULP at this magnitude (would be lost as float)
        var a = CommandCodec.DecodeStartTravel(CommandCodec.EncodeStartTravel(
                    new StartTravelPayload { VehicleId = "v", SiteIds = new string[0], StartGameTime = t0 }));
        var b = CommandCodec.DecodeStartTravel(CommandCodec.EncodeStartTravel(
                    new StartTravelPayload { VehicleId = "v", SiteIds = new string[0], StartGameTime = t1 }));
        Assert.Equal(t0, a.StartGameTime);
        Assert.Equal(t1, b.StartGameTime);
        Assert.NotEqual(a.StartGameTime, b.StartGameTime);                 // distinct after round-trip (double keeps it)
        Assert.Equal(0.25, b.StartGameTime - a.StartGameTime, 3);          // the 250 ms delta survives
        // Control documenting WHY double is required: casting both magnitudes to float32 COLLAPSES them (the
        // 250 ms delta is below one float ULP at 6.4e10), so a float wire field could not have preserved it.
        Assert.Equal((float)t0, (float)t1);
    }

    [Fact]
    public void StartTravelPayload_LegacyBytesNoTrailer_DecodeWithAbsentAlign()
    {
        // A pre-PIVOT sender wrote only VehicleId + OwnerFactionGuid + sites (no start-align trailer). The
        // decoder must EOF-guard and yield StartGameTime=0 / StartRangeRemaining=0 (absent -> client uses its
        // own local startTime capture), never throwing on the short buffer.
        using (var ms = new System.IO.MemoryStream())
        using (var bw = new System.IO.BinaryWriter(ms))
        {
            bw.Write("veh-legacy");      // VehicleId
            bw.Write("");                // OwnerFactionGuid
            bw.Write(2);                 // site count
            bw.Write("s1"); bw.Write("s2");
            var legacy = CommandCodec.DecodeStartTravel(ms.ToArray());
            Assert.Equal("veh-legacy", legacy.VehicleId);
            Assert.Equal(new[] { "s1", "s2" }, legacy.SiteIds);
            Assert.Equal(0.0, legacy.StartGameTime);          // absent -> 0
            Assert.Equal(0f, legacy.StartRangeRemaining);     // absent -> 0
        }
    }

    [Fact]
    public void StartTravelPayload_EmptyPath_RoundTrips()
    {
        var src = new StartTravelPayload { VehicleId = "veh-1", SiteIds = new string[0] };
        var back = CommandCodec.DecodeStartTravel(CommandCodec.EncodeStartTravel(src));
        Assert.Equal("veh-1", back.VehicleId);
        Assert.Empty(back.SiteIds);
    }
}
