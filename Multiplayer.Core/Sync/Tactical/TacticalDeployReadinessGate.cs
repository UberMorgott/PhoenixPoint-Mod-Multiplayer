namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Pure, Unity-free decision for WHEN the host may capture the tactical deploy snapshot.
    ///
    /// Root cause this guards (RCA 2026-06-18, 2-instance log decode): the deploy trigger postfixes
    /// <c>TacticalLevelController.OnLevelStateChanged(Playing)</c>, which runs SYNCHRONOUSLY at the
    /// Playing transition. But the native level finishes initializing only in the
    /// <c>OnLevelStart(level)</c> coroutine that <c>OnLevelStateChanged</c> schedules via
    /// <c>level.WaitFor(...)</c> (TLC.cs:433) — that coroutine has NOT run yet when our postfix fires.
    /// Capturing then calls <c>TacticalLevelController.RecordInstanceData()</c> while the level is
    /// half-built, throwing a layered NRE: first inside <c>TacticalFactionVision.RecordInstanceData</c>
    /// (null-faction deploy actors not yet entered-play), then — once that is scrubbed — inside
    /// <c>TacAchievementTracker.RecordInstanceData()</c> on <c>_level</c> (set only later, at
    /// TLC.cs:660 → PhoenixStatisticsManager.OnTacticalLevelStart → tracker.OnLevelStart). The throw
    /// aborts <c>HostOnLevelReady</c>, so <c>tac.deploy</c> is never broadcast and every client move
    /// stays unsynced (mirror never arms).
    ///
    /// The clean fix is to DEFER the capture until the level is genuinely turn-0 ready instead of
    /// peeling NRE layers field-by-field. <c>TacticalLevelController.HasAnyTurnStarted</c> flips true at
    /// TLC.cs:719 inside the turn-start action, which runs only AFTER <c>OnLevelStart</c> fully completed
    /// (it starts <c>NextTurnCrt</c> as its last step, TLC.cs:680) AND actors entered play with factions.
    /// So <c>HasAnyTurnStarted==true</c> is a superset-safe readiness gate: by then <c>_level</c> is set
    /// and FactionVision has no null-faction actors, and <c>RecordInstanceData</c> succeeds cleanly.
    ///
    /// The engine glue (a coroutine pumped on the level's Timing) calls <see cref="Decide"/> each frame
    /// and acts on the returned decision; a bounded frame budget gives a fail-safe capture so a
    /// pathological mission that never flips the flag still attempts a (best-effort) deploy rather than
    /// hanging forever.
    /// </summary>
    public static class TacticalDeployReadinessGate
    {
        public enum Decision
        {
            /// <summary>Level not ready yet and the frame budget is not exhausted → keep waiting.</summary>
            Wait,
            /// <summary>Level is fully turn-0 ready → capture now (the clean, expected path).</summary>
            CaptureReady,
            /// <summary>Readiness never observed within the frame budget → capture anyway (fail-safe).</summary>
            CaptureTimeout,
        }

        /// <summary>
        /// Decide whether the host should capture the deploy snapshot this frame.
        /// </summary>
        /// <param name="hasAnyTurnStarted">TacticalLevelController.HasAnyTurnStarted (turn 0 entered).</param>
        /// <param name="framesWaited">Frames elapsed since the Playing transition (0 on the first call).</param>
        /// <param name="maxFrames">Frame budget before the fail-safe timeout capture kicks in.</param>
        public static Decision Decide(bool hasAnyTurnStarted, int framesWaited, int maxFrames)
        {
            // Readiness always wins, even at/after the budget edge — a clean capture beats a fail-safe one.
            if (hasAnyTurnStarted) return Decision.CaptureReady;
            if (framesWaited >= maxFrames) return Decision.CaptureTimeout;
            return Decision.Wait;
        }
    }
}
