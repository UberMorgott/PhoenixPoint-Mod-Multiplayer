using Multiplayer.Network;
using Xunit;

public class ReconnectPolicyTests
{
    // ---- IsHeartbeatTimedOut: strictly-greater boundary (matches SessionManager) ----

    [Fact]
    public void Heartbeat_GapBelowTimeout_NotTimedOut()
    {
        Assert.False(ReconnectPolicy.IsHeartbeatTimedOut(0, 19999, 20000));
    }

    [Fact]
    public void Heartbeat_GapExactlyEqualTimeout_NotTimedOut()
    {
        // Exactly-equal is NOT timed out: SessionManager uses `now - last > timeout`.
        Assert.False(ReconnectPolicy.IsHeartbeatTimedOut(0, 20000, 20000));
    }

    [Fact]
    public void Heartbeat_GapStrictlyGreater_IsTimedOut()
    {
        Assert.True(ReconnectPolicy.IsHeartbeatTimedOut(0, 20001, 20000));
    }

    [Fact]
    public void Heartbeat_UsesDefaultTimeoutConstant()
    {
        Assert.Equal(20000, ReconnectPolicy.DefaultTimeoutMs);
        Assert.False(ReconnectPolicy.IsHeartbeatTimedOut(1000, 1000 + 20000, ReconnectPolicy.DefaultTimeoutMs));
        Assert.True(ReconnectPolicy.IsHeartbeatTimedOut(1000, 1000 + 20001, ReconnectPolicy.DefaultTimeoutMs));
    }

    // ---- IsHostLoss: both branches, delegating to SessionLifecycle ----

    [Fact]
    public void IsHostLoss_IntentionalDisconnect_False()
    {
        Assert.False(ReconnectPolicy.IsHostLoss(localDisconnectIntentional: true));
    }

    [Fact]
    public void IsHostLoss_UnexpectedDrop_True()
    {
        Assert.True(ReconnectPolicy.IsHostLoss(localDisconnectIntentional: false));
    }

    [Fact]
    public void IsHostLoss_MatchesSessionLifecycle()
    {
        // Thin alias — must never drift from the canonical predicate.
        Assert.Equal(SessionLifecycle.ShouldNotifyHostLeft(true), ReconnectPolicy.IsHostLoss(true));
        Assert.Equal(SessionLifecycle.ShouldNotifyHostLeft(false), ReconnectPolicy.IsHostLoss(false));
    }

    // ---- BackoffDelayMs: exponential sequence, cap saturation, overflow, negatives ----

    [Fact]
    public void Backoff_Sequence_DoublesEachAttempt()
    {
        Assert.Equal(1000, ReconnectPolicy.BackoffDelayMs(0, 1000, 30000)); // base
        Assert.Equal(2000, ReconnectPolicy.BackoffDelayMs(1, 1000, 30000)); // 2x
        Assert.Equal(4000, ReconnectPolicy.BackoffDelayMs(2, 1000, 30000)); // 4x
        Assert.Equal(8000, ReconnectPolicy.BackoffDelayMs(3, 1000, 30000)); // 8x
    }

    [Fact]
    public void Backoff_SaturatesAtCap()
    {
        // 1000 * 2^5 = 32000 > 30000 cap → clamped.
        Assert.Equal(30000, ReconnectPolicy.BackoffDelayMs(5, 1000, 30000));
        Assert.Equal(30000, ReconnectPolicy.BackoffDelayMs(6, 1000, 30000));
    }

    [Fact]
    public void Backoff_LargeAttempt_SaturatesToCap_NotNegative()
    {
        // Would overflow long if doubled naively; must saturate to cap, never wrap negative.
        long delay = ReconnectPolicy.BackoffDelayMs(1000, 1000, 30000);
        Assert.Equal(30000, delay);
        Assert.True(delay > 0);
    }

    [Fact]
    public void Backoff_IntMaxAttempt_SaturatesToCap()
    {
        long delay = ReconnectPolicy.BackoffDelayMs(int.MaxValue, 1000, 30000);
        Assert.Equal(30000, delay);
        Assert.True(delay > 0);
    }

    [Fact]
    public void Backoff_NegativeAttempt_ClampedToBase()
    {
        Assert.Equal(1000, ReconnectPolicy.BackoffDelayMs(-1, 1000, 30000));
        Assert.Equal(1000, ReconnectPolicy.BackoffDelayMs(-999, 1000, 30000));
    }

    [Fact]
    public void Backoff_BaseAlreadyAboveCap_ClampedToCap()
    {
        Assert.Equal(30000, ReconnectPolicy.BackoffDelayMs(0, 50000, 30000));
    }

    [Fact]
    public void Backoff_UsesDefaultConstants()
    {
        Assert.Equal(1000, ReconnectPolicy.DefaultBaseMs);
        Assert.Equal(30000, ReconnectPolicy.DefaultCapMs);
        Assert.Equal(4000, ReconnectPolicy.BackoffDelayMs(2, ReconnectPolicy.DefaultBaseMs, ReconnectPolicy.DefaultCapMs));
    }

    // ---- ShouldGiveUp: boundary ----

    [Fact]
    public void GiveUp_BelowMax_False()
    {
        Assert.False(ReconnectPolicy.ShouldGiveUp(4, 5));
    }

    [Fact]
    public void GiveUp_AtMax_True()
    {
        // Boundary: attempt == maxAttempts → give up.
        Assert.True(ReconnectPolicy.ShouldGiveUp(5, 5));
    }

    [Fact]
    public void GiveUp_AboveMax_True()
    {
        Assert.True(ReconnectPolicy.ShouldGiveUp(6, 5));
    }
}
