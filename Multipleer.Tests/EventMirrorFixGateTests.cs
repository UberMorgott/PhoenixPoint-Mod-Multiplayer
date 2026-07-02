using Multipleer.Network.Sync;
using Xunit;

public class EventMirrorFixGateTests
{
    // Pins the SHIPPED default ON: the single-choice event-window stage-lockstep fix (occId-keyed result page +
    // burst-safe reward arming + prompt-mirror/advance) is locked in after in-game validation (2026-06-26), so
    // it is now the default. Mirrors the other rollout-gate pin tests; flip back to false only to fall back to the legacy path.
    [Fact]
    public void Enabled_DefaultsOn()
    {
        Assert.True(EventMirrorFixGate.Enabled);
    }
}
