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
    /// Relay interceptor for geoscape SITE EXPLORATION: <c>GeoVehicle.StartExploringCurrentSite()</c>
    /// (GeoVehicle.cs:414) — the choke <c>ExploreSiteAbility.ActivateInternal</c> (:9) flows through when the
    /// player clicks "Explore point of interest". On a co-op CLIENT the sim is frozen (Inc4 S1: Timing.Paused),
    /// so a local StartExploringCurrentSite schedules its exploration timer on the paused geo Timing (never
    /// fires) and never reaches the host → the order silently died (same gap class as travel / MoveVehiclePatch).
    /// So: CLIENT → relay the intent (<see cref="ExploreSiteAction"/>) + BLOCK the local order; HOST → run the
    /// authoritative timed exploration + broadcast. The outcome (site reveal / encounter on completion) mirrors
    /// via the existing geoscape event replication — the client never simulates.
    ///
    /// Game types are NEVER hard-referenced: the target resolves via AccessTools reflection; Prepare() returns
    /// false (Harmony skips the patch) when the engine type is absent. Direct clone of <c>MoveVehiclePatch</c>
    /// (StartExploringCurrentSite is no-arg — it explores the vehicle's own CurrentSite — so no path to read).
    /// </summary>
    [HarmonyPatch]
    public static class ExploreSitePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var vehT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            if (vehT == null) return false;
            _target = AccessTools.Method(vehT, "StartExploringCurrentSite");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = GeoVehicle. __state carries the host action to broadcast AFTER the original succeeds (Postfix).
        public static bool Prefix(object __instance, out ISyncedAction __state)
        {
            __state = null;
            if (SyncApplyScope.IsApplying) return true;   // engine-driven host apply/replay → run the real order
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;   // single-player / no session

            if (!PermissionGate.Check(ActionCategory.VehicleTravel))
            {
                PermissionGate.Notify(ActionCategory.VehicleTravel);
                return false;
            }

            try
            {
                // No path arg triggers the reflection bind (unlike StartTravel), so force it before reading the key.
                VehicleTravelReflection.EnsureBound(GeoRuntime.Instance);
                if (!VehicleTravelReflection.TryReadVehicleKey(__instance, out int ownerId, out int vehicleId))
                    return true;   // unreadable key → don't block a native order we can't relay

                var action = new ExploreSiteAction(ownerId, vehicleId);
                // Host: defer the broadcast to the Postfix so a throwing original suppresses it (no desync).
                if (engine.IsHost) { __state = action; return true; }
                engine.Sync.SendActionRequest(action);
                return false;   // client: block the local (frozen) order; host executes + reveal mirrors back
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] ExploreSitePatch failed: " + ex.Message);
                return true;
            }
        }

        // Host-only (via __state) and only on a normal return of the original → broadcast the confirmed order.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try { NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ExploreSitePatch postfix broadcast failed: " + ex.Message); }
        }
    }
}
