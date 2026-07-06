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

        /// <summary>TS7 (host side): should the host broadcast a <c>tac.camerahint</c> (0x97) for an ability the
        /// acting actor just camera-tracked? TRUE only when the native camera WOULD move (<paramref name="trackWithCamera"/>),
        /// the actor is an ENEMY (NOT a player-controlled-faction actor — friendly reaction fire already rides the
        /// fire/melee-start chase), and the actor is VISIBLE to the player faction (revealed/located) so following it
        /// reveals nothing new — "no fog reveals". All three must hold. Unity-free → unit-tested.</summary>
        public static bool ShouldBroadcastEnemyCameraHint(bool trackWithCamera, bool actorIsPlayerFaction, bool actorVisibleToPlayer)
            => trackWithCamera && !actorIsPlayerFaction && actorVisibleToPlayer;
    }
}
