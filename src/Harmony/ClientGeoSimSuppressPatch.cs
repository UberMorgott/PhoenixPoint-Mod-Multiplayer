using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.CommandSync;

namespace Multiplayer.Harmony
{
    // Inc1 Task A: make the CLIENT geoscape engine inert. Prefixes the CLOSED 13-producer set
    // (GeoSimProducerTable) of `private NextUpdate <Method>(Timing)` callbacks. On a client in an active
    // session the prefix sets __result = NextUpdate.Never and returns false: the scheduler then Stops the
    // updateable, permanently de-registering that producer on the client -> ZERO local stochastic/clock-
    // driven authoritative sim (this is what was rolling the client's OWN GeoSite.EncounterID and making
    // host/client show DIFFERENT events). The client instead mirrors host geoscape state via the existing
    // save-blob snapshot on join. Host / single-player run normally. Best-effort: a missed producer is
    // bounded jitter, self-healed by the host snapshot. Travel suppression is OUT OF SCOPE here (deferred).
    //
    // One [HarmonyPatch] class, many targets via TargetMethods() (Harmony multi-target single-prefix).
    // Game types are NEVER hard-referenced: targets resolve via AccessTools.TypeByName/Method (disambiguated
    // by the single Base.Core.Timing param), and __result is typed `ref object` (boxed NextUpdate).
    [HarmonyPatch]
    public static class ClientGeoSimSuppressPatch
    {
        // Boxed NextUpdate.Never, resolved once in Prepare(); assigned into __result to stop each producer.
        private static object _never;

        public static bool Prepare()
        {
            var nextUpdateType = AccessTools.TypeByName("Base.Core.NextUpdate");
            if (nextUpdateType == null) return false; // engine not loaded -> Harmony skips this class
            _never = AccessTools.Field(nextUpdateType, "Never")?.GetValue(null);
            return _never != null;
        }

        // Resolve every producer's NextUpdate(Timing) callback. Skip (do not yield) any that fail to resolve
        // so an absent/renamed engine method never crashes PatchAll -- best-effort by design.
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var timingType = AccessTools.TypeByName("Base.Core.Timing");
            if (timingType == null) yield break;
            foreach (var p in GeoSimProducerTable.Producers)
            {
                var t = AccessTools.TypeByName(p.DeclaringTypeName);
                if (t == null) continue;
                // Pin the Timing-param overload (e.g. GeoAlienBase.ExpandAlienBase(Timing) vs ...(IConsole)).
                var m = AccessTools.Method(t, p.MethodName, new[] { timingType });
                if (m != null) yield return m;
            }
        }

        // __result : ref object (the producer returns Base.Core.NextUpdate, which the mod never references).
        // Returning false skips the heavy producer body; __result = NextUpdate.Never makes the scheduler Stop
        // the updateable so it never re-fires on the client. Host / SP -> run the body normally.
        public static bool Prefix(ref object __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true; // single player / no session: simulate normally
            if (engine.IsHost) return true;                      // host is the sole authoritative simulator

            __result = _never;                                   // client: stop the producer, no local sim
            return false;
        }
    }
}
