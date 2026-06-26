using Multipleer.Network.Sync;
using Xunit;

public class EventMirrorFixGateTests
{
    // Pins the SHIPPED default OFF: the geoscape event-window desync fixes (occId-keyed result page + burst-safe
    // reward arming) are additive and must be byte-for-byte unchanged until in-game-validated. Mirrors
    // GeoRailGateTests / ReportMirrorGateTests; flips to a DefaultsOn pin after in-game validation.
    [Fact]
    public void Enabled_DefaultsOff()
    {
        Assert.False(EventMirrorFixGate.Enabled);
    }
}
