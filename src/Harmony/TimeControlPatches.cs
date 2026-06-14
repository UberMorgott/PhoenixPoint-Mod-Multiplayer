using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.TimeSync;
using UnityEngine;

namespace Multipleer.Harmony
{
    /// <summary>
    /// Client-side time-control INPUT intercept: when a NON-host player clicks pause/play on the
    /// geoscape, route the request to the host and block the local commit (mirrors the IsHost-gated
    /// TacticalPatches prefix → SendToHost → return false pattern). The host applies last-writer-wins
    /// and broadcasts the authoritative TimeState back to all.
    ///
    /// Target = UIModuleTimeControl.OnPauseTime(bool) — the single private funnel behind the pause
    /// button (and SetTimeState), so it catches user clicks but is bypassed by our own programmatic
    /// remote-apply (which writes Timing.Paused directly, never through OnPauseTime).
    /// </summary>
    [HarmonyPatch]
    public static class TimeControlPausePatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl");
            if (_targetType == null) return false;
            _targetMethod = AccessTools.Method(_targetType, "OnPauseTime", new[] { typeof(bool) });
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        // pause = the requested paused state (OnPauseTime's bool arg).
        public static bool Prefix(bool pause)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true; // no session → local
            if (engine.IsHost) return true;                      // host commits locally
            if (TimeSyncManager.IsApplyingRemote) return true;   // our own remote-apply → let through

            try
            {
                var ts = engine.TimeSync;
                if (ts == null) return true;
                bool relayed = ts.RelayTimeRequest(pause, ts.CurrentSpeedIndex());
                return !relayed; // relayed → block local; otherwise fall through
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] TimeControlPausePatch failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Client-side speed intercept: UIModuleTimeControl.SelectTimePreset(int) is the single funnel for
    /// every speed change (increase/decrease buttons + ChangeTime). On a non-host client, relay the
    /// requested preset index to the host and block the local commit.
    /// </summary>
    [HarmonyPatch]
    public static class TimeControlSpeedPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl");
            if (_targetType == null) return false;
            _targetMethod = AccessTools.Method(_targetType, "SelectTimePreset", new[] { typeof(int) });
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        // presetIndex = the requested speed preset (SelectTimePreset's int arg).
        public static bool Prefix(int presetIndex)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;
            if (engine.IsHost) return true;
            if (TimeSyncManager.IsApplyingRemote) return true;   // our own MirrorSpeedUi call → let through

            try
            {
                var ts = engine.TimeSync;
                if (ts == null) return true;
                bool relayed = ts.RelayTimeRequest(ts.CurrentPaused(), presetIndex);
                return !relayed;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] TimeControlSpeedPatch failed: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Client-side shared-clock guard. Native auto-pauses from opening side panels (Research /
    /// Manufacturing / Diplomacy / Geoscape-Log / vehicle-selected) funnel through
    /// GeoscapeView.SetGamePauseState, which writes Timing.Paused DIRECTLY — bypassing the OnPauseTime
    /// relay. On a client that would locally pause the SHARED clock and then fight the host heartbeat
    /// (flicker/desync). Policy (host-authoritative): a client must NEVER write the shared Timing
    /// locally — so block the local write. The pause BUTTON still works via the OnPauseTime → host
    /// relay; the host-applied path (IsApplyingRemote) still passes through. Host is unaffected.
    /// </summary>
    [HarmonyPatch]
    public static class GeoscapeViewPausePatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            if (_targetType == null) return false;
            _targetMethod = AccessTools.Method(_targetType, "SetGamePauseState", new[] { typeof(bool) });
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        public static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true; // no session → local
            if (engine.IsHost) return true;                      // host owns the shared clock
            if (TimeSyncManager.IsApplyingRemote) return true;   // our host-applied path → let through
            return false;                                        // client: block local shared-clock write
        }
    }
}
