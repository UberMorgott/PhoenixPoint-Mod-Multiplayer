using Multipleer.Network.Sync;
using Xunit;

public class GeoRailGateTests
{
    // Pins the SHIPPED default ON: slice-1 locked in after in-game validation, so the new 0x67 wallet
    // envelope rail is now the default and mirrors the migrated geoscape message onto the shared rail.
    [Fact]
    public void Enabled_DefaultsOn()
    {
        Assert.True(GeoRailGate.Enabled);
    }
}
