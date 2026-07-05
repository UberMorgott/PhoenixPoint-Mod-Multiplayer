using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure (Unity-free) guards for the report-modal whitelist + variant map. The reflection-bound
/// TryBuild reads live game modalData and cannot be JIT'd in the test process — what IS unit-testable is the
/// classification boundary that drives whether the host broadcasts and the client suppresses a given ModalType.
/// </summary>
public class ReportModalClassifierTests
{
    // ── whitelist: the 4 Phase-A "A-variant" reports + the 4 mirrored mission briefs ──────────
    [Theory]
    [InlineData(4)]    // GeoScavengeBrief (site deploy brief — mirrored view-locked since 2026-07-05)
    [InlineData(6)]    // GeoPhoenixBaseOutcome
    [InlineData(14)]   // GeoResearchComplete
    [InlineData(15)]   // GeoAmbushBrief (mandatory ambush prompt — mirrored view-locked)
    [InlineData(25)]   // PandoranRevealResult
    [InlineData(26)]   // AncientSiteAttackBrief (site deploy brief — mirrored view-locked)
    [InlineData(28)]   // AncientSiteDefenceBrief (site deploy brief — mirrored view-locked)
    [InlineData(38)]   // DiplomacyResearchBrief
    public void IsReportModal_WhitelistedReports_True(int modalType)
        => Assert.True(ReportModalClassifier.IsReportModal(modalType));

    [Theory]
    [InlineData(0)]    // GeoHavenAttackBrief (host-local: GeoHavenDefenseMission ctor needs runtime attacker/zone)
    [InlineData(1)]    // GeoHavenAttackOutcome (Phase-B mission outcome)
    [InlineData(2)]    // GeoAlienBaseBrief (host-local: one ModalType, two mission classes — wrong-class rebuild throws)
    [InlineData(5)]    // GeoScavengeOutcome (Phase-B mission outcome — only the BRIEF is mirrored)
    [InlineData(7)]    // LoadPrompt
    [InlineData(8)]    // SiteEncounter (encounter modal — event-adjacent; MUST stay out of the report channel, S3)
    [InlineData(11)]   // GeoPhoenixBaseDefenseBrief (host-local: ctor needs runtime attackingSites)
    [InlineData(13)]   // DualClassPicker (decision)
    [InlineData(16)]   // GeoAmbushOutcome (Phase-B mission outcome — only the BRIEF is mirrored)
    [InlineData(20)]   // GeoPhoenixBaseInfestationBrief (host-local: bind reads live haven state)
    [InlineData(23)]   // AlienResearchBrief (deferred C)
    [InlineData(27)]   // AncientSiteAttackOutcome (Phase-B mission outcome)
    [InlineData(33)]   // InterceptionOutcome (deferred C)
    [InlineData(34)]   // BehemothAttackBrief (host-local: fallback modal, no single rebuild ctor)
    [InlineData(36)]   // InfestedHavenBrief (host-local: bind reads live haven/deployment state)
    [InlineData(37)]   // InfestedHavenOutcome (Phase-B mission outcome)
    [InlineData(40)]   // GameDemoEnd
    [InlineData(-1)]   // None
    [InlineData(9999)] // _CustomMission (would alias to a small byte if truncated — must stay false)
    public void IsReportModal_NonReports_False(int modalType)
        => Assert.False(ReportModalClassifier.IsReportModal(modalType));

    // ── S3 channel-ownership guard: across the ENTIRE ModalType enum, ONLY the whitelisted modals mirror.
    // Proves no OTHER brief/outcome/encounter/decision modal can ever leak onto the 0x69 report channel — the
    // geoscape EVENT channel (0x65/0x66) has exclusive ownership of event windows (which carry no ModalType at
    // all), and the 9e80b24 EVENT-rail deploy-prompt exclusion is a separate, untouched boundary. If a new
    // modal is ever whitelisted, this test forces the author to update the expected set deliberately.
    [Fact]
    public void IsReportModal_OnlyTheWhitelistedReports_AcrossEntireModalTypeEnum()
    {
        var whitelisted = new System.Collections.Generic.HashSet<int> { 4, 6, 14, 15, 25, 26, 28, 38 };
        // ModalType spans None=-1 and 0..40 (GameHavenAttackBrief..GameDemoEnd), plus _CustomMission=9999.
        foreach (var modalType in EnumerateModalTypeValues())
            Assert.Equal(whitelisted.Contains(modalType), ReportModalClassifier.IsReportModal(modalType));
    }

    // ── blocking classification: the mandatory ambush brief + every mirrored SITE MISSION BRIEF arm the host
    // intent gate + the client view-lock (host Confirm → tactical deploy flow; host Cancel → ReportModalHide
    // closes the client copy). NOTHING else may ever be blocking without deliberate intent — an unmirrored
    // modal classified blocking would freeze the client for a window it can never see resolve.
    [Fact]
    public void IsBlockingModal_OnlyTheMirroredMissionBriefs_AcrossEntireModalTypeEnum()
    {
        var blocking = new System.Collections.Generic.HashSet<int>
        {
            ReportModalClassifier.GeoAmbushBrief,          // 15
            ReportModalClassifier.GeoScavengeBrief,        // 4
            ReportModalClassifier.AncientSiteAttackBrief,  // 26
            ReportModalClassifier.AncientSiteDefenceBrief, // 28
        };
        foreach (var modalType in EnumerateModalTypeValues())
            Assert.Equal(blocking.Contains(modalType), ReportModalClassifier.IsBlockingModal(modalType));
    }

    [Fact]
    public void EveryBlockingModal_IsAlsoWhitelisted()
    {
        // A blocking-but-unmirrored type would arm the host gate for a window the client never shows — the
        // release still fires (ModalResultCallback) but the classification must stay consistent by design.
        foreach (var modalType in EnumerateModalTypeValues())
            if (ReportModalClassifier.IsBlockingModal(modalType))
                Assert.True(ReportModalClassifier.IsReportModal(modalType));
    }

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
    [InlineData(4, ReportModalVariant.SiteMissionBrief)]  // GeoScavengeBrief
    [InlineData(26, ReportModalVariant.SiteMissionBrief)] // AncientSiteAttackBrief
    [InlineData(28, ReportModalVariant.SiteMissionBrief)] // AncientSiteDefenceBrief
    public void VariantFor_MapsEachWhitelistedModal(int modalType, ReportModalVariant expected)
        => Assert.Equal(expected, ReportModalClassifier.VariantFor(modalType));

    // ── persistence (matches the native opener: persistent for NullData/SiteOnly and EVERY mission brief —
    // all briefs open via ShowMissionBriefing → OpenModalPersistent, GeoscapeView.cs:1903) ──
    [Theory]
    [InlineData(ReportModalVariant.NullData, true)]
    [InlineData(ReportModalVariant.SiteOnly, true)]
    [InlineData(ReportModalVariant.Research, false)]
    [InlineData(ReportModalVariant.Diplomacy, false)]
    [InlineData(ReportModalVariant.AmbushBrief, true)]
    [InlineData(ReportModalVariant.SiteMissionBrief, true)]
    public void IsPersistent_MatchesNativeOpener(ReportModalVariant variant, bool expected)
        => Assert.Equal(expected, ReportModalClassifier.IsPersistent(variant));

    // ── deferred host broadcast (research nav-flag read timing, soak 2026-07-05) ──────────────────────
    // ONLY the Research report defers: its payload reads ResearchElement.UnlocksResearches, which is stale
    // inside the OpenModal Postfix (the Research.OnResearchCompleted dispatch hasn't flipped dependent
    // elements to Revealed/Unlocked yet → host shipped NavHidden for a research that genuinely unlocks →
    // client force-hid the host's native "new research available" line). Blocking briefs must stay
    // synchronous (their host intent gate arms in the Postfix); the rest have no read-timing hazard.
    [Theory]
    [InlineData(14, true)]    // GeoResearchComplete → deferred one tick (post-cascade read)
    [InlineData(4, false)]    // GeoScavengeBrief — blocking, must arm/broadcast synchronously
    [InlineData(15, false)]   // GeoAmbushBrief — blocking
    [InlineData(26, false)]   // AncientSiteAttackBrief — blocking
    [InlineData(28, false)]   // AncientSiteDefenceBrief — blocking
    [InlineData(6, false)]    // GeoPhoenixBaseOutcome
    [InlineData(25, false)]   // PandoranRevealResult
    [InlineData(38, false)]   // DiplomacyResearchBrief (ShareLevel is a plain diplomacy int — no recompute)
    [InlineData(0, false)]    // non-whitelisted
    public void ShouldDeferHostBroadcast_OnlyResearch(int modalType, bool expected)
        => Assert.Equal(expected, ReportModalClassifier.ShouldDeferHostBroadcast(modalType));

    // The exact 2026-07-05 failure combo, pinned end-to-end on the pure side: a stale queue-time read folds
    // to NavHidden (FlagFor(false)) and — because Hidden is a DEFINITE flag — the client would force-hide the
    // line (ShouldOverride true). The defer decision above is what prevents that stale read from ever being
    // taken; a genuinely-unknown read (NavUnknown) must stay fail-open (no override → client-native compute).
    [Fact]
    public void StaleHiddenForcesHide_ButUnknownFailsOpen_RegressionCombo()
    {
        int staleFlag = ResearchNavMirror.FlagFor(false);   // what the stale queue-time read shipped
        Assert.Equal(ResearchNavMirror.NavHidden, staleFlag);
        Assert.True(ResearchNavMirror.ShouldOverride(isHost: false, isActiveSession: true, navFlag: staleFlag));
        Assert.False(ResearchNavMirror.NavVisible(staleFlag));   // → button stripped on the client (the bug)

        // Fail-open contract (unchanged): an unknown flag never forces anything — client stays native.
        Assert.False(ResearchNavMirror.ShouldOverride(isHost: false, isActiveSession: true,
                                                      navFlag: ResearchNavMirror.NavUnknown));
    }
}
