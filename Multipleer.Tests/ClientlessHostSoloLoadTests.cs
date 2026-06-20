using Multipleer.Network;
using Xunit;

/// <summary>
/// Clientless-host solo-load allowance. Companion to <c>InSessionHostLoadGateTests</c>.
///
/// The in-session gate (<c>ShouldInterceptInSessionHostLoad</c>) classifies a host whose co-op session
/// has already started as "must not silently solo-load while clients keep playing". But once EVERY
/// client has left, the host is alone: a CONTINUE / Quickload has no peers to desync, yet the old
/// behaviour still BLOCKED it (and the in-game co-op LOAD reroute is itself >=1-client gated via
/// <c>HostLoadGuard</c>) — locking the clientless host out of loading entirely.
///
/// <c>HostInSessionHasNoClients</c> is the pure predicate that re-opens the vanilla solo load for
/// exactly that case: host + active session + started + ZERO connected clients. The LoadGame prefix
/// returns <c>true</c> (let vanilla load run) when it fires, and only blocks/reroutes when >=1 client
/// is actually connected.
///
/// Boundaries (each a separate case so a regression localizes):
/// - host + active + started + 0 clients   → TRUE  (no peers to desync → allow vanilla solo load).
/// - host + active + started + 1 client     → FALSE (a real peer would desync → keep intercepting).
/// - host + active + started + N clients     → FALSE.
/// - host + active + !started (lobby)        → FALSE (owned by ShouldCaptureAsLobbyPick; not a solo load).
/// - non-host                                → FALSE (a client never solo-loads the campaign).
/// - no active session                       → FALSE (ordinary single-player; not gated at all).
/// </summary>
public class ClientlessHostSoloLoadTests
{
    [Fact]
    public void Host_Active_Started_ZeroClients_AllowSoloLoad_True()
        // Host alone (all clients left): nothing to desync → vanilla CONTINUE/Quickload may proceed.
        => Assert.True(SessionLifecycle.HostInSessionHasNoClients(
            isHost: true, isActiveSession: true, sessionStarted: true, connectedClientCount: 0));

    [Fact]
    public void Host_Active_Started_OneClient_False()
        // A real peer is connected → solo load would desync it; must NOT allow vanilla load.
        => Assert.False(SessionLifecycle.HostInSessionHasNoClients(
            isHost: true, isActiveSession: true, sessionStarted: true, connectedClientCount: 1));

    [Fact]
    public void Host_Active_Started_ManyClients_False()
        => Assert.False(SessionLifecycle.HostInSessionHasNoClients(
            isHost: true, isActiveSession: true, sessionStarted: true, connectedClientCount: 3));

    [Fact]
    public void Host_Active_NotStarted_LobbyCase_ZeroClients_False()
        // Lobby (not started) is a save-pick, not a solo load; this allowance must exclude it.
        => Assert.False(SessionLifecycle.HostInSessionHasNoClients(
            isHost: true, isActiveSession: true, sessionStarted: false, connectedClientCount: 0));

    [Fact]
    public void NotHost_Started_ZeroClients_False()
        // A client never solo-loads the campaign, regardless of count.
        => Assert.False(SessionLifecycle.HostInSessionHasNoClients(
            isHost: false, isActiveSession: true, sessionStarted: true, connectedClientCount: 0));

    [Fact]
    public void NoActiveSession_ZeroClients_False()
        // No co-op session → not the gated path at all (vanilla load was never intercepted here).
        => Assert.False(SessionLifecycle.HostInSessionHasNoClients(
            isHost: true, isActiveSession: false, sessionStarted: false, connectedClientCount: 0));

    // ── Contract pin: within the in-session host gate, the clientless allowance and the HostLoadGuard
    // reroute are mutually exclusive on client count. With 0 clients we ALLOW solo load and the reroute
    // guard is false; with >=1 client we do NOT allow solo load (the guard may permit a reroute). This
    // ensures the prefix never both allows a vanilla solo load AND reroutes for the same state.
    [Theory]
    [InlineData(0, true)]   // alone → allow solo load, reroute guard off
    [InlineData(1, false)]  // peer  → no solo load
    [InlineData(2, false)]
    public void InSessionHost_AllowanceVsReroute_PartitionOnClientCount(int clients, bool expectAllow)
    {
        bool allowSolo = SessionLifecycle.HostInSessionHasNoClients(
            isHost: true, isActiveSession: true, sessionStarted: true, connectedClientCount: clients);
        bool canReroute = SessionLifecycle.HostLoadGuard(
            isHost: true, isActiveSession: true, sessionStarted: true,
            connectedClientCount: clients, transferActive: false);

        Assert.Equal(expectAllow, allowSolo);
        Assert.False(allowSolo && canReroute); // never allow solo AND reroute for one state
    }
}
