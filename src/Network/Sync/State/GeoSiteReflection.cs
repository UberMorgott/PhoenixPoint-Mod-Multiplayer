using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the host-authoritative GeoSite IDENTITY state channel (channel #5). The mod has
    /// NO compile-time game references, so every member is resolved by name and cached. The client geoscape
    /// sim is frozen, so existing client GeoSites go STALE (Owner/Type/State/EncounterID/name not updated) →
    /// a geoscape-event modal resolves a stale <c>Context.Site</c> and the native art collection
    /// (derived from <c>Context.Site.Owner</c>/<c>Type</c>) renders the wrong header/backdrop. This bridge
    /// snapshots each CHANGED site's identity host-side and writes it onto the resolved client GeoSite so
    /// <c>EventReflection.ResolveSiteById</c> finds a FRESH site.
    ///
    /// Verified against the decompile (2026-06-17,
    ///   <c>PhoenixPoint.Geoscape.Levels.GeoMap</c> / <c>PhoenixPoint.Geoscape.Entities.GeoSite</c>):
    ///   • map: <c>GeoLevelController.Map</c> (public FIELD) → <c>GeoMap</c>; sites:
    ///     <c>GeoMap.AllSites</c> (public PROPERTY, IEnumerable&lt;GeoSite&gt;) — mirrors EventReflection.
    ///   • aggregate site events on GeoMap (all carry the GeoSite as arg 0; arg count varies):
    ///       <c>SiteAdded</c> / <c>SiteRemoved</c> / <c>SiteStateChanged</c> :
    ///         <c>GeoSite.SiteChangedEventHandler</c> = <c>void(GeoSite)</c> — 1 arg.
    ///       <c>SiteVisibilityChanged</c> / <c>SiteInspectedChanged</c> :
    ///         <c>GeoSite.SiteChangedEventHandler&lt;GeoFaction&gt;</c> = <c>void(GeoSite, GeoFaction)</c> — 2 args.
    ///       <c>SiteOwnerChanged</c> :
    ///         <c>GeoSite.SiteChangedFullEventHandler&lt;GeoFaction&gt;</c> = <c>void(GeoSite, GeoFaction, GeoFaction)</c> — 3 args.
    ///     A single typed handler can't cover all three arities, so we emit a <see cref="DynamicMethod"/>
    ///     adapter per event that captures arg 0 (the GeoSite) and forwards it to an <c>Action&lt;object&gt;</c>
    ///     (the dirty-mark sink extracts SiteId). Mirrors <c>ResearchStateReflection.MakeAdapter</c> but
    ///     captures arg 0 instead of discarding all args.
    ///   • GeoSite fields:
    ///       <c>SiteId</c> : public int FIELD (GeoSite.cs:45, default -1) — resolve by-id.
    ///       <c>Owner</c> : public GeoFaction PROPERTY (setter fires OwnerChanged cascade); for the client
    ///         mirror we write the private <c>_owner</c> backing FIELD (GeoSite.cs:53) DIRECTLY (no cascade —
    ///         pure mirror, the same reward-free discipline as DiplomacyReflection._diplomacy). Owner is
    ///         carried on the wire as <c>GeoFaction.Def.Guid</c> (GeoFaction.Def → GeoFactionDef : BaseDef,
    ///         GeoFaction.cs:121) and resolved on the client by matching a live faction's Def guid via
    ///         <c>GeoLevelController.Factions</c>.
    ///       <c>Type</c> : public GeoSiteType PROPERTY (setter fires TypeChanged); client writes private
    ///         <c>_type</c> backing FIELD (GeoSite.cs:55) DIRECTLY (no cascade).
    ///       <c>State</c> : public GeoSiteState PROPERTY with a PRIVATE setter; client writes private
    ///         <c>_state</c> backing FIELD (GeoSite.cs:59) DIRECTLY.
    ///       <c>SiteName</c> : public LocalizedTextBind PROPERTY; we overwrite its <c>LocalizationKey</c>
    ///         string FIELD (LocalizedTextBind.cs:11) on the live bind (no new-instance churn).
    ///       <c>EncounterID</c> : public string FIELD (GeoSite.cs:51) — written directly.
    ///     Type/State carry the RAW enum integer value (sparse enums); converted back via
    ///     <c>Enum.ToObject(enumType, byteValue)</c>.
    ///
    /// All reflection is null-safe: a missing field/event DEGRADES (logged once) rather than throwing, and a
    /// partially-resolved site skips the missing field/line (never NREs).
    /// </summary>
    public static class GeoSiteReflection
    {
        private static bool _ready;
        private static FieldInfo _mapField;        // GeoLevelController.Map (GeoMap)
        private static PropertyInfo _allSitesProp; // GeoMap.AllSites (IEnumerable<GeoSite>)
        private static FieldInfo _siteIdField;     // GeoSite.SiteId (int)
        private static FieldInfo _ownerBackingField; // GeoSite._owner (GeoFaction)
        private static PropertyInfo _ownerProp;      // GeoSite.Owner (read host-side)
        private static FieldInfo _typeBackingField;  // GeoSite._type (GeoSiteType)
        private static PropertyInfo _typeProp;       // GeoSite.Type (read host-side)
        private static FieldInfo _stateBackingField; // GeoSite._state (GeoSiteState)
        private static PropertyInfo _stateProp;      // GeoSite.State (read host-side)
        private static PropertyInfo _siteNameProp;   // GeoSite.SiteName (LocalizedTextBind)
        private static FieldInfo _locKeyField;       // LocalizedTextBind.LocalizationKey (string)
        private static FieldInfo _encounterIdField;  // GeoSite.EncounterID (string)
        private static PropertyInfo _factionDefProp; // GeoFaction.Def (GeoFactionDef : BaseDef)
        private static FieldInfo _factionsField;     // GeoLevelController.Factions (IEnumerable<GeoFaction>)
        private static Type _siteTypeEnum;           // GeoSiteType (for byte<->enum)
        private static Type _siteStateEnum;          // GeoSiteState (for byte<->enum)
        private static MethodInfo _getInspectedMethod;  // GeoSite.GetInspected(GeoFaction) → bool (per-faction reveal, GeoSite.cs:398)
        private static MethodInfo _setInspectedMethod;  // GeoSite.SetInspected(GeoFaction, bool)      (GeoSite.cs:403)
        private static MethodInfo _getVisibleMethod;    // GeoSite.GetVisible(GeoFaction) → bool  (GeoSite.cs:387)
        private static MethodInfo _setVisibleMethod;    // GeoSite.SetVisible(GeoFaction, bool)   (GeoSite.cs:392)
        private static MethodInfo _getVisitedMethod;    // GeoSite.GetVisited(GeoFaction) → bool  (GeoSite.cs:370)
        private static MethodInfo _getFactionDataMethod;   // GeoSite.GetFactionData(GeoFaction) PRIVATE (GeoSite.cs:420) — client no-event Visited write
        private static FieldInfo _factionDataVisitedField; // GeoSiteFactionData.Visited public field (GeoSiteFactionData.cs:19)
        private static PropertyInfo _viewerFactionProp; // GeoLevelController.ViewerFaction (the display/player faction)
        private static MethodInfo _refreshVisualsMethod; // GeoSite.RefreshVisuals() → _visuals.Refresh()             (GeoSite.cs:915)
        // ─── Case-B inert mirror-site spawn (client) ───
        private static Type _geoSiteType;            // PhoenixPoint.Geoscape.Entities.GeoSite (MakeGenericMethod + AllSites.Add)
        private static MethodInfo _spawnActorGeoSite; // ActorSpawner.SpawnActor<GeoSite>(BaseDef, ActorInstanceData, bool) closed generic
        private static PropertyInfo _siteMappingInstanceProp; // GeoSiteTypeMappingDef.Instance (static)
        private static MethodInfo _getSiteTemplateMethod;     // GeoSiteTypeMappingDef.GetSiteTemplate(GeoSiteType) → ComponentSetDef

        // The aggregate site events on GeoMap, by name. SiteAdded/SiteRemoved are bound for symmetry
        // (Case A only updates existing sites; an add/remove still re-snapshots so an in-play identity flip
        // converges, and a removed/added id absent on the client is harmlessly skipped in Apply).
        // SiteFirstTimeVisited (GeoMap.cs:263, fired by GeoSite.SiteVisited on the FIRST SetVisited(true)) covers
        // a site whose ONLY change is the Visited flip (plain first arrival at an already-inspected site) — without
        // it that flip never dirty-marks and the client's Visited state silently diverges.
        // SiteMissionStarted/Ended/Cancelled (GeoMap.cs:265-269, re-raised from the per-site events at
        // GeoMap.cs:426-429) drive the P1 ActiveMission mirror: a mission set (GeoSite.SetActiveMission →
        // RegisterMission → SiteMissionStarted) or clear (updateable-mission end / cancel) dirty-marks the site so
        // the snapshot carries the fresh mission record / tombstone. All carry the GeoSite as arg 0 (2-arg
        // SiteChangedEventHandler<GeoMission>), which the DynamicMethod adapter already handles.
        // WA-2 HAVEN family (GeoMap.cs:279-283) drives the haven tail: HavenPopulationChanged
        // (GeoHaven.PopulationChangedHandler = void(GeoHaven, int, int)), HavenPopulationZoneAttrition
        // (GeoHaven.ZoneAttritionHandler = void(GeoHaven, GeoHavenZone) — DIRTY TRIGGER only, per-zone health
        // not carried) and HavenInfestationStateChanged (Action<GeoHaven>). Their arg 0 is the GeoHAVEN, not
        // the GeoSite — the dirty sink unwraps it via GeoHaven.Site (GetOwningSiteId).
        // The list itself lives in the PURE codec file (GeoSiteDirtyEvents) so unit tests pin the decision.
        private static readonly string[] SiteEventNames = GeoSiteDirtyEvents.GeoMapEventNames;

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var geoLevelType = geo.GetType();
            _mapField = AccessTools.Field(geoLevelType, "Map");
            _factionsField = AccessTools.Field(geoLevelType, "Factions");
            if (_mapField == null) return;
            var map = _mapField.GetValue(geo);
            if (map == null) return;
            _allSitesProp = AccessTools.Property(map.GetType(), "AllSites");
            if (_allSitesProp == null) return;

            var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (geoSiteType == null) return;
            _geoSiteType = geoSiteType;
            _siteIdField = AccessTools.Field(geoSiteType, "SiteId");
            _ownerBackingField = AccessTools.Field(geoSiteType, "_owner");
            _ownerProp = AccessTools.Property(geoSiteType, "Owner");
            _typeBackingField = AccessTools.Field(geoSiteType, "_type");
            _typeProp = AccessTools.Property(geoSiteType, "Type");
            _stateBackingField = AccessTools.Field(geoSiteType, "_state");
            _stateProp = AccessTools.Property(geoSiteType, "State");
            _siteNameProp = AccessTools.Property(geoSiteType, "SiteName");
            _encounterIdField = AccessTools.Field(geoSiteType, "EncounterID");

            var locBindType = AccessTools.TypeByName("Base.UI.LocalizedTextBind");
            if (locBindType != null) _locKeyField = AccessTools.Field(locBindType, "LocalizationKey");

            _siteTypeEnum = AccessTools.TypeByName("PhoenixPoint.Common.Core.GeoSiteType");
            _siteStateEnum = AccessTools.TypeByName("PhoenixPoint.Common.Core.GeoSiteState");

            // Per-faction site REVEAL (exploration outcome). Best-effort + DELIBERATELY OUTSIDE the _ready gate:
            // a miss here only disables the inspected-flag mirror (the site still mirrors Owner/Type/State/…), it
            // must never break the identity channel. GetInspected/SetInspected each have a single overload, so a
            // name-only lookup binds them; ViewerFaction is the local display faction (identical Phoenix faction on
            // both instances of a shared co-op campaign) that the host reads + the client writes the reveal for.
            _getInspectedMethod = AccessTools.Method(geoSiteType, "GetInspected");
            _setInspectedMethod = AccessTools.Method(geoSiteType, "SetInspected");
            // Explored-state family siblings (same single-overload name-only binding; same best-effort contract).
            // Visible: native SetVisible is display-safe on the client — its VisibleChanged→GeoMap.SiteVisibilityChanged
            // cascade only reaches GeoscapeView's display re-raise (GeoscapeView.cs:1681-1687), no sim.
            _getVisibleMethod = AccessTools.Method(geoSiteType, "GetVisible");
            _setVisibleMethod = AccessTools.Method(geoSiteType, "SetVisible");
            // Visited: native SetVisited is NOT client-safe (SiteVisited→GeoMap.SiteFirstTimeVisited reaches
            // GeoPhoenixFaction.OnSiteFirstTimeVisited → HavenResearchManager + GeoscapeEventSystem — sim!), so the
            // client writes the per-faction GeoSiteFactionData.Visited FIELD directly (no event, pure mirror).
            _getVisitedMethod = AccessTools.Method(geoSiteType, "GetVisited");
            _getFactionDataMethod = AccessTools.Method(geoSiteType, "GetFactionData");
            var factionDataType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Sites.GeoSiteFactionData");
            if (factionDataType != null) _factionDataVisitedField = AccessTools.Field(factionDataType, "Visited");
            _viewerFactionProp = AccessTools.Property(geoLevelType, "ViewerFaction");
            // Native marker repaint (GeoSite.RefreshVisuals → GeoSiteVisualsController.Refresh → _refresh=true →
            // its Unity Update() → RefreshSiteVisuals, which reads GetInspected(Viewer)). We write Owner/Type/State
            // via private backing fields WITHOUT the change cascade (pure mirror), so the site's PropertyChanged —
            // the ONLY thing GeoSiteVisualsController listens to for a repaint — never fires; the client's map icon
            // therefore stayed on its stale (un-inspected) art after a mirrored exploration (S3). Best-effort +
            // OUTSIDE the _ready gate: a miss only disables the forced repaint, never breaks identity mirroring.
            _refreshVisualsMethod = AccessTools.Method(geoSiteType, "RefreshVisuals", Type.EmptyTypes);

            // Case-B inert mirror-site spawn members (client). Best-effort and DELIBERATELY OUTSIDE the _ready
            // gate below: a miss here only disables Case-B spawn (the event degrades to siteless render), it
            // must never break Case-A identity mirroring. ActorSpawner.SpawnActor<T>(BaseDef, ActorInstanceData,
            // bool callEnterPlayOnActor) (Base.Entities/ActorSpawner.cs:12) is the generic spawn; we close it to
            // GeoSite and pass callEnterPlayOnActor:false so DoEnterPlay/RegisterSite/producer-coroutines never
            // fire (pure inert mirror). GeoSiteTypeMappingDef.Instance.GetSiteTemplate(GeoSiteType) → ComponentSetDef
            // (GeoSiteTypeMappingDef.cs:21/33) resolves the prefab template for the spawn.
            var actorSpawnerType = AccessTools.TypeByName("Base.Entities.ActorSpawner");
            if (actorSpawnerType != null)
            {
                var spawnActorOpen = actorSpawnerType.GetMethod("SpawnActor",
                    BindingFlags.Public | BindingFlags.Static);
                if (spawnActorOpen != null && spawnActorOpen.IsGenericMethodDefinition)
                {
                    try { _spawnActorGeoSite = spawnActorOpen.MakeGenericMethod(_geoSiteType); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection: SpawnActor<GeoSite> bind failed (Case-B spawn disabled): " + ex.Message); }
                }
            }
            var siteMappingDefType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoSiteTypeMappingDef");
            if (siteMappingDefType != null)
            {
                _siteMappingInstanceProp = AccessTools.Property(siteMappingDefType, "Instance");
                if (_siteTypeEnum != null)
                    _getSiteTemplateMethod = AccessTools.Method(siteMappingDefType, "GetSiteTemplate", new[] { _siteTypeEnum });
            }

            // GeoFaction.Def — resolved off a live faction (mirrors DiplomacyReflection). Best-effort.
            if (_factionsField != null && _factionsField.GetValue(geo) is IEnumerable facs)
                foreach (var f in facs)
                {
                    if (f == null) continue;
                    _factionDefProp = AccessTools.Property(f.GetType(), "Def");
                    break;
                }

            // Core gate: id resolution + the identity writes the channel needs. Owner-by-guid + name are
            // best-effort (a faction-less / nameless site still mirrors Type/State/EncounterID).
            _ready = _siteIdField != null && _allSitesProp != null
                     && _stateBackingField != null && _typeBackingField != null
                     && _ownerBackingField != null && _encounterIdField != null;
        }

        /// <summary>The live <c>GeoMap</c>, or null.</summary>
        private static object GetMap(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null || _mapField == null) return null;
            try { return _mapField.GetValue(geo); }
            catch { return null; }
        }

        /// <summary>The live <c>GeoMap</c> (channel rebind guard). Ensures binding first, then returns it.</summary>
        public static object GetMapPublic(GeoRuntime rt)
        {
            try { Ensure(rt); } catch { /* best-effort */ }
            return GetMap(rt);
        }

        /// <summary>The live geoscape VIEWER faction (the local display/player faction —
        /// <c>GeoLevelController.ViewerFaction</c>), or null. The site "inspected" reveal is per-faction; the host
        /// reads it for this faction and the client writes it for the SAME faction (both instances of a shared
        /// co-op campaign view as the same Phoenix faction), so the client's map reflects the host's exploration.</summary>
        private static object GetViewerFaction(GeoRuntime rt)
        {
            try
            {
                var geo = rt?.GeoLevel();
                if (geo != null && _viewerFactionProp != null) return _viewerFactionProp.GetValue(geo, null);
            }
            catch { }
            return null;
        }

        /// <summary>Force the native map-marker repaint for a live <c>GeoSite</c> after a pure-mirror identity write.
        /// <c>GeoSite.RefreshVisuals()</c> flips <c>GeoSiteVisualsController._refresh</c> so its (frame-driven, NOT
        /// sim-frozen) <c>Update()</c> re-renders the icon from <c>GetInspected(Viewer)</c> — the repaint the bypassed
        /// <c>PropertyChanged</c> would normally trigger. Best-effort no-op if unbound; never throws.</summary>
        private static void RefreshSiteVisuals(object site)
        {
            if (site == null || _refreshVisualsMethod == null) return;
            try { _refreshVisualsMethod.Invoke(site, null); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.RefreshSiteVisuals failed: " + ex.Message); }
        }

        /// <summary>Resolve a live <c>GeoSite</c> by its <c>SiteId</c> via <c>GeoMap.AllSites</c> (mirrors
        /// <c>EventReflection.ResolveSiteById</c>), or null if absent (Case B — never created here).</summary>
        public static object ResolveSiteById(GeoRuntime rt, int siteId)
        {
            try
            {
                if (siteId < 0 || _allSitesProp == null || _siteIdField == null) return null;
                var map = GetMap(rt);
                if (map == null) return null;
                if (!(_allSitesProp.GetValue(map, null) is IEnumerable sites)) return null;
                foreach (var s in sites)
                {
                    if (s == null) continue;
                    var id = _siteIdField.GetValue(s);
                    if (id is int i && i == siteId) return s;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Host: read each dirty site's identity into a DTO list. A dirty id absent from <c>AllSites</c>
        /// (removed) is skipped — only live sites are mirrored.
        /// </summary>
        public static List<GeoSiteState> SnapshotDirty(GeoRuntime rt, IEnumerable<int> dirtySiteIds)
        {
            var list = new List<GeoSiteState>();
            try
            {
                Ensure(rt);
                if (!_ready || dirtySiteIds == null) return list;
                object viewerFaction = GetViewerFaction(rt);   // resolve once — the per-faction reveal reference
                foreach (var id in dirtySiteIds)
                {
                    var site = ResolveSiteById(rt, id);
                    if (site == null) continue; // removed / not yet present → nothing to mirror
                    try { list.Add(ReadSite(site, id, viewerFaction)); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.SnapshotDirty read '" + id + "' failed (skipped): " + ex.Message); }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.SnapshotDirty failed: " + ex.Message); }
            return list;
        }

        /// <summary>
        /// Host: build the IDENTITY snapshot (Owner/Type/State/SiteName-locKey/EncounterID) for a LIVE GeoSite,
        /// or null if it can't be read. Used by the event-raise broadcast to carry an absent-site fallback.
        /// </summary>
        public static GeoSiteState? BuildIdentity(GeoRuntime rt, object site)
        {
            try
            {
                if (site == null) return null;
                int siteId = GetSiteId(site);
                if (siteId < 0) return null;
                var list = SnapshotDirty(rt, new[] { siteId });
                if (list != null && list.Count > 0) return list[0];
                return null;
            }
            catch { return null; }
        }

        private static GeoSiteState ReadSite(object site, int siteId, object viewerFaction)
        {
            // Owner → Def guid (best-effort).
            string ownerGuid = "";
            try
            {
                var owner = _ownerProp != null ? _ownerProp.GetValue(site, null) : _ownerBackingField.GetValue(site);
                if (owner != null && _factionDefProp != null)
                    ownerGuid = DefReflection.GetGuid(_factionDefProp.GetValue(owner, null)) ?? "";
            }
            catch { ownerGuid = ""; }

            byte siteType = 0;
            try
            {
                var t = _typeProp != null ? _typeProp.GetValue(site, null) : _typeBackingField.GetValue(site);
                siteType = (byte)Convert.ToInt32(t);
            }
            catch { siteType = 0; }

            byte state = 0;
            try
            {
                var st = _stateProp != null ? _stateProp.GetValue(site, null) : _stateBackingField.GetValue(site);
                state = (byte)Convert.ToInt32(st);
            }
            catch { state = 0; }

            string siteName = "";
            try
            {
                var bind = _siteNameProp != null ? _siteNameProp.GetValue(site, null) : null;
                if (bind != null && _locKeyField != null)
                    siteName = _locKeyField.GetValue(bind) as string ?? "";
            }
            catch { siteName = ""; }

            string encounterId = "";
            try { encounterId = _encounterIdField.GetValue(site) as string ?? ""; }
            catch { encounterId = ""; }

            // Per-faction explored-state family (exploration outcome). Best-effort: unbound method / null viewer →
            // false (the site still mirrors its identity, the flag simply doesn't carry).
            bool inspected = false;
            try
            {
                if (_getInspectedMethod != null && viewerFaction != null)
                    inspected = (bool)_getInspectedMethod.Invoke(site, new[] { viewerFaction });
            }
            catch { inspected = false; }

            bool visible = false;
            try
            {
                if (_getVisibleMethod != null && viewerFaction != null)
                    visible = (bool)_getVisibleMethod.Invoke(site, new[] { viewerFaction });
            }
            catch { visible = false; }

            bool visited = false;
            try
            {
                if (_getVisitedMethod != null && viewerFaction != null)
                    visited = (bool)_getVisitedMethod.Invoke(site, new[] { viewerFaction });
            }
            catch { visited = false; }

            // P1 ActiveMission mirror: best-effort — a read failure carries a null record (tombstone), which is
            // also the honest degrade (the client then clears its mirror and the display path degrades).
            GeoMissionRecord mission = null;
            try { mission = ReadMissionRecord(site); }
            catch { mission = null; }

            // WA-2 tails: best-effort — a miss carries null (= not carried; the identity still mirrors).
            GeoHavenTail haven = null;
            try { haven = ReadHavenTail(site); }
            catch { haven = null; }
            GeoAlienBaseTail alienBase = null;
            try { alienBase = ReadAlienBaseTail(site); }
            catch { alienBase = null; }
            GeoExcavationTail excavation = null;
            try { excavation = ReadExcavationTail(GeoRuntime.Instance, site); }
            catch { excavation = null; }
            GeoAttackTail attack = null;
            try { attack = ReadAttackTail(GeoRuntime.Instance, site); }
            catch { attack = null; }

            return new GeoSiteState(siteId, ownerGuid, siteType, state, siteName, encounterId, inspected, visible, visited,
                mission, haven, alienBase, excavation, attack);
        }

        /// <summary>
        /// Client: write <paramref name="dto"/>'s identity onto a RESOLVED live <c>GeoSite</c>. Each field is
        /// written via its private backing field (no change-event cascade — pure mirror) and individually
        /// guarded so a partially-resolved site never NREs the rest. Owner is resolved from its Def guid via
        /// the live faction list; a guid that resolves to no faction leaves the existing owner untouched.
        /// </summary>
        public static void ApplyIdentity(GeoRuntime rt, object site, GeoSiteState dto)
        {
            if (site == null) return;
            try
            {
                Ensure(rt);
                if (!_ready) return;

                // Owner (resolve faction by Def guid; skip if unresolved — never null out an owner).
                if (!string.IsNullOrEmpty(dto.OwnerFactionDefGuid) && _ownerBackingField != null)
                {
                    try
                    {
                        var fac = ResolveFactionByGuid(rt, dto.OwnerFactionDefGuid);
                        if (fac != null) _ownerBackingField.SetValue(site, fac);
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyIdentity owner failed (skipped): " + ex.Message); }
                }

                // Type (raw enum value → enum).
                if (_typeBackingField != null && _siteTypeEnum != null)
                {
                    try { _typeBackingField.SetValue(site, Enum.ToObject(_siteTypeEnum, dto.SiteType)); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyIdentity type failed (skipped): " + ex.Message); }
                }

                // State (private setter — write the backing field directly).
                if (_stateBackingField != null && _siteStateEnum != null)
                {
                    try { _stateBackingField.SetValue(site, Enum.ToObject(_siteStateEnum, dto.State)); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyIdentity state failed (skipped): " + ex.Message); }
                }

                // SiteName loc-key (overwrite the live bind's LocalizationKey; skip if no key carried).
                if (!string.IsNullOrEmpty(dto.SiteName) && _siteNameProp != null && _locKeyField != null)
                {
                    try
                    {
                        var bind = _siteNameProp.GetValue(site, null);
                        if (bind != null) _locKeyField.SetValue(bind, dto.SiteName);
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyIdentity name failed (skipped): " + ex.Message); }
                }

                // EncounterID (public field; always written — "" clears a consumed encounter).
                if (_encounterIdField != null)
                {
                    try { _encounterIdField.SetValue(site, dto.EncounterID ?? ""); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyIdentity encounter failed (skipped): " + ex.Message); }
                }

                // Per-faction EXPLORED-STATE FAMILY (exploration outcome) — Visible + Inspected + Visited.
                // Visible FIRST (an invisible site renders no marker at all, GeoSiteVisualsController.cs:195):
                // native SetVisible(ViewerFaction, value) is display-safe on the client (VisibleChanged →
                // GeoMap.SiteVisibilityChanged → GeoscapeView display re-raise only). This is what makes the POIs
                // the host's exploration REVEALED AROUND the explored site appear on the sim-frozen client.
                var viewer = GetViewerFaction(rt);
                if (_setVisibleMethod != null && viewer != null)
                {
                    try { _setVisibleMethod.Invoke(site, new object[] { viewer, dto.Visible }); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyIdentity visible failed (skipped): " + ex.Message); }
                }

                // Inspected: drive the NATIVE SetInspected(ViewerFaction, value) so the site flips to inspected on
                // the sim-frozen client exactly as the host did — this is what makes an exploration (host-own or
                // client-relayed) visibly reveal the site. SetInspected only sets the flag + fires the display-only
                // InspectedChanged event (no sim advance, no reward cascade); the client channel is host-attach-only
                // so it never re-broadcasts. Host-authoritative (matches the host's value).
                if (_setInspectedMethod != null && viewer != null)
                {
                    try { _setInspectedMethod.Invoke(site, new object[] { viewer, dto.Inspected }); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyIdentity inspected failed (skipped): " + ex.Message); }
                }

                // Visited: native SetVisited would fire SiteVisited → GeoMap.SiteFirstTimeVisited →
                // GeoPhoenixFaction.OnSiteFirstTimeVisited (HavenResearchManager.UpdateHavenResearch,
                // GeoPhoenixFaction.cs:1163-1170) + GeoscapeEventSystem event triggers — SIM on a frozen client. So
                // write the per-faction GeoSiteFactionData.Visited FIELD directly (no event — the same no-cascade
                // discipline as the Owner/Type/State backing-field writes above).
                if (_getFactionDataMethod != null && _factionDataVisitedField != null && viewer != null)
                {
                    try
                    {
                        var factionData = _getFactionDataMethod.Invoke(site, new[] { viewer });
                        if (factionData != null) _factionDataVisitedField.SetValue(factionData, dto.Visited);
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyIdentity visited failed (skipped): " + ex.Message); }
                }

                // Owner/Type/State were written via backing fields (no change cascade) and SetInspected fires
                // PropertyChanged ONLY on a genuine flip — so force the native marker repaint unconditionally here
                // to render the freshly-mirrored inspected/identity (S3: client POI stuck on un-inspected art).
                RefreshSiteVisuals(site);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyIdentity failed: " + ex.Message); }
        }

        // ─── P1 ActiveMission mirror (host read + client attach) ─────────────────────────────────────────
        // Own lazy gate (EnsureMissionMirror), separate from Ensure: a miss here degrades ONLY the mission
        // mirror (identity/explored-state keep flowing); it can never break channel #5.
        //
        // Decompile-verified 2026-07-05:
        //   • GeoSite.ActiveMission — public auto-property { get; set; } (GeoSite.cs:101). The client attach
        //     writes it DIRECTLY (pure mirror): native SetActiveMission (GeoSite.cs:776) throws on an occupied
        //     site AND runs RegisterMission → hooks OnMissionActivated→LaunchTacticalGame handlers + fires
        //     SiteMissionStarted → GeoscapeView.OnSiteMissionStarted → ShowMissionBriefing → OpenModalPersistent
        //     — i.e. the STATE channel would open UI, violating the cross-rail invariant (spec §4: state
        //     channels never open UI; the 0x69 report rail is the ONLY display driver, and it arms the lock).
        //   • Client rebuild ctors — the pure serializer-support ctors (field assignment only, no live-object
        //     params, no sim writes):
        //       GeoHavenDefenseMission(TacMissionTypeDef, GeoSite, PPFactionDef)          private, :151
        //       GeoAlienBaseMission(TacMissionTypeDef, GeoSite, MissionParams)            public,  :20
        //       GeoAlienBaseAssaultMission(TacMissionTypeDef, GeoSite, PPFactionDef)      private, :51
        //       GeoPhoenixBaseDefenseMission(TacMissionTypeDef, GeoSite)                  private, :63
        //         (the public ctor runs PhoenixBase.ResetBaseAssaultProtection() — a sim write — so the
        //          private one + direct field stamps is the pure path)
        //       GeoPhoenixBaseInfestationMission(TacMissionTypeDef, GeoSite, GeoSquad, MissionParams) public, :20
        //       GeoInfestationCleanseMission(GeoSite, TacMissionTypeDef, MissionParams)   public,  :14
        //       GeoScavengingMission(GeoSite, TacMissionTypeDef, MissionParams)           public,  :23
        //       GeoAmbushMission(GeoSite, TacMissionTypeDef, MissionParams)               public,  :24
        //       GeoAncientSiteMission(GeoSite, TacMissionTypeDef)                         public,  :30
        //   • Runtime bits the brief binds read (stamped from the DTO after construction):
        //       GeoUpdateableMission.AttackerFaction — readonly PPFactionDef field (set via FieldInfo);
        //       GeoHavenDefenseMission._attackedZoneDef (GeoHavenZoneDef), AttackerDeployment/DefenderDeployment
        //         (auto-props, private set → PropertySetter), _attackingSites (IList<GeoSite>);
        //       GeoAlienBaseAssaultMission._attackerDeployment/_defenderDeployment (int fields);
        //       GeoPhoenixBaseDefenseMission._enemyFaction (PPFactionDef), _attackingSites (List<GeoSite>).
        //     The mission is NEVER StartUpdating'd and never registered (no producers, no updateable ticking on
        //     the frozen client sim) — it exists solely so native brief binds resolve site.ActiveMission.

        private static bool _missionEnsured;
        private static PropertyInfo _activeMissionProp;      // GeoSite.ActiveMission (public auto-prop)
        private static PropertyInfo _geoMissionDefProp;      // GeoMission.MissionDef
        private static FieldInfo _updAttackerFactionField;   // GeoUpdateableMission.AttackerFaction (readonly PPFactionDef)
        private static FieldInfo _updMissionUpdatedField;    // GeoUpdateableMission.OnMissionUpdated event backing delegate
        private static Type _havenDefT, _alienBaseT, _alienAssaultT, _pxDefT, _pxInfestT, _cleanseT, _scavT, _ambushT, _ancientT;
        private static ConstructorInfo _havenDefCtor;        // (TacMissionTypeDef, GeoSite, PPFactionDef) private
        private static ConstructorInfo _alienBaseCtor;       // (TacMissionTypeDef, GeoSite, MissionParams) public
        private static ConstructorInfo _alienAssaultCtor;    // (TacMissionTypeDef, GeoSite, PPFactionDef) private
        private static ConstructorInfo _pxDefCtor;           // (TacMissionTypeDef, GeoSite) private
        private static ConstructorInfo _pxInfestCtor;        // (TacMissionTypeDef, GeoSite, GeoSquad, MissionParams) public
        private static ConstructorInfo _cleanseCtor;         // (GeoSite, TacMissionTypeDef, MissionParams) public
        private static ConstructorInfo _scavCtor;            // (GeoSite, TacMissionTypeDef, MissionParams) public
        private static ConstructorInfo _ambushCtor2;         // (GeoSite, TacMissionTypeDef, MissionParams) public
        private static ConstructorInfo _ancientCtor2;        // (GeoSite, TacMissionTypeDef) public
        private static FieldInfo _havenZoneDefField;         // GeoHavenDefenseMission._attackedZoneDef
        private static MethodInfo _havenAtkDeploySetter;     // GeoHavenDefenseMission.AttackerDeployment private set
        private static MethodInfo _havenDefDeploySetter;     // GeoHavenDefenseMission.DefenderDeployment private set
        private static PropertyInfo _havenAtkDeployProp;     // (host read)
        private static PropertyInfo _havenDefDeployProp;     // (host read)
        private static FieldInfo _havenAttackingSitesField;  // GeoHavenDefenseMission._attackingSites (IList<GeoSite>)
        private static FieldInfo _assaultAtkDeployField;     // GeoAlienBaseAssaultMission._attackerDeployment
        private static FieldInfo _assaultDefDeployField;     // GeoAlienBaseAssaultMission._defenderDeployment
        private static FieldInfo _pxEnemyFactionField;       // GeoPhoenixBaseDefenseMission._enemyFaction
        private static FieldInfo _pxAttackingSitesField;     // GeoPhoenixBaseDefenseMission._attackingSites (List<GeoSite>)
        private static Type _geoSiteListType;                // List<GeoSite> (for _attackingSites fills)

        private static void EnsureMissionMirror()
        {
            if (_missionEnsured) return;
            _missionEnsured = true;   // one attempt; every user null-guards
            try
            {
                var geoSiteT = _geoSiteType ?? AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
                var geoMissionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoMission");
                var updT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoUpdateableMission");
                var tacDefT = AccessTools.TypeByName("PhoenixPoint.Common.Levels.Missions.TacMissionTypeDef");
                // Decompile-verified namespaces (PPFactionDef.cs:8 = PhoenixPoint.Common.Core; GeoSquad lives in
                // Geoscape.Entities). The previous names ("Common.Entities.PPFactionDef", "Geoscape.Core.GeoSquad")
                // resolved NULL → every member behind the `ppFacDefT != null` guards silently stayed null → the
                // host flushed haven-defense records as atk=0 def=0 zone= sites=0 and the client could neither
                // build nor stamp them (contested-site progress-circle divergence, soak 2026-07-05).
                var ppFacDefT = AccessTools.TypeByName("PhoenixPoint.Common.Core.PPFactionDef");
                var squadT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSquad");
                if (ppFacDefT == null || squadT == null)
                    Debug.LogWarning("[Multiplayer] EnsureMissionMirror type miss: ppFacDef=" + (ppFacDefT != null)
                                     + " squad=" + (squadT != null) + " — affected mission-class mirrors degrade");
                if (geoSiteT == null || geoMissionT == null || tacDefT == null) return;
                var missionParamsT = AccessTools.Inner(geoMissionT, "MissionParams");

                _activeMissionProp = AccessTools.Property(geoSiteT, "ActiveMission");
                _geoMissionDefProp = AccessTools.Property(geoMissionT, "MissionDef");
                if (updT != null) _updAttackerFactionField = AccessTools.Field(updT, "AttackerFaction");
                // Field-like event's compiler-generated backing delegate (same name as the event) — raised
                // after a value-only refresh so the contested-site pie repaints (see RaiseMissionUpdated).
                if (updT != null) _updMissionUpdatedField = AccessTools.Field(updT, "OnMissionUpdated");

                _havenDefT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoHavenDefenseMission");
                _alienBaseT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoAlienBaseMission");
                _alienAssaultT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Missions.GeoAlienBaseAssaultMission");
                _pxDefT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoPhoenixBaseDefenseMission");
                _pxInfestT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Missions.GeoPhoenixBaseInfestationMission");
                _cleanseT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Missions.GeoInfestationCleanseMission");
                _scavT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoScavengingMission");
                _ambushT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Missions.GeoAmbushMission");
                _ancientT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Missions.GeoAncientSiteMission");

                // EXACT param matches everywhere (harmony-accesstools-exact-param-match): the classes carry
                // [Obsolete]/serializer overloads that must not be picked.
                if (_havenDefT != null && ppFacDefT != null)
                {
                    _havenDefCtor = AccessTools.Constructor(_havenDefT, new[] { tacDefT, geoSiteT, ppFacDefT });
                    _havenZoneDefField = AccessTools.Field(_havenDefT, "_attackedZoneDef");
                    _havenAtkDeployProp = AccessTools.Property(_havenDefT, "AttackerDeployment");
                    _havenDefDeployProp = AccessTools.Property(_havenDefT, "DefenderDeployment");
                    _havenAtkDeploySetter = AccessTools.PropertySetter(_havenDefT, "AttackerDeployment");
                    _havenDefDeploySetter = AccessTools.PropertySetter(_havenDefT, "DefenderDeployment");
                    _havenAttackingSitesField = AccessTools.Field(_havenDefT, "_attackingSites");
                }
                if (_alienBaseT != null && missionParamsT != null)
                    _alienBaseCtor = AccessTools.Constructor(_alienBaseT, new[] { tacDefT, geoSiteT, missionParamsT });
                if (_alienAssaultT != null && ppFacDefT != null)
                {
                    _alienAssaultCtor = AccessTools.Constructor(_alienAssaultT, new[] { tacDefT, geoSiteT, ppFacDefT });
                    _assaultAtkDeployField = AccessTools.Field(_alienAssaultT, "_attackerDeployment");
                    _assaultDefDeployField = AccessTools.Field(_alienAssaultT, "_defenderDeployment");
                }
                if (_pxDefT != null)
                {
                    _pxDefCtor = AccessTools.Constructor(_pxDefT, new[] { tacDefT, geoSiteT });
                    _pxEnemyFactionField = AccessTools.Field(_pxDefT, "_enemyFaction");
                    _pxAttackingSitesField = AccessTools.Field(_pxDefT, "_attackingSites");
                }
                if (_pxInfestT != null && squadT != null && missionParamsT != null)
                    _pxInfestCtor = AccessTools.Constructor(_pxInfestT, new[] { tacDefT, geoSiteT, squadT, missionParamsT });
                if (_cleanseT != null && missionParamsT != null)
                    _cleanseCtor = AccessTools.Constructor(_cleanseT, new[] { geoSiteT, tacDefT, missionParamsT });
                if (_scavT != null && missionParamsT != null)
                    _scavCtor = AccessTools.Constructor(_scavT, new[] { geoSiteT, tacDefT, missionParamsT });
                if (_ambushT != null && missionParamsT != null)
                    _ambushCtor2 = AccessTools.Constructor(_ambushT, new[] { geoSiteT, tacDefT, missionParamsT });
                if (_ancientT != null)
                    _ancientCtor2 = AccessTools.Constructor(_ancientT, new[] { geoSiteT, tacDefT });

                _geoSiteListType = typeof(List<>).MakeGenericType(geoSiteT);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.EnsureMissionMirror failed (mission mirror disabled): " + ex.Message); }
        }

        /// <summary>The live <c>GeoSite.ActiveMission</c> of a resolved site, or null (no mission / unbound).</summary>
        public static object GetActiveMission(object site)
        {
            try
            {
                EnsureMissionMirror();
                if (site == null || _activeMissionProp == null) return null;
                return _activeMissionProp.GetValue(site, null);
            }
            catch { return null; }
        }

        /// <summary>The wire class discriminator for a live GeoMission instance (subclasses of a mapped class
        /// classify as that class — the same `is`-semantics GetMissionBriefModal uses), or
        /// <see cref="GeoMissionRecord.Unknown"/> for the unmapped fallback-34 family, or 0 for null.</summary>
        public static byte ClassifyMission(object mission)
        {
            if (mission == null) return 0;
            EnsureMissionMirror();
            if (_havenDefT != null && _havenDefT.IsInstanceOfType(mission)) return GeoMissionRecord.HavenDefense;
            if (_alienAssaultT != null && _alienAssaultT.IsInstanceOfType(mission)) return GeoMissionRecord.AlienBaseAssault;
            if (_alienBaseT != null && _alienBaseT.IsInstanceOfType(mission)) return GeoMissionRecord.AlienBase;
            if (_pxDefT != null && _pxDefT.IsInstanceOfType(mission)) return GeoMissionRecord.PhoenixBaseDefense;
            if (_pxInfestT != null && _pxInfestT.IsInstanceOfType(mission)) return GeoMissionRecord.PhoenixBaseInfestation;
            if (_cleanseT != null && _cleanseT.IsInstanceOfType(mission)) return GeoMissionRecord.InfestationCleanse;
            if (_scavT != null && _scavT.IsInstanceOfType(mission)) return GeoMissionRecord.Scavenging;
            if (_ambushT != null && _ambushT.IsInstanceOfType(mission)) return GeoMissionRecord.Ambush;
            if (_ancientT != null && _ancientT.IsInstanceOfType(mission)) return GeoMissionRecord.AncientSite;
            return GeoMissionRecord.Unknown;
        }

        /// <summary>The mirrored mission's <c>MissionDef.Guid</c>, or "" on any miss.</summary>
        public static string GetMissionDefGuid(object mission)
        {
            try
            {
                EnsureMissionMirror();
                if (mission == null || _geoMissionDefProp == null) return "";
                return DefReflection.GetGuid(_geoMissionDefProp.GetValue(mission, null)) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// HOST: read <c>site.ActiveMission</c> into a wire <see cref="GeoMissionRecord"/>, or null (= tombstone)
        /// when the site has no active mission. An unmapped class still carries a record
        /// (<see cref="GeoMissionRecord.Unknown"/> + def guid) so the client knows the host HAS a mission there
        /// (it clears any stale mirror and the display path degrades honestly).
        /// </summary>
        public static GeoMissionRecord ReadMissionRecord(object site)
        {
            try
            {
                var mission = GetActiveMission(site);
                if (mission == null) return null;
                byte cls = ClassifyMission(mission);
                if (cls == 0) return null;
                string defGuid = GetMissionDefGuid(mission);
                string attackerGuid = "";
                int atk = 0, def = 0;
                string zoneGuid = "";
                int[] siteIds = null;

                switch (cls)
                {
                    case GeoMissionRecord.HavenDefense:
                        attackerGuid = ReadPPFactionGuid(_updAttackerFactionField?.GetValue(mission));
                        try { atk = Convert.ToInt32(_havenAtkDeployProp?.GetValue(mission, null) ?? 0); } catch { }
                        try { def = Convert.ToInt32(_havenDefDeployProp?.GetValue(mission, null) ?? 0); } catch { }
                        try { zoneGuid = DefReflection.GetGuid(_havenZoneDefField?.GetValue(mission)) ?? ""; } catch { }
                        siteIds = ReadSiteIds(_havenAttackingSitesField?.GetValue(mission));
                        break;
                    case GeoMissionRecord.AlienBaseAssault:
                        attackerGuid = ReadPPFactionGuid(_updAttackerFactionField?.GetValue(mission));
                        try { atk = Convert.ToInt32(_assaultAtkDeployField?.GetValue(mission) ?? 0); } catch { }
                        try { def = Convert.ToInt32(_assaultDefDeployField?.GetValue(mission) ?? 0); } catch { }
                        break;
                    case GeoMissionRecord.PhoenixBaseDefense:
                        try { attackerGuid = DefReflection.GetGuid(_pxEnemyFactionField?.GetValue(mission)) ?? ""; } catch { }
                        siteIds = ReadSiteIds(_pxAttackingSitesField?.GetValue(mission));
                        break;
                }
                return new GeoMissionRecord(cls, defGuid, attackerGuid, atk, def, zoneGuid, siteIds);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoSiteReflection.ReadMissionRecord failed: " + ex.Message);
                return null;
            }
        }

        private static string ReadPPFactionGuid(object ppFactionDef)
        {
            try { return DefReflection.GetGuid(ppFactionDef) ?? ""; }
            catch { return ""; }
        }

        private static int[] ReadSiteIds(object siteList)
        {
            try
            {
                if (!(siteList is IEnumerable sites)) return null;
                var ids = new List<int>();
                foreach (var s in sites)
                {
                    int id = GetSiteId(s);
                    if (id >= 0) ids.Add(id);
                }
                return ids.ToArray();
            }
            catch { return null; }
        }

        /// <summary>
        /// CLIENT: mirror <paramref name="rec"/> onto the resolved site's <c>ActiveMission</c> — the P1 attach.
        ///   • null record / Unknown class → TOMBSTONE: clear <c>ActiveMission</c> (direct property write, no
        ///     cascade). Unknown clears too — a stale mirror of a replaced mission must never feed a brief bind.
        ///   • same class + same def guid already attached → refresh the mutable runtime bits (haven-defense /
        ///     assault deployments tick down hourly on the host) and keep the instance.
        ///   • else → construct the class-exact mission via its pure ctor, stamp the DTO's runtime bits, and
        ///     write <c>site.ActiveMission</c> DIRECTLY (never SetActiveMission — no RegisterMission handlers, no
        ///     SiteMissionStarted cascade, no UI: the 0x69 report rail is the only display driver, spec §4).
        /// Best-effort: any failure leaves the site untouched (the display path then degrades honestly).
        /// </summary>
        public static void ApplyMission(GeoRuntime rt, object site, GeoMissionRecord rec)
        {
            if (site == null) return;
            try
            {
                EnsureMissionMirror();
                if (_activeMissionProp == null || !_activeMissionProp.CanWrite) return;

                object current = GetActiveMission(site);
                switch (MissionMirrorDecision.Decide(current != null,
                            current != null ? ClassifyMission(current) : (byte)0,
                            current != null ? GetMissionDefGuid(current) : null, rec))
                {
                    case MissionMirrorAction.None:
                        return;
                    case MissionMirrorAction.Clear:
                        _activeMissionProp.SetValue(site, null, null);
                        RefreshSiteVisuals(site);   // frame-driven RefreshMissionVisuals destroys the pie
                        Debug.Log("[Multiplayer] GeoSiteReflection.ApplyMission site=" + GetSiteId(site)
                                  + " → cleared (tombstone" + (rec == null ? "" : ", unknown host class") + ")");
                        return;
                    case MissionMirrorAction.KeepRefresh:
                        // Same mission already mirrored → refresh the mutable bits only (keep the instance the
                        // queued brief may already reference), then REPAINT: the client sim is frozen, so the
                        // mirrored mission never ticks and GeoUpdatedableMissionVisualsController's only repaint
                        // triggers (SetMission / OnMissionUpdated) never fire on their own — without the raise
                        // the contested-site progress circle froze at its attach/join-snapshot value forever.
                        StampMissionRuntimeBits(rt, current, rec);
                        RaiseMissionUpdated(current);   // pie controller RefreshVisuals (display-only event)
                        RefreshSiteVisuals(site);       // covers a not-yet-instantiated pie (visibility flip)
                        Debug.Log("[Multiplayer] GeoSiteReflection.ApplyMission site=" + GetSiteId(site)
                                  + " → refreshed (class=" + rec.MissionClass + " atk=" + rec.AttackerDeployment
                                  + " def=" + rec.DefenderDeployment + ")");
                        return;
                    case MissionMirrorAction.Rebuild:
                        var mission = BuildMissionForRecord(rt, site, rec);
                        if (mission == null) return;   // unresolved def/ctor → leave untouched (display degrades)
                        StampMissionRuntimeBits(rt, mission, rec);
                        _activeMissionProp.SetValue(site, mission, null);
                        RefreshSiteVisuals(site);       // instantiates the pie prefab + SetMission → first paint
                        Debug.Log("[Multiplayer] GeoSiteReflection.ApplyMission site=" + GetSiteId(site)
                                  + " class=" + rec.MissionClass + " def=" + rec.MissionDefGuid + " → attached (pure mirror)");
                        return;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyMission failed: " + ex.Message); }
        }

        /// <summary>Raise the mission's <c>OnMissionUpdated</c> (field-like event backing delegate) after a
        /// value-only stamp. Vanilla's SOLE subscriber is <c>GeoUpdatedableMissionVisualsController</c>
        /// (GeoUpdatedableMissionVisualsController.cs:30/55-58) whose handler just re-sets the pie material's
        /// <c>_Progress</c> from <c>MissionProgress</c> — display-only, no sim cascade (TFTV never subscribes,
        /// grep 2026-07-05), frozen-sim safe. Best-effort no-op if unbound; never throws.</summary>
        private static void RaiseMissionUpdated(object mission)
        {
            if (mission == null || _updMissionUpdatedField == null) return;
            try { (_updMissionUpdatedField.GetValue(mission) as Delegate)?.DynamicInvoke(mission); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.RaiseMissionUpdated failed: " + ex.Message); }
        }

        /// <summary>Construct the class-exact display mission via its pure ctor (see the region note), or null.</summary>
        private static object BuildMissionForRecord(GeoRuntime rt, object site, GeoMissionRecord rec)
        {
            var missionDef = DefReflection.GetDefByGuid(rec.MissionDefGuid);
            if (missionDef == null)
            {
                Debug.LogWarning("[Multiplayer] GeoSiteReflection.BuildMissionForRecord: missionDef guid '"
                                 + rec.MissionDefGuid + "' did not resolve (skipped)");
                return null;
            }
            object attackerDef = string.IsNullOrEmpty(rec.AttackerFactionDefGuid)
                ? null : DefReflection.GetDefByGuid(rec.AttackerFactionDefGuid);
            try
            {
                switch (rec.MissionClass)
                {
                    case GeoMissionRecord.HavenDefense:
                        return _havenDefCtor?.Invoke(new[] { missionDef, site, attackerDef });
                    case GeoMissionRecord.AlienBase:
                        return _alienBaseCtor?.Invoke(new[] { missionDef, site, null });
                    case GeoMissionRecord.AlienBaseAssault:
                        return _alienAssaultCtor?.Invoke(new[] { missionDef, site, attackerDef });
                    case GeoMissionRecord.PhoenixBaseDefense:
                        return _pxDefCtor?.Invoke(new[] { missionDef, site });
                    case GeoMissionRecord.PhoenixBaseInfestation:
                        return _pxInfestCtor?.Invoke(new[] { missionDef, site, null, null });
                    case GeoMissionRecord.InfestationCleanse:
                        return _cleanseCtor?.Invoke(new[] { site, missionDef, null });
                    case GeoMissionRecord.Scavenging:
                        return _scavCtor?.Invoke(new[] { site, missionDef, null });
                    case GeoMissionRecord.Ambush:
                        return _ambushCtor2?.Invoke(new[] { site, missionDef, null });
                    case GeoMissionRecord.AncientSite:
                        return _ancientCtor2?.Invoke(new[] { site, missionDef });
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoSiteReflection.BuildMissionForRecord class=" + rec.MissionClass
                               + " ctor failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Stamp the DTO's runtime bits onto a constructed/refreshed mirror mission (each individually
        /// guarded — a partial stamp still renders; missing bits only degrade the affected brief line).</summary>
        private static void StampMissionRuntimeBits(GeoRuntime rt, object mission, GeoMissionRecord rec)
        {
            switch (rec.MissionClass)
            {
                case GeoMissionRecord.HavenDefense:
                    try
                    {
                        if (!string.IsNullOrEmpty(rec.AttackedZoneDefGuid) && _havenZoneDefField != null)
                        {
                            var zoneDef = DefReflection.GetDefByGuid(rec.AttackedZoneDefGuid);
                            if (zoneDef != null) _havenZoneDefField.SetValue(mission, zoneDef);
                        }
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] StampMissionRuntimeBits zone failed: " + ex.Message); }
                    try { _havenAtkDeploySetter?.Invoke(mission, new object[] { rec.AttackerDeployment }); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] StampMissionRuntimeBits atk failed: " + ex.Message); }
                    try { _havenDefDeploySetter?.Invoke(mission, new object[] { rec.DefenderDeployment }); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] StampMissionRuntimeBits def failed: " + ex.Message); }
                    try
                    {
                        if (_havenAttackingSitesField != null)
                            _havenAttackingSitesField.SetValue(mission, BuildSiteList(rt, rec.AttackingSiteIds));
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] StampMissionRuntimeBits havenSites failed: " + ex.Message); }
                    break;
                case GeoMissionRecord.AlienBaseAssault:
                    try { _assaultAtkDeployField?.SetValue(mission, rec.AttackerDeployment); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] StampMissionRuntimeBits assaultAtk failed: " + ex.Message); }
                    try { _assaultDefDeployField?.SetValue(mission, rec.DefenderDeployment); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] StampMissionRuntimeBits assaultDef failed: " + ex.Message); }
                    break;
                case GeoMissionRecord.PhoenixBaseDefense:
                    try
                    {
                        if (_pxEnemyFactionField != null && !string.IsNullOrEmpty(rec.AttackerFactionDefGuid))
                        {
                            var facDef = DefReflection.GetDefByGuid(rec.AttackerFactionDefGuid);
                            if (facDef != null) _pxEnemyFactionField.SetValue(mission, facDef);
                        }
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] StampMissionRuntimeBits enemyFaction failed: " + ex.Message); }
                    try
                    {
                        if (_pxAttackingSitesField != null)
                            _pxAttackingSitesField.SetValue(mission, BuildSiteList(rt, rec.AttackingSiteIds));
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] StampMissionRuntimeBits pxSites failed: " + ex.Message); }
                    break;
            }
        }

        /// <summary>A concrete <c>List&lt;GeoSite&gt;</c> of the resolvable ids (unresolved ids skipped) — never
        /// null so <c>AttackingSites</c>-style getters never NRE on the mirror.</summary>
        private static object BuildSiteList(GeoRuntime rt, int[] siteIds)
        {
            var list = (IList)Activator.CreateInstance(_geoSiteListType);
            if (siteIds != null)
                foreach (var id in siteIds)
                {
                    var s = ResolveSiteById(rt, id);
                    if (s != null) list.Add(s);
                }
            return list;
        }

        /// <summary>
        /// CLIENT (Case B): instantiate an INERT mirror <c>GeoSite</c> for an in-play site the sim-frozen client
        /// never created, so <see cref="ResolveSiteById"/> finds it and a geoscape-event card renders the correct
        /// backdrop/subtitle instead of the StartingBase ("Точка Феникс") default. Host-authoritative: the client
        /// only ever spawns the site the host described in <paramref name="identity"/>.
        ///
        /// IDEMPOTENT: returns the existing site (no double-add) if the id already resolves. The prefab template
        /// comes from <c>GeoSiteTypeMappingDef.Instance.GetSiteTemplate((GeoSiteType)identity.SiteType)</c>; a
        /// null template logs + returns null (caller falls back to siteless render — NEVER throws). The actor is
        /// spawned with <c>ActorSpawner.SpawnActor&lt;GeoSite&gt;(template, instanceData:null,
        /// callEnterPlayOnActor:false)</c> — callEnterPlayOnActor:false is CRITICAL: it skips
        /// <c>DoEnterPlay</c> → no producer coroutines, no <c>GeoMap.RegisterSite</c>, no sim-wake. The site is
        /// stamped with its <c>SiteId</c>, registered into <c>GeoMap.AllSites</c> DIRECTLY (bypassing
        /// RegisterSite — pure mirror, the same no-cascade discipline as the rest of this bridge), and its
        /// identity (Owner/Type/State/Name/EncounterID) is applied via <see cref="ApplyIdentity"/>. The whole body
        /// is wrapped so a reflection miss degrades to null (siteless render), never crashes the event UI.
        /// </summary>
        public static object SpawnMirrorSite(GeoRuntime rt, GeoSiteState identity)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return null;

                // Idempotent: never double-add a site that already exists on this client. STILL apply the carried
                // identity to it: GeoSiteChannel.Apply routes here whenever its own resolve failed — including the
                // FIRST payload after a client (re)load, where ResolveSiteById ran before Ensure had bound
                // _allSitesProp and returned null for a site that IS present. Returning without applying silently
                // dropped that payload's identity + explored-state flags (the "host-explored POI never flips on the
                // client" hole). ApplyIdentity is idempotent, so a genuine double-show stays a no-op.
                var existing = ResolveSiteById(rt, identity.SiteId);
                if (existing != null)
                {
                    ApplyIdentity(rt, existing, identity);
                    return existing;
                }

                if (_spawnActorGeoSite == null || _siteMappingInstanceProp == null
                    || _getSiteTemplateMethod == null || _siteTypeEnum == null
                    || _siteIdField == null || _allSitesProp == null)
                {
                    Debug.LogError("[Multiplayer] GeoSiteReflection.SpawnMirrorSite: spawn members unresolved (Case-B skipped, siteless fallback)");
                    return null;
                }

                // Resolve the prefab template for this site type (raw enum value → GeoSiteType).
                object siteTypeEnum = Enum.ToObject(_siteTypeEnum, identity.SiteType);
                object mappingInstance = _siteMappingInstanceProp.GetValue(null, null);
                if (mappingInstance == null)
                {
                    Debug.LogError("[Multiplayer] GeoSiteReflection.SpawnMirrorSite: GeoSiteTypeMappingDef.Instance null (Case-B skipped)");
                    return null;
                }
                object template = _getSiteTemplateMethod.Invoke(mappingInstance, new[] { siteTypeEnum });
                if (template == null)
                {
                    Debug.LogError("[Multiplayer] GeoSiteReflection.SpawnMirrorSite: no site template for type " + identity.SiteType + " (Case-B skipped, siteless fallback)");
                    return null;
                }

                // INERT spawn: callEnterPlayOnActor:false → no DoEnterPlay / RegisterSite / producer coroutines.
                object site = _spawnActorGeoSite.Invoke(null, new object[] { template, null, false });
                if (site == null)
                {
                    Debug.LogError("[Multiplayer] GeoSiteReflection.SpawnMirrorSite: SpawnActor<GeoSite> returned null (Case-B skipped)");
                    return null;
                }

                // Stamp the site id (resolve-by-id key) BEFORE registration.
                try { _siteIdField.SetValue(site, identity.SiteId); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.SpawnMirrorSite: stamp SiteId failed: " + ex.Message); }

                // Register WITHOUT cascade: add directly to GeoMap.AllSites (bypasses RegisterSite — pure mirror).
                try
                {
                    var map = GetMap(rt);
                    if (map != null && _allSitesProp.GetValue(map, null) is IList allSites)
                        allSites.Add(site);
                    else
                        Debug.LogError("[Multiplayer] GeoSiteReflection.SpawnMirrorSite: GeoMap.AllSites not an IList (Case-B site not registered)");
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.SpawnMirrorSite: AllSites.Add failed: " + ex.Message); }

                // Stamp Owner/Type/State/Name/EncounterID onto the fresh mirror (reuses the Case-A writer).
                ApplyIdentity(rt, site, identity);

                Debug.Log("[Multiplayer] GeoSiteReflection.SpawnMirrorSite: spawned inert mirror site " + identity.SiteId +
                          " type=" + identity.SiteType + " owner=" + identity.OwnerFactionDefGuid + " name=" + identity.SiteName);
                return site;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.SpawnMirrorSite failed: " + ex.Message); return null; }
        }

        /// <summary>Find the live <c>GeoFaction</c> whose <c>Def.Guid</c> equals <paramref name="guid"/>, or null.</summary>
        private static object ResolveFactionByGuid(GeoRuntime rt, string guid)
        {
            if (string.IsNullOrEmpty(guid) || _factionsField == null || _factionDefProp == null) return null;
            var geo = rt?.GeoLevel();
            if (geo == null) return null;
            if (!(_factionsField.GetValue(geo) is IEnumerable facs)) return null;
            foreach (var f in facs)
            {
                if (f == null) continue;
                if (DefReflection.GetGuid(_factionDefProp.GetValue(f, null)) == guid) return f;
            }
            return null;
        }

        // ─── host site-event subscription (aggregate, GeoMap-level) ───

        /// <summary>
        /// Subscribe <paramref name="onSiteChanged"/> (which receives event arg 0 — the changed <c>GeoSite</c>,
        /// or the <c>GeoHaven</c> for the WA-2 haven family; the sink unwraps via
        /// <see cref="GetOwningSiteId"/>) to the <see cref="SiteEventNames"/> aggregate events on the live
        /// <c>GeoMap</c>. Returns an opaque token for <see cref="Unsubscribe"/>, or null if the map / no event
        /// is available. Each event has a different arity (1/2/3 args) but the changed carrier is always arg 0,
        /// so a per-event DynamicMethod adapter captures arg 0 and forwards it. Best-effort: a missing event is
        /// skipped (the others still bind).
        /// </summary>
        public static object Subscribe(GeoRuntime rt, Action<object> onSiteChanged)
        {
            if (onSiteChanged == null) return null;
            try
            {
                Ensure(rt);
                var map = GetMap(rt);
                if (map == null) return null;
                var mapType = map.GetType();

                var token = new SiteEventToken();
                foreach (var name in SiteEventNames)
                {
                    var evt = mapType.GetEvent(name, BindingFlags.Public | BindingFlags.Instance);
                    if (evt == null) continue;
                    var handler = MakeSiteAdapter(evt, onSiteChanged);
                    if (handler == null) continue;
                    try
                    {
                        evt.AddEventHandler(map, handler);
                        token.Handlers.Add((map, evt, handler));
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.Subscribe add '" + name + "' failed (skipped): " + ex.Message); }
                }
                // WA-2 excavation dirty triggers (gap 3c): GeoPhoenixFaction.OnExcavationStarted/Completed
                // (GeoPhoenixFaction.cs:280-282). Both hand (faction, SiteExcavationState) — the site CARRIER
                // is arg 1 (GetOwningSiteId unwraps SiteExcavationState.Site). Same token/detach lifecycle as
                // the map events: the faction instance is reborn with the GeoLevel exactly like the GeoMap, so
                // the channel's instance-compare rebind covers both. Best-effort: a miss only drops the
                // excavation dirty trigger (start/completion still dirty via the mission family / state flips).
                var fac = rt?.PhoenixFaction();
                if (fac != null)
                    foreach (var name in GeoSiteDirtyEvents.PhoenixFactionEventNames)
                    {
                        var evt = fac.GetType().GetEvent(name, BindingFlags.Public | BindingFlags.Instance);
                        if (evt == null) continue;
                        var handler = MakeSiteAdapter(evt, onSiteChanged, argIndex: 1);
                        if (handler == null) continue;
                        try
                        {
                            evt.AddEventHandler(fac, handler);
                            token.Handlers.Add((fac, evt, handler));
                        }
                        catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.Subscribe add '" + name + "' failed (skipped): " + ex.Message); }
                    }
                // Attack-schedule dirty trigger (gap 6b): GeoFaction.SiteAttackScheduled on EVERY faction —
                // any faction (alien or human) can arm a pre-attack countdown on a Phoenix base / ancient
                // site. Carrier = arg 1 (the SiteAttackSchedule; GetOwningSiteId unwraps its Site field).
                // Same token/detach lifecycle: factions are reborn with the GeoLevel exactly like the GeoMap,
                // so the channel's instance-compare rebind covers them. Best-effort per faction.
                foreach (var anyFac in EnumerateFactions(rt))
                {
                    if (anyFac == null) continue;
                    foreach (var name in GeoSiteDirtyEvents.GeoFactionEventNames)
                    {
                        var evt = anyFac.GetType().GetEvent(name, BindingFlags.Public | BindingFlags.Instance);
                        if (evt == null) continue;
                        var handler = MakeSiteAdapter(evt, onSiteChanged, argIndex: 1);
                        if (handler == null) continue;
                        try
                        {
                            evt.AddEventHandler(anyFac, handler);
                            token.Handlers.Add((anyFac, evt, handler));
                        }
                        catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.Subscribe add '" + name + "' failed (skipped): " + ex.Message); }
                    }
                }
                return token.Handlers.Count > 0 ? token : null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.Subscribe failed: " + ex.Message); return null; }
        }

        public static void Unsubscribe(object token)
        {
            if (!(token is SiteEventToken t)) return;
            foreach (var (target, evt, handler) in t.Handlers)
            {
                try { evt.RemoveEventHandler(target, handler); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.Unsubscribe remove failed: " + ex.Message); }
            }
            t.Handlers.Clear();
        }

        /// <summary>Read <c>GeoSite.SiteId</c> off a live site instance (subscription side), or -1.</summary>
        public static int GetSiteId(object site)
        {
            if (site == null || _siteIdField == null) return -1;
            try
            {
                var id = _siteIdField.GetValue(site);
                return id is int i ? i : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// The owning site's <c>SiteId</c> for a dirty-event carrier that may not BE a <c>GeoSite</c>:
        /// the WA-2 haven family (GeoMap.cs:279-283) hands the <c>GeoHaven</c> component — unwrap via
        /// <c>GeoHaven.Site</c> (GeoHaven.cs:166); the excavation family (GeoPhoenixFaction.cs:280-282) hands
        /// the <c>SiteExcavationState</c> — unwrap via its readonly <c>Site</c> field
        /// (SiteExcavationState.cs:15). A plain <c>GeoSite</c> resolves as before; anything else (unbound /
        /// unexpected type) is -1 (the sink drops it).
        /// </summary>
        public static int GetOwningSiteId(object siteOrCarrier)
        {
            if (siteOrCarrier == null) return -1;
            try
            {
                EnsureHavenTail();
                if (_geoHavenType != null && _geoHavenType.IsInstanceOfType(siteOrCarrier) && _havenSiteProp != null)
                    return GetSiteId(_havenSiteProp.GetValue(siteOrCarrier, null));
                EnsureExcavationTail();
                if (_excavStateType != null && _excavStateType.IsInstanceOfType(siteOrCarrier) && _excavSiteField != null)
                    return GetSiteId(_excavSiteField.GetValue(siteOrCarrier));
                EnsureAttackTail();
                if (_attackScheduleType != null && _attackScheduleType.IsInstanceOfType(siteOrCarrier) && _attackSiteField != null)
                    return GetSiteId(_attackSiteField.GetValue(siteOrCarrier));
                return GetSiteId(siteOrCarrier);
            }
            catch { return -1; }
        }

        // ─── WA-2 haven tail (population / infestation display mirror, spec §5 gap 4d) ───
        // Own lazy gate: a miss here degrades ONLY the haven tail (identity/explored/mission keep flowing).
        //
        // Decompile-verified 2026-07-05 (GeoHaven.cs):
        //   • GeoHaven is a COMPONENT on the site actor (GeoHaven.cs:867 `site.GetComponent<GeoHaven>()`),
        //     with `Site` { get; private set; } back-reference (:166).
        //   • Population — public property (:172) over private int `_population` (:144). The SETTER cascades
        //     (OnPopulationChanged → ZonesStats.UpdateZonesStats/UpdateRange; 0 → Site.DestroySite — SIM), so
        //     the client writes the BACKING FIELD directly (the Owner/_owner no-cascade discipline).
        //   • IsInfested — DERIVED property (:213): `Site.Owner.IsAlienFaction`. No backing state exists to
        //     stamp; the identity mirror's Owner write is what flips it client-side. The carried flag is the
        //     host-authoritative record — apply only logs a divergence (owner guid failed to resolve).

        private static bool _havenTailEnsured;
        private static Type _geoHavenType;                // PhoenixPoint.Geoscape.Entities.GeoHaven
        private static PropertyInfo _havenSiteProp;       // GeoHaven.Site (owning-site unwrap)
        private static PropertyInfo _havenPopulationProp; // GeoHaven.Population (host read)
        private static FieldInfo _havenPopulationField;   // GeoHaven._population (client no-cascade write)
        private static PropertyInfo _havenIsInfestedProp; // GeoHaven.IsInfested (derived; host read / client diag)

        private static void EnsureHavenTail()
        {
            if (_havenTailEnsured) return;
            _havenTailEnsured = true;   // one attempt; every user null-guards
            try
            {
                _geoHavenType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoHaven");
                if (_geoHavenType == null) return;
                _havenSiteProp = AccessTools.Property(_geoHavenType, "Site");
                _havenPopulationProp = AccessTools.Property(_geoHavenType, "Population");
                _havenPopulationField = AccessTools.Field(_geoHavenType, "_population");
                _havenIsInfestedProp = AccessTools.Property(_geoHavenType, "IsInfested");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.EnsureHavenTail failed (haven tail disabled): " + ex.Message); }
        }

        /// <summary>The site's live <c>GeoHaven</c> component, or null (not a haven / unbound).</summary>
        private static object GetHavenComponent(object site)
        {
            if (_geoHavenType == null || !(site is Component c)) return null;
            try { return c.GetComponent(_geoHavenType); }
            catch { return null; }
        }

        /// <summary>HOST: read the haven tail off a live site, or null (not a haven / unbound → not carried).</summary>
        public static GeoHavenTail ReadHavenTail(object site)
        {
            try
            {
                EnsureHavenTail();
                var haven = GetHavenComponent(site);
                if (haven == null || _havenPopulationProp == null) return null;
                int population = Convert.ToInt32(_havenPopulationProp.GetValue(haven, null));
                bool infested = false;
                try
                {
                    if (_havenIsInfestedProp != null)
                        infested = (bool)_havenIsInfestedProp.GetValue(haven, null);
                }
                catch { infested = false; }
                return new GeoHavenTail(population, infested);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoSiteReflection.ReadHavenTail failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// CLIENT: stamp the mirrored haven tail onto the resolved site — VALUE-ONLY, idempotent, last-wins.
        /// Population is written to the private <c>_population</c> backing field (never the property: its
        /// setter cascades into ZonesStats/DestroySite — sim on the mirror). Infested is DERIVED from
        /// <c>Site.Owner</c> (which <see cref="ApplyIdentity"/> already mirrored) — a disagreement is logged,
        /// never stamped. Null tail = not carried (older payload / non-haven) → no-op, NEVER a clear.
        /// Ends with the native <see cref="RefreshSiteVisuals"/> kick (haven marker/alert art re-reads state).
        /// </summary>
        public static void ApplyHavenTail(GeoRuntime rt, object site, GeoHavenTail tail)
        {
            if (site == null || tail == null) return;
            try
            {
                EnsureHavenTail();
                var haven = GetHavenComponent(site);
                if (haven == null) return;   // not a haven on this client (or unbound) — nothing to stamp
                if (_havenPopulationField != null)
                {
                    try { _havenPopulationField.SetValue(haven, tail.Population); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyHavenTail population failed (skipped): " + ex.Message); }
                }
                try
                {
                    if (_havenIsInfestedProp != null
                        && (bool)_havenIsInfestedProp.GetValue(haven, null) != tail.Infested)
                        Debug.Log("[Multiplayer] GeoSiteReflection.ApplyHavenTail site=" + GetSiteId(site)
                                  + ": derived IsInfested disagrees with host flag " + tail.Infested
                                  + " (owner mirror likely unresolved)");
                }
                catch { /* diagnostic only */ }
                RefreshSiteVisuals(site);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyHavenTail failed: " + ex.Message); }
        }

        // ─── WA-2 alien-base tail (base-type evolution + site addons, spec §5 gap 4b) ───
        // Decompile-verified 2026-07-05:
        //   • GeoAlienBase is a COMPONENT on the site actor (GeoAlienBase.cs:24, Site back-ref :96).
        //   • AlienBaseTypeDef { get; private set; } (:62) — promoted by ChangeAlienBaseType (:167-197,
        //     fires GeoMap.SiteAlienBaseTypeChanged :287). The client stamps the compiler-generated setter
        //     ONLY (value write): ChangeAlienBaseType itself runs ActivateBase → CreateAlienBaseMission +
        //     SpawnMonster + expansion updateables — SIM on the mirror.
        //   • GeoSite._addons (HashSet<GeoSiteAddonDef>, GeoSite.cs:63; public Addons :237). Add/RemoveAddon
        //     fire AddonsChanged (:452/:463) → GeoMap.SiteAddonsChanged — the client REWRITES the set
        //     directly instead (no event re-raise, the usual no-cascade mirror discipline).

        private static bool _alienBaseTailEnsured;
        private static Type _geoAlienBaseType;              // PhoenixPoint.Geoscape.Entities.GeoAlienBase
        private static PropertyInfo _alienBaseTypeDefProp;  // GeoAlienBase.AlienBaseTypeDef (host read)
        private static MethodInfo _alienBaseTypeDefSetter;  // private auto-prop setter (client stamp)
        private static FieldInfo _siteAddonsField;          // GeoSite._addons (HashSet<GeoSiteAddonDef>)
        private static MethodInfo _addonsClearMethod;       // HashSet<GeoSiteAddonDef>.Clear (client rewrite)
        private static MethodInfo _addonsAddMethod;         // HashSet<GeoSiteAddonDef>.Add

        private static void EnsureAlienBaseTail()
        {
            if (_alienBaseTailEnsured) return;
            _alienBaseTailEnsured = true;   // one attempt; every user null-guards
            try
            {
                _geoAlienBaseType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoAlienBase");
                if (_geoAlienBaseType != null)
                {
                    _alienBaseTypeDefProp = AccessTools.Property(_geoAlienBaseType, "AlienBaseTypeDef");
                    _alienBaseTypeDefSetter = AccessTools.PropertySetter(_geoAlienBaseType, "AlienBaseTypeDef");
                }
                var geoSiteT = _geoSiteType ?? AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
                if (geoSiteT != null)
                {
                    _siteAddonsField = AccessTools.Field(geoSiteT, "_addons");
                    var hashSetT = _siteAddonsField?.FieldType;   // HashSet<GeoSiteAddonDef>
                    if (hashSetT != null)
                    {
                        _addonsClearMethod = AccessTools.Method(hashSetT, "Clear", Type.EmptyTypes);
                        _addonsAddMethod = AccessTools.Method(hashSetT, "Add");
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.EnsureAlienBaseTail failed (alien-base tail disabled): " + ex.Message); }
        }

        /// <summary>HOST: read the alien-base tail off a live site, or null (not an alien base / unbound).</summary>
        public static GeoAlienBaseTail ReadAlienBaseTail(object site)
        {
            try
            {
                EnsureAlienBaseTail();
                if (_geoAlienBaseType == null || !(site is Component c)) return null;
                object alienBase = null;
                try { alienBase = c.GetComponent(_geoAlienBaseType); }
                catch { return null; }
                if (alienBase == null) return null;
                string typeGuid = "";
                try { typeGuid = DefReflection.GetGuid(_alienBaseTypeDefProp?.GetValue(alienBase, null)) ?? ""; }
                catch { typeGuid = ""; }
                var addons = new List<string>();
                try
                {
                    if (_siteAddonsField?.GetValue(site) is IEnumerable set)
                        foreach (var def in set)
                        {
                            var g = DefReflection.GetGuid(def);
                            if (!string.IsNullOrEmpty(g)) addons.Add(g);
                        }
                }
                catch { /* addons stay best-effort-empty */ }
                return new GeoAlienBaseTail(typeGuid, addons.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoSiteReflection.ReadAlienBaseTail failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// CLIENT: stamp the mirrored alien-base tail — VALUE-ONLY, idempotent, last-wins. The type def is
        /// written via the compiler-generated setter (never <c>ChangeAlienBaseType</c> — its cascade is sim);
        /// an unresolved/empty guid SKIPS the stamp (never nulls a live type). The addon set is REWRITTEN
        /// (Clear + Add resolved defs — empty list is an honest clear; unresolved guids skipped). Null tail =
        /// not carried → no-op. Ends with the native <see cref="RefreshSiteVisuals"/> kick (base-type art).
        /// </summary>
        public static void ApplyAlienBaseTail(GeoRuntime rt, object site, GeoAlienBaseTail tail)
        {
            if (site == null || tail == null) return;
            try
            {
                EnsureAlienBaseTail();
                if (_geoAlienBaseType == null || !(site is Component c)) return;
                object alienBase = null;
                try { alienBase = c.GetComponent(_geoAlienBaseType); }
                catch { alienBase = null; }
                if (alienBase != null && _alienBaseTypeDefSetter != null && !string.IsNullOrEmpty(tail.TypeDefGuid))
                {
                    try
                    {
                        var typeDef = DefReflection.GetDefByGuid(tail.TypeDefGuid);
                        if (typeDef != null) _alienBaseTypeDefSetter.Invoke(alienBase, new[] { typeDef });
                        else Debug.LogWarning("[Multiplayer] GeoSiteReflection.ApplyAlienBaseTail: type guid '" + tail.TypeDefGuid + "' did not resolve (skipped)");
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyAlienBaseTail type failed (skipped): " + ex.Message); }
                }
                if (_siteAddonsField != null && _addonsClearMethod != null && _addonsAddMethod != null)
                {
                    try
                    {
                        var set = _siteAddonsField.GetValue(site);
                        if (set != null)
                        {
                            _addonsClearMethod.Invoke(set, null);
                            foreach (var g in tail.AddonDefGuids)
                            {
                                var def = DefReflection.GetDefByGuid(g);
                                if (def != null) _addonsAddMethod.Invoke(set, new[] { def });
                            }
                        }
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyAlienBaseTail addons failed (skipped): " + ex.Message); }
                }
                RefreshSiteVisuals(site);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyAlienBaseTail failed: " + ex.Message); }
        }

        // ─── WA-2 excavation tail (archeology dig state, spec §5 gap 3c) ───
        // Decompile-verified 2026-07-05:
        //   • GeoPhoenixFaction._excavatingSites (List<SiteExcavationState>, GeoPhoenixFaction.cs:108;
        //     public ExcavatingSites :196; StartExcavatingSite :802-821 creates + Inits + subscribes).
        //   • SiteExcavationState: readonly Site FIELD (:15); IsExcavated / ExcavationStartDate /
        //     ExcavationEndDate auto-props with private setters (:25-29); StartExcavation sets the dates and
        //     arms a Timing updateable (:54-68 — SIM, never run on the mirror); CompleteExcavation resets the
        //     dates, flips IsExcavated and creates the ancient-site mission (:70-89 — SIM).
        //   • TimeUnit (Base.Core) wraps a TimeSpan: read `.TimeSpan.Ticks`, build via FromTimeSpan(TimeSpan).
        //   • Display consumers re-derive natively: GeoSite.IsExcavated() walks ExcavatingSites (GeoSite.cs:472)
        //     and feeds the ancient-site art (GeoSiteVisualsController.cs:348) via RefreshVisuals.

        private static bool _excavTailEnsured;
        private static Type _excavStateType;            // ...Factions.Archeology.SiteExcavationState
        private static ConstructorInfo _excavStateCtor; // SiteExcavationState(GeoSite) public
        private static FieldInfo _excavSiteField;       // SiteExcavationState.Site (readonly field)
        private static PropertyInfo _excavIsExcavatedProp;   // IsExcavated (host read)
        private static MethodInfo _excavIsExcavatedSetter;   // private set (client stamp)
        private static PropertyInfo _excavEndDateProp;       // ExcavationEndDate (host read, TimeUnit)
        private static MethodInfo _excavEndDateSetter;       // private set (client stamp)
        private static MethodInfo _excavStartDateSetter;     // ExcavationStartDate private set (client Zero on complete)
        private static FieldInfo _excavatingSitesField;      // GeoPhoenixFaction._excavatingSites (List<...>)
        private static PropertyInfo _timeUnitTimeSpanProp;   // TimeUnit.TimeSpan
        private static MethodInfo _timeUnitFromTimeSpan;     // TimeUnit.FromTimeSpan(TimeSpan) static

        private static void EnsureExcavationTail()
        {
            if (_excavTailEnsured) return;
            _excavTailEnsured = true;   // one attempt; every user null-guards
            try
            {
                _excavStateType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.Archeology.SiteExcavationState");
                var geoSiteT = _geoSiteType ?? AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
                var timeUnitT = AccessTools.TypeByName("Base.Core.TimeUnit");
                var pxFactionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
                if (_excavStateType == null || geoSiteT == null || timeUnitT == null) return;
                _excavStateCtor = AccessTools.Constructor(_excavStateType, new[] { geoSiteT });
                _excavSiteField = AccessTools.Field(_excavStateType, "Site");
                _excavIsExcavatedProp = AccessTools.Property(_excavStateType, "IsExcavated");
                _excavIsExcavatedSetter = AccessTools.PropertySetter(_excavStateType, "IsExcavated");
                _excavEndDateProp = AccessTools.Property(_excavStateType, "ExcavationEndDate");
                _excavEndDateSetter = AccessTools.PropertySetter(_excavStateType, "ExcavationEndDate");
                _excavStartDateSetter = AccessTools.PropertySetter(_excavStateType, "ExcavationStartDate");
                if (pxFactionT != null) _excavatingSitesField = AccessTools.Field(pxFactionT, "_excavatingSites");
                _timeUnitTimeSpanProp = AccessTools.Property(timeUnitT, "TimeSpan");
                // EXACT param match (harmony-accesstools-exact-param-match): FromSeconds has overloads.
                _timeUnitFromTimeSpan = AccessTools.Method(timeUnitT, "FromTimeSpan", new[] { typeof(TimeSpan) });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.EnsureExcavationTail failed (excavation tail disabled): " + ex.Message); }
        }

        /// <summary>The phoenix faction's excavation record for <paramref name="site"/>, or null.</summary>
        private static object FindExcavationState(GeoRuntime rt, object site)
        {
            if (_excavatingSitesField == null || _excavSiteField == null) return null;
            var fac = rt?.PhoenixFaction();
            if (fac == null) return null;
            if (!(_excavatingSitesField.GetValue(fac) is IEnumerable list)) return null;
            foreach (var rec in list)
            {
                if (rec == null) continue;
                if (ReferenceEquals(_excavSiteField.GetValue(rec), site)) return rec;
            }
            return null;
        }

        /// <summary>HOST: read the excavation tail for a live site, or null (no record → not carried).</summary>
        public static GeoExcavationTail ReadExcavationTail(GeoRuntime rt, object site)
        {
            try
            {
                EnsureExcavationTail();
                var rec = FindExcavationState(rt, site);
                if (rec == null) return null;
                bool excavated = false;
                try { excavated = (bool)(_excavIsExcavatedProp?.GetValue(rec, null) ?? false); }
                catch { excavated = false; }
                long ticks = 0;
                try
                {
                    var endDate = _excavEndDateProp?.GetValue(rec, null);
                    if (endDate != null && _timeUnitTimeSpanProp != null)
                        ticks = ((TimeSpan)_timeUnitTimeSpanProp.GetValue(endDate, null)).Ticks;
                }
                catch { ticks = 0; }
                return new GeoExcavationTail(!excavated, ticks);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoSiteReflection.ReadExcavationTail failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// CLIENT: stamp the mirrored excavation state — VALUE-ONLY, idempotent, last-wins. Finds (or
        /// creates via the public <c>SiteExcavationState(GeoSite)</c> ctor + direct list add) the faction's
        /// record for this site, then stamps <c>IsExcavated</c>/<c>ExcavationEndDate</c> via the private
        /// setters. NEVER <c>Init</c>/<c>StartExcavation</c>/event subscribe — no Timing updateable, no
        /// CompleteExcavation cascade (mission creation) on the mirror; the host's own completion arrives as
        /// the next tail. Null tail = not carried → no-op. Ends with <see cref="RefreshSiteVisuals"/>
        /// (ancient-site art re-reads <c>GeoSite.IsExcavated()</c>).
        /// </summary>
        public static void ApplyExcavationTail(GeoRuntime rt, object site, GeoExcavationTail tail)
        {
            if (site == null || tail == null) return;
            try
            {
                EnsureExcavationTail();
                if (_excavIsExcavatedSetter == null || _excavEndDateSetter == null
                    || _timeUnitFromTimeSpan == null) return;
                var rec = FindExcavationState(rt, site);
                if (rec == null)
                {
                    if (_excavStateCtor == null || _excavatingSitesField == null) return;
                    var fac = rt?.PhoenixFaction();
                    if (fac == null || !(_excavatingSitesField.GetValue(fac) is IList list)) return;
                    rec = _excavStateCtor.Invoke(new[] { site });
                    list.Add(rec);
                }
                object endDate = _timeUnitFromTimeSpan.Invoke(null,
                    new object[] { TimeSpan.FromTicks(tail.Excavating ? tail.EndDateTicks : 0L) });
                try { _excavEndDateSetter.Invoke(rec, new[] { endDate }); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyExcavationTail endDate failed (skipped): " + ex.Message); }
                try { _excavIsExcavatedSetter.Invoke(rec, new object[] { !tail.Excavating }); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyExcavationTail isExcavated failed (skipped): " + ex.Message); }
                // Completed digs also reset the start date natively (CompleteExcavation) — mirror that.
                if (!tail.Excavating && _excavStartDateSetter != null)
                {
                    try
                    {
                        object zero = _timeUnitFromTimeSpan.Invoke(null, new object[] { TimeSpan.Zero });
                        _excavStartDateSetter.Invoke(rec, new[] { zero });
                    }
                    catch { /* cosmetic */ }
                }
                RefreshSiteVisuals(site);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyExcavationTail failed: " + ex.Message); }
        }

        // ─── attack-schedule tail (pre-attack countdown mirror, audit gap 6b) ───
        // Own lazy gate: a miss degrades ONLY the attack tail (identity/mission/other tails keep flowing).
        //
        // Decompile-verified 2026-07-05:
        //   • SiteAttackSchedule (PhoenixPoint.Geoscape.Core, SiteAttackSchedule.cs:13) — public fields
        //     Site (readonly, :16), ScheduledAt (TimeUnit, :25), NextUpdateAttack (NextUpdate, :22); props
        //     HasAttackScheduled (:38), ScheduledFor (:34 → NextUpdateAttack.NextTime); public ctor
        //     (GeoSite, GeoFaction) (:45); UnscheduleAttack() stops the Timing producer (:99).
        //   • GeoFaction holds one list per family: _phoenixBaseTargets/_ancientSiteTargets
        //     (GeoFaction.cs:105-107) exposed as PhoenixBaseAttackSchedule/AncientSiteAttackSchedule
        //     (:149-151); ScheduleAttackOnSite arms + raises SiteAttackScheduled (:1926-1938, event :319).
        //   • Host reads ARMED schedules; client stamps VALUE-ONLY (ScheduledAt + NextUpdateAttack via
        //     NextUpdate.Absolute) and NEVER RescheduleAttack — no Timing producer on the mirror, the
        //     mission lands via the ch#5 mission record. The client then re-raises the native
        //     SiteAttackScheduled event so GeoscapeLog renders the vanilla warning toast + status-bar
        //     countdown (GeoscapeLog.cs:446-476; display reads Timing.Now, which tracks host display time
        //     under the freeze) — with GeoscapeView.SuppressEvents flipped true around the raise so the
        //     HighPriority entry can NOT RequestGamePause → SetTimeState → OnPauseTime → the time-control
        //     relay (which would send !GlyphHostPaused — an UNPAUSE when the host already auto-paused).
        //     TFTV parity is automatic: TFTV prefix-suppresses OnFactionSiteAttackScheduled on BOTH sides
        //     (TFTVBaseDefenseGeoscape.cs:1502-1517), so the mirrored raise is silenced exactly like the
        //     host's native one.

        private static bool _attackTailEnsured;
        private static Type _attackScheduleType;          // PhoenixPoint.Geoscape.Core.SiteAttackSchedule
        private static ConstructorInfo _attackScheduleCtor;   // (GeoSite, GeoFaction) public
        private static FieldInfo _attackSiteField;            // Site (readonly public)
        private static FieldInfo _attackScheduledAtField;     // ScheduledAt (public TimeUnit field)
        private static FieldInfo _attackNextUpdateField;      // NextUpdateAttack (public NextUpdate field)
        private static PropertyInfo _attackHasScheduledProp;  // HasAttackScheduled (bool)
        private static PropertyInfo _attackScheduledForProp;  // ScheduledFor (TimeUnit)
        private static MethodInfo _attackUnscheduleMethod;    // UnscheduleAttack()
        private static object _nextUpdateNever;                // NextUpdate.Never (static readonly value)
        private static MethodInfo _nextUpdateAbsolute;         // NextUpdate.Absolute(TimeUnit) static
        private static PropertyInfo _factionPxScheduleProp;    // GeoFaction.PhoenixBaseAttackSchedule
        private static PropertyInfo _factionAncientScheduleProp; // GeoFaction.AncientSiteAttackSchedule
        private static FieldInfo _factionPxTargetsField;       // GeoFaction._phoenixBaseTargets (List)
        private static FieldInfo _factionAncientTargetsField;  // GeoFaction._ancientSiteTargets (List)
        private static FieldInfo _factionAttackEventField;     // SiteAttackScheduled backing delegate (client raise)
        private static PropertyInfo _geoViewProp;              // GeoLevelController.View (GeoscapeView)
        private static FieldInfo _viewSuppressEventsField;     // GeoscapeView.SuppressEvents (public bool field)

        private const int SiteTypePhoenixBase = 10;   // GeoSiteType.PhoenixBase (sparse enum, GeoSiteType.cs:6)

        private static void EnsureAttackTail()
        {
            if (_attackTailEnsured) return;
            _attackTailEnsured = true;   // one attempt; every user null-guards
            try
            {
                EnsureExcavationTail();  // binds the shared TimeUnit bridge (_timeUnitTimeSpanProp/FromTimeSpan)
                _attackScheduleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Core.SiteAttackSchedule");
                var geoFactionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
                var geoSiteT = _geoSiteType ?? AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
                var nextUpdateT = AccessTools.TypeByName("Base.Core.NextUpdate");
                var timeUnitT = AccessTools.TypeByName("Base.Core.TimeUnit");
                if (_attackScheduleType == null || geoFactionT == null || geoSiteT == null
                    || nextUpdateT == null || timeUnitT == null) return;
                _attackScheduleCtor = AccessTools.Constructor(_attackScheduleType, new[] { geoSiteT, geoFactionT });
                _attackSiteField = AccessTools.Field(_attackScheduleType, "Site");
                _attackScheduledAtField = AccessTools.Field(_attackScheduleType, "ScheduledAt");
                _attackNextUpdateField = AccessTools.Field(_attackScheduleType, "NextUpdateAttack");
                _attackHasScheduledProp = AccessTools.Property(_attackScheduleType, "HasAttackScheduled");
                _attackScheduledForProp = AccessTools.Property(_attackScheduleType, "ScheduledFor");
                _attackUnscheduleMethod = AccessTools.Method(_attackScheduleType, "UnscheduleAttack", Type.EmptyTypes);
                try { _nextUpdateNever = AccessTools.Field(nextUpdateT, "Never")?.GetValue(null); } catch { }
                // EXACT param match (harmony-accesstools-exact-param-match): Absolute has a single overload,
                // but pin the TimeUnit signature anyway so a future overload can't silently rebind.
                _nextUpdateAbsolute = AccessTools.Method(nextUpdateT, "Absolute", new[] { timeUnitT });
                _factionPxScheduleProp = AccessTools.Property(geoFactionT, "PhoenixBaseAttackSchedule");
                _factionAncientScheduleProp = AccessTools.Property(geoFactionT, "AncientSiteAttackSchedule");
                _factionPxTargetsField = AccessTools.Field(geoFactionT, "_phoenixBaseTargets");
                _factionAncientTargetsField = AccessTools.Field(geoFactionT, "_ancientSiteTargets");
                _factionAttackEventField = AccessTools.Field(geoFactionT, "SiteAttackScheduled");
                var geoLevelT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
                if (geoLevelT != null) _geoViewProp = AccessTools.Property(geoLevelT, "View");
                var viewT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
                if (viewT != null) _viewSuppressEventsField = AccessTools.Field(viewT, "SuppressEvents");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.EnsureAttackTail failed (attack tail disabled): " + ex.Message); }
        }

        /// <summary>All live factions (<c>GeoLevelController.Factions</c>), or an empty enumerable.</summary>
        private static IEnumerable EnumerateFactions(GeoRuntime rt)
        {
            try
            {
                var geo = rt?.GeoLevel();
                if (geo != null && _factionsField != null && _factionsField.GetValue(geo) is IEnumerable facs)
                    return facs;
            }
            catch { }
            return new object[0];
        }

        /// <summary>One faction's schedule entries for <paramref name="site"/> across BOTH families
        /// (phoenix-base + ancient-site lists). Yields the live SiteAttackSchedule objects.</summary>
        private static List<object> FindSchedulesForSite(object faction, object site)
        {
            var found = new List<object>();
            if (faction == null || site == null || _attackSiteField == null) return found;
            foreach (var prop in new[] { _factionPxScheduleProp, _factionAncientScheduleProp })
            {
                if (prop == null) continue;
                object list = null;
                try { list = prop.GetValue(faction, null); } catch { }
                if (!(list is IEnumerable schedules)) continue;
                foreach (var s in schedules)
                {
                    if (s == null) continue;
                    try { if (ReferenceEquals(_attackSiteField.GetValue(s), site)) found.Add(s); }
                    catch { }
                }
            }
            return found;
        }

        private static bool ScheduleArmed(object schedule)
        {
            try { return (bool)(_attackHasScheduledProp?.GetValue(schedule, null) ?? false); }
            catch { return false; }
        }

        private static long ScheduleTicks(PropertyInfo prop, FieldInfo field, object schedule)
        {
            try
            {
                object tu = prop != null ? prop.GetValue(schedule, null) : field?.GetValue(schedule);
                if (tu != null && _timeUnitTimeSpanProp != null)
                    return ((TimeSpan)_timeUnitTimeSpanProp.GetValue(tu, null)).Ticks;
            }
            catch { }
            return 0L;
        }

        /// <summary>
        /// HOST: read the pre-attack schedule tail for a live site. Null = NO schedule entry exists for this
        /// site on any faction (nothing ever armed → nothing to clear); else one wire entry per ARMED
        /// schedule (possibly zero = honest clear after the attack fired/unscheduled).
        /// </summary>
        public static GeoAttackTail ReadAttackTail(GeoRuntime rt, object site)
        {
            try
            {
                EnsureAttackTail();
                if (site == null || _attackScheduleType == null || _attackSiteField == null) return null;
                bool anyEntry = false;
                var entries = new List<GeoAttackEntry>();
                foreach (var faction in EnumerateFactions(rt))
                {
                    if (faction == null) continue;
                    var schedules = FindSchedulesForSite(faction, site);
                    if (schedules.Count == 0) continue;
                    anyEntry = true;
                    string facGuid = "";
                    try { facGuid = DefReflection.GetGuid(_factionDefProp?.GetValue(faction, null)) ?? ""; }
                    catch { facGuid = ""; }
                    foreach (var s in schedules)
                    {
                        if (!ScheduleArmed(s)) continue;
                        long at = ScheduleTicks(null, _attackScheduledAtField, s);
                        long forT = ScheduleTicks(_attackScheduledForProp, null, s);
                        entries.Add(new GeoAttackEntry(facGuid, at, forT));
                    }
                }
                return anyEntry ? new GeoAttackTail(entries.ToArray()) : null;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoSiteReflection.ReadAttackTail failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// CLIENT: mirror the pre-attack schedule state — VALUE-ONLY, idempotent, last-wins.
        ///   • Not-carried entries CLEAR: any armed schedule for this site whose faction is absent from the
        ///     tail is unscheduled (producer stop + NextUpdateAttack=Never) — covers fired/cancelled attacks.
        ///   • Carried entries STAMP find-or-create (public ctor + direct list add, family by site type) and
        ///     re-raise the native <c>SiteAttackScheduled</c> ONLY on a genuine arm/re-arm (idempotent
        ///     re-snapshots never duplicate the toast/timer), with <c>GeoscapeView.SuppressEvents</c> flipped
        ///     true around the raise (no client pause-relay side effects — see region banner).
        /// Null tail = not carried → no-op. Never RescheduleAttack (no Timing producer on the mirror).
        /// </summary>
        public static void ApplyAttackTail(GeoRuntime rt, object site, GeoAttackTail tail)
        {
            if (site == null || tail == null) return;
            try
            {
                EnsureAttackTail();
                if (_attackScheduleType == null || _attackNextUpdateField == null
                    || _nextUpdateAbsolute == null || _timeUnitFromTimeSpan == null) return;

                foreach (var faction in EnumerateFactions(rt))
                {
                    if (faction == null) continue;
                    string facGuid = "";
                    try { facGuid = DefReflection.GetGuid(_factionDefProp?.GetValue(faction, null)) ?? ""; }
                    catch { facGuid = ""; }

                    GeoAttackEntry carried = null;
                    for (int i = 0; i < tail.Entries.Length; i++)
                        if (tail.Entries[i].AttackerFactionDefGuid == facGuid) { carried = tail.Entries[i]; break; }

                    var schedules = FindSchedulesForSite(faction, site);

                    if (carried == null)
                    {
                        // Not carried for this faction → clear any armed mirror (attack fired/unscheduled).
                        foreach (var s in schedules)
                        {
                            if (!ScheduleArmed(s)) continue;
                            try { _attackUnscheduleMethod?.Invoke(s, null); } catch { }
                            if (_nextUpdateNever != null)
                            {
                                try { _attackNextUpdateField.SetValue(s, _nextUpdateNever); }
                                catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyAttackTail clear failed (skipped): " + ex.Message); }
                            }
                        }
                        continue;
                    }

                    // Carried → find-or-create this faction's schedule entry for the site.
                    object schedule = schedules.Count > 0 ? schedules[0] : null;
                    if (schedule == null)
                    {
                        if (_attackScheduleCtor == null) continue;
                        // Family by site type (sparse enum int): PhoenixBase → _phoenixBaseTargets, the
                        // ancient family (AncientHarvest/AncientRefinery) → _ancientSiteTargets — the same
                        // lists the native schedulers use (GeoFaction.cs:105-107).
                        int siteType = 0;
                        try { siteType = Convert.ToInt32(_typeProp?.GetValue(site, null) ?? 0); } catch { }
                        var listField = siteType == SiteTypePhoenixBase ? _factionPxTargetsField : _factionAncientTargetsField;
                        if (listField == null || !(listField.GetValue(faction) is IList targets)) continue;
                        try
                        {
                            schedule = _attackScheduleCtor.Invoke(new[] { site, faction });
                            targets.Add(schedule);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyAttackTail create failed (skipped): " + ex.Message);
                            continue;
                        }
                    }

                    // Idempotence: an unchanged armed schedule is a no-op (re-snapshot must not re-toast).
                    bool prevArmed = ScheduleArmed(schedule);
                    long prevFor = ScheduleTicks(_attackScheduledForProp, null, schedule);
                    if (prevArmed && prevFor == carried.ScheduledForTicks) continue;

                    try
                    {
                        try { _attackUnscheduleMethod?.Invoke(schedule, null); } catch { }  // drop any stale producer (save-load re-arm)
                        object at = _timeUnitFromTimeSpan.Invoke(null, new object[] { TimeSpan.FromTicks(carried.ScheduledAtTicks) });
                        object forT = _timeUnitFromTimeSpan.Invoke(null, new object[] { TimeSpan.FromTicks(carried.ScheduledForTicks) });
                        _attackScheduledAtField?.SetValue(schedule, at);
                        _attackNextUpdateField.SetValue(schedule, _nextUpdateAbsolute.Invoke(null, new[] { forT }));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyAttackTail stamp failed (skipped): " + ex.Message);
                        continue;
                    }

                    RaiseSiteAttackScheduled(rt, faction, schedule);
                    Debug.Log("[Multiplayer] GeoSiteReflection.ApplyAttackTail site=" + GetSiteId(site)
                              + " attacker=" + facGuid + " for=" + carried.ScheduledForTicks + " → armed (pure mirror)");
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ApplyAttackTail failed: " + ex.Message); }
        }

        /// <summary>
        /// CLIENT: re-raise the native <c>GeoFaction.SiteAttackScheduled</c> so GeoscapeLog renders the
        /// vanilla telegraph (warning toast + status-bar countdown) off the freshly-stamped schedule —
        /// with <c>GeoscapeView.SuppressEvents</c> pinned true for the synchronous dispatch so the
        /// HighPriority entry cannot RequestGamePause (→ pause-relay poison, see region banner). TFTV's
        /// OnFactionSiteAttackScheduled prefix suppresses the handler identically on both sides.
        /// </summary>
        private static void RaiseSiteAttackScheduled(GeoRuntime rt, object faction, object schedule)
        {
            try
            {
                if (_factionAttackEventField == null) return;
                var del = _factionAttackEventField.GetValue(faction) as Delegate;
                if (del == null) return;   // no subscriber (log not wired yet) — state is stamped, timer restores on next log init
                object view = null;
                bool prevSuppress = false, flipped = false;
                try
                {
                    var geo = rt?.GeoLevel();
                    if (geo != null && _geoViewProp != null) view = _geoViewProp.GetValue(geo, null);
                    if (view != null && _viewSuppressEventsField != null)
                    {
                        prevSuppress = (bool)_viewSuppressEventsField.GetValue(view);
                        _viewSuppressEventsField.SetValue(view, true);
                        flipped = true;
                    }
                }
                catch { flipped = false; }
                try { del.DynamicInvoke(faction, schedule); }
                finally
                {
                    if (flipped)
                    {
                        try { _viewSuppressEventsField.SetValue(view, prevSuppress); } catch { }
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.RaiseSiteAttackScheduled failed: " + ex.Message); }
        }

        /// <summary>The owning <c>GeoSite</c> of a live <c>GeoMission</c> (<c>GeoMission.Site</c>,
        /// GeoMission.cs:136), or null — the mission-drift dirty hook resolves its site through this.</summary>
        public static object GetMissionSite(object mission)
        {
            if (mission == null) return null;
            try { return AccessTools.Property(mission.GetType(), "Site")?.GetValue(mission, null); }
            catch { return null; }
        }

        private sealed class SiteEventToken
        {
            // (target, event, handler) triples: GeoMap events + the WA-2 GeoPhoenixFaction excavation events.
            public readonly List<(object target, EventInfo evt, Delegate handler)> Handlers
                = new List<(object, EventInfo, Delegate)>();
        }

        /// <summary>
        /// Emit a DynamicMethod delegate matching <paramref name="evt"/>'s handler signature that loads the
        /// site-carrier arg at <paramref name="argIndex"/> (arg 0 = the <c>GeoSite</c>/<c>GeoHaven</c> for the
        /// GeoMap events; arg 1 = the <c>SiteExcavationState</c> for the faction excavation events) and
        /// forwards it to <paramref name="onSiteChanged"/> (<c>Action&lt;object&gt;</c>), ignoring the rest.
        /// Mirrors <c>ResearchStateReflection.MakeAdapter</c> but captures one arg instead of discarding all.
        /// Null if the event has too few parameters or on failure.
        /// </summary>
        private static Delegate MakeSiteAdapter(EventInfo evt, Action<object> onSiteChanged, int argIndex = 0)
        {
            if (evt == null) return null;
            try
            {
                Type handlerType = evt.EventHandlerType;
                MethodInfo invoke = handlerType.GetMethod("Invoke");
                if (invoke == null) return null;
                ParameterInfo[] ps = invoke.GetParameters();
                if (ps.Length <= argIndex) return null; // carrier arg must exist

                // DynamicMethod signature: [Action<object> closure-arg, <event params...>]
                Type[] dmSig = new Type[ps.Length + 1];
                dmSig[0] = typeof(Action<object>);
                for (int i = 0; i < ps.Length; i++) dmSig[i + 1] = ps[i].ParameterType;

                var dm = new DynamicMethod("GeoMap_Site_Adapter", typeof(void), dmSig,
                    typeof(GeoSiteReflection).Module, skipVisibility: true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);                // the bound Action<object>
                il.Emit(OpCodes.Ldarg, argIndex + 1);    // the site-carrier event arg (reference type → object)
                il.Emit(OpCodes.Callvirt, typeof(Action<object>).GetMethod("Invoke"));
                il.Emit(OpCodes.Ret);

                return dm.CreateDelegate(handlerType, onSiteChanged);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.MakeSiteAdapter failed: " + ex.Message); return null; }
        }

        // ─── display-only mission rebuild for the OUTCOME mirror (Batch-2 P3) ───

        /// <summary>
        /// CLIENT: construct a class-exact DISPLAY-ONLY mission for a mirrored MISSION-OUTCOME modal from wire
        /// ids alone — the same pure ctor map the P1 record apply uses (<see cref="ApplyMission"/>'s
        /// BuildMissionForRecord), but the record is synthesized from the 0x69 payload's own
        /// (missionClass, missionDefGuid), NEVER read off <c>site.ActiveMission</c>: the outcome shows AFTER the
        /// mission ended, so the P1 mirror may already be tombstoned (spec Batch-2 ordering decision — the
        /// payload is self-sufficient). The mission is NEVER attached to the site (no SetActiveMission, no
        /// producers on the frozen client sim) — it only feeds the native outcome bind. Null on any miss.
        /// </summary>
        public static object BuildDisplayMission(GeoRuntime rt, object site, byte missionClass, string missionDefGuid)
        {
            if (site == null) return null;
            try
            {
                EnsureMissionMirror();
                return BuildMissionForRecord(rt, site, new GeoMissionRecord(missionClass, missionDefGuid));
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoSiteReflection.BuildDisplayMission failed: " + ex.Message);
                return null;
            }
        }

        // ─── resource-harvest FLOAT mirror (Batch-2 P6) ───
        // Native: GeoSite.ShowResourceHarvested(ResourcePack) (GeoSite.cs:931) renders the FIRST ResourceUnit's
        // Type + RoundedValue as a site-anchored float. Host READS that tuple off the live pack; the client
        // REPLAYS the same native call with a freshly built single-unit pack. Display-only — never credits
        // resources (wallet 0xA0 is the one balance writer).

        private static bool _harvestEnsured;
        private static MethodInfo _showResourceHarvested;   // GeoSite.ShowResourceHarvested(ResourcePack)
        private static ConstructorInfo _resourcePackCtor;   // ResourcePack() (params-array ctor exists too; EXACT empty match)
        private static MethodInfo _packAdd;                 // ResourcePack.Add(ResourceUnit)
        private static ConstructorInfo _resourceUnitCtor;   // ResourceUnit(ResourceType, float)
        private static FieldInfo _resourceUnitTypeField;    // ResourceUnit.Type (public FIELD, ResourceUnit.cs:12)
        private static FieldInfo _resourceUnitValueField;   // ResourceUnit.Value (public FIELD, ResourceUnit.cs:15)
        private static Type _resourceTypeEnum;              // PhoenixPoint.Common.Core.ResourceType

        private static void EnsureHarvest()
        {
            if (_harvestEnsured) return;
            _harvestEnsured = true;   // one attempt; every user null-guards
            try
            {
                var geoSiteT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
                var packT = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourcePack");
                var unitT = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceUnit");
                _resourceTypeEnum = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceType");
                if (geoSiteT == null || packT == null || unitT == null || _resourceTypeEnum == null) return;
                // EXACT param matches (harmony-accesstools-exact-param-match) — overloads exist on both.
                _showResourceHarvested = AccessTools.Method(geoSiteT, "ShowResourceHarvested", new[] { packT });
                _resourcePackCtor = AccessTools.Constructor(packT, Type.EmptyTypes);
                _packAdd = AccessTools.Method(packT, "Add", new[] { unitT });
                _resourceUnitCtor = AccessTools.Constructor(unitT, new[] { _resourceTypeEnum, typeof(float) });
                _resourceUnitTypeField = AccessTools.Field(unitT, "Type");
                _resourceUnitValueField = AccessTools.Field(unitT, "Value");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.EnsureHarvest failed: " + ex.Message); }
        }

        /// <summary>
        /// HOST: read the FIRST <c>ResourceUnit</c>'s raw type + value off a live <c>ResourcePack</c> — exactly
        /// the tuple the native float renders (GeoSite.cs:933 <c>resources.FirstOrDefault()</c>). False on an
        /// empty pack / any miss (caller skips the broadcast; the native float shows None/0 there anyway).
        /// </summary>
        public static bool ReadFirstResource(object resourcePack, out int resourceType, out float value)
        {
            resourceType = 0; value = 0f;
            try
            {
                EnsureHarvest();
                if (_resourceUnitTypeField == null || _resourceUnitValueField == null) return false;
                if (!(resourcePack is IEnumerable units)) return false;
                foreach (var u in units)
                {
                    resourceType = Convert.ToInt32(_resourceUnitTypeField.GetValue(u));
                    value = Convert.ToSingle(_resourceUnitValueField.GetValue(u));
                    return true;
                }
                return false;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ReadFirstResource failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// CLIENT: replay the native harvest float at the mirrored site — resolve the site by id, build a
        /// single-unit <c>ResourcePack</c> {(ResourceType)resourceType, value} and invoke the SAME native
        /// <c>GeoSite.ShowResourceHarvested</c> the host ran. Pure display (collector-visuals float);
        /// no wallet/state mutation. Best-effort: any miss logs + no-ops (a dropped float is cosmetic).
        /// </summary>
        public static void ShowHarvestFloat(GeoRuntime rt, int siteId, int resourceType, float value)
        {
            try
            {
                EnsureHarvest();
                if (_showResourceHarvested == null || _resourcePackCtor == null
                    || _packAdd == null || _resourceUnitCtor == null || _resourceTypeEnum == null)
                {
                    Debug.LogWarning("[Multiplayer] GeoSiteReflection.ShowHarvestFloat: reflection unbound (float dropped)");
                    return;
                }
                var site = ResolveSiteById(rt, siteId);
                if (site == null)
                {
                    Debug.Log("[Multiplayer] GeoSiteReflection.ShowHarvestFloat: siteId " + siteId
                              + " did not resolve (float dropped — cosmetic)");
                    return;
                }
                object unit = _resourceUnitCtor.Invoke(new[] { Enum.ToObject(_resourceTypeEnum, resourceType), (object)value });
                object pack = _resourcePackCtor.Invoke(null);
                _packAdd.Invoke(pack, new[] { unit });
                _showResourceHarvested.Invoke(site, new[] { pack });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.ShowHarvestFloat failed: " + ex.Message); }
        }
    }
}
