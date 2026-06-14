namespace Multipleer.Network.CommandSync
{
    // ── PURE (Unity-free) SCALE-AWARE RENDER-WINDOW estimator ─────────────────────────────────────────────
    //
    // Snapshot interpolation renders the host stream at renderTime = hostNow − W and lerps between the two
    // buffered samples that bracket renderTime. W (the render window / delay) must be large enough that two real
    // samples always bracket renderTime. The hard part is the GEOSCAPE TIME-SCALE: HostSendTime is the geoscape
    // GAME clock (Timing.Now), which advances at Scale × real-time (TimeControlModule SelectedTimeScale; observed
    // Scale=3600). The host flushes the transform every ~0.066 REAL seconds, so consecutive HostSendTime stamps
    // are 0.066 × Scale GAME-seconds apart (≈237 game-s at Scale=3600). A FIXED game-time delay (e.g. 0.35s) is
    // then far newer than the newest buffered sample → chronic underrun → the lived "client lags + jerky bursts".
    //
    // FIX: track the OBSERVED inter-sample host-time gap and set W ≈ GapMultiplier × smoothedGap (clamped to a
    // sane floor). Because the gap already equals 0.066 × Scale, W is implicitly scale-aware with NO need to plumb
    // Scale in — and a scale CHANGE just changes the observed gap, which W follows. Smoothing shape mirrors
    // HostClockOffsetEstimator's floor: SNAP DOWN when the gap shrinks (host slows → W must collapse promptly so
    // the render point re-anchors in ONE bounded catch-up step, not a multi-frame race), EASE UP when the gap
    // grows (host speeds up → add the extra lag smoothly, never as a single backward jump).
    internal sealed class AdaptiveRenderWindow
    {
        internal const double GapMultiplier = 2.0;   // trail ~2 sample gaps behind newest → robust two-sample bracket
        internal const double LeakUp = 0.2;          // EMA factor when the gap GROWS (host speeds up)

        private double _smoothedGap;                 // smoothed consecutive HostSendTime delta (game-seconds)
        private bool _have;
        private double _lastHostTime;                // newest in-order HostSendTime fed so far
        private bool _haveLast;

        // Feed a received sample's HostSendTime (game-seconds). Only a forward step past the current newest
        // defines a fresh gap; out-of-order / duplicate stamps are ignored (they don't define spacing).
        public void Observe(double hostSendTime)
        {
            if (_haveLast)
            {
                double gap = hostSendTime - _lastHostTime;
                if (gap > 0.0)
                {
                    if (!_have) { _smoothedGap = gap; _have = true; }
                    else if (gap < _smoothedGap) _smoothedGap = gap;              // snap DOWN (host slowed)
                    else _smoothedGap += (gap - _smoothedGap) * LeakUp;           // ease UP (host sped up)
                }
            }
            if (!_haveLast || hostSendTime > _lastHostTime)
            {
                _lastHostTime = hostSendTime;
                _haveLast = true;
            }
        }

        // The render window to apply: max(floor, GapMultiplier × smoothedGap). Before any gap is observed, the
        // floor (the legacy fixed delay) is used so behavior at Scale=1 / startup is unchanged.
        public double Window(double floorSeconds)
        {
            if (!_have) return floorSeconds;
            double w = GapMultiplier * _smoothedGap;
            return w > floorSeconds ? w : floorSeconds;
        }

        public bool HasGap => _have;

        public void Reset()
        {
            _smoothedGap = 0.0;
            _have = false;
            _lastHostTime = 0.0;
            _haveLast = false;
        }
    }
}
