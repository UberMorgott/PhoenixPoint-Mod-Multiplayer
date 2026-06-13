using Multipleer.Network.CommandSync;
using Multipleer.Network.MessageLayer;
using Multipleer.Validation;
using Xunit;

public class InterceptRegistryTests
{
    [Fact]
    public void Lookup_StartTravel_ReturnsConfirmedAircraftEntry()
    {
        var e = InterceptRegistry.Lookup(CampaignActionType.StartTravel);
        Assert.NotNull(e);
        Assert.Equal(CampaignPermission.ManageAircraft, e.RequiredPermission);
        Assert.Equal("PhoenixPoint.Geoscape.Entities.GeoVehicle", e.DeclaringTypeName);
        Assert.Equal("StartTravel", e.MethodName);
        Assert.True(e.SignatureConfirmed);
    }

    [Fact]
    public void Lookup_StartResearch_IsPending()
    {
        // SetQueued absent in this build → entry present but flagged unconfirmed.
        var e = InterceptRegistry.Lookup(CampaignActionType.StartResearch);
        Assert.NotNull(e);
        Assert.False(e.SignatureConfirmed);
    }

    [Fact]
    public void Lookup_UnregisteredType_ReturnsNull()
    {
        Assert.Null(InterceptRegistry.Lookup(CampaignActionType.AssignSoldier));
    }

    [Fact]
    public void Lookup_SetTimeState_ReturnsConfirmedControlTimeEntry()
    {
        var e = InterceptRegistry.Lookup(CampaignActionType.SetTimeState);
        Assert.NotNull(e);
        Assert.Equal(CampaignPermission.ControlTime, e.RequiredPermission);
        Assert.True(e.SignatureConfirmed);
    }
}
