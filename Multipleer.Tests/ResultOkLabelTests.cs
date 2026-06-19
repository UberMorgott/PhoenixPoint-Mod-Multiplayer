using Multipleer.Network.Sync;
using Xunit;

/// <summary>
/// R7 fix: the synthetic result page's single dismiss button must be labelled with the NATIVE OK key
/// (UIModuleSiteEncounters.OKTextKey), NEVER the chosen choice's own Text — labelling it with choice.Text
/// reproduced the clicked choice button (the in-game "duplicated choice button, no OK" symptom). The pure
/// key-selection decision is extracted so it is unit-testable (the surrounding BuildResultEvent is
/// reflection-bound and never JIT'd in tests).
///
/// Native rule (UIModuleSiteEncounters.SetClosingEncounter :347-350): label = OKTextKey when its
/// LocalizationKey is non-empty; the chosen choice's Text is used ONLY on the useEventTexts paging path
/// (never the multi-choice click), so the mod's result page always prefers the native OK key.
/// </summary>
public class ResultOkLabelTests
{
    [Fact]
    public void UsesNativeOkKey_WhenPresent()
        => Assert.Equal("OK_KEY", EventReflection.ChooseResultOkLabelKey("OK_KEY"));

    [Fact]
    public void FallsBackToLiteralOk_WhenNativeKeyNull()
        => Assert.Equal("OK", EventReflection.ChooseResultOkLabelKey(null));

    [Fact]
    public void FallsBackToLiteralOk_WhenNativeKeyEmpty()
        => Assert.Equal("OK", EventReflection.ChooseResultOkLabelKey(""));

    [Fact]
    public void NeverUsesChoiceText_OnlyDependsOnNativeOkKey()
    {
        // The selection takes ONLY the native OK key — there is no choice-text parameter, so a chosen choice's
        // label can never leak into the result page's dismiss button (regression guard for the dup-button bug).
        Assert.Equal("CONTINUE_KEY", EventReflection.ChooseResultOkLabelKey("CONTINUE_KEY"));
    }
}
