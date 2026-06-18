using Multipleer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the host deploy-capture READINESS gate. The host must NOT snapshot the
// tactical level the instant OnLevelStateChanged(Playing) fires: that postfix runs synchronously,
// BEFORE the scheduled OnLevelStart coroutine has populated the level (TacAchievementTracker._level,
// faction-assigned actors, clean FactionVision). Capturing too early threw a layered NRE inside
// TacticalLevelController.RecordInstanceData (FactionVision, then TacAchievementTracker._level),
// aborting the whole deploy broadcast. The gate defers capture until HasAnyTurnStarted==true (turn 0
// fully entered ⇒ level fully initialized), with a bounded frame fail-safe.
public class TacticalDeployReadinessGateTests
{
    [Fact]
    public void NotReady_BeforeFrameBudget_Waits()
    {
        var d = TacticalDeployReadinessGate.Decide(hasAnyTurnStarted: false, framesWaited: 0, maxFrames: 600);
        Assert.Equal(TacticalDeployReadinessGate.Decision.Wait, d);
    }

    [Fact]
    public void Ready_CapturesImmediately()
    {
        var d = TacticalDeployReadinessGate.Decide(hasAnyTurnStarted: true, framesWaited: 0, maxFrames: 600);
        Assert.Equal(TacticalDeployReadinessGate.Decision.CaptureReady, d);
    }

    [Fact]
    public void Ready_TakesPriorityOverTimeout()
    {
        // If the level became ready exactly at the budget edge, prefer the clean ready-capture.
        var d = TacticalDeployReadinessGate.Decide(hasAnyTurnStarted: true, framesWaited: 600, maxFrames: 600);
        Assert.Equal(TacticalDeployReadinessGate.Decision.CaptureReady, d);
    }

    [Fact]
    public void NeverReady_AtBudget_CapturesAsTimeoutFailSafe()
    {
        // Fail-safe: a (pathological) mission that never flips HasAnyTurnStarted must still attempt a
        // capture once the frame budget is exhausted, rather than wait forever.
        var d = TacticalDeployReadinessGate.Decide(hasAnyTurnStarted: false, framesWaited: 600, maxFrames: 600);
        Assert.Equal(TacticalDeployReadinessGate.Decision.CaptureTimeout, d);
    }

    [Fact]
    public void NeverReady_PastBudget_StillTimeout()
    {
        var d = TacticalDeployReadinessGate.Decide(hasAnyTurnStarted: false, framesWaited: 999, maxFrames: 600);
        Assert.Equal(TacticalDeployReadinessGate.Decision.CaptureTimeout, d);
    }

    [Fact]
    public void NotReady_OneFrameBeforeBudget_StillWaits()
    {
        var d = TacticalDeployReadinessGate.Decide(hasAnyTurnStarted: false, framesWaited: 599, maxFrames: 600);
        Assert.Equal(TacticalDeployReadinessGate.Decision.Wait, d);
    }
}
