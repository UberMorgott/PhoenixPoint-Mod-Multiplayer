using System;

namespace Multipleer.Network.CommandSync
{
    // ── PURE (Unity-free) HOST-CLOCK snapshot interpolation core ──────────────────────────────────────────
    //
    // Replaces ARRIVAL-time keyed snapshot interpolation (the cause of the in-game "client renders slower +
    // jerky" flaw). The host now stamps each 0x35 vehicle record with HostSendTime (geoscape Timing.Now). This
    // core buffers samples keyed by that HOST time and renders at (estimatedHostNow − delay), so the rendered
    // timeline is reconstructed from HOST SAMPLING spacing — NOT from irregular packet ARRIVAL spacing. That is
    // what removes the playback stretch: each fixed host-distance segment is replayed over the host-time gap the
    // host actually used, regardless of network jitter or the dirty-epsilon send cadence.
    //
    // WHY this is correct (and still a PURE single-writer mirror — no extrapolation, no second simulator):
    //   • Host and client real-time clocks both advance 1:1 with wall time but have an unknown constant skew +
    //     a variable one-way latency. For sample i, (localArrival_i − hostSend_i) = latency_i + skew. The
    //     MINIMUM of that over recent samples is the best floor estimate of (latency_floor + skew) — the lucky
    //     lowest-latency packet. So estimatedHostNow(localNow) = localNow − minDelayFloor. (We TRACK the min
    //     with a slow upward leak so the floor can rise if the network's latency floor genuinely increases,
    //     and never get pinned forever by one outlier-low packet.)
    //   • renderHostTime = estimatedHostNow − delay. Bracketing reuses the proven ClientInterpolationCore.Select
    //     over the host-time ring (clamp-to-oldest Direct / interior Interp / underrun Hold — never extrapolate).
    //
    // The interpolator (Unity glue) owns the Vector3.Lerp/Quaternion.Slerp; this core owns ONLY the scalar
    // timeline (offset estimate + bracket) so it is fully unit-testable without Unity. The harness drives this
    // exact class. A generic <T> value path keeps the test (double) and the interpolator (Vector3/Quaternion)
    // on the SAME timeline code; the test exercises the double instantiation.
    //
    // BACKWARD-SANE: if a record carries no HostSendTime (old/absent → 0), the caller stamps the sample's
    // localArrival as the host time AND feeds the same value as both hostSend and localArrival, which makes the
    // offset floor 0 and degenerates this core to the previous arrival-time behavior — nothing hard-crashes.
    internal sealed class ClientHostClockInterpolation
    {
        // Match the interpolator's ring (12 ≈ 0.8s @ 15Hz) so the pure core and the Unity component agree.
        internal const int Capacity = 12;

        private readonly float _delaySeconds;

        // ── SCALE-AWARE ADAPTIVE RENDER WINDOW ────────────────────────────────────────────────────────────
        // The fixed _delaySeconds is a GAME-time constant, but HostSendTime is the geoscape GAME clock which
        // advances at Scale × real-time (TimeControlModule SelectedTimeScale; Timing.cs Now = StartTime+OwnNow,
        // OwnNow scales by Scale). The host flushes every ~0.066 REAL seconds, so consecutive HostSendTime
        // stamps are 0.066 × Scale GAME-seconds apart (≈237 game-s at Scale=3600). A fixed 0.35 game-s delay is
        // then FAR newer than the newest buffered sample → chronic underrun → the lived "lags + jerky bursts".
        //
        // FIX: render at hostNow − W where W tracks the OBSERVED inter-sample host-time gap, so the render point
        // always trails ~WindowGapMultiplier real samples behind the newest at ANY time-scale (and back to the
        // 0.35 floor at Scale=1). W is derived purely from the host-time spacing the host actually used, so it is
        // implicitly scale-aware with NO need to plumb Scale into the core — a scale change simply changes the
        // observed gap and W follows it smoothly. The smoothed gap also damps per-arrival jitter.
        private readonly bool _adaptiveWindow;

        // Scale-aware render window estimator (snap-down/leak-up over the observed inter-sample host-time gap).
        // Shared, tested, and identical to the one the live ClientVehicleInterpolator uses.
        private readonly AdaptiveRenderWindow _window = new AdaptiveRenderWindow();

        // Parallel host-time-keyed ring (oldest→newest after compaction). We keep them ASCENDING by HostSendTime
        // on insert (out-of-order / duplicate safe) so Select sees a clean ascending window with no per-render sort.
        private readonly double[] _hostTimes = new double[Capacity];
        private readonly double[] _values = new double[Capacity];
        private int _count;

        // The host-time ring _hostTimes is already DOUBLE and ascending, so it is passed straight to
        // ClientInterpolationCore.Select (which now takes double[]). No float scratch / rebase: double holds the
        // geoscape clock (~6.4e10 game-s) at full sub-second resolution, so narrowing to float here (the old
        // _selTimes) is exactly what collapsed every sample to one value in-game — removed.

        // Host↔local clock offset (the smoothed minimum one-way delay floor) — the SAME pure estimator the live
        // ClientVehicleInterpolator uses, so the offline harness exercises the production offset code.
        private readonly HostClockOffsetEstimator _offset = new HostClockOffsetEstimator();

        public ClientHostClockInterpolation(float delaySeconds, bool adaptiveWindow = true)
        {
            _delaySeconds = delaySeconds;
            _adaptiveWindow = adaptiveWindow;
        }

        public int Count => _count;

        // The render window actually applied: the scale-aware adaptive window (≥ the fixed floor) when enabled,
        // else the legacy fixed delay. Public for the interpolator/diagnostics.
        public double EffectiveWindow =>
            _adaptiveWindow ? _window.Window(_delaySeconds) : _delaySeconds;

        // Ingest one received sample. hostSendTime = the wire HostSendTime (host Timing.Now at emission);
        // localArrival = the client's local clock when the packet was applied; value = the sample payload.
        public void Observe(double hostSendTime, double localArrival, double value)
        {
            // (1) Update the host↔local offset floor from this sample's observed one-way delay.
            _offset.Observe(hostSendTime, localArrival);

            // (2) Track the smoothed inter-sample HOST-time gap (game-s) → scale-aware render window.
            _window.Observe(hostSendTime);

            // (3) Insert into the host-time-keyed ring, kept ASCENDING (out-of-order safe). A duplicate host time
            // overwrites in place (newest value for that host instant wins) — harmless for a pure mirror.
            InsertSorted(hostSendTime, value);
        }

        // Estimated host clock NOW for a given local clock now: anchorHost + rate*(localNow − anchorLocal)
        // (offset+rate clock-sync; see HostClockOffsetEstimator).
        public double EstimateHostNow(double localNow)
        {
            return _offset.EstimateHostNow(localNow);
        }

        // Estimated host rate (game-seconds per real-second ≈ Scale), derived from the timestamp stream. For
        // diagnostics + the scale-convergence tests.
        public double EstimatedRate => _offset.EstimatedRate;

        // Render the value at renderHostTime = EstimateHostNow(localNow) − EffectiveWindow by bracketing the
        // host-time ring. EstimateHostNow maps the LOCAL clock to host time via the received-stamp offset floor;
        // this overload is for callers that only have a local clock. Prefer TryRenderAt on the client, where the
        // host-slaved geoscape clock (ClientTimeMirror) already gives host game-now directly (and advances at the
        // host's Scale × real rate between samples → smooth at any time-scale).
        public bool TryRender(double localNow, out double rendered)
        {
            return TryRenderAt(EstimateHostNow(localNow), out rendered, out _);
        }

        // Render at hostGameNow − EffectiveWindow, where hostGameNow is the host GAME clock NOW (on the client,
        // read the host-slaved Timing.Now). The window is scale-aware (adaptive to the observed host-time gap) so
        // the render point always brackets two real samples at any geoscape time-scale. Returns false only when
        // the buffer is empty. Outputs the bracket mode (Interp / Direct / Hold) for diagnostics + tests.
        public bool TryRenderAt(double hostGameNow, out double rendered, out ClientInterpolationCore.SampleMode mode)
        {
            rendered = 0.0;
            mode = ClientInterpolationCore.SampleMode.Empty;
            if (_count == 0) return false;

            double renderHost = hostGameNow - EffectiveWindow;

            // Bracket directly on the DOUBLE host-time ring — no float narrowing (that was the precision collapse).
            var b = ClientInterpolationCore.Select(_hostTimes, _count, renderHost);
            mode = b.Mode;
            switch (b.Mode)
            {
                case ClientInterpolationCore.SampleMode.Interp:
                    rendered = _values[b.I0] + (_values[b.I1] - _values[b.I0]) * b.Frac;
                    return true;
                case ClientInterpolationCore.SampleMode.Empty:
                    return false;
                default: // Direct / Hold
                    rendered = _values[b.I0];
                    return true;
            }
        }

        public void Reset()
        {
            _count = 0;
            _offset.Reset();
            _window.Reset();
        }

        // Insert (hostTime,value) keeping _hostTimes ascending. Duplicate host time → overwrite the value in
        // place. When full, the OLDEST (index 0) is dropped to make room (shift-left) — bounded, alloc-free.
        private void InsertSorted(double hostTime, double value)
        {
            // Find insertion index (first slot whose host time is >= hostTime).
            int idx = 0;
            while (idx < _count && _hostTimes[idx] < hostTime) idx++;

            // Duplicate host instant → overwrite newest value for it (no growth).
            if (idx < _count && _hostTimes[idx] == hostTime)
            {
                _values[idx] = value;
                return;
            }

            if (_count < Capacity)
            {
                // Shift [idx.._count) right by one to open a slot at idx.
                for (int j = _count; j > idx; j--)
                {
                    _hostTimes[j] = _hostTimes[j - 1];
                    _values[j] = _values[j - 1];
                }
                _hostTimes[idx] = hostTime;
                _values[idx] = value;
                _count++;
            }
            else
            {
                // Full ring. If the new sample is older than everything, it's stale → drop it.
                if (idx == 0) return;
                // Drop the oldest by shifting [1..idx) left into [0..idx-1), then place at idx-1.
                for (int j = 1; j < idx; j++)
                {
                    _hostTimes[j - 1] = _hostTimes[j];
                    _values[j - 1] = _values[j];
                }
                _hostTimes[idx - 1] = hostTime;
                _values[idx - 1] = value;
            }
        }
    }
}
