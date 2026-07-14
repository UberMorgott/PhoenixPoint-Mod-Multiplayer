using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Relay interceptor for geoscape vehicle TRAVEL: <c>GeoVehicle.StartTravel(List&lt;GeoSite&gt;)</c>
    /// (GeoVehicle.cs:518) — the single choke every player-facing order flows through
    /// (MoveVehicleAbility.ActivateInternal:63 via right-click AddTravelSite / context-menu / chained travel;
    /// TravelTo:556). On a co-op CLIENT the sim is frozen (Inc4 S1: Timing.Paused), so a local StartTravel
    /// neither advances (the paused NavigateRoutine never runs) nor reaches the host → the order silently died
    /// (Symptom A). So: CLIENT → relay the intent (<see cref="MoveVehicleAction"/>) + BLOCK the local order;
    /// HOST → run the authoritative order + broadcast. The resulting motion mirrors on 0xA5 (position) and the
    /// route-line metadata on 0xA6 — the client never simulates.
    ///
    /// Game types are NEVER hard-referenced: the target resolves via AccessTools reflection; Prepare() returns
    /// false (Harmony skips the patch) when the engine type is absent. Mirrors <c>ConstructFacilityPatch</c>.
    /// </summary>
    [HarmonyPatch]
    public static class MoveVehiclePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var vehT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            var siteT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (vehT == null || siteT == null) return false;
            var listOfSite = typeof(List<>).MakeGenericType(siteT);
            // Exact param match pins StartTravel(List<GeoSite>) over the StartTravel(List<Vector3>) overload.
            _target = AccessTools.Method(vehT, "StartTravel", new[] { listOfSite });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = GeoVehicle; path = List<GeoSite> destinations. __state carries the host action to
        // broadcast AFTER the original succeeds (Postfix).
        public static bool Prefix(object __instance, object path, out ISyncedAction __state)
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
                var rt = GeoRuntime.Instance;
                int[] destSiteIds = VehicleTravelReflection.ReadPathSiteIds(rt, path);   // Ensures binding first
                if (destSiteIds == null || destSiteIds.Length == 0) return true;         // nothing to relay → run local
                if (!VehicleTravelReflection.TryReadVehicleKey(__instance, out int ownerId, out int vehicleId))
                    return true;   // unreadable key → don't block a native order we can't relay

                var action = new MoveVehicleAction(ownerId, vehicleId, destSiteIds);
                // Host: defer the broadcast to the Postfix so a throwing original suppresses it (no desync).
                if (engine.IsHost)
                {
                    // Co-op brief-on-all fix: tag this HOST-OWN travel's final destination so the arrival mission
                    // brief is shown locally only (the host is authoritative) and mirrored to no client — vs a
                    // relayed client travel, tagged with its peer in SyncEngine.OnActionRequest. A relayed order
                    // never reaches here (IsApplying short-circuits at the top), so this branch is host-own only.
                    Multiplayer.Network.Sync.State.VehicleTravelInitiator.Record(
                        destSiteIds[destSiteIds.Length - 1],
                        Multiplayer.Network.Sync.State.VehicleTravelInitiator.HostSelf);
                    __state = action; return true;
                }
                engine.Sync.SendActionRequest(action);
                return false;   // client: block the local (frozen) order; host executes + mirrors back
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] MoveVehiclePatch failed: " + ex.Message);
                return true;
            }
        }

        // Host-only (via __state) and only on a normal return of the original → broadcast the confirmed order.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try { NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] MoveVehiclePatch postfix broadcast failed: " + ex.Message); }
        }
    }
}
