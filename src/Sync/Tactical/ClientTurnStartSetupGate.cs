namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind the CLIENT mirror's turn-start SETUP replay (Inc1 over-suppression fix).
    /// Extracted from <see cref="Multipleer.Harmony.Tactical.PlayTurnCrtMirrorFreezePatch"/> so the
    /// "which factions replay the turn-start setup" decision is unit-testable without TacticalFaction / Harmony.
    ///
    /// WHY (grounded, decompile file:line): Inc1 replaced the native <c>TacticalFaction.PlayTurnCrt</c>
    /// (TacticalFaction.cs:389) on the client mirror with a gutted loop that set <c>IsPlayingTurn</c> and ran
    /// the input loop but SKIPPED the native turn-START setup (:392-431). The skipped setup is what makes the
    /// client's OWN soldiers selectable + binds the camera + builds vision:
    ///   • <c>Vision.OnFactionStartTurn()</c> (:396),
    ///   • <c>View.SetViewerTacticalFaction(this)</c> (:397-400, player &amp; State==Playing),
    ///   • the per-actor <c>TacticalActor.StartTurn</c> loop (:424-427, RestartAbilities / AP reset → selectable).
    /// Without per-actor StartTurn no actor is selectable, so the native view dispatcher
    /// <c>UIStateInitial.InitialStateUpdateCrt</c> (UIStateInitial.cs:66-87) never advances to
    /// <c>UIStateCharacterSelected</c> (:80) → no action HUD and the camera never snaps (:81 SnapWorldCursor) →
    /// the client sits HUD-less with the camera mid-map. The fix: the mirror REPLAYS that client-safe setup
    /// subset for its own player faction BEFORE the input loop, while STILL skipping the host-authoritative
    /// stall yields (:432-441) and turn-END teardown (:486-509).
    ///
    /// The decision itself is trivial — only a PLAYER-controlled faction gets the live player turn (and thus
    /// the setup replay) on the client; an enemy/AI faction stays a pure spectator (its presentation is driven
    /// by <see cref="TacticalTurnSync.ClientOnTurn"/>, and its AI stays suppressed). Extracted so the contract
    /// is pinned by a test and can't silently regress back to "skip all setup".
    /// </summary>
    public static class ClientTurnStartSetupGate
    {
        /// <summary>
        /// True when the client mirror should REPLAY the client-safe turn-start setup (vision / viewer-faction +
        /// camera bind / per-actor StartTurn) for this faction before running its input loop: exactly when the
        /// faction is player-controlled. An enemy/AI faction returns false (pure spectator — no setup, no loop).
        /// </summary>
        public static bool ShouldReplayTurnStartSetup(bool isControlledByPlayer) => isControlledByPlayer;
    }
}
