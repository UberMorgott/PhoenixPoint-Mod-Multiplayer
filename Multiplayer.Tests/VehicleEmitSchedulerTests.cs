using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;   // VehicleInterpolator
using Xunit;

// Task 3 — pure logic for the host vehicle-mirror emit scheduling: the immediate-emit trigger predicate, the
// throttle-counter math, and the interp-delay derivation (auto-tracks the poll cadence, no hardcoded 0.375).
public class VehicleEmitSchedulerTests
{
    // ─── immediate-emit trigger ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void VehicleTravel_TriggersImmediateEmit()
        => Assert.True(VehicleEmitScheduler.TriggersImmediateEmit(ActionCategory.VehicleTravel));

    [Theory]
    [InlineData(ActionCategory.Research)]
    [InlineData(ActionCategory.Manufacturing)]
    [InlineData(ActionCategory.BaseConstruction)]
    [InlineData(ActionCategory.Dialogs)]
    [InlineData(ActionCategory.TimeControl)]
    public void NonVehicleCategories_DoNotTrigger(ActionCategory c)
        => Assert.False(VehicleEmitScheduler.TriggersImmediateEmit(c));

    // ─── throttle-counter math ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ShouldPoll_FiresAtOrAboveInterval()
    {
        Assert.False(VehicleEmitScheduler.ShouldPoll(5, 6));
        Assert.True(VehicleEmitScheduler.ShouldPoll(6, 6));
        Assert.True(VehicleEmitScheduler.ShouldPoll(7, 6));
    }

    [Fact]
    public void ArmImmediate_MakesTheNextTickFire()
    {
        const int interval = 6;
        int counter = VehicleEmitScheduler.ArmImmediate(interval);
        // Next Tick does ++counter then compares — must fire on that very next frame.
        Assert.True(VehicleEmitScheduler.ShouldPoll(counter + 1, interval));
    }

    // ─── interp-delay derivation (auto-tracks the emit cadence) ─────────────────────────────────────────
    [Fact]
    public void DeriveDelay_IsOnePointFiveEmitIntervals()
        => Assert.Equal(0.15, VehicleInterpolator.DeriveDelaySeconds(6, 60.0, 1.5), 6);

    [Fact]
    public void DeriveDelay_TracksTheCanonicalEmitInterval()
    {
        // The live mirror derives its delay from VehicleEmitScheduler's canonical cadence; assert the cadence (6)
        // yields the intended ~0.15 s, and that doubling the cadence doubles the delay (no hardcoded constant).
        Assert.Equal(6, VehicleEmitScheduler.EmitTickInterval);
        double at6 = VehicleInterpolator.DeriveDelaySeconds(
            VehicleEmitScheduler.EmitTickInterval, VehicleEmitScheduler.NominalFps, VehicleEmitScheduler.EmitDelayMultiplier);
        double at12 = VehicleInterpolator.DeriveDelaySeconds(
            VehicleEmitScheduler.EmitTickInterval * 2, VehicleEmitScheduler.NominalFps, VehicleEmitScheduler.EmitDelayMultiplier);
        Assert.Equal(0.15, at6, 6);
        Assert.Equal(at6 * 2, at12, 6);
    }

    [Fact]
    public void DeriveDelay_GuardsZeroFps()
        => Assert.Equal(0.0, VehicleInterpolator.DeriveDelaySeconds(6, 0.0, 1.5));
}
