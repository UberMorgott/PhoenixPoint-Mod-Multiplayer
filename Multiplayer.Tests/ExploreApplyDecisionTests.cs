using Multiplayer.Network.Sync;
using Xunit;

/// <summary>
/// Pure guard for the HOST explore-apply path (Symptom A): route a client-relayed explore order through the
/// native <c>ExploreSiteAbility.Activate</c> ONLY when the ability + default target resolved AND CanActivate is
/// true; otherwise fall back to the raw direct <c>StartExploringCurrentSite</c>. The reflection that produces the
/// three booleans is in-game bound (not unit-testable); this pins the boundary decision.
/// </summary>
public class ExploreApplyDecisionTests
{
    [Fact]
    public void AllTrue_UsesNativeActivate()
        => Assert.Equal(ExploreApplyPath.NativeActivate,
            ExploreApplyDecision.Decide(abilityResolved: true, targetResolved: true, canActivate: true));

    [Theory]
    // Any single missing fact → the defensive direct fallback (never drop a valid host order).
    [InlineData(false, true, true)]    // ability unresolved (bind miss / TFTV shape change)
    [InlineData(true, false, true)]    // no default target (e.g. CurrentSite null)
    [InlineData(true, true, false)]    // CanActivate false (already inspected / invalid / mid-explore)
    [InlineData(false, false, false)]  // nothing resolved
    public void AnyMissingFact_FallsBackDirect(bool ability, bool target, bool can)
        => Assert.Equal(ExploreApplyPath.FallbackDirect,
            ExploreApplyDecision.Decide(ability, target, can));
}
