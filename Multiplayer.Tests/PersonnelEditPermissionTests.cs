using System;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Multiplayer.Validation;
using Xunit;

// PS4 client-edit gating: per-category permission mapping + per-soldier ownership (SoldierAssignment 0x41)
// + the peer-keyed intent dedup that keeps 3+ players from dropping each other's edits.
public class PersonnelEditPermissionTests
{
    [Fact]
    public void PersonnelCategories_MapToExpectedBits()
    {
        Assert.Equal(CampaignPermission.ManageEquipment, PermissionGate.PermissionFor(ActionCategory.Equip));
        Assert.Equal(CampaignPermission.ManageRecruitment, PermissionGate.PermissionFor(ActionCategory.Recruitment));
        Assert.Equal(CampaignPermission.ControlSoldiers, PermissionGate.PermissionFor(ActionCategory.ControlSoldiers));
    }

    [Fact]
    public void EquipBitOnly_AllowsEquip_DeniesControlSoldiers()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermissionsRaw(g, (int)CampaignPermission.ManageEquipment); // no FullCommander
        Assert.True(PermissionGate.CheckFor(g, ActionCategory.Equip));
        Assert.False(PermissionGate.CheckFor(g, ActionCategory.ControlSoldiers));
        Assert.False(PermissionGate.CheckFor(g, ActionCategory.Recruitment));
    }

    [Fact]
    public void OwnsSoldier_TrueOnlyForOwnedUnit_UnderControlSoldiers()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermissionsRaw(g, (int)CampaignPermission.ControlSoldiers); // scoped, not FullCommander
        PermissionManager.AssignSoldier(g, 500);
        Assert.True(PersonnelEditReflection.OwnsSoldier(g, 500));    // owned → editable
        Assert.False(PersonnelEditReflection.OwnsSoldier(g, 501));   // not owned → denied
    }

    [Fact]
    public void OwnsSoldier_FullCommander_OwnsEverything()
    {
        var g = Guid.NewGuid();
        PermissionManager.SetPermission(g, CampaignPermission.FullCommander, true);
        Assert.True(PersonnelEditReflection.OwnsSoldier(g, 999));    // default co-op grant edits any soldier
    }

    [Fact]
    public void OwnsSoldier_EmptyOrUnknownActor_Denied()
    {
        Assert.False(PersonnelEditReflection.OwnsSoldier(Guid.Empty, 1));       // fail-closed
        Assert.False(PersonnelEditReflection.OwnsSoldier(Guid.NewGuid(), 1));   // no assignment → denied
    }

    [Fact]
    public void IntentDedup_PeerKeyed_NoCrossTalkBetweenClients()
    {
        // The host dedups incoming intents by (peerId, nonce). Two clients both start their local nonce
        // counters at 1 — a (nonce)-only key would silently drop the second client's first edit. Peer-keyed:
        // both pass; a genuine transport-duplicate (same peer, same nonce) is dropped.
        var dedup = new RequestDedup(16);
        Assert.False(dedup.IsDuplicate(1001UL, 1));   // client A, nonce 1 — new
        Assert.False(dedup.IsDuplicate(1002UL, 1));   // client B, nonce 1 — new (no cross-talk)
        Assert.True(dedup.IsDuplicate(1001UL, 1));    // client A, nonce 1 again — transport double, dropped
        Assert.False(dedup.IsDuplicate(1002UL, 2));   // client B, nonce 2 — new
    }
}
