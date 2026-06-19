using Multipleer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the CLIENT enemy-turn presentation gate (Feature A). On a tac.turn handoff the
// client must enter the native UIStateOtherFactionTurn (player bar hidden + "<faction> turn" banner +
// camera follows mobs) for a NON-player faction, by setting that faction's IsPlayingTurn=true and driving
// the view into UIStateInitial (whose dispatcher then transitions to UIStateOtherFactionTurn). Because the
// client never runs PlayTurnCrt for the enemy, nothing natively clears that IsPlayingTurn — so on every
// handoff we must clear the OUTGOING faction's manually-set IsPlayingTurn, but ONLY when it was a non-player
// faction (a player faction clears itself via its native PlayTurnCrt _endTurnRequested loop).
public class ClientEnemyTurnPresentationGateTests
{
    // ── ShouldEnterEnemyPresentation: enter only for a non-player handoff target ──────────────────
    [Fact]
    public void EnemyFaction_EntersEnemyPresentation()
    {
        Assert.True(ClientEnemyTurnPresentationGate.ShouldEnterEnemyPresentation(incomingIsControlledByPlayer: false));
    }

    [Fact]
    public void PlayerFaction_DoesNotEnterEnemyPresentation()
    {
        Assert.False(ClientEnemyTurnPresentationGate.ShouldEnterEnemyPresentation(incomingIsControlledByPlayer: true));
    }

    // ── ShouldClearOutgoingIsPlayingTurn: clear only a non-player faction we had marked playing ────
    [Fact]
    public void OutgoingEnemy_StillMarkedPlaying_IsCleared()
    {
        // The enemy faction we manually marked IsPlayingTurn=true has no native loop to clear it → we must.
        Assert.True(ClientEnemyTurnPresentationGate.ShouldClearOutgoingIsPlayingTurn(
            outgoingIsControlledByPlayer: false, outgoingIsPlayingTurn: true));
    }

    [Fact]
    public void OutgoingPlayer_StillMarkedPlaying_IsLeftAlone()
    {
        // A player faction clears its own IsPlayingTurn natively (PlayTurnCrt exit) → do NOT touch it.
        Assert.False(ClientEnemyTurnPresentationGate.ShouldClearOutgoingIsPlayingTurn(
            outgoingIsControlledByPlayer: true, outgoingIsPlayingTurn: true));
    }

    [Fact]
    public void OutgoingEnemy_NotPlaying_NoClearNeeded()
    {
        Assert.False(ClientEnemyTurnPresentationGate.ShouldClearOutgoingIsPlayingTurn(
            outgoingIsControlledByPlayer: false, outgoingIsPlayingTurn: false));
    }

    [Fact]
    public void OutgoingPlayer_NotPlaying_NoClearNeeded()
    {
        Assert.False(ClientEnemyTurnPresentationGate.ShouldClearOutgoingIsPlayingTurn(
            outgoingIsControlledByPlayer: true, outgoingIsPlayingTurn: false));
    }
}
