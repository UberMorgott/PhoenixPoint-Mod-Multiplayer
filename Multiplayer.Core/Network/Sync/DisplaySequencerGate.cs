namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Rollout gate for the Batch-3 P4 UNIFIED DISPLAY SEQUENCER (spec
    /// <c>2026-07-05-multiplayer-unified-popup-mirror-design.md</c> §P4) — the ONE flag the cutover rides so a
    /// display can never ride both the legacy per-rail path and the sequencer at once (the spec's
    /// double-display-mid-migration risk). ON: the host stamps every mirrored display message (event raise 0x65,
    /// report modal 0x69, cutscene action) with <c>{displaySeq, nativePriority}</c> at its own
    /// <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c> fire-time, and the client routes every STAMPED message
    /// through the single <see cref="State.UnifiedDisplayQueue"/> (nativePriority DESC, displaySeq ASC,
    /// one-at-a-time, released when the current display closes) before the existing per-rail handlers run.
    /// OFF (or an UNSTAMPED legacy message, displaySeq==0): the message takes its exact pre-Batch-3 direct
    /// path — never both, so the rails cut over atomically with the flag. Rollback = flip false + recompile.
    /// </summary>
    public static class DisplaySequencerGate
    {
        /// <summary>Master switch for the unified cross-rail display sequencer (Batch-3 P4).</summary>
        public static bool Enabled = true;
    }
}
