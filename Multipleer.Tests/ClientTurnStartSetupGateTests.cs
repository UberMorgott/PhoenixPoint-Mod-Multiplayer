using Multipleer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the CLIENT mirror's turn-start SETUP-replay gate (Inc1 over-suppression fix). On a
// frozen client mirror the Inc1 PlayTurnCrt replacement skipped the native turn-start setup (:392-431), so the
// client's own soldiers were never StartTurn'd → not selectable → UIStateInitial never reached
// UIStateCharacterSelected → no action HUD + camera stuck mid-map. The mirror now REPLAYS the client-safe setup
// subset (vision / viewer-faction+camera / per-actor StartTurn) BEFORE the input loop — but ONLY for the
// client's own PLAYER faction; an enemy/AI faction stays a pure spectator (no setup, no loop). This gate pins
// that contract so it can't silently regress back to "skip all setup".
public class ClientTurnStartSetupGateTests
{
    [Fact]
    public void PlayerFaction_ReplaysTurnStartSetup()
    {
        // The client's own player faction must replay the setup so its soldiers become selectable + camera binds.
        Assert.True(ClientTurnStartSetupGate.ShouldReplayTurnStartSetup(isControlledByPlayer: true));
    }

    [Fact]
    public void EnemyFaction_DoesNotReplaySetup()
    {
        // An enemy/AI faction is a pure spectator on the client (presentation driven by ClientOnTurn; AI stays
        // suppressed) → no turn-start setup, no input loop.
        Assert.False(ClientTurnStartSetupGate.ShouldReplayTurnStartSetup(isControlledByPlayer: false));
    }
}
