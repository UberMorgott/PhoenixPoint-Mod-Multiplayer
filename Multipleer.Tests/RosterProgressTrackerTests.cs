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
    }
}
