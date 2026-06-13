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
        var b = ClientInterpolationCore.Select(new float[] { 0f }, 0, 5f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Empty, b.Mode);
    }

    [Fact]
    public void Select_NullTimes_IsEmpty()
    {
        var b = ClientInterpolationCore.Select(null, 3, 5f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Empty, b.Mode);
    }

    [Fact]
    public void Select_SingleSample_IsDirectAtZero()
    {
        // First mirror / one sample → place it directly (full mirror until ≥2 samples).
        var b = ClientInterpolationCore.Select(new[] { 10f }, 1, 9.9f);
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
        var times = new[] { 1f, 2f, 3f };
        var b = ClientInterpolationCore.Select(times, 3, 0.5f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Direct, b.Mode);
        Assert.Equal(0, b.I0);
    }

    [Fact]
    public void Select_RenderTimeExactlyOldest_ClampsToOldestDirect()
    {
        var times = new[] { 1f, 2f, 3f };
        var b = ClientInterpolationCore.Select(times, 3, 1f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Direct, b.Mode);
        Assert.Equal(0, b.I0);
    }

    // ── Underrun → Hold (NO extrapolation) ───────────────────────────────────

    [Fact]
    public void Select_RenderTimeAfterNewest_HoldsNewest_NoExtrapolation()
    {
        // Host paused / craft parked → renderTime overruns the newest sample → HOLD the last one. Critically
        // this is NOT extrapolation: it returns the newest index with frac 0, so the icon stands still.
        var times = new[] { 1f, 2f, 3f };
        var b = ClientInterpolationCore.Select(times, 3, 9f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Hold, b.Mode);
        Assert.Equal(2, b.I0);
        Assert.Equal(2, b.I1);
        Assert.Equal(0f, b.Frac);
    }

    [Fact]
    public void Select_RenderTimeExactlyNewest_HoldsNewest()
    {
        var times = new[] { 1f, 2f, 3f };
        var b = ClientInterpolationCore.Select(times, 3, 3f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Hold, b.Mode);
        Assert.Equal(2, b.I0);
    }

    // ── Interior interpolation (Interp) ──────────────────────────────────────

    [Fact]
    public void Select_Midpoint_InterpolatesHalfway()
    {
        var times = new[] { 0f, 1f, 2f };
        var b = ClientInterpolationCore.Select(times, 3, 1.5f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Interp, b.Mode);
        Assert.Equal(1, b.I0);
        Assert.Equal(2, b.I1);
        Assert.Equal(0.5f, b.Frac, 5);
    }

    [Fact]
    public void Select_QuarterIntoFirstGap_PicksFirstPair()
    {
        var times = new[] { 0f, 4f, 8f };
        var b = ClientInterpolationCore.Select(times, 3, 1f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Interp, b.Mode);
        Assert.Equal(0, b.I0);
        Assert.Equal(1, b.I1);
        Assert.Equal(0.25f, b.Frac, 5);
    }

    [Fact]
    public void Select_AtLowerSampleOfGap_FracIsZero()
    {
        // renderTime exactly on an interior sample → that sample is the lower bound, frac 0 (place it, slerp no-op).
        var times = new[] { 0f, 4f, 8f };
        var b = ClientInterpolationCore.Select(times, 3, 4f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Interp, b.Mode);
        Assert.Equal(1, b.I0);
        Assert.Equal(2, b.I1);
        Assert.Equal(0f, b.Frac, 5);
    }

    [Fact]
    public void Select_Frac_AlwaysInUnitInterval()
    {
        var times = new[] { 0f, 1f, 2f, 3f, 4f };
        foreach (var rt in new[] { 0.1f, 0.999f, 1.5f, 2.0f, 2.5f, 3.9f })
        {
            var b = ClientInterpolationCore.Select(times, times.Length, rt);
            Assert.True(b.Frac >= 0f && b.Frac <= 1f, $"frac {b.Frac} out of [0,1] at rt={rt}");
        }
    }

    [Fact]
    public void Select_EqualAdjacentTimestamps_NoDivideByZero()
    {
        // Two samples stamped in the same frame (zero gap) must not divide by zero; degenerate to frac 0.
        var times = new[] { 1f, 1f, 5f };
        var b = ClientInterpolationCore.Select(times, 3, 1f);
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
        var times = new[] { 0f, 2f, 2f, 5f };
        var b = ClientInterpolationCore.Select(times, 4, 2f);
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
        var times = new[] { 0f, 1f, 2f, 99f, 99f };
        var b = ClientInterpolationCore.Select(times, 3, 5f);
        Assert.Equal(ClientInterpolationCore.SampleMode.Hold, b.Mode);
        Assert.Equal(2, b.I0); // newest of the first 3, not the trailing 99f slots
    }
}
