namespace Multipleer.Network.CommandSync
{
    // ── PURE (Unity-free) host-clock ESTIMATOR — OFFSET *and* RATE (clock-sync netcode) ───────────────────
    //
    // Estimates the host's GAME clock NOW from the client's LOCAL (real) clock, using only the
    // (HostSendTime, localArrival) pairs carried by received 0x35 vehicle records.
    //
    // WHY the previous version was wrong in-game: it modeled hostNow = localNow − delayFloor, i.e. it advanced
    // the render clock at the client's REAL rate. But HostSendTime is the GEOSCAPE GAME clock (Timing.Now),
    // which advances at Scale × real-time (observed Scale up to 3600). So at high scale the host timeline runs
    // far faster than the real render cadence and the real-rate estimate can NEVER sit between two host-time-
    // keyed samples → the bracket selector returns Direct/Hold every frame → the stepped jerk + huge lag.
    //
    // FIX (standard clock-sync): estimate BOTH a phase (anchor) AND a RATE from the timestamp stream.
    //   • estimateHostNow(localNow) = anchorHost + rate * (localNow − anchorLocal)  — advances SMOOTHLY every
    //     frame at the estimated host rate (NO dependency on the lagging stamped geoscape Timing.Now).
    //   • rate ≈ d(HostSendTime)/d(localArrival) ≈ Scale — derived from the timestamp stream, NEVER read off the
    //     engine, so it auto-tracks ANY geoscape time-scale. CRITICAL: the rate is measured over a SLIDING
    //     MULTI-SAMPLE BASELINE (≥ MinSpanReal of real time), not adjacent pairs — when arrival jitter is
    //     comparable to the send gap (the Scale≈1 case), a single-pair ratio is wildly noisy, but over a
    //     ~0.3–0.6 s baseline the bounded jitter is a small fraction of the span → a smooth, stable rate.
    //   • PHASE is closed with a PI loop: each sample re-anchors CONTINUOUSLY (no position jump) and folds the
    //     phase error into a bounded FREQUENCY nudge (_rateCorr, clamped to ±0.5×rate) — so the rendered clock
    //     speeds up / slows down slightly to catch up instead of STEPPING. The clamp keeps the effective rate
    //     strictly positive ⇒ the rendered host-time is monotonic (never a backward render frame).
    //   • WARMUP: the first ~2 in-order pairs SNAP the anchor exactly (a jump is fine pre-render) so the clock
    //     locks within ~2 samples (the "stutter in place 1–2 s then flies" startup). A SCALE CHANGE needs no
    //     special case: the LSQ slope re-converges as the sliding baseline (≤ MaxSpanReal) slides past it, and
    //     the PI loop absorbs the transient phase error smoothly.
    //
    // Shared by ClientHostClockInterpolation (the offline harness drives the whole path through it) and by the
    // live ClientVehicleInterpolator (which renders at estimateHostNow − window), so the production interpolator
    // and the tested core run the SAME clock code.
    internal sealed class HostClockOffsetEstimator
    {
        private bool _have;             // ≥1 sample observed (anchor valid)
        private bool _haveRate;         // ≥2 in-order samples observed (rate valid)

        private double _anchorLocal;    // local (real) clock of the last re-anchor
        private double _anchorHost;     // host (game) clock estimate AT _anchorLocal (re-anchored CONTINUOUSLY)
        private double _rate;           // estimated host game-seconds per local real-second (≈ Scale); the
                                        // FREQUENCY term (LSQ slope over the baseline)
        private double _rateCorr;       // phase-driven FREQUENCY nudge added to _rate until the next sample —
                                        // closes accumulated phase error smoothly (PI loop) with NO position jump

        // Sliding baseline of recent IN-ORDER (localArrival, hostSendTime) points, oldest→newest. The rate is
        // (newestHost − oldestHost)/(newestLocal − oldestLocal) over this window, so jitter averages out.
        private const int HistCap = 16;
        private readonly double[] _histLocal = new double[HistCap];
        private readonly double[] _histHost = new double[HistCap];
        private int _histCount;

        // Keep the baseline within [MinSpanReal, MaxSpanReal] of real time: long enough to average arrival
        // jitter, short enough that a scale change re-converges quickly. ~0.066 s send cadence → ~5–9 samples.
        private const double MinSpanReal = 0.30;
        private const double MaxSpanReal = 0.80;
        // PI phase loop: each sample folds the phase error into a bounded FREQUENCY nudge (not a position jump),
        // so the estimate stays C0-continuous (no backward step / no teleport) and the apparent render speed
        // stays inside [1−MaxCorrFrac, 1+MaxCorrFrac] × rate. PhaseGain = fraction of the error targeted to be
        // closed over the next sample interval.
        private const double PhaseGain = 0.5;
        private const double MaxCorrFrac = 0.5;   // |_rateCorr| ≤ 0.5×rate ⇒ effective rate ≥ 0.5×rate > 0 ⇒
                                                  // strictly forward (monotonic) ⇒ never a backward render step
        // Warmup pairs to SNAP the phase exactly (jump allowed — pre-window, craft not yet smoothly moving) so
        // the clock converges within ~2 samples (the "stutter in place 1–2 s then flies" startup).
        private const int WarmupPairs = 2;
        private const double MinLocalDelta = 1e-6;   // ignore degenerate local deltas (dup / same-frame)

        private int _inOrderPairs;       // count of in-order pairs seen — for warmup phase snap

        // Feed one received sample's host emission time and local arrival time. In-order samples (host time
        // strictly increasing) drive the rate; out-of-order / duplicate samples only refine the anchor.
        public void Observe(double hostSendTime, double localArrival)
        {
            if (!_have)
            {
                _have = true;
                _anchorLocal = localArrival;
                _anchorHost = hostSendTime;
                _rate = 1.0;
                _histLocal[0] = localArrival;
                _histHost[0] = hostSendTime;
                _histCount = 1;
                _inOrderPairs = 0;
                return;
            }

            double newestLocal = _histLocal[_histCount - 1];
            double newestHost = _histHost[_histCount - 1];
            bool inOrder = hostSendTime > newestHost && localArrival - newestLocal > MinLocalDelta;

            if (inOrder)
            {
                PushHist(localArrival, hostSendTime);
                PruneHist();
                _inOrderPairs++;

                // Rate = LEAST-SQUARES slope of host vs local over the sliding baseline. LSQ (not endpoint-to-
                // endpoint) is robust to per-arrival jitter even when jitter ≈ the send gap (the Scale≈1 case):
                // every point contributes, so the two noisy endpoints no longer dominate. A real scale change
                // shifts the slope as the window slides past it (bounded by MaxSpanReal); transient jitter does
                // NOT (it averages out), so NO spurious restart is needed.
                //
                // Until the baseline spans at least MinSpanReal of real time we don't yet have enough leverage
                // for a stable slope, so we hold the previous rate rather than let one short, bursty window
                // (e.g. two arrivals 20 ms apart) swing it — that swing is the source of per-frame render jitter.
                if (_histCount >= 2)
                {
                    double span = _histLocal[_histCount - 1] - _histLocal[0];
                    if (span >= MinSpanReal || !_haveRate)
                    {
                        double slope = LeastSquaresSlope();
                        if (slope > 0.0) { _rate = slope; _haveRate = true; }
                    }
                }
            }

            // OUT-OF-ORDER guard: a sample carrying an OLDER host time that ARRIVES late (hostSendTime ≤ the
            // newest host time we've seen) is STALE — it must NOT pull the phase backward (that yanks the render
            // clock back → a backward render step). It already had no effect on the rate (skipped above); skip
            // the phase correction too. Duplicates (same host time) likewise carry no new phase information.
            if (!inOrder) return;

            // ── PHASE: PI loop (frequency correction, NOT a position jump) ──────────────────────────────────
            // Where does the current estimate (rate + the previous corr) think the host clock was at THIS
            // sample's arrival, vs what the sample actually says?
            double effRate = _rate + _rateCorr;
            double predictedHostAtArrival = _anchorHost + effRate * (localArrival - _anchorLocal);
            double err = hostSendTime - predictedHostAtArrival;   // +behind / −ahead, in host-seconds

            if (_inOrderPairs <= WarmupPairs)
            {
                // WARMUP: snap the anchor exactly (a jump here is fine — it's before the craft renders smoothly)
                // so the clock locks on within ~2 samples. Clear the freq nudge; the LSQ rate carries it.
                _anchorLocal = localArrival;
                _anchorHost = hostSendTime;
                _rateCorr = 0.0;
            }
            else
            {
                // Re-anchor CONTINUOUSLY (no jump): the new anchorHost == the value the estimate already had at
                // this local instant. Fold the phase error into a bounded frequency nudge that closes it over
                // ~the next sample interval, so the rendered clock speeds up / slows down slightly instead of
                // stepping. Clamped to ±MaxCorrFrac×rate ⇒ the effective rate stays strictly positive ⇒ the
                // rendered host-time is monotonic (never a backward frame — the source of the old −5 spike).
                _anchorLocal = localArrival;
                _anchorHost = predictedHostAtArrival;

                double interval = AverageLocalGap();   // expected real seconds until the next sample
                double corr = interval > MinLocalDelta ? (err * PhaseGain) / interval : 0.0;
                double maxCorr = MaxCorrFrac * (_rate > 0.0 ? _rate : 1.0);
                if (corr > maxCorr) corr = maxCorr; else if (corr < -maxCorr) corr = -maxCorr;
                _rateCorr = corr;
            }
        }

        // Mean real-time gap between consecutive baseline samples (expected interval to the next sample). Used to
        // convert the phase error (host-seconds) into a frequency nudge. Falls back to a nominal cadence.
        private double AverageLocalGap()
        {
            if (_histCount >= 2)
            {
                double span = _histLocal[_histCount - 1] - _histLocal[0];
                if (span > MinLocalDelta) return span / (_histCount - 1);
            }
            return 0.066;   // nominal host flush cadence (real seconds)
        }

        // Least-squares slope d(host)/d(local) over the history window. Rebased to the oldest point for
        // numerical conditioning (host times can be large on a long campaign clock). Returns 0 if degenerate.
        private double LeastSquaresSlope()
        {
            double l0 = _histLocal[0], h0 = _histHost[0];
            double sl = 0, sh = 0, sll = 0, slh = 0;
            int n = _histCount;
            for (int i = 0; i < n; i++)
            {
                double l = _histLocal[i] - l0;
                double h = _histHost[i] - h0;
                sl += l; sh += h; sll += l * l; slh += l * h;
            }
            double denom = n * sll - sl * sl;
            if (denom <= 0.0) return 0.0;
            return (n * slh - sl * sh) / denom;
        }

        private void PushHist(double local, double host)
        {
            if (_histCount < HistCap)
            {
                _histLocal[_histCount] = local;
                _histHost[_histCount] = host;
                _histCount++;
            }
            else
            {
                for (int i = 1; i < HistCap; i++)
                {
                    _histLocal[i - 1] = _histLocal[i];
                    _histHost[i - 1] = _histHost[i];
                }
                _histLocal[HistCap - 1] = local;
                _histHost[HistCap - 1] = host;
            }
        }

        // Drop the oldest points while the baseline span exceeds MaxSpanReal, but always keep enough to span at
        // least MinSpanReal (so the rate denominator stays well-conditioned).
        private void PruneHist()
        {
            while (_histCount > 2)
            {
                double newest = _histLocal[_histCount - 1];
                double spanIfDropOldest = newest - _histLocal[1];
                if (newest - _histLocal[0] > MaxSpanReal && spanIfDropOldest >= MinSpanReal)
                {
                    for (int i = 1; i < _histCount; i++)
                    {
                        _histLocal[i - 1] = _histLocal[i];
                        _histHost[i - 1] = _histHost[i];
                    }
                    _histCount--;
                }
                else break;
            }
        }

        // Estimated host GAME clock NOW for the given local (real) clock now:
        //   anchorHost + (rate + rateCorr) * (localNow − anchorLocal).
        // (rate = LSQ frequency, rateCorr = bounded phase-loop nudge.) Before the first observation, returns
        // localNow unchanged (degenerates to local-time rendering).
        public double EstimateHostNow(double localNow) =>
            _have ? _anchorHost + (_rate + _rateCorr) * (localNow - _anchorLocal) : localNow;

        // Estimated host rate (game-seconds per real-second ≈ Scale). 1.0 until the 2nd in-order sample.
        public double EstimatedRate => _rate;

        public bool HasEstimate => _have;
        public bool HasRate => _haveRate;

        public void Reset()
        {
            _have = false;
            _haveRate = false;
            _anchorLocal = 0.0;
            _anchorHost = 0.0;
            _rate = 1.0;
            _rateCorr = 0.0;
            _histCount = 0;
            _inOrderPairs = 0;
        }
    }
}
