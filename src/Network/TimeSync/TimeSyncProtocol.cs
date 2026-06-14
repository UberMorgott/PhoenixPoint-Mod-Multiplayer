using System;

namespace Multipleer.Network.TimeSync
{
    /// <summary>
    /// Authoritative geoscape time-state carried on the wire.
    ///   Version    — HOST-monotonic ordering counter (single source = host). Every TimeState the host
    ///                emits carries the next host version; clients stale-drop on THIS value, never on
    ///                the per-sender wall-clock header ts (cross-machine clock skew would otherwise
    ///                permanently starve a slightly-behind peer). Unused (0) on a client TimeRequest.
    ///   Paused     — host clock paused flag (Base.Core.Timing.Paused).
    ///   SpeedIndex — UIModuleTimeControl.SelectedPresetTime index (NOT raw Scale — R7: avoids the
    ///                geo_speed x300 vs UI raw-Scale mismatch; clients hold identical PresetTimes[]).
    ///   Now        — host geoscape Timing.Now as total-seconds (TimeUnit.TimeSpan.TotalSeconds);
    ///                used only for the lag-resnap threshold decision, not for normal advance.
    /// </summary>
    public struct TimeStatePayload
    {
        public long Version;
        public bool Paused;
        public int SpeedIndex;
        public double Now;

        public TimeStatePayload(bool paused, int speedIndex, double now)
            : this(0L, paused, speedIndex, now)
        {
        }

        public TimeStatePayload(long version, bool paused, int speedIndex, double now)
        {
            Version = version;
            Paused = paused;
            SpeedIndex = speedIndex;
            Now = now;
        }
    }

    /// <summary>
    /// Unity-FREE pure decode/decision core for host-authoritative geoscape time sync.
    /// All Harmony/Unity/native glue lives in <c>TimeSyncManager</c>; this class is unit-testable.
    /// Wire layout (21 bytes): [0..7]=version(int64 LE) [8]=paused(1) [9..12]=speedIndex(int32 LE)
    /// [13..20]=now(double LE).
    /// </summary>
    /// <summary>
    /// Per-heartbeat client clock-correction decision. Either a continuous soft rate dilation
    /// (<see cref="ScaleMultiplier"/> applied to the host-commanded raw Scale) or, for a catastrophic
    /// gap, a one-shot <see cref="HardSnap"/> via Timing.ProcessInstanceData.
    /// </summary>
    public struct ClockCorrection
    {
        /// <summary>True → hard-resnap the clock to host.Now (large gap). False → apply dilation.</summary>
        public bool HardSnap;
        /// <summary>Multiplier on the host-commanded Scale. >1 client behind (catch up), &lt;1 ahead
        /// (run slower so host catches up), exactly 1 inside the deadband / on a hard snap.</summary>
        public double ScaleMultiplier;
    }

    public static class TimeSyncProtocol
    {
        // ── Continuous soft clock-rate correction (standard netcode "time dilation") ──
        // error = host.Now - client.Now, in geoscape SECONDS.

        /// <summary>No correction while |error| is below this (≈1 in-game sec) — kills micro-jitter so
        /// the client settles exactly at the host rate instead of dithering around it.</summary>
        public const double SyncDeadbandSeconds = 1.0;

        /// <summary>|error| at which the dilation saturates to ±<see cref="MaxDilation"/>. Below it the
        /// dilation ramps linearly with error, so small drift (e.g. 20s) converges in ~1-2s and large
        /// drift pulls at the capped rate. 30s chosen so a 20s desync glides back into lock quickly.</summary>
        public const double RampFullScaleErrorSeconds = 30.0;

        /// <summary>Max fractional rate dilation: client Scale ∈ host*[1-MaxDilation .. 1+MaxDilation].
        /// 0.25 = up to ±25% rate delta — fast enough to close drift, slow enough to stay invisible.</summary>
        public const double MaxDilation = 0.25;

        /// <summary>Client BEHIND host by more than this → forward hard-snap (acceptable: time only
        /// jumps forward). 600s = 10 in-game min: well above what dilation handles (≤ a minute or two
        /// of drift), so only a genuine stall/desync snaps.</summary>
        public const double ResnapHardForwardSeconds = 600.0;

        /// <summary>Client AHEAD of host by more than this → backward hard-snap (a visible BACKWARD
        /// time jump — only tolerated when catastrophically large; normal "ahead" drift is corrected by
        /// running the client slower via dilation). 3600s = 1 in-game hour.</summary>
        public const double ResnapHardBackwardSeconds = 3600.0;

        public const int WireSize = 8 + 1 + 4 + 8;

        public static byte[] EncodeTimeState(TimeStatePayload p)
        {
            var buf = new byte[WireSize];
            Array.Copy(BitConverter.GetBytes(p.Version), 0, buf, 0, 8);
            buf[8] = (byte)(p.Paused ? 1 : 0);
            Array.Copy(BitConverter.GetBytes(p.SpeedIndex), 0, buf, 9, 4);
            Array.Copy(BitConverter.GetBytes(p.Now), 0, buf, 13, 8);
            return buf;
        }

        public static bool TryDecodeTimeState(byte[] payload, out TimeStatePayload p)
        {
            p = default;
            if (payload == null || payload.Length < WireSize)
                return false;
            p.Version = BitConverter.ToInt64(payload, 0);
            p.Paused = payload[8] != 0;
            p.SpeedIndex = BitConverter.ToInt32(payload, 9);
            p.Now = BitConverter.ToDouble(payload, 13);
            return true;
        }

        /// <summary>Next host-monotonic ordering version (single-source counter on the host).</summary>
        public static long NextVersion(long current) => current + 1;

        /// <summary>
        /// Client stale-drop: apply only when the incoming HOST-stamped version is strictly newer than
        /// the last one we applied. Equal/older = drop (idempotent heartbeat re-delivery and
        /// out-of-order packets ignored). Single host clock → no cross-machine skew.
        /// </summary>
        public static bool ShouldApply(long incomingVersion, long lastAppliedVersion)
            => incomingVersion > lastAppliedVersion;

        /// <summary>True if version a is strictly newer than version b.</summary>
        public static bool IsNewer(long a, long b) => a > b;

        /// <summary>
        /// Decide how the client should reconcile its clock to the host this heartbeat.
        /// <paramref name="errorSeconds"/> = host.Now - client.Now (positive ⇒ client behind).
        ///   • |error| beyond the (asymmetric) hard thresholds ⇒ HardSnap (mult 1).
        ///   • |error| inside the deadband ⇒ no-op (mult exactly 1).
        ///   • otherwise ⇒ soft dilation: mult = 1 + clamp(error/Ramp * MaxDilation, ±MaxDilation),
        ///     so the client runs faster when behind / slower when ahead and settles to host rate as
        ///     error → 0. Linear+clamped ⇒ symmetric (mult(e)+mult(-e)==2) and monotone toward 1.
        /// </summary>
        public static ClockCorrection ComputeCorrection(double errorSeconds)
        {
            if (errorSeconds > ResnapHardForwardSeconds || errorSeconds < -ResnapHardBackwardSeconds)
                return new ClockCorrection { HardSnap = true, ScaleMultiplier = 1.0 };

            if (Math.Abs(errorSeconds) < SyncDeadbandSeconds)
                return new ClockCorrection { HardSnap = false, ScaleMultiplier = 1.0 };

            double raw = (errorSeconds / RampFullScaleErrorSeconds) * MaxDilation;
            double clamped = Math.Max(-MaxDilation, Math.Min(MaxDilation, raw));
            return new ClockCorrection { HardSnap = false, ScaleMultiplier = 1.0 + clamped };
        }
    }
}
