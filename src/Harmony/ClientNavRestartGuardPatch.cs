using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
using UnityEngine;

namespace Multipleer.Harmony
{
    // CLIENT NAV-RESTART GUARD (defensive TFTV-compat fix; instrumentation marker DIAG-NAVGUARD).
    //
    // ROOT CAUSE: TFTV's AircraftReworkSpeedAndRange.TryRefreshNavigation (refs/TFTV-src/.../
    // AircraftReworkSpeedAndRange.cs:524, driven from AdjustAircraftSpeed ~:274 via the agenda UI) calls
    // geoVehicle.Navigation.Navigate(path) on a PLAYER craft when its reworked-speed check fails. The check
    // fails on the CLIENT because firstMirror ProcessInstanceData (GeoBridge.ApplyVehicleStateFull) re-clones
    // BaseStats and resets the reworked speed. GeoNavComponent.Navigate(List<Vector3>) (decompile
    // GeoNavComponent.cs:163) is CancelNavigation()+CalculatePath()+StartNavigation() — it KILLS the running
    // NavigateRoutine and starts a fresh one whose captured startTime/totalTime are re-zeroed, so DIAG-NUM
    // showed num=Ratio01(now,0)=1.0 (instant-complete) every tick -> the craft freezes at the departure cell
    // on the client. AI crafts are never in the player agenda, so TFTV never re-Navigates them (they move).
    //
    // FIX: prefix GeoNavComponent.Navigate(List<Vector3>) and SKIP (return false = do NOT run the original,
    // leaving the live routine intact) ONLY a REDUNDANT client-side restart, i.e. when ALL hold:
    //   (a) active MP session AND we are the CLIENT  (NetworkEngine.Instance.IsActive && !IsHost),
    //   (b) the craft ALREADY has a live running routine (NavigationComponent.IsNavigating == CurrentAction!=null,
    //       decompile NavigationComponent.cs:35) — so initial starts are NOT blocked, only restarts,
    //   (c) NOT inside a host-authoritative apply (CommandRelay.IsApplying==false &&
    //       EntityReplicationScope.IsApplying==false) — so the legit ApplyStartTravel REPLAY (which runs the
    //       initial Navigate under CommandRelay.IsApplying) and any 0x35 mirror apply still go through.
    // Initial StartTravel replay: IsApplying=true -> allowed. Arrival flips Travelling via set_Travelling (no
    // Navigate) -> unaffected. AI crafts: Navigate runs at level start before the session is active -> allowed.
    // Single-player: no active session -> allowed. So this blocks ONLY TFTV's spurious local re-Navigate.
    //
    // Chosen target = the GeoVehicle-specific Navigate(List<Vector3>) override (the exact method TFTV calls),
    // NOT the deeper StartNavigation/CancelNavigation (those are also hit by the legit initial start and by
    // other nav paths) — keeping the guard surgical to the redundant-restart entry.
    [HarmonyPatch]
    public static class ClientNavRestartGuardPatch
    {
        private static Type _navComp;
        private static PropertyInfo _isNavigating; // NavigationComponent.IsNavigating (bool)
        private static FieldInfo _navActor;        // GeoNavComponent.NavActor (the GeoVehicle)

        private static readonly Dictionary<string, float> _nextLog = new Dictionary<string, float>();

        public static bool Prepare()
        {
            _navComp = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoNavComponent");
            return _navComp != null;
        }

        // Pin the List<Vector3> overload (GeoNavComponent.Navigate(List<Vector3>), the TFTV entry point) —
        // disambiguated from any other Navigate overload by the param type.
        public static MethodBase TargetMethod()
        {
            if (_navComp == null) return null;
            _isNavigating = AccessTools.Property(_navComp, "IsNavigating");
            _navActor = AccessTools.Field(_navComp, "NavActor");
            return AccessTools.Method(_navComp, "Navigate", new[] { typeof(List<Vector3>) });
        }

        // Return false => skip the original Navigate (keep the live routine). Return true => run it normally.
        public static bool Prefix(object __instance)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive) return true; // single player: normal
                if (engine.IsHost) return true;                      // host owns travel authoritatively

                // (c) host-authoritative apply (initial StartTravel replay / 0x35 mirror) must go through.
                if (CommandRelay.IsApplying || EntityReplicationScope.IsApplying) return true;

                // (b) only block a REDUNDANT restart — the craft must already have a live running routine.
                bool isNavigating = _isNavigating?.GetValue(__instance) is bool b && b;
                if (!isNavigating) return true; // no live routine -> this is a legit initial start, allow it

                // All conditions met: this is TFTV's spurious client-side re-Navigate on a travelling craft.
                // Throttled DIAG so we can confirm the guard fires.
                string vid = "?";
                try { var nav = _navActor?.GetValue(__instance); if (nav != null) vid = GeoBridge.VehicleId(nav); }
                catch { /* id best-effort */ }
                float now = Time.realtimeSinceStartup;
                _nextLog.TryGetValue(vid, out var next);
                if (now >= next)
                {
                    _nextLog[vid] = now + 1.0f;
                    UnityEngine.Debug.Log($"[Multipleer] DIAG-NAVGUARD blocked veh={vid} (redundant client re-Navigate on live routine)");
                }
                return false; // block: keep the running NavigateRoutine alive
            }
            catch
            {
                return true; // never break native travel on a guard error
            }
        }
    }
}
