namespace Multiplayer.Network
{
    /// <summary>
    /// Pure, Unity-free single-shot latch for the P0 new-campaign co-op bootstrap. The HOST arms it at
    /// the native new-game CONFIRM chokepoint (NewCampaignInterceptPatch on
    /// <c>UIStateNewGeoscapeGameSettings.GameSettings_OnConfirm</c>) and lets the NATIVE campaign
    /// creation run to completion; the latch then drives ONE autosave+transfer attempt, started at the
    /// geoscape-READY seam (<c>ModManager.OnGeoscapeStart</c>, GeoLevelController.cs:757 — the last
    /// line of <c>LevelCrt</c>'s init block) and feeding the EXISTING chunked save transfer + 2-phase
    /// barrier (<c>SaveTransferCoordinator.LaunchTransfer</c>) — no new transfer mechanism.
    ///
    /// THE ONE SHOT IS SPENT BY AN OUTCOME, NEVER BY AN EVALUATION. The previous latch disarmed inside
    /// <see cref="TryFire"/>, BEFORE the guard result and long before the attempt concluded: the first
    /// autosave threw (a fresh campaign is not save-able at the <c>Playing</c> edge — <c>LevelCrt</c>
    /// has not run a line, GeoLevelController.cs:377-379/464), the coroutine logged one line and
    /// yield-broke, and the bootstrap was already consumed. No retry, no notice, no transfer: every
    /// peer waited forever. Now a refused evaluation leaves the latch ARMED, an attempt in flight is
    /// marked <see cref="Firing"/> so it can never be started twice, and only
    /// <see cref="Conclude"/> — called on the launch AND on every failure/timeout path — spends it.
    /// </summary>
    public sealed class NewCampaignBootstrap
    {
        /// <summary>How long the latch waits, from the geoscape's playable frame, for that geoscape to report
        /// itself READY before the watchdog gives up and everyone is released. Bounded on purpose and
        /// generously: faction init is unbounded work that mods (TFTV) extend, so this is a LIVENESS backstop
        /// for a geoscape that never finishes initialising at all — not a race window. It lives HERE and not
        /// on the coordinator because it is not the load barrier's clock: L94 (e2) rules that the reveal wait
        /// has no clock, and this one gates the host's own autosave, not any peer's screen.</summary>
        public const int ReadyTimeoutMs = 120000;

        // Wall-clock deadline for the geoscape-ready seam; 0 = not waiting. Wall clock (not the geoscape
        // Timing) on purpose: the thing being waited on IS the geoscape finishing its own init, so its clock
        // is not a trustworthy driver.
        private long _readyDeadlineMs;

        /// <summary>True while the bootstrap is armed and has not concluded yet.</summary>
        public bool Armed { get; private set; }

        /// <summary>True while an autosave+transfer attempt is in flight (started, not yet concluded).</summary>
        public bool Firing { get; private set; }

        /// <summary>Arm the latch (native new-game confirm ran on the host). Re-arming is idempotent.</summary>
        public void Arm() { Armed = true; Firing = false; _readyDeadlineMs = 0; }

        /// <summary>Drop a pending bootstrap (host backed out of the native new-game settings).</summary>
        public void Disarm() { Armed = false; Firing = false; _readyDeadlineMs = 0; }

        /// <summary>A geoscape reached its playable frame with this bootstrap pending: start the liveness
        /// clock. That frame is the only moment anything knows a geoscape was entered while armed.</summary>
        public void NoteGeoscapeEntered(long nowMs)
        {
            if (Armed && !Firing) _readyDeadlineMs = nowMs + ReadyTimeoutMs;
        }

        /// <summary>The geoscape was entered but never reported ready in time (a mod threw inside LevelCrt,
        /// the level was torn down mid-init, …). True exactly once the deadline passes on a still-pending
        /// latch — never before it, never for an attempt already in flight, and never after Conclude.</summary>
        public bool ReadyWaitExpired(long nowMs) =>
            Armed && !Firing && _readyDeadlineMs != 0 && nowMs >= _readyDeadlineMs;

        /// <summary>
        /// Evaluate at the geoscape-READY seam. Returns true only when the attempt may start: still the
        /// host, session still live, geoscape live, and no transfer already in flight (the EXISTING
        /// barrier machinery is reused, never overlapped). A refusal does NOT consume the latch — the
        /// next ready seam evaluates again — and a true answer marks the attempt in flight so a second
        /// ready seam (a mod re-entering the geoscape, a second campaign) cannot launch a second
        /// transfer on top of it.
        /// </summary>
        public bool TryFire(bool isHost, bool isActiveSession, bool geoscapeActive, bool transferActive)
        {
            if (!Armed || Firing || !geoscapeActive) return false;
            if (!isHost || !isActiveSession || transferActive) return false;
            Firing = true;
            return true;
        }

        /// <summary>
        /// THE single consumption point: the attempt reached an outcome — the transfer launched, or it
        /// failed / timed out and the host+clients were told. Idempotent.
        /// </summary>
        public void Conclude() { Armed = false; Firing = false; _readyDeadlineMs = 0; }
    }
}
