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

    // FIX 4 fail-closed invariant: an unmapped / forged peer resolves to Guid.Empty (no assignment).
    // The permission check MUST deny it for every category — SyncEngine.OnActionRequest also rejects
    // Guid.Empty explicitly before this check, but the underlying gate must never default permissive.
    [Fact]
    public void EmptyGuid_IsDeniedEverything()
    {
        Assert.False(PermissionGate.CheckFor(Guid.Empty, ActionCategory.Research));
        Assert.False(PermissionGate.CheckFor(Guid.Empty, ActionCategory.Manufacturing));
        Assert.False(PermissionGate.CheckFor(Guid.Empty, ActionCategory.BaseConstruction));
        Assert.False(PermissionGate.CheckFor(Guid.Empty, ActionCategory.Dialogs));
        Assert.False(PermissionGate.CheckFor(Guid.Empty, ActionCategory.TimeControl));
    }
}
