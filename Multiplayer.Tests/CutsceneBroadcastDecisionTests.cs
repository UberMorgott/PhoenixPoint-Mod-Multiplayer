using Multiplayer.Network.Sync;
using Xunit;

// Geoscape cutscene mirror broadcast gate (CutsceneMirrorPatch → CutsceneBroadcastDecision). Locks the review
// fix: a HOST cutscene fired synchronously INSIDE a client-relayed action apply (SyncApplyScope active — e.g.
// relayed explore of an ExplorationTime==0 story site → SiteExplored inline → reward → ToCutsceneState) MUST
// broadcast; the IsApplying suppression exists only for the client's own mirror-driven replay.
public class CutsceneBroadcastDecisionTests
{
    [Fact]
    public void Host_InsideRelayedApply_StillBroadcasts()
        // THE regression: relayed explore applies under SyncApplyScope.Enter (SyncEngine.cs:208); the inline
        // cutscene must not be swallowed — the host never applies PlayCutsceneAction, so IsApplying is never
        // a host re-broadcast echo.
        => Assert.True(CutsceneBroadcastDecision.ShouldBroadcast(isHost: true, isActiveSession: true, isApplying: true));

    [Fact]
    public void Host_NativeLocalCutscene_Broadcasts()
        => Assert.True(CutsceneBroadcastDecision.ShouldBroadcast(isHost: true, isActiveSession: true, isApplying: false));

    [Fact]
    public void Client_MirrorDrivenReplay_NeverRebroadcasts()
        => Assert.False(CutsceneBroadcastDecision.ShouldBroadcast(isHost: false, isActiveSession: true, isApplying: true));

    [Fact]
    public void Client_NeverBroadcasts_EvenOutsideApply()
        => Assert.False(CutsceneBroadcastDecision.ShouldBroadcast(isHost: false, isActiveSession: true, isApplying: false));

    [Fact]
    public void Host_NoActiveSession_DoesNotBroadcast()
        => Assert.False(CutsceneBroadcastDecision.ShouldBroadcast(isHost: true, isActiveSession: false, isApplying: false));

    [Fact]
    public void NoEngine_DoesNotBroadcast()
        // Patch maps a null NetworkEngine to isHost:false / isActiveSession:false.
        => Assert.False(CutsceneBroadcastDecision.ShouldBroadcast(isHost: false, isActiveSession: false, isApplying: false));
}
