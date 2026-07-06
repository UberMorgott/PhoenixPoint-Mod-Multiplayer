namespace Multiplayer.Network
{
    /// <summary>
    /// Pure, Unity-free single-shot latch for the P0 new-campaign co-op bootstrap. The HOST arms it at
    /// the native new-game CONFIRM chokepoint (NewCampaignInterceptPatch on
    /// <c>UIStateNewGeoscapeGameSettings.GameSettings_OnConfirm</c>) and lets the NATIVE campaign
    /// creation run to completion; the latch is then consumed EXACTLY ONCE at the first PLAYABLE
    /// geoscape frame (CurtainShowPatch "Playing" seam), where the coordinator autosaves the freshly
    /// created campaign and feeds that autosave into the EXISTING chunked save transfer + 2-phase
    /// barrier (<c>SaveTransferCoordinator.LaunchTransfer</c>) — no new transfer mechanism.
    ///
    /// Extracted from the game-bound coordinator so the arm/fire-once contract is directly
    /// unit-testable (NewCampaignBootstrapTests); the coordinator forwards to this.
    /// </summary>
    public sealed class NewCampaignBootstrap
    {
        /// <summary>True while the bootstrap is armed and the geoscape has not been reached yet.</summary>
        public bool Armed { get; private set; }

        /// <summary>Arm the latch (native new-game confirm ran on the host). Re-arming is idempotent.</summary>
        public void Arm() => Armed = true;

        /// <summary>Drop a pending bootstrap (host backed out of the native new-game settings).</summary>
        public void Disarm() => Armed = false;

        /// <summary>
        /// Evaluate at a PLAYABLE-frame edge (a level reached Playing). Non-geoscape playable frames
        /// (e.g. the tutorial tactical mission — belt+braces, the intercept forces the tutorial off)
        /// keep the latch armed. The first playable GEOSCAPE frame is the SINGLE consumption point:
        /// the latch disarms whether or not the fire guard is open — a stale arm can never fire on a
        /// later unrelated load — and returns true only when the transfer may actually launch: still
        /// the host, session still live, and no transfer already in flight (the EXISTING barrier
        /// machinery is reused, never overlapped).
        /// </summary>
        public bool TryFire(bool isHost, bool isActiveSession, bool geoscapeActive, bool transferActive)
        {
            if (!Armed || !geoscapeActive) return false;
            Armed = false;
            return isHost && isActiveSession && !transferActive;
        }
    }
}
