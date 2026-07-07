using Multiplayer.Network.Sync.State;
using Xunit;

// Inc5 part 1 — DivergenceMonitor window/grace pins: one mismatch is only a warning (a probe round
// can race an in-flight state echo); MismatchThreshold consecutive mismatches flag DIVERGED exactly
// once per episode; the rca-3 reload boundary arms a grace window that also drops stale marks.
public class DivergenceMonitorTests
{
    private const byte Wallet = CrcSubsetIds.Wallet;
    private const byte Sites = CrcSubsetIds.Sites;

    [Fact]
    public void Match_StaysQuiet()
    {
        var m = new DivergenceMonitor();
        Assert.Equal(CrcVerdict.Match, m.Observe(Wallet, 1u, 1u, nowMs: 0));
        Assert.False(m.AnyDiverged);
    }

    [Fact]
    public void SingleMismatch_IsOnlyAWarning_ThenThresholdFlagsOnce()
    {
        var m = new DivergenceMonitor();
        Assert.Equal(CrcVerdict.Mismatch, m.Observe(Wallet, 1u, 2u, 0));       // round 1: transient
        Assert.False(m.IsDiverged(Wallet));
        Assert.Equal(CrcVerdict.Diverged, m.Observe(Wallet, 1u, 2u, 0));       // round 2: the loud transition
        Assert.True(m.IsDiverged(Wallet));
        Assert.Equal(CrcVerdict.StillDiverged, m.Observe(Wallet, 1u, 2u, 0));  // round 3+: quiet
        Assert.Equal(CrcVerdict.StillDiverged, m.Observe(Wallet, 3u, 4u, 0));
    }

    [Fact]
    public void MatchBetweenMismatches_ResetsTheWindow()
    {
        var m = new DivergenceMonitor();
        Assert.Equal(CrcVerdict.Mismatch, m.Observe(Wallet, 1u, 2u, 0));
        Assert.Equal(CrcVerdict.Match, m.Observe(Wallet, 5u, 5u, 0));          // echo converged mid-window
        Assert.Equal(CrcVerdict.Mismatch, m.Observe(Wallet, 1u, 2u, 0));       // counter restarted, not Diverged
        Assert.False(m.AnyDiverged);
    }

    [Fact]
    public void Recovery_FlipsBackExactlyOnce()
    {
        var m = new DivergenceMonitor();
        m.Observe(Wallet, 1u, 2u, 0);
        m.Observe(Wallet, 1u, 2u, 0);                                          // → Diverged
        Assert.Equal(CrcVerdict.Recovered, m.Observe(Wallet, 5u, 5u, 0));
        Assert.False(m.IsDiverged(Wallet));
        Assert.Equal(CrcVerdict.Match, m.Observe(Wallet, 5u, 5u, 0));          // steady again
        Assert.Equal(CrcVerdict.Mismatch, m.Observe(Wallet, 1u, 2u, 0));       // a NEW episode starts fresh
    }

    [Fact]
    public void Subsets_AreIndependent()
    {
        var m = new DivergenceMonitor();
        m.Observe(Wallet, 1u, 2u, 0);
        Assert.Equal(CrcVerdict.Mismatch, m.Observe(Sites, 1u, 2u, 0));        // wallet's miss doesn't bleed over
        m.Observe(Wallet, 1u, 2u, 0);                                          // wallet → Diverged
        Assert.True(m.IsDiverged(Wallet));
        Assert.False(m.IsDiverged(Sites));
    }

    [Fact]
    public void Grace_SkipsComparisons_UntilTheWindowElapses()
    {
        var m = new DivergenceMonitor(graceMs: 1000);
        m.ArmGrace(nowMs: 5000);
        Assert.True(m.InGrace(5000));
        Assert.Equal(CrcVerdict.GraceSkip, m.Observe(Wallet, 1u, 2u, 5500));   // inside → skipped, no miss counted
        Assert.Equal(CrcVerdict.Mismatch, m.Observe(Wallet, 1u, 2u, 6001));    // elapsed → normal, counter at 1
        Assert.Equal(CrcVerdict.Diverged, m.Observe(Wallet, 1u, 2u, 6002));
    }

    [Fact]
    public void ArmGrace_DropsStaleMissAndDivergedMarks()
    {
        var m = new DivergenceMonitor(graceMs: 1000);
        m.Observe(Wallet, 1u, 2u, 0);
        m.Observe(Wallet, 1u, 2u, 0);                                          // → Diverged (pre-reload state)
        Assert.True(m.AnyDiverged);
        m.ArmGrace(nowMs: 100_000);                                            // reload boundary = the resync
        Assert.False(m.AnyDiverged);
        Assert.Equal(CrcVerdict.Mismatch, m.Observe(Wallet, 1u, 2u, 101_001)); // fresh window after grace
    }

    [Fact]
    public void Grace_IsWrapTolerant_NegativeElapsedCountsAsExpired()
    {
        var m = new DivergenceMonitor(graceMs: 1000);
        m.ArmGrace(nowMs: int.MaxValue - 10);
        Assert.False(m.InGrace(int.MinValue + 5000));                          // wrapped far past the window
        Assert.Equal(CrcVerdict.Mismatch, m.Observe(Wallet, 1u, 2u, 0));
    }
}
