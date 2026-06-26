namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Host-side whitelist + variant map for the report-window mirror (Phase-A). PURE (Unity-free) so it links
    /// into the test project and is unit-tested directly. Decides whether a native <c>GeoscapeView</c> modal is a
    /// REPORT we can safely mirror host->client, and which client rebuild path (<see cref="ReportModalVariant"/>)
    /// it takes. Only the Phase-A "A-variant" report modals are whitelisted — modals the client can fully
    /// reconstruct from already-synced ids (no extra per-frame state):
    ///   • GeoPhoenixBaseOutcome (6)   → NullData   (modalData null)
    ///   • GeoResearchComplete   (14)  → Research   (modalData GeoResearchCompleteData → researchID)
    ///   • PandoranRevealResult  (25)  → SiteOnly   (modalData GeoSite|null → siteId)
    ///   • DiplomacyResearchBrief(38)  → Diplomacy  (modalData DiplomacyResearchRewardData → faction+researches+level)
    /// Everything else (briefs, interactive decisions, mission OUTCOME modals — Phase-B) is NOT whitelisted →
    /// the host never broadcasts it and the client never suppresses it (left fully native).
    ///
    /// The live modalData read (host) + reconstruct (client) lives in <see cref="ReportModalReflection"/>
    /// (reflection-bound, not unit-testable); this class is the pure boundary that drives it.
    /// </summary>
    public static class ReportModalClassifier
    {
        // ── ModalType enum values (PhoenixPoint.Common.Utils.ModalType, verified decompile 2026-06-26) ──
        public const int GeoPhoenixBaseOutcome = 6;
        public const int GeoResearchComplete = 14;
        public const int PandoranRevealResult = 25;
        public const int DiplomacyResearchBrief = 38;

        /// <summary>True iff <paramref name="modalType"/> (native ModalType enum value) is a Phase-A whitelisted
        /// report modal. Takes the full int value (ModalType is int-backed: None=-1, _CustomMission=9999) so a
        /// non-report id can never alias a whitelisted one. PURE.</summary>
        public static bool IsReportModal(int modalType)
        {
            switch (modalType)
            {
                case GeoPhoenixBaseOutcome:
                case GeoResearchComplete:
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
                case GeoPhoenixBaseOutcome:
                default:
                    return ReportModalVariant.NullData;
            }
        }

        /// <summary>NullData/SiteOnly modals are opened PERSISTENT natively (OpenModalPersistent);
        /// Research/Diplomacy via OpenModal (non-persistent). The client replays with the same persistence. PURE.</summary>
        public static bool IsPersistent(ReportModalVariant variant)
            => variant == ReportModalVariant.NullData || variant == ReportModalVariant.SiteOnly;
    }
}
