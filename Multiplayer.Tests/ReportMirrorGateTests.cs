using Multiplayer.Network.Sync;
using Xunit;

public class ReportMirrorGateTests
{
    // Pins the report-window mirror ON (2026-07-05): flipped after the research-complete report was confirmed
    // missing on the client. The host now broadcasts the whitelisted report modals on 0x69 and the client
    // reconstructs + shows them. Rollback = flip ReportMirrorGate.Enabled back to false.
    [Fact]
    public void Enabled_DefaultsOn()
    {
        Assert.True(ReportMirrorGate.Enabled);
    }
}
