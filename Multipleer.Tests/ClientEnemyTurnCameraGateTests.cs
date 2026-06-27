using Multipleer.Sync.Tactical;
using Xunit;

public class ClientEnemyTurnCameraGateTests
{
    [Fact]
    public void EnemyTurn_ActorResolved_Chases()
        => Assert.True(ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(isClientEnemyTurn: true, actorResolved: true));

    [Fact]
    public void PlayerTurn_DoesNotChase()
        => Assert.False(ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(isClientEnemyTurn: false, actorResolved: true));

    [Fact]
    public void EnemyTurn_ActorUnresolved_DoesNotChase()
        => Assert.False(ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(isClientEnemyTurn: true, actorResolved: false));
}
