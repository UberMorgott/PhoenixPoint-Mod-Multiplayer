using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE STEP DETECTOR — a measurement, not a fix (same contract as <see cref="ClockPhaseDiag"/>).
    ///
    /// WHY IT EXISTS. Two rounds of fixes (5 perf commits + the departure-anchor rework) did not change
    /// the field symptom "client aircraft steps/jerks while the host is smooth", and the 2026-08-18
    /// session logs cannot name the discontinuity: clockphase shows only a constant ~0.05-0.19 s
    /// pipeline lag (pixel-scale on the globe), nav anchors fire, frame profiles are near-identical on
    /// both peers. Pose is a pure per-frame function of (route, startTime, Actor.Timing.Now)
    /// (GeoNavComponent.cs:104-126), so a VISIBLE step can only be a discontinuity in one of those
    /// inputs or a stalled frame — and nothing logged today fires AT the step. This does: it watches
    /// every travelling vehicle's pose frame-over-frame and, when the pose moves by something a smooth
    /// derivation cannot produce, prints ONE line carrying every candidate culprit's alibi for that
    /// exact frame.
    ///
    /// HOW TO READ A LINE. <c>moved</c> vs <c>expected</c> (speed x actor-clock delta) is the step
    /// itself. The flags then separate the candidate classes the 2026-08-18 brief could not:
    ///   • <c>reseed</c>/<c>anchor</c> → the route was replaced this frame (a teleport by design);
    ///   • <c>rebase</c> → a TimeAnchor level-clock write coincided (if pose follows it, the
    ///     actor-clocks-are-immune-to-Rebase reasoning is WRONG);
    ///   • <c>apply</c> → a rail batch landed this frame (a leaf write moved a derivation input);
    ///   • <c>dReal</c> large with NO flags → a stalled frame; if RailCost's tickMax stayed flat too,
    ///     the stall is Unity/GC/game-side and no rail cut will help;
    ///   • no flags and dReal small → a derivation input changed with no batch — the silent-rewrite
    ///     class, and the dNow/dLevel pair says whether it was the clock or the route.
    /// Runs on the HOST too: the same step on both peers means the game itself steps (auto-pause
    /// storms at 3600x), and that verdict is as decisive as any.
    ///
    /// COST AND GATE. Behind <see cref="Multiplayer.Network.MpDiag"/>, driven from the EXISTING
    /// SyncEngine.Tick (no new timer/coroutine/per-frame poll — the tick already runs every frame for
    /// RailCost). Per frame: one pass over Map.Vehicles (a handful), a Distance and a dictionary probe
    /// per travelling vehicle. Applies nothing, decides nothing, resets on session teardown.
    /// </summary>
    internal static class VehicleStepDiag
    {
        private struct Sample
        {
            public Vector3 Pos;
            public double ActorNow;
            public double LevelNow;
            public float Real;
        }

        private static readonly Dictionary<string, Sample> _last = new Dictionary<string, Sample>(StringComparer.Ordinal);

        // Frame stamps set by the code that performs the act (assignment cost only, so unconditional):
        // "did X happen on this or the previous frame" is the whole alibi a step line needs.
        internal static int LastApplyFrame = -10, LastReseedFrame = -10, LastAnchorFrame = -10, LastRebaseFrame = -10;
        internal static string LastReseedRoot, LastAnchorRoot;

        /// <summary>A real step at geoscape speed is hundreds of game-seconds of travel; one 60 fps frame
        /// at 3600x is ~8 km for an aircraft. The floor keeps sub-frame jitter and Slerp curvature out;
        /// the factor keeps honest speed-ups (frame pacing) out. Both deliberately loose — this hunts
        /// VISIBLE steps, not noise.</summary>
        private const double FloorMeters = 20000.0;   // 20 km ≈ 2-3 frames of max-speed travel
        private const double Slack = 0.75;            // |moved - expected| must exceed 75% of expected

        private static float _windowAt;
        private static int _linesThisWindow;
        private const int MaxLinesPer10s = 20;

        private static float _nextSampleAt;

        internal static void Tick()
        {
            if (!Multiplayer.Network.MpDiag.On) return;
            var geo = GenericApplier.StartedGeoLevel();
            if (geo == null || geo.Map == null || geo.Timing == null) { if (_last.Count > 0) _last.Clear(); return; }

            float nowReal = Time.realtimeSinceStartup;
            if (nowReal - _windowAt > 10f) { _windowAt = nowReal; _linesThisWindow = 0; }
            bool sampleDue = nowReal >= _nextSampleAt;
            if (sampleDue) _nextSampleAt = nowReal + 1f;

            double levelNow = geo.Timing.Now.TimeSpan.TotalSeconds;
            int frame = Time.frameCount;
            var inv = CultureInfo.InvariantCulture;
            StringBuilder sampleLine = null;

            var vehicles = geo.Map.Vehicles;
            for (int i = 0; i < vehicles.Count; i++)
            {
                var v = vehicles[i];
                if (v == null || v.Timing == null) continue;
                var dest = v.DestinationSites;
                bool travelling = dest != null && dest.Count > 0;
                string root = IdentityResolver.RootRef(v);
                if (root == null) continue;
                if (!travelling) { _last.Remove(root); continue; }

                var cur = new Sample
                {
                    Pos = v.WorldPosition,
                    ActorNow = v.Timing.Now.TimeSpan.TotalSeconds,
                    LevelNow = levelNow,
                    Real = nowReal,
                };

                Sample prev;
                bool have = _last.TryGetValue(root, out prev);
                _last[root] = cur;
                if (!have) continue;

                double dNow = cur.ActorNow - prev.ActorNow;
                double moved = GeoMap.Distance(prev.Pos, cur.Pos).InMeters;
                double metersPerGameSecond = v.Speed.InMeters / 3600.0;   // GeoNavComponent.cs:95
                double expected = metersPerGameSecond * Math.Max(0.0, dNow);

                if (sampleDue)
                {
                    if (sampleLine == null) sampleLine = new StringBuilder("[Multiplayer][stepdiag] sample");
                    sampleLine.Append(' ').Append(root)
                              .Append(" now=").Append(cur.ActorNow.ToString("F1", inv))
                              .Append(" lvl=").Append(levelNow.ToString("F1", inv))
                              .Append(" pos=").Append(cur.Pos.x.ToString("F2", inv)).Append(',')
                              .Append(cur.Pos.y.ToString("F2", inv)).Append(',')
                              .Append(cur.Pos.z.ToString("F2", inv))
                              .Append(" legs=").Append(dest.Count);
                }

                if (Math.Abs(moved - expected) <= Math.Max(FloorMeters, expected * Slack)) continue;
                if (_linesThisWindow >= MaxLinesPer10s) continue;
                _linesThisWindow++;

                string flags = Flags(frame, root);
                MpLog.Log("[Multiplayer][stepdiag] STEP " + root +
                          " moved=" + (moved / 1000.0).ToString("F1", inv) + "km" +
                          " expected=" + (expected / 1000.0).ToString("F1", inv) + "km" +
                          " dNow=" + dNow.ToString("F1", inv) + "s" +
                          " dLevel=" + (cur.LevelNow - prev.LevelNow).ToString("F1", inv) + "s" +
                          " dReal=" + (cur.Real - prev.Real).ToString("F3", inv) + "s" +
                          " scale=" + geo.Timing.EffectiveScale.ToString("F0", inv) +
                          " flags=" + flags);
            }
            if (sampleLine != null) MpLog.Log(sampleLine.ToString());
        }

        private static string Flags(int frame, string root)
        {
            var sb = new StringBuilder();
            if (frame - LastApplyFrame <= 1) sb.Append("apply,");
            if (frame - LastReseedFrame <= 1) sb.Append(string.Equals(LastReseedRoot, root, StringComparison.Ordinal) ? "reseed," : "reseed(other),");
            if (frame - LastAnchorFrame <= 1) sb.Append(string.Equals(LastAnchorRoot, root, StringComparison.Ordinal) ? "anchor," : "anchor(other),");
            if (frame - LastRebaseFrame <= 1) sb.Append("rebase,");
            return sb.Length == 0 ? "none" : sb.ToString(0, sb.Length - 1);
        }

        internal static void Reset()
        {
            _last.Clear();
            LastApplyFrame = LastReseedFrame = LastAnchorFrame = LastRebaseFrame = -10;
            LastReseedRoot = LastAnchorRoot = null;
        }
    }
}
