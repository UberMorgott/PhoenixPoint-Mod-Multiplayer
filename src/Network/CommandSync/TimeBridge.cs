using System;
using HarmonyLib;
using Multipleer.Network.CommandSync;

namespace Multipleer.Network.CommandSync
{
    // Unity-side id<->engine bridge for time sync. AccessTools/Traverse only — the mod never
    // hard-references game types (matches GeoBridge). Reaches the live UIModuleTimeControl via
    // the active GeoscapeView, and the clock via GeoLevelController.Timing.
    internal static class TimeBridge
    {
        // Active GeoscapeView.GeoscapeModules.TimeControlModule, or null (not on geoscape).
        public static object GetTimeControlModule()
        {
            var view = GetGeoscapeView();
            if (view == null) return null;
            var modules = AccessTools.Field(view.GetType(), "GeoscapeModules")?.GetValue(view);
            if (modules == null) return null;
            return AccessTools.Field(modules.GetType(), "TimeControlModule")?.GetValue(modules);
        }

        // GameUtl.CurrentLevel().GetComponent<GeoLevelController>().View (GeoscapeView), or null.
        // NOTE (R8 verified against decompile): GeoLevelController.View is a public FIELD
        // (`public GeoscapeView View;`), not a property — AccessTools.Property returns null for it.
        // Resolve property first (future-proof), then fall back to the real field accessor.
        public static object GetGeoscapeView()
        {
            var geoLevel = GeoBridge.GetGeoLevelController();
            if (geoLevel == null) return null;
            var t = geoLevel.GetType();
            var prop = AccessTools.Property(t, "View");
            if (prop != null) return prop.GetValue(geoLevel);
            return AccessTools.Field(t, "View")?.GetValue(geoLevel);
        }

        // GeoLevelController.Timing (the authoritative clock), or null.
        public static object GetTiming()
        {
            var geoLevel = GeoBridge.GetGeoLevelController();
            if (geoLevel == null) return null;
            return AccessTools.Property(geoLevel.GetType(), "Timing")?.GetValue(geoLevel);
        }

        // Host geoscape clock NOW in seconds as DOUBLE = GeoLevelController.Timing.Now read via TimeUnit.TimeSpan
        // .TotalSeconds (Timing.Now = StartTime+OwnNow, Timing.cs:55). Used to stamp 0x35 vehicle records
        // (HostSendTime) so the client renders on the host timeline. DOUBLE — NOT float — because the geoscape
        // clock reaches ~6.4e10 game-seconds where a float32 ULP (~8192 s) dwarfs the ~231 s between samples; a
        // float stamp collapses every buffered sample to one value → no interpolation (the in-game lag/jerk). We
        // read TimeSpan.TotalSeconds (already a double) instead of the implicit operator float(TimeUnit) so NO
        // intermediate float cast re-introduces the precision loss. Returns 0 if the clock is unreachable (caller
        // treats 0 as "absent" → client falls back to arrival-time render).
        public static double GetHostNowSeconds()
        {
            var timing = GetTiming();
            if (timing == null) return 0.0;
            var now = AccessTools.Property(timing.GetType(), "Now")?.GetValue(timing);
            if (now == null) return 0.0;
            // TimeUnit exposes a public `TimeSpan` getter (used by TicksOf below). TimeSpan.TotalSeconds is a
            // double → full sub-second resolution at geoscape magnitude, no float narrowing.
            try
            {
                var ts = AccessTools.Property(now.GetType(), "TimeSpan")?.GetValue(now);
                if (ts is TimeSpan span) return span.TotalSeconds;
            }
            catch { /* fall through to 0 → arrival-time fallback on the client */ }
            return 0.0;
        }

        // Current SelectedPresetTime (int) off the live module; -1 if unavailable.
        public static int GetCurrentPresetIndex(object timeModule)
        {
            if (timeModule == null) return -1;
            var v = AccessTools.Field(timeModule.GetType(), "SelectedPresetTime")?.GetValue(timeModule);
            return v is int i ? i : -1;
        }

        // Current paused state off the module's private _timing; false if unavailable.
        public static bool GetCurrentPaused(object timeModule)
        {
            if (timeModule == null) return false;
            var timing = AccessTools.Field(timeModule.GetType(), "_timing")?.GetValue(timeModule);
            if (timing == null) return false;
            var v = AccessTools.Property(timing.GetType(), "Paused")?.GetValue(timing);
            return v is bool b && b;
        }

        // Authoritative apply on host/clients: drive the live module so SelectedPresetTime, animator,
        // and Timing.Scale/Paused stay coherent. Runs under CommandRelay.IsApplying (set by ApplyResult)
        // so the intercept prefixes treat the nested calls as re-entrant and let them through.
        // Fallback: poke Timing directly if the module is unreachable.
        public static void ApplySetTime(SetTimePayload p)
        {
            var module = GetTimeControlModule();
            if (module != null)
            {
                // SelectTimePreset(int) clamps internally; OnPauseTime(bool) is private.
                AccessTools.Method(module.GetType(), "SelectTimePreset", new[] { typeof(int) })
                           ?.Invoke(module, new object[] { p.PresetIndex });
                AccessTools.Method(module.GetType(), "OnPauseTime", new[] { typeof(bool) })
                           ?.Invoke(module, new object[] { p.Paused });
                return;
            }
            // Fallback: no module (e.g. timing change before UI ready) -> poke the clock's Paused only.
            var timing = GetTiming();
            if (timing != null)
                AccessTools.Property(timing.GetType(), "Paused")?.SetValue(timing, p.Paused);
        }

        // Host: snapshot the clock for the periodic mirror. Returns null if no clock.
        public static SetTimeStateSnapshot RecordHostState()
        {
            var timing = GetTiming();
            if (timing == null) return null;
            var data = AccessTools.Method(timing.GetType(), "RecordInstanceData")?.Invoke(timing, null);
            if (data == null) return null;
            return new SetTimeStateSnapshot
            {
                Payload = new TimeStatePayload
                {
                    Paused = (bool)AccessTools.Field(data.GetType(), "Paused").GetValue(data),
                    Scale = (float)AccessTools.Field(data.GetType(), "Scale").GetValue(data),
                    StartTimeTicks = TicksOf(AccessTools.Field(data.GetType(), "StartTime").GetValue(data)),
                    StartFixedTicks = TicksOf(AccessTools.Field(data.GetType(), "StartFixedTime").GetValue(data)),
                    OwnNowTicks = TicksOf(AccessTools.Field(data.GetType(), "OwnNow").GetValue(data)),
                    OwnFixedTicks = TicksOf(AccessTools.Field(data.GetType(), "OwnFixedNow").GetValue(data))
                }
            };
        }

        // Client: force the clock to the host snapshot via Timing.ProcessInstanceData (sets fields
        // directly, fires no events -> no re-intercept, bypasses the SetGamePauseState TimeLimit guard).
        // REVERTED to stock: the monotonic Now-clamp (DIAG-MONO) was confirmed DEAD (backwardPrevented=False
        // always) and is removed — the host snapshot is applied VERBATIM, exactly as the un-clamped engine path.
        public static void ApplyTimeState(TimeStatePayload p)
        {
            var timing = GetTiming();
            if (timing == null) return;
            var tidType = AccessTools.TypeByName("Base.Core.TimingInstanceData");
            if (tidType == null) return;

            var data = Activator.CreateInstance(tidType);
            AccessTools.Field(tidType, "Paused").SetValue(data, p.Paused);
            AccessTools.Field(tidType, "Scale").SetValue(data, p.Scale);
            AccessTools.Field(tidType, "StartTime").SetValue(data, TimeUnitFromTicks(p.StartTimeTicks));
            AccessTools.Field(tidType, "StartFixedTime").SetValue(data, TimeUnitFromTicks(p.StartFixedTicks));
            AccessTools.Field(tidType, "OwnNow").SetValue(data, TimeUnitFromTicks(p.OwnNowTicks));
            AccessTools.Field(tidType, "OwnFixedNow").SetValue(data, TimeUnitFromTicks(p.OwnFixedTicks));
            AccessTools.Method(timing.GetType(), "ProcessInstanceData", new[] { tidType })
                       ?.Invoke(timing, new[] { data });
        }

        // Client per-frame parity: force the live clock's Scale + Paused to the cached host values
        // WITHOUT touching the StartTime/Now anchors (those stay owned by the ~0.5s absolute-drift
        // stamp in ApplyTimeState). Pokes Timing.Scale / Timing.Paused directly — these native
        // property setters are NOT the UIModuleTimeControl.OnPauseTime/SelectTimePreset funnels the
        // intercept patches hook, so they bypass the client-write block; they self-guard equality
        // (Timing.cs: `if (value != _scale)` / `if (value != _paused)`) and, on a real change, call
        // RescheduleUpdateables -> un-freezes the NavigateRoutine the scheduler had been skipping.
        // Returns true iff a real correction was applied (host value differed from current), so the
        // caller can emit the DIAG-A1 divergence-closed line. wasScale/wasPaused report the pre-write
        // client values for that log. No-op (returns false) if no clock yet.
        public static bool SetScaleAndPaused(float scale, bool paused, out float wasScale, out bool wasPaused)
        {
            wasScale = 0f;
            wasPaused = false;
            var timing = GetTiming();
            if (timing == null) return false;
            var t = timing.GetType();
            var scaleProp = AccessTools.Property(t, "Scale");
            var pausedProp = AccessTools.Property(t, "Paused");
            if (scaleProp == null || pausedProp == null) return false;

            wasScale = scaleProp.GetValue(timing) is float cs ? cs : 0f;
            wasPaused = pausedProp.GetValue(timing) is bool cp && cp;

            bool corrected = false;
            // Equality guard: only write on a genuine divergence to avoid flicker / event spam.
            if (wasScale != scale) { scaleProp.SetValue(timing, scale); corrected = true; }
            if (wasPaused != paused) { pausedProp.SetValue(timing, paused); corrected = true; }
            return corrected;
        }

        // TimeUnit -> ticks via the public TimeSpan getter (no private _time reflection).
        private static long TicksOf(object timeUnit)
        {
            if (timeUnit == null) return 0L;
            var ts = AccessTools.Property(timeUnit.GetType(), "TimeSpan")?.GetValue(timeUnit);
            return ts is TimeSpan t ? t.Ticks : 0L;
        }

        // ticks -> TimeUnit via public static TimeUnit.FromTimeSpan(TimeSpan).
        private static object TimeUnitFromTicks(long ticks)
        {
            var tuType = AccessTools.TypeByName("Base.Core.TimeUnit");
            if (tuType == null) return null;
            var from = AccessTools.Method(tuType, "FromTimeSpan", new[] { typeof(TimeSpan) });
            return from?.Invoke(null, new object[] { TimeSpan.FromTicks(ticks) });
        }
    }

    // Non-null wrapper so callers distinguish "no clock" (null) from a valid snapshot.
    internal sealed class SetTimeStateSnapshot
    {
        public TimeStatePayload Payload;
    }
}
