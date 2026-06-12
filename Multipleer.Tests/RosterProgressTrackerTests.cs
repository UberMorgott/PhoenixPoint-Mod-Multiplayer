using Multipleer.Network;
using Xunit;

namespace Multipleer.Tests
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
    }
}
