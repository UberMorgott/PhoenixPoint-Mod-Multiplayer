using System;
using System.Collections.Generic;

namespace Multiplayer.Network.TimeSync
{
    /// <summary>
    /// Pure, Unity-FREE NTP-style clock-offset estimator (unit-testable). Reconciles the two machines'
    /// per-process monotonic real clocks (<c>Time.realtimeSinceStartupAsDouble</c>) so that
    /// <c>serverNow = localRT + Offset ≈ host localRT</c>.
    ///
    /// 3-stamp exchange (host processing ≈ instant on LAN/DirectIP):
    ///   client→host ping at t0 (client localRT) → host→client pong echoing t0 + t1 (host localRT at
    ///   receive) → client at t3 (client localRT) computes:
    ///     rtt    = t3 - t0
    ///     offset = t1 - (t0 + t3)/2
    /// Reports the MEDIAN offset of the last N accepted samples in a sliding window (robust to a single
    /// asymmetric/one-way-skewed sample, which a lowest-RTT "best of N" pick would let bias serverNow
    /// permanently), rejecting samples with rtt &gt; max (jitter/queuing).
    /// </summary>
    public class ClockOffsetEstimator
    {
        public struct Sample
        {
            public double Rtt;
            public double Offset;
        }

        private readonly double _maxRttSeconds;
        private readonly int _windowSize;
        private readonly int _fallbackAfterRejects;
        private readonly Queue<Sample> _window = new Queue<Sample>();

        private bool _hasOffset;
        private double _offset;
        private bool _usedFallback;

        // High-RTT fallback (internet play): if NO sample passes the rtt cap, after a grace count of
        // consecutive rejects we adopt the best-RTT sample SEEN so the client still gets an offset
        // (otherwise HasOffset never trips → ClientTick gate blocks every clock write → frozen clock).
        private int _rejectsSinceAccept;
        private bool _haveBestSeen;
        private double _bestSeenRtt;
        private double _bestSeenOffset;

        /// <param name="fallbackAfterRejects">
        /// After this many consecutive rejected (over-cap) samples with NO offset yet acquired, adopt the
        /// best-RTT sample seen so far (even if over cap) and log once. The join burst (~5 pings) seeds
        /// this within ~1 s on a high-RTT link. Sub-cap samples are always preferred (LAN unchanged).
        /// </param>
        public ClockOffsetEstimator(double maxRttSeconds = 0.5, int windowSize = 8, int fallbackAfterRejects = 3)
        {
            _maxRttSeconds = maxRttSeconds;
            _windowSize = windowSize < 1 ? 1 : windowSize;
            _fallbackAfterRejects = fallbackAfterRejects < 1 ? 1 : fallbackAfterRejects;
        }

        /// <summary>True once at least one sample has been accepted (gates the client clock derivation).</summary>
        public bool HasOffset => _hasOffset;

        /// <summary>Current best-of-window clock offset (seconds). 0 until <see cref="HasOffset"/>.</summary>
        public double Offset => _offset;

        /// <summary>
        /// True if the offset was first acquired via the high-RTT fallback (no sub-cap sample passed).
        /// The manager logs this once. Cleared once a normal sub-cap sample is accepted afterwards.
        /// </summary>
        public bool UsedFallback => _usedFallback;

        /// <summary>Pure single-sample math: rtt = t3-t0, offset = t1 - (t0+t3)/2.</summary>
        public static Sample MakeSample(double t0, double t1, double t3)
            => new Sample { Rtt = t3 - t0, Offset = t1 - (t0 + t3) / 2.0 };

        /// <summary>
        /// Feed a completed ping/pong (client t0, host t1, client t3). Returns true if accepted
        /// (rtt within bounds) and the best-of-window offset was recomputed; false if rejected (rtt too
        /// high). A negative rtt (clock anomaly) is also rejected.
        /// </summary>
        public bool AddSample(double t0, double t1, double t3)
        {
            var s = MakeSample(t0, t1, t3);

            // A negative rtt is a clock anomaly — never usable, not even as a fallback.
            if (s.Rtt < 0.0)
                return false;

            // Track the best (lowest-rtt) sample SEEN regardless of the cap, for the fallback.
            if (!_haveBestSeen || s.Rtt < _bestSeenRtt)
            {
                _haveBestSeen = true;
                _bestSeenRtt = s.Rtt;
                _bestSeenOffset = s.Offset;
            }

            if (s.Rtt > _maxRttSeconds)
            {
                _rejectsSinceAccept++;
                // High-RTT fallback: still no offset after a grace count of consecutive over-cap
                // samples → adopt the best seen so the client clock can run on an internet link.
                if (!_hasOffset && _rejectsSinceAccept >= _fallbackAfterRejects && _haveBestSeen)
                {
                    _offset = _bestSeenOffset;
                    _hasOffset = true;
                    _usedFallback = true;
                    return true;
                }
                return false;
            }

            // Sub-cap sample accepted → normal path; clears the fallback flag.
            _rejectsSinceAccept = 0;
            _usedFallback = false;

            _window.Enqueue(s);
            while (_window.Count > _windowSize)
                _window.Dequeue();

            // MEDIAN-of-window of the accepted OFFSETS. Previously this took the single lowest-RTT
            // sample's offset, so one asymmetric (one-way-skewed) sample with the lowest RTT biased
            // serverNow for the whole window lifetime. The median is robust to a single such outlier
            // (it must move past half the window to shift the estimate) while staying cheap and adding
            // no smoothing lag — the visual lerp already absorbs small steps (spec §7).
            _offset = MedianOffset();
            _hasOffset = true;
            return true;
        }

        /// <summary>Median of the accepted-sample offsets currently in the window (even count → mean of the two middle).</summary>
        private double MedianOffset()
        {
            int n = _window.Count;
            if (n == 0) return _offset;
            var offsets = new double[n];
            int i = 0;
            foreach (var w in _window) offsets[i++] = w.Offset;
            Array.Sort(offsets);
            int mid = n / 2;
            return (n % 2 == 1) ? offsets[mid] : (offsets[mid - 1] + offsets[mid]) / 2.0;
        }

        /// <summary>
        /// Clear all derived state back to construction defaults (no offset, empty window, step-ref and
        /// fallback reset). Used on an in-place client reconnect so the next ping burst re-seeds the
        /// offset from scratch rather than reusing a stale estimate.
        /// </summary>
        public void Reset()
        {
            _window.Clear();
            _hasOffset = false;
            _offset = 0.0;
            _usedFallback = false;
            _rejectsSinceAccept = 0;
            _haveBestSeen = false;
            _bestSeenRtt = 0.0;
            _bestSeenOffset = 0.0;
            _haveStepRef = false;
            _stepRefOffset = 0.0;
        }

        /// <summary>
        /// True when the most-recently-applied offset implies a jump larger than
        /// <paramref name="thresholdSeconds"/> versus the previous best — i.e. a real OS clock jump that
        /// should trigger a display hard-set rather than a lerp. Tracks the last reported offset.
        /// </summary>
        public bool IsLargeStep(double thresholdSeconds)
        {
            if (!_hasOffset) return false;
            bool first = !_haveStepRef;
            double delta = first ? 0.0 : Math.Abs(_offset - _stepRefOffset);
            _stepRefOffset = _offset;
            _haveStepRef = true;
            return !first && delta > thresholdSeconds;
        }

        private bool _haveStepRef;
        private double _stepRefOffset;
    }
}
