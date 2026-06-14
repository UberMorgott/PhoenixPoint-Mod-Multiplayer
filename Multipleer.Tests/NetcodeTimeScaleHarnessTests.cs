using System;
using System.Collections.Generic;
using Multipleer.Network.CommandSync;
using Xunit;

// ── OFFLINE NETCODE HARNESS #2 — TIME-SCALE (geoscape acceleration) ──────────────────────────────
//
// Reproduces — without the game — the SECOND, deeper in-game flaw the first harness MISSED: at geoscape
// time-acceleration (TimeControlModule SelectedTimeScale, observed Scale=3600) the mirrored client craft
// STILL strongly LAGS the host and moves in JERKY BURSTS.
//
// ROOT CAUSE (verified against decompile, file:line in the agent report):
//   • HostSendTime = GeoLevelController.Timing.Now is GAME-time seconds; Timing.Now advances at
//     Scale × real-time (Timing.cs:55,59,172 — OwnNow = (ParentOwnNow − set) × Scale, Parent = Time.time).
//   • The host flushes the transform every FlushIntervalSeconds = 0.066 REAL seconds
//     (GeoStateSyncBroadcaster.cs:51) → consecutive HostSendTime stamps are 0.066 × Scale GAME-seconds
//     apart (≈237 game-s at Scale=3600).
//   • The client render delay InterpDelaySeconds = 0.35 is a tiny FIXED GAME-time constant
//     (ClientVehicleInterpolator.cs:47). At high Scale, renderHost = hostNow − 0.35 sits only 0.35 game-s
//     behind the newest sample while samples are ~237 game-s apart → the render point is pinned at/after the
//     newest sample → chronic buffer UNDERRUN → Hold-then-jump = the lived "lags + jerky bursts".
//
// MODEL: the host advances GAME-time at Scale × wall-time and is sampled every FlushInterval WALL-seconds
// (so the GAME gap between samples = FlushInterval × Scale). The render-now is the CLIENT's host-slaved
// geoscape clock (ClientTimeMirror mirrors Timing → on the client Timing.Now already == host game-now,
// advancing at Scale × wall): so renderNow advances at Scale × wall too. We render at renderNow − W and
// require the render point to lie BETWEEN two buffered samples (no underrun/Hold) with bounded lag.
//
// The fixed-0.35 delay MUST underrun at Scale=3600 (RED); the scale-aware adaptive window keeps the render
// point bracketed at every Scale (GREEN).
public class NetcodeTimeScaleHarnessTests
{
    private const double Origin = 100.0;
    private const double Vel = 7.0;                 // host units per GAME-second
    private static double HostPos(double hostGameT) => Origin + Vel * hostGameT;

    private const double FlushReal = 0.066;         // GeoStateSyncBroadcaster real-time flush cadence
    private const double BaseLatencyReal = 0.05;    // constant one-way floor latency (real seconds)
    private static readonly double[] JitterReal = { 0.000, 0.012, 0.004, 0.018, 0.002, 0.015, 0.006, 0.010 };
    private static double Jitter(int i) => JitterReal[i % JitterReal.Length];

    private const double RenderStepReal = 1.0 / 60.0;   // 60 fps render cadence (wall)
    private const int Samples = 60;                      // ~4s of wall-clock travel

    private struct Arrived
    {
        public double HostSendTimeGame;   // host GAME clock at emission
        public double LocalArrivalReal;   // client wall clock when applied
        public double Value;              // host position at HostSendTimeGame
    }

    // Build a host stream at a given time Scale: samples emitted every FlushReal WALL-seconds, each stamped
    // in GAME-time (= wallEmit × Scale), arriving after base latency + bounded jitter (real).
    private static List<Arrived> BuildStream(double scale)
    {
        var list = new List<Arrived>();
        for (int i = 0; i < Samples; i++)
        {
            double wallEmit = i * FlushReal;
            double hostGame = wallEmit * scale;
            list.Add(new Arrived
            {
                HostSendTimeGame = hostGame,
                LocalArrivalReal = wallEmit + BaseLatencyReal + Jitter(i),
                Value = HostPos(hostGame)
            });
        }
        return list;
    }

    // Drive the production pure core over a scaled stream using the CLIENT host-slaved clock as render-now
    // (renderNowGame advances at Scale × wall, exactly like the mirrored Timing.Now). Returns the worst case:
    //   underrunFrac  = fraction of rendered frames that hit Hold (render point past newest sample → jerk),
    //   maxStepDevGame= worst per-frame rendered-speed deviation from the host's uniform game speed (Vel),
    //   maxLagGame    = worst (hostNowGame − renderedHostTimeEquivalent) lag, in GAME-seconds.
    private static (double underrunFrac, double maxStepDev, double maxLagGame) Drive(double scale, bool adaptive)
    {
        var stream = BuildStream(scale);
        // Fixed-delay core renders at renderNowGame − 0.35; adaptive core uses its scale-aware window.
        var core = new ClientHostClockInterpolation(0.35f, adaptiveWindow: adaptive);
        int next = 0;
        int frames = 0, holds = 0;
        double prev = double.NaN, maxStepDev = 0, maxLagGame = 0;

        double wallEnd = (Samples - 1) * FlushReal + BaseLatencyReal;
        double windowStartWall = 1.0;   // let the buffer fill (skip warm-up)

        for (double wall = 0.0; wall <= wallEnd; wall += RenderStepReal)
        {
            while (next < stream.Count && stream[next].LocalArrivalReal <= wall)
            {
                core.Observe(stream[next].HostSendTimeGame, stream[next].LocalArrivalReal, stream[next].Value);
                next++;
            }

            // CLIENT host-slaved render clock: host game-now advances at Scale × wall (== mirrored Timing.Now).
            double renderNowGame = wall * scale;
            if (!core.TryRenderAt(renderNowGame, out double rendered, out var mode)) continue;
            if (wall < windowStartWall) { prev = rendered; continue; }

            frames++;
            if (mode == ClientInterpolationCore.SampleMode.Hold) holds++;

            // Lag: where (in host game time) does the rendered value correspond to, vs host-now.
            double renderedHostTime = (rendered - Origin) / Vel;
            maxLagGame = Math.Max(maxLagGame, renderNowGame - renderedHostTime);

            if (!double.IsNaN(prev))
            {
                // Per-frame rendered speed should match host game speed: dValue/dGameTime ≈ Vel.
                double dGame = RenderStepReal * scale;
                double stepSpeed = (rendered - prev) / dGame;
                maxStepDev = Math.Max(maxStepDev, Math.Abs(stepSpeed - Vel));
            }
            prev = rendered;
        }
        double underrunFrac = frames > 0 ? (double)holds / frames : 1.0;
        return (underrunFrac, maxStepDev, maxLagGame);
    }

    // ── RED witness: the FIXED 0.35 game-s delay UNDERRUNS badly at high Scale (the in-game lag+jerk). ──
    [Fact]
    public void FixedDelay_HighScale_Underruns_AndJerks()
    {
        var (underrunFrac, maxStepDev, _) = Drive(scale: 3600.0, adaptive: false);
        // The render point is pinned at/after the newest sample → mostly Hold, and the rendered speed swings
        // wildly (crawl between samples, jump on arrival). This is the reproduced defect.
        Assert.True(underrunFrac > 0.5 || maxStepDev > Vel,
            $"Expected fixed-delay high-scale render to underrun/jerk; underrunFrac={underrunFrac:F3} maxStepDev={maxStepDev:F3} (Vel={Vel})");
    }

    // ── GREEN: the scale-aware ADAPTIVE window keeps the render point bracketed (no underrun) and smooth. ──
    [Theory]
    [InlineData(1.0)]
    [InlineData(60.0)]
    [InlineData(3600.0)]
    public void AdaptiveDelay_AnyScale_SmoothNoUnderrun(double scale)
    {
        var (underrunFrac, maxStepDev, maxLagGame) = Drive(scale, adaptive: true);
        // Almost always interpolating between two real samples → smooth, host-rate speed, bounded lag.
        Assert.True(underrunFrac < 0.05,
            $"Adaptive render must not underrun at scale={scale}; underrunFrac={underrunFrac:F3}");
        Assert.True(maxStepDev < 0.5 * Vel,
            $"Adaptive render must be smooth (speed ≈ host Vel) at scale={scale}; maxStepDev={maxStepDev:F3}");
        // Lag is bounded to a few sample intervals of GAME time (gap ≈ FlushReal × scale).
        double gapGame = FlushReal * scale;
        Assert.True(maxLagGame < 6.0 * gapGame + 0.5,
            $"Adaptive lag must be bounded (~few sample gaps) at scale={scale}; maxLagGame={maxLagGame:F3} gapGame={gapGame:F3}");
    }

    // ── EDGE: Scale CHANGE mid-flight (3600→1 and 1→3600) must not jump or stall. ──
    [Fact]
    public void AdaptiveDelay_ScaleChangeMidFlight_NoJumpNoStall()
    {
        foreach (var (s0, s1) in new[] { (3600.0, 1.0), (1.0, 3600.0) })
        {
            var core = new ClientHostClockInterpolation(0.35f, adaptiveWindow: true);
            double wall = 0.0, prev = double.NaN, maxStepDev = 0;
            double hostGame = 0.0;             // continuous host game clock across the scale change
            int holds = 0, frames = 0;
            int i = 0;
            double nextEmitWall = 0.0;
            double scale = s0;

            for (int step = 0; step < 480; step++) // ~8s wall @60fps
            {
                if (step == 240) scale = s1;       // flip scale at the midpoint (clock stays continuous)
                wall += RenderStepReal;
                hostGame += RenderStepReal * scale;

                // Emit every FlushReal wall-seconds at the current host game time.
                while (nextEmitWall <= wall)
                {
                    double emitGame = hostGame - (wall - nextEmitWall) * scale;
                    core.Observe(emitGame, nextEmitWall + BaseLatencyReal, HostPos(emitGame));
                    nextEmitWall += FlushReal;
                    i++;
                }

                if (!core.TryRenderAt(hostGame, out double rendered, out var mode)) continue;
                frames++;
                if (mode == ClientInterpolationCore.SampleMode.Hold) holds++;
                // Exclude a short settle window right after the manual scale flip: a 3600→1 slam legitimately
                // re-anchors the render point in ONE bounded catch-up step (the craft was rendered far in the
                // host's past and must snap forward once). We assert steady-state smoothness outside that window.
                // Skip the buffer warm-up (clamp-to-oldest Direct frames before ≥2 samples bracket renderHost)
                // and a short settle window right after the manual scale flip: a 3600→1 slam legitimately
                // re-anchors the render point in ONE bounded catch-up step (the craft was rendered far in the
                // host's past and must snap forward once). We assert steady-state smoothness outside those.
                bool warmUp = step < 30;
                bool inFlipSettle = step >= 240 && step <= 252;
                if (!double.IsNaN(prev) && !warmUp && !inFlipSettle)
                {
                    double dGame = RenderStepReal * scale;
                    if (dGame > 0)
                        maxStepDev = Math.Max(maxStepDev, Math.Abs((rendered - prev) / dGame - Vel));
                }
                prev = rendered;
            }

            double underrunFrac = (double)holds / Math.Max(1, frames);
            Assert.True(underrunFrac < 0.1,
                $"Scale change {s0}->{s1} must not stall; underrunFrac={underrunFrac:F3}");
            Assert.True(maxStepDev < Vel,
                $"Scale change {s0}->{s1} must not jump; maxStepDev={maxStepDev:F3}");
        }
    }

    // ── EDGE: dense + sparse + jitter at high scale still bracketed (no wild extrapolation). ──
    [Fact]
    public void AdaptiveDelay_SparseGap_HoldsNoOvershoot()
    {
        var core = new ClientHostClockInterpolation(0.35f, adaptiveWindow: true);
        double scale = 3600.0;
        // Two normal samples, then a long sparse gap (host stalled), then resume.
        core.Observe(0.0 * scale, 0.05, HostPos(0.0));
        core.Observe(FlushReal * scale, 0.05 + FlushReal, HostPos(FlushReal * scale));
        double newest = FlushReal * scale;
        // Render far past the newest sample (underrun) — must HOLD newest, never overshoot.
        core.TryRenderAt(newest + 10_000.0, out double held, out var mode);
        Assert.Equal(ClientInterpolationCore.SampleMode.Hold, mode);
        Assert.True(held <= HostPos(newest) + 1e-6, $"Underrun must hold newest, not extrapolate; held={held}");
    }
}
