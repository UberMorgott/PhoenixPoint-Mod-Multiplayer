using System;
using Multipleer.Network.Sync;
using Multipleer.Validation;
using Xunit;

public class PermissionGateTests
{
    [Fact]
    public void Category_MapsToPermission()
    {
        Assert.Equal(CampaignPermission.ManageResearch, PermissionGate.PermissionFor(ActionCategory.Research));
        Assert.Equal(CampaignPermission.ManageManufacturing, PermissionGate.PermissionFor(ActionCategory.Manufacturing));
        Assert.Equal(CampaignPermission.ManageBases, PermissionGate.PermissionFor(ActionCategory.BaseConstruction));
        Assert.Equal(CampaignPermission.ManageBases, PermissionGate.PermissionFor(ActionCategory.BaseRepair));
        Assert.Equal(CampaignPermission.ManageDialogs, PermissionGate.PermissionFor(ActionCategory.Dialogs));
    }

    [Fact]
    public void FullCommander_AllowsEverything()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermission(g, CampaignPermission.FullCommander, true);
        Assert.True(PermissionGate.CheckFor(g, ActionCategory.Research));
        Assert.True(PermissionGate.CheckFor(g, ActionCategory.Dialogs));
    }

    [Fact]
    public void SpecificBitCleared_Denies()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermissionsRaw(g, (int)CampaignPermission.ManageManufacturing); // only manufacturing, no FullCommander
        Assert.True(PermissionGate.CheckFor(g, ActionCategory.Manufacturing));
        Assert.False(PermissionGate.CheckFor(g, ActionCategory.Research));
    }
}
