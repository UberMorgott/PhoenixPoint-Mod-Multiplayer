using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Reflection bridge for geoscape event-choice resolution.
    ///
    /// Verified against the decompile (2026-06-15):
    ///   • apply:    <c>GeoscapeEvent.CompleteEvent(GeoEventChoice choice, GeoFaction faction)</c>
    ///               (GeoscapeEvent.cs:86) — selects a choice, applies its faction reward.
    ///   • id:       <c>GeoscapeEvent.EventID</c> (public string, :18).
    ///   • choices:  <c>GeoscapeEvent.EventData</c> (GeoscapeEventData, :30) →
    ///               <c>GeoscapeEventData.Choices</c> (List&lt;GeoEventChoice&gt;, :54); choice ↔ index
    ///               via <c>Choices.IndexOf/[]</c> (the game itself keys choices by index, :97).
    ///   • def lookup: <c>GeoLevelController.EventSystem</c> (GeoscapeEventSystem, GeoLevelController.cs:105)
    ///               → <c>GetEventByID(string, bool canFail)</c> (:280) → GeoscapeEventDef →
    ///               <c>GeoscapeEventDef.GeoscapeEventData</c> (GeoscapeEventDef.cs:18).
    ///   • context:  reconstruct via <c>new GeoscapeEventContext(GeoSite site, GeoFaction faction)</c>
    ///               (GeoscapeEventContext.cs:191) using <c>PhoenixFaction.StartingBase</c>
    ///               (GeoPhoenixFaction.cs:230) — mirrors GeoscapeEvent.CompleteMarketplaceEvent (:77).
    ///   • event ctor: <c>new GeoscapeEvent(GeoscapeEventData, GeoscapeEventContext)</c> (:54).
    ///
    /// NOTE (flagged, not in-game verifiable here): the host's CompleteEvent is authoritative and its
    /// currency outcome converges to clients through the wallet echo (mechanism A). On clients we
    /// reconstruct a fresh GeoscapeEvent from the def + StartingBase context and replay CompleteEvent so
    /// the choice/record is applied; non-currency outcomes (e.g. research reveals) ride their own synced
    /// actions. If a precise active-instance handle is later needed, key it through the event UI module.
    /// </summary>
    public static class EventReflection
    {
        /// <summary>Sentinel for an intentional null / "decline" choice (the game keys decline as the null choice).</summary>
        public const int ChoiceDecline = -1;

        /// <summary>
        /// Sentinel for a choice we could NOT resolve (null event, missing data/choices, or the choice was
        /// not found in the list). Distinct from <see cref="ChoiceDecline"/> so a real positive choice that
        /// fails lookup is never silently replicated as a decline — callers fail OPEN instead of broadcasting.
        /// </summary>
        public const int ChoiceLookupFailed = int.MinValue;

        private static bool _ready;
        private static Type _eventType;        // GeoscapeEvent
        private static Type _eventDataType;     // GeoscapeEventData
        private static Type _eventDefType;      // GeoscapeEventDef
        private static Type _eventSystemType;   // GeoscapeEventSystem
        private static Type _contextType;       // GeoscapeEventContext
        private static Type _geoFactionType;    // GeoFaction
        private static Type _geoSiteType;       // GeoSite
        private static MethodInfo _completeEvent;   // GeoscapeEvent.CompleteEvent(GeoEventChoice, GeoFaction)
        private static FieldInfo _eventIdField;     // GeoscapeEvent.EventID
        private static PropertyInfo _eventDataProp; // GeoscapeEvent.EventData
        private static FieldInfo _choicesField;     // GeoscapeEventData.Choices
        private static FieldInfo _defDataField;     // GeoscapeEventDef.GeoscapeEventData
        private static MethodInfo _getEventById;    // GeoscapeEventSystem.GetEventByID(string, bool)
        private static FieldInfo _eventSystemField; // GeoLevelController.EventSystem
        private static PropertyInfo _startingBaseProp; // GeoPhoenixFaction.StartingBase
        private static ConstructorInfo _eventCtor;     // GeoscapeEvent(GeoscapeEventData, GeoscapeEventContext)
        private static ConstructorInfo _contextCtor;   // GeoscapeEventContext(GeoSite, GeoFaction)
        private static ConstructorInfo _contextCtor3;  // GeoscapeEventContext(GeoSite, GeoFaction, GeoVehicle)
        private static FieldInfo _mapField;            // GeoLevelController.Map
        private static PropertyInfo _allSitesProp;     // GeoMap.AllSites
        private static FieldInfo _siteIdField;         // GeoSite.SiteId

        // Vehicle context (the [AircraftName] token derefs Context.Vehicle.Name → NRE when Vehicle == null).
        private static Type _geoVehicleType;           // GeoVehicle
        private static FieldInfo _vehicleIdField;      // GeoVehicle.VehicleID (public int, serialized; -1 = none)
        private static PropertyInfo _mapVehiclesProp;  // GeoMap.Vehicles (IList<GeoVehicle>)
        private static PropertyInfo _siteVehiclesProp; // GeoSite.Vehicles (IEnumerable<GeoVehicle> at this site)

        // Synthetic-record support (client INFO local-dismiss fix). The client's reconstructed event has
        // Record == null; UIStateGeoscapeEvent.ExitState (UIStateGeoscapeEvent.cs:61) reads
        // base.Event.Record.State and force-CompleteEvents when State==Triggered → on the client that both
        // NREs (null Record) and would apply a spurious local outcome. We attach a record already in the
        // Completed state so the guard is false (no force-complete, no NRE) and the dialog just closes.
        private static Type _recordType;               // GeoscapeEventRecord
        private static FieldInfo _recordStateField;    // GeoscapeEventRecord._state (GeoscapeEventRecordState)
        private static PropertyInfo _recordProp;        // GeoscapeEvent.Record { get; set; }

        /// <summary>
        /// GeoscapeEventRecordState.Completed (GeoscapeEventRecordState.cs:8). Verified int value 3. Used to
        /// stamp the synthetic record so <c>UIStateGeoscapeEvent.ExitState</c>'s
        /// <c>Record.State == Triggered</c> guard is false on a client local-dismiss.
        /// </summary>
        public const int RecordStateCompleted = 3;

        private static void Ensure()
        {
            if (_ready) return;
            _eventType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");
            _eventDataType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.Eventus.GeoscapeEventData");
            _eventDefType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.Eventus.GeoscapeEventDef");
            _eventSystemType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEventSystem");
            _contextType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEventContext");
            _geoFactionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            _geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var geoLevelType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            var choiceType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
            if (_eventType == null || _eventDataType == null || _contextType == null
                || _geoFactionType == null || _geoSiteType == null || geoLevelType == null || choiceType == null) return;

            _completeEvent = AccessTools.Method(_eventType, "CompleteEvent", new[] { choiceType, _geoFactionType });
            _eventIdField = AccessTools.Field(_eventType, "EventID");
            _eventDataProp = AccessTools.Property(_eventType, "EventData");
            _choicesField = AccessTools.Field(_eventDataType, "Choices");
            if (_eventDefType != null) _defDataField = AccessTools.Field(_eventDefType, "GeoscapeEventData");
            if (_eventSystemType != null)
                _getEventById = AccessTools.Method(_eventSystemType, "GetEventByID", new[] { typeof(string), typeof(bool) });
            _eventSystemField = AccessTools.Field(geoLevelType, "EventSystem");
            _eventCtor = AccessTools.Constructor(_eventType, new[] { _eventDataType, _contextType });
            _contextCtor = AccessTools.Constructor(_contextType, new[] { _geoSiteType, _geoFactionType });
            // Site-by-id resolution for context fidelity (GeoLevelController.Map:97 → GeoMap.AllSites:251 → GeoSite.SiteId:45).
            _mapField = AccessTools.Field(geoLevelType, "Map");
            var geoMapType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoMap");
            if (geoMapType != null) _allSitesProp = AccessTools.Property(geoMapType, "AllSites");
            _siteIdField = AccessTools.Field(_geoSiteType, "SiteId");

            // Vehicle context: the host raises a site-visited event with the 3-arg ctor
            // GeoscapeEventContext(GeoSite, GeoFaction, GeoVehicle) (GeoscapeEventSystem.Faction_VehicleVisitedSite:417,
            // GeoscapeEventContext.cs:146). Clients must rebuild the same 3-arg context so the [AircraftName] token
            // (Context.Vehicle.Name, GeoscapeEventContext.cs:32) doesn't NRE inside the native render. Resolve the
            // GeoVehicle by GeoVehicle.VehicleID (public int :51, serialized) via GeoMap.Vehicles (IList :257) or
            // GeoSite.Vehicles (vehicles currently at the site, :239).
            _geoVehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            if (_geoVehicleType != null)
            {
                _vehicleIdField = AccessTools.Field(_geoVehicleType, "VehicleID");
                _contextCtor3 = AccessTools.Constructor(_contextType, new[] { _geoSiteType, _geoFactionType, _geoVehicleType });
                _siteVehiclesProp = AccessTools.Property(_geoSiteType, "Vehicles");
                if (geoMapType != null) _mapVehiclesProp = AccessTools.Property(geoMapType, "Vehicles");
            }

            // Synthetic-record (client INFO local-dismiss): GeoscapeEvent.Record { get; set; } (GeoscapeEvent.cs:42)
            // backed by GeoscapeEventRecord._state (GeoscapeEventRecord.cs:20). We never invoke its ctor (needs a
            // live TimeUnit) — an uninitialized instance with _state stamped Completed is all ExitState reads.
            _recordType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEventRecord");
            if (_recordType != null) _recordStateField = AccessTools.Field(_recordType, "_state");
            _recordProp = AccessTools.Property(_eventType, "Record");

            _ready = _completeEvent != null && _eventIdField != null && _eventDataProp != null
                     && _choicesField != null;
        }

        // ─── interceptor-side getters (off the live GeoscapeEvent) ────────

        /// <summary>Read <c>GeoscapeEvent.EventID</c>.</summary>
        public static string GetEventId(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return null;
            try { Ensure(); return _eventIdField?.GetValue(geoscapeEvent) as string; }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.GetEventId failed: " + ex.Message); return null; }
        }

        // ─── two-class (INFO / CHOICE) dialog classification ──────────────

        /// <summary>
        /// Pure classifier mirroring the game's own <c>GeoscapeEventData.HasSingleChoice =&gt; Choices.Count &lt;= 1</c>
        /// (GeoscapeEventData.cs:65): an event with &gt;= 2 choices is a real CHOICE event; &lt;= 1 is INFO.
        /// </summary>
        public static bool IsChoiceEvent(int choicesCount) => choicesCount >= 2;

        /// <summary>
        /// Read <c>GeoscapeEvent.EventData.Choices.Count</c> off a live event. Returns -1 if it can't be read
        /// (null event / missing data / introspection failure) — callers MUST treat -1 as "ambiguous → lock as
        /// CHOICE", never as INFO, so a client never locally branches an outcome on an unreadable event.
        /// </summary>
        public static int GetChoiceCount(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return -1;
            try
            {
                Ensure();
                var data = _eventDataProp?.GetValue(geoscapeEvent, null);
                var choices = _choicesField?.GetValue(data) as IList;
                return choices?.Count ?? -1;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.GetChoiceCount failed: " + ex.Message); return -1; }
        }

        /// <summary>
        /// Client-lock predicate for the open event: true = CHOICE (lock the modal, buttons inert, no local
        /// close), false = INFO (each player dismisses locally, no outcome). Safe default = locked: an
        /// unreadable choice count (-1) returns true so the client never locally resolves an ambiguous event.
        /// </summary>
        public static bool IsClientChoiceLocked(object geoscapeEvent)
        {
            int count = GetChoiceCount(geoscapeEvent);
            if (count < 0) return true;            // ambiguous → treat as CHOICE (locked)
            return IsChoiceEvent(count);
        }

        /// <summary>
        /// Stamp a synthetic Completed <c>GeoscapeEventRecord</c> onto a (client-reconstructed) event so
        /// <c>UIStateGeoscapeEvent.ExitState</c>'s <c>Record.State == Triggered</c> force-complete guard is false
        /// on a local dismiss (no NRE on a null Record, no spurious local outcome). Best-effort no-op on failure.
        /// </summary>
        public static void AttachCompletedRecord(object geoscapeEvent)
        {
            try
            {
                Ensure();
                if (geoscapeEvent == null || _recordType == null || _recordStateField == null || _recordProp == null) return;
                if (_recordProp.GetValue(geoscapeEvent, null) != null) return; // already has a record → leave it
                // Uninitialized instance (skip the TimeUnit-taking ctor) with _state = Completed.
                object record = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(_recordType);
                _recordStateField.SetValue(record, RecordStateCompleted);
                _recordProp.SetValue(geoscapeEvent, record, null);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.AttachCompletedRecord failed: " + ex.Message); }
        }

        /// <summary>
        /// Index of <paramref name="choice"/> within the live event's Choices.
        /// Returns <see cref="ChoiceDecline"/> (-1) for an intentional null/decline choice,
        /// <see cref="ChoiceLookupFailed"/> when the event/data/choices can't be introspected or the choice
        /// isn't found. Callers MUST treat the failure sentinel as "abort + fail open", never as a decline.
        /// </summary>
        public static int GetChoiceIndex(object geoscapeEvent, object choice)
        {
            if (choice == null) return ChoiceDecline;        // intentional decline / no-choice
            if (geoscapeEvent == null) return ChoiceLookupFailed; // can't introspect a real choice → lookup failed
            try
            {
                Ensure();
                var data = _eventDataProp?.GetValue(geoscapeEvent, null);
                var choices = _choicesField?.GetValue(data) as IList;
                if (choices == null) return ChoiceLookupFailed; // no choice list → can't resolve a real choice
                int idx = choices.IndexOf(choice);
                return idx >= 0 ? idx : ChoiceLookupFailed;     // not found ≠ decline
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.GetChoiceIndex failed: " + ex.Message); return ChoiceLookupFailed; }
        }

        // ─── apply (Apply side) ───────────────────────────────────────────

        /// <summary>
        /// Reconstruct the event from its def + a StartingBase context and apply the indexed choice.
        /// <paramref name="choiceIndex"/> &lt; 0 selects the null ("decline") choice.
        /// </summary>
        public static void CompleteEvent(GeoRuntime rt, string eventId, int choiceIndex)
        {
            try
            {
                Ensure();
                if (!_ready || string.IsNullOrEmpty(eventId)) return;

                var fac = rt?.PhoenixFaction();
                if (fac == null) return;

                // Resolve the event data via the level's event system + def.
                object eventData = ResolveEventData(rt, eventId);
                if (eventData == null) return;

                // Build a context from the player's StartingBase site + faction.
                object site = GetStartingBase(fac);
                if (site == null || _contextCtor == null || _eventCtor == null) return;
                object context = _contextCtor.Invoke(new[] { site, fac });
                object geoEvent = _eventCtor.Invoke(new[] { eventData, context });

                // Resolve the choice by index (null when index < 0 → decline).
                object choice = null;
                if (choiceIndex >= 0)
                {
                    var choices = _choicesField?.GetValue(eventData) as IList;
                    if (choices != null && choiceIndex < choices.Count) choice = choices[choiceIndex];
                }
                _completeEvent.Invoke(geoEvent, new[] { choice, fac });
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.CompleteEvent failed: " + ex.Message); }
        }

        private static object ResolveEventData(GeoRuntime rt, string eventId)
        {
            try
            {
                var geo = rt?.GeoLevel();
                if (geo == null) return null;
                var system = _eventSystemField?.GetValue(geo);
                if (system == null || _getEventById == null || _defDataField == null) return null;
                var def = _getEventById.Invoke(system, new object[] { eventId, true }); // canFail=true → null, no throw
                if (def == null) return null;
                return _defDataField.GetValue(def);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.ResolveEventData failed: " + ex.Message); return null; }
        }

        private static object GetStartingBase(object phoenixFaction)
        {
            try
            {
                if (_startingBaseProp == null || _startingBaseProp.DeclaringType == null
                    || !_startingBaseProp.DeclaringType.IsInstanceOfType(phoenixFaction))
                    _startingBaseProp = AccessTools.Property(phoenixFaction.GetType(), "StartingBase");
                return _startingBaseProp?.GetValue(phoenixFaction, null);
            }
            catch { return null; }
        }

        // ─── display side (host extract + client reconstruct) ─────────────

        /// <summary>
        /// Host: read <c>GeoscapeEvent.Context.Site.SiteId</c> (GeoSite.SiteId, int, -1 = none) off the
        /// live raised event so clients can rebuild the same context. Returns -1 on any failure.
        /// </summary>
        public static int GetSiteId(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return -1;
            try
            {
                Ensure();
                var ctxField = AccessTools.Field(geoscapeEvent.GetType(), "Context"); // public readonly GeoscapeEventContext (:21)
                var ctx = ctxField?.GetValue(geoscapeEvent);
                if (ctx == null) return -1;
                var siteField = AccessTools.Field(ctx.GetType(), "Site"); // public GeoSite Site (:47)
                var site = siteField?.GetValue(ctx);
                if (site == null || _siteIdField == null) return -1;
                return (int)(_siteIdField.GetValue(site) ?? -1);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.GetSiteId failed: " + ex.Message); return -1; }
        }

        /// <summary>
        /// Host: read <c>GeoscapeEvent.Context.Vehicle.VehicleID</c> (GeoVehicle.VehicleID, int, -1 = none) off
        /// the live raised event so clients can rebuild the SAME 3-arg context. A site-visited event carries the
        /// visiting vehicle (GeoscapeEventSystem.cs:417); without it the client's [AircraftName] token NREs.
        /// Returns -1 on any failure / no vehicle (most event types).
        /// </summary>
        public static int GetVehicleId(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return -1;
            try
            {
                Ensure();
                var ctxField = AccessTools.Field(geoscapeEvent.GetType(), "Context"); // public readonly GeoscapeEventContext (:21)
                var ctx = ctxField?.GetValue(geoscapeEvent);
                if (ctx == null) return -1;
                var vehField = AccessTools.Field(ctx.GetType(), "Vehicle"); // public GeoVehicle Vehicle (:53)
                var vehicle = vehField?.GetValue(ctx);
                if (vehicle == null || _vehicleIdField == null) return -1;
                return (int)(_vehicleIdField.GetValue(vehicle) ?? -1);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.GetVehicleId failed: " + ex.Message); return -1; }
        }

        /// <summary>
        /// Client: reconstruct a <c>GeoscapeEvent</c> from its def for DISPLAY (no choice applied).
        /// Context site = the GeoSite whose SiteId matches <paramref name="siteId"/>, else StartingBase.
        /// When a vehicle resolves (broadcast <paramref name="vehicleId"/> → a vehicle at the site → null) the
        /// 3-arg <c>GeoscapeEventContext(site, faction, vehicle)</c> ctor is used so the [AircraftName] token
        /// (and any other Vehicle deref) has a real vehicle and the native render fills real text + choices
        /// instead of throwing into the prefab-placeholder state. Falls back to the 2-arg ctor otherwise.
        /// Returns null on any failure (caller best-effort no-ops).
        /// </summary>
        public static object BuildEvent(GeoRuntime rt, string eventId, int siteId, int vehicleId = -1)
        {
            try
            {
                Ensure();
                if (!_ready || string.IsNullOrEmpty(eventId)) return null;

                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;

                object eventData = ResolveEventData(rt, eventId);
                if (eventData == null) return null;

                object site = ResolveSiteById(rt, siteId) ?? GetStartingBase(fac);
                if (site == null || _eventCtor == null) return null;

                // Vehicle fallback chain: broadcast id → a vehicle currently at the resolved site → null.
                object vehicle = ResolveVehicleById(rt, vehicleId) ?? ResolveVehicleAtSite(site);

                object context;
                if (vehicle != null && _contextCtor3 != null)
                    context = _contextCtor3.Invoke(new[] { site, fac, vehicle });
                else if (_contextCtor != null)
                    context = _contextCtor.Invoke(new[] { site, fac });
                else
                    return null;

                object geoEvent = _eventCtor.Invoke(new[] { eventData, context });
                // Client-only display reconstruction: stamp a Completed record so a local dismiss does NOT
                // hit UIStateGeoscapeEvent.ExitState's null-Record NRE / force-complete (INFO local-hide fix).
                AttachCompletedRecord(geoEvent);
                return geoEvent;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.BuildEvent failed: " + ex.Message); return null; }
        }

        /// <summary>Client: GeoVehicle whose VehicleID matches <paramref name="vehicleId"/> via GeoMap.Vehicles, else null.</summary>
        private static object ResolveVehicleById(GeoRuntime rt, int vehicleId)
        {
            try
            {
                if (vehicleId < 0 || _mapField == null || _mapVehiclesProp == null || _vehicleIdField == null) return null;
                var geo = rt?.GeoLevel();
                if (geo == null) return null;
                var map = _mapField.GetValue(geo);
                if (map == null) return null;
                var vehicles = _mapVehiclesProp.GetValue(map, null) as IEnumerable;
                if (vehicles == null) return null;
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    var id = _vehicleIdField.GetValue(v);
                    if (id is int i && i == vehicleId) return v;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>Client fallback: the first GeoVehicle currently at <paramref name="site"/> (GeoSite.Vehicles), else null.</summary>
        private static object ResolveVehicleAtSite(object site)
        {
            try
            {
                if (site == null || _siteVehiclesProp == null) return null;
                var vehicles = _siteVehiclesProp.GetValue(site, null) as IEnumerable;
                if (vehicles == null) return null;
                foreach (var v in vehicles)
                    if (v != null) return v;
                return null;
            }
            catch { return null; }
        }

        private static object ResolveSiteById(GeoRuntime rt, int siteId)
        {
            try
            {
                if (siteId < 0 || _mapField == null || _allSitesProp == null || _siteIdField == null) return null;
                var geo = rt?.GeoLevel();
                if (geo == null) return null;
                var map = _mapField.GetValue(geo);
                if (map == null) return null;
                var sites = _allSitesProp.GetValue(map, null) as IEnumerable;
                if (sites == null) return null;
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
    }
}
