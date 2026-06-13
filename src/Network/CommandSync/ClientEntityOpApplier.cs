using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-2 (C): client-only apply of a host 0x36 GeoEntityOp. Runs the NATIVE entity lifecycle
    // (no hand-built visuals) under EntityReplicationScope so the birth/death postfixes the replay
    // triggers (HostEntityOpBroadcastPatch) recognize a replay and do not re-broadcast.
    //   * VehicleCreated -> GeoFaction.CreateVehicle(GeoSite, ComponentSetDef) [Instantiate -> DoEnterPlay
    //     -> OnLevelStart -> TeleportToSite -> VehicleAdded; marker comes from the lifecycle, C18 -> we do
    //     NOT gate VehicleAdded], then reconcile VehicleID/_lastVehicleIndex to the host's authoritative id
    //     so a later StartTravel for that id resolves in GeoBridge.FindVehicleById.
    //   * VehicleRemoved -> GeoVehicle.Destroy().
    //   * SiteRemoved   -> GeoSite.DestroySite().
    //   * SiteCreated   -> NOT applied (needs full GeoSiteInstaceData -> INC-3 0x35); logged + ignored.
    public static class ClientEntityOpApplier
    {
        public static void Apply(GeoEntityOp op)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return; // client-only

            var geoLevel = GeoBridge.GetGeoLevelController();
            if (geoLevel == null) { Debug.LogWarning("[Multipleer] EntityOp apply: no GeoLevelController."); return; }

            using (EntityReplicationScope.Enter())
            {
                switch (op.OpType)
                {
                    case GeoEntityOpType.VehicleCreated: ApplyVehicleCreated(geoLevel, op); break;
                    case GeoEntityOpType.VehicleRemoved: ApplyVehicleRemoved(geoLevel, op); break;
                    case GeoEntityOpType.SiteRemoved:    ApplySiteRemoved(geoLevel, op); break;
                    case GeoEntityOpType.SiteCreated:
                        Debug.Log("[Multipleer] EntityOp: SiteCreated deferred to INC-3 (needs site InstanceData).");
                        break;
                }
            }
        }

        private static void ApplyVehicleCreated(object geoLevel, GeoEntityOp op)
        {
            // Idempotency: if the vehicle id already exists (duplicate op / reload race), skip.
            if (GeoBridge.FindVehicleById(geoLevel, op.EntityId.ToString()) != null)
            {
                Debug.Log($"[Multipleer] EntityOp VehicleCreated: id {op.EntityId} already present, skip.");
                return;
            }

            var def = GeoBridge.FindDefByGuid(op.DefGuid);
            if (def == null) { Debug.LogWarning($"[Multipleer] VehicleCreated: def {op.DefGuid} not resolved."); return; }

            var faction = GeoBridge.FindFactionByGuid(geoLevel, op.OwnerFactionGuid);
            if (faction == null) { Debug.LogWarning("[Multipleer] VehicleCreated: owner faction not resolved."); return; }

            object vehicle;
            if (op.SiteId >= 0)
            {
                var site = GeoBridge.FindSiteById(geoLevel, op.SiteId);
                if (site == null) { Debug.LogWarning($"[Multipleer] VehicleCreated: anchor site {op.SiteId} not found."); return; }
                vehicle = GeoBridge.CreateVehicleAtSite(faction, site, def);
            }
            else
            {
                vehicle = GeoBridge.CreateVehicleAtPosition(faction, new Vector3(op.PosX, op.PosY, op.PosZ), def);
            }

            if (vehicle == null) { Debug.LogError("[Multipleer] VehicleCreated: native CreateVehicle returned null."); return; }
            GeoBridge.ReconcileVehicleId(faction, vehicle, op.EntityId);
            Debug.Log($"[Multipleer] EntityOp VehicleCreated: spawned + reconciled VehicleID {op.EntityId}.");
        }

        private static void ApplyVehicleRemoved(object geoLevel, GeoEntityOp op)
        {
            var vehicle = GeoBridge.FindVehicleById(geoLevel, op.EntityId.ToString());
            if (vehicle == null) { Debug.Log($"[Multipleer] VehicleRemoved: id {op.EntityId} absent, nothing to remove."); return; }
            AccessTools.Method(vehicle.GetType(), "Destroy", System.Type.EmptyTypes)?.Invoke(vehicle, null);
            Debug.Log($"[Multipleer] EntityOp VehicleRemoved: destroyed VehicleID {op.EntityId}.");
        }

        private static void ApplySiteRemoved(object geoLevel, GeoEntityOp op)
        {
            var site = GeoBridge.FindSiteById(geoLevel, op.SiteId);
            if (site == null) { Debug.Log($"[Multipleer] SiteRemoved: site {op.SiteId} absent."); return; }
            AccessTools.Method(site.GetType(), "DestroySite")?.Invoke(site, null);
            Debug.Log($"[Multipleer] EntityOp SiteRemoved: destroyed SiteId {op.SiteId}.");
        }
    }
}
