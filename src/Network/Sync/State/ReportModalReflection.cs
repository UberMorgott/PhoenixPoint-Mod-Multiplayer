using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the report-window mirror's modalData read (HOST) and rebuild (CLIENT). The mod has
    /// NO compile-time game references, so every member is resolved by name and cached. Two whitelisted report
    /// modals carry a typed payload object the client must reconstruct from already-synced ids:
    ///   • GeoResearchComplete → <c>GeoResearchCompleteData{ ResearchElement, SwitchToResearchState }</c>
    ///   • DiplomacyResearchBrief → <c>DiplomacyResearchRewardData{ GeoFaction Faction,
    ///         IEnumerable&lt;ResearchElement&gt; Researches, int DiplomacyShareLevel }</c>
    /// PandoranRevealResult carries a GeoSite (read/resolve via <see cref="GeoSiteReflection"/>); GeoPhoenixBaseOutcome
    /// carries null.
    ///
    /// Verified against the decompile (2026-06-26):
    ///   • <c>PhoenixPoint.Geoscape.View.ViewControllers.Modal.GeoResearchCompleteData</c>:
    ///       public field <c>ResearchElement ResearchElement</c>; public field <c>bool SwitchToResearchState</c>.
    ///   • <c>PhoenixPoint.Geoscape.View.ViewControllers.Modal.DiplomacyResearchRewardData</c>:
    ///       public fields <c>GeoFaction Faction</c>, <c>IEnumerable&lt;ResearchElement&gt; Researches</c>,
    ///       <c>int DiplomacyShareLevel</c>.
    ///   • <c>ResearchElement.ResearchID</c> (public readonly string, ResearchElement.cs:136) — wire id;
    ///       resolved back via <see cref="ResearchReflection.ResolveElement"/> (Research.GetResearchById).
    ///   • <c>GeoFaction.Def</c> (property) → GeoFactionDef : BaseDef (stable Guid); resolved on the client by
    ///       matching a live <c>GeoLevelController.Factions</c> entry's Def.Guid.
    /// Every member is best-effort: a miss DEGRADES (logged, null/empty) rather than throwing — the client then
    /// falls back to a siteless / element-less render, never crashes the geoscape UI.
    /// </summary>
    public static class ReportModalReflection
    {
        private static bool _ready;
        private static Type _researchCompleteDataType; // GeoResearchCompleteData
        private static FieldInfo _rcResearchElementField; // GeoResearchCompleteData.ResearchElement
        private static FieldInfo _rcSwitchToResearchField; // GeoResearchCompleteData.SwitchToResearchState
        private static PropertyInfo _rcUnlocksProp;       // ResearchElement.UnlocksResearches (lazy, off the live element)
        private static Type _diplomacyDataType;         // DiplomacyResearchRewardData
        private static FieldInfo _dipFactionField;       // DiplomacyResearchRewardData.Faction (GeoFaction)
        private static FieldInfo _dipResearchesField;    // DiplomacyResearchRewardData.Researches (IEnumerable<ResearchElement>)
        private static FieldInfo _dipShareLevelField;    // DiplomacyResearchRewardData.DiplomacyShareLevel (int)
        private static Type _researchElementType;        // ResearchElement (array element type for Researches)
        private static FieldInfo _factionsField;         // GeoLevelController.Factions
        private static PropertyInfo _factionDefProp;     // GeoFaction.Def

        // ── AmbushBrief members (own lazy gate — a miss degrades ONLY the ambush mirror, never the channel) ──
        private static bool _ambushEnsured;
        private static PropertyInfo _missionSiteProp;    // GeoMission.Site (public property, GeoMission.cs:136)
        private static PropertyInfo _missionDefProp;     // GeoMission.MissionDef (public property, GeoMission.cs:204)
        private static ConstructorInfo _ambushCtor;      // GeoAmbushMission(GeoSite, TacMissionTypeDef, MissionParams)

        // ── SiteMissionBrief members (own lazy gate — a miss degrades ONLY that brief's mirror) ──
        private static bool _siteMissionEnsured;
        private static ConstructorInfo _scavengeCtor;    // GeoScavengingMission(GeoSite, TacMissionTypeDef, MissionParams)
        private static ConstructorInfo _ancientCtor;     // GeoAncientSiteMission(GeoSite, TacMissionTypeDef)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            _researchCompleteDataType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewControllers.Modal.GeoResearchCompleteData");
            _diplomacyDataType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewControllers.Modal.DiplomacyResearchRewardData");
            _researchElementType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchElement");

            if (_researchCompleteDataType != null)
            {
                _rcResearchElementField = AccessTools.Field(_researchCompleteDataType, "ResearchElement");
                _rcSwitchToResearchField = AccessTools.Field(_researchCompleteDataType, "SwitchToResearchState");
            }
            if (_diplomacyDataType != null)
            {
                _dipFactionField = AccessTools.Field(_diplomacyDataType, "Faction");
                _dipResearchesField = AccessTools.Field(_diplomacyDataType, "Researches");
                _dipShareLevelField = AccessTools.Field(_diplomacyDataType, "DiplomacyShareLevel");
            }

            var geo = rt?.GeoLevel();
            if (geo != null)
            {
                _factionsField = AccessTools.Field(geo.GetType(), "Factions");
                if (_factionsField != null && _factionsField.GetValue(geo) is IEnumerable facs)
                    foreach (var f in facs)
                    {
                        if (f == null) continue;
                        _factionDefProp = AccessTools.Property(f.GetType(), "Def");
                        break;
                    }
            }

            // Core gate: the data-class shapes (needed to read host-side AND build client-side). Faction
            // resolution is best-effort (a null faction still shows the diplomacy card, just without the header).
            _ready = _researchCompleteDataType != null && _rcResearchElementField != null
                     && _diplomacyDataType != null && _dipFactionField != null
                     && _dipResearchesField != null && _dipShareLevelField != null
                     && _researchElementType != null;
        }

        /// <summary>
        /// Lazy bind of the ambush-brief members (separate from <see cref="Ensure"/> so a miss here can never
        /// regress the four verified Phase-A modals). Verified against the decompile (2026-07-05):
        ///   • <c>GeoMission.Site</c> public property (GeoMission.cs:136); <c>GeoMission.MissionDef</c> public
        ///     property (GeoMission.cs:204) — read host-side off the live GeoAmbushMission modalData.
        ///   • <c>GeoAmbushMission(GeoSite site, TacMissionTypeDef missionType, MissionParams missionParams =
        ///     null)</c> (GeoAmbushMission.cs:24) — EXACT 3-param match (harmony-accesstools-exact-param-match;
        ///     the obsolete 4-param and private 2-param overloads must not be picked). MissionParams is the
        ///     NESTED <c>GeoMission+MissionParams</c> → resolved via <c>AccessTools.Inner</c>.
        /// </summary>
        private static void EnsureAmbush()
        {
            if (_ambushEnsured) return;
            _ambushEnsured = true;   // one attempt; every user null-guards
            var missionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoMission");
            var ambushType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Missions.GeoAmbushMission");
            var siteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var tacMissionDefType = AccessTools.TypeByName("PhoenixPoint.Common.Levels.Missions.TacMissionTypeDef");
            if (missionType == null || ambushType == null || siteType == null || tacMissionDefType == null) return;

            _missionSiteProp = AccessTools.Property(missionType, "Site");
            _missionDefProp = AccessTools.Property(missionType, "MissionDef");
            var missionParamsType = AccessTools.Inner(missionType, "MissionParams");
            if (missionParamsType != null)
                _ambushCtor = AccessTools.Constructor(ambushType, new[] { siteType, tacMissionDefType, missionParamsType });
        }

        // ─── HOST read ────────────────────────────────────────────────────

        /// <summary>
        /// Host: classify <paramref name="modalType"/> (native ModalType enum value) and, if it is a whitelisted
        /// report, read its live <paramref name="modalData"/> into a wire <see cref="ReportModalPayload"/>. Returns
        /// false (no broadcast) for any non-whitelisted modal. Best-effort: a read failure on a whitelisted modal
        /// still produces a payload (degraded fields) rather than throwing — the client falls back gracefully.
        /// <paramref name="priority"/> is the host opener's modal priority (replayed verbatim).
        /// </summary>
        public static bool TryBuildPayload(int modalType, object modalData, int priority, out ReportModalPayload payload)
        {
            payload = default(ReportModalPayload);
            if (!ReportModalClassifier.IsReportModal(modalType)) return false;

            var variant = ReportModalClassifier.VariantFor(modalType);
            int siteId = -1;
            int shareLevel = 0;
            string defId = "";
            var extraIds = new List<string>();
            byte missionClass = 0;
            int outcomeState = 0;
            byte[] rewardBlob = null;

            switch (variant)
            {
                case ReportModalVariant.SiteOnly:
                    // modalData is a GeoSite (RevealedSites[0]) or null (no revealed site).
                    siteId = ReadSiteId(modalData);
                    break;
                case ReportModalVariant.Research:
                    // modalData is GeoResearchCompleteData → ResearchElement.ResearchID. ShareLevel carries the
                    // host's NATIVE "new research available" line visibility (ResearchNavMirror tri-state) so the
                    // client mirrors the host's answer instead of recomputing it on diverged derived state.
                    defId = ReadResearchCompleteId(modalData) ?? "";
                    shareLevel = ReadResearchNavFlag(modalData);
                    break;
                case ReportModalVariant.Diplomacy:
                    // modalData is DiplomacyResearchRewardData → Faction.Def.Guid + Researches[].ResearchID + DiplomacyShareLevel.
                    ReadDiplomacy(modalData, out defId, out extraIds, out shareLevel);
                    defId = defId ?? "";
                    extraIds = extraIds ?? new List<string>();
                    break;
                case ReportModalVariant.AmbushBrief:
                case ReportModalVariant.SiteMissionBrief:
                case ReportModalVariant.ActiveMissionBrief:
                    // modalData is the live GeoMission (ambush / scavenge / ancient-site / LIVE→site-id brief) →
                    // Site.SiteId + MissionDef.Guid off the GeoMission base properties. The ActiveMissionBrief
                    // family needs nothing more on THIS wire: the runtime bits ride the P1 mission record on
                    // the GeoSite channel (#5), which attached the mission the client will bind.
                    ReadAmbushBrief(modalData, out siteId, out defId);
                    break;
                case ReportModalVariant.MissionOutcome:
                    // modalData is the COMPLETED live GeoMission (post-tac rail UIStateInitial.cs:112 / cancel
                    // paths GeoscapeView.cs:1934/:1938). Base identity (siteId + missionDef guid) reads like the
                    // brief variants; the outcome-only tail (class discriminator + outcome state + reward display
                    // blob) rides THIS wire so the client rebuild never depends on the tombstonable P1 mirror.
                    ReadAmbushBrief(modalData, out siteId, out defId);
                    ReadMissionOutcome(modalData, out missionClass, out outcomeState, out rewardBlob);
                    break;
                case ReportModalVariant.InterceptionNotice:
                    // WA-3 interception pair (32/33): the client NEVER rebuilds the native window (32's
                    // InterceptionInfoData and 33's GeoAirMission are live-aircraft objects — see the
                    // classifier's INTERCEPTION FAMILY note), it only shows the notify-only prompt keyed by
                    // ModalType — so NOTHING is read off the modalData here (zero reflection = zero host-side
                    // failure modes). All payload fields stay defaults.
                    break;
                case ReportModalVariant.IntelNotice:
                    // AlienResearchBrief 23 (gap AC): the client NEVER rebuilds the native window (the bind
                    // reads the live GeoscapeViewContext.Input + live ALIEN-faction ResearchElements for its
                    // 3D mutation carousel — see the classifier's INTEL FAMILY note), it only shows the
                    // notify-only prompt — so NOTHING is read off the modalData here (WA-3 zero-reflection
                    // precedent). The diplomacy penalty rides the mirrored diplomacy channel (#4).
                    break;
                case ReportModalVariant.NullData:
                default:
                    break; // modalType only
            }

            // Whitelisted ModalType values are all 0..40 → fit the byte wire field.
            payload = new ReportModalPayload((byte)modalType, variant, siteId, priority, shareLevel, defId, extraIds,
                                             missionClass, outcomeState, rewardBlob);
            return true;
        }

        /// <summary>Host: <c>GeoSite.SiteId</c> off a PandoranRevealResult modalData (a GeoSite, or null → -1).</summary>
        public static int ReadSiteId(object site)
            => site == null ? -1 : GeoSiteReflection.GetSiteId(site);

        /// <summary>
        /// <c>mission.Site.SiteId</c> off a live <c>GeoMission</c> (any subclass — <c>GeoMission.Site</c> is the
        /// base public property, GeoMission.cs:136), or -1 on null/unreadable. Used by the begin-mission relay on
        /// BOTH sides: the client reads it off its rebuilt brief's <c>UIStateGeoModal.ModalData</c> to stamp the
        /// intent's site identity, the host reads it off ITS open brief to validate the relayed id. Best-effort.
        /// </summary>
        public static int GetMissionSiteId(object mission)
        {
            if (mission == null) return -1;
            try
            {
                EnsureAmbush();   // binds GeoMission.Site (shared member cache)
                if (_missionSiteProp == null) return -1;
                return ReadSiteId(_missionSiteProp.GetValue(mission, null));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer] ReportModalReflection.GetMissionSiteId failed: " + ex.Message);
                return -1;
            }
        }

        /// <summary>Host: <c>GeoResearchCompleteData.ResearchElement.ResearchID</c>, or null on any miss.</summary>
        public static string ReadResearchCompleteId(object researchCompleteData)
        {
            try
            {
                Ensure(GeoRuntime.Instance);
                if (researchCompleteData == null || _rcResearchElementField == null) return null;
                var element = _rcResearchElementField.GetValue(researchCompleteData);
                return ResearchReflection.GetId(element);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.ReadResearchCompleteId failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Host: the NATIVE "new research available" line visibility of the research-complete popup, as the
        /// bind computes it — <c>ResearchElement.UnlocksResearches.Any()</c> (GeoReseatchCompleteDataBind.cs:
        /// 124-125 → SetResearchRewards toggles NewResearchesGroup on <c>Count() > 0</c>).
        /// READ-TIMING CONTRACT: this recompute is only correct AFTER the research-completion cascade settles —
        /// the OpenModal Postfix runs INSIDE the <c>Research.OnResearchCompleted</c> dispatch, before dependent
        /// elements flip Revealed/Unlocked, so it must NOT be called from there (that shipped stale NavHidden
        /// every time — soak 2026-07-05). Callers reach it via the deferred one-tick broadcast
        /// (<c>SyncEngine.FlushDeferredReportModals</c>; <c>ReportModalClassifier.ShouldDeferHostBroadcast</c>).
        /// Returns a <see cref="ResearchNavMirror"/> tri-state: Unknown on ANY miss (the client then
        /// leaves its bind native — fail-open, never a stripped host/client button from a read failure).
        /// </summary>
        public static int ReadResearchNavFlag(object researchCompleteData)
        {
            try
            {
                Ensure(GeoRuntime.Instance);
                if (researchCompleteData == null || _rcResearchElementField == null) return ResearchNavMirror.NavUnknown;
                var element = _rcResearchElementField.GetValue(researchCompleteData);
                if (element == null) return ResearchNavMirror.NavUnknown;
                if (_rcUnlocksProp == null || !_rcUnlocksProp.DeclaringType.IsInstanceOfType(element))
                    _rcUnlocksProp = AccessTools.Property(element.GetType(), "UnlocksResearches");
                if (_rcUnlocksProp == null) return ResearchNavMirror.NavUnknown;
                if (!(_rcUnlocksProp.GetValue(element, null) is IEnumerable unlocks)) return ResearchNavMirror.NavUnknown;
                foreach (var _ in unlocks) return ResearchNavMirror.FlagFor(true);   // any element → line shown
                return ResearchNavMirror.FlagFor(false);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] ReportModalReflection.ReadResearchNavFlag failed: " + ex.Message);
                return ResearchNavMirror.NavUnknown;
            }
        }

        /// <summary>
        /// Host: read a <c>DiplomacyResearchRewardData</c> into wire fields — faction Def.Guid, the shared
        /// researches' ResearchIDs, and the share level. Best-effort: any miss yields empty/0 for that field.
        /// </summary>
        public static void ReadDiplomacy(object diplomacyData, out string factionGuid, out List<string> researchIds, out int shareLevel)
        {
            factionGuid = ""; researchIds = new List<string>(); shareLevel = 0;
            try
            {
                Ensure(GeoRuntime.Instance);
                if (diplomacyData == null) return;

                if (_dipFactionField != null)
                {
                    var faction = _dipFactionField.GetValue(diplomacyData);
                    if (faction != null && _factionDefProp != null)
                        factionGuid = DefReflection.GetGuid(_factionDefProp.GetValue(faction, null)) ?? "";
                }
                if (_dipResearchesField != null && _dipResearchesField.GetValue(diplomacyData) is IEnumerable researches)
                {
                    foreach (var el in researches)
                    {
                        var id = ResearchReflection.GetId(el);
                        if (!string.IsNullOrEmpty(id)) researchIds.Add(id);
                    }
                }
                if (_dipShareLevelField != null)
                {
                    try { shareLevel = Convert.ToInt32(_dipShareLevelField.GetValue(diplomacyData)); }
                    catch { shareLevel = 0; }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.ReadDiplomacy failed: " + ex.Message); }
        }

        /// <summary>
        /// Host: read the ambush brief's wire identity off its live <c>GeoAmbushMission</c> modalData —
        /// <c>Site.SiteId</c> + <c>MissionDef.Guid</c>. Best-effort: a miss leaves the degraded defaults
        /// (siteId -1 / defId "") and the client skips the show (never a crash; the host intent gate is armed
        /// independently in <c>ReportModalMirror.HostBroadcast</c>).
        /// </summary>
        public static void ReadAmbushBrief(object ambushMission, out int siteId, out string missionDefGuid)
        {
            siteId = -1;
            missionDefGuid = "";
            try
            {
                EnsureAmbush();
                if (ambushMission == null) return;
                if (_missionSiteProp != null)
                {
                    var site = _missionSiteProp.GetValue(ambushMission, null);
                    if (site != null) siteId = GeoSiteReflection.GetSiteId(site);
                }
                if (_missionDefProp != null)
                    missionDefGuid = DefReflection.GetGuid(_missionDefProp.GetValue(ambushMission, null)) ?? "";
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.ReadAmbushBrief failed: " + ex.Message); }
        }

        // ─── CLIENT rebuild ───────────────────────────────────────────────

        /// <summary>Client: resolve a GeoSite by id (PandoranRevealResult modalData), or null (siteless card).</summary>
        public static object ResolveSite(GeoRuntime rt, int siteId)
            => GeoSiteReflection.ResolveSiteById(rt, siteId);

        /// <summary>
        /// Client: build a <c>GeoResearchCompleteData{ ResearchElement = resolve-by-id, SwitchToResearchState = false }</c>.
        /// SwitchToResearchState is forced false so the modal's close path never drives ToResearchState on a client
        /// (cutscene safety is additionally covered by the null DialogCallback in <see cref="GeoModalDisplay"/>).
        /// Returns null if the element can't be resolved (caller skips the show).
        /// </summary>
        public static object BuildResearchCompleteData(GeoRuntime rt, string researchId)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return null;
                var element = ResearchReflection.ResolveElement(rt, researchId);
                if (element == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildResearchCompleteData: researchId '" + researchId + "' did not resolve (skipping show)");
                    return null;
                }
                var data = Activator.CreateInstance(_researchCompleteDataType);
                _rcResearchElementField.SetValue(data, element);
                if (_rcSwitchToResearchField != null) _rcSwitchToResearchField.SetValue(data, false);
                return data;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.BuildResearchCompleteData failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Client: build a <c>DiplomacyResearchRewardData{ Faction = resolve-by-guid, Researches = resolve-by-id[],
        /// DiplomacyShareLevel = level }</c>. Unresolved entries are skipped; a null faction still shows the card
        /// (header degraded). Returns null only on a hard reflection failure.
        /// </summary>
        public static object BuildDiplomacyData(GeoRuntime rt, string factionGuid, List<string> researchIds, int shareLevel)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return null;
                var data = Activator.CreateInstance(_diplomacyDataType);

                var faction = ResolveFactionByGuid(rt, factionGuid);
                if (faction != null) _dipFactionField.SetValue(data, faction);

                // Build a typed ResearchElement[] (assignable to IEnumerable<ResearchElement>) of the resolved elements.
                var resolved = new List<object>();
                if (researchIds != null)
                    foreach (var id in researchIds)
                    {
                        var el = ResearchReflection.ResolveElement(rt, id);
                        if (el != null) resolved.Add(el);
                    }
                var arr = Array.CreateInstance(_researchElementType, resolved.Count);
                for (int i = 0; i < resolved.Count; i++) arr.SetValue(resolved[i], i);
                _dipResearchesField.SetValue(data, arr);

                if (_dipShareLevelField != null) _dipShareLevelField.SetValue(data, shareLevel);
                return data;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.BuildDiplomacyData failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Client: rebuild a DISPLAY-ONLY <c>GeoAmbushMission(site, missionDef)</c> for the mirrored
        /// GeoAmbushBrief modal — the site resolved by id, the <c>TacMissionTypeDef</c> by guid off the shared
        /// <c>DefRepository</c>. The 3-param ctor is pure field assignment (GeoMission.cs:214-220 — Site/_squad/
        /// MissionDef/MissionData only); the mission is NEVER attached to the site (no SetActiveMission, no
        /// events, no producers on the frozen client sim) — it only feeds the native modal's data bind
        /// (AmbushDataBind → CommonMissionDataController.SetData: threat level, site local-time light, mist —
        /// all read-only off the synced site + def). Returns null on ANY unresolved piece (caller skips the show).
        /// </summary>
        public static object BuildAmbushMission(GeoRuntime rt, int siteId, string missionDefGuid)
        {
            try
            {
                EnsureAmbush();
                if (_ambushCtor == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildAmbushMission: GeoAmbushMission ctor unbound (skipping show)");
                    return null;
                }
                var site = GeoSiteReflection.ResolveSiteById(rt, siteId);
                if (site == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildAmbushMission: siteId " + siteId + " did not resolve (skipping show)");
                    return null;
                }
                var missionDef = DefReflection.GetDefByGuid(missionDefGuid);
                if (missionDef == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildAmbushMission: missionDef guid '" + missionDefGuid + "' did not resolve (skipping show)");
                    return null;
                }
                return _ambushCtor.Invoke(new object[] { site, missionDef, null });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.BuildAmbushMission failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Lazy bind of the site-mission-brief ctors (separate gate, mirroring <see cref="EnsureAmbush"/> so a
        /// miss can never regress the other variants). Verified against the decompile (2026-07-05):
        ///   • <c>GeoScavengingMission(GeoSite site, TacMissionTypeDef missionType, MissionParams missionParams
        ///     = null)</c> (GeoScavengingMission.cs:23) — EXACT 3-param match (harmony-accesstools-exact-param-
        ///     match; the [Obsolete] 4-param and private 2-param overloads must not be picked). Pure base-ctor
        ///     (field assignment only, GeoMission.cs:214-220).
        ///   • <c>GeoAncientSiteMission(GeoSite site, TacMissionTypeDef missionType)</c>
        ///     (GeoAncientSiteMission.cs:30) — EXACT 2-param match; empty body over the pure base-ctor.
        /// </summary>
        private static void EnsureSiteMission()
        {
            if (_siteMissionEnsured) return;
            _siteMissionEnsured = true;   // one attempt; every user null-guards
            var missionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoMission");
            var siteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var tacMissionDefType = AccessTools.TypeByName("PhoenixPoint.Common.Levels.Missions.TacMissionTypeDef");
            var scavType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoScavengingMission");
            var ancientType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Missions.GeoAncientSiteMission");
            if (missionType == null || siteType == null || tacMissionDefType == null) return;

            var missionParamsType = AccessTools.Inner(missionType, "MissionParams");
            if (scavType != null && missionParamsType != null)
                _scavengeCtor = AccessTools.Constructor(scavType, new[] { siteType, tacMissionDefType, missionParamsType });
            if (ancientType != null)
                _ancientCtor = AccessTools.Constructor(ancientType, new[] { siteType, tacMissionDefType });
        }

        /// <summary>
        /// Client: rebuild the DISPLAY-ONLY site-visit deploy-brief mission for a mirrored SiteMissionBrief
        /// modal — the concrete class selected by <paramref name="modalType"/> (GeoScavengeBrief 4 →
        /// GeoScavengingMission; AncientSiteAttackBrief 26 / AncientSiteDefenceBrief 28 → GeoAncientSiteMission),
        /// the site resolved by id, the <c>TacMissionTypeDef</c> by guid. Same contract as
        /// <see cref="BuildAmbushMission"/>: the ctor is pure field assignment, the mission is NEVER attached to
        /// the site (no SetActiveMission, no producers on the frozen client sim) — it only feeds the native
        /// brief's data bind (ScavengeBriefDataBind / AncientSiteBriefDataBind: Site + MissionDef-derived reads
        /// only). Returns null on ANY unresolved piece (caller skips the show; the host intent gate stays armed
        /// independently).
        /// </summary>
        public static object BuildSiteMissionBrief(GeoRuntime rt, byte modalType, int siteId, string missionDefGuid)
        {
            try
            {
                EnsureSiteMission();
                ConstructorInfo ctor;
                bool withParams;
                switch (modalType)
                {
                    case ReportModalClassifier.GeoScavengeBrief:
                        ctor = _scavengeCtor; withParams = true; break;
                    case ReportModalClassifier.AncientSiteAttackBrief:
                    case ReportModalClassifier.AncientSiteDefenceBrief:
                        ctor = _ancientCtor; withParams = false; break;
                    default:
                        Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildSiteMissionBrief: unmapped modalType " + modalType + " (skipping show)");
                        return null;
                }
                if (ctor == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildSiteMissionBrief: ctor unbound for modalType " + modalType + " (skipping show)");
                    return null;
                }
                var site = GeoSiteReflection.ResolveSiteById(rt, siteId);
                if (site == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildSiteMissionBrief: siteId " + siteId + " did not resolve (skipping show)");
                    return null;
                }
                var missionDef = DefReflection.GetDefByGuid(missionDefGuid);
                if (missionDef == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildSiteMissionBrief: missionDef guid '" + missionDefGuid + "' did not resolve (skipping show)");
                    return null;
                }
                return ctor.Invoke(withParams ? new object[] { site, missionDef, null } : new object[] { site, missionDef });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.BuildSiteMissionBrief failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Client: resolve the modalData for a mirrored ActiveMissionBrief — the client's OWN
        /// <c>site.ActiveMission</c>, attached by the P1 mission-state mirror (GeoSite channel #5,
        /// <c>GeoSiteReflection.ApplyMission</c>). NO object is constructed here: the attached mirror IS the
        /// class-exact mission with the DTO-stamped runtime bits, so the native bind
        /// (HavenDefenceBriefDataBind / AlienBaseDataBind / PhoenixBaseDefenseDataBind / …) reads it exactly
        /// like the host's. Returns null — the caller degrades to the notify-only text prompt
        /// (<c>ReportModalClassifier.ShouldShowDegradedNotice</c>) — when:
        ///   • the site id doesn't resolve (dangling id on a not-yet-synced client map);
        ///   • the site has no attached mission (mission record not landed yet / tombstoned / Unknown class);
        ///   • the mission class doesn't faithfully bind this ModalType
        ///     (<c>ReportModalClassifier.ActiveMissionRebuildMatches</c> — incl. the always-degrading 34);
        ///   • the wire missionDef guid mismatches the attached mission (stale mirror of a replaced mission).
        /// The HOST intent gate was armed at the 0x69 SHOW independently of this decision.
        /// </summary>
        public static object BuildActiveMissionBrief(GeoRuntime rt, byte modalType, int siteId, string missionDefGuid)
        {
            try
            {
                var site = GeoSiteReflection.ResolveSiteById(rt, siteId);
                if (site == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildActiveMissionBrief: siteId " + siteId
                                     + " did not resolve (degrading)");
                    return null;
                }
                var mission = GeoSiteReflection.GetActiveMission(site);
                if (mission == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildActiveMissionBrief: site " + siteId
                                     + " has no mirrored ActiveMission (degrading)");
                    return null;
                }
                byte missionClass = GeoSiteReflection.ClassifyMission(mission);
                if (!ReportModalClassifier.ActiveMissionRebuildMatches(modalType, missionClass))
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildActiveMissionBrief: mirrored class "
                                     + missionClass + " does not bind modalType " + modalType + " (degrading)");
                    return null;
                }
                if (!string.IsNullOrEmpty(missionDefGuid)
                    && GeoSiteReflection.GetMissionDefGuid(mission) != missionDefGuid)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildActiveMissionBrief: mirrored missionDef"
                                     + " mismatches the wire guid '" + missionDefGuid + "' (stale mirror — degrading)");
                    return null;
                }
                return mission;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] ReportModalReflection.BuildActiveMissionBrief failed: " + ex.Message);
                return null;
            }
        }

        // ─── MissionOutcome members (Batch-2 P3; own lazy gate — a miss degrades ONLY the outcome mirror) ───
        // Decompile-verified 2026-07-05:
        //   • GeoMission.GetMissionOutcomeState() → TacFactionState (GeoMission.cs:556; null Result → Playing).
        //   • GeoMission.Reward { get; private set; } (GeoMission.cs:145) — NOT set by the ctor (only
        //     Complete/Cancel create it), so the client's rebuilt display mission MUST be stamped with a
        //     reconstructed GeoFactionReward or the outcome binds NRE (HavenDefence/InfestedHaven read
        //     Reward.ApplyResult.Diplomacy; AncientSite reads Reward.HasRewards()).
        //   • GeoMission.Result { get; private set; } (:143) + IsCompleted { get; protected set; } (:149) —
        //     AlienBaseOutcomeDataBind reads Result.GetResultByFacionDef(viewer.Def.PPFactionDef).State
        //     (FirstOrDefault → null on empty ⇒ stamp ONE FactionResult{viewer PPFactionDef, outcomeState}).
        //   • TacMissionResult (parameterless; readonly field FactionResults: List<FactionResult>) +
        //     FactionResult (parameterless; public fields FactionDef/State) — PhoenixPoint.Common.Entities.
        //   • GeoFactionReward / GeoFactionRewardApplyResult (parameterless, PhoenixPoint.Geoscape.Core): all
        //     collections field-initialized; ApplyResult starts NULL on the reward → stamp one. Display fields
        //     filled from the wire RewardDisplaySnapshot: Resources (outer = HasRewards gate + ApplyResult =
        //     what RewardsController.SetResources renders), Items (GeoItem(def,count) per line), and
        //     FactionSkillPoints. Diplomacy lines are NOT reconstructed (parties ride the snapshot as display
        //     NAMES, not guids — live-object rebuild would be locale-fragile): the attitude row shows empty on
        //     the client (AttitudeChange.SetAttitudes(empty) hides natively); diplomacy STATE converges via
        //     channel #4. Documented display degradation.
        //   • GeoItem(ItemDef, int count = 1, int charges = -1, AmmoManager, int malfunctionPercent = -100)
        //     (GeoItem.cs:31) + ItemStorage.AddItem(GeoItem) (ItemStorage.cs:52).
        private static bool _outcomeEnsured;
        private static MethodInfo _getMissionOutcomeState;   // GeoMission.GetMissionOutcomeState()
        private static PropertyInfo _missionRewardProp;      // GeoMission.Reward (private set)
        private static MethodInfo _missionResultSetter;      // GeoMission.Result private set
        private static MethodInfo _missionCompletedSetter;   // GeoMission.IsCompleted protected set
        private static Type _tacMissionResultType;           // TacMissionResult
        private static FieldInfo _tacResultFactionsField;    // TacMissionResult.FactionResults (readonly List<>)
        private static Type _factionResultType;              // FactionResult
        private static FieldInfo _facResultDefField;         // FactionResult.FactionDef (PPFactionDef)
        private static FieldInfo _facResultStateField;       // FactionResult.State (TacFactionState)
        private static Type _tacFactionStateEnum;            // TacFactionState
        private static PropertyInfo _viewerFactionProp;      // GeoLevelController.ViewerFaction
        private static FieldInfo _geoFactionDefPPField;      // GeoFactionDef.PPFactionDef (public field)
        private static Type _factionRewardType;              // GeoFactionReward
        private static FieldInfo _frwResourcesField, _frwItemsField, _frwSkillField, _frwApplyResultField;
        private static Type _applyResultType;                // GeoFactionRewardApplyResult
        private static FieldInfo _arwResourcesField, _arwItemsField, _arwSkillField;
        private static MethodInfo _packAddUnique;            // ResourcePack.AddUnique(ResourceUnit)
        private static ConstructorInfo _resourceUnitCtor2;   // ResourceUnit(ResourceType, float)
        private static Type _resourceTypeEnum2;              // ResourceType
        private static MethodInfo _storageAddItem;           // ItemStorage.AddItem(GeoItem)
        private static ConstructorInfo _geoItemCtor;         // GeoItem(ItemDef, int, int, AmmoManager, int)

        private static void EnsureOutcome()
        {
            if (_outcomeEnsured) return;
            _outcomeEnsured = true;   // one attempt; every user null-guards
            try
            {
                var missionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoMission");
                var geoLevelT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
                var geoFactionDefT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFactionDef");
                _tacMissionResultType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.TacMissionResult");
                _factionResultType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.FactionResult");
                _tacFactionStateEnum = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacFactionState");
                _factionRewardType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Core.GeoFactionReward");
                _applyResultType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Core.GeoFactionRewardApplyResult");
                var packT = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourcePack");
                var unitT = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceUnit");
                _resourceTypeEnum2 = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceType");
                var storageT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.ItemStorage");
                var geoItemT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoItem");
                var itemDefT = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemDef");
                var ammoT = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.AmmoManager");
                if (missionT == null) return;

                _getMissionOutcomeState = AccessTools.Method(missionT, "GetMissionOutcomeState", Type.EmptyTypes);
                _missionRewardProp = AccessTools.Property(missionT, "Reward");
                _missionResultSetter = AccessTools.PropertySetter(missionT, "Result");
                _missionCompletedSetter = AccessTools.PropertySetter(missionT, "IsCompleted");
                if (_tacMissionResultType != null)
                    _tacResultFactionsField = AccessTools.Field(_tacMissionResultType, "FactionResults");
                if (_factionResultType != null)
                {
                    _facResultDefField = AccessTools.Field(_factionResultType, "FactionDef");
                    _facResultStateField = AccessTools.Field(_factionResultType, "State");
                }
                if (geoLevelT != null) _viewerFactionProp = AccessTools.Property(geoLevelT, "ViewerFaction");
                if (geoFactionDefT != null) _geoFactionDefPPField = AccessTools.Field(geoFactionDefT, "PPFactionDef");
                if (_factionRewardType != null)
                {
                    _frwResourcesField = AccessTools.Field(_factionRewardType, "Resources");
                    _frwItemsField = AccessTools.Field(_factionRewardType, "Items");
                    _frwSkillField = AccessTools.Field(_factionRewardType, "FactionSkillPoints");
                    _frwApplyResultField = AccessTools.Field(_factionRewardType, "ApplyResult");
                }
                if (_applyResultType != null)
                {
                    _arwResourcesField = AccessTools.Field(_applyResultType, "Resources");
                    _arwItemsField = AccessTools.Field(_applyResultType, "Items");
                    _arwSkillField = AccessTools.Field(_applyResultType, "FactionSkillPoints");
                }
                if (packT != null && unitT != null)
                {
                    // EXACT param matches (harmony-accesstools-exact-param-match) — both carry overloads.
                    _packAddUnique = AccessTools.Method(packT, "AddUnique", new[] { unitT });
                    if (_resourceTypeEnum2 != null)
                        _resourceUnitCtor2 = AccessTools.Constructor(unitT, new[] { _resourceTypeEnum2, typeof(float) });
                }
                if (storageT != null && geoItemT != null)
                    _storageAddItem = AccessTools.Method(storageT, "AddItem", new[] { geoItemT });
                if (geoItemT != null && itemDefT != null && ammoT != null)
                    _geoItemCtor = AccessTools.Constructor(geoItemT,
                        new[] { itemDefT, typeof(int), typeof(int), ammoT, typeof(int) });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.EnsureOutcome failed: " + ex.Message); }
        }

        /// <summary>
        /// Host: read the completed mission's OUTCOME wire fields off the live modalData —
        /// class discriminator (<c>GeoSiteReflection.ClassifyMission</c>), outcome state
        /// (<c>GetMissionOutcomeState()</c> raw int; miss → 0 None) and the reward display blob
        /// (<c>mission.Reward</c> → <c>RewardDisplayReflection.BuildFromReward</c> → snapshot encode; a
        /// cancel-path empty reward yields an empty snapshot → empty-render on the client, matching the host).
        /// </summary>
        public static void ReadMissionOutcome(object mission, out byte missionClass, out int outcomeState, out byte[] rewardBlob)
        {
            missionClass = 0;
            outcomeState = 0;
            rewardBlob = null;
            try
            {
                EnsureOutcome();
                if (mission == null) return;
                missionClass = GeoSiteReflection.ClassifyMission(mission);
                if (_getMissionOutcomeState != null)
                {
                    try { outcomeState = Convert.ToInt32(_getMissionOutcomeState.Invoke(mission, null)); }
                    catch { outcomeState = 0; }
                }
                var reward = _missionRewardProp?.GetValue(mission, null);
                var snap = RewardDisplayReflection.BuildFromReward(reward);
                rewardBlob = RewardDisplaySnapshot.Encode(snap);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.ReadMissionOutcome failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: rebuild the DISPLAY-ONLY mission for a mirrored MISSION-OUTCOME modal — entirely from the
        /// 0x69 payload (siteId + missionDef guid + missionClass + outcomeState + reward blob), NEVER from
        /// <c>site.ActiveMission</c> (the P1 mirror may already be tombstoned when the outcome shows — spec
        /// Batch-2 ordering decision: the payload is self-sufficient, like the event result card). The mission
        /// is constructed via the SAME pure ctor map as the P1 attach (<c>GeoSiteReflection.BuildDisplayMission</c>),
        /// then stamped with a minimal Result (viewer FactionResult carrying the host's outcome state — the
        /// AlienBase/AncientSite binds branch on it) and a reconstructed display Reward (resources/items/skill
        /// points off the wire snapshot — <c>RewardsController.SetReward</c> then renders fully natively). The
        /// mission is NEVER attached to the site and never launched. Null on ANY unresolved piece → the caller
        /// skips the show (non-blocking report; nothing waits on it).
        /// </summary>
        public static object BuildMissionOutcome(GeoRuntime rt, byte modalType, int siteId, string missionDefGuid,
                                                 byte missionClass, int outcomeState, byte[] rewardBlob)
        {
            try
            {
                EnsureOutcome();
                if (!ReportModalClassifier.OutcomeRebuildMatches(modalType, missionClass))
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildMissionOutcome: class " + missionClass
                                     + " does not bind outcome modalType " + modalType + " (skipping show)");
                    return null;
                }
                var site = GeoSiteReflection.ResolveSiteById(rt, siteId);
                if (site == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildMissionOutcome: siteId " + siteId
                                     + " did not resolve (skipping show)");
                    return null;
                }
                var mission = GeoSiteReflection.BuildDisplayMission(rt, site, missionClass, missionDefGuid);
                if (mission == null)
                {
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildMissionOutcome: display mission for class "
                                     + missionClass + " def '" + missionDefGuid + "' did not build (skipping show)");
                    return null;
                }
                if (!StampOutcomeResult(rt, mission, outcomeState))
                {
                    // Without a Result the AlienBase bind NREs (Result.GetResultByFacionDef) and the AncientSite
                    // bind mis-branches (Playing) — skip rather than show a wrong/crashing card.
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildMissionOutcome: outcome-result stamp failed (skipping show)");
                    return null;
                }
                if (!StampDisplayReward(mission, RewardDisplaySnapshot.Decode(rewardBlob)))
                {
                    // Without a Reward object the HavenDefence/InfestedHaven/AncientSite binds NRE on
                    // Reward.ApplyResult / Reward.HasRewards() — skip rather than crash the geoscape UI.
                    Debug.LogWarning("[Multiplayer] ReportModalReflection.BuildMissionOutcome: reward stamp failed (skipping show)");
                    return null;
                }
                return mission;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] ReportModalReflection.BuildMissionOutcome failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Stamp <c>mission.Result</c> = a minimal <c>TacMissionResult</c> with ONE
        /// <c>FactionResult{ FactionDef = viewer PPFactionDef, State = (TacFactionState)outcomeState }</c>, and
        /// <c>IsCompleted = true</c> (host parity). False on any reflection miss.</summary>
        private static bool StampOutcomeResult(GeoRuntime rt, object mission, int outcomeState)
        {
            try
            {
                if (_tacMissionResultType == null || _factionResultType == null || _tacFactionStateEnum == null
                    || _tacResultFactionsField == null || _facResultDefField == null || _facResultStateField == null
                    || _missionResultSetter == null) return false;

                object viewerPPDef = null;
                var geo = rt?.GeoLevel();
                var viewer = geo != null ? _viewerFactionProp?.GetValue(geo, null) : null;
                if (viewer != null)
                {
                    var facDef = AccessTools.Property(viewer.GetType(), "Def")?.GetValue(viewer, null);
                    if (facDef != null) viewerPPDef = _geoFactionDefPPField?.GetValue(facDef);
                }
                if (viewerPPDef == null) return false;   // GetResultByFacionDef would miss → bind NRE

                var result = Activator.CreateInstance(_tacMissionResultType);
                var facResult = Activator.CreateInstance(_factionResultType);
                _facResultDefField.SetValue(facResult, viewerPPDef);
                _facResultStateField.SetValue(facResult, Enum.ToObject(_tacFactionStateEnum, outcomeState));
                if (!(_tacResultFactionsField.GetValue(result) is IList factionResults)) return false;
                factionResults.Add(facResult);

                _missionResultSetter.Invoke(mission, new[] { result });
                try { _missionCompletedSetter?.Invoke(mission, new object[] { true }); } catch { /* cosmetic parity */ }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] ReportModalReflection.StampOutcomeResult failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Stamp <c>mission.Reward</c> = a reconstructed display <c>GeoFactionReward</c> (+ ApplyResult) off the
        /// wire snapshot: resource lines (outer pack = the <c>HasRewards()</c> gate; ApplyResult pack = what
        /// <c>RewardsController.SetResources</c> renders), item lines (fresh <c>GeoItem(def,count)</c> per side)
        /// and skill points. An empty/null snapshot still stamps the EMPTY reward pair — the binds then render
        /// the native "no rewards" card exactly like the host's cancel path. False on a reflection miss.
        /// </summary>
        private static bool StampDisplayReward(object mission, RewardDisplaySnapshot snap)
        {
            try
            {
                if (_factionRewardType == null || _applyResultType == null || _frwApplyResultField == null
                    || _missionRewardProp == null || _missionRewardProp.GetSetMethod(true) == null)
                    return false;

                var reward = Activator.CreateInstance(_factionRewardType);
                var applyResult = Activator.CreateInstance(_applyResultType);
                _frwApplyResultField.SetValue(reward, applyResult);

                if (snap != null && !snap.IsEmpty)
                {
                    // Resources → BOTH packs (outer gates HasRewards; ApplyResult renders).
                    if (_packAddUnique != null && _resourceUnitCtor2 != null && _resourceTypeEnum2 != null)
                        foreach (var line in snap.Resources)
                        {
                            object unit = _resourceUnitCtor2.Invoke(new[]
                                { Enum.ToObject(_resourceTypeEnum2, line.ResourceType), (object)(float)line.RoundedValue });
                            var outerPack = _frwResourcesField?.GetValue(reward);
                            if (outerPack != null) _packAddUnique.Invoke(outerPack, new[] { unit });
                            var arPack = _arwResourcesField?.GetValue(applyResult);
                            if (arPack != null) _packAddUnique.Invoke(arPack, new[] { unit });
                        }

                    // Items → a FRESH GeoItem per storage (never share one CommonItemData across storages).
                    if (_storageAddItem != null && _geoItemCtor != null)
                        foreach (var line in snap.Items)
                        {
                            var def = DefReflection.GetDefByGuid(line.ItemDefGuid);
                            if (def == null)
                            {
                                Debug.Log("[Multiplayer] outcome reward item dropped (unresolved def " + line.ItemDefGuid + ")");
                                continue;
                            }
                            var outerStorage = _frwItemsField?.GetValue(reward);
                            if (outerStorage != null)
                                _storageAddItem.Invoke(outerStorage, new[] { _geoItemCtor.Invoke(new object[] { def, line.Count, -1, null, -100 }) });
                            var arStorage = _arwItemsField?.GetValue(applyResult);
                            if (arStorage != null)
                                _storageAddItem.Invoke(arStorage, new[] { _geoItemCtor.Invoke(new object[] { def, line.Count, -1, null, -100 }) });
                        }

                    if (snap.FactionSkillPoints != 0)
                    {
                        try { _frwSkillField?.SetValue(reward, snap.FactionSkillPoints); } catch { }
                        try { _arwSkillField?.SetValue(applyResult, snap.FactionSkillPoints); } catch { }
                    }
                }

                _missionRewardProp.GetSetMethod(true).Invoke(mission, new[] { reward });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] ReportModalReflection.StampDisplayReward failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Find the live <c>GeoFaction</c> whose <c>Def.Guid</c> equals <paramref name="guid"/>, or null.</summary>
        private static object ResolveFactionByGuid(GeoRuntime rt, string guid)
        {
            if (string.IsNullOrEmpty(guid) || _factionsField == null || _factionDefProp == null) return null;
            var geo = rt?.GeoLevel();
            if (geo == null) return null;
            try
            {
                if (!(_factionsField.GetValue(geo) is IEnumerable facs)) return null;
                foreach (var f in facs)
                {
                    if (f == null) continue;
                    if (DefReflection.GetGuid(_factionDefProp.GetValue(f, null)) == guid) return f;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ReportModalReflection.ResolveFactionByGuid failed: " + ex.Message); }
            return null;
        }
    }
}
