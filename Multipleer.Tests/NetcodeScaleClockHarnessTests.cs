using System;
using System.Collections.Generic;
using Multipleer.Network.CommandSync;
using Xunit;

// ── OFFLINE NETCODE HARNESS #2 — SCALE-AWARE (the failure the first harness MISSED) ──────────────────
//
// The original NetcodeHostClockHarnessTests modeled host clock == wall clock (Scale=1): LocalArrival =
// HostSendTime + latency. That fed the render clock a SMOOTH, correct host time and so the two prior fixes
// (host-time stamps, adaptive window) were GREEN offline yet did NOTHING in-game. The in-game failure lives at
// HIGH GEOSCAPE TIME-SCALE: the host GAME clock (Timing.Now → HostSendTime) advances at Scale × real-time,
// while the client's LOCAL clock (Time.realtimeSinceStartup → LocalArrival) advances at REAL rate. So
//
//      HostSendTime_i ≈ Scale * realTime_i        LocalArrival_i ≈ realTime_i + latency
//
// The OLD HostClockOffsetEstimator estimates hostNow = localNow − delayFloor — i.e. it advances the render
// clock at REAL rate, NOT host rate. At Scale=60/3600 that estimate cannot possibly sit between two
// host-time-keyed samples (which are Scale× farther apart on the host timeline than the real render step
// covers), so every frame the bracket selector returns Direct/Hold (NEVER Interp) → the stepped ~1s-cadence
// jerk + huge lag the player sees. THIS harness models Scale and reproduces that offline.
//
// THE FIX (clock-sync netcode): estimate BOTH an offset AND a RATE from the timestamp stream
// (rate ≈ d(HostSendTime)/d(LocalArrival) ≈ Scale, derived — never reading Scale), advance the estimate
// smoothly every frame at that rate, and slew-correct gently toward the newest sample. Then renderTime sits
// between the two newest-ish samples → Interp every frame → smooth, low-lag, at ANY scale.
public class NetcodeScaleClockHarnessTests
{
    private const double Origin = 100.0;
    private const double Vel = 7.0;                 // host position units per HOST(game)-second
    private static double HostPos(double hostGameT) => Origin + Vel * hostGameT;

    // Host sends the transform every ~0.066 REAL seconds (GeoStateSyncBroadcaster flush), with small real
    // inter-arrival jitter. Real send times over ~2 real seconds. Deterministic (no RNG).
    private static readonly double[] RealSendTimes =
    {
        0.000, 0.066, 0.134, 0.198, 0.270, 0.330, 0.402, 0.460, 0.534, 0.594,
        0.668, 0.726, 0.802, 0.860, 0.936, 0.996, 1.070, 1.128, 1.204, 1.262,
        1.338, 1.396, 1.472, 1.530, 1.606, 1.664, 1.740, 1.798, 1.874, 1.932
    };

    private const double BaseLatency = 0.04;        // constant one-way floor latency (real seconds)
    private static readonly double[] JitterPattern = { 0.000, 0.012, 0.003, 0.015, 0.002, 0.010 };
    private static double Jitter(int i) => JitterPattern[i % JitterPattern.Length];

    private struct Arrived
    {
        public double HostSendTime;   // host GAME clock at emission = realSend * Scale
        public double LocalArrival;   // client REAL clock when applied = realSend + latency + jitter
        public double Value;          // host position at that game time
    }

    // Build the arrived stream for a given geoscape time Scale. Optionally apply a Scale CHANGE at a real-time
    // boundary (changeAtReal) to model switching speed mid-flight (the game clock rate jumps there).
    private static List<Arrived> BuildStream(double scale, double scaleAfter = -1, double changeAtReal = -1)
    {
        var list = new List<Arrived>();
        double hostGame = 0.0;
        double prevReal = 0.0;
        for (int i = 0; i < RealSendTimes.Length; i++)
        {
            double real = RealSendTimes[i];
            double s = (scaleAfter > 0 && real >= changeAtReal) ? scaleAfter : scale;
            hostGame += (real - prevReal) * s;     // game clock integrates Scale over real time
            prevReal = real;
            list.Add(new Arrived
            {
                HostSendTime = hostGame,
                LocalArrival = real + BaseLatency + Jitter(i),
                Value = HostPos(hostGame)
            });
        }
        return list;
    }

    private const float Delay = 0.35f;
    private const double RenderStep = 1.0 / 60.0;   // 60 fps render cadence, REAL seconds

    // Drive the production core (ClientHostClockInterpolation → HostClockOffsetEstimator) over a Scale stream at
    // a fixed REAL render cadence via the local-clock render overload (TryRender → EstimateHostNow), exactly the
    // path the live ClientVehicleInterpolator uses. Collect, over the steady mid-window: the fraction of frames
    // that bracketed in Interp mode, and the max per-step deviation of the rendered speed from host truth.
    private static (double interpFrac, double maxStepDev, double posMaxErr) DriveScale(
        double scale, double scaleAfter = -1, double changeAtReal = -1)
    {
        var stream = BuildStream(scale, scaleAfter, changeAtReal);
        var core = new ClientHostClockInterpolation(Delay);
        int next = 0;

        double windowStart = stream[0].LocalArrival + 0.7;   // allow warmup + window to fill
        double windowEnd = stream[stream.Count - 1].LocalArrival - 0.1;

        int interpFrames = 0, totalFrames = 0;
        double prevVal = double.NaN, prevReal = double.NaN;
        double maxStepDev = 0, posMaxErr = 0;
        double effScale = scale;

        for (double localNow = stream[0].LocalArrival; localNow <= windowEnd; localNow += RenderStep)
        {
            while (next < stream.Count && stream[next].LocalArrival <= localNow)
            {
                core.Observe(stream[next].HostSendTime, stream[next].LocalArrival, stream[next].Value);
                next++;
            }
            if (scaleAfter > 0 && localNow >= changeAtReal + BaseLatency) effScale = scaleAfter;

            double hostNowEst = core.EstimateHostNow(localNow);
            if (!core.TryRenderAt(hostNowEst, out double rendered, out var mode)) continue;

            if (localNow >= windowStart && localNow <= windowEnd)
            {
                totalFrames++;
                if (mode == ClientInterpolationCore.SampleMode.Interp) interpFrames++;

                // Host truth at the rendered host-time (estimate − window), in host units.
                double truth = HostPos(hostNowEst - core.EffectiveWindow);
                posMaxErr = Math.Max(posMaxErr, Math.Abs(rendered - truth));

                if (!double.IsNaN(prevVal))
                {
                    double dReal = localNow - prevReal;
                    // Expected rendered units per REAL second = Vel(units/gameSec) * Scale(gameSec/realSec).
                    double expectedPerReal = Vel * effScale;
                    double localSpeed = (rendered - prevVal) / dReal;
                    // Normalize by Scale so the tolerance is scale-independent (relative speed error).
                    maxStepDev = Math.Max(maxStepDev, Math.Abs(localSpeed - expectedPerReal) / (Vel * scale));
                }
                prevVal = rendered; prevReal = localNow;
            }
        }
        double interpFrac = totalFrames > 0 ? (double)interpFrames / totalFrames : 0.0;
        return (interpFrac, maxStepDev, posMaxErr);
    }

    // ── DELIVERABLE (RED until the estimator advances at the estimated HOST rate) ─────────────────────────
    // At Scale 60 and 3600 the steady-state render MUST bracket in Interp (smooth per-frame motion), NOT
    // Direct/Hold. With the old real-rate estimator this is ~0% Interp → RED.
    [Theory]
    [InlineData(1.0)]
    [InlineData(60.0)]
    [InlineData(3600.0)]
    public void ScaleStream_RendersInterp_Smooth(double scale)
    {
        var (interpFrac, maxStepDev, _) = DriveScale(scale);
        Assert.True(interpFrac > 0.9,
            $"Scale={scale}: render must be Interp (smooth) almost every frame; interpFrac={interpFrac:F3}");
        // Per-frame relative speed deviation: smooth motion = no big single-frame jumps. A small transient at
        // each sample re-anchor is expected (and invisible on the slow geoscape craft); the hard guarantee is
        // Interp every frame (above) + bounded lag (separate test). Keep this < 1.0 (no frame doubles/halts).
        Assert.True(maxStepDev < 1.0,
            $"Scale={scale}: per-step rendered speed must stay near host*scale; maxRelStepDev={maxStepDev:F3}");
    }

    // Lag must be bounded to ~one render window (a couple sample gaps), not the whole ring / a stale snapshot.
    [Theory]
    [InlineData(60.0)]
    [InlineData(3600.0)]
    public void ScaleStream_LagBounded_ToAboutOneWindow(double scale)
    {
        var (_, _, posMaxErr) = DriveScale(scale);
        // Rendered tracks host truth at (estimate − window). Allowed band ≈ a few host units of slack
        // (a fraction of one inter-sample host-distance, which is Vel*0.066*scale).
        double oneGapDist = Vel * 0.066 * scale;
        Assert.True(posMaxErr < 0.5 * oneGapDist + 2.0,
            $"Scale={scale}: rendered must track host within ~one sample gap; posMaxErr={posMaxErr:F2}, gapDist={oneGapDist:F2}");
    }

    // Scale CHANGE mid-flight (1 → 3600 at t≈0.6s real) must re-converge to Interp within a bounded number of
    // samples — no permanent stall, no jump. Assert the LATE window (well after the change) is Interp-dominated.
    [Fact]
    public void ScaleChange_MidFlight_Reconverges()
    {
        var stream = BuildStream(1.0, 3600.0, 0.6);
        var core = new ClientHostClockInterpolation(Delay);
        int next = 0;
        double lateStart = 1.2;   // well past the change + a few samples to re-converge
        double windowEnd = stream[stream.Count - 1].LocalArrival - 0.1;
        int interp = 0, total = 0;

        for (double localNow = stream[0].LocalArrival; localNow <= windowEnd; localNow += RenderStep)
        {
            while (next < stream.Count && stream[next].LocalArrival <= localNow)
            {
                core.Observe(stream[next].HostSendTime, stream[next].LocalArrival, stream[next].Value);
                next++;
            }
            double est = core.EstimateHostNow(localNow);
            if (!core.TryRenderAt(est, out _, out var mode)) continue;
            if (localNow >= lateStart)
            {
                total++;
                if (mode == ClientInterpolationCore.SampleMode.Interp) interp++;
            }
        }
        double frac = total > 0 ? (double)interp / total : 0.0;
        Assert.True(frac > 0.85,
            $"After a 1→3600 scale change the render must re-converge to Interp; lateInterpFrac={frac:F3}");
    }

    // WARMUP: the estimator must converge within the first 1–2 samples (the "stutter in place 1-2s then flies"
    // startup). By the 3rd received sample the estimated rate must be within 20% of the true Scale.
    [Fact]
    public void Warmup_RateConvergesWithinTwoSamples()
    {
        const double scale = 3600.0;
        var stream = BuildStream(scale);
        var core = new ClientHostClockInterpolation(Delay);
        for (int i = 0; i < 3; i++)
            core.Observe(stream[i].HostSendTime, stream[i].LocalArrival, stream[i].Value);

        double rate = core.EstimatedRate;
        Assert.True(Math.Abs(rate - scale) / scale < 0.2,
            $"Estimated host rate must converge to Scale within 2-3 samples; rate={rate:F1} vs Scale={scale}");
    }
}
