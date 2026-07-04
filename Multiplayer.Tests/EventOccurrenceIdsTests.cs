using Multiplayer.Harmony.Sync;
using Xunit;

/// <summary>
/// Host-side occurrence-id authority guards. No native per-occurrence id exists, so the host synthesizes a
/// monotonic id per GeoscapeEvent instance via order-independent <c>GetOrAssign</c> and maps it across the
/// raise↔dismiss pair for the SAME instance — in EITHER order (a single-choice event auto-completes, i.e.
/// dismisses, BEFORE it is raised for display). Also covers the dismiss-dedup tracking that stops the
/// FinishEncounter fallback from double-dismissing.
/// These tests run serially (Collection) because the authority is process-global static state.
/// </summary>
[Collection("EventOccurrenceIds")]
public class EventOccurrenceIdsTests
{
    [Fact]
    public void GetOrAssign_IsMonotonic_AndSkipsZero()
    {
        EventOccurrenceIds.ResetForTests();
        var a = new object();
        var b = new object();
        var c = new object();

        ushort ia = EventOccurrenceIds.GetOrAssign(a);
        ushort ib = EventOccurrenceIds.GetOrAssign(b);
        ushort ic = EventOccurrenceIds.GetOrAssign(c);

        Assert.True(ia != 0);            // 0 is the null/none sentinel — never handed out
        Assert.True(ib > ia);            // monotonic
        Assert.True(ic > ib);
    }

    [Fact]
    public void GetOrAssign_SameInstance_ReturnsSameId_RaiseThenDismiss()
    {
        EventOccurrenceIds.ResetForTests();
        var evt = new object();

        ushort atRaise = EventOccurrenceIds.GetOrAssign(evt);     // raise postfix first
        ushort atDismiss = EventOccurrenceIds.GetOrAssign(evt);   // dismiss postfix, same instance

        Assert.Equal(atRaise, atDismiss);   // SAME id maps across raise→dismiss for one occurrence
    }

    [Fact]
    public void GetOrAssign_SameInstance_ReturnsSameId_DismissThenRaise_BugARegression()
    {
        // Bug A: for a single-choice event the host CompleteEvent's (dismiss) at trigger time BEFORE the raise
        // (GeoscapeEventSystem.OnEventTriggered :659→:661, one instance). The earlier Assign() OVERWROTE on the
        // raise → dismiss got occId=1, raise got occId=2 → client couldn't correlate. GetOrAssign must reuse.
        EventOccurrenceIds.ResetForTests();
        var evt = new object();

        ushort atDismiss = EventOccurrenceIds.GetOrAssign(evt);   // dismiss postfix runs FIRST (auto-complete)
        ushort atRaise = EventOccurrenceIds.GetOrAssign(evt);     // raise postfix runs SECOND, same instance

        Assert.Equal(atDismiss, atRaise);   // ONE id for the occurrence regardless of order (was 1 vs 2 before)
    }

    [Fact]
    public void GetOrAssign_DistinctInstances_GetDistinctIds_EvenSameDefId()
    {
        EventOccurrenceIds.ResetForTests();
        // Two distinct GeoscapeEvent-like instances that would share the SAME def-id on the wire.
        var occ1 = new object();
        var occ2 = new object();

        ushort id1 = EventOccurrenceIds.GetOrAssign(occ1);
        ushort id2 = EventOccurrenceIds.GetOrAssign(occ2);

        Assert.NotEqual(id1, id2);   // no collision despite identical def-id
    }

    [Fact]
    public void NullEvent_ReturnsZeroSentinel()
    {
        EventOccurrenceIds.ResetForTests();
        Assert.Equal(0, EventOccurrenceIds.GetOrAssign(null));
    }

    // ─── Dismiss-dedup tracking (stops the FinishEncounter fallback double-dismiss) ───────────

    [Fact]
    public void MarkDismissed_ThenWasDismissed_IsTrue()
    {
        EventOccurrenceIds.ResetForTests();
        var evt = new object();
        ushort occ = EventOccurrenceIds.GetOrAssign(evt);

        Assert.False(EventOccurrenceIds.WasDismissed(occ));   // not yet
        EventOccurrenceIds.MarkDismissed(occ);
        Assert.True(EventOccurrenceIds.WasDismissed(occ));    // authoritative dismiss recorded
    }

    [Fact]
    public void WasDismissed_UnmarkedOccurrence_IsFalse()
    {
        EventOccurrenceIds.ResetForTests();
        var a = new object();
        var b = new object();
        ushort occA = EventOccurrenceIds.GetOrAssign(a);
        ushort occB = EventOccurrenceIds.GetOrAssign(b);
        EventOccurrenceIds.MarkDismissed(occA);

        Assert.True(EventOccurrenceIds.WasDismissed(occA));
        Assert.False(EventOccurrenceIds.WasDismissed(occB));  // a different occurrence is unaffected
    }

    [Fact]
    public void WasDismissed_ZeroSentinel_IsFalse()
    {
        EventOccurrenceIds.ResetForTests();
        EventOccurrenceIds.MarkDismissed(0);                  // no-op for the null sentinel
        Assert.False(EventOccurrenceIds.WasDismissed(0));
    }

    // ─── Reverse lookup occId → live event (host arbiter resolves a claim against the LIVE instance) ───

    [Fact]
    public void TryGetEvent_ReturnsAssignedInstance()
    {
        EventOccurrenceIds.ResetForTests();
        var ev = new object();   // stand-in for a live GeoscapeEvent (the table is type-agnostic)
        ushort id = EventOccurrenceIds.GetOrAssign(ev);
        Assert.True(EventOccurrenceIds.TryGetEvent(id, out var got));
        Assert.Same(ev, got);
    }

    [Fact]
    public void TryGetEvent_UnknownId_ReturnsFalse()
    {
        EventOccurrenceIds.ResetForTests();
        Assert.False(EventOccurrenceIds.TryGetEvent(4242, out var got));
        Assert.Null(got);
    }

    [Fact]
    public void TryGetEvent_Zero_ReturnsFalse()
    {
        EventOccurrenceIds.ResetForTests();
        Assert.False(EventOccurrenceIds.TryGetEvent(0, out _));
    }
}
