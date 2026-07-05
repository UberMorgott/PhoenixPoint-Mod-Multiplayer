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
    /// Deliberately still HOST-LOCAL (cannot be rebuilt faithfully from synced ids — do NOT add without a new
    /// rebuild path):
    ///   • GeoHavenAttackBrief 0     — GeoHavenDefenseMission ctor needs runtime (HavenAttacker, attackZone);
    ///     bind reads live deployment points / leader / zone RNG (HavenDefenceBriefDataBind).
    ///   • GeoAlienBaseBrief 2       — ONE ModalType, TWO classes (GeoAlienBaseMission | GeoAlienBaseAssaultMission);
    ///     the assault ctor needs runtime deployments and the bind hard-casts per class → a wrong-class rebuild throws.
    ///   • GeoPhoenixBaseDefenseBrief 11 — ctor needs runtime attackingSites.
    ///   • GeoPhoenixBaseInfestationBrief 20 / InfestedHavenBrief 36 — binds read live haven/deployment state.
    ///   • BehemothAttackBrief 34    — fallback for otherwise-unmapped mission classes; no single rebuild ctor.
    /// <see cref="IsBlockingModal"/> marks every mirrored brief BLOCKING: the client's mirror is view-locked
    /// (inert buttons, no local close) and the host rejects client intents while it is pending
    /// (HostBlockingPromptGate) — for the mandatory ambush that is native semantics; for the optional briefs it
    /// mirrors the host's own modal-lock (the host player is deciding; a client action would race the decision).
    ///
    /// The live modalData read (host) + reconstruct (client) lives in <see cref="ReportModalReflection"/>
    /// (reflection-bound, not unit-testable); this class is the pure boundary that drives it.
    /// </summary>
    public static class ReportModalClassifier
    {
        // ── ModalType enum values (PhoenixPoint.Common.Utils.ModalType, verified decompile 2026-06-26) ──
        public const int GeoScavengeBrief = 4;     // ModalType.cs:9 — optional scavenge/resource-site deploy brief
        public const int GeoPhoenixBaseOutcome = 6;
        public const int GeoResearchComplete = 14;
        public const int GeoAmbushBrief = 15;      // ModalType.cs:20 — mandatory ambush prompt (GeoscapeView.cs:1780)
        public const int PandoranRevealResult = 25;
        public const int AncientSiteAttackBrief = 26;  // ModalType.cs:31 — ancient-site attack deploy brief
        public const int AncientSiteDefenceBrief = 28; // ModalType.cs:33 — ancient-site defence deploy brief
        public const int DiplomacyResearchBrief = 38;

        /// <summary>True iff <paramref name="modalType"/> (native ModalType enum value) is a whitelisted
        /// report modal. Takes the full int value (ModalType is int-backed: None=-1, _CustomMission=9999) so a
        /// non-report id can never alias a whitelisted one. PURE.</summary>
        public static bool IsReportModal(int modalType)
        {
            switch (modalType)
            {
                case GeoScavengeBrief:
                case GeoPhoenixBaseOutcome:
                case GeoResearchComplete:
                case GeoAmbushBrief:
                case PandoranRevealResult:
                case AncientSiteAttackBrief:
                case AncientSiteDefenceBrief:
                case DiplomacyResearchBrief:
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
                case GeoPhoenixBaseOutcome:
                default:
                    return ReportModalVariant.NullData;
            }
        }

        /// <summary>NullData/SiteOnly/AmbushBrief/SiteMissionBrief modals are opened PERSISTENT natively
        /// (OpenModalPersistent — every mission brief via ShowMissionBriefing, GeoscapeView.cs:1903);
        /// Research/Diplomacy via OpenModal (non-persistent). The client replays with the same persistence. PURE.</summary>
        public static bool IsPersistent(ReportModalVariant variant)
            => variant == ReportModalVariant.NullData || variant == ReportModalVariant.SiteOnly
               || variant == ReportModalVariant.AmbushBrief || variant == ReportModalVariant.SiteMissionBrief;

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
        /// geoscape is modal-locked and the co-op session must not advance under it. Drives BOTH ends of the
        /// lock: the client's mirrored modal is view-locked (BlockingModalClientLockPatches — inert buttons,
        /// no local close, released only by the host's resolve → ReportModalHide) and the host rejects every
        /// in-flight client intent while it is pending (HostBlockingPromptGate in SyncEngine.OnActionRequest).
        /// = the mandatory ambush brief + every mirrored SITE MISSION BRIEF (host Confirm → tactical co-op
        /// deploy flow; host Cancel → ReportModalHide closes the client copy, normal flow resumes). PURE.
        /// </summary>
        public static bool IsBlockingModal(int modalType)
        {
            switch (modalType)
            {
                case GeoAmbushBrief:
                case GeoScavengeBrief:
                case AncientSiteAttackBrief:
                case AncientSiteDefenceBrief:
                    return true;
                default:
                    return false;
            }
        }
    }
}
