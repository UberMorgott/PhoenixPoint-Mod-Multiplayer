using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Xunit;

// Geoscape cutscene mirror (host → client): pure wire round-trip for the PlayCutsceneAction payload
// (Write → bytes → Read via the registry). Apply binds live game types (CutsceneReflection) and is in-game verified.
public class PlayCutsceneActionTests
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
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.PlayCutscene));
    }

    [Fact]
    public void PlayCutscene_HasDistinctStableId()
        => Assert.Equal((ushort)50, SyncedActionIds.PlayCutscene);   // 50 = presentation range base

    [Fact]
    public void RoundTrip_PreservesGuidAndPriority()
    {
        SyncRegistration.RegisterAll();
        var original = new PlayCutsceneAction("some-cutscene-guid", 100);

        var rt = RoundTrip(original);
        Assert.IsType<PlayCutsceneAction>(rt);
        var cs = (PlayCutsceneAction)rt;

        Assert.Equal(SyncedActionIds.PlayCutscene, cs.ActionId);
        Assert.Equal(ActionCategory.Dialogs, cs.Category);
        Assert.Equal("some-cutscene-guid", cs.CutsceneGuid);
        Assert.Equal(100, cs.Priority);
        Assert.Equal(Write(original), Write(cs));   // byte-identical re-serialize
    }

    [Fact]
    public void RoundTrip_PreservesDefaultPriority()
    {
        SyncRegistration.RegisterAll();
        var original = new PlayCutsceneAction("g2", 0);
        var cs = (PlayCutsceneAction)RoundTrip(original);
        Assert.Equal("g2", cs.CutsceneGuid);
        Assert.Equal(0, cs.Priority);
    }

    [Fact]
    public void Category_IsDialogs()
        => Assert.Equal(ActionCategory.Dialogs, new PlayCutsceneAction("g", 0).Category);
}
