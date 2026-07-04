using System.IO;
using System.Text;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.Actions;
using Xunit;

// Inc4 S2 travel intent relay — pure wire round-trip for the MoveVehicleAction payload (Write → bytes → Read
// via the registry). Apply/Validate bind live game types and are in-game verified, not unit-testable here.
public class MoveVehicleActionTests
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

    private static ISyncedAction RoundTrip(ISyncedAction a)
    {
        var bytes = Write(a);
        using (var ms = new MemoryStream(bytes))
        using (var r = new BinaryReader(ms, Encoding.UTF8))
            return SyncedActionRegistry.Read(a.ActionId, r);
    }

    [Fact]
    public void Registered_AfterRegisterAll()
    {
        SyncRegistration.RegisterAll();
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.MoveVehicle));
    }

    [Fact]
    public void RoundTrip_PreservesOwnerVehicleAndOrderedDestPath()
    {
        SyncRegistration.RegisterAll();
        int owner = Multipleer.Network.Sync.State.GeoVehiclePos.StableOwnerKey("PP_PhoenixFactionDef");
        var original = new MoveVehicleAction(owner, 3, new[] { 116, 85, 37 });

        var rt = RoundTrip(original);
        Assert.IsType<MoveVehicleAction>(rt);
        var mv = (MoveVehicleAction)rt;

        Assert.Equal(SyncedActionIds.MoveVehicle, mv.ActionId);
        Assert.Equal(ActionCategory.VehicleTravel, mv.Category);
        Assert.Equal(owner, mv.OwnerId);
        Assert.Equal(3, mv.VehicleId);
        Assert.Equal(new[] { 116, 85, 37 }, mv.DestSiteIds);   // order preserved (chained/multi-hop faithful)
        Assert.Equal(Write(original), Write(mv));               // byte-identical re-serialize
    }

    [Fact]
    public void RoundTrip_SingleDestination()
    {
        SyncRegistration.RegisterAll();
        var mv = (MoveVehicleAction)RoundTrip(new MoveVehicleAction(7, 1, new[] { 42 }));
        Assert.Equal(new[] { 42 }, mv.DestSiteIds);
    }

    [Fact]
    public void Category_IsVehicleTravel()
        => Assert.Equal(ActionCategory.VehicleTravel, new MoveVehicleAction(0, 0, new[] { 1 }).Category);
}
