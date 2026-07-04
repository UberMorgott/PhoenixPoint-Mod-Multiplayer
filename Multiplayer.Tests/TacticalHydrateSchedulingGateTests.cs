using Multiplayer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the client deploy-hydrate SCHEDULING gate. The client hydrate runs the native
// Serializer.Read coroutine, which reads Timing.Current — and Timing.Current throws unless the calling
// thread is inside a running IUpdateable (Timing.cs:41-44). Driven inline from the network inbound
// callback (round-6 break), it threw and the mirror never armed. The fix defers the hydrate onto the
// level's Timing (so the serializer pump runs inside a running IUpdateable, mirroring the proven host
// capture path); if no Timing can be resolved, it falls back to an inline best-effort hydrate.
public class TacticalHydrateSchedulingGateTests
{
    [Fact]
    public void TimingAvailable_DefersOntoTiming()
    {
        var d = TacticalHydrateSchedulingGate.Decide(hasTiming: true);
        Assert.Equal(TacticalHydrateSchedulingGate.Decision.DeferOnTiming, d);
    }

    [Fact]
    public void NoTiming_FallsBackToInline()
    {
        var d = TacticalHydrateSchedulingGate.Decide(hasTiming: false);
        Assert.Equal(TacticalHydrateSchedulingGate.Decision.HydrateInline, d);
    }
}
