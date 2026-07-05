namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Host-side whitelist + variant map for the report-window mirror (Phase-A). PURE (Unity-free) so it links
    /// into the test project and is unit-tested directly. Decides whether a native <c>GeoscapeView</c> modal is a
    /// REPORT we can safely mirror host->client, and which client rebuild path (<see cref="ReportModalVariant"/>)
    /// it takes. Only modals the client can fully reconstruct from already-synced ids are whitelisted:
    ///   • GeoPhoenixBaseOutcome (6)   → NullData   (modalData null)
    ///   • GeoResearchComplete   (14)  → Research   (modalData GeoResearchCompleteData → researchID)
    ///   • GeoAmbushBrief        (15)  → AmbushBrief (modalData GeoAmbushMission → siteId + missionDef guid)
    ///   • PandoranRevealResult  (25)  → SiteOnly   (modalData GeoSite|null → siteId)
    ///   • DiplomacyResearchBrief(38)  → Diplomacy  (modalData DiplomacyResearchRewardData → faction+researches+level)
    ///   • GeoScavengeBrief      (4)   → SiteMissionBrief (modalData GeoScavengingMission → siteId + missionDef guid)
    ///   • AncientSiteAttackBrief(26)  → SiteMissionBrief (modalData GeoAncientSiteMission → siteId + missionDef guid)
    ///   • AncientSiteDefenceBrief(28) → SiteMissionBrief (modalData GeoAncientSiteMission → siteId + missionDef guid)
    ///   • GeoHavenAttackBrief   (0), GeoAlienBaseBrief (2), GeoPhoenixBaseDefenseBrief (11),
    ///     GeoPhoenixBaseInfestationBrief (20), BehemothAttackBrief (34), InfestedHavenBrief (36)
    ///                                 → ActiveMissionBrief (modalData = live GeoMission → siteId + missionDef
    ///                                   guid; the client binds its OWN site.ActiveMission, attached by the P1
    ///                                   mission-state mirror — Batch-1 of the 2026-07-05 popup-mirror spec)
    /// Everything else (interactive decisions, mission OUTCOME modals — Phase-B) is NOT whitelisted →
    /// the host never broadcasts it and the client never suppresses it (left fully native).
    ///
    /// SITE MISSION BRIEFS (supersedes the 5b2a5e1 "ambush-only" rule — deliberate, 2026-07-05): the persistent
    /// mission briefs all funnel through ONE opener (<c>ShowMissionBriefing</c> →
    /// <c>OpenModalPersistent(GetMissionBriefModal(mission), mission, 0)</c>, GeoscapeView.cs:1883-1903). The
    /// briefs whose modalData rebuilds FAITHFULLY from just (site, missionDef) — a pure field-assignment ctor and
    /// a bind that reads only Site/MissionDef-derived state — are now mirrored view-only + BLOCKING, released by
    /// the host's resolve (<c>ModalResultCallback</c> → ReportModalHide broadcast). Host CANCEL therefore closes
    /// the client copy explicitly — the 9e80b24 goal ("no lingering client prompt after a host cancel") is kept
    /// by the EXPLICIT hide instead of by exclusion. The 9e80b24 EVENT-rail deploy-prompt exclusion
    /// (EventReflection.IsMissionDeployEvent) is untouched — custom/KeepEncounterID missions ride events, not
    /// this modal rail. Mirrored briefs and why they qualify:
    ///   • GeoScavengeBrief 4        — GeoScavengingMission(site, def, params=null) (GeoScavengingMission.cs:23,
    ///     pure base-ctor); bind reads Site.IsInMist/RandomSeed + CommonMissionData only (ScavengeBriefDataBind).
    ///   • AncientSiteAttackBrief 26 / AncientSiteDefenceBrief 28 — GeoAncientSiteMission(site, def)
    ///     (GeoAncientSiteMission.cs:30, pure base-ctor); bind reads ArcheologySettings + Site.Type +
    ///     GetEnemyFaction (def-derived) only (AncientSiteBriefDataBind).
    /// MISSION-BRIEF FAMILY (Batch-1 P2 of the 2026-07-05 unified popup-mirror spec — supersedes the earlier
    /// "deliberately HOST-LOCAL" rule for these six): the LIVE→site-id briefs whose modalData is a live
    /// GeoMission with runtime state a fresh ctor can't supply are now mirrored via
    /// <see cref="ReportModalVariant.ActiveMissionBrief"/> — the P1 mission-state mirror (GeoSite channel #5,
    /// <c>GeoMissionRecord</c>) attaches the SAME mission subclass to the client's own site, and the client
    /// rebuild resolves <c>site.ActiveMission</c> (class-checked, <see cref="ActiveMissionRebuildMatches"/>):
    ///   • GeoHavenAttackBrief 0     — GeoHavenDefenseMission; attacker faction + deployments + attacked zone
    ///     ride the mission record (HavenDefenceBriefDataBind reads them off the attached mirror).
    ///   • GeoAlienBaseBrief 2       — ONE ModalType, TWO classes (GeoAlienBaseMission | GeoAlienBaseAssaultMission);
    ///     the record's class discriminator picks the exact class (the bind hard-casts — wrong class throws).
    ///   • GeoPhoenixBaseDefenseBrief 11 — attackingSites ride the record as site ids.
    ///   • GeoPhoenixBaseInfestationBrief 20 / InfestedHavenBrief 36 — binds read Site/MissionDef-derived state
    ///     off the attached mirror.
    ///   • BehemothAttackBrief 34    — the GetMissionBriefModal FALLBACK (GeoscapeView.cs:1751): the host class
    ///     is unmapped → the client never attaches it → the rebuild ALWAYS degrades (honest fallback).
    /// DEGRADED FALLBACK (spec P1/P2 invariant): a failed rebuild (dangling site id / class mismatch / 34)
    /// shows a notify-only native text prompt instead of the brief — and the HOST blocking gate is armed from
    /// the 0x69 SHOW regardless, so client intents can never race the host's pending decision.
    /// <see cref="IsBlockingModal"/> marks every mirrored brief BLOCKING: the host rejects client intents while
    /// it is pending (HostBlockingPromptGate) and its resolve broadcasts ReportModalHide. The CLIENT view-lock
    /// (inert buttons, no local close) applies only to the MANDATORY subset (<see cref="IsMandatoryBrief"/> —
    /// ambush 15, base defense 11, ancient defence 28: native semantics, PauseGame + IsMandatoryMission); an
    /// OPTIONAL brief's mirror stays locally dismissible (null DialogCallback → pure local pop; the armed host
    /// gate alone prevents racing the host's decision — 2026-07-05 soak: the wider Batch-1 lock made the
    /// scavenge brief's CLOSE dead on the client for as long as the host left its copy open).
    ///
    /// MISSION OUTCOME FAMILY (Batch-2 P3 of the 2026-07-05 unified popup-mirror spec): the post-tactical
    /// loot/result reports. Host opens them via the SAME OpenModal(Persistent) chokepoint — the post-tac rail
    /// (<c>UIStateInitial.cs:105-139</c>, <c>OpenModalPersistent(GetMissionOutcomeModal(lastMission), lastMission,
    /// int.MaxValue)</c>) and the cancel paths (<c>OnSiteMissionCancelled</c> GeoscapeView.cs:1930-1939: base-defense
    /// cancel → 12, ancient-defence cancel → 29, both prio int.MaxValue). Whitelisted outcome ids
    /// {1,3,5,12,16,21,27,29,35,37} → <see cref="ReportModalVariant.MissionOutcome"/>. NON-blocking by contract:
    /// outcomes are reports, not decisions — no HostBlockingPromptGate arm, no client view-lock, native local close.
    /// TOMBSTONE-ORDERING DECISION (spec Batch-2, documented choice): the outcome shows AFTER the mission ended,
    /// so the P1 site-channel mirror may already have TOMBSTONED <c>site.ActiveMission</c> when the 0x69 arrives.
    /// The MissionOutcome payload therefore carries EVERYTHING the rebuild needs IN THE MESSAGE ITSELF
    /// (missionClass discriminator + outcome state + reward display blob — the event-result-card pattern), and the
    /// client rebuilds a DISPLAY-ONLY mission via the same pure ctor map (never attached to the site, never
    /// SetActiveMission) — the rebuild is fully independent of the mission-record mirror's lifecycle.
    /// Rebuild-vs-skip is <see cref="OutcomeRebuildMatches"/> (mirrors the native <c>GetMissionOutcomeModal</c>
    /// class→modal mapping, GeoscapeView.cs:1800-1881); a failed rebuild skips the show (logged) — no degraded
    /// notice, because nothing blocks on it.
    /// INTERCEPTION FAMILY (WA-3 gap 5c, supersedes the earlier "33 deliberately excluded" rule — 2026-07-05):
    /// both interception modals are whitelisted as <see cref="ReportModalVariant.InterceptionNotice"/> — the
    /// client ALWAYS degrades to the notify-only text prompt because BOTH binds are decompile-verified
    /// unbuildable from synced ids (interception is a host-side minigame; its loot rides the silent wallet
    /// rail and the hull damage rides the WA-3 0xA6 HP tail):
    ///   • InterceptionBrief 32 — <c>GeoscapeView.ShowInterception</c> (:762) stamps
    ///     <c>InterceptionInfoData.CurrentPlayerAircraft/CurrentEnemyAircraft</c> (LIVE GeoVehicles) and opens
    ///     via <c>OpenModalPersistent(32, data)</c> → native <c>PauseGame = true</c> (:861). The bind
    ///     (InterceptionBriefDataBind:81) drives an INTERACTIVE aircraft-cycling input loop over the site's
    ///     live vehicle lists + equipment — no id-rebuild exists. BLOCKING: the host is paused deciding
    ///     (intercept / auto-resolve / disengage), so the intent gate arms like every optional blocking brief;
    ///     released natively via ModalResultCallback (:833 → InterceptionBriefCallback) / the Hide belt.
    ///     NON-mandatory: the client's notice is a locally-dismissible plain prompt.
    ///   • InterceptionOutcome 33 — <c>ShowInterceptionResult</c> (:781) opens <c>OpenModal(33, null,
    ///     GeoAirMission, int.MaxValue)</c>. <c>GeoAirMission</c> is NOT a GeoMission (standalone class,
    ///     GeoAirMission.cs:11) → outside the <see cref="GeoMissionRecord"/> ctor map, so it can NOT ride the
    ///     payload-carried MissionOutcome variant; its bind (InterceptionOutcomeDataBind.DisplayMissionData)
    ///     reads live <c>PlayerAircraft/EnemyAircraft.Vehicle</c> equipment panels + <c>PlayerAircraftCrew</c>
    ///     + <c>Reward</c> — unstampable. NON-blocking (a report): resolved-text notice, no gate, local close.
    /// DELIBERATE EXCLUSIONS (verified 2026-07-05):
    ///   • HavenInfiltrateOutcome 18 — the steal-mission classes are outside the P1 class map (no rebuild ctor path).
    ///   • BehemothAttackOutcome 35 IS whitelisted (suppresses a client-local ghost + keeps host authority) but its
    ///     host class is by definition unmapped → <see cref="OutcomeRebuildMatches"/> is always false → skip-show.
    ///
    /// The live modalData read (host) + reconstruct (client) lives in <see cref="ReportModalReflection"/>
    /// (reflection-bound, not unit-testable); this class is the pure boundary that drives it.
    /// </summary>
    public static class ReportModalClassifier
    {
        // ── ModalType enum values (PhoenixPoint.Common.Utils.ModalType, verified decompile 2026-06-26) ──
        public const int GeoHavenAttackBrief = 0;  // ModalType.cs:5 — haven-defense deploy brief (top user pain)
        public const int GeoAlienBaseBrief = 2;    // ModalType.cs:7 — alien-base brief (TWO classes, discriminator-bound)
        public const int GeoScavengeBrief = 4;     // ModalType.cs:9 — optional scavenge/resource-site deploy brief
        public const int GeoPhoenixBaseOutcome = 6;
        public const int GeoPhoenixBaseDefenseBrief = 11; // ModalType.cs:16 — BASE ATTACK brief (auto-opens, mandatory)
        public const int GeoResearchComplete = 14;
        public const int GeoAmbushBrief = 15;      // ModalType.cs:20 — mandatory ambush prompt (GeoscapeView.cs:1780)
        public const int GeoPhoenixBaseInfestationBrief = 20; // ModalType.cs:25 — infested phoenix base brief
        public const int PandoranRevealResult = 25;
        public const int AncientSiteAttackBrief = 26;  // ModalType.cs:31 — ancient-site attack deploy brief
        public const int AncientSiteDefenceBrief = 28; // ModalType.cs:33 — ancient-site defence deploy brief
        public const int BehemothAttackBrief = 34; // ModalType.cs:39 — GetMissionBriefModal FALLBACK (degrades)
        public const int InfestedHavenBrief = 36;  // ModalType.cs:41 — infested-haven cleanse brief
        public const int DiplomacyResearchBrief = 38;

        // ── mission OUTCOME modals (Batch-2 P3; all open persistent at prio int.MaxValue) ──
        public const int GeoHavenAttackOutcome = 1;            // ModalType.cs:6  — haven-defense loot report
        public const int GeoAlienBaseOutcome = 3;              // ModalType.cs:8  — alien-base result (both classes)
        public const int GeoScavengeOutcome = 5;               // ModalType.cs:10 — scavenge loot report
        public const int GeoPhoenixBaseDefenseOutcome = 12;    // ModalType.cs:17 — base-defense result (+ cancel path :1934)
        public const int GeoAmbushOutcome = 16;                // ModalType.cs:21 — ambush loot report
        public const int GeoPhoenixBaseInfestationOutcome = 21;// ModalType.cs:26 — infested-base result
        public const int AncientSiteAttackOutcome = 27;        // ModalType.cs:32 — ancient-site attack result
        public const int AncientSiteDefenceOutcome = 29;       // ModalType.cs:34 — ancient-site defence result (+ cancel :1938)
        public const int BehemothAttackOutcome = 35;           // ModalType.cs:40 — outcome FALLBACK family (never rebuilds)
        public const int InfestedHavenOutcome = 37;            // ModalType.cs:42 — infested-haven cleanse result

        // ── interception pair (WA-3 gap 5c; both ALWAYS notify-only on the client) ──
        public const int InterceptionBrief = 32;               // ModalType.cs:37 — live-aircraft decision brief (blocking)
        public const int InterceptionOutcome = 33;             // ModalType.cs:38 — air-battle result report (non-blocking)

        /// <summary>True iff <paramref name="modalType"/> (native ModalType enum value) is a whitelisted
        /// report modal. Takes the full int value (ModalType is int-backed: None=-1, _CustomMission=9999) so a
        /// non-report id can never alias a whitelisted one. PURE.</summary>
        public static bool IsReportModal(int modalType)
        {
            switch (modalType)
            {
                case GeoHavenAttackBrief:
                case GeoAlienBaseBrief:
                case GeoScavengeBrief:
                case GeoPhoenixBaseOutcome:
                case GeoPhoenixBaseDefenseBrief:
                case GeoResearchComplete:
                case GeoAmbushBrief:
                case GeoPhoenixBaseInfestationBrief:
                case PandoranRevealResult:
                case AncientSiteAttackBrief:
                case AncientSiteDefenceBrief:
                case BehemothAttackBrief:
                case InfestedHavenBrief:
                case DiplomacyResearchBrief:
                case GeoHavenAttackOutcome:
                case GeoAlienBaseOutcome:
                case GeoScavengeOutcome:
                case GeoPhoenixBaseDefenseOutcome:
                case GeoAmbushOutcome:
                case GeoPhoenixBaseInfestationOutcome:
                case AncientSiteAttackOutcome:
                case AncientSiteDefenceOutcome:
                case BehemothAttackOutcome:
                case InfestedHavenOutcome:
                case InterceptionBrief:
                case InterceptionOutcome:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The client rebuild path for a whitelisted modal; <see cref="ReportModalVariant.NullData"/>
        /// for a non-whitelisted one (callers gate on <see cref="IsReportModal"/> first). PURE.</summary>
        public static ReportModalVariant VariantFor(int modalType)
        {
            switch (modalType)
            {
                case PandoranRevealResult: return ReportModalVariant.SiteOnly;
                case GeoResearchComplete: return ReportModalVariant.Research;
                case DiplomacyResearchBrief: return ReportModalVariant.Diplomacy;
                case GeoAmbushBrief: return ReportModalVariant.AmbushBrief;
                case GeoScavengeBrief:
                case AncientSiteAttackBrief:
                case AncientSiteDefenceBrief:
                    return ReportModalVariant.SiteMissionBrief;
                case GeoHavenAttackBrief:
                case GeoAlienBaseBrief:
                case GeoPhoenixBaseDefenseBrief:
                case GeoPhoenixBaseInfestationBrief:
                case BehemothAttackBrief:
                case InfestedHavenBrief:
                    return ReportModalVariant.ActiveMissionBrief;
                case GeoHavenAttackOutcome:
                case GeoAlienBaseOutcome:
                case GeoScavengeOutcome:
                case GeoPhoenixBaseDefenseOutcome:
                case GeoAmbushOutcome:
                case GeoPhoenixBaseInfestationOutcome:
                case AncientSiteAttackOutcome:
                case AncientSiteDefenceOutcome:
                case BehemothAttackOutcome:
                case InfestedHavenOutcome:
                    return ReportModalVariant.MissionOutcome;
                case InterceptionBrief:
                case InterceptionOutcome:
                    return ReportModalVariant.InterceptionNotice;
                case GeoPhoenixBaseOutcome:
                default:
                    return ReportModalVariant.NullData;
            }
        }

        /// <summary>NullData/SiteOnly/AmbushBrief/SiteMissionBrief/ActiveMissionBrief/MissionOutcome modals are
        /// opened PERSISTENT natively (OpenModalPersistent — every mission brief via ShowMissionBriefing,
        /// GeoscapeView.cs:1903; every whitelisted outcome via the post-tac rail UIStateInitial.cs:112 and the
        /// cancel paths :1934/:1938); Research/Diplomacy via OpenModal (non-persistent). The client replays with
        /// the same persistence. InterceptionNotice never replays a native modal at all (the client shows the
        /// notify-only prompt) → false. PURE.</summary>
        public static bool IsPersistent(ReportModalVariant variant)
            => variant == ReportModalVariant.NullData || variant == ReportModalVariant.SiteOnly
               || variant == ReportModalVariant.AmbushBrief || variant == ReportModalVariant.SiteMissionBrief
               || variant == ReportModalVariant.ActiveMissionBrief || variant == ReportModalVariant.MissionOutcome;

        /// <summary>
        /// The interception-notice TEXT decision: true iff <paramref name="modalType"/> is the PENDING-decision
        /// half of the pair (InterceptionBrief 32 — host paused choosing intercept/disengage, gate armed;
        /// notice says actions are paused) vs the resolved report (InterceptionOutcome 33 — notice says the
        /// air battle finished; nothing blocks). PURE.
        /// </summary>
        public static bool InterceptionNoticeIsPending(int modalType) => modalType == InterceptionBrief;

        /// <summary>
        /// True iff the HOST must defer this report's payload build + broadcast to the NEXT engine tick instead
        /// of reading it inside the <c>OpenModal</c> Postfix. The Research variant's payload carries the native
        /// "new research available" line visibility (<c>ResearchElement.UnlocksResearches</c>) — but the popup is
        /// queued from INSIDE the <c>Research.OnResearchCompleted</c> multicast dispatch
        /// (GeoscapeView.OnFactionResearchCompleted → OpenModal, GeoscapeView.cs:1980-1992), and the dependent
        /// elements' Revealed/Unlocked flips happen in OTHER subscribers of the SAME event
        /// (ExistingResearchRequirement.OnResearchCompleted → UpdateProgress) that run AFTER the opener — so a
        /// Postfix-time read sees stale states and ships NavHidden for a research that genuinely unlocks
        /// (soak 2026-07-05: host shipped shareLevel=1 for every completion; client force-hid a line the host's
        /// own bind — which runs on a LATER view-update frame — showed). One tick later the cascade (same call
        /// stack) has fully settled, matching what the host's native bind renders. Blocking briefs must stay
        /// synchronous (their intent gate arms in the Postfix); every other variant has no read-timing hazard. PURE.
        /// </summary>
        public static bool ShouldDeferHostBroadcast(int modalType)
            => VariantFor(modalType) == ReportModalVariant.Research;

        /// <summary>
        /// True iff <paramref name="modalType"/> is a BLOCKING host prompt: a window during which the host's own
        /// geoscape is modal-locked and the co-op session must not advance under it. Drives the HOST end of the
        /// lock — the host rejects every in-flight client intent while it is pending (HostBlockingPromptGate in
        /// SyncEngine.OnActionRequest) and broadcasts ReportModalHide on resolve so a still-open client mirror
        /// closes. = the mandatory ambush brief + every mirrored MISSION BRIEF — the SiteMissionBrief family
        /// {4,26,28} and the ActiveMissionBrief family {0,2,11,20,34,36} (host Confirm → tactical co-op
        /// deploy flow; host Cancel → ReportModalHide closes the client copy, normal flow resumes). The gate
        /// arms from the 0x69 SHOW itself, NOT from a successful client rebuild — a degraded notify-only
        /// fallback still leaves the host rejecting intents (spec P2 hard invariant). The CLIENT view-lock is
        /// the NARROWER <see cref="IsMandatoryBrief"/> (soak 2026-07-05 dead-CLOSE fix). WA-3: the
        /// InterceptionBrief 32 joins the blocking set — its native open is PauseGame=true
        /// (OpenModalPersistent, GeoscapeView.cs:861) with the host deciding intercept/disengage; its resolve
        /// funnels through the SAME ModalResultCallback (:833) → the existing release + hide rails apply.
        /// InterceptionOutcome 33 is a report — never blocking. PURE.
        /// </summary>
        public static bool IsBlockingModal(int modalType)
        {
            switch (modalType)
            {
                case GeoAmbushBrief:
                case GeoScavengeBrief:
                case AncientSiteAttackBrief:
                case AncientSiteDefenceBrief:
                case GeoHavenAttackBrief:
                case GeoAlienBaseBrief:
                case GeoPhoenixBaseDefenseBrief:
                case GeoPhoenixBaseInfestationBrief:
                case BehemothAttackBrief:
                case InfestedHavenBrief:
                case InterceptionBrief:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True iff the CLIENT's mirrored copy of <paramref name="modalType"/> must be VIEW-LOCKED (inert
        /// buttons, no local close — BlockingModalClientLockPatches): ONLY the MANDATORY briefs, whose native
        /// window is unskippable single-player-wise (MissionDef.MandatoryMission → PauseGame + no decline;
        /// GeoscapeView.ShowMissionBriefing :1899): ambush 15, phoenix-base defense 11, ancient-site defence 28.
        /// The OPTIONAL mirrored briefs (scavenge 4, ancient attack 26, ActiveMissionBrief family 0/2/20/34/36)
        /// natively HAVE a working CLOSE — the client copy keeps it: closing is a pure local dismiss (the
        /// mirror's DialogCallback is null — UIStateGeoModal.FinishDialog :86 just pops the state) and can
        /// never race the host's pending decision because <see cref="IsBlockingModal"/> keeps the HOST intent
        /// gate armed for ALL blocking briefs until the host resolves. Fix for the 2026-07-05 soak bug: the
        /// Batch-1 lock covered every blocking brief, so the scavenge brief's CLOSE was dead on the client for
        /// as long as the host left its copy open. PURE.
        /// </summary>
        public static bool IsMandatoryBrief(int modalType)
        {
            switch (modalType)
            {
                case GeoAmbushBrief:
                case GeoPhoenixBaseDefenseBrief:
                case AncientSiteDefenceBrief:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The ActiveMissionBrief rebuild-vs-degrade decision: true iff a mission of <paramref name="missionClass"/>
        /// (a <see cref="GeoMissionRecord"/> discriminator, as classified off the client's mirrored
        /// <c>site.ActiveMission</c>) faithfully binds the brief <paramref name="modalType"/> — the same class →
        /// modal mapping the native <c>GetMissionBriefModal</c> uses (GeoscapeView.cs:1724-1798). A mismatch
        /// (stale mirror / dangling site / the fallback-34 family, whose host class is by definition unmapped)
        /// → the caller degrades to the notify-only text prompt. GeoAlienBaseBrief 2 accepts BOTH alien-base
        /// classes (the bind casts per class, and the attached mirror IS the exact class). PURE.
        /// </summary>
        public static bool ActiveMissionRebuildMatches(int modalType, byte missionClass)
        {
            switch (modalType)
            {
                case GeoHavenAttackBrief: return missionClass == GeoMissionRecord.HavenDefense;
                case GeoAlienBaseBrief:
                    return missionClass == GeoMissionRecord.AlienBase
                        || missionClass == GeoMissionRecord.AlienBaseAssault;
                case GeoPhoenixBaseDefenseBrief: return missionClass == GeoMissionRecord.PhoenixBaseDefense;
                case GeoPhoenixBaseInfestationBrief: return missionClass == GeoMissionRecord.PhoenixBaseInfestation;
                case InfestedHavenBrief: return missionClass == GeoMissionRecord.InfestationCleanse;
                case BehemothAttackBrief: return false;   // fallback family: unmapped host class → always degrade
                default: return false;
            }
        }

        /// <summary>
        /// True iff a FAILED client rebuild of <paramref name="variant"/> must surface the DEGRADED notify-only
        /// text prompt instead of silently skipping the show. Only the ActiveMissionBrief family degrades
        /// visibly (spec Batch-1): the pre-existing AmbushBrief/SiteMissionBrief variants keep their verified
        /// skip-show behavior (no-regress pin for {15,4,26,28}); a SUCCESSFUL rebuild never degrades. The host
        /// intent gate is armed independently of this decision. PURE.
        /// </summary>
        public static bool ShouldShowDegradedNotice(ReportModalVariant variant, bool rebuildSucceeded)
            => !rebuildSucceeded && variant == ReportModalVariant.ActiveMissionBrief;

        /// <summary>
        /// The MissionOutcome rebuild-vs-skip decision: true iff a display-only mission of
        /// <paramref name="missionClass"/> (a <see cref="GeoMissionRecord"/> discriminator, carried IN the 0x69
        /// outcome payload — never read off the tombstonable site mirror) faithfully binds the outcome
        /// <paramref name="modalType"/> — the same class → modal mapping the native <c>GetMissionOutcomeModal</c>
        /// uses (GeoscapeView.cs:1800-1881). GeoAlienBaseOutcome 3 accepts BOTH alien-base classes (the bind
        /// branches per class); AncientSite attack 27 / defence 29 both bind the ONE GeoAncientSiteMission class
        /// (the native split is participant-data-derived — the host's modal id on the wire IS the split).
        /// BehemothAttackOutcome 35 is the outcome FALLBACK family (unmapped host class) → always false →
        /// the caller skips the show (non-blocking report; nothing waits on it). PURE.
        /// </summary>
        public static bool OutcomeRebuildMatches(int modalType, byte missionClass)
        {
            switch (modalType)
            {
                case GeoHavenAttackOutcome: return missionClass == GeoMissionRecord.HavenDefense;
                case GeoAlienBaseOutcome:
                    return missionClass == GeoMissionRecord.AlienBase
                        || missionClass == GeoMissionRecord.AlienBaseAssault;
                case GeoScavengeOutcome: return missionClass == GeoMissionRecord.Scavenging;
                case GeoPhoenixBaseDefenseOutcome: return missionClass == GeoMissionRecord.PhoenixBaseDefense;
                case GeoAmbushOutcome: return missionClass == GeoMissionRecord.Ambush;
                case GeoPhoenixBaseInfestationOutcome: return missionClass == GeoMissionRecord.PhoenixBaseInfestation;
                case AncientSiteAttackOutcome:
                case AncientSiteDefenceOutcome:
                    return missionClass == GeoMissionRecord.AncientSite;
                case InfestedHavenOutcome: return missionClass == GeoMissionRecord.InfestationCleanse;
                case BehemothAttackOutcome: return false;   // fallback family: unmapped host class → always skip
                default: return false;
            }
        }
    }
}
