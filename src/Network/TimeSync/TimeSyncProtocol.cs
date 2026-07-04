using System;

namespace Multiplayer.Network.TimeSync
{
    /// <summary>
    /// Authoritative geoscape time ANCHOR carried on the wire (host→all, packet 0x37 TimeAnchor).
    /// The host re-captures this anchor at every pause / play / speed change (and on join / heartbeat).
    /// The client derives game-time as a pure function of it: <c>g = gAnchor + rate*(serverNow-tAnchor)</c>
    /// (see <see cref="AnchorClock"/>). The anchor is TIME-INVARIANT between changes — a heartbeat
    /// re-delivery carries the SAME anchor (a safety re-sync, NOT a correction signal). This is what
    /// kills the old control-loop oscillation.
    ///   Version      — HOST-monotonic ordering counter (single source = host). Clients stale-drop on
    ///                  THIS value (<see cref="ShouldApply"/>), never on the per-sender wall-clock header ts.
    ///   TAnchorTicks — host real-time at the change instant, as TimeSpan.FromSeconds(hostRT).Ticks
    ///                  (monotonic Time.realtimeSinceStartupAsDouble, NOT wall-clock; exact int64, no drift).
    ///   GAnchorTicks — game-time at the change instant = Timing.Now.TimeSpan.Ticks.
    ///   Paused       — host clock paused flag.
    ///   SpeedIndex   — UIModuleTimeControl.SelectedPresetTime (0..2); rate = paused ? 0 : PresetTimes[idx].
    /// </summary>
    public struct AnchorPayload
    {
        public long Version;
        public long TAnchorTicks;
        public long GAnchorTicks;
        public bool Paused;
        public int SpeedIndex;

        public AnchorPayload(long version, long tAnchorTicks, long gAnchorTicks, bool paused, int speedIndex)
        {
            Version = version;
            TAnchorTicks = tAnchorTicks;
            GAnchorTicks = gAnchorTicks;
            Paused = paused;
            SpeedIndex = speedIndex;
        }

        /// <summary>TimeSpan ticks ↔ seconds helpers (exact int64 carriage of double seconds).</summary>
        public static long SecondsToTicks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;
        public static double TicksToSeconds(long ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;
    }

    /// <summary>
    /// Client→host time-control request (packet 0x38 TimeRequest): the user's desired {paused, speedIndex}.
    /// The host applies last-writer-wins, then captures + broadcasts a fresh <see cref="AnchorPayload"/>.
    /// No version/timebase fields — it is a pure intent relay.
    /// </summary>
    public struct TimeRequestPayload
    {
        public bool Paused;
        public int SpeedIndex;

        public TimeRequestPayload(bool paused, int speedIndex)
        {
            Paused = paused;
            SpeedIndex = speedIndex;
        }
    }

    /// <summary>
    /// NTP-style clock-offset exchange (packets 0x39 TimeClockPing client→host, 0x3A TimeClockPong
    /// host→client). The host echoes the client's <c>T0</c> and stamps its own receive time <c>T1</c>;
    /// the client completes the 3-stamp at receive (t3) via <see cref="ClockOffsetEstimator"/>.
    /// All stamps are monotonic real-time seconds (Time.realtimeSinceStartupAsDouble), not wall-clock.
    /// </summary>
    public struct ClockPingPayload
    {
        public int PingId;
        public double T0;

        public ClockPingPayload(int pingId, double t0) { PingId = pingId; T0 = t0; }
    }

    public struct ClockPongPayload
    {
        public int PingId;
        public double T0; // echoed
        public double T1; // host localRT at receive

        public ClockPongPayload(int pingId, double t0, double t1) { PingId = pingId; T0 = t0; T1 = t1; }
    }

    /// <summary>
    /// Unity-FREE pure decode/decision core for the host-authoritative geoscape anchor clock.
    /// All Harmony/Unity/native glue lives in <c>TimeSyncManager</c>; this class is unit-testable.
    /// Anchor wire layout (29 bytes, little-endian):
    ///   [0..7]=version(int64) [8..15]=tAnchorTicks(int64) [16..23]=gAnchorTicks(int64)
    ///   [24]=paused(byte) [25..28]=speedIndex(int32).
    /// </summary>
    public static class TimeSyncProtocol
    {
        public const int WireSize = 8 + 8 + 8 + 1 + 4; // 29

        // ─── Anchor (0x37) encode/decode ──────────────────────────────────
        public static byte[] EncodeAnchor(AnchorPayload p)
        {
            var buf = new byte[WireSize];
            Array.Copy(BitConverter.GetBytes(p.Version), 0, buf, 0, 8);
            Array.Copy(BitConverter.GetBytes(p.TAnchorTicks), 0, buf, 8, 8);
            Array.Copy(BitConverter.GetBytes(p.GAnchorTicks), 0, buf, 16, 8);
            buf[24] = (byte)(p.Paused ? 1 : 0);
            Array.Copy(BitConverter.GetBytes(p.SpeedIndex), 0, buf, 25, 4);
            return buf;
        }

        public static bool TryDecodeAnchor(byte[] payload, out AnchorPayload p)
        {
            p = default;
            if (payload == null || payload.Length < WireSize)
                return false;
            p.Version = BitConverter.ToInt64(payload, 0);
            p.TAnchorTicks = BitConverter.ToInt64(payload, 8);
            p.GAnchorTicks = BitConverter.ToInt64(payload, 16);
            p.Paused = payload[24] != 0;
            p.SpeedIndex = BitConverter.ToInt32(payload, 25);
            return true;
        }

        // ─── TimeRequest (0x38) encode/decode ─────────────────────────────
        public const int RequestWireSize = 1 + 4; // 5

        public static byte[] EncodeRequest(TimeRequestPayload p)
        {
            var buf = new byte[RequestWireSize];
            buf[0] = (byte)(p.Paused ? 1 : 0);
            Array.Copy(BitConverter.GetBytes(p.SpeedIndex), 0, buf, 1, 4);
            return buf;
        }

        public static bool TryDecodeRequest(byte[] payload, out TimeRequestPayload p)
        {
            p = default;
            if (payload == null || payload.Length < RequestWireSize)
                return false;
            p.Paused = payload[0] != 0;
            p.SpeedIndex = BitConverter.ToInt32(payload, 1);
            return true;
        }

        // ─── ClockPing (0x39) / ClockPong (0x3A) encode/decode ────────────
        public const int PingWireSize = 4 + 8;       // 12
        public const int PongWireSize = 4 + 8 + 8;   // 20

        public static byte[] EncodePing(ClockPingPayload p)
        {
            var buf = new byte[PingWireSize];
            Array.Copy(BitConverter.GetBytes(p.PingId), 0, buf, 0, 4);
            Array.Copy(BitConverter.GetBytes(p.T0), 0, buf, 4, 8);
            return buf;
        }

        public static bool TryDecodePing(byte[] payload, out ClockPingPayload p)
        {
            p = default;
            if (payload == null || payload.Length < PingWireSize)
                return false;
            p.PingId = BitConverter.ToInt32(payload, 0);
            p.T0 = BitConverter.ToDouble(payload, 4);
            return true;
        }

        public static byte[] EncodePong(ClockPongPayload p)
        {
            var buf = new byte[PongWireSize];
            Array.Copy(BitConverter.GetBytes(p.PingId), 0, buf, 0, 4);
            Array.Copy(BitConverter.GetBytes(p.T0), 0, buf, 4, 8);
            Array.Copy(BitConverter.GetBytes(p.T1), 0, buf, 12, 8);
            return buf;
        }

        public static bool TryDecodePong(byte[] payload, out ClockPongPayload p)
        {
            p = default;
            if (payload == null || payload.Length < PongWireSize)
                return false;
            p.PingId = BitConverter.ToInt32(payload, 0);
            p.T0 = BitConverter.ToDouble(payload, 4);
            p.T1 = BitConverter.ToDouble(payload, 12);
            return true;
        }

        // ─── Version ordering (generic, reusable seam) ────────────────────
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
    }
}
