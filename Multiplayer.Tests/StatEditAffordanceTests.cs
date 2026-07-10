using Multiplayer.Network.Sync.State;
using Xunit;

// Per-peer OPTIMISTIC minus-button click counter for the thin-client stat editor (minus-affordance 2026-07-11):
// increment on the local +1 click, decrement ONLY when the authoritative live stat is observed to drop (a landed
// refund) — a host-denied click leaves the live stat unchanged → no decrement. Pure static state — each test
// resets first (xunit parallelizes across classes).
public class StatEditAffordanceTests
{
    // ── RecordPlus raises the net; Net reads it (the minus affordance gate) ──

    [Fact]
    public void RecordPlus_RaisesNet()
    {
        StatEditAffordance.ResetSession();
        Assert.Equal(0, StatEditAffordance.Net(7, 0));
        StatEditAffordance.RecordPlus(7, 0);
        StatEditAffordance.RecordPlus(7, 0);
        Assert.Equal(2, StatEditAffordance.Net(7, 0));
    }

    // ── ObserveLiveStat: first sight = baseline (no decrement); a rise never decrements ──

    [Fact]
    public void ObserveLiveStat_FirstSight_IsBaseline_NoDecrement()
    {
        StatEditAffordance.ResetSession();
        StatEditAffordance.RecordPlus(1, 1);
        Assert.Equal(0, StatEditAffordance.ObserveLiveStat(1, 1, 10));   // prev unseen → record only
        Assert.Equal(1, StatEditAffordance.Net(1, 1));
    }

    [Fact]
    public void ObserveLiveStat_Rise_IsConfirmedSpend_NoDecrement()
    {
        StatEditAffordance.ResetSession();
        StatEditAffordance.RecordPlus(1, 0);
        StatEditAffordance.RecordPlus(1, 0);
        StatEditAffordance.ObserveLiveStat(1, 0, 10);                    // baseline
        Assert.Equal(0, StatEditAffordance.ObserveLiveStat(1, 0, 12));   // stat went UP → already counted at click
        Assert.Equal(2, StatEditAffordance.Net(1, 0));
    }

    // ── A drop = confirmed refund → net lowered by the drop ──

    [Fact]
    public void ObserveLiveStat_Drop_DecrementsNetByTheDrop()
    {
        StatEditAffordance.ResetSession();
        StatEditAffordance.RecordPlus(2, 2);
        StatEditAffordance.RecordPlus(2, 2);
        StatEditAffordance.RecordPlus(2, 2);
        StatEditAffordance.ObserveLiveStat(2, 2, 13);                    // baseline (start+3)
        Assert.Equal(2, StatEditAffordance.ObserveLiveStat(2, 2, 11));   // dropped 2 → dec 2
        Assert.Equal(1, StatEditAffordance.Net(2, 2));
    }

    // ── DENY: a click the host rejected leaves the live stat unchanged → no decrement ──

    [Fact]
    public void ObserveLiveStat_NoChange_IsDeny_NoDecrement()
    {
        StatEditAffordance.ResetSession();
        StatEditAffordance.RecordPlus(3, 0);
        StatEditAffordance.RecordPlus(3, 0);
        StatEditAffordance.ObserveLiveStat(3, 0, 5);                     // baseline
        Assert.Equal(0, StatEditAffordance.ObserveLiveStat(3, 0, 5));    // denied refund → stat unchanged → no dec
        Assert.Equal(2, StatEditAffordance.Net(3, 0));
    }

    [Fact]
    public void ObserveLiveStat_Drop_NeverBelowZero()
    {
        StatEditAffordance.ResetSession();
        StatEditAffordance.RecordPlus(4, 1);
        StatEditAffordance.ObserveLiveStat(4, 1, 5);                     // baseline
        Assert.Equal(3, StatEditAffordance.ObserveLiveStat(4, 1, 2));   // dropped 3, only 1 counted → dec 3 reported
        Assert.Equal(0, StatEditAffordance.Net(4, 1));                   // net floored at 0
    }

    // ── Independence across stats and units ──

    [Fact]
    public void Stats_And_Units_AreIndependent()
    {
        StatEditAffordance.ResetSession();
        StatEditAffordance.RecordPlus(1, 0);
        StatEditAffordance.RecordPlus(1, 0);
        StatEditAffordance.RecordPlus(1, 2);
        StatEditAffordance.RecordPlus(9, 0);
        Assert.Equal(2, StatEditAffordance.Net(1, 0));
        Assert.Equal(0, StatEditAffordance.Net(1, 1));   // untouched will
        Assert.Equal(1, StatEditAffordance.Net(1, 2));
        Assert.Equal(1, StatEditAffordance.Net(9, 0));
        StatEditAffordance.ObserveLiveStat(1, 0, 10);
        StatEditAffordance.ObserveLiveStat(1, 0, 9);     // unit 1 strength drop
        Assert.Equal(1, StatEditAffordance.Net(1, 0));
        Assert.Equal(1, StatEditAffordance.Net(9, 0));   // unit 9 unaffected
    }

    // ── Guards: invalid stat id is inert (Net 0, RecordPlus/Observe no-op) ──

    [Fact]
    public void InvalidStatId_IsInert()
    {
        StatEditAffordance.ResetSession();
        StatEditAffordance.RecordPlus(3, 5);              // out of range → no-op
        Assert.Equal(0, StatEditAffordance.Net(3, 5));
        Assert.Equal(0, StatEditAffordance.ObserveLiveStat(3, 5, 4));
    }

    // ── Resets: dismissal (one unit) and session start/end (all) ──

    [Fact]
    public void ResetUnit_DropsOneUnit_KeepsOthers()
    {
        StatEditAffordance.ResetSession();
        StatEditAffordance.RecordPlus(1, 0);
        StatEditAffordance.RecordPlus(2, 0);
        StatEditAffordance.ResetUnit(1);
        Assert.Equal(0, StatEditAffordance.Net(1, 0));
        Assert.Equal(1, StatEditAffordance.Net(2, 0));
    }

    [Fact]
    public void ResetSession_ClearsEverything()
    {
        StatEditAffordance.RecordPlus(1, 0);
        StatEditAffordance.RecordPlus(2, 1);
        StatEditAffordance.ResetSession();
        Assert.Equal(0, StatEditAffordance.Net(1, 0));
        Assert.Equal(0, StatEditAffordance.Net(2, 1));
        // last-observed also cleared → next observe re-baselines (no phantom decrement)
        Assert.Equal(0, StatEditAffordance.ObserveLiveStat(1, 0, 3));
    }
}
