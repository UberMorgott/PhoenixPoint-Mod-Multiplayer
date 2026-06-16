using System;
using System.IO;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.State;
using Xunit;

public class SurfaceRegistryTests
{
    private sealed class FakeAction : ISyncedAction
    {
        public int Value { get; }
        public FakeAction(int v) => Value = v;
        public ushort ActionId => 1;
        public ActionCategory Category => ActionCategory.Research;
        public void Write(BinaryWriter w) => w.Write(Value);
        public bool Validate(GeoRuntime rt, Guid actor) => true;
        public void Apply(GeoRuntime rt) { }
    }

    [Fact]
    public void RegisterAction_Lookup_ReturnsEntryWithReaderAndScreen()
    {
        var reg = new SurfaceRegistry();
        reg.RegisterAction(SurfaceIds.StartResearch, r => new FakeAction(r.ReadInt32()),
            GeoUiRefresh.Screen.Research);

        Assert.True(reg.IsRegistered(SurfaceIds.StartResearch));
        var e = reg.Get(SurfaceIds.StartResearch);
        Assert.NotNull(e);
        Assert.True(e.Accepts(SyncKind.ActionRequest));
        Assert.True(e.Accepts(SyncKind.ActionApply));
        Assert.False(e.Accepts(SyncKind.StateSnapshot));
        Assert.Equal(GeoUiRefresh.Screen.Research, e.Screen);
        Assert.NotNull(e.Reader);
    }

    [Fact]
    public void UnknownSurface_GetReturnsNull_IsRegisteredFalse()
    {
        var reg = new SurfaceRegistry();
        Assert.Null(reg.Get(200));
        Assert.False(reg.IsRegistered(200));
    }

    [Fact]
    public void RegisteredAction_ReaderReconstructsFromPayload()
    {
        var reg = new SurfaceRegistry();
        reg.RegisterAction(SurfaceIds.StartResearch, r => new FakeAction(r.ReadInt32()), null);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) w.Write(77);
        ms.Position = 0;
        using var rdr = new BinaryReader(ms);
        var a = reg.Get(SurfaceIds.StartResearch).Reader(rdr);
        Assert.Equal(77, ((FakeAction)a).Value);
    }

    // Integration constraint (brief): action ids and channel ids share a numbering byte
    // (StartResearch=1 vs InventoryChannel=1). They MUST NOT clobber each other — the registry
    // resolves by (kind-class, surfaceId), so an action surface and a state-channel surface that
    // share a numeric id BOTH resolve correctly. (Channel instance is null here: the live
    // IStateChannel fake would have to reference SyncEngine, which is not linked into the test
    // assembly; this test asserts the STRUCTURAL no-collision guarantee, which is what matters.)
    [Fact]
    public void ActionAndChannel_SameNumericId_DoNotCollide()
    {
        Assert.Equal(SurfaceIds.StartResearch, SurfaceIds.InventoryChannel); // shared byte = 1, by design
        var reg = new SurfaceRegistry();
        reg.RegisterAction(SurfaceIds.StartResearch, r => new FakeAction(r.ReadInt32()),
            GeoUiRefresh.Screen.Research);
        reg.RegisterChannel(SurfaceIds.InventoryChannel, channel: null,
            screen: GeoUiRefresh.Screen.Manufacturing);

        // Kind-aware resolution keeps them apart on the same byte id.
        var action = reg.Get(1, SyncKind.ActionRequest);
        var channel = reg.Get(1, SyncKind.StateSnapshot);
        Assert.NotNull(action);
        Assert.NotNull(channel);
        Assert.NotSame(action, channel);

        // Action surface: reader present, accepts only action kinds, refreshes Research.
        Assert.NotNull(action.Reader);
        Assert.True(action.Accepts(SyncKind.ActionRequest));
        Assert.False(action.Accepts(SyncKind.StateSnapshot));
        Assert.Equal(GeoUiRefresh.Screen.Research, action.Screen);

        // Channel surface: no action reader, accepts only state kinds, refreshes Manufacturing.
        Assert.Null(channel.Reader);
        Assert.True(channel.Accepts(SyncKind.StateSnapshot));
        Assert.True(channel.Accepts(SyncKind.StateDelta));
        Assert.False(channel.Accepts(SyncKind.ActionRequest));
        Assert.Equal(GeoUiRefresh.Screen.Manufacturing, channel.Screen);

        // Bare single-arg Get resolves the ACTION surface (the only kind routed in Phase 1).
        Assert.Same(action, reg.Get(1));
        Assert.True(reg.IsRegistered(1)); // either-space registration counts as registered
    }
}
