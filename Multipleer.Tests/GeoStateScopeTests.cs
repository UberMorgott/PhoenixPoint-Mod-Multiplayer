using Multipleer.Network.CommandSync;
using Xunit;

public class GeoStateScopeTests
{
    [Fact]
    public void ScopeBytes_AreStable()
    {
        // Byte values are serialized on the wire (0x35 GeoStateDiff) — never renumber.
        Assert.Equal((byte)1, (byte)GeoStateScope.Vehicle);
        Assert.Equal((byte)2, (byte)GeoStateScope.Site);
        Assert.Equal((byte)3, (byte)GeoStateScope.MarketPrice);
        Assert.Equal((byte)4, (byte)GeoStateScope.FactionTraffic);
        Assert.Equal((byte)5, (byte)GeoStateScope.FactionState);
        Assert.Equal((byte)255, (byte)GeoStateScope.Checksum);
    }
}
