using Multipleer.Network.TimeSync;
using Xunit;

namespace Multipleer.Tests
{
    public class TimeSyncProtocolTests
    {
        // ─── Anchor payload roundtrip (0x37, 29-byte: version, tAnchor, gAnchor, paused, speedIdx) ──
        [Theory]
        // version, tAnchorTicks, gAnchorTicks, paused, speedIndex
        [InlineData(0L, 0L, 0L, true, 0)]
        [InlineData(1L, 123456789L, 987654321L, false, 1)]
        [InlineData(42L, -5000L, 9_000_000_000L, true, 2)]
        [InlineData(9_000_000_000L, 315576000000000L, 315576000000000L, false, 2)] // ~100 game-years in ticks
        public void Anchor_EncodeDecode_Roundtrips(long version, long tAnchor, long gAnchor, bool paused, int idx)
        {
            var p = new AnchorPayload(version, tAnchor, gAnchor, paused, idx);
            var bytes = TimeSyncProtocol.EncodeAnchor(p);
            Assert.Equal(TimeSyncProtocol.WireSize, bytes.Length);
            Assert.Equal(29, bytes.Length);

            Assert.True(TimeSyncProtocol.TryDecodeAnchor(bytes, out var back));
            Assert.Equal(version, back.Version);
            Assert.Equal(tAnchor, back.TAnchorTicks);
            Assert.Equal(gAnchor, back.GAnchorTicks);
            Assert.Equal(paused, back.Paused);
            Assert.Equal(idx, back.SpeedIndex);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 1, 0, 0, 0 })]                  // too short
        [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28 })] // 28 bytes, one short
        public void Anchor_Decode_RejectsMalformed(byte[] bad)
        {
            Assert.False(TimeSyncProtocol.TryDecodeAnchor(bad, out _));
        }

        [Fact]
        public void Anchor_TicksSecondsHelpers_Roundtrip()
        {
            double seconds = 12345.678;
            long ticks = AnchorPayload.SecondsToTicks(seconds);
            Assert.Equal(seconds, AnchorPayload.TicksToSeconds(ticks), 6);
        }

        // ─── TimeRequest payload roundtrip (0x38, 5-byte: paused, speedIdx) ──
        [Theory]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 2)]
        public void Request_EncodeDecode_Roundtrips(bool paused, int idx)
        {
            var p = new TimeRequestPayload(paused, idx);
            var bytes = TimeSyncProtocol.EncodeRequest(p);
            Assert.Equal(TimeSyncProtocol.RequestWireSize, bytes.Length);
            Assert.True(TimeSyncProtocol.TryDecodeRequest(bytes, out var back));
            Assert.Equal(paused, back.Paused);
            Assert.Equal(idx, back.SpeedIndex);
        }

        [Fact]
        public void Request_Decode_RejectsShort()
            => Assert.False(TimeSyncProtocol.TryDecodeRequest(new byte[] { 1, 0 }, out _));

        // ─── Clock ping/pong roundtrip (0x39 / 0x3A) ──────────────────────
        [Theory]
        [InlineData(0, 0.0)]
        [InlineData(7, 12345.6789)]
        [InlineData(-3, 1e9)]
        public void Ping_EncodeDecode_Roundtrips(int pingId, double t0)
        {
            var p = new ClockPingPayload(pingId, t0);
            var bytes = TimeSyncProtocol.EncodePing(p);
            Assert.Equal(TimeSyncProtocol.PingWireSize, bytes.Length);
            Assert.True(TimeSyncProtocol.TryDecodePing(bytes, out var back));
            Assert.Equal(pingId, back.PingId);
            Assert.Equal(t0, back.T0, 9);
        }

        [Theory]
        [InlineData(0, 0.0, 0.0)]
        [InlineData(7, 100.5, 250.25)]
        [InlineData(-3, 1e9, 1e9 + 0.05)]
        public void Pong_EncodeDecode_Roundtrips(int pingId, double t0, double t1)
        {
            var p = new ClockPongPayload(pingId, t0, t1);
            var bytes = TimeSyncProtocol.EncodePong(p);
            Assert.Equal(TimeSyncProtocol.PongWireSize, bytes.Length);
            Assert.True(TimeSyncProtocol.TryDecodePong(bytes, out var back));
            Assert.Equal(pingId, back.PingId);
            Assert.Equal(t0, back.T0, 9);
            Assert.Equal(t1, back.T1, 9);
        }

        [Fact]
        public void Ping_Decode_RejectsShort()
            => Assert.False(TimeSyncProtocol.TryDecodePing(new byte[] { 1, 0, 0, 0 }, out _));

        [Fact]
        public void Pong_Decode_RejectsShort()
            => Assert.False(TimeSyncProtocol.TryDecodePong(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, out _));

        // ─── Host-stamped version: monotonic ordering + client stale-drop (KEPT, generic seam) ──
        [Fact]
        public void NextVersion_IsStrictlyMonotonic()
        {
            long v = 0;
            v = TimeSyncProtocol.NextVersion(v); Assert.Equal(1L, v);
            v = TimeSyncProtocol.NextVersion(v); Assert.Equal(2L, v);
            v = TimeSyncProtocol.NextVersion(v); Assert.Equal(3L, v);
        }

        [Fact]
        public void ShouldApply_NewerVersion_Applied()
            => Assert.True(TimeSyncProtocol.ShouldApply(incomingVersion: 200, lastAppliedVersion: 100));

        [Fact]
        public void ShouldApply_OlderVersion_Dropped()
            => Assert.False(TimeSyncProtocol.ShouldApply(incomingVersion: 50, lastAppliedVersion: 100));

        [Fact]
        public void ShouldApply_EqualVersion_Dropped()
            => Assert.False(TimeSyncProtocol.ShouldApply(incomingVersion: 100, lastAppliedVersion: 100));

        [Fact]
        public void IsNewer_StrictlyGreater()
        {
            Assert.True(TimeSyncProtocol.IsNewer(2, 1));
            Assert.False(TimeSyncProtocol.IsNewer(1, 1));
            Assert.False(TimeSyncProtocol.IsNewer(0, 1));
        }

        [Fact]
        public void StaleDrop_HostVersionsAdvanceClientWatermark()
        {
            // Out-of-order host versions: only strictly-newer ones advance the applied watermark.
            long applied = 0;
            foreach (var incoming in new long[] { 1, 3, 2, 3, 4 })
            {
                if (TimeSyncProtocol.ShouldApply(incoming, applied))
                    applied = incoming;
            }
            Assert.Equal(4L, applied); // 2 (out-of-order) and the duplicate 3 were dropped
        }
    }
}
