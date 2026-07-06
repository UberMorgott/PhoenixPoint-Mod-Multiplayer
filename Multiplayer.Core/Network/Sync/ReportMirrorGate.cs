namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Rollout gate for the host->client geoscape REPORT-WINDOW mirror (Phase-A of the event-window-mirror plan).
    /// When <see cref="Enabled"/> is false the host broadcasts NOTHING on the <c>PacketType.ReportModalShow</c>
    /// (0x69) surface and the client Prefix suppresses NOTHING (byte-for-byte native). ENABLED (2026-07-05) after
    /// the research-complete report was confirmed missing on the client: the host's native research-complete window
    /// (<c>GeoscapeView.OnFactionResearchCompleted</c> → <c>OpenModal(GeoResearchComplete)</c>) never reached the
    /// client, because the client's completion arrives reward-free via the ResearchChannel echo (no
    /// <c>OnResearchCompleted</c> event) AND the client's <c>SuppressEvents</c> gates the native opener. Flipping
    /// this on makes the host broadcast the whitelisted report modals — GeoResearchComplete (14) → the client
    /// rebuilds <c>GeoResearchCompleteData</c> from the synced researchId and shows the SAME native popup — plus the
    /// other three Phase-A reports (GeoPhoenixBaseOutcome/PandoranRevealResult/DiplomacyResearchBrief). Fires once
    /// per host modal open (dedup/replay-safe: <c>SyncApplyScope.IsApplying</c> blocks re-broadcast; the client
    /// Prefix suppresses its own local open, so no double-show; multi-completion bursts queue as the host opens
    /// them). Rollback = flip back to false + recompile.
    /// </summary>
    public static class ReportMirrorGate
    {
        /// <summary>Master switch for the additive report-window mirror. ENABLED (research-complete popup mirror).</summary>
        public static bool Enabled = true;
    }
}
