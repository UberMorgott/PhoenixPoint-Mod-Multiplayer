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
            SiteIds = new[] { "site-a", "site-b", "site-c" }
        };

        var bytes = CommandCodec.EncodeStartTravel(src);
        var back = CommandCodec.DecodeStartTravel(bytes);

        Assert.Equal("veh-7", back.VehicleId);
        Assert.Equal("njf-guid", back.OwnerFactionGuid);
        Assert.Equal(new[] { "site-a", "site-b", "site-c" }, back.SiteIds);
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
