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
    /// Reflection bridge for the host-authoritative faction-OBJECTIVES + event-variables state channel
    /// (#7, spec §P7). The mod has NO compile-time game references, so every member is resolved by name
    /// and cached (the <see cref="DiplomacyReflection"/> pattern).
    ///
    /// Verified against the decompile (2026-07-06):
    ///   • <c>GeoFaction.Objectives : IReadOnlyList&lt;GeoFactionObjective&gt;</c> (GeoFaction.cs:235);
    ///     <c>AddObjective</c>/<c>RemoveObjective</c> (:997/:1007) each fire the faction's
    ///     <c>ObjectivesChanged</c>, which <c>GeoscapeView</c> re-raises to the objectives panel
    ///     (GeoscapeView.cs:398/1624 → UIState*.RefreshObjectives) — so a client add/remove repaints
    ///     NATIVELY, zero new UI. They also drive <c>OnAssigned/OnUnassignedFromFaction</c> (event
    ///     subscribe/unsubscribe per class) — the same lifecycle the native save-load path runs (:481).
    ///   • base fields (GeoFactionObjective.cs): <c>Title</c>/<c>Description</c> (LocalizedTextBind),
    ///     <c>GivenByFaction</c> (readonly GeoFactionDef), <c>IsCriticalPath</c> (bool).
    ///   • <c>LocalizedTextBind</c> = {<c>LocalizationKey</c>, <c>_doNotLocalize</c>} + (string,bool) ctor.
    ///   • disc classes: EventGeoFactionObjective {readonly EventID, _completed};
    ///     DiplomaticGeoFactionObjective {readonly ForFaction : GeoFactionDef};
    ///     ResearchGeoFactionObjective {readonly Research : ResearchElement};
    ///     MissionGeoFactionObjective {readonly Mission : GeoMission}. Each keeps a private
    ///     parameterless serializer ctor → <c>Activator.CreateInstance(type, nonPublic: true)</c> builds
    ///     WITHOUT the public ctor's live-object arguments; readonly fields stamp via FieldInfo.
    ///   • <c>GeoLevelController.EventSystem</c> (public field, :105) →
    ///     <c>GeoscapeEventSystem._customVariables : Dictionary&lt;string,int&gt;</c> (:88),
    ///     <c>GetEventByID(string, bool canFail)</c> (:280), <c>VariableSet : Action&lt;string,int&gt;</c> (:114).
    ///
    /// CLIENT GUARDS (TFTV-absent tolerance + panel NRE proofing — every skip is per-record):
    ///   • disc Event: rebuilt ONLY when <c>GetEventByID(EventID, canFail:true)</c> resolves — the panel's
    ///     <c>InitObjective</c> dereferences <c>GetEventByID(EventID).GeoscapeEventData</c> with NO null
    ///     check (UIModuleGeoObjectives.cs:133), so an unresolvable (TFTV-injected) event def on a
    ///     TFTV-less client must never be materialized.
    ///   • GivenByFaction guid must resolve (panel reads <c>GivenByFaction.GeoFactionViewDef.FactionColor</c>
    ///     unguarded, :128); Diplomatic ForFaction / Research id / Mission site likewise resolve-or-skip.
    ///   • Reconcile scope = the four carried EXACT classes only: client-local objectives of any OTHER
    ///     class (StoryMission…, tutorial, mod subclasses) are never touched, so partial coverage can
    ///     never delete state this channel doesn't carry.
    ///
    /// Variables apply is a PURE VALUE MIRROR: overwrite the private dictionary directly — never
    /// <c>SetVariable</c>, whose <c>VariableSet</c> cascade raises geoscape events (client sim events are
    /// host-mirrored, one writer per field). TFTV consumers (quest gates, ODI TopInfoBar reading BC_SDI)
    /// read the same table via <c>GetVariable</c> and see host truth.
    /// </summary>
    public static class ObjectivesReflection
    {
        private static bool _ready;
        // base GeoFactionObjective members
        private static Type _objBaseType;
        private static FieldInfo _titleField;        // Title (LocalizedTextBind)
        private static FieldInfo _descField;         // Description (LocalizedTextBind)
        private static FieldInfo _givenByField;      // GivenByFaction (readonly GeoFactionDef)
        private static FieldInfo _criticalField;     // IsCriticalPath (bool)
        // LocalizedTextBind
        private static ConstructorInfo _locBindCtor; // LocalizedTextBind(string, bool)
        private static FieldInfo _locKeyField;       // LocalizationKey
        private static FieldInfo _locNoLocField;     // _doNotLocalize
        // carried concrete classes + their disc-specific fields
        private static Type _eventObjType;           // EventGeoFactionObjective
        private static FieldInfo _eventIdField;      // .EventID (readonly string)
        private static FieldInfo _eventCompletedField; // ._completed (bool)
        private static Type _diploObjType;           // DiplomaticGeoFactionObjective
        private static FieldInfo _forFactionField;   // .ForFaction (readonly GeoFactionDef)
        private static Type _researchObjType;        // ResearchGeoFactionObjective
        private static FieldInfo _researchField;     // .Research (readonly ResearchElement)
        private static Type _missionObjType;         // MissionGeoFactionObjective
        private static FieldInfo _missionField;      // .Mission (readonly GeoMission)
        // faction members
        private static PropertyInfo _objectivesProp; // GeoFaction.Objectives (IReadOnlyList)
        private static MethodInfo _addObjective;     // GeoFaction.AddObjective(GeoFactionObjective)
        private static MethodInfo _removeObjective;  // GeoFaction.RemoveObjective(GeoFactionObjective)
        private static FieldInfo _objectivesChangedField; // field-like event backing delegate (value-only re-raise)
        // event system members
        private static FieldInfo _eventSystemField;  // GeoLevelController.EventSystem (public field)
        private static FieldInfo _customVarsField;   // GeoscapeEventSystem._customVariables (Dictionary<string,int>)
        private static MethodInfo _getEventById;     // GeoscapeEventSystem.GetEventByID(string, bool)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            _objBaseType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Objectives.GeoFactionObjective");
            _eventObjType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Objectives.EventGeoFactionObjective");
            _diploObjType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Objectives.DiplomaticGeoFactionObjective");
            _researchObjType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Objectives.ResearchGeoFactionObjective");
            _missionObjType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Objectives.MissionGeoFactionObjective");
            var locBindType = AccessTools.TypeByName("Base.UI.LocalizedTextBind");
            if (_objBaseType == null || locBindType == null) return;

            _titleField = AccessTools.Field(_objBaseType, "Title");
            _descField = AccessTools.Field(_objBaseType, "Description");
            _givenByField = AccessTools.Field(_objBaseType, "GivenByFaction");
            _criticalField = AccessTools.Field(_objBaseType, "IsCriticalPath");
            _locBindCtor = AccessTools.Constructor(locBindType, new[] { typeof(string), typeof(bool) });
            _locKeyField = AccessTools.Field(locBindType, "LocalizationKey");
            _locNoLocField = AccessTools.Field(locBindType, "_doNotLocalize");

            if (_eventObjType != null)
            {
                _eventIdField = AccessTools.Field(_eventObjType, "EventID");
                _eventCompletedField = AccessTools.Field(_eventObjType, "_completed");
            }
            if (_diploObjType != null) _forFactionField = AccessTools.Field(_diploObjType, "ForFaction");
            if (_researchObjType != null) _researchField = AccessTools.Field(_researchObjType, "Research");
            if (_missionObjType != null) _missionField = AccessTools.Field(_missionObjType, "Mission");

            // Faction members off the live faction instance (GeoPhoenixFaction : GeoFaction).
            var fac = rt?.PhoenixFaction();
            if (fac == null) return;   // not in geoscape yet → retry next call
            var facType = fac.GetType();
            _objectivesProp = AccessTools.Property(facType, "Objectives");
            _addObjective = AccessTools.Method(facType, "AddObjective", new[] { _objBaseType });
            _removeObjective = AccessTools.Method(facType, "RemoveObjective", new[] { _objBaseType });
            // Field-like event → compiler-generated backing field of the same name (for the value-only
            // stamp re-raise; a miss only skips the stamp-only repaint, add/remove repaint natively).
            _objectivesChangedField = AccessTools.Field(facType, "ObjectivesChanged");

            var geo = rt?.GeoLevel();
            if (geo != null)
            {
                _eventSystemField = AccessTools.Field(geo.GetType(), "EventSystem");
                var es = _eventSystemField?.GetValue(geo);
                if (es != null)
                {
                    _customVarsField = AccessTools.Field(es.GetType(), "_customVariables");
                    _getEventById = AccessTools.Method(es.GetType(), "GetEventByID", new[] { typeof(string), typeof(bool) });
                }
            }

            _ready = _titleField != null && _descField != null && _givenByField != null
                     && _criticalField != null && _locBindCtor != null && _locKeyField != null
                     && _objectivesProp != null && _addObjective != null && _removeObjective != null
                     && _customVarsField != null;
        }

        /// <summary>The live <c>GeoscapeEventSystem</c> (public field on the level controller), or null.</summary>
        private static object GetEventSystem(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null || _eventSystemField == null) return null;
            try { return _eventSystemField.GetValue(geo); }
            catch { return null; }
        }

        // ─── host snapshot ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Host: snapshot the player faction's carried objectives + the full custom-variable table.
        /// The WHOLE set is read in one pass inside one flush tick (dirty is coalesced by the engine),
        /// so a TFTV quest step that writes several variables + an objective in one call stack never
        /// tears across snapshots. Null if unavailable.
        /// </summary>
        public static ObjectivesSnapshot Snapshot(GeoRuntime rt)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return null;
                var fac = rt?.PhoenixFaction();
                var es = GetEventSystem(rt);
                if (fac == null || es == null) return null;

                var snap = new ObjectivesSnapshot();
                if (_objectivesProp.GetValue(fac, null) is IEnumerable objectives)
                {
                    foreach (var obj in objectives)
                    {
                        if (obj == null) continue;
                        try
                        {
                            var rec = ReadRecord(obj);
                            if (rec != null) snap.Objectives.Add(rec);
                        }
                        catch { /* per-objective best-effort: an unreadable one is simply not carried */ }
                    }
                }
                if (_customVarsField.GetValue(es) is IDictionary vars)
                {
                    foreach (DictionaryEntry e in vars)
                    {
                        if (!(e.Key is string name)) continue;
                        int value;
                        try { value = Convert.ToInt32(e.Value); } catch { continue; }
                        snap.Variables.Add((name, value));
                    }
                }
                return snap;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ObjectivesReflection.Snapshot failed: " + ex.Message); return null; }
        }

        /// <summary>One live objective → wire record, or null when not carried (foreign class /
        /// unresolvable identity — e.g. a mission objective whose site id can't be read).</summary>
        private static ObjectivesSnapshot.ObjectiveRecord ReadRecord(object obj)
        {
            var t = obj.GetType();
            byte disc;
            string payload = "";
            int aux = 0;
            if (t == _eventObjType)
            {
                disc = ObjectivesSnapshot.DiscEvent;
                payload = _eventIdField?.GetValue(obj) as string;
                if (string.IsNullOrEmpty(payload)) return null;
            }
            else if (t == _diploObjType)
            {
                disc = ObjectivesSnapshot.DiscDiplomatic;
                payload = DefReflection.GetGuid(_forFactionField?.GetValue(obj));
                if (string.IsNullOrEmpty(payload)) return null;
            }
            else if (t == _researchObjType)
            {
                disc = ObjectivesSnapshot.DiscResearch;
                payload = ResearchStateReflection.GetId(_researchField?.GetValue(obj));
                if (string.IsNullOrEmpty(payload)) return null;
            }
            else if (t == _missionObjType)
            {
                disc = ObjectivesSnapshot.DiscMission;
                GeoSiteReflection.GetMapPublic(GeoRuntime.Instance);   // force site-reflection binding
                var site = GeoSiteReflection.GetMissionSite(_missionField?.GetValue(obj));
                aux = GeoSiteReflection.GetSiteId(site);
                if (aux < 0) return null;
            }
            else return null;   // not a carried class (story/tutorial/mod subclass) — never carried

            string givenBy = DefReflection.GetGuid(_givenByField.GetValue(obj));
            if (string.IsNullOrEmpty(givenBy)) return null;   // panel derefs GivenByFaction unguarded

            var rec = new ObjectivesSnapshot.ObjectiveRecord { Disc = disc, Payload = payload ?? "", Aux = aux, GivenByGuid = givenBy };
            byte flags = 0;
            if (_criticalField.GetValue(obj) is bool crit && crit) flags |= ObjectivesSnapshot.FlagCritical;
            if (disc == ObjectivesSnapshot.DiscEvent && _eventCompletedField?.GetValue(obj) is bool done && done)
                flags |= ObjectivesSnapshot.FlagCompleted;
            ReadLocBind(_titleField.GetValue(obj), out var titleKey, out var titleNoLoc, out var titlePresent);
            ReadLocBind(_descField.GetValue(obj), out var descKey, out var descNoLoc, out var descPresent);
            rec.TitleKey = titleKey;
            rec.DescKey = descKey;
            if (titlePresent) flags |= ObjectivesSnapshot.FlagTitlePresent;
            if (titleNoLoc) flags |= ObjectivesSnapshot.FlagTitleNoLoc;
            if (descPresent) flags |= ObjectivesSnapshot.FlagDescPresent;
            if (descNoLoc) flags |= ObjectivesSnapshot.FlagDescNoLoc;
            rec.Flags = flags;
            return rec;
        }

        private static void ReadLocBind(object locBind, out string key, out bool noLoc, out bool present)
        {
            key = "";
            noLoc = false;
            present = locBind != null;
            if (!present) return;
            try
            {
                key = _locKeyField.GetValue(locBind) as string ?? "";
                noLoc = _locNoLocField != null && _locNoLocField.GetValue(locBind) is bool b && b;
            }
            catch { /* unreadable bind → carried as empty key */ }
        }

        // ─── client apply ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Client: reconcile the local player faction's CARRIED-class objectives to the host set and
        /// overwrite the event-variable table. Adds/removes go through the native
        /// <c>AddObjective</c>/<c>RemoveObjective</c> (correct subscribe/unsubscribe lifecycle + free
        /// <c>ObjectivesChanged</c> panel repaint); matches stamp mutable bits in place; a stamp-only
        /// apply re-raises <c>ObjectivesChanged</c> once via the backing delegate (value-only pattern).
        /// </summary>
        public static void Apply(GeoRuntime rt, ObjectivesSnapshot target)
        {
            if (target == null) return;
            try
            {
                Ensure(rt);
                if (!_ready) return;
                var fac = rt?.PhoenixFaction();
                var es = GetEventSystem(rt);
                if (fac == null || es == null) return;

                // 1) VARIABLES — full overwrite of the private dict, no VariableSet cascade.
                if (_customVarsField.GetValue(es) is IDictionary vars)
                {
                    vars.Clear();
                    foreach (var (name, value) in target.Variables)
                        if (name != null) vars[name] = value;
                }

                // 2) OBJECTIVES — reconcile carried classes only.
                var pool = new List<(object obj, string key)>();   // client's current carried objectives
                if (_objectivesProp.GetValue(fac, null) is IEnumerable current)
                {
                    foreach (var obj in current)
                    {
                        if (obj == null) continue;
                        string key = LiveMatchKey(obj);
                        if (key != null) pool.Add((obj, key));
                    }
                }

                int added = 0, removed = 0, stamped = 0;
                foreach (var rec in target.Objectives)
                {
                    if (!ObjectivesSnapshot.IsCarriedDisc(rec.Disc)) continue;   // future disc → skip
                    int match = -1;
                    string wantKey = rec.MatchKey();
                    for (int i = 0; i < pool.Count; i++)
                        if (pool[i].key == wantKey) { match = i; break; }
                    if (match >= 0)
                    {
                        if (StampRecord(pool[match].obj, rec)) stamped++;
                        pool.RemoveAt(match);   // matched — survives reconcile
                        continue;
                    }
                    try
                    {
                        var built = BuildObjective(rt, es, rec);
                        if (built == null) continue;   // unresolvable on this client (e.g. TFTV absent) → skip
                        _addObjective.Invoke(fac, new[] { built });
                        added++;
                    }
                    catch (Exception bex) { Debug.LogWarning("[Multiplayer] ObjectivesReflection: rebuild skipped disc=" + rec.Disc + ": " + bex.Message); }
                }
                // Leftover pool = carried-class objectives the host no longer has → remove natively.
                foreach (var (obj, _) in pool)
                {
                    try { _removeObjective.Invoke(fac, new[] { obj }); removed++; }
                    catch (Exception rex) { Debug.LogWarning("[Multiplayer] ObjectivesReflection: remove skipped: " + rex.Message); }
                }

                // Stamp-only apply → one native re-raise so the open panel repaints (add/remove already did).
                if (stamped > 0 && added == 0 && removed == 0) RaiseObjectivesChanged(fac);
                if (added + removed + stamped > 0)
                    Debug.Log("[Multiplayer] ObjectivesReflection.Apply objectives added=" + added
                              + " removed=" + removed + " stamped=" + stamped
                              + " vars=" + target.Variables.Count);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ObjectivesReflection.Apply failed: " + ex.Message); }
        }

        /// <summary>The reconcile key of a LIVE client objective, or null when it is not a carried class
        /// (never touched by reconcile).</summary>
        private static string LiveMatchKey(object obj)
        {
            try
            {
                var rec = ReadRecord(obj);
                return rec?.MatchKey();
            }
            catch { return null; }
        }

        /// <summary>Stamp the mutable display bits of a matched live objective (critical flag, title /
        /// description binds, disc-1 completed). Returns true when anything actually changed.</summary>
        private static bool StampRecord(object obj, ObjectivesSnapshot.ObjectiveRecord rec)
        {
            bool changed = false;
            try
            {
                if (_criticalField.GetValue(obj) is bool crit && crit != rec.Critical)
                {
                    _criticalField.SetValue(obj, rec.Critical);
                    changed = true;
                }
                if (rec.Disc == ObjectivesSnapshot.DiscEvent && _eventCompletedField != null
                    && _eventCompletedField.GetValue(obj) is bool done && done != rec.Completed)
                {
                    _eventCompletedField.SetValue(obj, rec.Completed);
                    changed = true;
                }
                changed |= StampLocBind(obj, _titleField, rec.TitleKey,
                    (rec.Flags & ObjectivesSnapshot.FlagTitlePresent) != 0,
                    (rec.Flags & ObjectivesSnapshot.FlagTitleNoLoc) != 0);
                changed |= StampLocBind(obj, _descField, rec.DescKey,
                    (rec.Flags & ObjectivesSnapshot.FlagDescPresent) != 0,
                    (rec.Flags & ObjectivesSnapshot.FlagDescNoLoc) != 0);
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] ObjectivesReflection.StampRecord failed: " + ex.Message); }
            return changed;
        }

        private static bool StampLocBind(object obj, FieldInfo field, string key, bool present, bool noLoc)
        {
            ReadLocBind(field.GetValue(obj), out var curKey, out var curNoLoc, out var curPresent);
            if (curPresent == present && curKey == (key ?? "") && curNoLoc == noLoc) return false;
            field.SetValue(obj, present ? _locBindCtor.Invoke(new object[] { key ?? "", noLoc }) : null);
            return true;
        }

        /// <summary>Build the vanilla objective instance for a wire record via the private serializer
        /// ctor + field stamps, or null when any identity fails to resolve on THIS client.</summary>
        private static object BuildObjective(GeoRuntime rt, object es, ObjectivesSnapshot.ObjectiveRecord rec)
        {
            var givenBy = DefReflection.GetDefByGuid(rec.GivenByGuid);
            if (givenBy == null) return null;   // faction def missing → panel would NRE

            Type type;
            switch (rec.Disc)
            {
                case ObjectivesSnapshot.DiscEvent:
                    type = _eventObjType;
                    if (type == null || _eventIdField == null) return null;
                    // TFTV-absent guard: the panel derefs GetEventByID(EventID) with no null check.
                    if (_getEventById == null || _getEventById.Invoke(es, new object[] { rec.Payload, true }) == null)
                        return null;
                    break;
                case ObjectivesSnapshot.DiscDiplomatic:
                    type = _diploObjType;
                    if (type == null || _forFactionField == null) return null;
                    if (DefReflection.GetDefByGuid(rec.Payload) == null) return null;
                    break;
                case ObjectivesSnapshot.DiscResearch:
                    type = _researchObjType;
                    if (type == null || _researchField == null) return null;
                    if (ResolveResearchElement(rt, rec.Payload) == null) return null;
                    break;
                case ObjectivesSnapshot.DiscMission:
                    type = _missionObjType;
                    if (type == null || _missionField == null) return null;
                    GeoSiteReflection.GetMapPublic(rt);
                    var site = GeoSiteReflection.ResolveSiteById(rt, rec.Aux);
                    if (site == null || GeoSiteReflection.GetActiveMission(site) == null) return null;
                    break;
                default: return null;
            }

            var obj = Activator.CreateInstance(type, nonPublic: true);
            _givenByField.SetValue(obj, givenBy);
            _criticalField.SetValue(obj, rec.Critical);
            if ((rec.Flags & ObjectivesSnapshot.FlagTitlePresent) != 0)
                _titleField.SetValue(obj, _locBindCtor.Invoke(new object[] { rec.TitleKey ?? "", (rec.Flags & ObjectivesSnapshot.FlagTitleNoLoc) != 0 }));
            if ((rec.Flags & ObjectivesSnapshot.FlagDescPresent) != 0)
                _descField.SetValue(obj, _locBindCtor.Invoke(new object[] { rec.DescKey ?? "", (rec.Flags & ObjectivesSnapshot.FlagDescNoLoc) != 0 }));
            switch (rec.Disc)
            {
                case ObjectivesSnapshot.DiscEvent:
                    _eventIdField.SetValue(obj, rec.Payload);
                    if (_eventCompletedField != null) _eventCompletedField.SetValue(obj, rec.Completed);
                    break;
                case ObjectivesSnapshot.DiscDiplomatic:
                    _forFactionField.SetValue(obj, DefReflection.GetDefByGuid(rec.Payload));
                    break;
                case ObjectivesSnapshot.DiscResearch:
                    _researchField.SetValue(obj, ResolveResearchElement(rt, rec.Payload));
                    break;
                case ObjectivesSnapshot.DiscMission:
                    var site2 = GeoSiteReflection.ResolveSiteById(rt, rec.Aux);
                    _missionField.SetValue(obj, GeoSiteReflection.GetActiveMission(site2));
                    break;
            }
            return obj;
        }

        /// <summary>The client faction's live <c>ResearchElement</c> for a wire research id
        /// (<c>Research.GetResearchById</c>, the ch2 lookup), or null.</summary>
        private static object ResolveResearchElement(GeoRuntime rt, string researchId)
        {
            if (string.IsNullOrEmpty(researchId)) return null;
            try
            {
                var research = ResearchStateReflection.GetResearch(rt);
                if (research == null) return null;
                var m = AccessTools.Method(research.GetType(), "GetResearchById", new[] { typeof(string) });
                return m?.Invoke(research, new object[] { researchId });
            }
            catch { return null; }
        }

        /// <summary>Raise the faction's field-like <c>ObjectivesChanged(GeoFaction)</c> once via its
        /// backing delegate — the native repaint signal for a stamp-only apply (the exact event
        /// <c>AddObjective</c>/<c>RemoveObjective</c> fire natively). Best-effort.</summary>
        private static void RaiseObjectivesChanged(object fac)
        {
            try
            {
                var del = _objectivesChangedField?.GetValue(fac) as Delegate;
                del?.DynamicInvoke(fac);
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] ObjectivesReflection.RaiseObjectivesChanged failed: " + ex.Message); }
        }

        // ─── host dirty-trigger subscriptions ─────────────────────────────────────────────

        /// <summary>
        /// Host: subscribe a no-arg callback to the player faction's <c>ObjectivesChanged</c> +
        /// <c>ObjectiveCompleted</c> (add/remove/update funnel, GeoFaction.cs:1004/1014/1733 + the
        /// completion event that does NOT re-fire ObjectivesChanged) via the shared DynamicMethod
        /// adapter. Returns an opaque token for <see cref="Unsubscribe"/>, or null.
        /// </summary>
        public static object SubscribeObjectiveEvents(GeoRuntime rt, Action onChanged)
        {
            if (onChanged == null) return null;
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;
                var token = new EventToken();
                SubscribeOne(token, fac, "ObjectivesChanged", onChanged);
                SubscribeOne(token, fac, "ObjectiveCompleted", onChanged);
                return token.Entries.Count > 0 ? token : null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ObjectivesReflection.SubscribeObjectiveEvents failed: " + ex.Message); return null; }
        }

        /// <summary>Host: subscribe a no-arg callback to <c>GeoscapeEventSystem.VariableSet</c>
        /// (<c>Action&lt;string,int&gt;</c>, fired by every <c>SetVariable</c> — the TFTV quest-step
        /// writer). Returns an opaque token for <see cref="Unsubscribe"/>, or null.</summary>
        public static object SubscribeVariableSet(GeoRuntime rt, Action onChanged)
        {
            if (onChanged == null) return null;
            try
            {
                Ensure(rt);
                var es = GetEventSystem(rt);
                if (es == null) return null;
                var token = new EventToken();
                SubscribeOne(token, es, "VariableSet", onChanged);
                return token.Entries.Count > 0 ? token : null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ObjectivesReflection.SubscribeVariableSet failed: " + ex.Message); return null; }
        }

        private static void SubscribeOne(EventToken token, object target, string eventName, Action onChanged)
        {
            var evt = target.GetType().GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance);
            if (evt == null) return;
            var handler = MakeAdapter(evt, onChanged);
            if (handler == null) return;
            evt.AddEventHandler(target, handler);
            token.Entries.Add((target, evt, handler));
        }

        public static void Unsubscribe(object token)
        {
            if (!(token is EventToken t)) return;
            foreach (var (target, evt, handler) in t.Entries)
            {
                try { evt.RemoveEventHandler(target, handler); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] ObjectivesReflection.Unsubscribe failed: " + ex.Message); }
            }
            t.Entries.Clear();
        }

        private sealed class EventToken
        {
            public readonly List<(object target, EventInfo evt, Delegate handler)> Entries
                = new List<(object, EventInfo, Delegate)>();
        }

        /// <summary>Emit a DynamicMethod delegate matching <paramref name="evt"/>'s signature that ignores
        /// its args and calls <paramref name="onChanged"/> (the <c>DiplomacyReflection.MakeAdapter</c> pattern).</summary>
        private static Delegate MakeAdapter(EventInfo evt, Action onChanged)
        {
            if (evt == null) return null;
            try
            {
                Type handlerType = evt.EventHandlerType;
                MethodInfo invoke = handlerType.GetMethod("Invoke");
                if (invoke == null) return null;
                ParameterInfo[] ps = invoke.GetParameters();

                Type[] dmSig = new Type[ps.Length + 1];
                dmSig[0] = typeof(Action);
                for (int i = 0; i < ps.Length; i++) dmSig[i + 1] = ps[i].ParameterType;

                var dm = new DynamicMethod("Objectives_Event_Adapter", typeof(void), dmSig,
                    typeof(ObjectivesReflection).Module, skipVisibility: true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke"));
                il.Emit(OpCodes.Ret);

                return dm.CreateDelegate(handlerType, onChanged);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ObjectivesReflection.MakeAdapter failed: " + ex.Message); return null; }
        }
    }
}
