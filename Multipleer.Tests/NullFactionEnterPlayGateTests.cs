using Multipleer.Harmony.Tactical;
using Xunit;

// Pure gate behind NullFactionEnterPlayPatch (deploy null-faction guard). The live Harmony prefix binds
// game types and is in-game verified, but the decision ("should the native OnActorEnteredPlay run, or be
// suppressed to avoid the null-faction ArgumentException that aborts host deploy-capture?") is extracted
// into a pure static method so all four cases are asserted directly.
//   true  => let the native vision handler run (no session, or the actor has a real faction).
//   false => suppress (active synced session AND the entering actor's faction is null → native would throw).
public class NullFactionEnterPlayGateTests
{
    [Fact]
    public void NoSession_RunsNative()
    {
        // Single-player / no active session → native OnActorEnteredPlay runs untouched, even for a null faction.
        Assert.True(NullFactionEnterPlayGate.ShouldRunNativeEnterPlay(inActiveSession: false, factionIsNull: true));
        Assert.True(NullFactionEnterPlayGate.ShouldRunNativeEnterPlay(inActiveSession: false, factionIsNull: false));
    }

    [Fact]
    public void ActiveSession_RealFaction_RunsNative()
    {
        // Active synced session but the actor has a real faction (every soldier) → native runs (no throw),
        // so the verified soldier-load is never touched.
        Assert.True(NullFactionEnterPlayGate.ShouldRunNativeEnterPlay(inActiveSession: true, factionIsNull: false));
    }

    [Fact]
    public void ActiveSession_NullFaction_Suppresses()
    {
        // Active synced session AND a faction-less entering actor (the deploy intruder) → suppress the native
        // handler so its ArgumentException can't abort the host's HostOnLevelReady deploy capture.
        Assert.False(NullFactionEnterPlayGate.ShouldRunNativeEnterPlay(inActiveSession: true, factionIsNull: true));
    }
}
