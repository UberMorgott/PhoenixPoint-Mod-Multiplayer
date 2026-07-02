namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Default-OFF rollout gate for the host->client geoscape REPORT-WINDOW mirror (Phase-A of the
    /// event-window-mirror plan, additive-first). While <see cref="Enabled"/> is false (the SHIPPED default)
    /// the host broadcasts NOTHING on the new <c>PacketType.ReportModalShow</c> (0x69) surface and the client
    /// Prefix suppresses NOTHING, so behavior is byte-for-byte unchanged — the existing GeoscapeEvent-dialog
    /// replication path (0x65/0x66) is untouched. Flip to true (a one-line dev edit + recompile) only after
    /// in-game verification, mirroring the earlier geoscape rail-unify slice-1 rollout.
    /// </summary>
    public static class ReportMirrorGate
    {
        /// <summary>Master switch for the additive report-window mirror. Shipped OFF.</summary>
        public static bool Enabled = false;
    }
}
