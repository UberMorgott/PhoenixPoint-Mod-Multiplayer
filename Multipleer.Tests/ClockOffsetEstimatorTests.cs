using Multipleer.Network.TimeSync;
using Xunit;

namespace Multipleer.Tests
{
    /// <summary>
    /// Pure-math tests for the NTP-style 3-stamp clock-offset estimator:
    ///   rtt    = t3 - t0
    ///   offset = t1 - (t0 + t3)/2     (so serverNow = localRT + offset ≈ host localRT)
    /// best-of-lowest-RTT over a sliding window, reject rtt>max, HasOffset gating, large-step detection.
    /// </summary>
    public class ClockOffsetEstimatorTests
    {
        // ─── Pure single-sample math ──────────────────────────────────────
        [Theory]
        // t0, t1, t3, expectedRtt, expectedOffset
        [InlineData(0.0, 5.0, 2.0, 2.0, 4.0)]      // host clock 5 ahead at mid; rtt 2; offset = 5 - 1 = 4
        [InlineData(10.0, 10.0, 10.2, 0.2, -0.1)]  // host ~same; offset = 10 - 10.1 = -0.1
        [InlineData(100.0, 250.5, 101.0, 1.0, 150.0)] // host 150 ahead; offset = 250.5 - 100.5
        public void Sample_ComputesRttAndOffset(double t0, double t1, double t3, double expRtt, double expOffset)
        {
            var s = ClockOffsetEstimator.MakeSample(t0, t1, t3);
            Assert.Equal(expRtt, s.Rtt, 9);
            Assert.Equal(expOffset, s.Offset, 9);
        }

        // ─── HasOffset gating ─────────────────────────────────────────────
        [Fact]
        public void HasOffset_FalseUntilFirstAcceptedSample()
        {
            var est = new ClockOffsetEstimator();
            Assert.False(est.HasOffset);
            est.AddSample(t0: 0.0, t1: 1.0, t3: 0.2); // rtt 0.2 accepted
            Assert.True(est.HasOffset);
        }

        // ─── Reject high-RTT jitter samples ───────────────────────────────
        [Fact]
        public void AddSample_RejectsHighRtt()
        {
            var est = new ClockOffsetEstimator(maxRttSeconds: 0.5);
            // rtt = 2.0 > 0.5 → rejected, no offset stored.
            bool accepted = est.AddSample(t0: 0.0, t1: 100.0, t3: 2.0);
            Assert.False(accepted);
            Assert.False(est.HasOffset);
        }

        // ─── Best-of-lowest-RTT in the window ─────────────────────────────
        [Fact]
        public void Offset_PicksLowestRttSampleInWindow()
        {
            var est = new ClockOffsetEstimator(maxRttSeconds: 1.0, windowSize: 8);
            // High-RTT (but accepted) sample with a skewed offset.
            est.AddSample(t0: 0.0, t1: 10.0, t3: 0.8);   // rtt 0.8, offset = 10 - 0.4 = 9.6
            // Lower-RTT sample with the "true" offset 5.0.
            est.AddSample(t0: 0.0, t1: 5.05, t3: 0.1);   // rtt 0.1, offset = 5.05 - 0.05 = 5.0
            // Best-of-window must prefer the lowest-rtt sample's offset.
            Assert.Equal(5.0, est.Offset, 9);
        }

        [Fact]
        public void Window_EvictsOldestBeyondWindowSize()
        {
            var est = new ClockOffsetEstimator(maxRttSeconds: 10.0, windowSize: 2);
            // Sample A: very low rtt, offset 1.0 — will be evicted.
            est.AddSample(t0: 0.0, t1: 1.0, t3: 0.001);   // rtt 0.001, offset ~1.0 (lowest)
            est.AddSample(t0: 0.0, t1: 7.0, t3: 0.5);     // rtt 0.5, offset = 7 - 0.25 = 6.75
            est.AddSample(t0: 0.0, t1: 8.0, t3: 0.4);     // rtt 0.4, offset = 8 - 0.2 = 7.8
            // Window now holds only the last 2 (the 0.001-rtt sample evicted); best is the 0.4-rtt one.
            Assert.Equal(7.8, est.Offset, 9);
        }

        // ─── Large-step detection (real OS clock jump) ────────────────────
        [Fact]
        public void IsLargeStep_DetectsBigOffsetShift()
        {
            var est = new ClockOffsetEstimator(maxRttSeconds: 1.0, windowSize: 8);
            est.AddSample(t0: 0.0, t1: 5.0, t3: 0.1);   // offset 4.95
            Assert.False(est.IsLargeStep(thresholdSeconds: 2.0)); // first sample: not "step" vs nothing
            // A fresh estimator path: simulate by reading current offset, then a jumped sample.
            double before = est.Offset;
            est.AddSample(t0: 100.0, t1: 200.0, t3: 100.1); // offset ~99.95 → big jump
            Assert.True(System.Math.Abs(est.Offset - before) > 2.0);
        }

        // ─── High-RTT fallback (internet play; no sub-cap sample ever passes) ──
        [Fact]
        public void HighRtt_FallbackEngagesAfterGrace_AdoptsBestSeenOffset()
        {
            // Cap 0.5 s; every sample's rtt is > 0.5 (a >500 ms internet link). Without the fallback
            // HasOffset would never trip → ClientTick gate blocks every clock write → frozen client clock.
            var est = new ClockOffsetEstimator(maxRttSeconds: 0.5, windowSize: 8, fallbackAfterRejects: 3);

            // First two over-cap samples are rejected and DO NOT yet engage the fallback.
            Assert.False(est.AddSample(t0: 0.0, t1: 10.0, t3: 0.9));  // rtt 0.9, offset = 10 - 0.45 = 9.55
            Assert.False(est.HasOffset);
            Assert.False(est.AddSample(t0: 0.0, t1: 8.0, t3: 0.7));   // rtt 0.7 (best seen), offset = 8 - 0.35 = 7.65
            Assert.False(est.HasOffset);

            // Third consecutive over-cap reject meets the grace count → adopt the BEST-RTT seen (0.7).
            Assert.True(est.AddSample(t0: 0.0, t1: 12.0, t3: 0.8));   // rtt 0.8, offset 11.6 (not the best)
            Assert.True(est.HasOffset);
            Assert.True(est.UsedFallback);
            Assert.Equal(7.65, est.Offset, 9);                        // best-seen (0.7 rtt) offset, not the latest
        }

        [Fact]
        public void HighRtt_Fallback_ClearedOnceASubCapSampleArrives()
        {
            var est = new ClockOffsetEstimator(maxRttSeconds: 0.5, fallbackAfterRejects: 2);
            est.AddSample(t0: 0.0, t1: 9.0, t3: 0.8);  // reject 1
            est.AddSample(t0: 0.0, t1: 9.0, t3: 0.9);  // reject 2 → fallback engages
            Assert.True(est.UsedFallback);
            // A genuine sub-cap sample now arrives → normal path takes over, fallback flag clears.
            Assert.True(est.AddSample(t0: 0.0, t1: 5.05, t3: 0.1)); // rtt 0.1 accepted, offset 5.0
            Assert.False(est.UsedFallback);
            Assert.Equal(5.0, est.Offset, 9);
        }

        [Fact]
        public void LowRtt_NeverEngagesFallback_LanUnchanged()
        {
            // A normal sub-cap sample is accepted immediately; the fallback never engages (LAN/DirectIP).
            var est = new ClockOffsetEstimator(maxRttSeconds: 0.5, fallbackAfterRejects: 3);
            Assert.True(est.AddSample(t0: 0.0, t1: 1.0, t3: 0.2)); // rtt 0.2 accepted
            Assert.True(est.HasOffset);
            Assert.False(est.UsedFallback);
        }

        // ─── Reset (in-place client reconnect) ────────────────────────────
        [Fact]
        public void Reset_ClearsOffsetAndStepRefAndFallback()
        {
            var est = new ClockOffsetEstimator(maxRttSeconds: 1.0, windowSize: 8);
            est.AddSample(t0: 0.0, t1: 5.0, t3: 0.1); // offset 4.95
            Assert.True(est.HasOffset);
            est.IsLargeStep(2.0); // arm the step reference

            est.Reset();

            Assert.False(est.HasOffset);                       // gate closed again → re-arms ping burst
            Assert.Equal(0.0, est.Offset, 9);
            Assert.False(est.UsedFallback);
            // Step-ref cleared: the first post-reset sample is "first vs nothing" (no spurious large step).
            est.AddSample(t0: 100.0, t1: 200.0, t3: 100.1);    // a wildly different offset (~99.95)
            Assert.False(est.IsLargeStep(2.0));                // first reference after reset → not a step
        }
    }
}
