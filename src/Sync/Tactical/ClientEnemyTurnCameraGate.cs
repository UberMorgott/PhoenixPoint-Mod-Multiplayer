namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Pure policy for the client enemy-turn cinematic camera: chase an actor only when the
    /// client is presenting an enemy (non-player) turn AND the actor resolved. Unity-free so it
    /// is unit-tested; the engine-side chase lives in <see cref="TacticalEnemyTurnCamera"/>.
    /// </summary>
    public static class ClientEnemyTurnCameraGate
    {
        public static bool ShouldChaseEnemyAction(bool isClientEnemyTurn, bool actorResolved)
            => isClientEnemyTurn && actorResolved;
    }
}
