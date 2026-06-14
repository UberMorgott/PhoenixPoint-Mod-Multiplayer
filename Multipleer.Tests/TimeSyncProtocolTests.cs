using Multipleer.Network.TimeSync;
using Xunit;

namespace Multipleer.Tests
{
    public class TimeSyncProtocolTests
    {
        // ─── Payload roundtrip ────────────────────────────────────────────
        [Theory]
        [InlineData(true, 0, 0.0)]
        [InlineData(false, 1, 12345.678)]
        [InlineData(true, 2, -42.5)]
        [InlineData(false, 7, 3.15576e9)]   // ~100 game-years in seconds (large geoscape Now)
        public void EncodeDecode_Roundtrips(bool paused, int idx, double now)
        {
            var p = new TimeStatePayload(paused, idx, now);
            var bytes = TimeSyncProtocol.EncodeTimeState(p);
            Assert.Equal(TimeSyncProtocol.WireSize, bytes.Length);

            Assert.True(TimeSyncProtocol.TryDecodeTimeState(bytes, out var back));
            Assert.Equal(paused, back.Paused);
            Assert.Equal(idx, back.SpeedIndex);
            Assert.Equal(now, back.Now, 6);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 1, 0, 0, 0 })] // too short (missing the 8-byte Now)
        public void Decode_RejectsMalformed(byte[] bad)
        {
            Assert.False(TimeSyncProtocol.TryDecodeTimeState(bad, out _));
        }

        // ─── Stale-drop / last-writer-wins by ts ──────────────────────────
        [Fact]
        public void ShouldApply_NewerTs_Applied()
            => Assert.True(TimeSyncProtocol.ShouldApply(incomingTs: 200, lastAppliedTs: 100));

        [Fact]
        public void ShouldApply_OlderTs_Dropped()
            => Assert.False(TimeSyncProtocol.ShouldApply(incomingTs: 50, lastAppliedTs: 100));

        [Fact]
        public void ShouldApply_EqualTs_Dropped()
            => Assert.False(TimeSyncProtocol.ShouldApply(incomingTs: 100, lastAppliedTs: 100));

        [Fact]
        public void IsNewer_OrdersByTimestamp()
        {
            Assert.True(TimeSyncProtocol.IsNewer(300, 299));
            Assert.False(TimeSyncProtocol.IsNewer(299, 300));
            Assert.False(TimeSyncProtocol.IsNewer(300, 300));
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
