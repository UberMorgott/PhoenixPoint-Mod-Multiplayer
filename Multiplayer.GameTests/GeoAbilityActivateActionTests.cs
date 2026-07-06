using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Xunit;

// Generic geoscape ability relay — pure wire round-trip for the GeoAbilityActivateAction payload (Write → bytes
// → Read via the registry), per actor-kind / target-kind combination. Apply binds live game types via the
// ApplyProvider seam (GeoAbilityRelayReflection) and is in-game verified, not unit-testable here.
public class GeoAbilityActivateActionTests
{
    private static byte[] Write(ISyncedAction a)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            a.Write(w);
            w.Flush();
            return ms.ToArray();
        }
    }

    private static GeoAbilityActivateAction RoundTrip(ISyncedAction a)
    {
        var bytes = Write(a);
        using (var ms = new MemoryStream(bytes))
        using (var r = new BinaryReader(ms, Encoding.UTF8))
            return Assert.IsType<GeoAbilityActivateAction>(SyncedActionRegistry.Read(a.ActionId, r));
    }

    [Fact]
    public void Registered_AfterRegisterAll()
    {
        SyncRegistration.RegisterAll();
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.GeoAbilityActivate));
    }

    [Fact]
    public void Pins_ActionIdAndCategory()
    {
        var a = new GeoAbilityActivateAction(GeoAbilityActivateAction.ActorSite, 0, 0, 5, "g",
            GeoAbilityActivateAction.TargetNone, 0, 0, 0, 0f, 0f, 0f, "");
        Assert.Equal(SyncedActionIds.GeoAbilityActivate, a.ActionId);
        Assert.Equal(ActionCategory.GeoAbility, a.Category);
    }

    [Fact]
    public void RoundTrip_VehicleActor_SiteTarget_WithFaction()   // Excavate / Scan / Harvest shape
    {
        SyncRegistration.RegisterAll();
        int owner = Multiplayer.Network.Sync.State.GeoVehiclePos.StableOwnerKey("PP_PhoenixFactionDef");
        var original = new GeoAbilityActivateAction(GeoAbilityActivateAction.ActorVehicle, owner, 3, 0,
            "excavate-guid", GeoAbilityActivateAction.TargetSite, 116, 0, 0, 0f, 0f, 0f, "phoenix-guid");

        var rt = RoundTrip(original);
        Assert.Equal(GeoAbilityActivateAction.ActorVehicle, rt.ActorKind);
        Assert.Equal(owner, rt.ActorOwnerId);
        Assert.Equal(3, rt.ActorVehicleId);
        Assert.Equal("excavate-guid", rt.AbilityDefGuid);
        Assert.Equal(GeoAbilityActivateAction.TargetSite, rt.TargetKind);
        Assert.Equal(116, rt.TargetSiteId);
        Assert.Equal("phoenix-guid", rt.TargetFactionGuid);
        Assert.Equal(Write(original), Write(rt));   // byte-identical re-serialize
    }

    [Fact]
    public void RoundTrip_VehicleActor_PosTarget()   // AncientSiteProbe shape (no faction, no target actor)
    {
        SyncRegistration.RegisterAll();
        var original = new GeoAbilityActivateAction(GeoAbilityActivateAction.ActorVehicle, 7, 1, 0,
            "probe-guid", GeoAbilityActivateAction.TargetPos, 0, 0, 0, 1.5f, -2.25f, 3.75f, "");

        var rt = RoundTrip(original);
        Assert.Equal(GeoAbilityActivateAction.TargetPos, rt.TargetKind);
        Assert.Equal(1.5f, rt.TX);
        Assert.Equal(-2.25f, rt.TY);
        Assert.Equal(3.75f, rt.TZ);
        Assert.Equal("", rt.TargetFactionGuid);
        Assert.Equal(Write(original), Write(rt));
    }

    [Fact]
    public void RoundTrip_VehicleActor_VehicleTarget()   // EmergencyRepair shape (target = a vehicle)
    {
        SyncRegistration.RegisterAll();
        var original = new GeoAbilityActivateAction(GeoAbilityActivateAction.ActorVehicle, 9, 2, 0,
            "repair-guid", GeoAbilityActivateAction.TargetVehicle, 0, 9, 2, 0f, 0f, 0f, "");

        var rt = RoundTrip(original);
        Assert.Equal(GeoAbilityActivateAction.TargetVehicle, rt.TargetKind);
        Assert.Equal(9, rt.TargetOwnerId);
        Assert.Equal(2, rt.TargetVehicleId);
        Assert.Equal(Write(original), Write(rt));
    }

    [Fact]
    public void RoundTrip_SiteActor_SiteTarget_WithFaction()   // ActivateBase / AncientGuardianGuard shape
    {
        SyncRegistration.RegisterAll();
        var original = new GeoAbilityActivateAction(GeoAbilityActivateAction.ActorSite, 0, 0, 42,
            "activatebase-guid", GeoAbilityActivateAction.TargetSite, 42, 0, 0, 0f, 0f, 0f, "phoenix-guid");

        var rt = RoundTrip(original);
        Assert.Equal(GeoAbilityActivateAction.ActorSite, rt.ActorKind);
        Assert.Equal(42, rt.ActorSiteId);
        Assert.Equal(GeoAbilityActivateAction.TargetSite, rt.TargetKind);
        Assert.Equal(42, rt.TargetSiteId);
        Assert.Equal("phoenix-guid", rt.TargetFactionGuid);
        Assert.Equal(Write(original), Write(rt));
    }

    [Fact]
    public void RoundTrip_SiteActor_NoneTarget()   // self-target fallback (no payload target block)
    {
        SyncRegistration.RegisterAll();
        var original = new GeoAbilityActivateAction(GeoAbilityActivateAction.ActorSite, 0, 0, 11,
            "guid", GeoAbilityActivateAction.TargetNone, 0, 0, 0, 0f, 0f, 0f, "");

        var rt = RoundTrip(original);
        Assert.Equal(GeoAbilityActivateAction.TargetNone, rt.TargetKind);
        Assert.Equal(11, rt.ActorSiteId);
        Assert.Equal(Write(original), Write(rt));
    }
}
