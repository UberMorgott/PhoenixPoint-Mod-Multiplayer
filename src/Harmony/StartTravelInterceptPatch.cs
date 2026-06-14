using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
using Multipleer.Network.MessageLayer;
using UnityEngine; // DIAG-A1 TEMP (strip after RCA) — Debug.Log for the host->client travel-order trace

namespace Multipleer.Harmony
{
    // GeoVehicle.StartTravel(List<GeoSite>). Client -> encode + relay to host + block local exec. Host
    // (own action) -> execute locally, then Postfix broadcasts the result to all peers (the 0x35 state
    // mirror replicates Travelling/DestinationSites but never starts the client NavigateRoutine, so the
    // host-origin command broadcast is required for host-clicked craft to actually MOVE on the client).
    // A relayed/approved action arriving back through CommandRelay.ApplyResult sets the re-entrancy guard
    // (IsApplying) so this Prefix lets it execute and the Postfix does NOT re-broadcast (HostArbiter already did).
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

        // RESTORED (host-origin command broadcast). The 0x35 GeoStateDiff alone replicates Travelling +
        // DestinationSites but NEVER calls native StartTravel on the client, so the client's NavigateRoutine
        // never starts and the craft renders frozen (route-line draws, no motion). This Postfix fans the host's
        // OWN applied StartTravel out to clients on the SAME 0x31 result channel a client-origin order uses
        // (engine.BroadcastCampaignActionResult -> client OnHostCampaignActionResult -> ClientApplier.HandleResult
        // -> CommandExecutor.ApplyStartTravel -> native StartTravel), so the client runs its own NavigateRoutine
        // off the host-synced clock. ClientApplier re-applies StartTravel again (PIVOT Step A), so this is the
        // missing host->client edge for host-origin orders only.
        //
        // EXACTLY ONCE / NO DOUBLE-FIRE: broadcast happens iff (IsHost && IsActive && !CommandRelay.IsApplying).
        //   * host clicks its own craft  -> IsHost, !IsApplying -> broadcast (the one case we want).
        //   * client-relayed order on host (ApplyResult sets IsApplying) -> guarded out; HostArbiter already
        //     broadcasts that order once after _relay.ApplyResult, so this would otherwise double-send.
        //   * client applying a host broadcast (!IsHost, and IsApplying) -> guarded out -> no echo loop.
        //   * single player (engine null / !IsActive) -> guarded out.
        public static void Postfix(object __instance, object path)
        {
            var engine = NetworkEngine.Instance;
            // DIAG-A1 TEMP (strip after RCA) — boundary (a): Postfix fired. Log WHY it bails on each guard so a
            // frozen client tells us exactly which condition blocked the host-origin broadcast.
            if (engine == null || !engine.IsActive || !engine.IsHost)
            {
                Debug.Log($"[Multipleer] DIAG-A1 host-origin StartTravel Postfix SKIP (guard1) " +
                          $"engineNull={engine == null} isActive={(engine != null && engine.IsActive)} isHost={(engine != null && engine.IsHost)}"); // DIAG-A1 TEMP (strip after RCA)
                return;  // SP + client: never broadcast
            }
            if (CommandRelay.IsApplying)
            {
                Debug.Log("[Multipleer] DIAG-A1 host-origin StartTravel Postfix SKIP (guard2 CommandRelay.IsApplying=true: client-relayed apply, HostArbiter broadcasts it)"); // DIAG-A1 TEMP (strip after RCA)
                return;                               // client-relayed apply: HostArbiter already broadcasts -> no double-fire
            }

            // Host-origin: native StartTravel has already run (Postfix). Build the SAME payload shape a
            // client-origin order carries — incl. OwnerFactionGuid read from the vehicle Owner the same way
            // the Prefix does, so a host-origin NON-Phoenix craft resolves on the client (empty -> Phoenix).
            var owner = AccessTools.Property(__instance.GetType(), "Owner")?.GetValue(__instance);
            var payload = new StartTravelPayload
            {
                VehicleId = GeoBridge.VehicleId(__instance),
                OwnerFactionGuid = owner != null ? GeoBridge.FactionGuid(owner) : "",
                SiteIds = ExtractSiteIds(path),
                // PIVOT Step A start-time alignment: stamp the host geoscape game-time (DOUBLE seconds, no
                // float cast — TimeBridge.GetHostNowSeconds reads TimeSpan.TotalSeconds) + the craft's range at
                // this instant (native StartTravel just ran, so RangeRemaining is the route's start range). The
                // client carries these to reconcile its native NavigateRoutine origin against the host's (no
                // constant offset). 0 if the clock/range is unreachable -> client local fallback.
                StartGameTime = TimeBridge.GetHostNowSeconds(),
                StartRangeRemaining = GeoBridge.ReadRangeRemaining(__instance)
            };
            var msg = new CampaignActionMessage
            {
                ActionId = Guid.NewGuid(),
                ActionType = CampaignActionType.StartTravel,
                TargetId = payload.VehicleId,
                Payload = CommandCodec.EncodeStartTravel(payload),
                Timestamp = DateTime.UtcNow.Ticks
            };
            // DIAG-A1 TEMP (strip after RCA) — boundary (a): the host-origin order IS being broadcast. veh + site
            // count + the guard state that let us through, so we know the Postfix reached the wire.
            Debug.Log($"[Multipleer] DIAG-A1 host-origin StartTravel broadcast veh={payload.VehicleId} " +
                      $"owner={(string.IsNullOrEmpty(payload.OwnerFactionGuid) ? "EMPTY" : payload.OwnerFactionGuid)} " +
                      $"sites={(payload.SiteIds != null ? payload.SiteIds.Length : -1)} isHost={engine.IsHost} " +
                      $"isActive={engine.IsActive} isApplying={CommandRelay.IsApplying}"); // DIAG-A1 TEMP (strip after RCA)
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
