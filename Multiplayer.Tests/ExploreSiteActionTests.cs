using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Xunit;

// Explore-POI relay (Task 1) — pure wire round-trip for the ExploreSiteAction payload (Write → bytes → Read via
// the registry). Apply/Validate bind live game types and are in-game verified, not unit-testable here.
public class ExploreSiteActionTests
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
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.ExploreSite));
    }

    [Fact]
    public void ExploreSite_HasDistinctStableId()
        => Assert.Equal((ushort)41, SyncedActionIds.ExploreSite);   // 41 = next free in the vehicle range (40=MoveVehicle)

    [Fact]
    public void RoundTrip_PreservesOwnerAndVehicle()
    {
        SyncRegistration.RegisterAll();
        int owner = Multiplayer.Network.Sync.State.GeoVehiclePos.StableOwnerKey("PP_PhoenixFactionDef");
        var original = new ExploreSiteAction(owner, 5);

        var rt = RoundTrip(original);
        Assert.IsType<ExploreSiteAction>(rt);
        var ex = (ExploreSiteAction)rt;

        Assert.Equal(SyncedActionIds.ExploreSite, ex.ActionId);
        Assert.Equal(ActionCategory.VehicleTravel, ex.Category);
        Assert.Equal(owner, ex.OwnerId);
        Assert.Equal(5, ex.VehicleId);
        Assert.Equal(Write(original), Write(ex));   // byte-identical re-serialize
    }

    [Fact]
    public void Category_IsVehicleTravel()
        => Assert.Equal(ActionCategory.VehicleTravel, new ExploreSiteAction(0, 0).Category);
}
