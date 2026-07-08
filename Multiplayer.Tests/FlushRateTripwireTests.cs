using Multiplayer.Network.Sync;
using Xunit;

// RCA 2026-07-08 round 2 safeguard: the per-channel flush-rate tripwire that makes a silent dirty-mark
// storm (per-frame marks → per-Tick channel flushes) visible in field logs with ONE warning line.
public class FlushRateTripwireTests
{
    private const byte Ch9 = 9;

    /// <summary>Drive a steady flush rate for a span; returns how many times the tripwire fired.</summary>
    private static int Drive(FlushRateTripwire t, byte ch, long startMs, long durationMs, int perSec)
    {
        int fired = 0;
        long interval = 1000 / perSec;
        for (long ms = startMs; ms < startMs + durationMs; ms += interval)
            if (t.OnFlush(ch, ms)) fired++;
        return fired;
    }

    [Fact]
    public void HealthyRate_NeverFires()
    {
        var t = new FlushRateTripwire(maxPerSec: 5, sustainSec: 3);
        Assert.Equal(0, Drive(t, Ch9, 0, 20_000, perSec: 2));   // 2/s for 20s → quiet
    }

    [Fact]
    public void BoundaryRate_ExactlyMax_NeverFires()
    {
        var t = new FlushRateTripwire(maxPerSec: 5, sustainSec: 3);
        Assert.Equal(0, Drive(t, Ch9, 0, 20_000, perSec: 5));   // exactly 5/s is NOT > 5/s
    }

    [Fact]
    public void SustainedStorm_FiresExactlyOnce()
    {
        var t = new FlushRateTripwire(maxPerSec: 5, sustainSec: 3);
        // ~60/s (a per-Tick mark storm) for 10 seconds → one warning, not a warning storm.
        Assert.Equal(1, Drive(t, Ch9, 0, 10_000, perSec: 60));
    }

    [Fact]
    public void ShortBurst_UnderSustain_NeverFires()
    {
        var t = new FlushRateTripwire(maxPerSec: 5, sustainSec: 3);
        // 60/s but only for 2 seconds (a bulk hourly sweep / join seed) → below the 3s sustain → quiet.
        Assert.Equal(0, Drive(t, Ch9, 0, 2_000, perSec: 60));
    }

    [Fact]
    public void QuietGap_ResetsRun_ThenSecondStormFiresAgain()
    {
        var t = new FlushRateTripwire(maxPerSec: 5, sustainSec: 3);
        Assert.Equal(1, Drive(t, Ch9, 0, 10_000, perSec: 60));        // storm 1 → fired once
        Assert.Equal(0, Drive(t, Ch9, 20_000, 5_000, perSec: 1));     // quiet spell → run resets, re-arms
        Assert.Equal(1, Drive(t, Ch9, 40_000, 10_000, perSec: 60));   // storm 2 → fires once more
    }

    [Fact]
    public void Channels_AreIndependent()
    {
        var t = new FlushRateTripwire(maxPerSec: 5, sustainSec: 3);
        int fired1 = 0, fired9 = 0;
        for (long ms = 0; ms < 10_000; ms += 16)   // ch9 storms...
        {
            if (t.OnFlush(9, ms)) fired9++;
            if (ms % 1000 == 0 && t.OnFlush(1, ms)) fired1++;   // ...ch1 stays healthy (1/s)
        }
        Assert.Equal(1, fired9);
        Assert.Equal(0, fired1);
    }

    [Fact]
    public void Reset_DropsState()
    {
        var t = new FlushRateTripwire(maxPerSec: 5, sustainSec: 3);
        Assert.Equal(1, Drive(t, Ch9, 0, 10_000, perSec: 60));
        t.Reset();
        // Fresh state: the same storm must build its 3 sustained seconds again, then fire once.
        Assert.Equal(1, Drive(t, Ch9, 100_000, 10_000, perSec: 60));
    }
}
