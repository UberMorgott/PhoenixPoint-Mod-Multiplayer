using System;
using System.Reflection;
using Xunit;

/// <summary>
/// Regression test for the SECOND client RESULT-card "zero reward lines" bug.
///
/// The game type <c>Base.UI.LocalizedTextBind</c> declares a SINGLE display method with an OPTIONAL
/// parameter: <c>public string Localize(string language = null)</c> (LocalizedTextBind.cs:35). Native
/// reward text resolves a resource name via <c>...DisplayName1.Localize()</c>
/// (UIModuleSiteEncounters.cs:415), which the C# compiler emits as <c>Localize(null)</c> — i.e. ONE
/// argument is always passed.
///
/// The client mirror invokes that method by reflection (<c>RewardDisplayReflection.LocalizeBind</c>).
/// The original call <c>_localize.Invoke(bind, null)</c> passes a NULL parameter array, which
/// <see cref="MethodBase.Invoke(object, object[])"/> treats as ZERO arguments. Optional defaults are a
/// compile-time feature; reflection does NOT auto-apply them, so a 0-length arg set against a 1-parameter
/// method throws <see cref="TargetParameterCountException"/>. <c>LocalizeBind</c>'s try/catch swallowed it
/// and returned null, so EVERY localized reward delta line (resources / diplomacy / items / sites / …)
/// resolved to an empty display name and dropped — the client drew 0 reward lines
/// ("reward-type resolve raw=64 name='Research' -> display=''" in-game).
///
/// Fix: pass an explicit 1-element argument array (<c>new object[] { null }</c>, language=null) so the
/// reflected call matches the method's parameter count exactly, mirroring native <c>Localize(null)</c>.
/// </summary>
public class RewardDisplayLocalizeInvokeTests
{
    // Test double mirroring LocalizedTextBind.Localize(string language = null) (LocalizedTextBind.cs:35):
    // one optional string parameter; returns the (already-resolved) display text.
    private class FakeLocalizedTextBind
    {
        public string LocalizationKey = "MATERIALS";
        public string Localize(string language = null) => LocalizationKey;
    }

    private static MethodInfo LocalizeMethod() =>
        typeof(FakeLocalizedTextBind).GetMethod("Localize");

    [Fact]
    public void OldInvoke_WithNullArgArray_ThrowsTargetParameterCount()
    {
        // Documents the bug: a null (== zero-length) parameter array does NOT satisfy the one optional
        // parameter — reflection never fills the default — so Invoke throws before the method runs.
        var mi = LocalizeMethod();
        Assert.Throws<TargetParameterCountException>(() => mi.Invoke(new FakeLocalizedTextBind(), null));
    }

    [Fact]
    public void FixedInvoke_WithSingleNullArg_ResolvesDisplayName()
    {
        // The fix: a 1-element arg array (language = null) matches the parameter count and returns the
        // resolved display name — exactly what native DisplayName1.Localize() == Localize(null) produces.
        var mi = LocalizeMethod();
        var result = mi.Invoke(new FakeLocalizedTextBind(), new object[] { null }) as string;

        Assert.Equal("MATERIALS", result);
        Assert.False(string.IsNullOrEmpty(result));   // a non-empty display name => the reward line renders
    }
}
