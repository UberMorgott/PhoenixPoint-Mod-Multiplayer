using System;
using System.Collections.Generic;
using Multipleer.Network.Sync.State;
using Xunit;

// Inc4 S2 travel-mirror SMOOTHING — pure snapshot-interpolation core. Locks the interpolation contract that
// turns the host's ~4 Hz absolute-placement snapshots into per-frame native-smooth motion on the client:
// slerp between the two straddling snapshots at (now - delay), clamp-and-hold on a starved buffer (no
// extrapolation), stale-purge, and seq-regression drop. The math is managed (System.Math) so it runs in the
// bare test host without the Unity native runtime.
public class VehicleInterpolatorTests
{
    private const double Delay = 0.375;   // render latency (seconds)
    private const double Ttl = 5.0;       // stale-purge window (seconds)

    private static GeoVehiclePos Rec(float z, float qx, float qy, float qz, float qw)
        => new GeoVehiclePos(1, 1, 0f, 0f, z, qx, qy, qz, qw);

    // ── the core smoothing behaviour: two snapshots, sample at the render midpoint → true slerp/lerp blend ──
    [Fact]
    public void TwoSnapshots_SampleAtMidpoint_BlendsHalfway()
    {
        var interp = new VehicleInterpolator(Delay, Ttl);
        long key = GeoVehiclePos.MakeKey(1, 1);
        // A = identity rotation, heading z=0 at t=0.0; B = 90° about Y, heading z=90 at t=1.0.
        interp.Push(key, 1, Rec(0f, 0f, 0f, 0f, 1f), 0.0);
        interp.Push(key, 2, Rec(90f, 0f, 0.70710678f, 0f, 0.70710678f), 1.0);

        // renderTime = now - Delay = 0.5 → exactly halfway between the two snapshots.
        Assert.True(interp.TrySample(key, 0.5 + Delay, out var s));

        // Slerp midpoint of identity↔90°-Y is 45° about Y = (0, sin22.5, 0, cos22.5).
        Assert.Equal(0f, s.QX, 4);
        Assert.Equal(0.38268343f, s.QY, 4);
        Assert.Equal(0f, s.QZ, 4);
        Assert.Equal(0.92387953f, s.QW, 4);
        // Heading lerps linearly (shortest arc): halfway from 0°→90° = 45°.
        Assert.Equal(45f, s.Z, 3);
    }

    // ── starved buffer: renderTime past the newest sample → clamp-and-hold the newest, never extrapolate ──
    [Fact]
    public void StarvedBuffer_ClampsToNewest_NoExtrapolation()
    {
        var interp = new VehicleInterpolator(Delay, Ttl);
        long key = GeoVehiclePos.MakeKey(1, 1);
        interp.Push(key, 1, Rec(0f, 0f, 0f, 0f, 1f), 0.0);
        interp.Push(key, 2, Rec(90f, 0f, 0.70710678f, 0f, 0.70710678f), 1.0);

        // now huge → renderTime ≫ newest(1.0): must hold EXACTLY the newest sample (B), not overshoot past 90°.
        Assert.True(interp.TrySample(key, 100.0, out var s));
        Assert.Equal(0.70710678f, s.QY, 5);
        Assert.Equal(0.70710678f, s.QW, 5);
        Assert.Equal(90f, s.Z, 4);
    }

    [Fact]
    public void SingleSnapshot_ClampsToIt()
    {
        var interp = new VehicleInterpolator(Delay, Ttl);
        long key = GeoVehiclePos.MakeKey(1, 1);
        interp.Push(key, 7, Rec(30f, 0f, 0f, 0.25881905f, 0.96592583f), 2.0);

        // Any sample time with a single frame clamps to that frame.
        Assert.True(interp.TrySample(key, 2.0 + Delay, out var s));   // renderTime == frame time
        Assert.Equal(0.25881905f, s.QZ, 5);
        Assert.Equal(30f, s.Z, 4);
        Assert.True(interp.TrySample(key, 50.0, out var s2));         // far future → still that frame
        Assert.Equal(30f, s2.Z, 4);
    }

    [Fact]
    public void NoData_TrySampleReturnsFalse()
    {
        var interp = new VehicleInterpolator(Delay, Ttl);
        Assert.False(interp.TrySample(GeoVehiclePos.MakeKey(9, 9), 1.0, out _));
    }

    // ── stale purge: a key not refreshed within the TTL is dropped; a fresh one survives ──
    [Fact]
    public void PurgeStale_DropsUnrefreshed_KeepsFresh()
    {
        var interp = new VehicleInterpolator(Delay, staleTtlSeconds: 5.0);
        long stale = GeoVehiclePos.MakeKey(1, 1);
        long fresh = GeoVehiclePos.MakeKey(2, 1);
        interp.Push(stale, 1, Rec(0f, 0f, 0f, 0f, 1f), 0.0);   // last arrival 0.0
        interp.Push(fresh, 1, Rec(0f, 0f, 0f, 0f, 1f), 8.0);   // last arrival 8.0
        Assert.Equal(2, interp.Count);

        var removed = new List<long>();
        int n = interp.PurgeStale(now: 10.0, removed);   // 10-0=10 > 5 → stale; 10-8=2 < 5 → fresh survives

        Assert.Equal(1, n);
        Assert.Contains(stale, removed);
        Assert.Equal(1, interp.Count);
        Assert.False(interp.TrySample(stale, 10.0, out _));
        Assert.True(interp.TrySample(fresh, 10.0, out _));
    }

    // ── seq regression: an older-or-equal seq must be ignored so a stale re-send can't rewind the mirror ──
    [Fact]
    public void SeqRegression_IsIgnored()
    {
        var interp = new VehicleInterpolator(Delay, Ttl);
        long key = GeoVehiclePos.MakeKey(1, 1);
        interp.Push(key, 5, Rec(90f, 0f, 0.70710678f, 0f, 0.70710678f), 1.0);  // newest = seq 5, z=90
        interp.Push(key, 3, Rec(0f, 0f, 0f, 0f, 1f), 2.0);                     // OLDER seq → ignored
        interp.Push(key, 5, Rec(0f, 0f, 0f, 0f, 1f), 3.0);                     // EQUAL seq (dup) → ignored

        // If either had been accepted the newest-by-arrival frame would carry z=0; it must still be seq-5's z=90.
        Assert.True(interp.TrySample(key, 100.0, out var s));
        Assert.Equal(90f, s.Z, 4);
        Assert.Equal(0.70710678f, s.QY, 5);
    }

    // ── managed slerp reproduces Unity's SHORTEST-arc semantics (negative dot → flip) ──
    [Fact]
    public void Slerp_TakesShortestArc_OnNegativeDot()
    {
        // a = identity; b = -(90°-Y) — numerically the same rotation as +(90°-Y) but with a negative dot to a.
        // Shortest-arc slerp must land on the 45°-Y midpoint (same as the positive-dot case), not the long way.
        VehicleInterpolator.Slerp(0f, 0f, 0f, 1f,
                                  0f, -0.70710678f, 0f, -0.70710678f, 0.5f,
                                  out float ox, out float oy, out float oz, out float ow);
        // Result is unit-length and equals ±(0, sin22.5, 0, cos22.5); normalize sign via qw>0.
        if (ow < 0f) { ox = -ox; oy = -oy; oz = -oz; ow = -ow; }
        Assert.Equal(0f, ox, 4);
        Assert.Equal(0.38268343f, oy, 4);
        Assert.Equal(0f, oz, 4);
        Assert.Equal(0.92387953f, ow, 4);
    }

    // ── managed LerpAngle wraps the short way across 360° (350°→10° passes through 0°, not backwards) ──
    [Fact]
    public void LerpAngle_WrapsShortestArc()
    {
        // 350°→10° is a +20° arc through 360°/0° (short way), NOT a −340° arc back through 180° (long way).
        // Midpoint = 350 + 20*0.5 = 360°, which is Unity Mathf.LerpAngle's exact output (≡ 0°; localEulerAngles
        // normalizes it on assignment). Assert it went the SHORT way by normalizing to [0,360).
        Assert.Equal(0f, Norm360(VehicleInterpolator.LerpAngle(350f, 10f, 0.5f)), 3);
        Assert.Equal(45f, VehicleInterpolator.LerpAngle(0f, 90f, 0.5f), 3);      // plain interior lerp
        Assert.Equal(355f, VehicleInterpolator.LerpAngle(350f, 10f, 0.25f), 3);  // 1/4 of the way 350→(360) = 355
    }

    private static float Norm360(float a)
    {
        float r = a % 360f;
        return r < 0f ? r + 360f : r;
    }
}
