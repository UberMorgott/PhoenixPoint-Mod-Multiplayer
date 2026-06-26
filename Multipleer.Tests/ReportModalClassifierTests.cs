using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure (Unity-free) guards for the Phase-A report-modal whitelist + variant map. The reflection-bound
/// TryBuild reads live game modalData and cannot be JIT'd in the test process — what IS unit-testable is the
/// classification boundary that drives whether the host broadcasts and the client suppresses a given ModalType.
/// </summary>
public class ReportModalClassifierTests
{
    // ── whitelist: ONLY the 4 Phase-A "A-variant" report modals ──────────────────────────────────
    [Theory]
    [InlineData(6)]    // GeoPhoenixBaseOutcome
    [InlineData(14)]   // GeoResearchComplete
    [InlineData(25)]   // PandoranRevealResult
    [InlineData(38)]   // DiplomacyResearchBrief
    public void IsReportModal_WhitelistedReports_True(int modalType)
        => Assert.True(ReportModalClassifier.IsReportModal(modalType));

    [Theory]
    [InlineData(0)]    // GeoHavenAttackBrief (brief)
    [InlineData(1)]    // GeoHavenAttackOutcome (Phase-B mission outcome)
    [InlineData(5)]    // GeoScavengeOutcome (Phase-B mission outcome)
    [InlineData(7)]    // LoadPrompt
    [InlineData(13)]   // DualClassPicker (decision)
    [InlineData(23)]   // AlienResearchBrief (deferred C)
    [InlineData(33)]   // InterceptionOutcome (deferred C)
    [InlineData(37)]   // InfestedHavenOutcome (Phase-B mission outcome)
    [InlineData(40)]   // GameDemoEnd
    [InlineData(-1)]   // None
    [InlineData(9999)] // _CustomMission (would alias to byte 15 if truncated — must stay false)
    public void IsReportModal_NonReports_False(int modalType)
        => Assert.False(ReportModalClassifier.IsReportModal(modalType));

    // ── variant map ──────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(6, ReportModalVariant.NullData)]    // GeoPhoenixBaseOutcome
    [InlineData(25, ReportModalVariant.SiteOnly)]   // PandoranRevealResult
    [InlineData(14, ReportModalVariant.Research)]   // GeoResearchComplete
    [InlineData(38, ReportModalVariant.Diplomacy)]  // DiplomacyResearchBrief
    public void VariantFor_MapsEachWhitelistedModal(int modalType, ReportModalVariant expected)
        => Assert.Equal(expected, ReportModalClassifier.VariantFor(modalType));

    // ── persistence (matches the native opener: persistent for NullData/SiteOnly, OpenModal for the rest) ──
    [Theory]
    [InlineData(ReportModalVariant.NullData, true)]
    [InlineData(ReportModalVariant.SiteOnly, true)]
    [InlineData(ReportModalVariant.Research, false)]
    [InlineData(ReportModalVariant.Diplomacy, false)]
    public void IsPersistent_MatchesNativeOpener(ReportModalVariant variant, bool expected)
        => Assert.Equal(expected, ReportModalClassifier.IsPersistent(variant));
}
