using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
using UnityEngine;

namespace Multipleer.Harmony
{
    // DIAG-NUM (instrumentation ONLY — no behavior change): the native travel routine
    // GeoNavComponent.NavigateRoutine (decompile GeoNavComponent.cs:94-151) is a compiler-generated
    // iterator. Its per-tick body advances the craft by num = totalTime.Ratio01(startTime, Timing.Now)
    // (cs:112) then Slerp(start,end,num) (cs:117). DIAG-NAV proved a frozen PLAYER craft has posDelta=0 /
    // rangeDelta=0 (num≈0) while AI crafts move — but the player craft's child Timing.Now DOES rise. To see
    // the routine's internals we POSTFIX the iterator state-machine's MoveNext (the per-yield tick body).
    //
    // Iterator nested type + captured-field names verified via ilspycmd on the live game DLL:
    //   PhoenixPoint.Geoscape.Entities.GeoNavComponent/<NavigateRoutine>d__13
    //     .field public  GeoNavComponent  '<>4__this'        (the component instance)
    //     .field private TimeUnit         '<totalTime>5__6'  (segment travel duration)
    //     .field private TimeUnit         '<startTime>5__7'  (captured Timing.Now at segment start)
    //   (num is a per-tick LOCAL — not a captured field — so we RECOMPUTE it from the captured fields with the
    //    exact native formula totalTime.Ratio01(startTime, this.NavActor.Actor.Timing.Now).)
    // The nested type's index (d__13) may drift across game patches, so we resolve it by NAME-CONTAINS
    // "NavigateRoutine" among GeoNavComponent's nested types, then take its MoveNext — version-resilient and
    // equivalent to HarmonyLib AccessTools.EnumeratorMoveNext (not exposed in this Harmony 2.2 build).
    [HarmonyPatch]
    public static class NavRoutineNumProbePatch
    {
        private static Type _navComp;
        private static FieldInfo _fThis;       // <>4__this   -> GeoNavComponent
        private static FieldInfo _fStartTime;  // <startTime>5__7
        private static FieldInfo _fTotalTime;  // <totalTime>5__6
        private static FieldInfo _fNavActor;   // GeoNavComponent.NavActor (INavigatableGeoActor)
        private static MethodInfo _ratio01;    // TimeUnit.Ratio01(TimeUnit start, TimeUnit now)

        // Throttle ~1/sec per vehicle id.
        private static readonly Dictionary<string, float> _nextLog = new Dictionary<string, float>();

        public static bool Prepare()
        {
            _navComp = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoNavComponent");
            return _navComp != null;
        }

        // Resolve the iterator MoveNext by locating GeoNavComponent's nested type whose name contains
        // "NavigateRoutine" (the <NavigateRoutine>d__NN state machine), then its MoveNext.
        public static MethodBase TargetMethod()
        {
            if (_navComp == null) return null;
            foreach (var nt in _navComp.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (nt.Name.IndexOf("NavigateRoutine", StringComparison.Ordinal) < 0) continue;
                var mn = AccessTools.Method(nt, "MoveNext");
                if (mn == null) continue;
                // Cache the captured-field handles off the SAME nested type.
                _fThis = AccessTools.Field(nt, "<>4__this");
                _fStartTime = AccessTools.Field(nt, "<startTime>5__7");
                _fTotalTime = AccessTools.Field(nt, "<totalTime>5__6");
                _fNavActor = AccessTools.Field(_navComp, "NavActor");
                var timeUnitType = AccessTools.TypeByName("Base.Core.TimeUnit");
                if (timeUnitType != null)
                    _ratio01 = AccessTools.Method(timeUnitType, "Ratio01", new[] { timeUnitType, timeUnitType });
                return mn;
            }
            return null;
        }

        // Postfix the per-tick MoveNext: read captured startTime/totalTime, the live child Timing.Now,
        // recompute num, and log per travelling geoscape vehicle (player vs AI). Pure read; never throws.
        public static void Postfix(object __instance, bool __result)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || engine.IsHost) return; // client-only (cut host noise)
                if (__instance == null || _fThis == null) return;

                var navComp = _fThis.GetValue(__instance);                  // GeoNavComponent
                if (navComp == null) return;
                var navActor = _fNavActor?.GetValue(navComp);               // INavigatableGeoActor == GeoVehicle
                if (navActor == null) return;

                // Only travelling geoscape vehicles are interesting.
                var travelling = AccessTools.Property(navActor.GetType(), "Travelling")?.GetValue(navActor);
                if (!(travelling is bool tb && tb)) return;

                // Vehicle id + def name (player PP/NA_Manticore vs AI NJ/SYN/ANU) via the cached GeoBridge helpers.
                string vid = GeoBridge.VehicleId(navActor);
                float now = Time.realtimeSinceStartup;
                _nextLog.TryGetValue(vid, out var next);
                if (now < next) return;
                _nextLog[vid] = now + 1.0f;
                string def = GeoBridge.VehicleDefNameOf(navActor);

                // Captured iterator fields (boxed TimeUnit structs).
                var startTimeObj = _fStartTime?.GetValue(__instance);
                var totalTimeObj = _fTotalTime?.GetValue(__instance);

                // Live child Timing.Now = NavActor.Actor.Timing.Now.
                var actor = AccessTools.Property(navActor.GetType(), "Actor")?.GetValue(navActor) ?? navActor;
                var timing = actor != null ? AccessTools.Property(actor.GetType(), "Timing")?.GetValue(actor) : null;
                var nowObj = timing != null ? AccessTools.Property(timing.GetType(), "Now")?.GetValue(timing) : null;

                // Recompute num with the EXACT native formula: totalTime.Ratio01(startTime, Timing.Now).
                float num = -1f;
                if (_ratio01 != null && totalTimeObj != null && startTimeObj != null && nowObj != null)
                {
                    var r = _ratio01.Invoke(totalTimeObj, new[] { startTimeObj, nowObj });
                    if (r is float rf) num = rf;
                }

                long startTicks = TicksOf(startTimeObj), totalTicks = TicksOf(totalTimeObj), nowTicks = TicksOf(nowObj);

                UnityEngine.Debug.Log($"[Multipleer] DIAG-NUM veh#{vid} def={def} moveNext={__result} " +
                    $"startTime={startTicks} now={nowTicks} totalTime={totalTicks} " +
                    $"nowMinusStart={nowTicks - startTicks} num={num:F4}");
            }
            catch { /* never throw from a DIAG postfix */ }
        }

        // TimeUnit -> ticks via the public TimeSpan getter (0 on any null).
        private static long TicksOf(object timeUnit)
        {
            if (timeUnit == null) return 0L;
            var ts = AccessTools.Property(timeUnit.GetType(), "TimeSpan")?.GetValue(timeUnit);
            return ts is TimeSpan t ? t.Ticks : 0L;
        }
    }
}
