namespace Multipleer.Network.CommandSync
{
    // TEMP no-op stub created with the Task-8 0x35 wiring (INC-3a). RouteMessage already decodes the
    // GeoStateDiff envelope and dispatches here; until Task 9 fills this in the apply does NOTHING, so
    // wiring the packet does not change client behavior yet (build stays 0/0). Mirrors the INC-2
    // wire-before-applier sequencing.
    //
    // Task 9 REPLACES this body with the real client-only mirror: client-only gate
    // (engine != null && IsActive && !IsHost), per-(FactionGuid,VehicleID) seq guard (drop
    // Seq <= lastApplied), resolve via GeoBridge.FindVehicleByFactionAndId, light ApplyVehicleState /
    // heavy ApplyVehicleStateFull (first mirror + CRC-heal), all under EntityReplicationScope.Enter(),
    // plus the one-shot DIAG3 nav log.
    public static class ClientGeoStateApplier
    {
        public static void Apply(GeoStateDiff diff)
        {
            // Task 9: real seq-guarded all-faction mirror under EntityReplicationScope. No-op for now.
        }
    }
}
