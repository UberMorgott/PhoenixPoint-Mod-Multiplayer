namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind the CLIENT enemy-turn presentation (Feature A). Extracted from
    /// <see cref="TacticalTurnSync.ClientOnTurn"/> so the view-gate logic is unit-testable without
    /// NetworkEngine, TacticalFaction, or TacticalView.
    ///
    /// WHY (grounded, decompile file:line): the native tactical view machine self-drives the enemy-turn
    /// presentation. <c>UIStateInitial.InitialStateUpdateCrt</c>
    /// (PhoenixPoint.Tactical.View.ViewStates\UIStateInitial.cs:58-65) does, when the current faction is
    /// NOT player-controlled:
    ///     while (!CurrentFaction.IsPlayingTurn) yield return NextUpdate.NextFrame;
    ///     SwitchToState(new UIStateOtherFactionTurn(), ClearStackAndPush);
    /// <c>UIStateOtherFactionTurn.EnterState</c> (Tactical.View.ViewStates\UIStateOtherFactionTurn.cs:13-20)
    /// then hides the player action bar (MainUILayer "OtherPlayerTurn") + shows the "&lt;faction&gt; turn"
    /// banner (SetNextFactionTurn) + sets overwatch visuals; <c>UpdateState</c> (:22-28) auto-returns to
    /// <c>UIStateInitial(initForNewTurn:true)</c> once <c>CurrentFaction.IsControlledByPlayer</c>.
    ///
    /// On the client the host is authoritative: the autonomous turn engine (NextTurnCrt) + AI (AIUpdateCrt)
    /// are suppressed, so <c>TacticalFaction.PlayTurnCrt</c> never runs for the enemy and its
    /// <c>IsPlayingTurn</c> (TacticalFaction.cs:79, set true at :442, false at :486) is NEVER set. Thus the
    /// native dispatcher dead-spins at the while-loop above and no enemy-turn UI/camera appears. The fix is
    /// purely a VIEW gate: when the handoff target is a non-player faction, set its <c>IsPlayingTurn = true</c>
    /// (reflection) AND drive the view into <c>UIStateInitial</c> — the native loop then breaks and enters
    /// <c>UIStateOtherFactionTurn</c> on its own. The viewer faction stays the client's own player faction
    /// (fog/vision correctness); the camera follows acting mobs for free because their local
    /// <c>TacticalAbility.Activate</c> pushes <c>CameraDirector.Hint(AbilityActivated)</c>.
    ///
    /// CLEANUP: because we set <c>IsPlayingTurn = true</c> by hand and never start <c>PlayTurnCrt</c>, nothing
    /// natively clears it (the native exit at :486 only runs inside PlayTurnCrt). So on every handoff we must
    /// clear the OUTGOING faction's <c>IsPlayingTurn</c> when it was a non-player faction we had marked playing.
    /// (The outgoing PLAYER faction is cleared the native way — its real PlayTurnCrt loop exits via
    /// <c>_endTurnRequested</c> — so we must NOT touch it here.)
    /// </summary>
    public static class ClientEnemyTurnPresentationGate
    {
        /// <summary>
        /// True when the client should enter the native enemy-turn presentation
        /// (<c>UIStateOtherFactionTurn</c>) for the handoff target faction: exactly when the target is NOT
        /// player-controlled. A player faction takes the normal player-turn path (action HUD) instead.
        /// </summary>
        public static bool ShouldEnterEnemyPresentation(bool incomingIsControlledByPlayer)
            => !incomingIsControlledByPlayer;

        /// <summary>
        /// True when, on a turn handoff, the OUTGOING faction's manually-set <c>IsPlayingTurn</c> must be
        /// cleared by us. Only the case where the outgoing faction was a non-player faction we had marked
        /// playing for the enemy-turn presentation — a player faction clears itself natively via its
        /// PlayTurnCrt loop, so we leave it alone (clearing it here would be redundant or wrong).
        /// </summary>
        public static bool ShouldClearOutgoingIsPlayingTurn(
            bool outgoingIsControlledByPlayer, bool outgoingIsPlayingTurn)
            => outgoingIsPlayingTurn && !outgoingIsControlledByPlayer;
    }
}
