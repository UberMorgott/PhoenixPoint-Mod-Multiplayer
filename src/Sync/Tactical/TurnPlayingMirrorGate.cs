namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind the CLIENT mirror's <c>TacticalLevelController._nextTurnUpdateable</c>
    /// presentation marker. Extracted from <see cref="TacticalTurnSync"/> so the "when must TurnIsPlaying be
    /// true on the client" contract is unit-testable without TacticalLevelController / Harmony / Timing.
    ///
    /// WHY (grounded, decompile file:line): the native tactical view dispatcher
    /// <c>UIStateInitial.InitialStateUpdateCrt</c> (UIStateInitial.cs:34-95) gates ALL turn presentation behind
    /// <c>IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate()</c> (UIStateInitial.cs:49 →
    /// TacticalView.cs:972-979), which returns true (→ <c>UIStateWaiting</c>, no HUD/control, camera stuck)
    /// whenever <c>!LevelController.TurnIsPlaying</c> (TacticalView.cs:965). And
    /// <c>TacticalLevelController.TurnIsPlaying => _nextTurnUpdateable != null</c> (TLC.cs:251). Native sets
    /// <c>_nextTurnUpdateable = Timing.Start(NextTurnCrt())</c> exactly ONCE per mission, in
    /// <c>OnLevelStart</c> (TLC.cs:680), and that single long-lived updateable drives EVERY faction turn for the
    /// whole mission (cleared only at teardown, TLC.cs:769). So natively <c>TurnIsPlaying</c> is true throughout
    /// combat — for player turns AND enemy turns alike; it is NOT a per-faction flag.
    ///
    /// On the client the autonomous turn engine <c>NextTurnCrt</c> is (correctly) suppressed
    /// (<see cref="Multipleer.Harmony.Tactical.NextTurnCrtSuppressPatch"/>), so the single native updateable that
    /// would keep <c>TurnIsPlaying</c> true is never a live/meaningful one and the field can read null → the
    /// gate at UIStateInitial.cs:49 dead-spins in <c>UIStateWaiting</c> and the client view never grants control
    /// (nor enemy-turn presentation). The fix mirrors native LOCAL presentation state: while the client mirror
    /// is inside a tactical turn, assign a LIVE <c>IUpdateable</c> (the handle of the mirror's own player-turn
    /// coroutine, see <c>TacticalTurnSync.StartPlayTurn</c>) to <c>_nextTurnUpdateable</c> so
    /// <c>TurnIsPlaying</c> is true and the dispatcher advances.
    ///
    /// IMPORTANT — this is FACTION-AGNOSTIC (grounded UIStateInitial.cs:49 runs BEFORE the player branch :66 AND
    /// the enemy branch :58). The player-vs-enemy ROUTING is decided later, at UIStateInitial.cs:58
    /// (<c>!CurrentFaction.IsControlledByPlayer</c> → <c>UIStateOtherFactionTurn</c>) vs :66 (player →
    /// <c>UIStateCharacterSelected</c>) — NOT by <c>_nextTurnUpdateable</c>. So we must KEEP the marker non-null
    /// across the WHOLE client mission (player and enemy turns); clearing it on an enemy handoff would NOT route
    /// to the spectator view — it would drop BOTH branches into <c>UIStateWaiting</c> and break the existing
    /// enemy-turn presentation (<see cref="ClientEnemyTurnPresentationGate"/>). It is cleared only when the
    /// mission ends (native parity, TLC.cs:769).
    /// </summary>
    public static class TurnPlayingMirrorGate
    {
        /// <summary>
        /// True when the client mirror must mark <c>TurnIsPlaying</c> true (set <c>_nextTurnUpdateable</c> to a
        /// live updateable) so the native view dispatcher can leave <c>UIStateWaiting</c>. Exactly: this instance
        /// is a mirroring CLIENT and a tactical turn is active (we are entering / inside a faction turn). Faction
        /// type is irrelevant — the marker is mission-scoped, not per-faction (see class remarks).
        /// </summary>
        public static bool ShouldMarkTurnPlaying(bool isClientMirroring, bool turnActive)
            => isClientMirroring && turnActive;

        /// <summary>
        /// True when the client mirror must CLEAR <c>_nextTurnUpdateable</c> (set null → <c>TurnIsPlaying</c>
        /// false). Mirrors native, which only nulls it at mission teardown (TLC.cs:769): exactly when this
        /// instance is a mirroring CLIENT and the tactical mission is ending. We NEVER clear it mid-mission on a
        /// faction handoff — that would land BOTH the player and enemy view branches in <c>UIStateWaiting</c>
        /// (the gate at UIStateInitial.cs:49 is faction-agnostic; routing is decided at :58 vs :66).
        /// </summary>
        public static bool ShouldClearTurnPlaying(bool isClientMirroring, bool missionEnding)
            => isClientMirroring && missionEnding;
    }
}
