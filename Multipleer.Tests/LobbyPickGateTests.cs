using Multipleer.Network;
using Xunit;

/// <summary>
/// Lobby save-pick gate. Fixes: a host in the INITIAL co-op lobby (session NOT started) picks a save
/// from a native Load screen and the game immediately LOADS it instead of recording it as the lobby's
/// chosen save and waiting for all clients READY + host PLAY. The old intercept gated only on a fragile
/// static <c>_armed</c> flag that could be FALSE at click time (the lobby re-makes native menu buttons
/// click-raycastable, so the host can reach the native Load screen un-armed). The durable fix re-gates
/// on real session state via this pure predicate: a host whose co-op session is active but NOT yet
/// started must NEVER trigger a native load — the pick is captured as the lobby's chosen save instead.
///
/// The mid-session F2 host-load path (sessionStarted == true) is intentionally EXCLUDED — that load is
/// host-authoritative and immediate (it re-runs the chunked transfer; see <c>SessionLifecycle.HostLoadGuard</c>).
/// Non-host peers and single-player (no active co-op session) are excluded so vanilla load is untouched.
/// </summary>
public class LobbyPickGateTests
{
    [Fact]
    public void Host_LobbyActive_NotStarted_True()
        => Assert.True(SessionLifecycle.ShouldCaptureAsLobbyPick(isHost: true, lobbyActive: true, sessionStarted: false));

    [Fact]
    public void Host_LobbyActive_Started_MidSession_False()
        // Mid-session F2 host load: the durable lobby gate must NOT swallow it (HostLoadGuard owns it).
        => Assert.False(SessionLifecycle.ShouldCaptureAsLobbyPick(isHost: true, lobbyActive: true, sessionStarted: true));

    [Fact]
    public void NotHost_False()
        // A client never opens the lobby save picker; even if it reached a Load screen it must load normally.
        => Assert.False(SessionLifecycle.ShouldCaptureAsLobbyPick(isHost: false, lobbyActive: true, sessionStarted: false));

    [Fact]
    public void NoLobby_SinglePlayer_False()
        // No active co-op session → ordinary single-player load, never captured.
        => Assert.False(SessionLifecycle.ShouldCaptureAsLobbyPick(isHost: true, lobbyActive: false, sessionStarted: false));

    [Fact]
    public void NotHost_NoLobby_False()
        => Assert.False(SessionLifecycle.ShouldCaptureAsLobbyPick(isHost: false, lobbyActive: false, sessionStarted: false));
}
