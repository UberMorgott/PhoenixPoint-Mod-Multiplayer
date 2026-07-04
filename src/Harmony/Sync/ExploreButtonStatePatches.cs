using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// DELIBERATE UX improvement (co-op + single-player): grey out the "Explore point of interest" button while the
    /// vehicle is ALREADY exploring a site. Native PP leaves it lit — <c>ExploreSiteAbility.GetDisabledStateInternal</c>
    /// (ExploreSiteAbility.cs:18) only checks the actor + soldiers, never <c>IsExploringSite</c> — so the button stays
    /// clickable during an exploration (the click is a no-op via <c>ActivateInternal</c>'s <c>!IsExploringSite</c>
    /// guard, but it reads as enabled). This narrow Postfix forces it DISABLED while exploring; the reason maps to the
    /// authoritative source per side (HOST/single-player = native <c>IsExploringSite</c>; CLIENT = the mirrored 0xA7
    /// flag), the selection being the pure <see cref="ExploreDisableDecision"/>.
    ///
    /// Scope: ExploreSiteAbility ONLY (never any other ability). Only overrides <c>NotDisabled</c> → we never mask a
    /// real native disable reason. Reflective target (graceful skip if the engine type is absent). The re-enable when
    /// exploration finishes is driven by <see cref="ExploreAbilityRefreshPatch"/> (host) / the 0xA7 mirror (client),
    /// both via the native <c>AbilityStateChangeCheck</c> refresh — no custom UI redraw.
    /// </summary>
    [HarmonyPatch]
    public static class ExploreButtonDisablePatch
    {
        private static MethodBase _target;   // ExploreSiteAbility.GetDisabledStateInternal()

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Abilities.ExploreSiteAbility");
            _target = t != null ? AccessTools.Method(t, "GetDisabledStateInternal") : null;
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Runs after the native override. Only acts when the native result is NotDisabled AND the vehicle is exploring.
        public static void Postfix(ExploreSiteAbility __instance, ref GeoAbilityDisabledState __result)
        {
            if (__result != GeoAbilityDisabledState.NotDisabled) return;   // keep any real native disable reason
            try
            {
                if (!(__instance?.GeoActor is GeoVehicle vehicle)) return;

                var engine = NetworkEngine.Instance;
                bool onActiveClient = engine != null && engine.IsActiveSession && !engine.IsHost;

                // HOST / single-player source: native flag. CLIENT source: mirrored 0xA7 flag (read only on the client).
                bool hostNativeExploring = vehicle.IsExploringSite;
                bool clientMirrorExploring = false;
                if (onActiveClient && VehicleTravelReflection.TryReadVehicleKey(vehicle, out int ownerId, out int vehicleId))
                    clientMirrorExploring = GeoVehicleExploreMirror.IsExploringMirrored(GeoVehiclePos.MakeKey(ownerId, vehicleId));

                if (ExploreDisableDecision.ShouldDisable(onActiveClient, hostNativeExploring, clientMirrorExploring))
                    __result = GeoAbilityDisabledState.RequirementsNotMet;   // greys the button (generic, non-misleading reason)
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ExploreButtonDisablePatch failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Re-evaluate the vehicle's <c>ExploreSiteAbility</c> disabled-state at the exact moments exploration STARTS and
    /// ENDS, so <see cref="ExploreButtonDisablePatch"/>'s new verdict actually reaches the UI. Postfixes the two
    /// private choke methods <c>GeoVehicle.ExploreCurrentSite(TimeUnit,TimeUnit)</c> (start) and
    /// <c>GeoVehicle.EndExploreCurrentSite()</c> (end/complete/cancel) — the SAME methods the host runs natively AND
    /// the client mirror invokes when replaying host exploration state, so one hook covers both sides. Fires the
    /// native <c>AbilityStateChangeCheck</c> refresh (→ <c>OnAbilityStateChanged</c> →
    /// <c>UIStateVehicleSelected.UpdateVehicleActions</c>), never a custom redraw. Reflective targets (graceful skip).
    /// </summary>
    [HarmonyPatch]
    public static class ExploreAbilityRefreshPatch
    {
        public static bool Prepare()
            => AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle") != null;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            var vehT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            var timeUnit = AccessTools.TypeByName("Base.Core.TimeUnit");
            if (vehT == null) yield break;
            if (timeUnit != null)
            {
                var start = AccessTools.Method(vehT, "ExploreCurrentSite", new[] { timeUnit, timeUnit });
                if (start != null) yield return start;
            }
            var end = AccessTools.Method(vehT, "EndExploreCurrentSite");
            if (end != null) yield return end;
        }

        public static void Postfix(GeoVehicle __instance)
        {
            try { GeoVehicleExploreReflection.RefreshExploreAbilityState(__instance); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ExploreAbilityRefreshPatch failed: " + ex.Message); }
        }
    }
}
