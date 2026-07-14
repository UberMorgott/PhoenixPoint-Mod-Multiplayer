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

    // ── Batch-2 P3: the whitelisted mission OUTCOME modals (post-tac rail UIStateInitial.cs:105-139 at prio
    // int.MaxValue + the cancel paths GeoscapeView.cs:1934/:1938 — all through the same OpenModalPersistent
    // chokepoint). NON-blocking reports: payload-carried rebuild, native local close. ──
    [Theory]
    [InlineData(1)]    // GeoHavenAttackOutcome
    [InlineData(3)]    // GeoAlienBaseOutcome
    [InlineData(5)]    // GeoScavengeOutcome
    [InlineData(12)]   // GeoPhoenixBaseDefenseOutcome (+ base-defense cancel path)
    [InlineData(16)]   // GeoAmbushOutcome
    [InlineData(21)]   // GeoPhoenixBaseInfestationOutcome
    [InlineData(27)]   // AncientSiteAttackOutcome
    [InlineData(29)]   // AncientSiteDefenceOutcome (+ ancient-defence cancel path)
    [InlineData(35)]   // BehemothAttackOutcome (fallback family — mirrors; rebuild always skips)
    [InlineData(37)]   // InfestedHavenOutcome
    public void IsReportModal_WhitelistedOutcomes_True(int modalType)
        => Assert.True(ReportModalClassifier.IsReportModal(modalType));

    // ── WA-3 gap 5c: the interception pair mirrors as an ALWAYS-notify-only variant (live-aircraft binds —
    // 32's InterceptionInfoData interactive brief, 33's GeoAirMission which is NOT even a GeoMission — are
    // decompile-verified unbuildable client-side; whitelisting also suppresses any client-local ghost). ──
    [Theory]
    [InlineData(32)]   // InterceptionBrief — pending-decision notice, blocking (host PauseGame=true at open)
    [InlineData(33)]   // InterceptionOutcome — resolved notice, non-blocking report
    public void IsReportModal_InterceptionPair_True(int modalType)
        => Assert.True(ReportModalClassifier.IsReportModal(modalType));

    // ── gap AC (verify-first close 2026-07-07): AlienResearchBrief 23 mirrors as an ALWAYS-notify-only
    // variant. Verified in refs/TFTV-src: TFTV does NOT suppress the vanilla GeoAlienFaction.UpdateResearch →
    // OnNewIntelligence → modal-23 path (its UpdateResearch patch is commented out; the ACTIVE
    // AlienResearchQueueSeeder patch keeps pandoran research running), and TFTV's own SDI/ODI intel rides the
    // already-mirrored 0x65 event rail (EventSystem.TriggerGeoscapeEvent) — so the vanilla modal still fires
    // under TFTV and needed the 0x69 whitelist. The bind is unbuildable client-side (live
    // GeoscapeViewContext.Input + live alien ResearchElements → 3D mutation carousel). ──
    [Fact]
    public void IsReportModal_AlienResearchBrief_True()
        => Assert.True(ReportModalClassifier.IsReportModal(ReportModalClassifier.AlienResearchBrief));

    [Theory]
    [InlineData(7)]    // LoadPrompt
    [InlineData(8)]    // SiteEncounter (encounter modal — event-adjacent; MUST stay out of the report channel, S3)
    [InlineData(13)]   // DualClassPicker (decision)
    [InlineData(17)]   // HavenInfiltrateBrief (steal-mission family — outside the P1 class map)
    [InlineData(18)]   // HavenInfiltrateOutcome (steal-mission family — deliberately excluded, Batch-2)
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
        {
            0, 2, 4, 6, 11, 14, 15, 20, 25, 26, 28, 34, 36, 38,       // Phase-A reports + mirrored briefs
            1, 3, 5, 12, 16, 21, 27, 29, 35, 37,                       // Batch-2 P3 mission outcomes
            32, 33,                                                    // WA-3 interception pair (notify-only)
            23,                                                        // gap AC alien intel report (notify-only)
        };
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
            ReportModalClassifier.InterceptionBrief,       // 32 — WA-3: host paused deciding intercept/disengage
        };
        foreach (var modalType in EnumerateModalTypeValues())
            Assert.Equal(blocking.Contains(modalType), ReportModalClassifier.IsBlockingModal(modalType));
    }

    // ── client view-lock classification: ONLY the mandatory briefs (native PauseGame + IsMandatoryMission —
    // no native decline) lock the client's mirror. Every OPTIONAL blocking brief (scavenge 4, ancient attack
    // 26, ActiveMissionBrief family) keeps its native CLOSE on the client (pure local dismiss; the HOST intent
    // gate — armed for ALL blocking briefs — prevents racing). 2026-07-05 soak fix: the wider lock left the
    // scavenge brief's CLOSE dead on the client while the host's copy stayed open.
    [Fact]
    public void IsMandatoryBrief_OnlyAmbushBaseDefenseAncientDefence_AcrossEntireModalTypeEnum()
    {
        var mandatory = new System.Collections.Generic.HashSet<int>
        {
            ReportModalClassifier.GeoAmbushBrief,             // 15
            ReportModalClassifier.GeoPhoenixBaseDefenseBrief, // 11
            ReportModalClassifier.AncientSiteDefenceBrief,    // 28
        };
        foreach (var modalType in EnumerateModalTypeValues())
            Assert.Equal(mandatory.Contains(modalType), ReportModalClassifier.IsMandatoryBrief(modalType));
    }

    [Fact]
    public void EveryMandatoryBrief_IsAlsoBlocking()
    {
        // The client lock (IsMandatoryBrief) must never cover a window the host gate + hide-release rails
        // (IsBlockingModal) do not — a locked mirror without a host ReportModalHide would never release.
        foreach (var modalType in EnumerateModalTypeValues())
            if (ReportModalClassifier.IsMandatoryBrief(modalType))
                Assert.True(ReportModalClassifier.IsBlockingModal(modalType));
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

    // ── vehicle-arrival brief classification (co-op brief-on-all fix): a mission brief that opens when a
    // vehicle ARRIVES at its site is player-initiated deploy UI, mirrored to the initiating peer only. =
    // IsMissionBrief MINUS the ambush 15 (a mid-travel encounter, not an arrival at a chosen site). The
    // set is exactly the SiteMissionBrief family {4,26,28} + the ActiveMissionBrief family {0,2,11,20,34,36}.
    [Fact]
    public void IsVehicleArrivalBrief_OnlyTheArrivalBriefFamilies_AcrossEntireModalTypeEnum()
    {
        var arrival = new System.Collections.Generic.HashSet<int>
        {
            ReportModalClassifier.GeoScavengeBrief,        // 4
            ReportModalClassifier.AncientSiteAttackBrief,  // 26
            ReportModalClassifier.AncientSiteDefenceBrief, // 28
            ReportModalClassifier.GeoHavenAttackBrief,     // 0
            ReportModalClassifier.GeoAlienBaseBrief,       // 2
            ReportModalClassifier.GeoPhoenixBaseDefenseBrief,     // 11
            ReportModalClassifier.GeoPhoenixBaseInfestationBrief, // 20
            ReportModalClassifier.BehemothAttackBrief,     // 34
            ReportModalClassifier.InfestedHavenBrief,      // 36
        };
        foreach (var modalType in EnumerateModalTypeValues())
            Assert.Equal(arrival.Contains(modalType), ReportModalClassifier.IsVehicleArrivalBrief(modalType));
    }

    [Fact]
    public void IsVehicleArrivalBrief_ExcludesAmbush_ButAmbushStaysBlocking()
    {
        // Ambush 15 is blocking + mirrored, but NOT an arrival-at-a-chosen-site brief → it keeps broadcast-to-all
        // (no destination tag), so it must be excluded from the initiator-routed set.
        Assert.False(ReportModalClassifier.IsVehicleArrivalBrief(ReportModalClassifier.GeoAmbushBrief));
        Assert.True(ReportModalClassifier.IsBlockingModal(ReportModalClassifier.GeoAmbushBrief));
    }

    [Fact]
    public void EveryVehicleArrivalBrief_IsAlsoBlockingAndWhitelisted()
    {
        // The routed set must stay a subset of the mirrored blocking briefs — routing a non-blocking / unmirrored
        // modal to one peer would hide a window the rest of the session should see.
        foreach (var modalType in EnumerateModalTypeValues())
            if (ReportModalClassifier.IsVehicleArrivalBrief(modalType))
            {
                Assert.True(ReportModalClassifier.IsBlockingModal(modalType));
                Assert.True(ReportModalClassifier.IsReportModal(modalType));
            }
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
    [InlineData(1, ReportModalVariant.MissionOutcome)]   // GeoHavenAttackOutcome (Batch-2 P3)
    [InlineData(3, ReportModalVariant.MissionOutcome)]   // GeoAlienBaseOutcome
    [InlineData(5, ReportModalVariant.MissionOutcome)]   // GeoScavengeOutcome
    [InlineData(12, ReportModalVariant.MissionOutcome)]  // GeoPhoenixBaseDefenseOutcome
    [InlineData(16, ReportModalVariant.MissionOutcome)]  // GeoAmbushOutcome
    [InlineData(21, ReportModalVariant.MissionOutcome)]  // GeoPhoenixBaseInfestationOutcome
    [InlineData(27, ReportModalVariant.MissionOutcome)]  // AncientSiteAttackOutcome
    [InlineData(29, ReportModalVariant.MissionOutcome)]  // AncientSiteDefenceOutcome
    [InlineData(35, ReportModalVariant.MissionOutcome)]  // BehemothAttackOutcome (fallback — never rebuilds)
    [InlineData(37, ReportModalVariant.MissionOutcome)]  // InfestedHavenOutcome
    [InlineData(32, ReportModalVariant.InterceptionNotice)] // InterceptionBrief (WA-3 — always notify-only)
    [InlineData(33, ReportModalVariant.InterceptionNotice)] // InterceptionOutcome (WA-3 — always notify-only)
    [InlineData(23, ReportModalVariant.IntelNotice)]        // AlienResearchBrief (gap AC — always notify-only)
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
    [InlineData(ReportModalVariant.MissionOutcome, true)]   // post-tac rail + cancel paths: OpenModalPersistent
    [InlineData(ReportModalVariant.InterceptionNotice, false)] // never replays a native modal (notify-only prompt)
    [InlineData(ReportModalVariant.IntelNotice, false)]        // never replays a native modal (notify-only prompt)
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
    [InlineData(1, false)]    // GeoHavenAttackOutcome — outcome payload reads settled post-tac state, no defer
    [InlineData(5, false)]    // GeoScavengeOutcome
    [InlineData(12, false)]   // GeoPhoenixBaseDefenseOutcome
    [InlineData(32, false)]   // InterceptionBrief — blocking, must arm/broadcast synchronously (payload reads nothing)
    [InlineData(33, false)]   // InterceptionOutcome — payload reads nothing, no read-timing hazard
    [InlineData(23, false)]   // AlienResearchBrief — payload reads nothing (zero reflection), no read-timing hazard
    public void ShouldDeferHostBroadcast_OnlyResearch(int modalType, bool expected)
        => Assert.Equal(expected, ReportModalClassifier.ShouldDeferHostBroadcast(modalType));

    // ── WA-3 interception-notice text decision: 32 = pending (host paused deciding; gate armed),
    // 33 = resolved report (nothing blocks). ──────────────────────────────────────────────────────
    [Fact]
    public void InterceptionNoticeIsPending_OnlyTheBrief()
    {
        Assert.True(ReportModalClassifier.InterceptionNoticeIsPending(ReportModalClassifier.InterceptionBrief));
        Assert.False(ReportModalClassifier.InterceptionNoticeIsPending(ReportModalClassifier.InterceptionOutcome));
        // The pending half must be exactly the blocking half — a pending notice without an armed host gate
        // (or vice versa) would lie to the user about whether actions are paused.
        foreach (var modalType in new[] { ReportModalClassifier.InterceptionBrief, ReportModalClassifier.InterceptionOutcome })
            Assert.Equal(ReportModalClassifier.InterceptionNoticeIsPending(modalType),
                         ReportModalClassifier.IsBlockingModal(modalType));
    }

    [Fact]
    public void InterceptionPair_NeverMandatory_NeverDegradedBriefNotice()
    {
        // Not mandatory: the client's notice is a locally dismissible plain prompt (no view-lock machinery).
        Assert.False(ReportModalClassifier.IsMandatoryBrief(ReportModalClassifier.InterceptionBrief));
        Assert.False(ReportModalClassifier.IsMandatoryBrief(ReportModalClassifier.InterceptionOutcome));
        // The ActiveMissionBrief degrade decision must NOT claim the interception variant — it has its own
        // always-notify path (ShowInterceptionNotice), pinned here so the two degrade rails stay distinct.
        Assert.False(ReportModalClassifier.ShouldShowDegradedNotice(ReportModalVariant.InterceptionNotice, rebuildSucceeded: false));
    }

    // ── gap AC: the intel notice is a pure REPORT — never blocking (native OpenModal, no PauseGame), never
    // mandatory (no view-lock machinery), and never claimed by the ActiveMissionBrief degrade rail (it has
    // its own always-notify path, ShowIntelReportNotice). Pinned so the notify-only contract stays intact. ──
    [Fact]
    public void IntelNotice_NonBlocking_NeverMandatory_NeverDegradedBriefNotice()
    {
        Assert.False(ReportModalClassifier.IsBlockingModal(ReportModalClassifier.AlienResearchBrief));
        Assert.False(ReportModalClassifier.IsMandatoryBrief(ReportModalClassifier.AlienResearchBrief));
        Assert.False(ReportModalClassifier.ShouldShowDegradedNotice(ReportModalVariant.IntelNotice, rebuildSucceeded: false));
    }

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

    // ── Batch-2 P3: MissionOutcome rebuild-vs-skip decision — the same class→modal mapping the native
    // GetMissionOutcomeModal uses (GeoscapeView.cs:1800-1881). The class rides the 0x69 PAYLOAD (never
    // site.ActiveMission — the P1 mirror may already be tombstoned when the outcome shows). ──
    [Theory]
    // outcome 1 (haven attack) ← HavenDefense only
    [InlineData(1, GeoMissionRecord.HavenDefense, true)]
    [InlineData(1, GeoMissionRecord.Scavenging, false)]
    // outcome 3 (alien base) ← BOTH alien-base classes (the bind branches per class)
    [InlineData(3, GeoMissionRecord.AlienBase, true)]
    [InlineData(3, GeoMissionRecord.AlienBaseAssault, true)]
    [InlineData(3, GeoMissionRecord.HavenDefense, false)]
    // outcome 5 (scavenge) ← Scavenging only
    [InlineData(5, GeoMissionRecord.Scavenging, true)]
    [InlineData(5, GeoMissionRecord.Ambush, false)]
    // outcome 12 (base defense, incl. the cancel path) ← PhoenixBaseDefense only
    [InlineData(12, GeoMissionRecord.PhoenixBaseDefense, true)]
    [InlineData(12, GeoMissionRecord.PhoenixBaseInfestation, false)]
    // outcome 16 (ambush) ← Ambush only
    [InlineData(16, GeoMissionRecord.Ambush, true)]
    [InlineData(16, GeoMissionRecord.Scavenging, false)]
    // outcome 21 (base infestation) ← PhoenixBaseInfestation only
    [InlineData(21, GeoMissionRecord.PhoenixBaseInfestation, true)]
    [InlineData(21, GeoMissionRecord.InfestationCleanse, false)]
    // outcomes 27/29 (ancient attack/defence) ← the ONE GeoAncientSiteMission class binds BOTH
    // (the native attack/defence split is participant-derived — the host's modal id on the wire IS the split)
    [InlineData(27, GeoMissionRecord.AncientSite, true)]
    [InlineData(29, GeoMissionRecord.AncientSite, true)]
    [InlineData(27, GeoMissionRecord.Scavenging, false)]
    [InlineData(29, GeoMissionRecord.HavenDefense, false)]
    // outcome 35 (fallback family): host class is by definition unmapped → NEVER rebuilds → skip-show
    [InlineData(35, GeoMissionRecord.HavenDefense, false)]
    [InlineData(35, GeoMissionRecord.Unknown, false)]
    // outcome 37 (infested haven) ← InfestationCleanse only
    [InlineData(37, GeoMissionRecord.InfestationCleanse, true)]
    [InlineData(37, GeoMissionRecord.PhoenixBaseInfestation, false)]
    // unknown/tombstone classes never bind; brief ids never route here
    [InlineData(1, GeoMissionRecord.Unknown, false)]
    [InlineData(12, (byte)0, false)]
    [InlineData(11, GeoMissionRecord.PhoenixBaseDefense, false)]  // brief id ≠ outcome id
    [InlineData(4, GeoMissionRecord.Scavenging, false)]
    public void OutcomeRebuildMatches_ClassExactTable(int modalType, byte missionClass, bool expected)
        => Assert.Equal(expected, ReportModalClassifier.OutcomeRebuildMatches(modalType, missionClass));

    // ── tombstone-vs-outcome ORDERING decision pin (Batch-2, documented choice): the outcome mirror is
    // PAYLOAD-CARRIED — its variant is MissionOutcome (self-sufficient rebuild off the 0x69 fields), never
    // ActiveMissionBrief (which binds the tombstonable P1 site mirror). If someone remaps an outcome id onto
    // the ActiveMissionBrief path, a post-tactical tombstone race would silently eat loot reports. ──
    [Fact]
    public void Outcomes_ArePayloadCarried_NeverActiveMissionMirrorBound()
    {
        foreach (var id in new[] { 1, 3, 5, 12, 16, 21, 27, 29, 35, 37 })
        {
            Assert.Equal(ReportModalVariant.MissionOutcome, ReportModalClassifier.VariantFor(id));
            Assert.NotEqual(ReportModalVariant.ActiveMissionBrief, ReportModalClassifier.VariantFor(id));
            // Non-blocking by contract: outcomes are reports, not decisions — no gate, no view-lock.
            Assert.False(ReportModalClassifier.IsBlockingModal(id));
            Assert.False(ReportModalClassifier.IsMandatoryBrief(id));
        }
    }

    // ── IsMissionBrief: the begin-mission relay whitelist (blocking briefs MINUS the interception brief) ──
    [Fact]
    public void IsMissionBrief_BlockingBriefsMinusInterception_AcrossEntireModalTypeEnum()
    {
        var expected = new[] { 0, 2, 4, 11, 15, 20, 26, 28, 34, 36 };   // SiteMissionBrief + ActiveMissionBrief families
        for (int id = -1; id <= 40; id++)
            Assert.Equal(System.Array.IndexOf(expected, id) >= 0, ReportModalClassifier.IsMissionBrief(id));
        Assert.False(ReportModalClassifier.IsMissionBrief(9999));   // _CustomMission rides the EVENT rail, not this relay
    }

    [Fact]
    public void IsMissionBrief_InterceptionBrief_NeverRelays()
    {
        // Blocking for the hide/notice rails, but its confirm launches the air-combat minigame, not a ground
        // mission — and the client mirror is a notify-only text prompt (no UIStateGeoModal to click anyway).
        Assert.True(ReportModalClassifier.IsBlockingModal(ReportModalClassifier.InterceptionBrief));
        Assert.False(ReportModalClassifier.IsMissionBrief(ReportModalClassifier.InterceptionBrief));
    }
}
