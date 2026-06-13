using System;
using Multipleer.Network.CommandSync;
using Multipleer.Network.MessageLayer;
using Multipleer.Validation;
using Xunit;

public class PermissionGateTests
{
    [Fact]
    public void StartTravel_RequiresManageAircraft()
    {
        Assert.Equal(CampaignPermission.ManageAircraft,
            PermissionGate.RequiredPermission(CampaignActionType.StartTravel));
    }

    [Fact]
    public void IsAllowed_True_WhenGuidHasManageAircraft()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermission(g, CampaignPermission.ManageAircraft, true);
        Assert.True(PermissionGate.IsAllowed(g, CampaignActionType.StartTravel));
    }

    [Fact]
    public void IsAllowed_False_WhenGuidLacksFlag()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermission(g, CampaignPermission.ManageResearch, true);
        Assert.False(PermissionGate.IsAllowed(g, CampaignActionType.StartTravel));
    }

    [Fact]
    public void IsAllowed_False_ForEmptyGuid()
    {
        Assert.False(PermissionGate.IsAllowed(Guid.Empty, CampaignActionType.StartTravel));
    }

    [Fact]
    public void IsAllowed_True_ForFullCommander()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermission(g, CampaignPermission.FullCommander, true);
        Assert.True(PermissionGate.IsAllowed(g, CampaignActionType.StartTravel));
    }

    [Fact]
    public void SetTimeState_RequiresControlTime()
    {
        Assert.Equal(CampaignPermission.ControlTime,
            PermissionGate.RequiredPermission(CampaignActionType.SetTimeState));
    }

    [Fact]
    public void IsAllowed_True_WhenGuidHasControlTime()
    {
        var g = System.Guid.NewGuid();
        PermissionManager.SetPermission(g, CampaignPermission.ControlTime, true);
        Assert.True(PermissionGate.IsAllowed(g, CampaignActionType.SetTimeState));
    }

    [Fact]
    public void IsAllowed_False_ForSetTimeState_WhenGuidLacksControlTime()
    {
        var g = System.Guid.NewGuid();
        PermissionManager.SetPermission(g, CampaignPermission.ManageResearch, true);
        Assert.False(PermissionGate.IsAllowed(g, CampaignActionType.SetTimeState));
    }
}
