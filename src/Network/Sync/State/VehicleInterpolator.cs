using System;
using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Inc4 S2 travel-mirror SMOOTHING — pure, Unity-free snapshot-interpolation core. The host broadcasts each
    /// moving vehicle's absolute globe placement only ~4 Hz (throttled poll), so writing those raw snapshots
    /// straight to the client transform makes vehicles STEP visibly (host motion is per-frame native). This
    /// buffers the last few timestamped snapshots per (owner,vehicle) key and, driven per-frame, renders a point
    /// <see cref="DelaySeconds"/> BEHIND the newest snapshot — slerping the pivot rotation + shortest-arc-lerping
    /// the heading between the two snapshots that STRADDLE that render time. Rendering slightly in the past is
    /// what lets consecutive snapshots be interpolated (never extrapolated): the render clock sweeps smoothly from
    /// one snapshot to the next, and on a starved buffer (a late/lost snapshot) it CLAMPS to the newest sample and
    /// holds — no overshoot.
    ///
    /// Standard entity-interpolation (Source-engine "interp") — deliberately minimal (one focused class, no
    /// generic framework). Kept Unity-free (mirrors <see cref="GeoVehiclePos"/>): the quaternion slerp and the
    /// 360°-wrapping angle lerp are implemented in MANAGED math (System.Math only), NOT via
    /// <c>UnityEngine.Quaternion.Slerp</c>/<c>Mathf.LerpAngle</c> — those are native internal-calls that need the
    /// Unity runtime, so a managed re-impl is what makes the core directly unit-testable in a bare test host. The
    /// managed slerp reproduces Unity's shortest-path, normalized-result semantics. The game glue
    /// (<see cref="GeoVehicleMirror"/>) buffers inbound snapshots here and writes each per-frame <see cref="Sample"/>
    /// to the live pivot/Surface transforms; it is NOT linked into the test project.
    /// </summary>
    public sealed class VehicleInterpolator
    {
        /// <summary>One blended output row: heading euler (degrees) + pivot rotation quaternion, as plain floats
        /// (the caller turns these into <c>UnityEngine.Vector3</c>/<c>Quaternion</c> at the transform boundary).</summary>
        public struct Sample
        {
            public float X, Y, Z;        // Surface.localEulerAngles (heading, deg)
            public float QX, QY, QZ, QW; // PivotTransform.localRotation
        }

        private struct Frame
        {
            public double Time;          // arrival time (monotonic unscaled seconds)
            public uint Seq;             // per-surface batch seq the frame arrived on (monotonic ordering)
            public float X, Y, Z;
            public float QX, QY, QZ, QW;
        }

        // Per-key ring of recent frames (oldest → newest). Depth 4 comfortably holds the pair straddling a
        // ~1.5-emit-interval render delay even across one dropped poll, and stays tiny.
        private const int Capacity = 4;

        private sealed class Buffer
        {
            public readonly List<Frame> Frames = new List<Frame>(Capacity);
            public double LastArrival;
        }

        private readonly double _delaySeconds;
        private readonly double _staleTtlSeconds;
        private readonly Dictionary<long, Buffer> _buffers = new Dictionary<long, Buffer>();

        public VehicleInterpolator(double delaySeconds, double staleTtlSeconds)
        {
            _delaySeconds = delaySeconds;
            _staleTtlSeconds = staleTtlSeconds;
        }

        /// <summary>The render latency: samples are rendered at <c>now - DelaySeconds</c>.</summary>
        public double DelaySeconds => _delaySeconds;

        /// <summary>Derive the render latency from the host EMIT cadence so the delay auto-tracks the poll rate
        /// (no hardcoded magic number that silently rots when the cadence is retuned): render
        /// <paramref name="emitMultiplier"/> emit-intervals behind the newest snapshot, where one emit interval =
        /// <paramref name="emitTickInterval"/> ticks / <paramref name="nominalFps"/>. With the default
        /// 1.5 × (6 / 60) that is 0.15 s — enough that the two snapshots straddling the render clock are always
        /// buffered (interpolate, never extrapolate), yet imperceptible for slow geoscape travel. Pure — the
        /// interp-delay derivation is directly unit-testable without Unity/SyncEngine.</summary>
        public static double DeriveDelaySeconds(int emitTickInterval, double nominalFps, double emitMultiplier)
        {
            if (nominalFps <= 0.0) return 0.0;
            return emitMultiplier * (emitTickInterval / nominalFps);
        }

        /// <summary>Number of live keys currently buffered.</summary>
        public int Count => _buffers.Count;

        /// <summary>Buffer a freshly-received snapshot for a key. A frame whose seq is NOT strictly newer than the
        /// key's latest buffered frame is IGNORED (out-of-order / duplicate transport delivery) so a stale re-send
        /// can never rewind the interpolation.</summary>
        public void Push(long key, uint seq, in GeoVehiclePos rec, double arrivalTime)
        {
            if (!_buffers.TryGetValue(key, out var b)) { b = new Buffer(); _buffers[key] = b; }
            int n = b.Frames.Count;
            if (n > 0 && seq <= b.Frames[n - 1].Seq) return;   // seq regression / dup → ignore
            b.Frames.Add(new Frame
            {
                Time = arrivalTime, Seq = seq,
                X = rec.X, Y = rec.Y, Z = rec.Z,
                QX = rec.QX, QY = rec.QY, QZ = rec.QZ, QW = rec.QW,
            });
            if (b.Frames.Count > Capacity) b.Frames.RemoveAt(0);   // drop oldest
            b.LastArrival = arrivalTime;
        }

        /// <summary>Sample a key at render time <c>now - DelaySeconds</c>. Returns false when the key has no
        /// buffered frames. Interpolates between the two straddling snapshots; clamps to the newest frame when the
        /// buffer is starved (render time is past the newest sample — hold, never extrapolate) and to the oldest
        /// when render time predates our history.</summary>
        public bool TrySample(long key, double now, out Sample sample)
        {
            sample = default(Sample);
            if (!_buffers.TryGetValue(key, out var b) || b.Frames.Count == 0) return false;
            var frames = b.Frames;
            int last = frames.Count - 1;
            double renderTime = now - _delaySeconds;

            if (renderTime >= frames[last].Time) { sample = ToSample(frames[last]); return true; } // starved → hold newest
            if (renderTime <= frames[0].Time)    { sample = ToSample(frames[0]);    return true; } // before history → oldest

            for (int i = last; i > 0; i--)
            {
                if (frames[i - 1].Time <= renderTime)
                {
                    Frame lo = frames[i - 1], hi = frames[i];
                    double span = hi.Time - lo.Time;
                    float t = span > 1e-9 ? (float)((renderTime - lo.Time) / span) : 0f;
                    sample = Blend(lo, hi, t);
                    return true;
                }
            }
            sample = ToSample(frames[0]);
            return true;
        }

        /// <summary>Drop keys whose newest frame is older than <c>now - staleTtl</c> (destroyed/despawned vehicle,
        /// or one that stopped travelling so the host no longer ships it). Appends removed keys to <paramref name="removed"/>
        /// (if non-null) so the caller can keep a parallel live-object cache in lock-step. Returns the removed count.</summary>
        public int PurgeStale(double now, List<long> removed)
        {
            List<long> stale = null;
            foreach (var kv in _buffers)
                if (now - kv.Value.LastArrival > _staleTtlSeconds)
                    (stale ?? (stale = new List<long>())).Add(kv.Key);
            if (stale == null) return 0;
            foreach (var k in stale) { _buffers.Remove(k); removed?.Add(k); }
            return stale.Count;
        }

        /// <summary>Forget one key (its live vehicle was destroyed this frame).</summary>
        public void Remove(long key) => _buffers.Remove(key);

        /// <summary>Drop all buffers (session boundary / freeze disengaged).</summary>
        public void Clear() => _buffers.Clear();

        // ─── pure blend math (managed; NO UnityEngine native call) ─────────────────────────────────────────

        private static Sample ToSample(Frame f)
        {
            Sample s;
            s.X = f.X; s.Y = f.Y; s.Z = f.Z;
            s.QX = f.QX; s.QY = f.QY; s.QZ = f.QZ; s.QW = f.QW;
            return s;
        }

        private static Sample Blend(Frame a, Frame b, float t)
        {
            Sample s;
            s.X = LerpAngle(a.X, b.X, t);   // heading is Surface.localEulerAngles (deg) → shortest-arc per component
            s.Y = LerpAngle(a.Y, b.Y, t);
            s.Z = LerpAngle(a.Z, b.Z, t);
            Slerp(a.QX, a.QY, a.QZ, a.QW, b.QX, b.QY, b.QZ, b.QW, t,
                  out s.QX, out s.QY, out s.QZ, out s.QW);
            return s;
        }

        /// <summary>Shortest-path quaternion slerp with a normalized result — reproduces
        /// <c>UnityEngine.Quaternion.Slerp</c> semantics in pure managed math (so the core needs no Unity native
        /// runtime). Falls back to normalized-lerp when the two rotations are near-parallel (avoids the sin(0)
        /// division), which is the common case for consecutive travel snapshots.</summary>
        internal static void Slerp(float ax, float ay, float az, float aw,
                                   float bx, float by, float bz, float bw,
                                   float t, out float ox, out float oy, out float oz, out float ow)
        {
            double dot = (double)ax * bx + (double)ay * by + (double)az * bz + (double)aw * bw;
            if (dot < 0.0) { bx = -bx; by = -by; bz = -bz; bw = -bw; dot = -dot; }   // shortest arc
            double s0, s1;
            if (dot > 0.9995)
            {
                s0 = 1.0 - t; s1 = t;   // near-parallel → normalized lerp
            }
            else
            {
                double theta0 = Math.Acos(dot);
                double theta = theta0 * t;
                double sinTheta0 = Math.Sin(theta0);
                double sinTheta = Math.Sin(theta);
                s1 = sinTheta / sinTheta0;
                s0 = Math.Cos(theta) - dot * s1;
            }
            double rx = s0 * ax + s1 * bx, ry = s0 * ay + s1 * by, rz = s0 * az + s1 * bz, rw = s0 * aw + s1 * bw;
            double len = Math.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
            if (len < 1e-9) { ox = ax; oy = ay; oz = az; ow = aw; return; }   // degenerate → hold a
            ox = (float)(rx / len); oy = (float)(ry / len); oz = (float)(rz / len); ow = (float)(rw / len);
        }

        /// <summary>Interpolate an angle (degrees) across the SHORTEST 360° arc — reproduces
        /// <c>UnityEngine.Mathf.LerpAngle</c> (so 350°→10° passes through 360°/0°, not backwards through 180°).</summary>
        internal static float LerpAngle(float a, float b, float t)
        {
            float delta = Repeat(b - a, 360f);
            if (delta > 180f) delta -= 360f;
            return a + delta * t;
        }

        private static float Repeat(float x, float length)
        {
            float r = x - (float)Math.Floor(x / length) * length;
            return r < 0f ? r + length : r;
        }
    }
}
