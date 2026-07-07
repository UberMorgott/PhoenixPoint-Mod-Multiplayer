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
    ///   • <see cref="MissionOutcome"/> — post-tactical mission OUTCOME report (Batch-2 P3; ids
    ///                                  1/3/5/12/16/21/27/29/35/37). Carries siteId + missionDef guid PLUS the
    ///                                  outcome-only trailing fields (missionClass discriminator, outcome state,
    ///                                  reward display blob) so the client rebuild is INDEPENDENT of the P1
    ///                                  site-mirror lifecycle (the mission record may already be tombstoned when
    ///                                  the outcome shows — the payload is self-sufficient, like the event
    ///                                  result card). NON-blocking: native local close, no gate, no view-lock.
    ///   • <see cref="AmbushBrief"/>  — modalData is a GeoAmbushMission (GeoAmbushBrief 15, the mandatory
    ///                                  "You've been ambushed!" prompt). carries siteId (mission.Site.SiteId) +
    ///                                  defId (mission.MissionDef.Guid); the client rebuilds a display-only
    ///                                  GeoAmbushMission(site, missionDef) and shows the SAME native modal,
    ///                                  view-locked (see BlockingModalClientLockPatches).
    ///   • <see cref="SiteMissionBrief"/> — modalData is a site-visit deploy-brief GeoMission
    ///                                  (GeoScavengeBrief 4 / AncientSiteAttackBrief 26 / AncientSiteDefenceBrief
    ///                                  28). Same wire shape as AmbushBrief (siteId + missionDef guid); the
    ///                                  client rebuilds the matching display-only mission class
    ///                                  (ReportModalReflection.BuildSiteMissionBrief) and shows the SAME native
    ///                                  brief, view-locked until the host's Confirm/Cancel (ReportModalHide).
    ///   • <see cref="ActiveMissionBrief"/> — modalData is a LIVE→site-id brief GeoMission whose bind needs
    ///                                  runtime state a fresh ctor can't supply (GeoHavenAttackBrief 0 /
    ///                                  GeoAlienBaseBrief 2 / GeoPhoenixBaseDefenseBrief 11 /
    ///                                  GeoPhoenixBaseInfestationBrief 20 / BehemothAttackBrief 34 /
    ///                                  InfestedHavenBrief 36). Same wire shape (siteId + missionDef guid); the
    ///                                  client resolves its OWN <c>site.ActiveMission</c> — attached by the P1
    ///                                  mission-state mirror on channel #5 — and shows the SAME native brief,
    ///                                  view-locked. Rebuild failure (site/mission unresolved, class mismatch,
    ///                                  fallback-34 family) → degraded notify-only text modal; the HOST intent
    ///                                  gate is armed either way (intents never race the host's decision).
    ///   • <see cref="InterceptionNotice"/> — the interception pair (InterceptionBrief 32 / InterceptionOutcome
    ///                                  33, WA-3 gap 5c). BOTH binds are decompile-verified UNBUILDABLE
    ///                                  client-side (see ReportModalClassifier — live aircraft objects), so the
    ///                                  client NEVER rebuilds the native window: it always shows the notify-only
    ///                                  text prompt (32 = pending-decision text + host gate armed; 33 = resolved
    ///                                  text, non-blocking). No ids carried — all fields stay defaults.
    ///   • <see cref="IntelNotice"/>  — the pandoran-evolution intel report (AlienResearchBrief 23, gap AC).
    ///                                  The bind is UNBUILDABLE client-side (reads the live
    ///                                  GeoscapeViewContext.Input + live alien-faction ResearchElements for its
    ///                                  3D mutation carousel — AlienResearchBriefDataBind.cs:250), so the client
    ///                                  ALWAYS shows the notify-only text prompt (InterceptionNotice precedent).
    ///                                  Non-blocking report; the diplomacy penalty it reports rides the mirrored
    ///                                  diplomacy channel (#4). No ids carried — all fields stay defaults.
    ///   • <see cref="CampaignEnd"/>  — the CAMPAIGN ended on the host (feat-campaign-end): victory outro /
    ///                                  defeat by Phoenix collapse / TFTV custom ending — all through the ONE
    ///                                  native chokepoint GeoLevelController.TriggerGameOver. NOT an OpenModal
    ///                                  window: ModalType carries the synthetic CampaignEndFlow.SentinelModalType
    ///                                  (255), ShareLevel the victory|defeat flag, DefId the victor faction's
    ///                                  Def.Guid (ending id). The client replays the SAME native outro
    ///                                  (cinematic def resolved locally — never on the wire) or degrades to a
    ///                                  notify prompt + menu return. See <see cref="CampaignEndFlow"/>.
    /// </summary>
    public enum ReportModalVariant : byte
    {
        NullData = 0,
        SiteOnly = 1,
        Research = 2,
        Diplomacy = 3,
        MissionOutcome = 4,   // Phase-B placeholder (do not emit in Phase-A)
        AmbushBrief = 5,      // mandatory ambush prompt mirror (view-only + blocking on the client)
        SiteMissionBrief = 6, // optional site deploy briefs (scavenge/ancient) — mirrored view-only + blocking
        ActiveMissionBrief = 7, // LIVE→site-id briefs off the P1-mirrored site.ActiveMission — blocking + degradable
        InterceptionNotice = 8, // interception brief/outcome (32/33) — ALWAYS notify-only on the client (WA-3)
        CampaignEnd = 9,      // campaign conclusion notice (synthetic sentinel 255) — outro replay / degrade+teardown
        IntelNotice = 10,     // pandoran-evolution intel report (23) — ALWAYS notify-only on the client (gap AC)
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

        /// <summary>Diplomacy: the share level. Research: the host's native "new research available" line
        /// visibility (<see cref="ResearchNavMirror"/> tri-state: 0 unknown / 1 hidden / 2 shown). 0 otherwise.</summary>
        public int ShareLevel;

        /// <summary>Research: the ResearchElement.ResearchID. Diplomacy: the faction's Def.Guid. "" otherwise.</summary>
        public string DefId;

        /// <summary>Diplomacy: the shared researches' ResearchID list. Empty for the other variants.</summary>
        public List<string> ExtraIds;

        /// <summary>MissionOutcome: the <see cref="GeoMissionRecord"/> class discriminator of the host's
        /// completed mission (carried on THIS wire — the site mirror may be tombstoned). 0 otherwise.</summary>
        public byte MissionClass;

        /// <summary>MissionOutcome: the host's <c>GeoMission.GetMissionOutcomeState()</c> (raw
        /// <c>TacFactionState</c>: None=0/Playing=1/Defeated=2/Won=3). 0 otherwise.</summary>
        public int OutcomeState;

        /// <summary>MissionOutcome: <see cref="RewardDisplaySnapshot"/>-encoded reward display lines
        /// (host <c>mission.Reward</c> read via <c>RewardDisplayReflection.BuildFromReward</c>). Empty otherwise.</summary>
        public byte[] RewardBlob;

        /// <summary>Batch-3 P5: host-monotonic report occurrence id (<see cref="DisplaySequence.NextReportOccId"/>)
        /// stamped at broadcast so the client's <see cref="ReportOccurrenceDedup"/> drops a transport double-send.
        /// 0 = legacy/unstamped wire (never deduped).</summary>
        public ushort OccId;

        /// <summary>Batch-3 P4: unified display-order stamp (<see cref="DisplaySequence.NextSeq"/>) taken at the
        /// host's own <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c> fire-time. 0 = legacy/unstamped → the client
        /// takes the pre-Batch-3 direct display path. The queue's nativePriority is <see cref="Priority"/> (the
        /// modal opener's priority IS the native view-switch request priority — decompile GeoscapeView.cs:849/868).</summary>
        public uint DisplaySeq;

        public ReportModalPayload(byte modalType, ReportModalVariant variant, int siteId, int priority,
                                  int shareLevel, string defId, List<string> extraIds)
            : this(modalType, variant, siteId, priority, shareLevel, defId, extraIds, 0, 0, null)
        {
        }

        public ReportModalPayload(byte modalType, ReportModalVariant variant, int siteId, int priority,
                                  int shareLevel, string defId, List<string> extraIds,
                                  byte missionClass, int outcomeState, byte[] rewardBlob)
        {
            ModalType = modalType;
            Variant = variant;
            SiteId = siteId;
            Priority = priority;
            ShareLevel = shareLevel;
            DefId = defId ?? "";
            ExtraIds = extraIds ?? new List<string>();
            MissionClass = missionClass;
            OutcomeState = outcomeState;
            RewardBlob = rewardBlob ?? new byte[0];
            OccId = 0;
            DisplaySeq = 0;
        }
    }
}
