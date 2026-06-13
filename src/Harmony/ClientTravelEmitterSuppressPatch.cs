using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using UnityEngine;

namespace Multipleer.Harmony
{
    // SD-AIDR INC-1 (B): on the CLIENT, suppress the THREE authoritative travel emitters that
    // GeoNavComponent.NavigateRoutine (whitelisted renderer) calls inline on its GeoVehicle (NavActor):
    //   1. set_Travelling(bool) -> CurrentSite.VehicleLeft(this) + CurrentSite = null (GeoVehicle.cs:212-216)
    //   2. InitiateTravelling()  -> TravelStartedEvent?.Invoke (GeoVehicle.cs:587-591)  [+ cosmetic anim]
    //   3. OnArrived(Vector3,bool) -> CurrentSite assign + _destinationSites pop + VehicleArrived +
    //                                 ArrivedAtDestinationEvent (GeoVehicle.cs:315-338)  [+ cosmetic anim]
    // NavigateRoutine's pure render (Slerp pos/rot/RangeRemaining) is NOT patched and keeps running on the
    // slaved clock -> the client ship flies in sync; only the host-owned authoritative emitters are dropped.
    //
    // GATE (T1, stricter than the CommandSync intercepts): these are sim SIDE-EFFECTS, not commands, so the
    // client must NEVER produce them -- suppress UNCONDITIONALLY on a client (NO IsApplying carve-out). The
    // host owns CurrentSite/site occupancy and pushes them via 0x36/0x35 in INC 2/3. INC-1 consequence:
    // client CurrentSite/_traveling stay stale until the diff lands (documented INC-1 boundary); the
    // cosmetic Animator state is also suppressed (self-correcting, accepted).
    [HarmonyPatch]
    public static class ClientTravelEmitterSuppressPatch
    {
        public static bool Prepare()
        {
            return AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle") != null;
        }

        // Yield the three GeoVehicle emitter seams; skip any that fail to resolve (best-effort).
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var gv = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            if (gv == null) yield break;

            // 1. Travelling setter (the side-effects, not the field write per se).
            var setTravelling = AccessTools.PropertySetter(gv, "Travelling");
            if (setTravelling != null) yield return setTravelling;

            // 2. InitiateTravelling() - public, no params.
            var initiate = AccessTools.Method(gv, "InitiateTravelling", Type.EmptyTypes);
            if (initiate != null) yield return initiate;

            // 3. OnArrived(Vector3, bool) - private; pin the param types.
            var onArrived = AccessTools.Method(gv, "OnArrived",
                new[] { typeof(UnityEngine.Vector3), typeof(bool) });
            if (onArrived != null) yield return onArrived;
        }

        // Client in an active session -> return false (suppress the authoritative emitter). Host / SP run it.
        // No IsApplying carve-out: the client must never emit travel authority locally (INC-1 design T1).
        public static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true; // single player
            if (engine.IsHost) return true;                      // host emits authoritatively
            return false;                                        // client: suppress (render-only)
        }
    }
}
