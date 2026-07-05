namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Host-side whitelist + variant map for the report-window mirror (Phase-A). PURE (Unity-free) so it links
    /// into the test project and is unit-tested directly. Decides whether a native <c>GeoscapeView</c> modal is a
    /// REPORT we can safely mirror host->client, and which client rebuild path (<see cref="ReportModalVariant"/>)
    /// it takes. Only the Phase-A "A-variant" report modals are whitelisted — modals the client can fully
    /// reconstruct from already-synced ids (no extra per-frame state):
    ///   • GeoPhoenixBaseOutcome (6)   → NullData   (modalData null)
    ///   • GeoResearchComplete   (14)  → Research   (modalData GeoResearchCompleteData → researchID)
    ///   • GeoAmbushBrief        (15)  → AmbushBrief (modalData GeoAmbushMission → siteId + missionDef guid)
    ///   • PandoranRevealResult  (25)  → SiteOnly   (modalData GeoSite|null → siteId)
    ///   • DiplomacyResearchBrief(38)  → Diplomacy  (modalData DiplomacyResearchRewardData → faction+researches+level)
    /// Everything else (briefs, interactive decisions, mission OUTCOME modals — Phase-B) is NOT whitelisted →
    /// the host never broadcasts it and the client never suppresses it (left fully native).
    ///
    /// AMBUSH vs OPTIONAL DEPLOY PROMPTS (do not blur — 9e80b24 interplay): GeoAmbushBrief is the ONE
    /// mission-brief modal mirrored, because an ambush is MANDATORY (TacMissionTypeDef.MandatoryMission — no
    /// cancel; the whole geoscape is modal-locked until the host starts the mission). OPTIONAL arrival
    /// deploy/land prompts stay host-local on BOTH rails: event-rail prompts are excluded at the source by
    /// EventReflection.IsMissionDeployEvent (9e80b24), and modal-rail briefs (GeoHavenAttackBrief 0,
    /// GeoScavengeBrief 4, ...) are simply not whitelisted here — a host cancel must stay silent on the client.
    /// <see cref="IsBlockingModal"/> additionally marks the ambush as BLOCKING: the client's mirror is
    /// view-locked (inert button, no local close) and the host rejects client intents while it is pending
    /// (HostBlockingPromptGate) — native single-player semantics (nothing can happen under the prompt).
    ///
    /// The live modalData read (host) + reconstruct (client) lives in <see cref="ReportModalReflection"/>
    /// (reflection-bound, not unit-testable); this class is the pure boundary that drives it.
    /// </summary>
    public static class ReportModalClassifier
    {
        // ── ModalType enum values (PhoenixPoint.Common.Utils.ModalType, verified decompile 2026-06-26) ──
        public const int GeoPhoenixBaseOutcome = 6;
        public const int GeoResearchComplete = 14;
        public const int GeoAmbushBrief = 15;      // ModalType.cs:20 — mandatory ambush prompt (GeoscapeView.cs:1780)
        public const int PandoranRevealResult = 25;
        public const int DiplomacyResearchBrief = 38;

        /// <summary>True iff <paramref name="modalType"/> (native ModalType enum value) is a whitelisted
        /// report modal. Takes the full int value (ModalType is int-backed: None=-1, _CustomMission=9999) so a
        /// non-report id can never alias a whitelisted one. PURE.</summary>
        public static bool IsReportModal(int modalType)
        {
            switch (modalType)
            {
                case GeoPhoenixBaseOutcome:
                case GeoResearchComplete:
                case GeoAmbushBrief:
                case PandoranRevealResult:
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
                case GeoPhoenixBaseOutcome:
                default:
                    return ReportModalVariant.NullData;
            }
        }

        /// <summary>NullData/SiteOnly/AmbushBrief modals are opened PERSISTENT natively (OpenModalPersistent —
        /// the ambush brief via ShowMissionBriefing, GeoscapeView.cs:1903); Research/Diplomacy via OpenModal
        /// (non-persistent). The client replays with the same persistence. PURE.</summary>
        public static bool IsPersistent(ReportModalVariant variant)
            => variant == ReportModalVariant.NullData || variant == ReportModalVariant.SiteOnly
               || variant == ReportModalVariant.AmbushBrief;

        /// <summary>
        /// True iff <paramref name="modalType"/> is a BLOCKING host prompt: a mandatory, non-cancelable window
        /// during which the native single-player geoscape allows NOTHING else to happen. Drives BOTH ends of the
        /// co-op lock: the client's mirrored modal is view-locked (BlockingModalClientLockPatches — inert button,
        /// no local close, released only by the host's resolve → ReportModalHide) and the host rejects every
        /// in-flight client intent while it is pending (HostBlockingPromptGate in SyncEngine.OnActionRequest).
        /// Today = GeoAmbushBrief only; optional deploy briefs must NEVER be added here (they are host-local,
        /// see the class doc). PURE.
        /// </summary>
        public static bool IsBlockingModal(int modalType)
            => modalType == GeoAmbushBrief;
    }
}
