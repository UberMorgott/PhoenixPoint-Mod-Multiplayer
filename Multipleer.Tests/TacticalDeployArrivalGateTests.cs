using Multipleer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the CLIENT deploy-ARRIVAL gate. The real co-op flow brings the client into its
// own tactical level through the load barrier (curtain Loaded→Playing) BEFORE the host's chunked
// tac.deploy finishes reassembling. So when the deploy arrives, the client is already in a live
// tactical level and must HYDRATE that level — NOT run ClientLaunchMission, which assumes the client is
// still in the geoscape (its GeoLevelController() is null in tactical → the round-5 break:
// "ClientLaunchMission: no GeoLevelController", mirror never arms, every tac.move "no actor for netId").
// Only when no tactical level is live yet (legacy geoscape path) should the deploy drive a launch.
public class TacticalDeployArrivalGateTests
{
    [Fact]
    public void LiveTacticalLevel_HydratesExisting()
    {
        // Round-5 real case: client already in its tactical level when the deploy arrives.
        var d = TacticalDeployArrivalGate.Decide(hasLiveTacticalLevel: true);
        Assert.Equal(TacticalDeployArrivalGate.Decision.HydrateExisting, d);
    }

    [Fact]
    public void NoTacticalLevel_LaunchesThenHydrates()
    {
        // Legacy geoscape path: deploy must drive the client launch, hydrate on the Playing transition.
        var d = TacticalDeployArrivalGate.Decide(hasLiveTacticalLevel: false);
        Assert.Equal(TacticalDeployArrivalGate.Decision.LaunchThenHydrate, d);
    }
}
