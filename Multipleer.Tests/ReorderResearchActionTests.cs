using System.IO;
using System.Text;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.Actions;
using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip + contract tests for <see cref="ReorderResearchAction"/> (research queue REORDER
/// sync). Mirrors <c>SyncActionSerializationTests</c> / <c>HostOnlyApplyMarkerTests</c>: only the pure
/// serialization path and the <see cref="IHostOnlyApply"/> marker are unit-testable — Apply binds live
/// game types via reflection and cannot be JIT'd in the test process.
/// </summary>
public class ReorderResearchActionTests
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
    public void Registration_RegistersReorderReader()
    {
        SyncRegistration.RegisterAll();
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.ReorderResearch));
    }

    [Theory]
    [InlineData(ResearchReorderKind.ToTop)]
    [InlineData(ResearchReorderKind.Up)]
    [InlineData(ResearchReorderKind.Down)]
    public void ReorderResearch_RoundTrips(ResearchReorderKind kind)
    {
        SyncRegistration.RegisterAll();
        var a = RoundTrip(new ReorderResearchAction("PX_LaserTech_ResearchDef", kind));
        Assert.IsType<ReorderResearchAction>(a);
        Assert.Equal(SyncedActionIds.ReorderResearch, a.ActionId);
        Assert.Equal(ActionCategory.Research, a.Category);
        // re-serialize and compare bytes to confirm id + kind survived intact.
        Assert.Equal(Write(new ReorderResearchAction("PX_LaserTech_ResearchDef", kind)), Write(a));
    }

    [Fact]
    public void ReorderKind_WireValuesAreStable()
    {
        // The kind byte is part of the wire contract — pin the values so a reorder never silently changes meaning.
        Assert.Equal((byte)0, (byte)ResearchReorderKind.ToTop);
        Assert.Equal((byte)1, (byte)ResearchReorderKind.Up);
        Assert.Equal((byte)2, (byte)ResearchReorderKind.Down);
    }

    [Fact]
    public void ReorderResearch_IsHostOnlyApply()
        => Assert.IsAssignableFrom<IHostOnlyApply>(new ReorderResearchAction("PX_LaserTech_ResearchDef", ResearchReorderKind.Up));

    [Fact]
    public void ReorderResearch_SurfaceIsRegistered()
    {
        var reg = new SurfaceRegistry();
        SyncRegistration.RegisterSurfaces(reg);
        Assert.True(reg.IsRegistered(SurfaceIds.ReorderResearch));
        Assert.True(reg.Get(SurfaceIds.ReorderResearch).Accepts(SyncKind.ActionRequest));
        Assert.Equal(GeoUiRefresh.Screen.Research, reg.Get(SurfaceIds.ReorderResearch).Screen);
    }
}
