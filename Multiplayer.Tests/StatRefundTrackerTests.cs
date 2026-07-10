using Multiplayer.Network.Sync.State;
using Xunit;

// HOST anti-farm ledger for per-click stat REFUNDS (per-click stat relay 2026-07-10): a negative
// SpendStatPointsAction may return AT MOST the net positive points applied to that (unit, stat) this session,
// so a refund never banks free SP nor drops a stat below its session-start value. Pure static state — each test
// resets first (xunit parallelizes across classes).
public class StatRefundTrackerTests
{
    // ── ClampRefund: the pure ceiling arithmetic (signed-delta refund decision) ──

    [Fact]
    public void ClampRefund_NonPositiveRequest_IsZero()
    {
        Assert.Equal(0, StatRefundTracker.ClampRefund(5, 0));
        Assert.Equal(0, StatRefundTracker.ClampRefund(5, -3));
    }

    [Fact]
    public void ClampRefund_NegativeNet_IsZero()
    {
        Assert.Equal(0, StatRefundTracker.ClampRefund(-2, 3));
    }

    [Fact]
    public void ClampRefund_BoundedByNet()
    {
        Assert.Equal(2, StatRefundTracker.ClampRefund(2, 5));   // want 5, only 2 applied → 2
        Assert.Equal(3, StatRefundTracker.ClampRefund(5, 3));   // want 3, plenty applied → 3
        Assert.Equal(4, StatRefundTracker.ClampRefund(4, 4));   // exact
    }

    // ── Spend raises the cap; refund lowers it (floored at 0) ──

    [Fact]
    public void Spend_RaisesCap_RefundLowersIt()
    {
        StatRefundTracker.ResetSession();
        Assert.Equal(0, StatRefundTracker.RefundableCap(7, 0));
        StatRefundTracker.RecordSpend(7, 0, 3);
        Assert.Equal(3, StatRefundTracker.RefundableCap(7, 0));
        StatRefundTracker.RecordSpend(7, 0, 2);
        Assert.Equal(5, StatRefundTracker.RefundableCap(7, 0));
        StatRefundTracker.RecordRefund(7, 0, 4);
        Assert.Equal(1, StatRefundTracker.RefundableCap(7, 0));
    }

    [Fact]
    public void Refund_NeverBelowZero()
    {
        StatRefundTracker.ResetSession();
        StatRefundTracker.RecordSpend(1, 1, 2);
        StatRefundTracker.RecordRefund(1, 1, 9);   // over-refund clamps the net at 0
        Assert.Equal(0, StatRefundTracker.RefundableCap(1, 1));
    }

    [Fact]
    public void Refund_CannotExceedNetApplied_AntiFarm()
    {
        StatRefundTracker.ResetSession();
        StatRefundTracker.RecordSpend(2, 2, 3);
        int cap = StatRefundTracker.RefundableCap(2, 2);
        Assert.Equal(3, StatRefundTracker.ClampRefund(cap, 10));   // asked to refund 10, only 3 spent → 3
    }

    // ── Independence across stats and units ──

    [Fact]
    public void Stats_And_Units_AreIndependent()
    {
        StatRefundTracker.ResetSession();
        StatRefundTracker.RecordSpend(1, 0, 4);   // unit 1 strength
        StatRefundTracker.RecordSpend(1, 2, 1);   // unit 1 speed
        StatRefundTracker.RecordSpend(9, 0, 2);   // unit 9 strength
        Assert.Equal(4, StatRefundTracker.RefundableCap(1, 0));
        Assert.Equal(0, StatRefundTracker.RefundableCap(1, 1));   // untouched will
        Assert.Equal(1, StatRefundTracker.RefundableCap(1, 2));
        Assert.Equal(2, StatRefundTracker.RefundableCap(9, 0));
    }

    // ── Guards: invalid stat id / non-positive points are inert ──

    [Fact]
    public void InvalidStatId_And_NonPositivePoints_AreNoOps()
    {
        StatRefundTracker.ResetSession();
        StatRefundTracker.RecordSpend(3, 5, 4);    // stat id out of range
        StatRefundTracker.RecordSpend(3, 0, 0);    // zero points
        StatRefundTracker.RecordSpend(3, 1, -2);   // negative points
        Assert.Equal(0, StatRefundTracker.RefundableCap(3, 0));
        Assert.Equal(0, StatRefundTracker.RefundableCap(3, 1));
        Assert.Equal(0, StatRefundTracker.RefundableCap(3, 5));
    }

    // ── Resets: dismissal (one unit) and session start/end (all) ──

    [Fact]
    public void ResetUnit_DropsOneUnit_KeepsOthers()
    {
        StatRefundTracker.ResetSession();
        StatRefundTracker.RecordSpend(1, 0, 3);
        StatRefundTracker.RecordSpend(2, 0, 3);
        StatRefundTracker.ResetUnit(1);
        Assert.Equal(0, StatRefundTracker.RefundableCap(1, 0));
        Assert.Equal(3, StatRefundTracker.RefundableCap(2, 0));
    }

    [Fact]
    public void ResetSession_ClearsEverything()
    {
        StatRefundTracker.RecordSpend(1, 0, 3);
        StatRefundTracker.RecordSpend(2, 1, 5);
        StatRefundTracker.ResetSession();
        Assert.Equal(0, StatRefundTracker.RefundableCap(1, 0));
        Assert.Equal(0, StatRefundTracker.RefundableCap(2, 1));
    }
}
