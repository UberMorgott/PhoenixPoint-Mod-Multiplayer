namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure decision for the commit-seam HARVEST (stat-sync race RCA 2026-07-10). A ConflictRepaint is about
    /// to re-drive the OPEN soldier's progression panel (<c>SetCharacterProgression → RefreshStats</c> resets
    /// <c>_current*Stat</c> → <c>_starting*Stat</c>), which DISCARDS the local pending stat allocation before the
    /// deferred <c>CommitStatChanges</c> seam ever relays it — log-proven: a client's stat spends reached the
    /// host ZERO times a whole session because the host's periodic same-unit #9 re-broadcast wiped the buffer
    /// every few seconds. So the pending spend must be COMMITTED before the wipe: a co-op CLIENT relays one
    /// <c>SpendStatPoints</c> intent per positive delta; the HOST commits it natively. This decides WHICH from
    /// the peer role + the pending deltas; the reflective read of the module fields (and the reflective act) is
    /// the caller's job (PersonnelEditRelay / GeoUiRefresh). An unconfirmed ability pick (<c>_boughtAbilitySlot</c>)
    /// is a pre-confirm SELECTION (OnTrackSlotPointerClicked arms it + opens a confirmation popup), never a
    /// commit, so it is NOT harvested here — the caller discards it.
    /// </summary>
    public static class StatSpendHarvest
    {
        public enum Mode
        {
            /// <summary>Nothing to commit: no active session, no pending stat delta, or mutoid on a client.</summary>
            None,
            /// <summary>Host is authoritative → invoke the native <c>CommitStatChanges()</c> before the wipe
            /// (handles the SP pool split AND the mutoid mutagen Take).</summary>
            HostCommit,
            /// <summary>Co-op client → relay one <c>SpendStatPoints</c> intent per positive delta before the wipe;
            /// the host applies + re-broadcasts and the panel converges to the committed values.</summary>
            ClientRelay,
        }

        /// <summary>Decide the harvest action for one about-to-wipe ConflictRepaint. <paramref name="pandoran"/> =
        /// mutoid (mutagen-cost) progression: out of the SP intent family, so a CLIENT does not relay it (the host
        /// still commits it natively — the native CommitStatChanges does the mutagen Take).</summary>
        public static Mode Decide(bool activeSession, bool isHost, bool pandoran, int dStr, int dWill, int dSpeed)
        {
            if (!activeSession) return Mode.None;                          // single-player: no conflict repaints
            if (dStr <= 0 && dWill <= 0 && dSpeed <= 0) return Mode.None;  // pending edit was ability-only / none
            if (isHost) return Mode.HostCommit;                           // native commit: SP split + mutoid mutagen
            if (pandoran) return Mode.None;                               // mutoid stat spend is out of the SP intent family
            return Mode.ClientRelay;
        }
    }
}
