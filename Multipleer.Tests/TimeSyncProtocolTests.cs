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

        // ─── Continuous soft clock-rate correction (time dilation) ────────
        // error = host.Now - client.Now ; positive ⇒ client BEHIND host.

        [Fact]
        public void Correction_WithinDeadband_NoChange()
        {
            var c = TimeSyncProtocol.ComputeCorrection(0.5); // < 1s deadband
            Assert.False(c.HardSnap);
            Assert.Equal(1.0, c.ScaleMultiplier, 9);
        }

        [Fact]
        public void Correction_ClientBehind_DilatesFaster()
        {
            // 20s behind → run faster (mult > 1), no hard snap
            var c = TimeSyncProtocol.ComputeCorrection(20.0);
            Assert.False(c.HardSnap);
            Assert.True(c.ScaleMultiplier > 1.0);
            Assert.True(c.ScaleMultiplier <= 1.0 + TimeSyncProtocol.MaxDilation);
        }

        [Fact]
        public void Correction_ClientAhead_DilatesSlower()
        {
            // 20s ahead → run slower (mult < 1), no hard snap (no backward jump)
            var c = TimeSyncProtocol.ComputeCorrection(-20.0);
            Assert.False(c.HardSnap);
            Assert.True(c.ScaleMultiplier < 1.0);
            Assert.True(c.ScaleMultiplier >= 1.0 - TimeSyncProtocol.MaxDilation);
        }

        [Fact]
        public void Correction_ConvergesToOneAsErrorShrinks()
        {
            // monotone: smaller |error| → multiplier closer to 1
            double m20 = TimeSyncProtocol.ComputeCorrection(20.0).ScaleMultiplier;
            double m10 = TimeSyncProtocol.ComputeCorrection(10.0).ScaleMultiplier;
            double m2 = TimeSyncProtocol.ComputeCorrection(2.0).ScaleMultiplier;
            Assert.True(m20 > m10 && m10 > m2 && m2 > 1.0);
        }

        [Theory]
        [InlineData(15.0)]
        [InlineData(7.5)]
        [InlineData(50.0)]
        [InlineData(123.4)]
        public void Correction_IsSymmetric(double err)
        {
            // linear+clamped ⇒ mult(e) + mult(-e) == 2
            double pos = TimeSyncProtocol.ComputeCorrection(err).ScaleMultiplier;
            double neg = TimeSyncProtocol.ComputeCorrection(-err).ScaleMultiplier;
            Assert.Equal(2.0, pos + neg, 9);
        }

        [Fact]
        public void Correction_ClampsToMaxDilation()
        {
            // error past the ramp but below the hard threshold saturates at ±MaxDilation (no snap)
            var behind = TimeSyncProtocol.ComputeCorrection(200.0);
            Assert.False(behind.HardSnap);
            Assert.Equal(1.0 + TimeSyncProtocol.MaxDilation, behind.ScaleMultiplier, 9);

            var ahead = TimeSyncProtocol.ComputeCorrection(-200.0);
            Assert.False(ahead.HardSnap);
            Assert.Equal(1.0 - TimeSyncProtocol.MaxDilation, ahead.ScaleMultiplier, 9);
        }

        [Fact]
        public void Correction_LargeForwardError_HardSnaps()
        {
            // client far behind (> 10 in-game min) → forward hard snap
            var c = TimeSyncProtocol.ComputeCorrection(TimeSyncProtocol.ResnapHardForwardSeconds + 1);
            Assert.True(c.HardSnap);
        }

        [Fact]
        public void Correction_ModerateBackwardError_DilatesNotSnaps()
        {
            // client ahead by 10 in-game min (past forward threshold but well within backward) →
            // dilate slower, do NOT backward-snap (backward jump only when catastrophic ≥ 1h)
            var c = TimeSyncProtocol.ComputeCorrection(-(TimeSyncProtocol.ResnapHardForwardSeconds + 60));
            Assert.False(c.HardSnap);
            Assert.Equal(1.0 - TimeSyncProtocol.MaxDilation, c.ScaleMultiplier, 9);
        }

        [Fact]
        public void Correction_CatastrophicBackwardError_HardSnaps()
        {
            var c = TimeSyncProtocol.ComputeCorrection(-(TimeSyncProtocol.ResnapHardBackwardSeconds + 1));
            Assert.True(c.HardSnap);
        }
    }
}
