using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Which client-side rebuild path a mirrored report modal takes. The host classifier maps a whitelisted
    /// <c>ModalType</c> to one of these; the client decode switches on it to reconstruct the <c>modalData</c>
    /// from already-synced ids. Pure (Unity-free) so it lives on the wire + in unit tests.
    ///   • <see cref="NullData"/>     — modalData is null (GeoPhoenixBaseOutcome). modalType only.
    ///   • <see cref="SiteOnly"/>     — modalData is a GeoSite or null (PandoranRevealResult). carries siteId.
    ///   • <see cref="Research"/>     — modalData is GeoResearchCompleteData (GeoResearchComplete). carries researchID.
    ///   • <see cref="Diplomacy"/>    — modalData is DiplomacyResearchRewardData (DiplomacyResearchBrief).
    ///                                  carries factionDefGuid + researchID[] + shareLevel.
    ///   • <see cref="MissionOutcome"/> — RESERVED for Phase-B (mission-outcome modals); NOT emitted this phase.
    /// </summary>
    public enum ReportModalVariant : byte
    {
        NullData = 0,
        SiteOnly = 1,
        Research = 2,
        Diplomacy = 3,
        MissionOutcome = 4,   // Phase-B placeholder (do not emit in Phase-A)
    }

    /// <summary>
    /// Pure (Unity-free) DTO for one mirrored host report window. Built host-side by
    /// <c>ReportModalClassifier.TryBuild</c> (reading the live modalData via reflection), serialized by
    /// <c>SyncProtocol.EncodeReportModal</c>, and decoded + reconstructed on the client in
    /// <c>SyncEngine.OnReportModalShow</c>. Only the fields a variant needs are populated; the rest stay at
    /// their defaults (siteId = -1, priority/shareLevel = 0, defId = "", extraIds empty).
    /// </summary>
    public struct ReportModalPayload
    {
        /// <summary>The native <c>ModalType</c> enum value (0..40 range; fits a byte for every report modal).</summary>
        public byte ModalType;

        /// <summary>Selects the client rebuild path.</summary>
        public ReportModalVariant Variant;

        /// <summary>SiteOnly: the GeoSite.SiteId (-1 = none). Unused (-1) for the other variants.</summary>
        public int SiteId;

        /// <summary>The host opener's modal priority, replayed verbatim so the client stacks identically.</summary>
        public int Priority;

        /// <summary>Diplomacy: the share level. 0 for the other variants.</summary>
        public int ShareLevel;

        /// <summary>Research: the ResearchElement.ResearchID. Diplomacy: the faction's Def.Guid. "" otherwise.</summary>
        public string DefId;

        /// <summary>Diplomacy: the shared researches' ResearchID list. Empty for the other variants.</summary>
        public List<string> ExtraIds;

        public ReportModalPayload(byte modalType, ReportModalVariant variant, int siteId, int priority,
                                  int shareLevel, string defId, List<string> extraIds)
        {
            ModalType = modalType;
            Variant = variant;
            SiteId = siteId;
            Priority = priority;
            ShareLevel = shareLevel;
            DefId = defId ?? "";
            ExtraIds = extraIds ?? new List<string>();
        }
    }
}
