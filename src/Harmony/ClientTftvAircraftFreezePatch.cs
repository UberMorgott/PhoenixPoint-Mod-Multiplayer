using System.Reflection;
using HarmonyLib;
using Multipleer.Network;

namespace Multipleer.Harmony
{
    // Inc1 Task C: CLIENT-ONLY, defensive freeze of TFTV's "Aircraft Rework" hourly maintenance.
    //
    // With TFTV installed, TFTV.AircraftReworkSpeedAndRange.AdjustAircraftSpeed(GeoVehicle, bool) is the
    // SINGLE choke that mutates a GeoVehicle's speed and re-navigates its path: it calls ResetSpeed ->
    // sets geoVehicle.Stats.Speed.Value AND TryRefreshNavigation -> geoVehicle.Navigation.Navigate(path).
    // (The roadmap/spec cited AircraftReworkMaintenance.cs:377/384, but that block is dead commented-out
    // code — AdjustAircraftSpeed is the live, sole speed+re-navigate mutation point.)
    //
    // On a co-op CLIENT this self-adjustment fights host-authoritative state: the client must MIRROR the
    // host's speed/path (delivered by the host snapshot/diff), not recompute its own. So on an active-session
    // client this Prefix returns false, skipping the whole pass. Host / single-player run TFTV normally.
    //
    // TFTV is NEVER hard-referenced: the type/method resolve via AccessTools reflection. When TFTV is absent
    // Prepare() returns false and Harmony skips the patch entirely (zero impact). Harmony auto-registers this
    // class through MultipleerMain's PatchAll.
    [HarmonyPatch]
    public static class ClientTftvAircraftFreezePatch
    {
        // Resolved once in Prepare(); used by TargetMethod(). TFTV's namespace is "TFTV" (folder is
        // TFTVAircraftRework but the C# `namespace TFTV` differs — confirmed against the real source).
        private static System.Type _tftvType;

        public static bool Prepare()
        {
            _tftvType = AccessTools.TypeByName("TFTV.AircraftReworkSpeedAndRange");
            return _tftvType != null; // TFTV not loaded -> Harmony skips this class
        }

        // Pin the exact overload: AdjustAircraftSpeed(GeoVehicle geoVehicle, bool forceTimer).
        // GeoVehicle = PhoenixPoint.Geoscape.Entities.GeoVehicle (resolved reflectively, never referenced).
        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null; // defensive: TFTV absent -> no target (mirrors house pattern)
            var geoVehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            if (geoVehicleType == null) return null;
            return AccessTools.Method(_tftvType, "AdjustAircraftSpeed",
                new[] { geoVehicleType, typeof(bool) });
        }

        // Returning false SKIPS TFTV's speed-set + re-Navigate pass; true lets it run. Suppress only on an
        // active-session client — host / single-player simulate normally.
        public static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            return ClientTftvAircraftFreezeGate.ShouldRunTftvNormally(
                engineExists: engine != null,
                isActive: engine != null && engine.IsActive,
                isHost: engine != null && engine.IsHost);
        }
    }
}
