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
    public static class TimeSyncProtocol
    {
        /// <summary>
        /// Lag re-snap threshold, in geoscape SECONDS. If |client.Now - host.Now| exceeds this on a
        /// heartbeat, the client hard-resnaps its clock via Timing.ProcessInstanceData. Chosen at
        /// 7200s = 2 in-game hours: large enough that normal frame-rate jitter / a few dropped
        /// heartbeats never trigger a visible time-jump, small enough that a real desync is corrected
        /// before the next hourly sim tick diverges noticeably.
        /// </summary>
        public const double ResnapThresholdSeconds = 7200.0;

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
        /// Re-snap decision: returns true when the client clock has drifted from the host clock by
        /// more than <paramref name="thresholdSeconds"/> (default <see cref="ResnapThresholdSeconds"/>).
        /// </summary>
        public static bool NeedsResnap(double clientNow, double hostNow, double thresholdSeconds)
            => Math.Abs(clientNow - hostNow) > thresholdSeconds;

        public static bool NeedsResnap(double clientNow, double hostNow)
            => NeedsResnap(clientNow, hostNow, ResnapThresholdSeconds);
    }
}
