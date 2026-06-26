using Multipleer.Network.Sync;
using Xunit;

public class GeoRailGateTests
{
    // Pins the SHIPPED default OFF: with the gate off the geoscape envelope rail emits nothing, so the
    // legacy 0x60-0x66 path is byte-for-byte unchanged (behavior-preserving additive rollout).
    [Fact]
    public void Enabled_DefaultsOff()
    {
        Assert.False(GeoRailGate.Enabled);
    }
}
