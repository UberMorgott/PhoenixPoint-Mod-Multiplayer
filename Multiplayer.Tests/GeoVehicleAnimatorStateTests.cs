using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure mapping behind the aircraft turbine-tracer fix: the sim-frozen co-op client never runs NavigateRoutine, so
/// the native InitiateTravelling() (sole writer of Animator "State"=1) never fires and the flight-visual state stays
/// PARKED while the position mirror flies the vehicle — leaving the engine tracer VFX in its parked orientation
/// (renders in front instead of trailing). The travel-meta mirror writes AnimatorTravelState(Travelling) to close
/// that gap; this pins the value maps to the native GeoVehicle "State" constants (0 parked, 1 travelling).
/// </summary>
public class GeoVehicleAnimatorStateTests
{
    [Fact]
    public void Travelling_MapsToTravellingState_1()
        => Assert.Equal(1, GeoVehicleTravelMeta.AnimatorTravelState(travelling: true));

    [Fact]
    public void NotTravelling_MapsToParkedState_0()
        => Assert.Equal(0, GeoVehicleTravelMeta.AnimatorTravelState(travelling: false));

    [Fact]
    public void Constants_MatchNativeStateValues()
    {
        Assert.Equal(0, GeoVehicleTravelMeta.AnimStateParked);
        Assert.Equal(1, GeoVehicleTravelMeta.AnimStateTravelling);
    }
}
