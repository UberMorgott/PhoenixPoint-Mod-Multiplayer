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
            SiteIds = new[] { "site-a", "site-b", "site-c" }
        };

        var bytes = CommandCodec.EncodeStartTravel(src);
        var back = CommandCodec.DecodeStartTravel(bytes);

        Assert.Equal("veh-7", back.VehicleId);
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
