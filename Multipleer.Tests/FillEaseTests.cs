using Multipleer.UI;
using Xunit;

namespace Multipleer.Tests
{
    public sealed class FillEaseTests
    {
        [Fact]
        public void ClimbsTowardTarget_ByAtMostMaxStep()
        {
            // From 0 toward 1 with a 0.1 budget → advances exactly 0.1 (does not jump to target).
            var next = FillEase.EaseFill(0f, 1f, 0.1f);
            Assert.Equal(0.1f, next, 5);
        }

        [Fact]
        public void DoesNotOvershootTarget()
        {
            // Budget larger than the remaining gap → lands exactly on target, never past it.
            var next = FillEase.EaseFill(0.65f, 0.69f, 0.5f);
            Assert.Equal(0.69f, next, 5);
        }

        [Fact]
        public void NeverRecedes_TargetBelowCurrentHolds()
        {
            // Monotonic: a lower (or equal) target must not pull the bar backward.
            Assert.Equal(0.8f, FillEase.EaseFill(0.8f, 0.4f, 0.5f), 5);
            Assert.Equal(0.8f, FillEase.EaseFill(0.8f, 0.8f, 0.5f), 5);
        }

        [Fact]
        public void ClampsToUnitRange()
        {
            // Result never exceeds 1 even with a huge budget; out-of-range inputs are clamped in.
            Assert.Equal(1f, FillEase.EaseFill(0.95f, 1f, 10f), 5);
            Assert.Equal(1f, FillEase.EaseFill(0.5f, 2f, 10f), 5);   // target >1 clamps to 1
            Assert.Equal(0f, FillEase.EaseFill(-0.5f, -0.2f, 1f), 5); // negative current clamps to 0, target below → holds
        }

        [Fact]
        public void ZeroOrNegativeStep_Holds()
        {
            Assert.Equal(0.3f, FillEase.EaseFill(0.3f, 0.9f, 0f), 5);
            Assert.Equal(0.3f, FillEase.EaseFill(0.3f, 0.9f, -1f), 5);
        }
    }
}
