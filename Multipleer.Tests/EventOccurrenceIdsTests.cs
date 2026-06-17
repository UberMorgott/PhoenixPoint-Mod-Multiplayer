using Multipleer.Harmony.Sync;
using Xunit;

/// <summary>
/// Host-side occurrence-id authority guards. No native per-occurrence id exists, so the host synthesizes a
/// monotonic id per raised event instance and maps it across raise→dismiss for the SAME instance.
/// These tests run serially (Collection) because the authority is process-global static state.
/// </summary>
[Collection("EventOccurrenceIds")]
public class EventOccurrenceIdsTests
{
    [Fact]
    public void Assign_IsMonotonic_AndSkipsZero()
    {
        EventOccurrenceIds.ResetForTests();
        var a = new object();
        var b = new object();
        var c = new object();

        ushort ia = EventOccurrenceIds.Assign(a);
        ushort ib = EventOccurrenceIds.Assign(b);
        ushort ic = EventOccurrenceIds.Assign(c);

        Assert.True(ia != 0);            // 0 is the null/none sentinel — never handed out
        Assert.True(ib > ia);            // monotonic
        Assert.True(ic > ib);
    }

    [Fact]
    public void GetOrAssign_SameInstance_ReturnsSameId_AcrossRaiseDismiss()
    {
        EventOccurrenceIds.ResetForTests();
        var evt = new object();

        ushort atRaise = EventOccurrenceIds.Assign(evt);          // raise postfix
        ushort atDismiss = EventOccurrenceIds.GetOrAssign(evt);   // dismiss postfix, same instance

        Assert.Equal(atRaise, atDismiss);   // SAME id maps across raise→dismiss for one occurrence
    }

    [Fact]
    public void Assign_DistinctInstances_GetDistinctIds_EvenSameDefId()
    {
        EventOccurrenceIds.ResetForTests();
        // Two distinct GeoscapeEvent-like instances that would share the SAME def-id on the wire.
        var occ1 = new object();
        var occ2 = new object();

        ushort id1 = EventOccurrenceIds.Assign(occ1);
        ushort id2 = EventOccurrenceIds.Assign(occ2);

        Assert.NotEqual(id1, id2);   // no collision despite identical def-id
    }

    [Fact]
    public void GetOrAssign_UnseenInstance_AssignsFreshId()
    {
        EventOccurrenceIds.ResetForTests();
        var seen = new object();
        EventOccurrenceIds.Assign(seen);

        // An instance the host never saw raised (answer applied directly) still gets a unique id.
        var unseen = new object();
        ushort id = EventOccurrenceIds.GetOrAssign(unseen);
        Assert.True(id != 0);
        Assert.Equal(id, EventOccurrenceIds.GetOrAssign(unseen)); // stable on the second read
    }

    [Fact]
    public void NullEvent_ReturnsZeroSentinel()
    {
        EventOccurrenceIds.ResetForTests();
        Assert.Equal(0, EventOccurrenceIds.Assign(null));
        Assert.Equal(0, EventOccurrenceIds.GetOrAssign(null));
    }
}
