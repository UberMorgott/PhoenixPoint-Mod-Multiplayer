using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure (Unity-free) guards for the report-modal whitelist + variant map. The reflection-bound
/// TryBuild reads live game modalData and cannot be JIT'd in the test process — what IS unit-testable is the
/// classification boundary that drives whether the host broadcasts and the client suppresses a given ModalType.
/// </summary>
public class ReportModalClassifierTests
{
    // ── whitelist: the 4 Phase-A "A-variant" reports + the 4 mirrored mission briefs
    //    + the Batch-1 ActiveMissionBrief family (P1-mirrored LIVE→site-id briefs, 2026-07-05 spec) ──────────
    [Theory]
    [InlineData(0)]    // GeoHavenAttackBrief (haven defense — binds the P1-mirrored site.ActiveMission)
    [InlineData(2)]    // GeoAlienBaseBrief (two classes — the P1 record's discriminator picks the exact one)
    [InlineData(4)]    // GeoScavengeBrief (site deploy brief — mirrored view-locked since 2026-07-05)
    [InlineData(6)]    // GeoPhoenixBaseOutcome
    [InlineData(11)]   // GeoPhoenixBaseDefenseBrief (BASE ATTACK — auto-opens via OnSiteMissionStarted)
    [InlineData(14)]   // GeoResearchComplete
    [InlineData(15)]   // GeoAmbushBrief (mandatory ambush prompt — mirrored view-locked)
    [InlineData(20)]   // GeoPhoenixBaseInfestationBrief
    [InlineData(25)]   // PandoranRevealResult
    [InlineData(26)]   // AncientSiteAttackBrief (site deploy brief — mirrored view-locked)
    [InlineData(28)]   // AncientSiteDefenceBrief (site deploy brief — mirrored view-locked)
    [InlineData(34)]   // BehemothAttackBrief (fallback family — mirrors, rebuild always degrades to the notice)
    [InlineData(36)]   // InfestedHavenBrief
    [InlineData(38)]   // DiplomacyResearchBrief
    public void IsReportModal_WhitelistedReports_True(int modalType)
        => Assert.True(ReportModalClassifier.IsReportModal(modalType));

    [Theory]
    [InlineData(1)]    // GeoHavenAttackOutcome (Phase-B mission outcome)
    [InlineData(5)]    // GeoScavengeOutcome (Phase-B mission outcome — only the BRIEF is mirrored)
    [InlineData(7)]    // LoadPrompt
    [InlineData(8)]    // SiteEncounter (encounter modal — event-adjacent; MUST stay out of the report channel, S3)
    [InlineData(12)]   // GeoPhoenixBaseDefenseOutcome (Phase-B mission outcome)
    [InlineData(13)]   // DualClassPicker (decision)
    [InlineData(16)]   // GeoAmbushOutcome (Phase-B mission outcome — only the BRIEF is mirrored)
    [InlineData(21)]   // GeoPhoenixBaseInfestationOutcome (Phase-B mission outcome)
    [InlineData(23)]   // AlienResearchBrief (deferred — P8 verify-first)
    [InlineData(27)]   // AncientSiteAttackOutcome (Phase-B mission outcome)
    [InlineData(33)]   // InterceptionOutcome (deferred C)
    [InlineData(35)]   // BehemothAttackOutcome (Phase-B mission outcome)
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
        var whitelisted = new System.Collections.Generic.HashSet<int>
            { 0, 2, 4, 6, 11, 14, 15, 20, 25, 26, 28, 34, 36, 38 };
        // ModalType spans None=-1 and 0..40 (GameHavenAttackBrief..GameDemoEnd), plus _CustomMission=9999.
        foreach (var modalType in EnumerateModalTypeValues())
            Assert.Equal(whitelisted.Contains(modalType), ReportModalClassifier.IsReportModal(modalType));
    }

    // ── blocking classification: the mandatory ambush brief + every mirrored MISSION BRIEF (site briefs
    // {4,26,28} + the Batch-1 ActiveMissionBrief family {0,2,11,20,34,36}) arm the host intent gate + the
    // client view-lock (host Confirm → tactical deploy flow; host Cancel → ReportModalHide closes the client
    // copy). NOTHING else may ever be blocking without deliberate intent — an unmirrored modal classified
    // blocking would freeze the client for a window it can never see resolve.
    [Fact]
    public void IsBlockingModal_OnlyTheMirroredMissionBriefs_AcrossEntireModalTypeEnum()
    {
        var blocking = new System.Collections.Generic.HashSet<int>
        {
            ReportModalClassifier.GeoAmbushBrief,          // 15
            ReportModalClassifier.GeoScavengeBrief,        // 4
            ReportModalClassifier.AncientSiteAttackBrief,  // 26
            ReportModalClassifier.AncientSiteDefenceBrief, // 28
            ReportModalClassifier.GeoHavenAttackBrief,     // 0  — Batch-1 P2
            ReportModalClassifier.GeoAlienBaseBrief,       // 2
            ReportModalClassifier.GeoPhoenixBaseDefenseBrief,     // 11
            ReportModalClassifier.GeoPhoenixBaseInfestationBrief, // 20
            ReportModalClassifier.BehemothAttackBrief,     // 34
            ReportModalClassifier.InfestedHavenBrief,      // 36
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
    [InlineData(0, ReportModalVariant.ActiveMissionBrief)]  // GeoHavenAttackBrief
    [InlineData(2, ReportModalVariant.ActiveMissionBrief)]  // GeoAlienBaseBrief
    [InlineData(11, ReportModalVariant.ActiveMissionBrief)] // GeoPhoenixBaseDefenseBrief
    [InlineData(20, ReportModalVariant.ActiveMissionBrief)] // GeoPhoenixBaseInfestationBrief
    [InlineData(34, ReportModalVariant.ActiveMissionBrief)] // BehemothAttackBrief (fallback family)
    [InlineData(36, ReportModalVariant.ActiveMissionBrief)] // InfestedHavenBrief
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
    [InlineData(ReportModalVariant.ActiveMissionBrief, true)]
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
    [InlineData(0, false)]    // GeoHavenAttackBrief — blocking, must arm/broadcast synchronously
    [InlineData(2, false)]    // GeoAlienBaseBrief — blocking
    [InlineData(11, false)]   // GeoPhoenixBaseDefenseBrief — blocking
    [InlineData(20, false)]   // GeoPhoenixBaseInfestationBrief — blocking
    [InlineData(34, false)]   // BehemothAttackBrief — blocking
    [InlineData(36, false)]   // InfestedHavenBrief — blocking
    [InlineData(1, false)]    // non-whitelisted
    public void ShouldDeferHostBroadcast_OnlyResearch(int modalType, bool expected)
        => Assert.Equal(expected, ReportModalClassifier.ShouldDeferHostBroadcast(modalType));

    // ── ActiveMissionBrief rebuild-vs-degrade decision (Batch-1): the client binds its P1-mirrored
    // site.ActiveMission ONLY when the mirrored class faithfully binds the brief's ModalType — the same
    // class→modal mapping native GetMissionBriefModal uses. Everything else degrades to the notify-only
    // text prompt; the host intent gate is armed either way (spec P2 hard invariant). ──────────────────
    [Theory]
    // brief 0 (haven defense) ← HavenDefense only
    [InlineData(0, GeoMissionRecord.HavenDefense, true)]
    [InlineData(0, GeoMissionRecord.AlienBase, false)]
    [InlineData(0, GeoMissionRecord.PhoenixBaseDefense, false)]
    // brief 2 (alien base) ← BOTH alien-base classes (the 2-class bind ambiguity — discriminator decides)
    [InlineData(2, GeoMissionRecord.AlienBase, true)]
    [InlineData(2, GeoMissionRecord.AlienBaseAssault, true)]
    [InlineData(2, GeoMissionRecord.HavenDefense, false)]
    // brief 11 (base attack) ← PhoenixBaseDefense only
    [InlineData(11, GeoMissionRecord.PhoenixBaseDefense, true)]
    [InlineData(11, GeoMissionRecord.AlienBaseAssault, false)]
    // brief 20 / 36 (infestation family)
    [InlineData(20, GeoMissionRecord.PhoenixBaseInfestation, true)]
    [InlineData(20, GeoMissionRecord.InfestationCleanse, false)]
    [InlineData(36, GeoMissionRecord.InfestationCleanse, true)]
    [InlineData(36, GeoMissionRecord.PhoenixBaseInfestation, false)]
    // brief 34 (fallback family): host class is by definition unmapped → NEVER rebuilds, always degrades
    [InlineData(34, GeoMissionRecord.HavenDefense, false)]
    [InlineData(34, GeoMissionRecord.Unknown, false)]
    // unknown/tombstone classes never bind anything
    [InlineData(0, GeoMissionRecord.Unknown, false)]
    [InlineData(11, (byte)0, false)]
    // non-ActiveMissionBrief modals never route here
    [InlineData(15, GeoMissionRecord.Ambush, false)]
    [InlineData(4, GeoMissionRecord.Scavenging, false)]
    public void ActiveMissionRebuildMatches_ClassExactTable(int modalType, byte missionClass, bool expected)
        => Assert.Equal(expected, ReportModalClassifier.ActiveMissionRebuildMatches(modalType, missionClass));

    // ── degraded-notice decision: ONLY a FAILED ActiveMissionBrief rebuild surfaces the notify-only text
    // prompt. The pre-existing AmbushBrief/SiteMissionBrief variants keep their verified skip-show behavior
    // (no-regress pin for {15,4,26,28}); a successful rebuild never degrades. ──────────────────────────
    [Theory]
    [InlineData(ReportModalVariant.ActiveMissionBrief, false, true)]   // failed rebuild → show the notice
    [InlineData(ReportModalVariant.ActiveMissionBrief, true, false)]   // rebuilt → native brief, no notice
    [InlineData(ReportModalVariant.AmbushBrief, false, false)]         // pre-existing skip-show pinned
    [InlineData(ReportModalVariant.SiteMissionBrief, false, false)]    // pre-existing skip-show pinned
    [InlineData(ReportModalVariant.Research, false, false)]
    [InlineData(ReportModalVariant.NullData, false, false)]
    public void ShouldShowDegradedNotice_OnlyFailedActiveMissionRebuilds(
        ReportModalVariant variant, bool rebuilt, bool expected)
        => Assert.Equal(expected, ReportModalClassifier.ShouldShowDegradedNotice(variant, rebuilt));

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
