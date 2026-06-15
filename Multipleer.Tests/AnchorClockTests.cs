using System;
using Multipleer.Network.TimeSync;
using Xunit;

namespace Multipleer.Tests
{
    /// <summary>
    /// Pure-math tests for the anchor+rate authoritative clock (no feedback loop):
    ///   Derive(gAnchor, rate, tAnchor, serverNow) = gAnchor + rate*(serverNow - tAnchor)
    ///   VisualStep(display, auth, k, snapThreshold, rate) = smoothed display toward auth (forward-monotone
    ///     hard-set on a large gap).
    /// </summary>
    public class AnchorClockTests
    {
        // ─── Derivation ───────────────────────────────────────────────────
        [Theory]
        // gAnchor, rate, tAnchor, serverNow, expected
        [InlineData(0.0, 0.0, 0.0, 100.0, 0.0)]          // paused (rate 0) → constant gAnchor regardless of time
        [InlineData(1000.0, 0.0, 5.0, 5000.0, 1000.0)]   // paused at a non-zero anchor
        [InlineData(0.0, 1.0, 0.0, 50.0, 50.0)]          // rate 1 → game-seconds == elapsed real-seconds
        [InlineData(0.0, 2.0, 0.0, 50.0, 100.0)]         // rate 2 → double
        [InlineData(0.0, 3.0, 0.0, 50.0, 150.0)]         // rate 3
        [InlineData(100.0, 2.0, 10.0, 30.0, 140.0)]      // 100 + 2*(30-10)
        public void Derive_Linear(double gAnchor, double rate, double tAnchor, double serverNow, double expected)
        {
            Assert.Equal(expected, AnchorClock.Derive(gAnchor, rate, tAnchor, serverNow), 9);
        }

        [Fact]
        public void Derive_LargeGameTime_NoPrecisionBlowup()
        {
            // ~100 game-years of game time (3.15576e9 s) at rate 1.
            double gAnchor = 3.15576e9;
            double auth = AnchorClock.Derive(gAnchor, 1.0, 0.0, 60.0);
            Assert.Equal(gAnchor + 60.0, auth, 3);
        }

        // ─── Continuity across an anchor switch (the core no-jump property) ──
        [Fact]
        public void Derive_ContinuousAcrossAnchorSwitch()
        {
            // At the switch instant the host re-captures gAnchor at the current derived time, so the
            // old-anchor auth == new-anchor auth at that serverNow (piecewise-linear, no jump).
            double switchServerNow = 120.0;

            // Old anchor: started at gAnchor=0 (tAnchor=0) running rate 2.
            double oldAuthAtSwitch = AnchorClock.Derive(0.0, 2.0, 0.0, switchServerNow); // = 240

            // Host captures a NEW anchor at the switch instant: gAnchor = oldAuthAtSwitch, tAnchor = now,
            // new rate (e.g. switching to speed 1). At the switch instant the new curve must equal it.
            double newAuthAtSwitch = AnchorClock.Derive(oldAuthAtSwitch, 1.0, switchServerNow, switchServerNow);
            Assert.Equal(oldAuthAtSwitch, newAuthAtSwitch, 9);

            // And the new curve continues forward from there with the new rate.
            double newAuthLater = AnchorClock.Derive(oldAuthAtSwitch, 1.0, switchServerNow, switchServerNow + 10.0);
            Assert.Equal(oldAuthAtSwitch + 10.0, newAuthLater, 9);
        }

        [Fact]
        public void Derive_PauseThenUnpause_NoJump()
        {
            // Running rate 2, paused at serverNow=50 → gAnchor frozen at derived value.
            double frozen = AnchorClock.Derive(0.0, 2.0, 0.0, 50.0); // 100
            // While paused (rate 0), auth stays at frozen no matter the serverNow.
            Assert.Equal(frozen, AnchorClock.Derive(frozen, 0.0, 50.0, 200.0), 9);
            // Unpause at serverNow=200: new anchor gAnchor=frozen, tAnchor=200, rate 2 → at the instant equals frozen.
            Assert.Equal(frozen, AnchorClock.Derive(frozen, 2.0, 200.0, 200.0), 9);
        }

        // ─── Monotone while rate > 0 ───────────────────────────────────────
        [Fact]
        public void Derive_MonotoneWhileRunning()
        {
            double prev = double.NegativeInfinity;
            for (double sn = 0.0; sn <= 100.0; sn += 5.0)
            {
                double auth = AnchorClock.Derive(10.0, 2.0, 0.0, sn);
                Assert.True(auth >= prev);
                prev = auth;
            }
        }

        // ─── VisualStep: smooth convergence ────────────────────────────────
        [Fact]
        public void VisualStep_ConvergesTowardAuth()
        {
            double display = 0.0;
            const double auth = 100.0;
            const double k = 0.3;
            for (int i = 0; i < 100; i++)
                display = AnchorClock.VisualStep(display, auth, k, snapThreshold: 2.0, rate: 1.0);
            Assert.Equal(auth, display, 3);
        }

        [Fact]
        public void VisualStep_ConstantAuth_SettlesAndStays()
        {
            // Fed a constant auth, display converges to it and then stays (no independent integration).
            const double auth = 42.0;
            double display = 41.5; // within snap threshold → lerp
            const double k = 0.5;
            for (int i = 0; i < 200; i++)
                display = AnchorClock.VisualStep(display, auth, k, snapThreshold: 2.0, rate: 1.0);
            Assert.Equal(auth, display, 6);
            // One more step: stays put.
            Assert.Equal(auth, AnchorClock.VisualStep(display, auth, k, 2.0, rate: 1.0), 6);
        }

        [Fact]
        public void VisualStep_HardSetsOnLargeGap()
        {
            // |auth - display| > snapThreshold → jump straight to auth (skip lerp), e.g. first frame after join.
            double display = 0.0;
            double auth = 1000.0;
            double next = AnchorClock.VisualStep(display, auth, k: 0.1, snapThreshold: 2.0, rate: 1.0);
            Assert.Equal(auth, next, 9); // hard set, not 0 + 0.1*1000 = 100
        }

        [Fact]
        public void VisualStep_HardSetsOnLargeBackwardGap()
        {
            // Genuine backward correction beyond the snap threshold hard-sets (rare; OS clock jump).
            double display = 1000.0;
            double auth = 10.0;
            double next = AnchorClock.VisualStep(display, auth, k: 0.1, snapThreshold: 2.0, rate: 1.0);
            Assert.Equal(auth, next, 9);
        }

        [Fact]
        public void VisualStep_ForwardMonotone_NoVisibleRewind_OnSmallBackwardCorrection_WhileRunning()
        {
            // While RUNNING (rate > 0), a tiny negative correction (within the snap threshold) must NOT
            // visibly rewind the clock: clamp forward (hold the current display rather than decreasing it).
            double display = 100.0;
            double auth = 99.5; // 0.5 behind, within 2s snap threshold
            double next = AnchorClock.VisualStep(display, auth, k: 0.5, snapThreshold: 2.0, rate: 2.0);
            Assert.True(next >= display); // never decreases
            Assert.Equal(100.0, next, 9); // held, not lerped down to 99.75
        }

        [Fact]
        public void VisualStep_Paused_SettlesDownToFrozenTruth_NoForwardClamp()
        {
            // When PAUSED (rate == 0) the forward-monotone clamp is OFF: a small backward correction must
            // be allowed so the display settles DOWN onto the frozen truth (spec §7 — no sub-frame bias).
            double display = 100.0;
            double auth = 99.5; // frozen truth slightly below the displayed value, within snap threshold
            double next = AnchorClock.VisualStep(display, auth, k: 0.5, snapThreshold: 2.0, rate: 0.0);
            Assert.True(next < display);     // allowed to move down
            Assert.Equal(99.75, next, 9);    // lerped down toward the frozen truth (not held)
            // Iterating to convergence lands exactly on the frozen truth.
            for (int i = 0; i < 200; i++)
                next = AnchorClock.VisualStep(next, auth, k: 0.5, snapThreshold: 2.0, rate: 0.0);
            Assert.Equal(auth, next, 9);
        }

        [Fact]
        public void VisualStep_ForwardSmallCorrection_LerpsUp()
        {
            // A small forward correction (within snap threshold) lerps up smoothly.
            double display = 99.0;
            double auth = 100.0;
            double next = AnchorClock.VisualStep(display, auth, k: 0.5, snapThreshold: 2.0, rate: 1.0);
            Assert.True(next > display && next < auth);
            Assert.Equal(99.5, next, 9);
        }

        [Fact]
        public void LerpFactor_FromTauAndDt_InRange()
        {
            // k = 1 - exp(-dt/tau); for tau≈0.15 and a typical 60fps dt, k is a sane fraction in (0,1).
            double k = AnchorClock.LerpFactor(dt: 1.0 / 60.0, tau: 0.15);
            Assert.True(k > 0.0 && k < 1.0);
            // Larger dt → larger k (faster catch-up).
            Assert.True(AnchorClock.LerpFactor(0.1, 0.15) > AnchorClock.LerpFactor(0.01, 0.15));
            // Degenerate tau → snap (k=1).
            Assert.Equal(1.0, AnchorClock.LerpFactor(0.016, 0.0), 9);
        }
    }
}
