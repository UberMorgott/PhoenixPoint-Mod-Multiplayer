using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // Client-only apply of a host clock snapshot (0x34, subtype TimingState). Forces the local clock to
    // host via Timing.ProcessInstanceData (TimeBridge.ApplyTimeState) -> displayed Now/Scale/Paused match
    // host, frame-drift corrected. Host ignores (it owns the clock). ProcessInstanceData fires no events
    // and reschedules nothing, so no re-intercept and no SetGamePauseState TimeLimit guard.
    //
    // PARITY MIRROR (DIAG-A1 fix): the ~0.5s stamp above corrects ABSOLUTE drift but leaves the client's
    // Scale/Paused free to diverge BETWEEN stamps — the geoscape locally auto-pauses on UI / vehicle
    // selection, or Scale goes stale. While Timing.Paused is true the TimingScheduler skips the
    // NavigateRoutine slerp -> the client craft FREEZES, then JUMPS when the next stamp flips Paused.
    // Cure: cache the host Scale+Paused on every stamp and re-assert them EVERY frame (ParityTick),
    // overriding any local change so parity holds continuously. Client = pure mirror of host time state.
    public static class ClientTimeMirror
    {
        // Last host-known time state (cached from the most recent 0x34 stamp). _hasState gates the
        // per-frame parity tick so we never assert a clock value before the first host stamp arrives.
        private static bool _hasState;
        private static float _hostScale;
        private static bool _hostPaused;
        // DIAG-A1 TEMP (strip after RCA) — host clock NOW (seconds, double) carried by the most recent stamp,
        // for the throttled per-frame lockstep readout (compare against the client's live Timing.Now).
        private static double _hostStampNow;
        private static int _lockstepLogCtr; // DIAG-A1 TEMP — frame throttle so the lockstep line isn't per-frame spam

        public static void Apply(TimeStatePayload payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            // Cache the host Scale+Paused for the per-frame parity tick (kept even if the absolute apply
            // below throws — the parity values are still the freshest host truth we have).
            _hostScale = payload.Scale;
            _hostPaused = payload.Paused;
            // DIAG-A1 TEMP — host Now (StartTime+OwnNow) in seconds for the lockstep readout. Ticks are
            // double-precision (geoscape clock ~6.4e10) so reconstruct from ticks, never a float cast.
            _hostStampNow = new System.TimeSpan(payload.StartTimeTicks + payload.OwnNowTicks).TotalSeconds;
            _hasState = true;
            try { TimeBridge.ApplyTimeState(payload); }
            catch (System.Exception ex) { Debug.LogError($"[Multipleer] ClientTimeMirror apply failed: {ex}"); }
        }

        // Pure helper (unit-testable): given the cached host state, whether a host stamp has been seen,
        // and the role flags, decide whether the per-frame parity tick should run at all. Keeps the
        // gating logic out of the reflection path so it can be asserted without a live game clock.
        public static bool ShouldRunParity(bool isActive, bool isHost, bool hasState)
            => isActive && !isHost && hasState;

        // Reset on session teardown so a host/join/leave cycle never re-asserts a stale clock value
        // onto the next session's geoscape before its first stamp arrives.
        public static void Reset()
        {
            _hasState = false;
            _hostScale = 0f;
            _hostPaused = false;
            _hostStampNow = 0.0;       // DIAG-A1 TEMP
            _lockstepLogCtr = 0;       // DIAG-A1 TEMP
            // (Monotonic-clamp resets removed with the reverted clamps.) Clear the DIAG-NAV probe state so a
            // fresh session starts with clean per-second delta baselines.
            GeoBridge.ResetNavProbe();
        }

        // Client per-frame mirror of host Scale/Paused. Hooked from NetworkEngine.Update(). Runs only
        // on an active client that has received >=1 host stamp; re-asserts the cached host values onto
        // the live geoscape clock (TimeBridge.SetScaleAndPaused self-guards equality, so a frame with
        // no divergence is a cheap no-op). Wrapped in CommandRelay's applying scope so it shares the
        // exact authoritative bypass the host-stamp apply uses (the intercept prefixes return true /
        // execute under IsApplying), guaranteeing the host-driven write is never treated as a local
        // client pause/scale edit.
        public static void ParityTick()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null) return;
            if (!ShouldRunParity(engine.IsActive, engine.IsHost, _hasState)) return;

            try
            {
                bool corrected;
                float wasScale; bool wasPaused;
                using (CommandRelay.ApplyScope())
                    corrected = TimeBridge.SetScaleAndPaused(_hostScale, _hostPaused, out wasScale, out wasPaused);

                if (corrected)
                {
                    // DIAG-A1 TEMP — confirms a divergence existed between stamps and parity closed it.
                    Debug.Log($"[Multipleer] DIAG-A1 time-parity correct: Paused host={_hostPaused} client(was)={wasPaused} Scale host={_hostScale} was={wasScale}");
                }

                // DIAG-A1 TEMP (strip after RCA) — LOCKSTEP readout, throttled to ~every 30th frame. Proves the
                // client clock tracks the host: clientNow (live Timing.Now) vs hostStampNow (last 0x34 stamp),
                // mirrored Scale/Paused, whether a native travel routine is live + the craft's globe position.
                // If clientNow advances in step with hostStampNow and Paused mirrors the host, the NavigateRoutine
                // ticks identically -> native client travel is in lockstep. routineActive/craftPos come from the
                // first traveling client vehicle (null if none in flight).
                if ((++_lockstepLogCtr % 30) == 0)
                {
                    double clientNow = TimeBridge.GetHostNowSeconds(); // client's OWN live clock (double, no float cast)
                    bool routineActive; UnityEngine.Vector3 craftPos;
                    GeoBridge.DescribeFirstTravellingVehicle(out routineActive, out craftPos);
                    Debug.Log($"[Multipleer] DIAG-A1 lockstep clientNow={clientNow:F2} hostStampNow={_hostStampNow:F2} " +
                              $"deltaSec={clientNow - _hostStampNow:F2} Scale={_hostScale} Paused={_hostPaused} " +
                              $"routineActive={routineActive} craftPos=({craftPos.x:F1},{craftPos.y:F1},{craftPos.z:F1})"); // DIAG-A1 TEMP (strip after RCA)
                }
            }
            catch (System.Exception ex) { Debug.LogError($"[Multipleer] ClientTimeMirror parity tick failed: {ex}"); }
        }
    }
}
