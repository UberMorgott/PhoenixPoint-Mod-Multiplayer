using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure (Unity-free) guards for the Phase-A report-modal whitelist + variant map. The reflection-bound
/// TryBuild reads live game modalData and cannot be JIT'd in the test process — what IS unit-testable is the
/// classification boundary that drives whether the host broadcasts and the client suppresses a given ModalType.
/// </summary>
public class ReportModalClassifierTests
{
    // ── whitelist: the 4 Phase-A "A-variant" report modals + the blocking ambush brief ──────────
    [Theory]
    [InlineData(6)]    // GeoPhoenixBaseOutcome
    [InlineData(14)]   // GeoResearchComplete
    [InlineData(15)]   // GeoAmbushBrief (mandatory ambush prompt — mirrored view-locked)
    [InlineData(25)]   // PandoranRevealResult
    [InlineData(38)]   // DiplomacyResearchBrief
    public void IsReportModal_WhitelistedReports_True(int modalType)
        => Assert.True(ReportModalClassifier.IsReportModal(modalType));

    [Theory]
    [InlineData(0)]    // GeoHavenAttackBrief (OPTIONAL deploy brief — host-local, host cancel stays silent)
    [InlineData(1)]    // GeoHavenAttackOutcome (Phase-B mission outcome)
    [InlineData(4)]    // GeoScavengeBrief (OPTIONAL deploy brief — 9e80b24 interplay: never mirror)
    [InlineData(5)]    // GeoScavengeOutcome (Phase-B mission outcome)
    [InlineData(7)]    // LoadPrompt
    [InlineData(8)]    // SiteEncounter (encounter modal — event-adjacent; MUST stay out of the report channel, S3)
    [InlineData(13)]   // DualClassPicker (decision)
    [InlineData(16)]   // GeoAmbushOutcome (Phase-B mission outcome — only the BRIEF is mirrored)
    [InlineData(23)]   // AlienResearchBrief (deferred C)
    [InlineData(33)]   // InterceptionOutcome (deferred C)
    [InlineData(37)]   // InfestedHavenOutcome (Phase-B mission outcome)
    [InlineData(40)]   // GameDemoEnd
    [InlineData(-1)]   // None
    [InlineData(9999)] // _CustomMission (would alias to a small byte if truncated — must stay false)
    public void IsReportModal_NonReports_False(int modalType)
        => Assert.False(ReportModalClassifier.IsReportModal(modalType));

    // ── S3 channel-ownership guard: across the ENTIRE ModalType enum, ONLY the five whitelisted modals mirror.
    // Proves no OTHER brief/outcome/encounter/decision modal can ever leak onto the 0x69 report channel — the
    // geoscape EVENT channel (0x65/0x66) has exclusive ownership of event windows (which carry no ModalType at
    // all), and OPTIONAL deploy briefs stay host-local (9e80b24 interplay). If a new report is ever whitelisted,
    // this test forces the author to update the expected set deliberately.
    [Fact]
    public void IsReportModal_OnlyTheWhitelistedReports_AcrossEntireModalTypeEnum()
    {
        var whitelisted = new System.Collections.Generic.HashSet<int> { 6, 14, 15, 25, 38 };
        // ModalType spans None=-1 and 0..40 (GameHavenAttackBrief..GameDemoEnd), plus _CustomMission=9999.
        foreach (var modalType in EnumerateModalTypeValues())
            Assert.Equal(whitelisted.Contains(modalType), ReportModalClassifier.IsReportModal(modalType));
    }

    // ── blocking classification: GeoAmbushBrief is the ONE blocking prompt (mandatory, no cancel) — it arms
    // the host intent gate + the client view-lock. NOTHING else may ever be blocking without deliberate intent
    // (an optional brief classified blocking would freeze the client for a host-cancelable window).
    [Fact]
    public void IsBlockingModal_OnlyTheAmbushBrief_AcrossEntireModalTypeEnum()
    {
        foreach (var modalType in EnumerateModalTypeValues())
            Assert.Equal(modalType == ReportModalClassifier.GeoAmbushBrief,
                ReportModalClassifier.IsBlockingModal(modalType));
    }

    [Fact]
    public void IsBlockingModal_AmbushBrief_IsAlsoWhitelisted()
        => Assert.True(ReportModalClassifier.IsReportModal(ReportModalClassifier.GeoAmbushBrief));

    private static System.Collections.Generic.IEnumerable<int> EnumerateModalTypeValues()
    {
        yield return -1;      // None
        for (int i = 0; i <= 40; i++) yield return i;
        yield return 9999;    // _CustomMission
    }

    // ── variant map ──────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(6, ReportModalVariant.NullData)]    // GeoPhoenixBaseOutcome
    [InlineData(25, ReportModalVariant.SiteOnly)]   // PandoranRevealResult
    [InlineData(14, ReportModalVariant.Research)]   // GeoResearchComplete
    [InlineData(38, ReportModalVariant.Diplomacy)]  // DiplomacyResearchBrief
    [InlineData(15, ReportModalVariant.AmbushBrief)] // GeoAmbushBrief
    public void VariantFor_MapsEachWhitelistedModal(int modalType, ReportModalVariant expected)
        => Assert.Equal(expected, ReportModalClassifier.VariantFor(modalType));

    // ── persistence (matches the native opener: persistent for NullData/SiteOnly/AmbushBrief —
    // the ambush brief opens via ShowMissionBriefing → OpenModalPersistent, GeoscapeView.cs:1903) ──
    [Theory]
    [InlineData(ReportModalVariant.NullData, true)]
    [InlineData(ReportModalVariant.SiteOnly, true)]
    [InlineData(ReportModalVariant.Research, false)]
    [InlineData(ReportModalVariant.Diplomacy, false)]
    [InlineData(ReportModalVariant.AmbushBrief, true)]
    public void IsPersistent_MatchesNativeOpener(ReportModalVariant variant, bool expected)
        => Assert.Equal(expected, ReportModalClassifier.IsPersistent(variant));
}
