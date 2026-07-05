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

            switch (variant)
            {
                case ReportModalVariant.SiteOnly:
                    // modalData is a GeoSite (RevealedSites[0]) or null (no revealed site).
                    siteId = ReadSiteId(modalData);
                    break;
                case ReportModalVariant.Research:
                    // modalData is GeoResearchCompleteData → ResearchElement.ResearchID.
                    defId = ReadResearchCompleteId(modalData) ?? "";
                    break;
                case ReportModalVariant.Diplomacy:
                    // modalData is DiplomacyResearchRewardData → Faction.Def.Guid + Researches[].ResearchID + DiplomacyShareLevel.
                    ReadDiplomacy(modalData, out defId, out extraIds, out shareLevel);
                    defId = defId ?? "";
                    extraIds = extraIds ?? new List<string>();
                    break;
                case ReportModalVariant.AmbushBrief:
                    // modalData is the live GeoAmbushMission → Site.SiteId + MissionDef.Guid.
                    ReadAmbushBrief(modalData, out siteId, out defId);
                    break;
                case ReportModalVariant.NullData:
                default:
                    break; // modalType only
            }

            // Whitelisted ModalType values are all 0..40 → fit the byte wire field.
            payload = new ReportModalPayload((byte)modalType, variant, siteId, priority, shareLevel, defId, extraIds);
            return true;
        }

        /// <summary>Host: <c>GeoSite.SiteId</c> off a PandoranRevealResult modalData (a GeoSite, or null → -1).</summary>
        public static int ReadSiteId(object site)
            => site == null ? -1 : GeoSiteReflection.GetSiteId(site);

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
