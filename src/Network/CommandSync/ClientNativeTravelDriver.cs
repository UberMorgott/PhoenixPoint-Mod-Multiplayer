using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // SWITCH-A (INC-3a render rework): on the CLIENT, drive the game's NATIVE GeoNavComponent.NavigateRoutine
    // to render a mirrored vehicle's travel — so position + in-plane nose heading + great-circle arc are
    // native BY CONSTRUCTION (fixes the JERK from ~2Hz pos snaps and the FLIES-SIDEWAYS from the never-set
    // Surface heading). This is RENDER driven by the host's mirrored travel INTENT, NOT client state
    // simulation. The host stays SOLE AUTHORITY: arrival / CurrentSite / occupancy still land only via the
    // existing ClientTravelEmitterSuppressPatch (set_Travelling / InitiateTravelling / OnArrived suppressed
    // on the client) + the discrete 0x35 transitions. The routine animates because:
    //   * StartTravel(List<GeoSite>) -> Navigation.Navigate -> StartNavigation schedules NavigateRoutine on
    //     the ActionComponent (NavigationComponent.cs:177-180) — NOT gated on the Travelling flag.
    //   * NavigateRoutine's loop is TIME-based (num = totalTime.Ratio01(startTime, Timing.Now),
    //     GeoNavComponent.cs:112), driven by the host-slaved clock; suppressing set_Travelling/OnArrived does
    //     not abort it (the suppress prefixes only skip those setter/method bodies).
    //   * NavigateRoutine is on the WHITELIST — NOT in GeoSimProducerTable, so ClientGeoSimSuppressPatch
    //     never stops it.
    // The mirror-driven StartTravel is invoked under EntityReplicationScope so StartTravelInterceptPatch.Prefix
    // recognizes a replicated apply (NOT a player input) and lets it execute without relaying to the host.
    //
    // While an identity is native-travelling:
    //   * GeoBridge.ApplyVehicleState SKIPS the Surface.position / Surface.rotation writes + RouteVehicleRender
    //     (the routine owns pos + heading) — continuous 0x35 SurfacePos is ignored, not snapped (kills the jerk).
    //   * ClientVehicleInterpolator.Tick SKIPS the identity (no double driver fighting the routine).
    // Parked / first-mirror placement + the ease fallback (for an unresolved destination) keep using the
    // interpolator exactly as before.
    internal static class ClientNativeTravelDriver
    {
        // Identities currently rendered by a native NavigateRoutine, mapped to the destination signature that
        // started/last-rerouted the routine (ordered site ids, comma-joined). Used to make StartTravel
        // IDEMPOTENT: only (re)start on entry or a genuine reroute (dest sig changed), never per snapshot.
        private static readonly Dictionary<(string, int), string> _nativeTravel =
            new Dictionary<(string, int), string>();

        // True while the identity's travel is being rendered by the native routine. Queried by
        // GeoBridge.ApplyVehicleState (skip pos/rot writes) and ClientVehicleInterpolator.Tick (skip ease).
        public static bool IsNativeTravelling((string, int) identity)
            => _nativeTravel.ContainsKey(identity);

        // Decision point — called from ClientGeoStateApplier.ApplyVehicleRecord for the resolved vehicle.
        // Drives the native routine off the mirrored intent in the record. PURE RENDER (no authority side
        // effects: the 3 emitters are suppressed; we only schedule/cancel the render coroutine). geoLevel is
        // the live GeoLevelController (already resolved by the caller).
        public static void OnRecord(object geoLevel, object vehicle, GeoVehicleStateRecord r)
        {
            var identity = (r.FactionGuid ?? "", r.VehicleID);
            int mask = r.ChangedMask;
            bool already = _nativeTravel.ContainsKey(identity);

            // ARRIVAL: Travelling cleared, or a CurrentSite was set. End native render so the normal mirror
            // placement (CurrentSite write + exact pos snap via the interpolator) takes over cleanly.
            bool arrived = ((mask & GeoStateMask.Travelling) != 0 && !r.Travelling)
                        || ((mask & GeoStateMask.CurrentSite) != 0 && r.CurrentSiteId >= 0);
            if (arrived)
            {
                if (already)
                {
                    GeoBridge.CancelVehicleNavigation(vehicle); // stop any lingering routine — no residual driver
                    _nativeTravel.Remove(identity);
                    Debug.Log($"[Multipleer] DIAGB native-travel: arrive {r.FactionGuid}#{r.VehicleID} site={r.CurrentSiteId}");
                }
                return;
            }

            // TRAVELLING / REROUTE / LATE-ARM:
            //   * start on the Travelling=true transition (mask carries the Travelling bit);
            //   * restart when the destination set changes mid-flight while already native (a discrete
            //     DestinationSites push);
            //   * LATE-ARM (finding #2): if the destination GeoSites were NOT yet synced on the client at the
            //     Travelling=true instant, BuildSitePath returned null and native mode never engaged. While the
            //     wire record STILL shows the craft travelling (r.Travelling true — the record carries this
            //     field on every push regardless of mask) and native mode is NOT yet active for this identity,
            //     a later push retries BuildSitePath and arms native render the moment the sites resolve.
            //     Otherwise the whole trip would render jerky via the fallback.
            bool travellingTrue = (mask & GeoStateMask.Travelling) != 0 && r.Travelling;
            bool destChanged = (mask & GeoStateMask.DestinationSites) != 0;
            bool lateArm = !already && r.Travelling;                  // in-flight per the wire, not yet native
            if (!travellingTrue && !(already && destChanged) && !lateArm) return; // not a travel/arm event

            string newSig = SignatureOf(r.DestinationSiteIds);
            if (string.IsNullOrEmpty(newSig)) return; // no destination ids -> nothing to drive (fall back)

            // Idempotence: already rendering this exact destination -> leave the routine running.
            if (already && _nativeTravel.TryGetValue(identity, out var curSig) && curSig == newSig) return;

            // Resolve the destination GeoSites; if any is not yet synced on the client, fall back to the
            // interpolator for now (do NOT mark native) — a later push self-heals once the sites arrive.
            var path = GeoBridge.BuildSitePath(geoLevel, ToStrings(r.DestinationSiteIds));
            if (path == null) return;

            // Drive the NATIVE routine. Under EntityReplicationScope so StartTravelInterceptPatch.Prefix lets
            // it execute (replicated apply, not a player input) instead of relaying to the host. StartTravel ->
            // Navigate internally CancelNavigation()s first, so this also handles a reroute restart.
            using (EntityReplicationScope.Enter())
            {
                GeoBridge.StartVehicleTravel(vehicle, path);
            }
            _nativeTravel[identity] = newSig;
            Debug.Log($"[Multipleer] DIAGB native-travel: {(already ? "reroute" : "start")} {r.FactionGuid}#{r.VehicleID} dest=[{newSig}]");
        }

        // 0x36 VehicleRemoved: drop native-travel tracking (the vehicle's native OnExitPlay cancels its own
        // routine). Safe if absent.
        public static void OnRemoved((string, int) identity)
        {
            if (_nativeTravel.Remove(identity))
                Debug.Log($"[Multipleer] DIAGB native-travel: cancel {identity.Item1}#{identity.Item2} (removed)");
        }

        // Session reset / teardown (NetworkEngine.Shutdown): clear all native-travel tracking.
        public static void Reset() => _nativeTravel.Clear();

        // Stable signature of an ordered destination id set (comma-joined). Empty for null/empty.
        private static string SignatureOf(int[] ids)
        {
            if (ids == null || ids.Length == 0) return "";
            return string.Join(",", System.Array.ConvertAll(ids, i => i.ToString()));
        }

        // int[] site ids -> string[] for GeoBridge.BuildSitePath (which keys GeoMap.AllSites by string SiteId).
        private static string[] ToStrings(int[] ids)
        {
            if (ids == null) return new string[0];
            return System.Array.ConvertAll(ids, i => i.ToString());
        }
    }
}
