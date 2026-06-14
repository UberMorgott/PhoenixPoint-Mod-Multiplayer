using System;
using System.Collections.Generic;
using Multipleer.Network.CommandSync;
using Xunit;

// ── OFFLINE NETCODE HARNESS #3 — REALISTIC GEOSCAPE-CLOCK MAGNITUDE (float32 precision collapse) ──
//
// Reproduces — without the game — the THIRD, deepest in-game flaw that harnesses #1/#2 MISSED because they
// used host-time values near 0 (where float32 is precise). The IN-GAME failure lives at the ABSOLUTE
// MAGNITUDE of the geoscape clock:
//
//   • HostSendTime = GeoLevelController.Timing.Now is GAME-time seconds since the campaign epoch. Live DIAG
//     measured it at ≈ 6.4568670e10 game-seconds.
//   • At that magnitude a float32 ULP (smallest representable step) is ≈ 8192 game-seconds. The live DIAG
//     printed window=8192.000 and oldestHostTime == newestHostTime == 64568670000.00 — every buffered stamp
//     had collapsed to ONE float.
//   • Consecutive host stamps are only ≈ FlushReal(0.066) × Scale(~3500) ≈ 231 game-seconds apart — ~28×
//     SMALLER than one float32 ULP. So ALL buffered samples QUANTIZE to the SAME float32 at stamp time (the
//     wire field) and in a float ring → Select sees times[0] == times[1] == … → renderTime <= times[0] →
//     Direct EVERY frame → interpolation impossible despite a full ring → huge lag + stepped jerk.
//
// FIX = the TIME REPRESENTATION: carry HostSendTime as double end-to-end (wire + all interpolation math).
// Positions stay float — only TIME goes double. These tests assert the time keys survive DISTINCT at 6.4e10
// magnitude and select Interp. With the OLD float wire/ring they collapse → RED; with double → GREEN.
public class NetcodeRealisticMagnitudeHarnessTests
{
    // Realistic geoscape clock base + spacing from the live DIAG (≈6.4568670e10 game-s, ~231 game-s/sample).
    private const double GeoBase = 64568670000.0;   // live-measured Timing.Now magnitude
    private const double SampleGap = 231.0;         // FlushReal(0.066) × Scale(~3500) game-seconds per sample
    private const double Origin = 100.0;
    private const double Vel = 7.0;                  // host units per GAME-second
    private static double HostPos(double hostGameT) => Origin + Vel * hostGameT;

    // ── PURE WITNESS of the defect mechanism (runs against ANY pipeline): two host stamps one sample-gap apart
    // at 6.4e10 magnitude COLLAPSE to the same float32, while double keeps them distinct. This is the exact
    // precision loss that the float HostSendTime wire field + float ring suffered in-game. ────────────────────
    [Fact]
    public void Float32_CollapsesGeoscapeStamps_DoubleDoesNot()
    {
        // float32 ULP at ~6.4e10 is ~4096–8192 game-s; a whole campaign-second of host motion is far below it.
        // Two DISTINCT host instants 1 game-second apart therefore map to the SAME float32 (precision is gone),
        // while double keeps them distinct. This is the precision collapse the float HostSendTime wire/ring hit.
        double t0 = GeoBase;
        double t1 = GeoBase + 1.0;          // 1 game-second apart — vastly smaller than one float32 ULP here

        float f0 = (float)t0, f1 = (float)t1;
        Assert.True(f0 == f1, $"float32 must collapse sub-ULP geoscape stamps (f0={f0:F1} f1={f1:F1})");

        // double preserves them → this is what the fix relies on end-to-end.
        Assert.NotEqual(t0, t1);
    }

    // The production wire codec must carry HostSendTime with enough precision that two stamps one sample-gap
    // apart at geoscape magnitude survive DISTINCT. With a float32 wire field they decode EQUAL (RED); with a
    // double wire field they survive (GREEN).
    [Fact]
    public void Wire_HostSendTime_SurvivesDistinct_AtGeoscapeMagnitude()
    {
        double t0 = GeoBase;
        double t1 = GeoBase + SampleGap;

        var r0 = Roundtrip(t0);
        var r1 = Roundtrip(t1);

        Assert.NotEqual(r0, r1);
        Assert.Equal(t0, r0);
        Assert.Equal(t1, r1);
    }

    // Drive the production interpolation core at realistic ABSOLUTE magnitude and require Interp (not
    // Direct/Hold) + smooth host-rate tracking. With float-keyed samples every stamp collapses → Direct every
    // frame (RED). With double keys throughout → Interp + smooth (GREEN).
    [Fact]
    public void Interpolation_IsInterp_AndSmooth_AtGeoscapeMagnitude()
    {
        const double scale = 3500.0;
        const double flushReal = 0.066;
        const double baseLatency = 0.05;
        const double renderStepReal = 1.0 / 60.0;
        const int samples = 60;

        var core = new ClientHostClockInterpolation(0.35f, adaptiveWindow: true);

        // Host stream at realistic ABSOLUTE magnitude: stamps = GeoBase + wallEmit × scale (≈6.4e10, ~231 apart).
        var stamps = new List<(double hostGame, double localArrival, double value)>();
        for (int i = 0; i < samples; i++)
        {
            double wallEmit = i * flushReal;
            double hostGame = GeoBase + wallEmit * scale;
            stamps.Add((hostGame, wallEmit + baseLatency, HostPos(hostGame)));
        }

        int next = 0, frames = 0, holds = 0, directs = 0, interps = 0;
        double prev = double.NaN, maxStepDev = 0.0;
        double wallEnd = (samples - 1) * flushReal + baseLatency;

        for (double wall = 0.0; wall <= wallEnd; wall += renderStepReal)
        {
            while (next < stamps.Count && stamps[next].localArrival <= wall)
            {
                // WireQuantize models the float HostSendTime wire field + float ring: at 6.4e10 magnitude this
                // collapses 231-game-s-apart stamps to one value (the in-game bug). The fix carries the stamp as
                // double end-to-end, so WireQuantize becomes a no-op identity once the wire/ring are double.
                core.Observe(WireQuantize(stamps[next].hostGame), stamps[next].localArrival, stamps[next].value);
                next++;
            }

            double renderNowGame = GeoBase + wall * scale;   // host-slaved geoscape clock NOW (absolute magnitude)
            if (!core.TryRenderAt(renderNowGame, out double rendered, out var mode)) continue;
            if (wall < 1.0) { prev = rendered; continue; }   // let the buffer fill

            frames++;
            if (mode == ClientInterpolationCore.SampleMode.Hold) holds++;
            else if (mode == ClientInterpolationCore.SampleMode.Direct) directs++;
            else if (mode == ClientInterpolationCore.SampleMode.Interp) interps++;

            if (!double.IsNaN(prev))
            {
                double dGame = renderStepReal * scale;
                double stepSpeed = (rendered - prev) / dGame;
                maxStepDev = Math.Max(maxStepDev, Math.Abs(stepSpeed - Vel));
            }
            prev = rendered;
        }

        double interpFrac = frames > 0 ? (double)interps / frames : 0.0;
        Assert.True(interpFrac > 0.8,
            $"Expected mostly Interp at geoscape magnitude; interpFrac={interpFrac:F3} directs={directs} holds={holds} frames={frames}");
        Assert.True(maxStepDev < 0.5 * Vel,
            $"Render must be smooth (speed ≈ host Vel) at geoscape magnitude; maxStepDev={maxStepDev:F3} (Vel={Vel})");
    }

    // Two distinct stamps one sample-gap apart at 6.4e10 must remain DISTINCT keys and bracket to Interp.
    [Fact]
    public void TwoStamps_OneGapApart_RemainDistinct_AndSelectInterp()
    {
        var core = new ClientHostClockInterpolation(0.0f, adaptiveWindow: false);
        double t0 = GeoBase, t1 = GeoBase + SampleGap;
        core.Observe(WireQuantize(t0), 0.05, HostPos(t0));
        core.Observe(WireQuantize(t1), 0.05 + 0.066, HostPos(t1));

        double mid = GeoBase + SampleGap * 0.5;
        Assert.True(core.TryRenderAt(mid, out double rendered, out var mode));
        Assert.Equal(ClientInterpolationCore.SampleMode.Interp, mode);
        Assert.Equal(HostPos(mid), rendered, 3);
    }

    // Models the on-wire / in-ring TIME representation. The fix carries the stamp as double end-to-end, so this
    // is the IDENTITY (no precision loss) — matching GeoStateDiffCodec's double HostSendTime wire field and the
    // double Times ring. (Pre-fix this was `(float)hostGame`, which reproduced the geoscape-magnitude collapse
    // that made the realistic-magnitude tests RED.)
    private static double WireQuantize(double hostGame) => hostGame;

    // Encode/decode one HostSendTime stamp at realistic magnitude through the production codec, return the value.
    private static double Roundtrip(double hostSendTime)
    {
        var src = new GeoVehicleStateRecord
        {
            FactionGuid = "FAC_GEO_GUID",
            VehicleID = 1,
            Seq = 1UL,
            ChangedMask = GeoStateMask.SurfacePos | GeoStateMask.HostSendTime,
            PosX = 1f, PosY = 2f, PosZ = 3f,
            HostSendTime = hostSendTime // double end-to-end — full precision at geoscape magnitude
        };
        var diff = new GeoStateDiff { Records = new List<GeoVehicleStateRecord> { src } };
        return GeoStateDiffCodec.Decode(GeoStateDiffCodec.Encode(diff)).Records[0].HostSendTime;
    }
}
