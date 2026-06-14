using Multipleer.Network.CommandSync;
using Xunit;

// Tests for the PURE (Unity-free) snapshot-buffer bracket selector behind the INC-C client interpolator.
// These pin the render-time → sample-pair mapping: empty / single / clamp-to-oldest (Direct), underrun (Hold,
// NO extrapolation), and the interior interpolation fraction (Interp). The Vector3.Lerp / Quaternion.Slerp the
// interpolator applies are mechanical given the (i0,i1,frac) this selector returns, so verifying the selector
// verifies the smoothing's correctness without needing Unity in the test host.
public class ClientInterpolationCoreTests
{
    // ── Degenerate / edge counts ─────────────────────────────────────────────

    [Fact]
    public void Select_EmptyBuffer_IsEmpty()
    {
        var b = ClientInterpolationCore.Select(new double[] { 0.0 }, 0, 5.0);
        Assert.Equal(ClientInterpolationCore.SampleMode.Empty, b.Mode);
    }

    [Fact]
    public void Select_NullTimes_IsEmpty()
    {
        var b = ClientInterpolationCore.Select(null, 3, 5.0);
        Assert.Equal(ClientInterpolationCore.SampleMode.Empty, b.Mode);
    }

    [Fact]
    public void Select_SingleSample_IsDirectAtZero()
    {
        // First mirror / one sample → place it directly (full mirror until ≥2 samples).
        var b = ClientInterpolationCore.Select(new[] { 10.0 }, 1, 9.9);
        Assert.Equal(ClientInterpolationCore.SampleMode.Direct, b.Mode);
        Assert.Equal(0, b.I0);
        Assert.Equal(0, b.I1);
        Assert.Equal(0f, b.Frac);
    }

    // ── Clamp to oldest (Direct) ─────────────────────────────────────────────

    [Fact]
    public void Select_RenderTimeBeforeOldest_ClampsToOldestDirect()
    {
        // renderTime older than every sample → place the oldest raw (never extrapolate backwards).
        var times = new[] { 1.0, 2.0, 3.0 };
        var b = ClientInterpolationCore.Select(times, 3, 0.5);
        Assert.Equal(ClientInterpolationCore.SampleMode.Direct, b.Mode);
        Assert.Equal(0, b.I0);
    }

    [Fact]
    public void Select_RenderTimeExactlyOldest_ClampsToOldestDirect()
    {
        var times = new[] { 1.0, 2.0, 3.0 };
        var b = ClientInterpolationCore.Select(times, 3, 1.0);
        Assert.Equal(ClientInterpolationCore.SampleMode.Direct, b.Mode);
        Assert.Equal(0, b.I0);
    }

    // ── Underrun → Hold (NO extrapolation) ───────────────────────────────────

    [Fact]
    public void Select_RenderTimeAfterNewest_HoldsNewest_NoExtrapolation()
    {
        // Host paused / craft parked → renderTime overruns the newest sample → HOLD the last one. Critically
        // this is NOT extrapolation: it returns the newest index with frac 0, so the icon stands still.
        var times = new[] { 1.0, 2.0, 3.0 };
        var b = ClientInterpolationCore.Select(times, 3, 9.0);
        Assert.Equal(ClientInterpolationCore.SampleMode.Hold, b.Mode);
        Assert.Equal(2, b.I0);
        Assert.Equal(2, b.I1);
        Assert.Equal(0f, b.Frac);
    }

    [Fact]
    public void Select_RenderTimeExactlyNewest_HoldsNewest()
    {
        var times = new[] { 1.0, 2.0, 3.0 };
        var b = ClientInterpolationCore.Select(times, 3, 3.0);
        Assert.Equal(ClientInterpolationCore.SampleMode.Hold, b.Mode);
        Assert.Equal(2, b.I0);
    }

    // ── Interior interpolation (Interp) ──────────────────────────────────────

    [Fact]
    public void Select_Midpoint_InterpolatesHalfway()
    {
        var times = new[] { 0.0, 1.0, 2.0 };
        var b = ClientInterpolationCore.Select(times, 3, 1.5);
        Assert.Equal(ClientInterpolationCore.SampleMode.Interp, b.Mode);
        Assert.Equal(1, b.I0);
        Assert.Equal(2, b.I1);
        Assert.Equal(0.5f, b.Frac, 5);
    }

    [Fact]
    public void Select_QuarterIntoFirstGap_PicksFirstPair()
    {
        var times = new[] { 0.0, 4.0, 8.0 };
        var b = ClientInterpolationCore.Select(times, 3, 1.0);
        Assert.Equal(ClientInterpolationCore.SampleMode.Interp, b.Mode);
        Assert.Equal(0, b.I0);
        Assert.Equal(1, b.I1);
        Assert.Equal(0.25f, b.Frac, 5);
    }

    [Fact]
    public void Select_AtLowerSampleOfGap_FracIsZero()
    {
        // renderTime exactly on an interior sample → that sample is the lower bound, frac 0 (place it, slerp no-op).
        var times = new[] { 0.0, 4.0, 8.0 };
        var b = ClientInterpolationCore.Select(times, 3, 4.0);
        Assert.Equal(ClientInterpolationCore.SampleMode.Interp, b.Mode);
        Assert.Equal(1, b.I0);
        Assert.Equal(2, b.I1);
        Assert.Equal(0f, b.Frac, 5);
    }

    [Fact]
    public void Select_Frac_AlwaysInUnitInterval()
    {
        var times = new[] { 0.0, 1.0, 2.0, 3.0, 4.0 };
        foreach (var rt in new[] { 0.1, 0.999, 1.5, 2.0, 2.5, 3.9 })
        {
            var b = ClientInterpolationCore.Select(times, times.Length, rt);
            Assert.True(b.Frac >= 0f && b.Frac <= 1f, $"frac {b.Frac} out of [0,1] at rt={rt}");
        }
    }

    [Fact]
    public void Select_EqualAdjacentTimestamps_NoDivideByZero()
    {
        // Two samples stamped in the same frame (zero gap) must not divide by zero; degenerate to frac 0.
        var times = new[] { 1.0, 1.0, 5.0 };
        var b = ClientInterpolationCore.Select(times, 3, 1.0);
        // renderTime==oldest → clamp to Direct(0); ensure no NaN/exception path.
        Assert.Equal(ClientInterpolationCore.SampleMode.Direct, b.Mode);
        Assert.False(float.IsNaN(b.Frac));
    }

    [Fact]
    public void Select_EqualInteriorTimestamps_PicksFollowingPair_NoNaN()
    {
        // A duplicate INTERIOR timestamp (e.g. two samples at t=2 in {0,2,2,5}) must not strand the scan or
        // emit NaN. renderTime=2 falls on the lower bound of the (2,5) pair → frac 0, clean Interp. (The
        // zero-span guard at ClientInterpolationCore span>0f?…:0f is defensively unreachable for strictly
        // ascending input since rt>=t0 && rt<t1 cannot hold when t0==t1; this asserts the live behavior.)
        var times = new[] { 0.0, 2.0, 2.0, 5.0 };
        var b = ClientInterpolationCore.Select(times, 4, 2.0);
        Assert.Equal(ClientInterpolationCore.SampleMode.Interp, b.Mode);
        Assert.Equal(2, b.I0);
        Assert.Equal(3, b.I1);
        Assert.Equal(0f, b.Frac, 5);
        Assert.False(float.IsNaN(b.Frac));
    }

    [Fact]
    public void Select_OnlyCountSamplesConsidered_TrailingSlotsIgnored()
    {
        // Count<array length: stale trailing slots (here 99f) must be ignored — newest is times[count-1]=2f.
        var times = new[] { 0.0, 1.0, 2.0, 99.0, 99.0 };
        var b = ClientInterpolationCore.Select(times, 3, 5.0);
        Assert.Equal(ClientInterpolationCore.SampleMode.Hold, b.Mode);
        Assert.Equal(2, b.I0); // newest of the first 3, not the trailing 99f slots
    }

    // ── FIX B: travel-direction heading source ───────────────────────────────
    // The client nose now aims along travelDir = Pos[i1] − Pos[i0] (the Interp bracket), instant:true, instead of
    // at the far DestinationSites waypoint — killing the per-frame slerp-vs-jitter wobble. The heading math is
    // reflection-bound (Unity GeoNavComponent), so what is offline-verifiable is the invariant the interpolator
    // relies on: in the Interp case the bracket is a STRICTLY FORWARD pair (i1 is the newer/ahead sample, i0 the
    // older), so Pos[i1]−Pos[i0] points the way the host is actually moving (never backwards). Pinning this guards
    // the direction sign feeding UpdateVehicleHeadingAlong.
    [Fact]
    public void Select_InterpBracket_IsForwardPair_ForTravelDirection()
    {
        var times = new[] { 0.0, 1.0, 2.0, 3.0 };
        foreach (var rt in new[] { 0.4, 1.5, 2.7 })
        {
            var b = ClientInterpolationCore.Select(times, times.Length, rt);
            Assert.Equal(ClientInterpolationCore.SampleMode.Interp, b.Mode);
            // i1 is the AHEAD (newer) sample, i0 the behind one → (Pos[i1]-Pos[i0]) is the forward travel vector.
            Assert.True(b.I1 > b.I0, $"bracket not forward at rt={rt}: i0={b.I0} i1={b.I1}");
            Assert.Equal(b.I0 + 1, b.I1); // adjacent samples → the immediate motion segment
        }
    }
}
