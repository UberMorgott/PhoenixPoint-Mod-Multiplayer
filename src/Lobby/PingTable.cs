using System;
using System.Collections.Generic;

namespace Multiplayer.Network
{
    /// <summary>
    /// PER-PEER ROUND-TRIP TIME — A LOCAL READING, AND NOTHING ELSE.
    ///
    /// No transport here supplies RTT (verified 2026-08-07 by reflection over the shipped
    /// Facepunch.Steamworks.Win64.dll: the legacy <c>SteamNetworking</c> P2P API this mod binds has no
    /// status accessor at all, <c>P2PSessionState_t</c> carries no ping field, TCP's SRTT is behind a
    /// raw WSAIoctl, and StunUDP has no protocol layer). So the number has to be measured — and the
    /// repo already ships the probe to measure it with: <c>PacketType.Heartbeat</c> / <c>HeartbeatAck</c>,
    /// which have carried an 8-byte timestamp nobody read since they were written. This class is the
    /// reader; <see cref="SessionManager.HandleHeartbeat"/> is the seam.
    ///
    /// PER-PEER, NOT PER-TRANSPORT. A peer sits on exactly one child transport and
    /// <c>CompositeTransport._peerToChild</c> already says which, so attribution is a dictionary lookup,
    /// not three pingers.
    ///
    /// CONTAINMENT (law L158). An SRTT is a reading of THIS machine's clock. It is not domain state, it
    /// is not owned by the peer it describes, and it must never reach <c>DiffEngine</c>,
    /// <c>GenericApplier</c>, <c>SurfaceRouter</c>, <c>TimeAnchor</c> or any <c>[SerializeMember]</c>
    /// leaf: replicating it would turn every peer's jitter into rail churn (L73's inequality is the
    /// first casualty) and it would fail silently, this repo's dominant bug class. Nothing may GATE on
    /// a reading either — a peer with no sample is simply unknown (NO-QUORUM mandate, L145).
    ///
    /// ESTIMATOR: RFC 6298 §2.3's SRTT recurrence at alpha = 1/4 — <c>srtt += (R - srtt) >> 2</c>, and
    /// <c>SRTT = R</c> on the first sample (§2.2). Its RTTVAR half is deliberately absent: RTTVAR exists
    /// only to size a retransmission timeout and we retransmit nothing. alpha = 1/4 (not the RFC's 1/8)
    /// settles in ~4 samples ≈ 4 s at this cadence; 1/8 would lag ~8 s and read as broken.
    /// ponytail: if a display ever flickers, drop alpha to 1/8 before adding any second filter.
    /// </summary>
    public class PingTable
    {
        /// <summary>Probe cadence — this IS <c>SessionManager.HeartbeatIntervalMs</c>, named here because
        /// staleness and the in-flight window are both expressed in it.</summary>
        public const int CadenceMs = 1000;

        /// <summary>No sample within three cadences renders as NO SAMPLE. A stale number that still looks
        /// like a measurement is worse than an admitted gap.</summary>
        private const int StaleAfterMs = 3 * CadenceMs;

        /// <summary>Send and receive both run in Unity <c>Update</c>, so a main-thread hitch is
        /// indistinguishable from network latency — a 400 ms world-load stall would be charged to the
        /// link. These stalls are routine and the repo already concedes it (SessionManager.cs:155-188
        /// suspends every liveness timeout across a transfer/load for exactly this reason). A tick gap
        /// past this bar POISONS whatever probe is in flight; the next probe starts clean.</summary>
        private const long MaxTickGapMs = 150;

        private readonly Dictionary<ulong, int> _srtt = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, long> _sampledAt = new Dictionary<ulong, long>();
        /// <summary>Dedup: the last stamp already ACCEPTED from each peer. <c>StunTransport</c> sends every
        /// reliable datagram TWICE (StunTransport.cs:384-387), so the duplicate's ack arrives late and would
        /// feed a fabricated high sample straight into SRTT. First ack per stamp wins; the rest are noise.</summary>
        private readonly Dictionary<ulong, long> _acceptedStamp = new Dictionary<ulong, long>();

        // Two in-flight stamps, not one: at a 1 s cadence a single slot would reject every RTT above
        // 1000 ms and render it as "unknown" rather than "bad". Two slots cover up to 2 s; past that the
        // link is not one a co-op session survives anyway. 0 = empty / poisoned by a tick gap.
        private long _stamp;
        private long _prevStamp;
        private long _lastTickMs;

        public static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>Once per frame, before anything else. Poisons the in-flight probes if the main thread
        /// stalled across them — see <see cref="MaxTickGapMs"/>.</summary>
        public void Tick(long nowMs)
        {
            if (_lastTickMs != 0 && nowMs - _lastTickMs > MaxTickGapMs)
                _stamp = _prevStamp = 0;
            _lastTickMs = nowMs;
        }

        /// <summary>Registers a new probe and returns the stamp to put on the wire.</summary>
        public long StartProbe(long nowMs)
        {
            _prevStamp = _stamp;
            _stamp = nowMs;
            return _stamp;
        }

        /// <summary>An ack came back carrying <paramref name="stamp"/>. Ignored unless it echoes a probe we
        /// actually have in flight and is the FIRST ack for it from this peer.</summary>
        public void Sample(ulong peerId, long stamp, long nowMs)
        {
            if (stamp == 0 || (stamp != _stamp && stamp != _prevStamp)) return;
            if (_acceptedStamp.TryGetValue(peerId, out var seen) && seen == stamp) return;
            _acceptedStamp[peerId] = stamp;

            var r = nowMs - stamp;
            if (r < 0) return;                       // clock stepped backwards mid-probe
            if (r > ushort.MaxValue) r = ushort.MaxValue;
            _srtt[peerId] = _srtt.TryGetValue(peerId, out var s)
                ? s + (((int)r - s) >> 2)            // RFC 6298 §2.3, alpha = 1/4
                : (int)r;                            // RFC 6298 §2.2, first sample
            _sampledAt[peerId] = nowMs;
        }

        /// <summary>Smoothed RTT to a peer in ms, or <c>-1</c> for NO FRESH SAMPLE. Never blocks, never
        /// waits, never gates — an unknown peer is unknown and the caller renders it as such.</summary>
        public int GetPingMs(ulong peerId) => GetPingMs(peerId, NowMs());

        public int GetPingMs(ulong peerId, long nowMs)
            => _sampledAt.TryGetValue(peerId, out var at) && nowMs - at <= StaleAfterMs
                ? _srtt[peerId]
                : -1;

        // ─── Wire ────────────────────────────────────────────────────────────
        // Only the HOST has a link to every peer, so only the host can measure everyone. Its table rides
        // the heartbeat it ALREADY broadcasts — no new packet type, no new cadence, no new timer, and no
        // surface id (Heartbeat is a top-level PacketType and never enters SurfaceRouter).
        //   [stamp:i64][count:i32][(peerId:u64, srttMs:u16) x N]      = 12 + 10N bytes (508 B at N=50)
        // A client's probe carries the bare stamp (count omitted), and every ack echoes the leading 8
        // bytes only — echoing a whole table back would double the host's own traffic for nothing.

        public byte[] Encode(long stamp, long nowMs)
        {
            var rows = new List<KeyValuePair<ulong, int>>();
            foreach (var kv in _sampledAt)
                if (nowMs - kv.Value <= StaleAfterMs && _srtt.TryGetValue(kv.Key, out var ms))
                    rows.Add(new KeyValuePair<ulong, int>(kv.Key, ms));

            var buf = new byte[12 + rows.Count * 10];
            Array.Copy(BitConverter.GetBytes(stamp), 0, buf, 0, 8);
            Array.Copy(BitConverter.GetBytes(rows.Count), 0, buf, 8, 4);
            var o = 12;
            foreach (var row in rows)
            {
                Array.Copy(BitConverter.GetBytes(row.Key), 0, buf, o, 8); o += 8;
                Array.Copy(BitConverter.GetBytes((ushort)Math.Min(row.Value, ushort.MaxValue)), 0, buf, o, 2); o += 2;
            }
            return buf;
        }

        /// <summary>Merges a host table into the local readings and returns the probe stamp to ack (0 if the
        /// payload carries none). The host's table never contains the host itself, so a client's own
        /// locally-measured host reading is never overwritten by a reported one.</summary>
        public long Decode(byte[] payload, long nowMs)
        {
            if (payload == null || payload.Length < 8) return 0;
            var stamp = BitConverter.ToInt64(payload, 0);
            if (payload.Length < 12) return stamp;

            var count = BitConverter.ToInt32(payload, 8);
            if (count < 0 || 12 + count * 10 > payload.Length) return stamp;   // truncated / hostile row count
            var o = 12;
            for (var i = 0; i < count; i++)
            {
                var peerId = BitConverter.ToUInt64(payload, o); o += 8;
                _srtt[peerId] = BitConverter.ToUInt16(payload, o); o += 2;
                _sampledAt[peerId] = nowMs;
            }
            return stamp;
        }

        /// <summary>The ack payload: the leading stamp of whatever we received, verbatim. Echoing is the
        /// entire trick — restamping with the responder's own clock (what this code did before 2026-08-07)
        /// compares two different clocks and yields a number that is not an RTT.</summary>
        public static byte[] EchoStamp(byte[] payload)
        {
            var echo = new byte[8];
            if (payload != null && payload.Length >= 8) Array.Copy(payload, 0, echo, 0, 8);
            return echo;
        }

        /// <summary>Drops a peer's readings when its roster row goes.</summary>
        public void Forget(ulong peerId)
        {
            _srtt.Remove(peerId);
            _sampledAt.Remove(peerId);
            _acceptedStamp.Remove(peerId);
        }
    }
}
