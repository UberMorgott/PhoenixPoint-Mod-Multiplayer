using Multiplayer.Network.Sync;
using Xunit;

// Co-op edit-session engine rebuild (2026-07-08): the PURE state machine behind the soldier-equip sync.
// These pin the TERMINATION INVARIANTS + the exact loop shapes that burned the 3 reverted guard-patch
// rounds (defer-forever, per-Tick re-arm+churn, drop→clobber). capTicks=2000 (~2s of ms) throughout.
public class EditSessionTests
{
    private static EditSession New(long cap = 2000) => new EditSession(cap);

    // ─── open / close lifecycle ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Open_SetsTarget_ClearsGestureAndPending()
    {
        var s = New();
        s.Open(7);
        Assert.True(s.IsOpen);
        Assert.Equal(7, s.Target);
        Assert.False(s.GestureInFlight);
        Assert.False(s.PendingRepaint);
    }

    [Fact]
    public void Open_DropsStalePendingFromPriorScreen()
    {
        var s = New();
        s.Open(1);
        s.RemoteApplied();                 // owed a repaint on soldier 1
        s.Open(2);                         // switch soldier before it drained
        Assert.False(s.PendingRepaint);    // a drag-skip armed in the dying screen must not fire here
        Assert.Equal(2, s.Target);
    }

    [Fact]
    public void Close_ClearsAllState()
    {
        var s = New();
        s.GestureBegin(3, 0);
        s.RemoteApplied();
        s.Close(3);
        Assert.False(s.IsOpen);
        Assert.False(s.GestureInFlight);
        Assert.False(s.PendingRepaint);
        Assert.False(s.DrainRepaint(10_000));   // nothing survives the screen
    }

    [Fact]
    public void Close_OfADifferentUnit_IsIgnored()
    {
        var s = New();
        s.Open(5);
        s.Close(99);                       // stale ExitState of an already-replaced screen
        Assert.True(s.IsOpen);
        Assert.Equal(5, s.Target);
    }

    // ─── immediate repaint (no gesture) ────────────────────────────────────────────────────────────────

    [Fact]
    public void RemoteApplied_NoGesture_DrainsImmediatelyOnce()
    {
        var s = New();
        s.Open(1);
        s.RemoteApplied();
        Assert.True(s.DrainRepaint(0));    // no drag → repaint now
        Assert.False(s.DrainRepaint(0));   // fires AT MOST once (clear-before-fire, no re-arm)
    }

    [Fact]
    public void DrainRepaint_WithNothingOwed_IsFalse()
    {
        var s = New();
        s.Open(1);
        Assert.False(s.DrainRepaint(0));   // the fast-path Tick no-op
    }

    // ─── defer while a gesture is in flight ────────────────────────────────────────────────────────────

    [Fact]
    public void RemoteApplied_DuringGesture_IsDeferred_ThenFiresOnGestureEnd()
    {
        var s = New();
        s.GestureBegin(1, 0);
        s.RemoteApplied();
        Assert.True(s.ShouldDeferRepaint(100));
        Assert.False(s.DrainRepaint(100));   // held while dragging
        s.GestureEnd(1);
        Assert.False(s.ShouldDeferRepaint(100));
        Assert.True(s.DrainRepaint(100));    // released the instant the drag ends
        Assert.False(s.DrainRepaint(100));   // ...exactly once
    }

    [Fact]
    public void ManyAppliesDuringGesture_CoalesceToOneDrain()
    {
        var s = New();
        s.GestureBegin(1, 0);
        s.RemoteApplied();
        s.RemoteApplied();
        s.RemoteApplied();
        s.GestureEnd(1);
        Assert.True(s.DrainRepaint(50));     // one repaint owed for N applies
        Assert.False(s.DrainRepaint(50));
    }

    [Fact]
    public void ShouldDeferRepaint_IsFalse_WhenOpenButIdle()
    {
        var s = New();
        s.Open(1);
        Assert.False(s.ShouldDeferRepaint(0));   // only an IN-FLIGHT gesture defers, never mere screen-open
    }

    // ─── the hard time cap (anti-freeze — the b9144e5 "deferred forever" lesson) ────────────────────────

    [Fact]
    public void Cap_ForcesRepaint_EvenMidGesture()
    {
        var s = New(2000);
        s.GestureBegin(1, 0);
        s.RemoteApplied();
        Assert.True(s.ShouldDeferRepaint(1999));    // within cap → still deferring
        Assert.False(s.DrainRepaint(1999));
        Assert.False(s.ShouldDeferRepaint(2000));   // cap reached → deferral broken (drag still in flight)
        Assert.True(s.DrainRepaint(2000));          // forced repaint mid-gesture (stale beats frozen)
        Assert.False(s.DrainRepaint(2001));
    }

    [Fact]
    public void Cap_NeverDefers_WhenNonPositive()
    {
        var s = New(0);
        s.GestureBegin(1, 0);
        s.RemoteApplied();
        Assert.False(s.ShouldDeferRepaint(0));   // defensive: cap<=0 → apply always repaints immediately
        Assert.True(s.DrainRepaint(0));
    }

    // ─── the exact loop shape that collapsed fps: per-Tick drain of a still-held drag ───────────────────

    [Fact]
    public void HeldGesture_RepeatedTickDrain_NeverReArmsNorFiresEarly()
    {
        var s = New(2000);
        s.GestureBegin(1, 0);
        s.RemoteApplied();
        // Simulate 60 Hz Tick drains for ~1.5s of a held drag: every one is a no-op, no re-arm, no early fire.
        for (long t = 0; t < 1500; t += 16)
            Assert.False(s.DrainRepaint(t));
        Assert.True(s.PendingRepaint);           // still owed, still deferred — not lost, not multiplied
        // Cap elapses → exactly ONE fire, then quiescent forever until a NEW apply.
        Assert.True(s.DrainRepaint(2000));
        for (long t = 2016; t < 4000; t += 16)
            Assert.False(s.DrainRepaint(t));     // no re-arm without a new RemoteApplied
    }

    [Fact]
    public void GestureEnd_AloneNeverArmsARepaint()
    {
        var s = New();
        s.GestureBegin(1, 0);
        s.GestureEnd(1);                         // a drag with no apply during it
        Assert.False(s.DrainRepaint(100));       // nothing owed → nothing fires
        Assert.False(s.PendingRepaint);
    }

    [Fact]
    public void GestureBegin_OnNotOpenSession_SelfOpensTheTarget()
    {
        var s = New();
        s.GestureBegin(42, 0);                   // a raw begin-drag beat the open hook
        Assert.True(s.IsOpen);
        Assert.Equal(42, s.Target);
        Assert.True(s.GestureInFlight);
    }

    // ─── a full realistic sequence: open → drag → apply mid-drag → drop → paint ─────────────────────────

    [Fact]
    public void FullSequence_DragThenApplyThenDrop_PaintsOnceAtDrop()
    {
        var s = New(2000);
        s.Open(1);
        s.GestureBegin(1, 100);                  // user grabs an item
        s.RemoteApplied();                       // a peer's #9 lands mid-drag → deferred
        Assert.False(s.DrainRepaint(150));       // Tick during drag: held
        Assert.False(s.DrainRepaint(200));
        s.GestureEnd(1);                         // user drops
        Assert.True(s.DrainRepaint(210));        // next Tick paints the authoritative state once
        Assert.False(s.DrainRepaint(220));
    }
}
