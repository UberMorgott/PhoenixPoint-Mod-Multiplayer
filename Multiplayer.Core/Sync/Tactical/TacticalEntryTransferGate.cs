namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Pure, Unity-free decision (Batch 1): should the HOST ship a byte-identical mid-tactical save to the
    /// clients at deploy-ready, so a client BUILDS its tactical level from the host's exact state instead of
    /// self-launching + reconciling? True iff ALL hold:
    ///   • flagOn        — the entry-via-save feature flag (TacticalDeploySync.UseSaveTransferEntry) is on;
    ///   • isHost        — only the co-op host authors + ships the save;
    ///   • sessionActive — a co-op session is live (nobody to ship to otherwise);
    ///   • isTactical    — the current level is tactical (a geoscape save at this seam would be the WRONG save);
    ///   • !transferActive — no other save transfer is already in flight (never stack a 2nd over an open barrier).
    /// Branch-per-case style mirrors <see cref="TacticalDeployArrivalGate"/> and the SessionLifecycle host gates.
    /// </summary>
    public static class TacticalEntryTransferGate
    {
        public static bool ShouldSendTacticalSave(
            bool isHost, bool sessionActive, bool isTactical, bool transferActive, bool flagOn)
            => flagOn && isHost && sessionActive && isTactical && !transferActive;
    }
}
