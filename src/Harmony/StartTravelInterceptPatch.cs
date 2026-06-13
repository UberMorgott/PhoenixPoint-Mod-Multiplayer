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
            if (CommandRelay.IsApplying) return true;                 // re-entrant apply (host executing an
                                                                      // approved order): execute the real method
            // INC-A: the client runs NO native StartTravel routine of its own — it is a pure mirror, driven only
            // by applied 0x35 state. There is no EntityReplicationScope carve-out for StartTravel anymore (the
            // client second-writer that used it is removed), so a client-origin call always relays + blocks.
            if (engine.IsHost) return true;                          // host-origin: execute, postfix broadcasts

            // Client-origin: encode + relay to host + block local execution. Carry the owning faction's
            // Def.Guid (INC-3a) so the host resolves the craft by (factionGuid, VehicleID) — not Phoenix-only —
            // letting a client order a non-Phoenix craft to travel. Owner read via the same accessor
            // GeoBridge.RecordVehicleState uses; empty -> host strict-resolves to Phoenix (legacy case).
            var owner = AccessTools.Property(__instance.GetType(), "Owner")?.GetValue(__instance);
            var payload = new StartTravelPayload
            {
                VehicleId = GeoBridge.VehicleId(__instance),
                OwnerFactionGuid = owner != null ? GeoBridge.FactionGuid(owner) : "",
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

        // INC-3a: the host-origin StartTravel command-REPLAY Postfix is RETIRED. Host motion now mirrors to
        // clients purely as 0x35 GeoStateDiff (Travelling + DestinationSites + per-tick pos/rot/range), so a
        // host-moved craft no longer broadcasts a StartTravel command. Only the client->host INPUT relay
        // (Prefix above) is kept; the client never replays StartTravel (ClientApplier skips it) and never
        // calls StartTravel on a mirrored vehicle.

        private static string[] ExtractSiteIds(object path)
        {
            var ids = new List<string>();
            if (path is System.Collections.IEnumerable e)
                foreach (var site in e) ids.Add(GeoBridge.SiteId(site));
            return ids.ToArray();
        }
    }
}
