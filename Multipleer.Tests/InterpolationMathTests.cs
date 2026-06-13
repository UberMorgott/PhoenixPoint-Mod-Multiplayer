using System;
using Multipleer.Network.CommandSync;
using Xunit;

// Tests for the PURE frame-rate-independent render-smoothing math behind ClientVehicleInterpolator.
// The interpolator applies the SAME SmoothFactor to a Vector3.Slerp, so these scalar properties
// (convergence, no overshoot, exact settle, dt-independence) transfer to the rendered globe motion.
public class InterpolationMathTests
{
    private const float K = 18f; // matches ClientVehicleInterpolator.K

    // ─── SmoothFactor basic shape ────────────────────────────────────────────

    [Fact]
    public void SmoothFactor_IsInUnitInterval_ForPositiveInputs()
    {
        // Always in [0,1] — guarantees no overshoot at any frame rate. For realistic frame gaps it is
        // strictly < 1 (asymptotic). A pathological multi-second spike makes exp(-K*dt) underflow float
        // to 0 so the factor saturates at exactly 1f — that is a clean "snap to the latest host position",
        // which still NEVER passes the target (verified by the SmoothTowards overshoot tests).
        foreach (var dt in new[] { 0.001f, 0.016f, 0.1f, 0.5f, 5f })
        {
            float f = InterpolationMath.SmoothFactor(K, dt);
            Assert.True(f >= 0f, $"factor {f} < 0 at dt={dt}");
            Assert.True(f <= 1f, $"factor {f} > 1 at dt={dt}");
        }
        // Realistic per-frame gaps stay strictly below 1 (still easing, not snapping).
        foreach (var dt in new[] { 0.001f, 0.016f, 0.033f, 0.1f })
            Assert.True(InterpolationMath.SmoothFactor(K, dt) < 1f, $"factor saturated at realistic dt={dt}");
    }

    [Fact]
    public void SmoothFactor_IsZero_ForNonPositiveDtOrK()
    {
        // Paused frame / disabled easing => no movement (and no NaN from exp of bad input).
        Assert.Equal(0f, InterpolationMath.SmoothFactor(K, 0f));
        Assert.Equal(0f, InterpolationMath.SmoothFactor(K, -0.5f));
        Assert.Equal(0f, InterpolationMath.SmoothFactor(0f, 0.1f));
        Assert.Equal(0f, InterpolationMath.SmoothFactor(-3f, 0.1f));
    }

    [Fact]
    public void SmoothFactor_IncreasesWithDt()
    {
        // Larger frame gap closes more of the remaining distance (monotonic in dt).
        Assert.True(InterpolationMath.SmoothFactor(K, 0.1f) > InterpolationMath.SmoothFactor(K, 0.05f));
        Assert.True(InterpolationMath.SmoothFactor(K, 0.2f) > InterpolationMath.SmoothFactor(K, 0.1f));
    }

    // ─── SmoothTowards: convergence ──────────────────────────────────────────

    [Fact]
    public void SmoothTowards_Converges_ToTarget_OverManySteps()
    {
        float current = 0f;
        const float target = 100f;
        // ~1s of 60fps frames: must be visually settled on target.
        for (int i = 0; i < 60; i++)
            current = InterpolationMath.SmoothTowards(current, target, K, 1f / 60f);
        Assert.True(Math.Abs(target - current) < 0.01f, $"did not converge: {current}");
    }

    [Fact]
    public void SmoothTowards_ConvergesWithin_OneUpdateInterval_ToMostOfTheGap()
    {
        // Design goal: close the large majority of the gap within ~one 0.1s host snapshot interval.
        float current = 0f;
        const float target = 100f;
        float elapsed = 0f;
        const float dt = 1f / 60f;
        while (elapsed < 0.1f) { current = InterpolationMath.SmoothTowards(current, target, K, dt); elapsed += dt; }
        Assert.True(current > 80f, $"only reached {current} after 0.1s (expected >80% of the gap)");
    }

    // ─── No overshoot / monotonic approach ───────────────────────────────────

    [Fact]
    public void SmoothTowards_NeverOvershoots_Target()
    {
        float current = 0f;
        const float target = 50f;
        for (int i = 0; i < 200; i++)
        {
            float next = InterpolationMath.SmoothTowards(current, target, K, 0.1f);
            Assert.True(next >= current, "moved backward (away from a forward target)");
            Assert.True(next <= target + 1e-4f, $"overshot target: {next} > {target}");
            current = next;
        }
    }

    [Fact]
    public void SmoothTowards_NeverOvershoots_WithLargeDt()
    {
        // Even a huge frame spike (factor near 1) must not pass the target.
        float current = 0f;
        const float target = 7f;
        float next = InterpolationMath.SmoothTowards(current, target, K, 100f);
        Assert.True(next <= target + 1e-4f, $"overshot on big dt: {next}");
        Assert.True(next > target - 0.01f, "should be essentially at target after a huge step");
    }

    [Fact]
    public void SmoothTowards_ApproachesFromAbove_WhenTargetBelow()
    {
        // Symmetry: decreasing direction also eases monotonically without undershoot past target.
        float current = 100f;
        const float target = 20f;
        for (int i = 0; i < 200; i++)
        {
            float next = InterpolationMath.SmoothTowards(current, target, K, 0.1f);
            Assert.True(next <= current, "moved away from a lower target");
            Assert.True(next >= target - 1e-4f, $"undershot target: {next} < {target}");
            current = next;
        }
    }

    // ─── Settles exactly at target (no extrapolation — pure-mirror invariant) ─

    [Fact]
    public void SmoothTowards_SettlesExactly_WhenAlreadyAtTarget()
    {
        // Host updates stopped: current == target. The value must stay put — never drift past (no
        // extrapolation beyond the latest host position).
        const float target = 42f;
        float current = target;
        for (int i = 0; i < 100; i++)
            current = InterpolationMath.SmoothTowards(current, target, K, 1f / 60f);
        Assert.Equal(target, current, 5);
    }

    // ─── Frame-rate independence ─────────────────────────────────────────────

    [Fact]
    public void SmoothTowards_IsFrameRateIndependent()
    {
        // The same wall-clock duration must yield the same eased value regardless of how many frames
        // it is split into (exponential smoothing composes exactly).
        const float target = 100f;
        const float totalTime = 0.25f;

        float coarse = 0f;
        coarse = InterpolationMath.SmoothTowards(coarse, target, K, totalTime); // 1 step

        float fine = 0f;
        const int steps = 250;
        float dt = totalTime / steps;
        for (int i = 0; i < steps; i++) // 250 steps
            fine = InterpolationMath.SmoothTowards(fine, target, K, dt);

        // Both reach the SAME point along the easing curve (within float tolerance).
        Assert.True(Math.Abs(coarse - fine) < 0.5f, $"frame-rate dependent: coarse={coarse} fine={fine}");
    }

    [Fact]
    public void SmoothFactor_ComposesExactly_TwoHalfStepsEqualOneFullStep()
    {
        // The exact algebraic identity behind frame-rate independence:
        //   1 - e^(-k*dt) == 1 - (1 - f_half)^2   where f_half = 1 - e^(-k*dt/2)
        const float dt = 0.1f;
        float full = InterpolationMath.SmoothFactor(K, dt);
        float half = InterpolationMath.SmoothFactor(K, dt / 2f);
        float composed = 1f - (1f - half) * (1f - half);
        Assert.True(Math.Abs(full - composed) < 1e-5f, $"full={full} composed={composed}");
    }
}

// Tests for the PURE native-equation segment-render math (reproduces GeoNavComponent.NavigateRoutine's
// num/totalTime so the client can render the EXACT native arc at the EXACT rate against its host-slaved
// clock). All scale-invariant and Unity-free; the Vector3.Slerp itself lives in the interpolator and is
// driven by the `num` proven here.
public class SegmentRenderMathTests
{
    // A unit-sphere-ish setup centred at origin for clean angle math.
    private const float R = 100f; // arbitrary globe radius; results are radius-invariant by construction.

    // ── GreatCircleAngleRad ──────────────────────────────────────────────────

    [Fact]
    public void GreatCircleAngle_IsZero_ForCoincidentPoints()
    {
        float a = InterpolationMath.GreatCircleAngleRad(R, 0, 0, R, 0, 0, 0, 0, 0);
        Assert.Equal(0f, a, 5);
    }

    [Fact]
    public void GreatCircleAngle_IsQuarterPi_ForKnownSeparation()
    {
        // +X and +Y on a sphere about the origin subtend 90° at the centre.
        float a = InterpolationMath.GreatCircleAngleRad(R, 0, 0, 0, R, 0, 0, 0, 0);
        Assert.Equal((float)(Math.PI / 2.0), a, 4);
    }

    [Fact]
    public void GreatCircleAngle_IsPi_ForAntipodes()
    {
        float a = InterpolationMath.GreatCircleAngleRad(R, 0, 0, -R, 0, 0, 0, 0, 0);
        Assert.Equal((float)Math.PI, a, 4);
    }

    [Fact]
    public void GreatCircleAngle_IsRadiusInvariant()
    {
        // Same directions at two different radii → same subtended angle (the scale-invariance the
        // totalTime derivation relies on).
        float small = InterpolationMath.GreatCircleAngleRad(1, 0, 0, 0, 1, 0, 0, 0, 0);
        float big = InterpolationMath.GreatCircleAngleRad(5000, 0, 0, 0, 5000, 0, 0, 0, 0);
        Assert.Equal(small, big, 5);
    }

    [Fact]
    public void GreatCircleAngle_IsZero_ForDegenerateRadius()
    {
        // A point AT the centre has no defined direction → 0 (caller treats as non-move → ease).
        float a = InterpolationMath.GreatCircleAngleRad(0, 0, 0, R, 0, 0, 0, 0, 0);
        Assert.Equal(0f, a, 5);
    }

    // ── SegmentTotalSeconds ──────────────────────────────────────────────────

    [Fact]
    public void SegmentTotalSeconds_MatchesNativeDerivation()
    {
        // totalTime = angle*6371*3600/speed. For a 90° leg at speed=400 (EarthUnits.Value):
        //   (π/2)*6371*3600/400 ≈ 90034.6 s.
        float angle = (float)(Math.PI / 2.0);
        float total = InterpolationMath.SegmentTotalSeconds(angle, 400f);
        float expected = angle * 6371f * 3600f / 400f;
        Assert.Equal(expected, total, 1);
    }

    [Fact]
    public void SegmentTotalSeconds_ScalesInverselyWithSpeed()
    {
        float angle = 0.5f;
        float slow = InterpolationMath.SegmentTotalSeconds(angle, 200f);
        float fast = InterpolationMath.SegmentTotalSeconds(angle, 400f);
        Assert.Equal(slow / 2f, fast, 2); // double speed → half the time
    }

    [Fact]
    public void SegmentTotalSeconds_IsInvalid_ForNonPositiveSpeed()
    {
        Assert.True(InterpolationMath.SegmentTotalSeconds(0.5f, 0f) < 0f);
        Assert.True(InterpolationMath.SegmentTotalSeconds(0.5f, -10f) < 0f);
    }

    [Fact]
    public void SegmentTotalSeconds_IsZero_ForZeroAngle()
    {
        // Native: totalTime==Zero → num forced to 1 (instant arrival). Here total=0 signals that.
        Assert.Equal(0f, InterpolationMath.SegmentTotalSeconds(0f, 400f));
    }

    // ── SegmentNum (the native ratio) ────────────────────────────────────────

    [Fact]
    public void SegmentNum_IsZeroAtStart_HalfAtMidpoint_OneAtEnd()
    {
        const double start = 1000.0;
        const float total = 200f;
        Assert.Equal(0f, InterpolationMath.SegmentNum(start, start, total), 5);
        Assert.Equal(0.5f, InterpolationMath.SegmentNum(start, start + 100.0, total), 5);
        Assert.Equal(1f, InterpolationMath.SegmentNum(start, start + 200.0, total), 5);
    }

    [Fact]
    public void SegmentNum_ClampsToUnitInterval()
    {
        const double start = 1000.0;
        const float total = 200f;
        Assert.Equal(0f, InterpolationMath.SegmentNum(start, start - 50.0, total), 5);  // before start
        Assert.Equal(1f, InterpolationMath.SegmentNum(start, start + 9999.0, total), 5); // past end
    }

    [Fact]
    public void SegmentNum_IsOne_ForNonPositiveTotal()
    {
        // Mirrors GeoNavComponent.cs:113-116 (totalTime==Zero → num=1).
        Assert.Equal(1f, InterpolationMath.SegmentNum(1000.0, 1000.0, 0f), 5);
        Assert.Equal(1f, InterpolationMath.SegmentNum(1000.0, 1000.0, -5f), 5);
    }

    [Fact]
    public void SegmentNum_IsTimeBased_NotFrameBased()
    {
        // The ratio depends only on elapsed wall-clock vs totalTime — identical whether sampled in 1 step
        // or many (the property that makes the rendered speed native-identical at any frame rate).
        const double start = 0.0;
        const float total = 60f;
        float at30sOnce = InterpolationMath.SegmentNum(start, 30.0, total);
        // Sample the same 30s reached via many sub-steps then land EXACTLY on 30 — value at 30s is the same
        // regardless of how many frames the interval is split into (time-based, not frame-based).
        float at30sStepwise = 0f;
        for (double t = 0.0; t < 30.0; t += 0.37)
            at30sStepwise = InterpolationMath.SegmentNum(start, t, total);
        at30sStepwise = InterpolationMath.SegmentNum(start, 30.0, total);
        Assert.Equal(at30sOnce, at30sStepwise, 4);
        Assert.Equal(0.5f, at30sOnce, 4); // 30/60 = half way
    }

    // ── ArcRatioFromAngles ───────────────────────────────────────────────────

    [Fact]
    public void ArcRatio_ProjectsSampleOntoArc()
    {
        Assert.Equal(0.5f, InterpolationMath.ArcRatioFromAngles(0.25f, 0.5f), 5);
        Assert.Equal(0f, InterpolationMath.ArcRatioFromAngles(0f, 0.5f), 5);
        Assert.Equal(1f, InterpolationMath.ArcRatioFromAngles(0.5f, 0.5f), 5);
    }

    [Fact]
    public void ArcRatio_Clamps_AndHandlesZeroLengthSegment()
    {
        Assert.Equal(1f, InterpolationMath.ArcRatioFromAngles(0.9f, 0.5f), 5); // past end → clamp 1
        Assert.Equal(0f, InterpolationMath.ArcRatioFromAngles(-0.1f, 0.5f), 5); // before start → clamp 0
        Assert.Equal(1f, InterpolationMath.ArcRatioFromAngles(0.3f, 0f), 5);    // zero leg → already arrived
    }

    [Fact]
    public void ArcRatio_IsCenterRelative_NotWorldOriginRelative()
    {
        // The arc geometry (angle + projection) is measured about the GLOBE CENTRE, not world origin — the
        // same frame the recentered render slerp uses (C + Slerp(start-C, end-C, num)). With a NON-origin
        // centre, the geodesic midpoint must project to ~0.5. Centre C=(10,10,10); start/end at ±45° about C
        // in the XY plane (radius 5), sample at 0° (the geodesic midpoint) → ratio 0.5.
        const float cx = 10f, cy = 10f, cz = 10f, rad = 5f;
        double a0 = -Math.PI / 4, a1 = Math.PI / 4, am = 0.0;
        float sx = (float)(cx + rad * Math.Cos(a0)), sy = (float)(cy + rad * Math.Sin(a0));
        float ex = (float)(cx + rad * Math.Cos(a1)), ey = (float)(cy + rad * Math.Sin(a1));
        float mx = (float)(cx + rad * Math.Cos(am)), my = (float)(cy + rad * Math.Sin(am));

        float angStartEnd = InterpolationMath.GreatCircleAngleRad(sx, sy, cz, ex, ey, cz, cx, cy, cz);
        float angStartSample = InterpolationMath.GreatCircleAngleRad(sx, sy, cz, mx, my, cz, cx, cy, cz);
        float ratio = InterpolationMath.ArcRatioFromAngles(angStartSample, angStartEnd);
        Assert.Equal(0.5f, ratio, 3); // geodesic midpoint about C → halfway

        // About C the full leg subtends 90°, the half-leg 45° — center-relative, not origin-relative.
        Assert.Equal((float)(Math.PI / 2.0), angStartEnd, 3);
        Assert.Equal((float)(Math.PI / 4.0), angStartSample, 3);
    }

    // ── CorrectedStartSec (gentle bounded phase correction) ──────────────────

    [Fact]
    public void CorrectedStartSec_NudgesTowardImpliedPhase_ByFactor()
    {
        // Host sample says we're at num=0.5 right now (nowSec=100, total=100 → impliedStart=50). Our segment
        // thinks it started at 60. A factor=0.5 correction moves startSec halfway from 60 toward 50 → 55.
        double corrected = InterpolationMath.CorrectedStartSec(
            curStartSec: 60.0, nowSec: 100.0, totalSec: 100f,
            sampleNum: 0.5f, factor: 0.5f, maxShiftSec: 100f);
        Assert.Equal(55.0, corrected, 4);
    }

    [Fact]
    public void CorrectedStartSec_ClampsTheShift_ToMaxShift()
    {
        // Host sample at num=0 with nowSec=100 → impliedStart = 100 - 0*100 = 100 (host says the leg only just
        // departed). cur=60 → raw delta (100-60)*0.5 = +20, CLAMPED to +maxShift (+2) → 62. The clamp is what
        // stops a transient outlier from rubber-banding the icon.
        double corrected = InterpolationMath.CorrectedStartSec(
            curStartSec: 60.0, nowSec: 100.0, totalSec: 100f,
            sampleNum: 0f, factor: 0.5f, maxShiftSec: 2f);
        Assert.Equal(62.0, corrected, 4);
    }

    [Fact]
    public void CorrectedStartSec_Settles_WhenPhaseAlreadyMatchesHost()
    {
        // If the segment phase already agrees with the host sample, the implied start equals the current
        // start → zero shift (settled, no jitter). impliedStart = 100 - 0.5*100 = 50; cur=50 → unchanged.
        double corrected = InterpolationMath.CorrectedStartSec(
            curStartSec: 50.0, nowSec: 100.0, totalSec: 100f,
            sampleNum: 0.5f, factor: 0.15f, maxShiftSec: 0.5f);
        Assert.Equal(50.0, corrected, 6);
    }

    [Fact]
    public void CorrectedStartSec_ConvergesMonotonically_OverRepeatedSamples()
    {
        // Repeated samples at a fixed host phase drive startSec to the implied value and then HOLD — settles,
        // does not oscillate (frame-gap independent: keyed off the host sample, not dt).
        double start = 70.0;
        const double nowSec = 100.0;
        const float total = 100f;
        const float sampleNum = 0.5f;             // implied start = 50
        double prevErr = Math.Abs(start - 50.0);
        for (int i = 0; i < 50; i++)
        {
            start = InterpolationMath.CorrectedStartSec(start, nowSec, total, sampleNum, 0.15f, 0.5f);
            double err = Math.Abs(start - 50.0);
            Assert.True(err <= prevErr + 1e-9, $"error grew: {err} > {prevErr}");
            prevErr = err;
        }
        Assert.True(Math.Abs(start - 50.0) < 1.0, $"did not converge toward implied start: {start}");
    }

    [Fact]
    public void CorrectedStartSec_IsNoOp_ForZeroTotalOrZeroFactor()
    {
        Assert.Equal(60.0, InterpolationMath.CorrectedStartSec(60.0, 100.0, 0f, 0.5f, 0.15f, 0.5f), 6);
        Assert.Equal(60.0, InterpolationMath.CorrectedStartSec(60.0, 100.0, 100f, 0.5f, 0f, 0.5f), 6);
    }
}
