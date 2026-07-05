using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// WA-2 mission-drift throttle (spec 2026-07-05 §5, gap 1c): the GeoUpdateableMission.Update dirty hook must
/// mark the owning site at MOST once per interval per site, with sites throttling independently — a burst of
/// same-hour mission ticks collapses into one channel-#5 re-snapshot (last-wins wire, zero wire change).
/// </summary>
public class MissionDriftThrottleTests
{
    [Fact]
    public void FirstMark_Passes_BurstWithinIntervalSuppressed()
    {
        var t = new MissionDriftThrottle(1.0);
        Assert.True(t.ShouldMark(7, 100.0));
        Assert.False(t.ShouldMark(7, 100.1));
        Assert.False(t.ShouldMark(7, 100.99));
    }

    [Fact]
    public void MarkAfterInterval_PassesAgain()
    {
        var t = new MissionDriftThrottle(1.0);
        Assert.True(t.ShouldMark(7, 100.0));
        Assert.True(t.ShouldMark(7, 101.0));    // exactly the interval boundary → allowed
        Assert.False(t.ShouldMark(7, 101.5));
        Assert.True(t.ShouldMark(7, 102.5));
    }

    [Fact]
    public void Sites_ThrottleIndependently()
    {
        var t = new MissionDriftThrottle(1.0);
        Assert.True(t.ShouldMark(1, 100.0));
        Assert.True(t.ShouldMark(2, 100.0));    // different site — not suppressed by site 1's mark
        Assert.False(t.ShouldMark(1, 100.5));
        Assert.False(t.ShouldMark(2, 100.5));
        Assert.True(t.ShouldMark(1, 101.1));
    }

    [Fact]
    public void NegativeSiteId_NeverMarks()
    {
        var t = new MissionDriftThrottle(1.0);
        Assert.False(t.ShouldMark(-1, 100.0));
        Assert.False(t.ShouldMark(-1, 200.0));
    }

    [Fact]
    public void Reset_ClearsRecordedMarks()
    {
        var t = new MissionDriftThrottle(1.0);
        Assert.True(t.ShouldMark(7, 100.0));
        t.Reset();
        Assert.True(t.ShouldMark(7, 100.1));    // record dropped → allowed again
    }

    [Fact]
    public void SuppressedCall_DoesNotSlideTheWindow()
    {
        // A suppressed tick must NOT re-arm the interval: marks land at a steady ≤1/interval cadence even
        // under continuous ticking (window anchored at the last ALLOWED mark, not the last attempt).
        var t = new MissionDriftThrottle(1.0);
        Assert.True(t.ShouldMark(7, 100.0));
        Assert.False(t.ShouldMark(7, 100.9));
        Assert.True(t.ShouldMark(7, 101.0));    // still allowed at 100.0 + 1.0 despite the 100.9 attempt
    }
}
