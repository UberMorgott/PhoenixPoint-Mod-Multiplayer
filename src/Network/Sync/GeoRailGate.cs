namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Default-OFF rollout gate for the Increment-1 geoscape ENVELOPE rail (unified backbone spec §6.1,
    /// "Unify the rail", additive-first). While <see cref="Enabled"/> is false (the shipped default) the
    /// geoscape co-op rides ONLY the legacy raw packets (0x60-0x66) exactly as today — the host emits NOTHING
    /// on the new SyncEnvelope (0x67) geoscape surfaces, so behavior is byte-for-byte unchanged. Flip to true
    /// (a one-line dev edit + recompile) to ALSO mirror the migrated geoscape message(s) onto the shared
    /// envelope rail for in-game verification; the migrated wallet message is version-guarded + idempotent, so
    /// running both paths is safe. The legacy raw-packet send is retired only in a LATER, in-game-verified slice.
    /// </summary>
    public static class GeoRailGate
    {
        /// <summary>Master switch for the additive geoscape envelope rail. Shipped OFF.</summary>
        public static bool Enabled = false;
    }
}
