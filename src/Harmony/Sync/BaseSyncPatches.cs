using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
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
        // __state carries the host action to broadcast AFTER the original succeeds (Postfix).
        public static bool Prefix(object __instance, object facilityDef, Vector2Int position, object rotation, out ISyncedAction __state)
        {
            __state = null;
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
                // Host: defer the broadcast to the Postfix so a throwing original suppresses it (no desync).
                if (engine.IsHost) { __state = action; return true; }
                engine.Sync.SendActionRequest(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] ConstructFacilityPatch failed: " + ex.Message);
                return true;
            }
        }

        // Host-only (via __state) and only on a normal return of the original → broadcast the confirmed construct.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try { NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ConstructFacilityPatch postfix broadcast failed: " + ex.Message); }
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
        // __state carries the host action to broadcast AFTER the original succeeds (Postfix).
        public static bool Prefix(object __instance, object facility, out ISyncedAction __state)
        {
            __state = null;
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
                // Host: defer the broadcast to the Postfix so a throwing original suppresses it (no desync).
                if (engine.IsHost) { __state = action; return true; }
                engine.Sync.SendActionRequest(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] RepairFacilityPatch failed: " + ex.Message);
                return true;
            }
        }

        // Host-only (via __state) and only on a normal return of the original → broadcast the confirmed repair.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try { NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RepairFacilityPatch postfix broadcast failed: " + ex.Message); }
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
        // __state carries the completion action snapshotted in Prefix; broadcast in Postfix only if the
        // original CompleteFacility returns normally (a thrown original skips the Postfix → no false echo).
        public static bool Prefix(object __instance, out ISyncedAction __state)
        {
            __state = null;
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
                    __state = new FacilityCompletedAction(baseId, facId, pos.x, pos.y);   // broadcast on success
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] CompleteFacilityPatch failed: " + ex.Message);
            }
            return true;
        }

        // Host-only (via __state) and only on a normal return of the original → broadcast the confirmed completion.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try
            {
                var sync = NetworkEngine.Instance?.Sync;
                sync?.BroadcastHostAction(__state);
                // A completed facility (construction OR repair) changes the faction's hourly research
                // production the moment it turns Functioning (a new/repaired lab starts producing). The ch2
                // snapshot carries that rate (v3 block), but its next scheduled send is the HOURLY heartbeat
                // — mark ch2 dirty now so the client's research ETA refreshes instantly instead (coalesced
                // flush; mirrors the AddResearchToQueuePatch postfix precedent). Facility DAMAGE is not
                // hooked — the hourly heartbeat converges that within the hour.
                sync?.MarkChannelDirty(2);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] CompleteFacilityPatch postfix broadcast failed: " + ex.Message); }
        }
    }
}
