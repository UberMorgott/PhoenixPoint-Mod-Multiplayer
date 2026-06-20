using Multipleer.Network;
using Xunit;

/// <summary>
/// Client-load block gate. Closes the NON-HOST half of the LoadGame convergence hole.
///
/// The in-session host gates (<c>ShouldInterceptInSessionHostLoad</c> / <c>HostInSessionHasNoClients</c>)
/// only ever reason about a HOST reaching <c>PhoenixSaveManager.LoadGame</c>. But a CLIENT in an active
/// co-op session who triggers a load (main-menu CONTINUE, pause-menu LOAD, or in-game Quickload F-key —
/// all converging at <c>LoadGame</c>) would fall straight through the host-only branches to the vanilla
/// solo load → it loads a save locally while still wired into the live co-op session → desync. In a
/// host-authoritative model ONLY the host may load; a client load attempt must be blocked.
///
/// <c>ShouldBlockClientLoad</c> is the pure predicate for exactly that case: an ACTIVE session in which
/// the local peer is NOT the host. The LoadGame prefix returns <c>false</c> (block the solo load) and
/// surfaces a "only the host can load" notice when it fires.
///
/// Boundaries (each a separate case so a regression localizes):
/// - client + active session → TRUE  (block: a non-host must never solo-load mid co-op).
/// - host   + active session → FALSE (host paths own the load; this gate must NOT claim them).
/// - client + NO session     → FALSE (ordinary single-player CONTINUE/Quickload, untouched).
/// - host   + NO session     → FALSE (ordinary single-player, untouched).
///
/// Pre-fix these FAIL by not compiling: <c>SessionLifecycle.ShouldBlockClientLoad</c> did not exist, so
/// the suite would not build until the predicate is added — the canonical failing test.
/// </summary>
public class ClientLoadBlockTests
{
    [Fact]
    public void Client_ActiveSession_True()
        // A non-host in a live co-op session must be blocked from solo-loading → desync otherwise.
        => Assert.True(SessionLifecycle.ShouldBlockClientLoad(isHost: false, isActiveSession: true));

    [Fact]
    public void Host_ActiveSession_False()
        // The host owns the load (lobby capture / in-session reroute); this gate must exclude it.
        => Assert.False(SessionLifecycle.ShouldBlockClientLoad(isHost: true, isActiveSession: true));

    [Fact]
    public void Client_NoSession_False()
        // No co-op session → ordinary single-player load on a non-host machine, untouched.
        => Assert.False(SessionLifecycle.ShouldBlockClientLoad(isHost: false, isActiveSession: false));

    [Fact]
    public void Host_NoSession_False()
        // No co-op session → ordinary single-player load, untouched.
        => Assert.False(SessionLifecycle.ShouldBlockClientLoad(isHost: true, isActiveSession: false));

    // ── Partition pin: across the four (isHost × isActiveSession) states, the client-block fires for
    // EXACTLY the one non-host + active-session state and never overlaps the host gates. This keeps the
    // host-authoritative invariant: in an active session either the host load path runs OR the client is
    // blocked — never both, never neither.
    [Theory]
    [InlineData(false, true,  true)]   // client + active → block
    [InlineData(true,  true,  false)]  // host   + active → host owns it
    [InlineData(false, false, false)]  // client + no session → single-player
    [InlineData(true,  false, false)]  // host   + no session → single-player
    public void BlockFiresOnlyForNonHostActive(bool isHost, bool active, bool expectBlock)
    {
        Assert.Equal(expectBlock, SessionLifecycle.ShouldBlockClientLoad(isHost, active));
        // A client block and any host-in-session interception are mutually exclusive (never both true).
        bool hostInSession = SessionLifecycle.ShouldInterceptInSessionHostLoad(isHost, active, sessionStarted: true);
        Assert.False(SessionLifecycle.ShouldBlockClientLoad(isHost, active) && hostInSession);
    }
}
