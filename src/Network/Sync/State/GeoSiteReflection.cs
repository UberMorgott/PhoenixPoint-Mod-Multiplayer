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

        // The 7 aggregate site events on GeoMap, by name. SiteAdded/SiteRemoved are bound for symmetry
        // (Case A only updates existing sites; an add/remove still re-snapshots so an in-play identity flip
        // converges, and a removed/added id absent on the client is harmlessly skipped in Apply).
        // SiteFirstTimeVisited (GeoMap.cs:263, fired by GeoSite.SiteVisited on the FIRST SetVisited(true)) covers
        // a site whose ONLY change is the Visited flip (plain first arrival at an already-inspected site) — without
        // it that flip never dirty-marks and the client's Visited state silently diverges.
        private static readonly string[] SiteEventNames =
        {
            "SiteOwnerChanged", "SiteStateChanged", "SiteVisibilityChanged",
            "SiteInspectedChanged", "SiteFirstTimeVisited", "SiteAdded", "SiteRemoved",
        };

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

            return new GeoSiteState(siteId, ownerGuid, siteType, state, siteName, encounterId, inspected, visible, visited);
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
        /// Subscribe <paramref name="onSiteChanged"/> (which receives the changed <c>GeoSite</c> instance) to
        /// all 6 aggregate site events on the live <c>GeoMap</c>. Returns an opaque token for
        /// <see cref="Unsubscribe"/>, or null if the map / no event is available. Each event has a different
        /// arity (1/2/3 args) but the GeoSite is always arg 0, so a per-event DynamicMethod adapter captures
        /// arg 0 and forwards it. Best-effort: a missing event is skipped (the others still bind).
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

                var token = new SiteEventToken { Map = map };
                foreach (var name in SiteEventNames)
                {
                    var evt = mapType.GetEvent(name, BindingFlags.Public | BindingFlags.Instance);
                    if (evt == null) continue;
                    var handler = MakeSiteAdapter(evt, onSiteChanged);
                    if (handler == null) continue;
                    try
                    {
                        evt.AddEventHandler(map, handler);
                        token.Handlers.Add((evt, handler));
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.Subscribe add '" + name + "' failed (skipped): " + ex.Message); }
                }
                return token.Handlers.Count > 0 ? token : null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.Subscribe failed: " + ex.Message); return null; }
        }

        public static void Unsubscribe(object token)
        {
            if (!(token is SiteEventToken t) || t.Map == null) return;
            foreach (var (evt, handler) in t.Handlers)
            {
                try { evt.RemoveEventHandler(t.Map, handler); }
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

        private sealed class SiteEventToken
        {
            public object Map;
            public readonly List<(EventInfo evt, Delegate handler)> Handlers = new List<(EventInfo, Delegate)>();
        }

        /// <summary>
        /// Emit a DynamicMethod delegate matching <paramref name="evt"/>'s handler signature that loads arg 0
        /// (the <c>GeoSite</c>) and forwards it to <paramref name="onSiteChanged"/> (<c>Action&lt;object&gt;</c>),
        /// ignoring any further args. Mirrors <c>ResearchStateReflection.MakeAdapter</c> but captures arg 0
        /// instead of discarding all args. Null if the event has no parameters or on failure.
        /// </summary>
        private static Delegate MakeSiteAdapter(EventInfo evt, Action<object> onSiteChanged)
        {
            if (evt == null) return null;
            try
            {
                Type handlerType = evt.EventHandlerType;
                MethodInfo invoke = handlerType.GetMethod("Invoke");
                if (invoke == null) return null;
                ParameterInfo[] ps = invoke.GetParameters();
                if (ps.Length == 0) return null; // every site event has the GeoSite as arg 0

                // DynamicMethod signature: [Action<object> closure-arg, <event params...>]
                Type[] dmSig = new Type[ps.Length + 1];
                dmSig[0] = typeof(Action<object>);
                for (int i = 0; i < ps.Length; i++) dmSig[i + 1] = ps[i].ParameterType;

                var dm = new DynamicMethod("GeoMap_Site_Adapter", typeof(void), dmSig,
                    typeof(GeoSiteReflection).Module, skipVisibility: true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);   // the bound Action<object>
                il.Emit(OpCodes.Ldarg_1);   // event arg 0 == the GeoSite (reference type → object)
                il.Emit(OpCodes.Callvirt, typeof(Action<object>).GetMethod("Invoke"));
                il.Emit(OpCodes.Ret);

                return dm.CreateDelegate(handlerType, onSiteChanged);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoSiteReflection.MakeSiteAdapter failed: " + ex.Message); return null; }
        }
    }
}
