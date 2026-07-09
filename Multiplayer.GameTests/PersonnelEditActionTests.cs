using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Xunit;

// PS4 client-edit intent relay — pure wire round-trips (Write → bytes → Read via the registry) for the six
// personnel actions, plus category + IHostOnlyApply + registration. Apply/Validate bind live game types and
// are in-game verified, not unit-testable here (MoveVehicleActionTests precedent).
public class PersonnelEditActionTests
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

    private static T RoundTrip<T>(T a) where T : ISyncedAction
    {
        SyncRegistration.RegisterAll();
        var bytes = Write(a);
        using (var ms = new MemoryStream(bytes))
        using (var r = new BinaryReader(ms, Encoding.UTF8))
            return (T)SyncedActionRegistry.Read(a.ActionId, r);
    }

    [Fact]
    public void Equip_RoundTrip_PreservesUnitAndThreeGuidLists()
    {
        var original = new EquipSoldierAction(4242,
            new[] { "armour-a", "armour-b" },
            new[] { "gun-1" },
            new string[0]);
        var rt = RoundTrip(original);
        Assert.Equal(SyncedActionIds.EquipSoldier, rt.ActionId);
        Assert.Equal(ActionCategory.Equip, rt.Category);
        Assert.Equal(4242, rt.UnitId);
        Assert.Equal(new[] { "armour-a", "armour-b" }, rt.Armour);
        Assert.Equal(new[] { "gun-1" }, rt.Equipment);
        Assert.Empty(rt.Inventory);
        Assert.Equal(Write(original), Write(rt));   // byte-identical re-serialize
    }

    [Fact]
    public void Augment_RoundTrip_PreservesAugmentGuid()
    {
        var rt = RoundTrip(new AugmentSoldierAction(7, "bionic-arm"));
        Assert.Equal(ActionCategory.Equip, rt.Category);
        Assert.Equal(7, rt.UnitId);
        Assert.Equal("bionic-arm", rt.AugmentGuid);
    }

    [Fact]
    public void HireRecruit_RoundTrip_PreservesSourceAndDest()
    {
        var rt = RoundTrip(new HireRecruitAction(0, 116, 85));
        Assert.Equal(ActionCategory.Recruitment, rt.Category);
        Assert.Equal(0, rt.SourceKind);
        Assert.Equal(116, rt.SourceId);
        Assert.Equal(85, rt.DestBaseSiteId);
    }

    [Fact]
    public void Transfer_RoundTrip_PreservesUnitAndDest()
    {
        var rt = RoundTrip(new TransferSoldierAction(9001, 1, 3));
        Assert.Equal(ActionCategory.ControlSoldiers, rt.Category);
        Assert.Equal(9001, rt.UnitId);
        Assert.Equal(1, rt.DestKind);
        Assert.Equal(3, rt.DestId);
    }

    [Fact]
    public void Dismiss_RoundTrip_PreservesUnit()
    {
        var rt = RoundTrip(new DismissSoldierAction(55));
        Assert.Equal(ActionCategory.ControlSoldiers, rt.Category);
        Assert.Equal(55, rt.UnitId);
    }

    [Fact]
    public void Rename_RoundTrip_PreservesUnitAndName()
    {
        var rt = RoundTrip(new RenameSoldierAction(12, "Иван Гроза"));   // UTF8 non-ASCII name preserved
        Assert.Equal(ActionCategory.ControlSoldiers, rt.Category);
        Assert.Equal(12, rt.UnitId);
        Assert.Equal("Иван Гроза", rt.NewName);
    }

    [Fact]
    public void AllPersonnelActions_RegisteredAfterRegisterAll()
    {
        SyncRegistration.RegisterAll();
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.EquipSoldier));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.AugmentSoldier));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.HireRecruit));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.TransferSoldier));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.DismissSoldier));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.RenameSoldier));
    }

    [Fact]
    public void AllPersonnelActions_AreHostOnlyApply()
    {
        // IHostOnlyApply → the client never replays the action (canon: client = pure mirror); the result
        // converges only via the #6/#9/#10 state channels, so no client-side double-apply.
        Assert.IsAssignableFrom<IHostOnlyApply>(new EquipSoldierAction(0, null, null, null));
        Assert.IsAssignableFrom<IHostOnlyApply>(new AugmentSoldierAction(0, null));
        Assert.IsAssignableFrom<IHostOnlyApply>(new HireRecruitAction(0, 0, 0));
        Assert.IsAssignableFrom<IHostOnlyApply>(new TransferSoldierAction(0, 0, 0));
        Assert.IsAssignableFrom<IHostOnlyApply>(new DismissSoldierAction(0));
        Assert.IsAssignableFrom<IHostOnlyApply>(new RenameSoldierAction(0, ""));
    }

    [Fact]
    public void PersonnelActionIds_AreInReservedBlock_60to79()
    {
        Assert.InRange(SyncedActionIds.EquipSoldier, (ushort)60, (ushort)79);
        Assert.InRange(SyncedActionIds.RenameSoldier, (ushort)60, (ushort)79);
        // Surface ids mirror the action ids (byte-stable migration).
        Assert.Equal(SyncedActionIds.EquipSoldier, SurfaceIds.EquipSoldier);
        Assert.Equal(SyncedActionIds.RenameSoldier, SurfaceIds.RenameSoldier);
    }
}
