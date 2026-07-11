namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Pure, Unity-free decision (Batch 1): should a CLIENT that suppressed its self-launch (waiting on the
    /// host's entry-via-save transfer) FALL BACK to the legacy self-launch? True iff the stall deadline has
    /// passed AND the deploy is still un-hydrated AND no transfer arrived (no SaveChunk → TransferActive still
    /// false) AND no tactical level got built. Guards the dead-hang when the host's mid-tactical save write
    /// aborts BEFORE SendBlob/OpenBarrier: no chunks + no barrier means the reveal/kick fallbacks never arm,
    /// so the client would wait forever. Branch-per-input style mirrors <see cref="TacticalEntryTransferGate"/>.
    /// </summary>
    public static class TacticalEntryStallGate
    {
        public static bool ShouldFallbackToSelfLaunch(
            bool deadlinePassed, bool stillPending, bool transferArrived, bool liveTacticalLevel)
            => deadlinePassed && stillPending && !transferArrived && !liveTacticalLevel;
    }
}
