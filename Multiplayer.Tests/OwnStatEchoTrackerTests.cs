using Multiplayer.Network.Sync.State;
using Xunit;

// Own-echo skip counter (per-click stat relay 2026-07-10): each OWN stat +/- click a peer makes is registered;
// when its authoritative #9 echo would ConflictRepaint the open soldier, one echo is consumed and the repaint is
// downgraded to a buffer-preserving PartialRepaint so the native minus gate stays open. Only a FOREIGN change
// (count 0) still discards the buffer. Pure static state — each test resets first (xunit parallelizes classes).
public class OwnStatEchoTrackerTests
{
    [Fact]
    public void NoRegistration_ConsumeIsFalse_ForeignChangeStillConflicts()
    {
        OwnStatEchoTracker.ResetSession();
        Assert.False(OwnStatEchoTracker.TryConsumeEcho(42));   // count 0 → not a self-echo
        Assert.Equal(0, OwnStatEchoTracker.Outstanding(42));
    }

    [Fact]
    public void Register_ThenConsume_IsSelfEcho_Once()
    {
        OwnStatEchoTracker.ResetSession();
        OwnStatEchoTracker.RegisterOwnClick(7);
        Assert.Equal(1, OwnStatEchoTracker.Outstanding(7));
        Assert.True(OwnStatEchoTracker.TryConsumeEcho(7));     // self-echo
        Assert.Equal(0, OwnStatEchoTracker.Outstanding(7));
        Assert.False(OwnStatEchoTracker.TryConsumeEcho(7));    // drained → next is foreign
    }

    [Fact]
    public void Register_Decrements_OnePerEcho()
    {
        OwnStatEchoTracker.ResetSession();
        OwnStatEchoTracker.RegisterOwnClick(3);
        OwnStatEchoTracker.RegisterOwnClick(3);
        OwnStatEchoTracker.RegisterOwnClick(3);
        Assert.Equal(3, OwnStatEchoTracker.Outstanding(3));
        Assert.True(OwnStatEchoTracker.TryConsumeEcho(3));
        Assert.True(OwnStatEchoTracker.TryConsumeEcho(3));
        Assert.Equal(1, OwnStatEchoTracker.Outstanding(3));
        Assert.True(OwnStatEchoTracker.TryConsumeEcho(3));
        Assert.False(OwnStatEchoTracker.TryConsumeEcho(3));    // floored at 0, never negative
    }

    [Fact]
    public void Units_AreIndependent()
    {
        OwnStatEchoTracker.ResetSession();
        OwnStatEchoTracker.RegisterOwnClick(1);
        OwnStatEchoTracker.RegisterOwnClick(2);
        OwnStatEchoTracker.RegisterOwnClick(2);
        Assert.True(OwnStatEchoTracker.TryConsumeEcho(1));
        Assert.False(OwnStatEchoTracker.TryConsumeEcho(1));    // unit 1 drained
        Assert.Equal(2, OwnStatEchoTracker.Outstanding(2));    // unit 2 untouched
    }

    [Fact]
    public void ResetUnit_DropsOneUnit_KeepsOthers()
    {
        OwnStatEchoTracker.ResetSession();
        OwnStatEchoTracker.RegisterOwnClick(1);
        OwnStatEchoTracker.RegisterOwnClick(2);
        OwnStatEchoTracker.ResetUnit(1);
        Assert.False(OwnStatEchoTracker.TryConsumeEcho(1));
        Assert.True(OwnStatEchoTracker.TryConsumeEcho(2));
    }

    [Fact]
    public void ResetSession_ClearsEverything()
    {
        OwnStatEchoTracker.RegisterOwnClick(5);
        OwnStatEchoTracker.RegisterOwnClick(6);
        OwnStatEchoTracker.ResetSession();
        Assert.Equal(0, OwnStatEchoTracker.Outstanding(5));
        Assert.Equal(0, OwnStatEchoTracker.Outstanding(6));
    }
}
