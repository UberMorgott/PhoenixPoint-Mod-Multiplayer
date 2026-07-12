using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.TimeSync;
using UnityEngine;

namespace Multiplayer.Harmony
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

        // pause = the requested paused state (OnPauseTime's bool arg); __instance = the UIModuleTimeControl.
        public static bool Prefix(bool pause, object __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true; // no session → local
            if (TimeSyncManager.IsApplyingRemote) return true;   // our own remote-apply → let through (host + client)
            // INTERCEPTION TIME-LOCK: geoscape time control is hard-locked while the host resolves an air-combat
            // interception. Block the HOST's own pause commit — but ONLY on the GEOSCAPE widget: the interception
            // spawns a SECOND UIModuleTimeControl (InterceptionTimeControlModule) that drives the minigame's own
            // clock, and this same patch hits it too, so an unscoped deny would kill pause INSIDE the air combat.
            if (InterceptionTimeLock.Active && TimeSyncManager.IsGeoscapeTimeControl(__instance)) return false;
            if (engine.IsHost) return true;                      // host commits locally

            try
            {
                var ts = engine.TimeSync;
                if (ts == null) return true;
                // Review fix BUG 1b: under the client sim-freeze the widget computed pause =
                // !_timing.Paused (UIModuleTimeControl.cs:178) off the PINNED-true local Timing → always
                // false, so the client could only ever UNPAUSE the host. The user's toggle intent is
                // against the HOST state: relay !GlyphHostPaused instead. Freeze inactive (host /
                // flag-OFF): the widget's computed arg passes through byte-identical.
                bool freeze = ClientSimFreeze.ShouldFreeze(
                    ClientSimFreeze.Enabled, true, engine.IsActiveSession, engine.IsHost);
                bool requestPaused = ClientSimFreeze.PauseRelayArg(freeze, pause, TimeSyncManager.GlyphHostPaused);
                bool relayed = ts.RelayTimeRequest(requestPaused, ts.CurrentSpeedIndex());
                return !relayed; // relayed → block local; otherwise fall through
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] TimeControlPausePatch failed: " + ex.Message);
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

        // presetIndex = the requested speed preset (SelectTimePreset's int arg); __instance = UIModuleTimeControl.
        public static bool Prefix(int presetIndex, object __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;
            if (TimeSyncManager.IsApplyingRemote) return true;   // our own MirrorSpeedUi call → let through (host + client)
            // INTERCEPTION TIME-LOCK: block the HOST's own speed commit while an interception is resolving — but
            // ONLY on the GEOSCAPE widget (the interception's own InterceptionTimeControlModule sets speed at
            // UIStateInterception init; an unscoped deny would break the minigame's clock). Not remote-apply (above).
            if (InterceptionTimeLock.Active && TimeSyncManager.IsGeoscapeTimeControl(__instance)) return false;
            if (engine.IsHost) return true;

            try
            {
                var ts = engine.TimeSync;
                if (ts == null) return true;
                bool relayed = ts.RelayTimeRequest(ts.CurrentPaused(), presetIndex);
                return !relayed;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] TimeControlSpeedPatch failed: " + ex.Message);
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

    /// <summary>
    /// Client-side PROGRAMMATIC auto-pause guard (regression fix rca — spontaneous unpause).
    /// Native geoscape UI states (vehicle-selected, research / manufacturing / replenish / geoscape-log,
    /// nothing-selected, asset-deployment, …) request a game auto-pause via
    /// <c>GeoscapeView.RequestGamePause() → RequestPauseCrt()</c>, whose body runs
    /// <c>SetGamePauseState(true)</c> (already blocked for a client by <see cref="GeoscapeViewPausePatch"/>)
    /// AND THEN <c>UIModuleTimeControl.SetTimeState(Timing.Paused) → OnPauseTime(...)</c>. Under the Inc4
    /// client sim-freeze (default-ON since 2026-07-05) the client's local <c>Timing.Paused</c> is PINNED
    /// true, so that programmatic <c>OnPauseTime</c> lands in <see cref="TimeControlPausePatch"/>, whose
    /// freeze branch relays <c>!GlyphHostPaused</c> — a TOGGLE against the host, NOT the requested value.
    /// When the host is already paused that toggle becomes an UNPAUSE, so every client vehicle-select /
    /// panel-open while paused spuriously RESUMES the shared clock ("game un-pauses by itself").
    ///
    /// <c>SetTimeState(bool)</c>'s ONLY caller is <c>RequestPauseCrt</c> (GeoscapeView.cs:1295) — it is
    /// never the user's pause BUTTON (that is <c>OnPauseTimeKeyPressed → OnPauseTime</c> directly). So
    /// blocking <c>SetTimeState</c> on a client kills the programmatic poison path (mirroring the
    /// SetGamePauseState block) while the explicit pause-button relay stays intact — host-authoritative:
    /// a client's panel/auto-pause never drives the shared clock; only its deliberate pause button does.
    /// Host / no-session / our own remote-apply pass through unchanged.
    /// </summary>
    [HarmonyPatch]
    public static class TimeControlSetTimeStatePatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl");
            if (_targetType == null) return false;
            _targetMethod = AccessTools.Method(_targetType, "SetTimeState", new[] { typeof(bool) });
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        public static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true; // no session → local
            if (engine.IsHost) return true;                      // host owns the shared clock
            if (TimeSyncManager.IsApplyingRemote) return true;   // our host-applied path → let through
            return false;                                        // client: block programmatic auto-pause relay
        }
    }
}
