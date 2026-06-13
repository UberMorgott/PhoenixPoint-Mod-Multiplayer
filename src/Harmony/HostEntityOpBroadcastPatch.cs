using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
using UnityEngine;

namespace Multipleer.Harmony
{
    // SD-AIDR INC-2 (B): on the HOST, broadcast an entity create/destroy op (0x36) whenever the native
    // birth/death seams fire, so clients replicate the entity. Mirrors StartTravelInterceptPatch.Postfix:
    // host-only gate; bail during a relayed/replayed apply so we never echo a replicated op.
    //   * GeoFaction.CreateVehicle(GeoSite, ComponentSetDef)        -> VehicleCreated (site anchor)
    //   * GeoFaction.CreateVehicleAtPosition(Vector3, ComponentSetDef) -> VehicleCreated (position)
    //   * GeoFaction.UnregisterVehicle(GeoVehicle)                  -> VehicleRemoved (single removal choke)
    //   * GeoSite.DestroySite()                                     -> SiteRemoved
    // SiteCreated is NOT emitted in INC-2 (a new site needs full InstanceData -> INC-3 0x35).
    [HarmonyPatch]
    public static class HostEntityOpBroadcastPatch
    {
        private static MethodBase _createVehicle;
        private static MethodBase _createVehicleAtPos;
        private static MethodBase _unregisterVehicle;
        private static MethodBase _destroySite;

        public static bool Prepare()
        {
            var faction = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            var site = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var csdType = AccessTools.TypeByName("Base.Core.ComponentSetDef");
            var vehType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            if (faction == null || site == null || csdType == null || vehType == null) return false;

            _createVehicle = AccessTools.Method(faction, "CreateVehicle", new[] { geoSiteType, csdType });
            _createVehicleAtPos = AccessTools.Method(faction, "CreateVehicleAtPosition",
                new[] { typeof(Vector3), csdType });
            _unregisterVehicle = AccessTools.Method(faction, "UnregisterVehicle", new[] { vehType });
            _destroySite = AccessTools.Method(site, "DestroySite", Type.EmptyTypes);

            // Patch the class if at least one seam resolved (best-effort).
            return _createVehicle != null || _createVehicleAtPos != null
                || _unregisterVehicle != null || _destroySite != null;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            if (_createVehicle != null) yield return _createVehicle;
            if (_createVehicleAtPos != null) yield return _createVehicleAtPos;
            if (_unregisterVehicle != null) yield return _unregisterVehicle;
            if (_destroySite != null) yield return _destroySite;
        }

        // __originalMethod tells us which seam fired (one Postfix, four targets). __instance is the
        // GeoFaction (vehicle seams) or GeoSite (DestroySite); __result is the new GeoVehicle for the
        // create seams (object so the mod never references GeoVehicle). For UnregisterVehicle the removed
        // vehicle arrives as the first parameter (__0). For DestroySite there are no params.
        public static void Postfix(object __instance, object __result, MethodBase __originalMethod, object __0)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return; // host emits authoritatively
            if (CommandRelay.IsApplying) return;            // a relayed command apply already fans out
            if (EntityReplicationScope.IsApplying) return;  // a client replay (never on host, defensive)

            var name = __originalMethod.Name;
            try
            {
                if (name == "CreateVehicle" || name == "CreateVehicleAtPosition")
                {
                    var vehicle = __result; // the new GeoVehicle
                    if (vehicle == null) return;
                    if (!int.TryParse(GeoBridge.VehicleId(vehicle), out var createdId)) return;
                    var op = new GeoEntityOp
                    {
                        OpType = GeoEntityOpType.VehicleCreated,
                        DefGuid = GeoBridge.VehicleDefGuid(vehicle),
                        OwnerFactionGuid = GeoBridge.FactionGuid(__instance),
                        SiteId = ResolveCurrentSiteId(vehicle),
                        EntityId = createdId
                    };
                    // Position fallback when the vehicle is not anchored on a site (CreateVehicleAtPosition).
                    if (op.SiteId < 0)
                    {
                        var pos = WorldPositionOf(vehicle);
                        op.PosX = pos.x; op.PosY = pos.y; op.PosZ = pos.z;
                    }
                    engine.BroadcastGeoEntityOp(op);
                }
                else if (name == "UnregisterVehicle")
                {
                    var vehicle = __0; // the removed GeoVehicle
                    if (vehicle == null) return;
                    if (!int.TryParse(GeoBridge.VehicleId(vehicle), out var removedId)) return;
                    engine.BroadcastGeoEntityOp(new GeoEntityOp
                    {
                        OpType = GeoEntityOpType.VehicleRemoved,
                        SiteId = -1,
                        EntityId = removedId
                    });
                }
                else if (name == "DestroySite")
                {
                    var site = __instance; // the GeoSite being destroyed
                    if (!int.TryParse(GeoBridge.SiteId(site), out var removedSiteId)) return;
                    engine.BroadcastGeoEntityOp(new GeoEntityOp
                    {
                        OpType = GeoEntityOpType.SiteRemoved,
                        SiteId = removedSiteId,
                        EntityId = -1
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Multipleer] HostEntityOpBroadcastPatch ({name}) failed: {ex}");
            }
        }

        // GeoVehicle.CurrentSite?.SiteId, or -1 if travelling / no site (use position instead).
        private static int ResolveCurrentSiteId(object vehicle)
        {
            var site = AccessTools.Property(vehicle.GetType(), "CurrentSite")?.GetValue(vehicle);
            if (site == null) return -1;
            var s = GeoBridge.SiteId(site);
            return int.TryParse(s, out var id) ? id : -1;
        }

        // ActorComponent.WorldPosition (base) — Vector3.
        private static Vector3 WorldPositionOf(object vehicle)
        {
            var v = AccessTools.Property(vehicle.GetType(), "WorldPosition")?.GetValue(vehicle);
            return v is Vector3 p ? p : Vector3.zero;
        }
    }
}
