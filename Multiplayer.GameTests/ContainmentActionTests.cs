using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Xunit;

// Containment client intents (feat-containment-actions) — pure wire round-trips (Write → bytes → Read via
// the registry) for kill/harvest of a captured Pandoran, plus category + IHostOnlyApply + registration
// (the PersonnelEditActionTests pattern). Apply/Validate bind live game types and are in-game verified,
// not unit-testable here (MoveVehicleActionTests precedent); the host-side captive resolution logic is
// pure and covered by ContainmentTargetTests in Multiplayer.Tests.
public class ContainmentActionTests
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
    public void KillCaptured_RoundTrip_PreservesOrdinalAndFingerprint()
    {
        var original = new KillCapturedUnitAction(3, "arthron-templatedef-guid");
        var rt = RoundTrip(original);
        Assert.Equal(SyncedActionIds.KillCapturedUnit, rt.ActionId);
        Assert.Equal(ActionCategory.Recruitment, rt.Category);   // pool-family gate → ManageRecruitment
        Assert.Equal(3, rt.Ordinal);
        Assert.Equal("arthron-templatedef-guid", rt.TemplateGuid);
        Assert.Equal(Write(original), Write(rt));   // byte-identical re-serialize
    }

    [Fact]
    public void HarvestCaptured_RoundTrip_PreservesOrdinalFingerprintAndResource()
    {
        var original = new HarvestCapturedUnitAction(0, "triton-templatedef-guid", 5);
        var rt = RoundTrip(original);
        Assert.Equal(SyncedActionIds.HarvestCapturedUnit, rt.ActionId);
        Assert.Equal(ActionCategory.Recruitment, rt.Category);
        Assert.Equal(0, rt.Ordinal);
        Assert.Equal("triton-templatedef-guid", rt.TemplateGuid);
        Assert.Equal(5, rt.ResourceType);
        Assert.Equal(Write(original), Write(rt));
    }

    [Fact]
    public void NullFingerprint_WritesAsEmpty_NeverThrows()
    {
        // A null guid must serialize as "" (BinaryWriter.Write(string) NREs on null) — and an empty
        // fingerprint fails closed host-side (ContainmentTarget.Resolve → -1 → logged no-op).
        var rt = RoundTrip(new KillCapturedUnitAction(1, null));
        Assert.Equal(string.Empty, rt.TemplateGuid);
    }

    [Fact]
    public void ContainmentActions_RegisteredAfterRegisterAll()
    {
        SyncRegistration.RegisterAll();
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.KillCapturedUnit));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.HarvestCapturedUnit));
    }

    [Fact]
    public void ContainmentActions_AreHostOnlyApply()
    {
        // IHostOnlyApply → the client never replays the action (canon: client = pure mirror); the result
        // converges only via the #10 captured full-set + the 0xA0 wallet snapshot (harvest yield).
        Assert.IsAssignableFrom<IHostOnlyApply>(new KillCapturedUnitAction(0, "g"));
        Assert.IsAssignableFrom<IHostOnlyApply>(new HarvestCapturedUnitAction(0, "g", 0));
    }

    [Fact]
    public void ContainmentActionIds_AreInPersonnelBlock_AndMirrorSurfaceIds()
    {
        Assert.InRange(SyncedActionIds.KillCapturedUnit, (ushort)60, (ushort)79);
        Assert.InRange(SyncedActionIds.HarvestCapturedUnit, (ushort)60, (ushort)79);
        Assert.Equal(SyncedActionIds.KillCapturedUnit, SurfaceIds.KillCapturedUnit);
        Assert.Equal(SyncedActionIds.HarvestCapturedUnit, SurfaceIds.HarvestCapturedUnit);
    }
}
