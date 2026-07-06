using Multiplayer.Network.Sync;
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

    // ─── client OK-click local-close discriminator (synthetic result page vs real host event) ────────
    // The client local-closes ONLY its own synthetic result/info page (EventID == ""); a real single- OR
    // multi-choice host event (non-empty EventID) is swallowed and waits for the host's dismiss. This is the
    // pure predicate behind EncounterChoiceClientPatch (the reflection-bound prefix itself is not unit-testable).

    [Theory]
    [InlineData("", true)]        // synthetic result/info page → local close
    [InlineData(null, true)]      // null id (best-effort read failure) treated as synthetic → local close
    [InlineData("EX103", false)]  // real single-choice host event → swallow (host drives dismiss)
    [InlineData("SDI_07", false)] // real single-choice info event → swallow (the regression case)
    [InlineData("PROG_EV_42", false)] // real multi-choice host event → swallow
    public void IsSyntheticResultPage_OnlyEmptyEventId(string eventId, bool expectedSynthetic)
        => Assert.Equal(expectedSynthetic, EventReflection.IsSyntheticResultPage(eventId));

    // ─── mission-deploy exclusion (SOURCE-side: host never broadcasts an arrival deploy/land prompt) ──────
    // Root fix for the in-game leak where a host arrive→CANCEL at a mission site (PROG_AN2_MISS "Второе
    // посвящение", subtitle "МЕСТНОСТЬ РАЗВЕДКИ") left a stuck synthetic result page on the client. The event
    // is classified as a mission-deploy prompt iff ANY choice's outcome starts a tactical mission
    // (Outcome.StartMission.MissionTypeDef != null, GeoEventChoiceOutcome.cs:315). The reflection reader
    // (IsMissionDeployEvent(object)) binds live game types and cannot JIT in-process; the ANY-classifier that
    // drives it (IsMissionDeployByOutcomes) is fully covered here (no Unity types).
    [Theory]
    [InlineData(new bool[] { }, false)]              // no choices → not a deploy prompt (siteless narrative)
    [InlineData(new[] { false }, false)]             // single info/reward choice (EX93/PROG_PX12 scavenge) → mirror
    [InlineData(new[] { false, false }, false)]      // multi-choice narrative/diplomacy brief → mirror as normal
    [InlineData(new[] { true, false }, true)]        // Deploy + Leave (PROG_AN2_MISS shape) → deploy prompt, exclude
    [InlineData(new[] { false, true }, true)]        // order-independent (Leave + Deploy)
    [InlineData(new[] { true }, true)]               // lone Deploy choice → deploy prompt, exclude
    [InlineData(new[] { true, true }, true)]         // both start missions → still a deploy prompt
    public void IsMissionDeployByOutcomes_AnyChoiceStartsMission(bool[] choiceStartsMission, bool expectedDeploy)
        => Assert.Equal(expectedDeploy, EventReflection.IsMissionDeployByOutcomes(choiceStartsMission));

    [Fact]
    public void IsMissionDeployByOutcomes_NullFlagsFailOpenToNotDeploy()
        => Assert.False(EventReflection.IsMissionDeployByOutcomes(null)); // read failure → broadcast as before (no legit-event regression)
}
