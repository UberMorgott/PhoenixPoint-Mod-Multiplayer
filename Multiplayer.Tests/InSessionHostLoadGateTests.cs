using Multiplayer.Network;
using Xunit;

/// <summary>
/// In-session host-load gate. Closes the CONTINUE / Quickload intercept hole: those paths bypass the
/// UI <c>OnLoadGamePressed</c> intercept and converge at <c>PhoenixSaveManager.LoadGame</c>. A host who
/// presses main-menu CONTINUE or Quickload (menu OR in-geoscape) while a co-op session is ALREADY
/// STARTED would otherwise do a REAL solo load of a different save while clients keep running → silent
/// desync. This pure predicate classifies that mid-session case: a host, in an active session, that has
/// already started, must NEVER be allowed to solo-load — the LoadGame prefix reroutes it into the
/// host-authoritative in-session reload (the F2 / <c>HostStartSessionInGame</c> path), or at minimum
/// blocks it.
///
/// Boundaries (each a separate case so a regression localizes):
/// - host + active + started  → TRUE  (the new mid-session interception this gate exists for).
/// - host + active + !started → FALSE (that is the LOBBY case, owned by <c>ShouldCaptureAsLobbyPick</c> —
///   this predicate must NOT also claim it, or the two would double-handle the lobby pick).
/// - non-host                 → FALSE (a client never solo-loads the campaign; vanilla load is fine).
/// - no active session        → FALSE (ordinary single-player CONTINUE/Quickload, untouched).
///
/// Pre-fix these FAIL by not compiling: <c>SessionLifecycle.ShouldInterceptInSessionHostLoad</c> did not
/// exist, so the suite would not build until the predicate is added — the canonical failing test.
/// </summary>
public class InSessionHostLoadGateTests
{
    [Fact]
    public void Host_Active_Started_True()
        // Mid-session host CONTINUE/Quickload: must be intercepted, never a silent solo load.
        => Assert.True(SessionLifecycle.ShouldInterceptInSessionHostLoad(isHost: true, isActiveSession: true, sessionStarted: true));

    [Fact]
    public void Host_Active_NotStarted_LobbyCase_False()
        // Lobby pick is owned by ShouldCaptureAsLobbyPick; this in-session gate must exclude it.
        => Assert.False(SessionLifecycle.ShouldInterceptInSessionHostLoad(isHost: true, isActiveSession: true, sessionStarted: false));

    [Fact]
    public void NotHost_Started_False()
        // A client reaching LoadGame must load normally — never rerouted as a host in-session reload.
        => Assert.False(SessionLifecycle.ShouldInterceptInSessionHostLoad(isHost: false, isActiveSession: true, sessionStarted: true));

    [Fact]
    public void NoActiveSession_SinglePlayer_False()
        // No co-op session at all → vanilla single-player CONTINUE/Quickload, untouched.
        => Assert.False(SessionLifecycle.ShouldInterceptInSessionHostLoad(isHost: true, isActiveSession: false, sessionStarted: false));

    [Fact]
    public void NotHost_NoSession_False()
        => Assert.False(SessionLifecycle.ShouldInterceptInSessionHostLoad(isHost: false, isActiveSession: false, sessionStarted: false));

    // ── Mutual-exclusion guard: the lobby gate and the in-session gate must partition the host cases ──
    // For a host in an active session, EXACTLY ONE of the two gates fires depending on sessionStarted.
    // This pins the contract so a future edit to either predicate can't make them overlap (double-handle)
    // or both miss (a host LoadGame that solo-loads through the hole again).
    [Theory]
    [InlineData(false)] // lobby  → ShouldCaptureAsLobbyPick true, ShouldInterceptInSessionHostLoad false
    [InlineData(true)]  // started→ ShouldCaptureAsLobbyPick false, ShouldInterceptInSessionHostLoad true
    public void HostActive_ExactlyOneGateFires(bool sessionStarted)
    {
        bool lobby = SessionLifecycle.ShouldCaptureAsLobbyPick(isHost: true, lobbyActive: true, sessionStarted: sessionStarted);
        bool inSession = SessionLifecycle.ShouldInterceptInSessionHostLoad(isHost: true, isActiveSession: true, sessionStarted: sessionStarted);
        Assert.True(lobby ^ inSession); // exactly one true (XOR)
    }
}
