namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure store + decision gate for the CLIENT-side research RATE override (research channel #2, v3
    /// rate block). Fixes the research window showing DIFFERENT completion dates on host vs client:
    /// the ETA (<c>Research.GetTotalTimeLeft</c>, Research.cs:694) divides remaining cost by
    /// <c>GetHourlyResearchProduction</c> (Research.cs:649), which each peer computes LOCALLY from its
    /// own facility production (labs/workers/AI-core tiers/damage/power) — and that diverges on the
    /// mirrored client. Progress already syncs (ch2 hourly overwrite), so the RATE is the only ETA
    /// divergence source. The host replicates its effective rate in the ch2 snapshot; the client stores
    /// it here and <c>ClientResearchRatePatch</c> feeds it back through the native formula.
    ///
    /// Unity-free and unit-testable (mirrors the <see cref="Multiplayer.Network.ClientSimFreeze"/>
    /// gate-helper pattern); the Harmony patch computes the live bools and defers the decision here.
    /// </summary>
    public static class ClientResearchRate
    {
        /// <summary>
        /// Last host-synced hourly research rate. Null until the first ch2 snapshot carrying a rate
        /// arrives (fresh join / old-host payload) — the override postfix NEVER fires while null, so a
        /// client that hasn't heard from the host keeps its locally-computed rate. Cleared via
        /// <see cref="Reset"/> on engine teardown (NetworkEngine.Shutdown/TearDown): without it, a fast
        /// client→client reconnection could apply the PREVIOUS session's rate in the window between join
        /// (IsActiveSession true) and that session's first ch2 seed.
        /// </summary>
        public static float? SyncedRate;

        /// <summary>Session teardown: drop the synced rate so the next session starts null (no
        /// cross-session leak; the override stays off until that session's first ch2 seed arrives).</summary>
        public static void Reset()
        {
            SyncedRate = null;
        }

        /// <summary>
        /// Client apply (ch2): record the snapshot's rate. A payload WITHOUT a rate (old host build /
        /// host-side bind failure) keeps the last known value rather than clearing — a stale-but-real
        /// host rate still beats falling back to the client's diverged local computation.
        /// </summary>
        public static void OnSnapshotApplied(float? hourlyRate)
        {
            if (hourlyRate.HasValue) SyncedRate = hourlyRate;
        }

        /// <summary>
        /// Pure truth table: override <c>GetHourlyResearchProduction</c>'s result with the synced rate
        /// ONLY on an active-session CLIENT that has RECEIVED a rate, and only for the local Phoenix
        /// faction's own <c>Research</c> instance:
        ///   * single-player / no active session -> false (local computation is authoritative)
        ///   * host                              -> false (host computes the authoritative rate natively)
        ///   * no synced value yet               -> false (never override before the first sync)
        ///   * ally/NPC Research instance        -> false (GetAlliesContribution walks ally instances —
        ///                                          those keep their client-local rate; accepted edge)
        /// </summary>
        public static bool ShouldOverride(bool engineExists, bool isActive, bool isHost,
            bool hasSyncedRate, bool isLocalPhoenixResearch)
        {
            if (!engineExists || !isActive) return false;  // single-player / no active session
            if (isHost) return false;                      // host is the rate authority
            if (!hasSyncedRate) return false;              // nothing synced yet (fresh join)
            if (!isLocalPhoenixResearch) return false;     // only the local Phoenix faction's Research
            return true;
        }
    }
}
