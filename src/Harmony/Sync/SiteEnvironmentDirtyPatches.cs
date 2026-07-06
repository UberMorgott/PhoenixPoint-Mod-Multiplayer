using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// HOST-only channel #5 dirty hook for per-site WEATHER (audit gap 6f). The host re-rolls each site's
    /// weather once an in-game hour (<c>GeoLevelController</c>:894 → <c>GeoSite.DetermineWeather()</c> over
    /// <c>_updateSites</c>); the sim-frozen client never re-rolls, so its weather drifts after join. This
    /// CHANGE-GATED postfix marks the site's id dirty (<see cref="GeoSiteChannel.MarkSiteDirtyExternal"/>)
    /// only when the roll actually changed <c>_weather</c> — the hourly batch that leaves weather unchanged
    /// ships nothing — so ch#5 re-snapshots the changed site and its weather tail converges on the client.
    /// Pure observe (never blocks/mutates the native roll); client / single-player = no-op. Reflective target
    /// (Prepare false → PatchAll skips) so an engine rename never bombs.
    /// </summary>
    [HarmonyPatch]
    public static class SiteWeatherDirtyPatch
    {
        private static MethodBase _target;
        private static FieldInfo _weatherField;   // GeoSite._weather (GeoSiteWeather)

        public static bool Prepare()
        {
            var siteT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (siteT == null) return false;
            _target = AccessTools.Method(siteT, "DetermineWeather", Type.EmptyTypes);
            _weatherField = AccessTools.Field(siteT, "_weather");
            return _target != null && _weatherField != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Capture the pre-roll weather so the postfix can detect an actual change. __state = -1 means "not
        // captured" (not host / read failure) → the postfix no-ops.
        public static void Prefix(object __instance, out int __state)
        {
            __state = -1;
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                if (_weatherField != null) __state = Convert.ToInt32(_weatherField.GetValue(__instance));
            }
            catch { __state = -1; }
        }

        public static void Postfix(object __instance, int __state)
        {
            try
            {
                if (__state < 0) return;   // not host / not captured
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                int after = Convert.ToInt32(_weatherField.GetValue(__instance));
                if (after == __state) return;   // weather unchanged this roll → nothing to sync
                int siteId = GeoSiteReflection.GetSiteId(__instance);
                if (siteId >= 0) GeoSiteChannel.MarkSiteDirtyExternal(siteId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SiteWeatherDirtyPatch.Postfix failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// HOST-only channel #5 dirty hook for a site's EXPIRING-TIMER countdown (<c>GeoSite.ExpiringTimerAt</c>
    /// setter, GeoSite.cs:121). Armed by an expiring-encounter site or TFTV's pandoran base-attack countdown
    /// (TFTVBaseDefenseGeoscape) and cleared to Zero when it fires/expires; the sim-frozen client never
    /// derives it. Postfix on the setter marks the site dirty so ch#5 re-snapshots — its expiring-timer tail
    /// then arms/clears on the client (the countdown ticks natively under the freeze). The native setter
    /// already guards no-op sets internally; a stray re-dirty is harmless (coalesced). Pure observe;
    /// client / single-player = no-op. TFTV-absent (or no expiring encounter) → the setter is never called
    /// → this never fires. Reflective target (Prepare false → PatchAll skips).
    /// </summary>
    [HarmonyPatch]
    public static class SiteExpiringTimerDirtyPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var siteT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (siteT == null) return false;
            _target = AccessTools.PropertySetter(siteT, "ExpiringTimerAt");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                int siteId = GeoSiteReflection.GetSiteId(__instance);
                if (siteId >= 0) GeoSiteChannel.MarkSiteDirtyExternal(siteId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SiteExpiringTimerDirtyPatch.Postfix failed: " + ex.Message); }
        }
    }
}
