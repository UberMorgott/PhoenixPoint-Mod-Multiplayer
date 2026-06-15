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

        /// <summary>Index of <paramref name="choice"/> within the live event's Choices, or -1.</summary>
        public static int GetChoiceIndex(object geoscapeEvent, object choice)
        {
            if (geoscapeEvent == null) return -1;
            if (choice == null) return -1; // null choice = "decline / no choice"
            try
            {
                Ensure();
                var data = _eventDataProp?.GetValue(geoscapeEvent, null);
                var choices = _choicesField?.GetValue(data) as IList;
                return choices?.IndexOf(choice) ?? -1;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventReflection.GetChoiceIndex failed: " + ex.Message); return -1; }
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
    }
}
