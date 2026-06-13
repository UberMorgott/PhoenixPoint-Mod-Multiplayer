using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
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
    // GATE (T1): these are sim SIDE-EFFECTS, not commands, so the client must never produce them SPONTANEOUSLY
    // (a client NavigateRoutine / native travel emit). Suppress on a client EXCEPT inside the 0x35 mirror apply.
    //
    // INC-D CARVE-OUT (P2 travel-line RCA): the line "куда летит" is drawn natively by
    // GeoscapeView.DrawVehiclePathLinks, which gates on vehicle.DestinationSites.Count>0 (always-on viewer path,
    // GeoscapeView.cs:1502/1512) and, for the SELECTED craft, additionally on Travelling==true
    // (UIStateVehicleSelected.DrawCurrentPath, :484). The client mirror (ClientGeoStateApplier, under
    // EntityReplicationScope.Enter) calls the Travelling SETTER to write the host's authoritative Travelling
    // value (0x35 mask Travelling8). With the old UNCONDITIONAL suppress that setter was BLOCKED, so the
    // mirrored craft's Travelling stayed false forever (live log: DIAGB read-back Travelling=False on every
    // apply, even on mask=56/59 records that carried Travelling8) → the native selected-path line never lit on
    // the client. FIX: let the setter run WHEN EntityReplicationScope.IsApplying (our mirror write only). The
    // setter merely flips _traveling and, on a departure, detaches CurrentSite (GeoVehicle.cs:201-219) — it
    // does NOT spawn NavigateRoutine, so the single-writer / no-client-sim invariant is preserved; the applier
    // sets Travelling THEN CurrentSite (mask order) so an arrival record re-attaches the site after the detach.
    // NavigateRoutine's spontaneous set_Travelling runs OUTSIDE the mirror scope and stays suppressed.
    // (InitiateTravelling / OnArrived are never invoked under IsApplying — the applier touches only the property
    // setters — so the shared carve-out cannot fire their events/anim; they remain fully suppressed in practice.)
    // The host owns CurrentSite/site occupancy and pushes them via 0x36/0x35; DestinationSites are resolved to
    // real client GeoSite objects in GeoBridge.ApplyVehicleState (mask DestinationSites32) so the always-on path
    // line also lights once the destination sites are synced.
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
        // INC-D carve-out (P2 travel-line): a write made by the 0x35 state mirror (EntityReplicationScope.IsApplying)
        // is the host's authoritative value, not a local emit — let it through so the mirrored Travelling/CurrentSite
        // land and the native travel-line renderer lights. The carve-out is NARROWED to the Travelling SETTER only
        // (via Harmony's __originalMethod): that is the sole emitter the applier actually invokes under IsApplying,
        // and its only side-effect is detaching CurrentSite (no routine, no events). InitiateTravelling / OnArrived
        // stay UNCONDITIONALLY suppressed even under IsApplying — they fire TravelStartedEvent/ArrivedEvent + anim
        // that the client must never produce; the applier never calls them today, and pinning the carve-out to the
        // setter keeps that guarantee robust against any future apply-path change. NavigateRoutine's own spontaneous
        // emits run outside the mirror scope and stay suppressed for all three.
        public static bool Prefix(MethodBase __originalMethod)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true; // single player
            if (engine.IsHost) return true;                      // host emits authoritatively
            // Only the Travelling property setter is un-suppressed during a mirror apply (P2). A property setter's
            // reflected name is "set_Travelling"; InitiateTravelling/OnArrived never match, so they stay suppressed.
            if (EntityReplicationScope.IsApplying && __originalMethod?.Name == "set_Travelling")
                return true;                                     // 0x35 mirror: write host's authoritative Travelling
            return false;                                        // client: suppress (render-only)
        }
    }
}
