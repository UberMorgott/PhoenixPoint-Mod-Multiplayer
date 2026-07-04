using Multiplayer.Network.Sync;
using Xunit;

public class ReportMirrorGateTests
{
    // Pins the SHIPPED default OFF: Phase-A report-window mirror is additive and must be byte-for-byte
    // unchanged until in-game-validated. Mirrors the other rollout-gate pin tests; flips to a DefaultsOn pin after validation.
    [Fact]
    public void Enabled_DefaultsOff()
    {
        Assert.False(ReportMirrorGate.Enabled);
    }
}
