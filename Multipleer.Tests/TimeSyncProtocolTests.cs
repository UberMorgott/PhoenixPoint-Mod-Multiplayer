using Multipleer.Network.TimeSync;
using Xunit;

namespace Multipleer.Tests
{
    public class TimeSyncProtocolTests
    {
        // ─── Payload roundtrip (incl. host-stamped Version) ───────────────
        [Theory]
        [InlineData(0L, true, 0, 0.0)]
        [InlineData(1L, false, 1, 12345.678)]
        [InlineData(42L, true, 2, -42.5)]
        [InlineData(9_000_000_000L, false, 7, 3.15576e9)] // large version + ~100 game-years Now
        public void EncodeDecode_Roundtrips(long version, bool paused, int idx, double now)
        {
            var p = new TimeStatePayload(version, paused, idx, now);
            var bytes = TimeSyncProtocol.EncodeTimeState(p);
            Assert.Equal(TimeSyncProtocol.WireSize, bytes.Length);

            Assert.True(TimeSyncProtocol.TryDecodeTimeState(bytes, out var back));
            Assert.Equal(version, back.Version);
            Assert.Equal(paused, back.Paused);
            Assert.Equal(idx, back.SpeedIndex);
            Assert.Equal(now, back.Now, 6);
        }

        [Fact]
        public void RequestPayload_DefaultsVersionZero()
        {
            // A client TimeRequest uses the 3-arg ctor → Version unused (0); host ignores it.
            var p = new TimeStatePayload(true, 2, 0.0);
            Assert.Equal(0L, p.Version);
            var back = Decode(TimeSyncProtocol.EncodeTimeState(p));
            Assert.Equal(0L, back.Version);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 1, 0, 0, 0 })] // too short (missing version+payload)
        public void Decode_RejectsMalformed(byte[] bad)
        {
            Assert.False(TimeSyncProtocol.TryDecodeTimeState(bad, out _));
        }

        // ─── Host-stamped version: monotonic ordering + client stale-drop ──
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
        public void StaleDrop_HostVersionsAdvanceClientWatermark()
        {
            // Simulate the client stale-drop loop over a stream of host-stamped versions delivered
            // out of order: only strictly-newer versions advance the applied watermark.
            long applied = 0;
            foreach (var incoming in new long[] { 1, 3, 2, 3, 4 })
            {
                if (TimeSyncProtocol.ShouldApply(incoming, applied))
                    applied = incoming;
            }
            Assert.Equal(4L, applied); // 2 (out-of-order) and the duplicate 3 were dropped
        }

        private static TimeStatePayload Decode(byte[] bytes)
        {
            Assert.True(TimeSyncProtocol.TryDecodeTimeState(bytes, out var p));
            return p;
        }

        // ─── Re-snap threshold decision ───────────────────────────────────
        [Fact]
        public void NeedsResnap_WithinThreshold_False()
        {
            // 1 in-game hour drift, threshold = 2h → no resnap
            Assert.False(TimeSyncProtocol.NeedsResnap(clientNow: 10_000, hostNow: 10_000 + 3600));
        }

        [Fact]
        public void NeedsResnap_BeyondThreshold_True()
        {
            // 3 in-game hours drift, threshold = 2h → resnap
            Assert.True(TimeSyncProtocol.NeedsResnap(clientNow: 10_000, hostNow: 10_000 + 3 * 3600));
        }

        [Fact]
        public void NeedsResnap_ExactlyAtThreshold_False()
        {
            // strict > comparison: drift == threshold does NOT resnap
            Assert.False(TimeSyncProtocol.NeedsResnap(
                clientNow: 0, hostNow: TimeSyncProtocol.ResnapThresholdSeconds,
                thresholdSeconds: TimeSyncProtocol.ResnapThresholdSeconds));
        }

        [Fact]
        public void NeedsResnap_IsSymmetric()
        {
            // sign of the drift must not matter (client ahead OR behind host)
            Assert.True(TimeSyncProtocol.NeedsResnap(20_000, 0, 7200));
            Assert.True(TimeSyncProtocol.NeedsResnap(0, 20_000, 7200));
        }
    }
}
