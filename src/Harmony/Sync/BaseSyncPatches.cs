using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.Actions;
using UnityEngine;

namespace Multipleer.Harmony.Sync
{
    /// <summary>
    /// Relay interceptor for base construction START:
    /// <c>GeoPhoenixBase.ConstructFacility(PhoenixFacilityDef, Vector2Int, PhoenixBaseLayoutRotation)</c>
    /// (GeoPhoenixBase.cs:230). Client → relay + block; host → broadcast + run.
    /// </summary>
    [HarmonyPatch]
    public static class ConstructFacilityPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase");
            var defT = AccessTools.TypeByName("PhoenixPoint.Common.Entities.PhoenixFacilityDef");
            var rotT = AccessTools.TypeByName("PhoenixPoint.Common.Core.PhoenixBaseLayoutRotation");
            if (t == null || defT == null || rotT == null) return false;
            _target = AccessTools.Method(t, "ConstructFacility", new[] { defT, typeof(Vector2Int), rotT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = GeoPhoenixBase; facilityDef = PhoenixFacilityDef; position = Vector2Int; rotation = enum.
        public static bool Prefix(object __instance, object facilityDef, Vector2Int position, object rotation)
        {
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!PermissionGate.Check(ActionCategory.BaseConstruction))
            {
                PermissionGate.Notify(ActionCategory.BaseConstruction);
                return false;
            }

            try
            {
                string baseId = BaseReflection.GetBaseId(__instance);
                string defId = DefReflection.GetGuid(facilityDef);
                if (string.IsNullOrEmpty(baseId) || string.IsNullOrEmpty(defId)) return true;
                int rot = 0;
                try { rot = Convert.ToInt32(rotation); } catch { rot = 0; }
                var action = new ConstructFacilityAction(baseId, defId, position.x, position.y, rot);
                if (engine.IsHost) { engine.Sync.BroadcastHostAction(action); return true; }
                engine.Sync.SendActionRequest(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] ConstructFacilityPatch failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Relay interceptor for base facility REPAIR:
    /// <c>GeoPhoenixBase.RepairFacility(GeoPhoenixFacility)</c> (:263).
    /// </summary>
    [HarmonyPatch]
    public static class RepairFacilityPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase");
            var facT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility");
            if (t == null || facT == null) return false;
            _target = AccessTools.Method(t, "RepairFacility", new[] { facT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = GeoPhoenixBase; facility = GeoPhoenixFacility.
        public static bool Prefix(object __instance, object facility)
        {
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!PermissionGate.Check(ActionCategory.BaseRepair))
            {
                PermissionGate.Notify(ActionCategory.BaseRepair);
                return false;
            }

            try
            {
                string baseId = BaseReflection.GetBaseId(__instance);
                string facId = BaseReflection.GetFacilityId(facility);
                Vector2Int pos = BaseReflection.GetGridPosition(facility);
                if (string.IsNullOrEmpty(baseId)) return true;
                var action = new RepairFacilityAction(baseId, facId, pos.x, pos.y);
                if (engine.IsHost) { engine.Sync.BroadcastHostAction(action); return true; }
                engine.Sync.SendActionRequest(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] RepairFacilityPatch failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Relay interceptor for facility COMPLETION: <c>GeoPhoenixFacility.CompleteFacility()</c>
    /// (GeoPhoenixFacility.cs:347). Host → broadcast completion + run; client → suppress self-completion.
    /// </summary>
    [HarmonyPatch]
    public static class CompleteFacilityPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var facT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility");
            if (facT == null) return false;
            _target = AccessTools.Method(facT, "CompleteFacility", new Type[0]);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = GeoPhoenixFacility being completed.
        public static bool Prefix(object __instance)
        {
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!engine.IsHost) return false; // client: host drives completion

            try
            {
                var rt = GeoRuntime.Instance;
                var geoBase = BaseReflection.FindBaseOfFacility(rt, __instance);
                string baseId = BaseReflection.GetBaseId(geoBase);
                string facId = BaseReflection.GetFacilityId(__instance);
                Vector2Int pos = BaseReflection.GetGridPosition(__instance);
                if (!string.IsNullOrEmpty(baseId))
                    engine.Sync.BroadcastHostAction(new FacilityCompletedAction(baseId, facId, pos.x, pos.y));
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] CompleteFacilityPatch failed: " + ex.Message);
            }
            return true;
        }
    }
}
