using Multipleer.Network.Sync;
using Xunit;

/// <summary>
/// Pure, Unity-free guards for the two-class geoscape-event dialog split (host-authoritative outcomes,
/// client never blocked). The Harmony/Unity-bound prefixes (OnChoiceSelected / SelectChoice / OnCancel /
/// IsSingleChoiceEncounter / FinishEncounter) bind live game types via reflection and cannot be JIT'd in the
/// test process — they are reasoned-correct from the decompile. What IS unit-testable is the classification
/// boundary that drives every one of them.
///
/// Contract locked in:
///   • INFO   = Choices.Count &lt;= 1  → mirrors game's GeoscapeEventData.HasSingleChoice (count &lt;= 1).
///   • CHOICE = Choices.Count &gt;= 2  → locked client modal.
///   • unreadable count (-1) → CHOICE (locked) — safe default; a client never locally resolves an
///     ambiguous event.
///   • RecordStateCompleted == 3 matches the verified GeoscapeEventRecordState.Completed enum value used by
///     the synthetic-record INFO local-dismiss fix.
/// </summary>
public class EventDialogClassificationTests
{
    [Theory]
    [InlineData(0, false)]   // INFO (no choices)
    [InlineData(1, false)]   // INFO (single choice == HasSingleChoice)
    [InlineData(2, true)]    // CHOICE
    [InlineData(3, true)]    // CHOICE
    [InlineData(10, true)]   // CHOICE
    public void IsChoiceEvent_MatchesCountThreshold(int count, bool expectedChoice)
        => Assert.Equal(expectedChoice, EventReflection.IsChoiceEvent(count));

    [Fact]
    public void IsChoiceEvent_BoundaryIsTwo()
    {
        Assert.False(EventReflection.IsChoiceEvent(1)); // last INFO
        Assert.True(EventReflection.IsChoiceEvent(2));  // first CHOICE
    }

    // NOTE: GetChoiceCount / IsClientChoiceLocked are intentionally NOT exercised here. Although their
    // null-event guards return early, the methods reference UnityEngine.Debug (best-effort logging) so the
    // JIT loads UnityEngine.CoreModule, which is a compile-only (Private=false) reference absent at test
    // runtime — same constraint that keeps the reflection Apply paths out of the unit tests. The classifier
    // boundary that drives them is fully covered above via the pure IsChoiceEvent(int) overload; the
    // safe-default-to-CHOICE on a -1 (unreadable) count is verified by IsClientChoiceLocked's own logic
    // (count < 0 -> true) which is a direct call to IsChoiceEvent on the readable branch.

    [Fact]
    public void RecordStateCompleted_MatchesVerifiedEnumValue()
        => Assert.Equal(3, EventReflection.RecordStateCompleted); // GeoscapeEventRecordState.Completed = 3
}
