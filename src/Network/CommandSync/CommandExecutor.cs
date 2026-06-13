using System.Collections.Generic;
using HarmonyLib;
using Multipleer.Network.MessageLayer;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // Resolves an InterceptEntry to the live game method via AccessTools and invokes it with the
    // decoded payload. Stage 1 implements ONLY the StartTravel apply (the confirmed vertical proof);
    // other confirmed entries get their apply branch as they are wired in Task 7.
    internal static class CommandExecutor
    {
        public static void Execute(InterceptEntry entry, CampaignActionMessage action)
        {
            switch (action.ActionType)
            {
                case CampaignActionType.StartTravel:
                    ApplyStartTravel(action);
                    break;
                case CampaignActionType.SetTimeState:
                    ApplySetTime(action);
                    break;
                default:
                    Debug.LogWarning($"[Multipleer] CommandExecutor: no apply branch for {action.ActionType}.");
                    break;
            }
        }

        private static void ApplyStartTravel(CampaignActionMessage action)
        {
            var p = CommandCodec.DecodeStartTravel(action.Payload);

            var geoLevel = GeoBridge.GetGeoLevelController();
            if (geoLevel == null) { Debug.LogWarning("[Multipleer] StartTravel apply: no GeoLevelController."); return; }

            // [DIAG2] TEMPORARY (logging only, no behavior change). On the CLIENT apply path: dump the
            // requested vehicle id + the client's FULL PhoenixFaction.Vehicles set on BOTH outcomes
            // (found or not-found), so it can be diffed against "DIAG2 host vehicles" to see whether the
            // requestedVehicleId is present in the client's set.
            try
            {
                var snap = GeoBridge.DescribeVehicles(geoLevel);
                Debug.Log($"[Multipleer] DIAG2 client StartTravel apply: requestedVehicleId={p.VehicleId}");
                Debug.Log($"[Multipleer] DIAG2 client vehicles[{snap.Count}]: {snap.List}");
            }
            catch (System.Exception diagEx) { Debug.LogWarning($"[Multipleer] DIAG2 client log failed: {diagEx.Message}"); }

            var vehicle = GeoBridge.FindVehicleById(geoLevel, p.VehicleId);
            if (vehicle == null) { Debug.LogWarning($"[Multipleer] StartTravel apply: vehicle {p.VehicleId} not found."); return; }

            var path = GeoBridge.BuildSitePath(geoLevel, p.SiteIds);
            if (path == null) { Debug.LogWarning("[Multipleer] StartTravel apply: could not resolve site path."); return; }

            // Invoke GeoVehicle.StartTravel(List<GeoSite>) — OVERLOADED with StartTravel(List<Vector3>),
            // so disambiguate by the GeoSite list type. Runs under CommandRelay's guard (the intercept
            // prefix checks CommandRelay.IsApplying and lets this re-entrant call through).
            var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (geoSiteType == null) { Debug.LogError("[Multipleer] StartTravel apply: GeoSite type not resolved."); return; }
            var listType = typeof(List<>).MakeGenericType(geoSiteType);
            var method = AccessTools.Method(vehicle.GetType(), "StartTravel", new[] { listType });
            if (method == null) { Debug.LogError("[Multipleer] StartTravel apply: method not resolved."); return; }
            method.Invoke(vehicle, new object[] { path });
        }

        // Apply an authorized time change on host + clients. Decodes {Paused, PresetIndex} and drives
        // the live UIModuleTimeControl via TimeBridge (coherent Scale/animator/pause). Runs under
        // CommandRelay.IsApplying (set by ApplyResult) so the OnPauseTime/SelectTimePreset prefixes
        // see a re-entrant apply and execute the real writes instead of re-sending.
        private static void ApplySetTime(CampaignActionMessage action)
        {
            var p = CommandCodec.DecodeSetTime(action.Payload);
            TimeBridge.ApplySetTime(p);
        }
    }
}
