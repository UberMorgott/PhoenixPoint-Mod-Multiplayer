using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using UnityEngine;

namespace Multipleer.Harmony
{
    // [DIAG] TEMPORARY diagnostic patch (logging only, no behavior change). Remove after the
    // host->client vehicle-create replication boundary run is analysed.
    //
    // Targets GeoPhoenixFaction.DeployAsset(IGeoCharacterContainer container, object asset, bool manufactured)
    // — the single deploy seam that calls CreateVehicle when asset is a ComponentSetDef. We log WHICH machine
    // ran the deploy (IsHost) so we can tell a client-origin deploy (which never reaches the host create-seam
    // and so is never broadcast) from a host-origin one. The Prefix ALWAYS returns true (flow untouched).
    [HarmonyPatch]
    public static class DiagDeployLogPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            if (t == null) return false;
            _target = AccessTools.Method(t, "DeployAsset");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = GeoPhoenixFaction; __0 = container (IGeoCharacterContainer); __1 = asset; __2 = manufactured.
        // Everything is read defensively (no NRE from a diagnostic) and the method always returns true.
        public static bool Prefix(object __0, object __1, bool __2)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                var isHost = (engine != null) ? engine.IsHost.ToString() : "noEngine";
                var isActive = (engine != null) ? engine.IsActive.ToString() : "noEngine";
                var defName = (__1 != null) ? __1.GetType().Name : "null";
                var containerName = (__0 != null) ? __0.GetType().Name : "null";
                Debug.Log($"[Multipleer] DIAG DeployAsset on machine IsHost={isHost} IsActive={isActive} " +
                    $"assetType={defName} container={containerName} manufactured={__2}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Multipleer] DIAG DeployAsset log failed: {e.Message}");
            }
            return true; // never alter flow
        }
    }
}
