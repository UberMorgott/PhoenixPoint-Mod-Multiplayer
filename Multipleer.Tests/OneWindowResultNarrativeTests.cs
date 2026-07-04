using Multipleer.Network.Sync;
using Xunit;

/// <summary>
/// Fix: a VoidOmen-style event (single choice, empty outcome text → host's oneWindow
/// IsSingleChoiceEncounter) mirrored to a client as an EMPTY result parchment — the buffered
/// reward-less dismiss resolved straight to BuildResultEvent, whose body was the chosen choice's
/// OUTCOME text (empty for a one-window event) with NO narrative → blank page. The host instead
/// shows the RAISE narrative in that single combined window.
///
/// Native rule (UIModuleSiteEncounters.SetClosingEncounter :332-336): the single-choice combined
/// window is built with <c>useEventTexts:true</c>, so when the chosen choice's outcome text is empty
/// the body falls back to the raise narrative — <c>Description.Last().GetText(context)</c>. A
/// MULTI-choice close (OnChoiceSelected :580-595, useEventTexts defaults false) never falls back:
/// an empty-outcome + reward-less multi-choice click just <c>FinishEncounter()</c>s (no page), so
/// the narrative substitution must be gated to the single-choice one-window shape ONLY.
///
/// The pure body-selection decision is extracted so it is unit-testable (the surrounding
/// BuildResultEvent is reflection-bound and never JIT'd in tests, same as ChooseResultOkLabelKey).
/// </summary>
public class OneWindowResultNarrativeTests
{
    [Fact]
    public void OneWindowEmptyOutcome_UsesRaiseNarrative()
        => Assert.Equal("The Void whispers.",
            EventReflection.ChooseResultBodyText(outcomeText: "", narrativeText: "The Void whispers.", singleChoiceOneWindow: true));

    [Fact]
    public void OneWindowWithOutcomeText_KeepsOutcome_NarrativeIgnored()
        => Assert.Equal("Outcome shown.",
            EventReflection.ChooseResultBodyText(outcomeText: "Outcome shown.", narrativeText: "narrative", singleChoiceOneWindow: true));

    [Fact]
    public void MultiChoiceEmptyOutcome_NeverFallsBackToNarrative()
        => Assert.Equal("",
            EventReflection.ChooseResultBodyText(outcomeText: "", narrativeText: "narrative", singleChoiceOneWindow: false));

    [Fact]
    public void MultiChoiceWithOutcomeText_KeepsOutcome()
        => Assert.Equal("Outcome shown.",
            EventReflection.ChooseResultBodyText(outcomeText: "Outcome shown.", narrativeText: "narrative", singleChoiceOneWindow: false));

    [Fact]
    public void OneWindowEmptyOutcome_NullNarrative_DegradesToEmpty_NoThrow()
        => Assert.Equal("",
            EventReflection.ChooseResultBodyText(outcomeText: "", narrativeText: null, singleChoiceOneWindow: true));

    // ─── VoidOmen never-blank narrative fallback (ChooseWireNarrative) ─────────────────
    // A runtime-narrative def (TFTV VoidOmen_{0..19}) can reach the host broadcast with an EMPTY
    // Description (base def key ""; the flavor literal-bind mutation is absent after a save/load or a
    // non-GenerateVoidOmenEvent re-raise) while its Title still resolves. Description.Last().GetText → ""
    // would ship a blank narrative → blank mirrored window. The host degrades to the resolved Title.

    [Fact]
    public void WireNarrative_DescriptionPresent_UsesDescription()
        => Assert.Equal("The Void whispers.",
            EventReflection.ChooseWireNarrative(descriptionText: "The Void whispers.", titleText: "Void Omen I"));

    [Fact]
    public void WireNarrative_EmptyDescription_FallsBackToTitle()
        => Assert.Equal("Void Omen I",
            EventReflection.ChooseWireNarrative(descriptionText: "", titleText: "Void Omen I"));

    [Fact]
    public void WireNarrative_BothEmpty_DegradesToEmpty_NoThrow()
        => Assert.Equal("",
            EventReflection.ChooseWireNarrative(descriptionText: "", titleText: null));
}
