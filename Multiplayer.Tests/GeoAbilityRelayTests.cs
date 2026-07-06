using Multiplayer.Sync.Geoscape;
using Xunit;

// Generic geoscape ability relay — pure allowlist policy (which GeoAbility subclasses ride the client→host
// GeoAbility.Activate relay). Unity-free; the Harmony patch + reflection glue are in-game verified, not here.
public class GeoAbilityRelayTests
{
    [Theory]
    [InlineData("HarvestFromSiteAbility")]
    [InlineData("ExcavateAbility")]
    [InlineData("EmergencyRepairAbility")]
    [InlineData("ScanAbility")]
    [InlineData("AncientSiteProbeAbility")]
    [InlineData("ActivateBaseAbility")]
    [InlineData("AncientGuardianGuardAbility")]
    public void Relayable_TheSevenSimMutatingAbilities(string typeName)
        => Assert.True(GeoAbilityRelay.IsRelayable(typeName));

    [Theory]
    [InlineData("MoveVehicleAbility")]   // travel — relayed by its OWN StartTravel patch, must pass through here
    [InlineData("LaunchMissionAbility")] // enters tactical — not a geoscape sim mutation
    [InlineData("ShootAbility")]         // tactical, not geoscape
    [InlineData("SomeUnknownAbility")]
    public void NotRelayable_OffListAbilities(string typeName)
        => Assert.False(GeoAbilityRelay.IsRelayable(typeName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NotRelayable_NullOrEmpty(string typeName)
        => Assert.False(GeoAbilityRelay.IsRelayable(typeName));

    [Fact]
    public void Allowlist_HasExactlySevenEntries()
        => Assert.Equal(7, GeoAbilityRelay.RelayableAbilityTypeNames.Length);
}
