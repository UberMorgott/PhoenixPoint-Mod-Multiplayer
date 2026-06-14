using System;

namespace Multipleer.Network.TimeSync
{
    /// <summary>
    /// Authoritative geoscape time-state carried on the wire.
    ///   Paused     — host clock paused flag (Base.Core.Timing.Paused).
    ///   SpeedIndex — UIModuleTimeControl.SelectedPresetTime index (NOT raw Scale — R7: avoids the
    ///                geo_speed x300 vs UI raw-Scale mismatch; clients hold identical PresetTimes[]).
    ///   Now        — host geoscape Timing.Now as total-seconds (TimeUnit.TimeSpan.TotalSeconds);
    ///                used only for the lag-resnap threshold decision, not for normal advance.
    /// </summary>
    public struct TimeStatePayload
    {
        public bool Paused;
        public int SpeedIndex;
        public double Now;

        public TimeStatePayload(bool paused, int speedIndex, double now)
        {
            Paused = paused;
            SpeedIndex = speedIndex;
            Now = now;
        }
    }

    /// <summary>
    /// Unity-FREE pure decode/decision core for host-authoritative geoscape time sync.
    /// All Harmony/Unity/native glue lives in <c>TimeSyncManager</c>; this class is unit-testable.
    /// Wire layout (13 bytes): [0]=paused(1) [1..4]=speedIndex(int32 LE) [5..12]=now(double LE).
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

        public const int WireSize = 1 + 4 + 8;

        public static byte[] EncodeTimeState(TimeStatePayload p)
        {
            var buf = new byte[WireSize];
            buf[0] = (byte)(p.Paused ? 1 : 0);
            Array.Copy(BitConverter.GetBytes(p.SpeedIndex), 0, buf, 1, 4);
            Array.Copy(BitConverter.GetBytes(p.Now), 0, buf, 5, 8);
            return buf;
        }

        public static bool TryDecodeTimeState(byte[] payload, out TimeStatePayload p)
        {
            p = default;
            if (payload == null || payload.Length < WireSize)
                return false;
            p.Paused = payload[0] != 0;
            p.SpeedIndex = BitConverter.ToInt32(payload, 1);
            p.Now = BitConverter.ToDouble(payload, 5);
            return true;
        }

        /// <summary>
        /// Stale-drop + last-writer-wins decision: apply only when the incoming header timestamp is
        /// strictly newer than the last one we applied. Equal/older = drop (idempotent heartbeat
        /// re-delivery and out-of-order packets are ignored). Same predicate governs host-side
        /// last-writer-wins of competing client TimeRequests.
        /// </summary>
        public static bool ShouldApply(long incomingTs, long lastAppliedTs)
            => incomingTs > lastAppliedTs;

        /// <summary>True if the two timestamps are strictly ordered newer-than (a after b).</summary>
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
