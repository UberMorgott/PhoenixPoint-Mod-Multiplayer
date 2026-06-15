using System;
using System.IO;
using Multipleer.Network.Sync;
using Xunit;

public class SyncedActionRegistryTests
{
    [Fact]
    public void RegisterAndRead_RoundTrips()
    {
        SyncedActionRegistry.Register(9999, r => new FakeAction(r.ReadInt32()));
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) w.Write(42);
        ms.Position = 0;
        using var rdr = new BinaryReader(ms);
        var a = SyncedActionRegistry.Read(9999, rdr);
        Assert.IsType<FakeAction>(a);
        Assert.Equal(42, ((FakeAction)a).Value);
    }

    [Fact]
    public void UnknownId_ReturnsNull() => Assert.Null(SyncedActionRegistry.Read(54321, null));

    /// <summary>Minimal test-only ISyncedAction stub: no-op Validate/Apply.</summary>
    private sealed class FakeAction : ISyncedAction
    {
        public int Value { get; }
        public FakeAction(int value) => Value = value;
        public ushort ActionId => 9999;
        public ActionCategory Category => ActionCategory.Research;
        public void Write(BinaryWriter w) => w.Write(Value);
        public bool Validate(GeoRuntime rt, Guid actor) => true;
        public void Apply(GeoRuntime rt) { }
    }
}
