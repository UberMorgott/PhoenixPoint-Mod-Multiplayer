using Multiplayer.Network;
using Xunit;

namespace Multiplayer.Tests
{
    public class RosterProgressTrackerTests
    {
        [Fact]
        public void Percent_Only_Increases_Within_A_Phase()
        {
            var t = new RosterProgressTracker();
            t.Merge(slot: 1, phase: 0, percent: 40);
            t.Merge(slot: 1, phase: 0, percent: 10); // stale/reordered → ignored
            Assert.Equal((0, 40), t.Get(1));
        }

        [Fact]
        public void Phase_Only_Advances()
        {
            var t = new RosterProgressTracker();
            t.Merge(1, 1, 5);                  // already in load phase
            t.Merge(1, 0, 99);                 // late download packet → ignored
            Assert.Equal((1, 5), t.Get(1));
        }

        [Fact]
        public void Advancing_Phase_Resets_Percent_Baseline()
        {
            var t = new RosterProgressTracker();
            t.Merge(1, 0, 100);
            t.Merge(1, 1, 3);                  // new phase, lower percent is accepted
            Assert.Equal((1, 3), t.Get(1));
        }

        [Fact]
        public void Unknown_Slot_Reads_As_Phase0_Zero()
        {
            var t = new RosterProgressTracker();
            Assert.Equal((0, 0), t.Get(9));
        }

        [Fact]
        public void AllDone_False_Until_Every_Expected_Slot_Reports()
        {
            var t = new RosterProgressTracker();
            var expected = new byte[] { 0, 1 };
            Assert.False(t.AllDone(expected));
            t.MarkDone(0);
            Assert.False(t.AllDone(expected));
            t.MarkDone(1);
            Assert.True(t.AllDone(expected));
        }

        [Fact]
        public void AllDone_Ignores_Extra_Done_Slots()
        {
            var t = new RosterProgressTracker();
            t.MarkDone(0);
            t.MarkDone(5);                         // slot that left / not expected
            Assert.True(t.AllDone(new byte[] { 0 }));
        }

        [Fact]
        public void MarkDone_Is_Idempotent()
        {
            var t = new RosterProgressTracker();
            t.MarkDone(0);
            t.MarkDone(0);
            Assert.True(t.IsDone(0));
            Assert.True(t.AllDone(new byte[] { 0 }));
        }

        [Fact]
        public void Reset_Clears_State_And_Done()
        {
            var t = new RosterProgressTracker();
            t.Merge(1, 1, 77);
            t.MarkDone(1);

            t.Reset();

            Assert.Equal((0, 0), t.Get(1));            // progress state cleared
            Assert.False(t.IsDone(1));                 // done-set cleared
            Assert.False(t.AllDone(new byte[] { 1 })); // gate empty again
        }

        [Theory]
        [InlineData(0f, (byte)0)]
        [InlineData(0.5f, (byte)50)]
        [InlineData(1f, (byte)100)]
        [InlineData(0.999f, (byte)99)]   // floor, never rounds up to a premature 100
        [InlineData(-0.2f, (byte)0)]     // clamp low
        [InlineData(1.5f, (byte)100)]    // clamp high
        public void ProgressByte_Clamps_And_Floors(float progress, byte expected)
        {
            Assert.Equal(expected, RosterProgressTracker.ProgressByte(progress));
        }

        [Theory]
        [InlineData((byte)0, (byte)0, 0f)]       // nothing yet
        [InlineData((byte)0, (byte)100, 0.5f)]   // download done (instant loopback) → HALF bar, NOT full (the bug fix)
        [InlineData((byte)1, (byte)0, 0.5f)]     // load starts where download ended → continuous, no jump
        [InlineData((byte)1, (byte)100, 1f)]     // actually loaded → only now is the bar full
        public void CombinedFill_Maps_Both_Phases_Across_The_Bar(byte phase, byte percent, float expected)
        {
            Assert.Equal(expected, RosterProgressTracker.CombinedFill(phase, percent), precision: 3);
        }

        [Fact]
        public void CombinedFill_LoadingInProgress_Exceeds_DownloadComplete()
        {
            // A peer that has BEGUN loading (phase 1, any percent > 0) must read fuller than a peer that only
            // finished downloading (phase 0, 100%) — the whole point of phase-aware fill.
            Assert.True(RosterProgressTracker.CombinedFill(1, 1) > RosterProgressTracker.CombinedFill(0, 100));
        }

        [Fact]
        public void InPhase2_False_Before_Begin()
        {
            Assert.False(RosterProgressTracker.InPhase2(begun: false, loadCompleteSent: false));
        }

        [Fact]
        public void InPhase2_True_After_Begin_Before_Done()
        {
            Assert.True(RosterProgressTracker.InPhase2(begun: true, loadCompleteSent: false));
        }

        [Fact]
        public void InPhase2_False_After_Done()
        {
            Assert.False(RosterProgressTracker.InPhase2(begun: true, loadCompleteSent: true));
        }
    }
}
