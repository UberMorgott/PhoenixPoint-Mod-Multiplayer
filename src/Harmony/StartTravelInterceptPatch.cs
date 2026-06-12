using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
using Multipleer.Network.MessageLayer;

namespace Multipleer.Harmony
{
    // C7 vertical proof: GeoVehicle.StartTravel(List<GeoSite>). Client -> encode + relay to host +
    // block local exec. Host (own action) -> execute locally, then postfix broadcasts the result to
    // all peers. A relayed/approved action arriving back through CommandRelay.ApplyResult sets the
    // re-entrancy guard (IsApplying) so this prefix lets it execute and the postfix does not re-broadcast.
    [HarmonyPatch]
    public static class StartTravelInterceptPatch
    {
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            var vehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (vehicleType == null || geoSiteType == null) return false;
            var listType = typeof(List<>).MakeGenericType(geoSiteType);
            // Disambiguate the GeoSite overload from StartTravel(List<Vector3>).
            _targetMethod = AccessTools.Method(vehicleType, "StartTravel", new[] { listType });
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        // __instance = the GeoVehicle; path = List<GeoSite> (received as object / IEnumerable for our use).
        public static bool Prefix(object __instance, object path)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;     // single player: pass through
            if (CommandRelay.IsApplying) return true;                 // re-entrant apply: execute the real method
            if (engine.IsHost) return true;                          // host-origin: execute, postfix broadcasts

            // Client-origin: encode + relay to host + block local execution.
            var payload = new StartTravelPayload
            {
                VehicleId = GeoBridge.VehicleId(__instance),
                SiteIds = ExtractSiteIds(path)
            };
            var msg = new CampaignActionMessage
            {
                ActionId = Guid.NewGuid(),
                ActionType = CampaignActionType.StartTravel,
                TargetId = payload.VehicleId,
                Payload = CommandCodec.EncodeStartTravel(payload),
                Timestamp = DateTime.UtcNow.Ticks
            };
            CommandRelay.Instance?.RelayFromClient(msg);
            return false;
        }

        // Host-origin action executed locally -> fan the result out to clients (skip during re-entrant apply,
        // where HostArbiter already broadcasts explicitly after CommandRelay.ApplyResult).
        public static void Postfix(object __instance, object path)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (CommandRelay.IsApplying) return;

            var payload = new StartTravelPayload
            {
                VehicleId = GeoBridge.VehicleId(__instance),
                SiteIds = ExtractSiteIds(path)
            };
            var msg = new CampaignActionMessage
            {
                ActionId = Guid.NewGuid(),
                ActionType = CampaignActionType.StartTravel,
                TargetId = payload.VehicleId,
                Payload = CommandCodec.EncodeStartTravel(payload),
                Timestamp = DateTime.UtcNow.Ticks
            };
            engine.BroadcastCampaignActionResult(msg);
        }

        private static string[] ExtractSiteIds(object path)
        {
            var ids = new List<string>();
            if (path is System.Collections.IEnumerable e)
                foreach (var site in e) ids.Add(GeoBridge.SiteId(site));
            return ids.ToArray();
        }
    }
}
