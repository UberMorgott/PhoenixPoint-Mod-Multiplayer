using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Network.Sync
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

        // Native OK-label (result page dismiss button) reflection chain: GeoLevelController.View →
        // GeoscapeView.GeoscapeModules → GeoscapeModulesData.SiteEncountersModule → UIModuleSiteEncounters.OKTextKey
        // (LocalizedTextBind) → LocalizationKey (read via the existing _localizedKeyField). Best-effort: any missing
        // link → literal "OK" fallback (ChooseResultOkLabelKey).
        private static FieldInfo _glViewField;          // GeoLevelController.View
        private static FieldInfo _gvModulesField;        // GeoscapeView.GeoscapeModules
        private static FieldInfo _gmSiteEncModuleField;  // GeoscapeModulesData.SiteEncountersModule
        private static FieldInfo _encOkTextKeyField;     // UIModuleSiteEncounters.OKTextKey (LocalizedTextBind)
        private static FieldInfo _encGeoEventField;      // UIModuleSiteEncounters._geoEvent (live GeoscapeEvent shown)
        private static FieldInfo _encPagingField;        // UIModuleSiteEncounters._pagingEvent (paging text flag)
        private static MethodInfo _encOnChoiceSelected;  // UIModuleSiteEncounters.OnChoiceSelected(GeoEventChoice) (private)
        private static PropertyInfo _isCompletedProp;    // GeoscapeEvent.IsCompleted
        private static PropertyInfo _startingBaseProp; // GeoPhoenixFaction.StartingBase
        private static ConstructorInfo _eventCtor;     // GeoscapeEvent(GeoscapeEventData, GeoscapeEventContext)
        private static ConstructorInfo _contextCtor;   // GeoscapeEventContext(GeoSite, GeoFaction)
        private static ConstructorInfo _contextCtor3;  // GeoscapeEventContext(GeoSite, GeoFaction, GeoVehicle)
        private static ConstructorInfo _contextCtorSiteless;  // GeoscapeEventContext(GeoFaction, GeoFaction) — Site=null (native :176)
        private static FieldInfo _mapField;            // GeoLevelController.Map
        private static PropertyInfo _allSitesProp;     // GeoMap.AllSites
        private static FieldInfo _siteIdField;         // GeoSite.SiteId

        // Synthetic RESULT/OUTCOME page (client follow-up display, mirrors UIModuleSiteEncounters.SetClosingEncounter
        // text half ONLY — never the ShowReward/GeoFactionReward.Apply state mutation). Verified 2026-06-17:
        //   • GeoscapeEvent.Context (public readonly GeoscapeEventContext, :21), .EventData (get/private set, :30).
        //   • GeoscapeEventData.{EventID,Flavour,Leader} (string :37/39/41), .Description (List<EventTextVariation> :51),
        //     .Choices (List<GeoEventChoice> :54).
        //   • EventTextVariation.General (LocalizedTextBind :12), .GetText(GeoscapeEventContext) (:20).
        //   • GeoEventChoice.{Text,Outcome} (LocalizedTextBind :12 / GeoEventChoiceOutcome :18).
        //   • GeoEventChoiceOutcome.OutcomeText (EventTextVariation :25).
        //   • GeoscapeEventContext.ReplaceEventTokens(string) (:225).
        private static FieldInfo _ctxField;            // GeoscapeEvent.Context
        private static PropertyInfo _eventDataSetProp; // GeoscapeEvent.EventData (for synthetic data swap, has private set)
        private static FieldInfo _edEventIdField;      // GeoscapeEventData.EventID
        private static FieldInfo _edFlavourField;      // GeoscapeEventData.Flavour
        private static FieldInfo _edLeaderField;       // GeoscapeEventData.Leader
        private static FieldInfo _edTitleField;        // GeoscapeEventData.Title (LocalizedTextBind, GeoscapeEventData.cs:43)
        private static FieldInfo _edDescriptionField;  // GeoscapeEventData.Description (List<EventTextVariation>)
        private static Type _textVariationType;        // EventTextVariation
        private static FieldInfo _tvGeneralField;      // EventTextVariation.General (LocalizedTextBind)
        private static MethodInfo _tvGetTextMethod;    // EventTextVariation.GetText(GeoscapeEventContext)
        private static Type _localizedTextType;        // Base.UI.LocalizedTextBind
        private static ConstructorInfo _localizedTextCtor2; // LocalizedTextBind(string, bool doNotLocalize)
        private static FieldInfo _localizedKeyField;   // LocalizedTextBind.LocalizationKey (for empty-check)
        private static MethodInfo _localizeMethod;     // LocalizedTextBind.Localize(string language = null) (LocalizedTextBind.cs:35)
        private static Type _choiceType2;              // GeoEventChoice (for synthetic OK button)
        private static FieldInfo _choiceTextField;     // GeoEventChoice.Text (LocalizedTextBind)
        private static FieldInfo _choiceOutcomeField;  // GeoEventChoice.Outcome (GeoEventChoiceOutcome)
        private static FieldInfo _outcomeTextField;    // GeoEventChoiceOutcome.OutcomeText (EventTextVariation)
        private static FieldInfo _outcomeStartMissionField; // GeoEventChoiceOutcome.StartMission (OutcomeStartMission, GeoEventChoiceOutcome.cs:55)
        private static FieldInfo _startMissionTypeDefField; // OutcomeStartMission.MissionTypeDef (CustomMissionTypeDef, OutcomeStartMission.cs:9)
        private static MethodInfo _replaceTokensMethod; // GeoscapeEventContext.ReplaceEventTokens(string)
        private static ConstructorInfo _eventDataCtor;  // GeoscapeEventData() default ctor

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
            // Faction-only ctor → a genuinely SITELESS context (Site == null). Native render is title-only,
            // no NRE (UIModuleSiteEncounters.ShowEncounter null-checks Context.Site at :203; GetEventArt at
            // SiteEncountersArtCollectionDef.cs:125). Used for siteless events AND site events whose site is
            // absent on the client — NEVER StartingBase ("Точка Феникс" is the StartingBase subtitle).
            _contextCtorSiteless = AccessTools.Constructor(_contextType, new[] { _geoFactionType, _geoFactionType });
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

            // Synthetic RESULT/OUTCOME page support (client follow-up display, text half of SetClosingEncounter).
            _ctxField = AccessTools.Field(_eventType, "Context");
            _eventDataSetProp = AccessTools.Property(_eventType, "EventData"); // private set → SetValue via reflection
            _edEventIdField = AccessTools.Field(_eventDataType, "EventID");
            _edFlavourField = AccessTools.Field(_eventDataType, "Flavour");
            _edLeaderField = AccessTools.Field(_eventDataType, "Leader");
            _edTitleField = AccessTools.Field(_eventDataType, "Title");
            _edDescriptionField = AccessTools.Field(_eventDataType, "Description");
            _eventDataCtor = AccessTools.Constructor(_eventDataType, Type.EmptyTypes);
            _textVariationType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.EventTextVariation");
            if (_textVariationType != null)
            {
                _tvGeneralField = AccessTools.Field(_textVariationType, "General");
                _tvGetTextMethod = AccessTools.Method(_textVariationType, "GetText", new[] { _contextType });
            }
            _localizedTextType = AccessTools.TypeByName("Base.UI.LocalizedTextBind");
            if (_localizedTextType != null)
            {
                _localizedTextCtor2 = AccessTools.Constructor(_localizedTextType, new[] { typeof(string), typeof(bool) });
                _localizedKeyField = AccessTools.Field(_localizedTextType, "LocalizationKey");
                _localizeMethod = AccessTools.Method(_localizedTextType, "Localize", new[] { typeof(string) });
            }
            _choiceType2 = choiceType;
            _choiceTextField = AccessTools.Field(choiceType, "Text");
            _choiceOutcomeField = AccessTools.Field(choiceType, "Outcome");
            var outcomeType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoiceOutcome");
            if (outcomeType != null)
            {
                _outcomeTextField = AccessTools.Field(outcomeType, "OutcomeText");
                _outcomeStartMissionField = AccessTools.Field(outcomeType, "StartMission");
            }
            // Mission-deploy discriminator: a choice whose Outcome.StartMission.MissionTypeDef != null is a
            // Deploy/Land button (GeoEventChoiceOutcome.cs:315 → RewardStartCustomMission). Best-effort — a null
            // field only makes IsMissionDeployEvent fail OPEN (broadcast as before), never suppress a legit event.
            var startMissionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.OutcomeStartMission");
            if (startMissionType != null) _startMissionTypeDefField = AccessTools.Field(startMissionType, "MissionTypeDef");
            _replaceTokensMethod = AccessTools.Method(_contextType, "ReplaceEventTokens", new[] { typeof(string) });

            // Native OK-label chain (best-effort; NOT part of _ready — a missing link just falls back to "OK").
            _glViewField = AccessTools.Field(geoLevelType, "View");
            var geoscapeViewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            if (geoscapeViewType != null) _gvModulesField = AccessTools.Field(geoscapeViewType, "GeoscapeModules");
            // ROOT CAUSE (in-game false-negative, run 2026-07-03 00:27): GeoscapeModulesData lives in namespace
            // Base.UI (decompile Base.UI/GeoscapeModulesData.cs:7; SiteEncountersModule field :32) — NOT
            // PhoenixPoint.Geoscape.View. The old wrong-namespace TypeByName returned null → _gmSiteEncModuleField
            // stayed null forever → TryHostNativeResolve AND TryHostNativeAdvanceSingleChoice always tripped
            // guard=not-ready/missing-member and the host never drove a client-requested prompt→result advance.
            // Self-grounding fix: take the type straight off the verified GeoscapeView.GeoscapeModules field
            // (its FieldType IS GeoscapeModulesData), with the CORRECT literal name as fallback.
            var geoModulesDataType = _gvModulesField != null
                ? _gvModulesField.FieldType
                : AccessTools.TypeByName("Base.UI.GeoscapeModulesData");
            if (geoModulesDataType != null) _gmSiteEncModuleField = AccessTools.Field(geoModulesDataType, "SiteEncountersModule");
            var siteEncModuleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            if (siteEncModuleType != null)
            {
                _encOkTextKeyField = AccessTools.Field(siteEncModuleType, "OKTextKey");
                // Host native-resolve chain (best-effort; NOT part of _ready). Drives the host's OWN open modal
                // through the exact native click path so the host renders the native result/reward page.
                _encGeoEventField = AccessTools.Field(siteEncModuleType, "_geoEvent");        // live GeoscapeEvent shown
                _encPagingField = AccessTools.Field(siteEncModuleType, "_pagingEvent");       // true while paging text
                _encOnChoiceSelected = AccessTools.Method(siteEncModuleType, "OnChoiceSelected", new[] { choiceType }); // private native click handler
            }
            _isCompletedProp = AccessTools.Property(_eventType, "IsCompleted");   // GeoscapeEvent.IsCompleted (:36)

            _ready = _completeEvent != null && _eventIdField != null && _eventDataProp != null
                     && _choicesField != null;

            LogLookupAuditOnce();
        }

        private static bool _lookupAuditLogged;

        // One-shot startup validation: name every reflection lookup the native-drive / core paths depend on
        // that resolved NULL, so a silently unbound member (the guard=missing-* family) is visible from the
        // log alone — no in-game repro click needed. Logged once per process at the first Ensure().
        private static void LogLookupAuditOnce()
        {
            if (_lookupAuditLogged) return;
            _lookupAuditLogged = true;
            var missing = new System.Collections.Generic.List<string>();
            if (_completeEvent == null) missing.Add("GeoscapeEvent.CompleteEvent");
            if (_eventIdField == null) missing.Add("GeoscapeEvent.EventID");
            if (_eventDataProp == null) missing.Add("GeoscapeEvent.EventData");
            if (_isCompletedProp == null) missing.Add("GeoscapeEvent.IsCompleted");
            if (_choicesField == null) missing.Add("GeoscapeEventData.Choices");
            if (_glViewField == null) missing.Add("GeoLevelController.View");
            if (_gvModulesField == null) missing.Add("GeoscapeView.GeoscapeModules");
            if (_gmSiteEncModuleField == null) missing.Add("GeoscapeModulesData.SiteEncountersModule");
            if (_encGeoEventField == null) missing.Add("UIModuleSiteEncounters._geoEvent");
            if (_encPagingField == null) missing.Add("UIModuleSiteEncounters._pagingEvent");
            if (_encOnChoiceSelected == null) missing.Add("UIModuleSiteEncounters.OnChoiceSelected");
            if (_encOkTextKeyField == null) missing.Add("UIModuleSiteEncounters.OKTextKey");
            Debug.Log("[Multiplayer] EventReflection lookup audit: " + (missing.Count == 0
                ? "all resolved"
                : "MISSING " + string.Join(",", missing.ToArray())));
        }

        // Shared reflection-member gate for the two host native-drive entry points. Returns the FIRST failing
        // member's distinct greppable tag (State.NativeDriveGuard), or null when the drive may proceed.
        private static string NativeDriveMissingMemberTag()
            => State.NativeDriveGuard.MissingMemberTag(
                ready: _ready,
                hasViewField: _glViewField != null,
                hasModulesField: _gvModulesField != null,
                hasSiteEncModuleField: _gmSiteEncModuleField != null,
                hasGeoEventField: _encGeoEventField != null,
                hasOnChoiceSelected: _encOnChoiceSelected != null);

        // ─── interceptor-side getters (off the live GeoscapeEvent) ────────

        /// <summary>Read <c>GeoscapeEvent.EventID</c>.</summary>
        public static string GetEventId(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return null;
            try { Ensure(); return _eventIdField?.GetValue(geoscapeEvent) as string; }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.GetEventId failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Pure predicate: is <paramref name="eventId"/> the EventID of the client's OWN synthetic result/info
        /// page? The synthetic page is the SOLE dialog built with an EMPTY EventID (<see cref="BuildResultEvent"/>
        /// sets it to "" so it is never re-broadcast / re-keyed). Every REAL host event has a non-empty EventID.
        /// Used by the client OK-click prefix to decide local-close (synthetic) vs swallow-and-wait (real host).
        /// Unit-testable (no game types).
        /// </summary>
        public static bool IsSyntheticResultPage(string eventId) => string.IsNullOrEmpty(eventId);

        /// <summary>
        /// Pure decision: does <see cref="BuildEvent"/> use the SITELESS faction-only context (vs the site
        /// context)? True when the site did not resolve on this client (a siteless event, siteId &lt; 0, OR a
        /// site absent from this sim-frozen client's map) — so the client renders title-only like the host
        /// rather than falling back to StartingBase. False only when a real site resolved. Unit-testable.
        /// </summary>
        public static bool UsesSitelessContext(bool resolvedSite, int siteId) => !resolvedSite;

        /// <summary>
        /// Pure decision: should the client spawn an INERT mirror <c>GeoSite</c> (Case B) before building the
        /// event? True iff the host carried a site IDENTITY (<paramref name="hasIdentity"/>) AND the real site
        /// did NOT resolve on this sim-frozen client (<paramref name="siteResolved"/> == false) — i.e. an
        /// in-play site the client never created. Spawning it makes <see cref="GeoSiteReflection.ResolveSiteById"/>
        /// find a real site so the native render takes the site branch (correct backdrop + subtitle) instead of
        /// the StartingBase ("Точка Феникс") default. False when there is no identity to spawn from, or the site
        /// already exists (idempotent). This is the SINGLE tested decision both the client raise handler and the
        /// GeoSite channel use. Unit-testable (no game types).
        /// </summary>
        public static bool ShouldSpawnMirror(bool hasIdentity, bool siteResolved) => hasIdentity && !siteResolved;

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
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.GetChoiceCount failed: " + ex.Message); return -1; }
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
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.AttachCompletedRecord failed: " + ex.Message); }
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
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.GetChoiceIndex failed: " + ex.Message); return ChoiceLookupFailed; }
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
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.CompleteEvent failed: " + ex.Message); }
        }

        /// <summary>
        /// HOST: when a REMOTE client answered an event the host is currently SHOWING, drive the host's OWN open
        /// <c>UIModuleSiteEncounters</c> through the EXACT native click path (<c>OnChoiceSelected(choice)</c>) so
        /// the host renders the native result/reward page + auto-broadcasts the dismiss — identical to a host click.
        /// Returns TRUE if it natively drove the resolution; FALSE if the host isn't showing that event (caller
        /// then falls back to the model-only <see cref="CompleteEventByOccurrence"/>). Last-write-wins safe: a
        /// repeat on an already-<c>IsCompleted</c> event is a no-op (returns true; native CompleteEvent self-guards).
        /// Verified against the decompile 2026-06-18: OnChoiceSelected:548 (private), _geoEvent:153, _pagingEvent,
        /// module reachable via GeoLevelController.View→GeoscapeView.GeoscapeModules→SiteEncountersModule.
        /// </summary>
        public static bool TryHostNativeResolve(GeoRuntime rt, ushort occurrenceId, string eventId, int choiceIndex)
        {
            try
            {
                Ensure();
                // Distinct tag per failed reflection lookup (NativeDriveGuard) — the old combined
                // not-ready/missing-member tag hid the 2026-07-03 wrong-namespace root cause.
                string missingMember = NativeDriveMissingMemberTag();
                if (missingMember != null)
                {
                    Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " → FALLBACK guard=" + missingMember);
                    return false;
                }
                var geo = rt?.GeoLevel();
                if (geo == null)
                {
                    Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " → FALLBACK guard=geoLevel-null");
                    return false;
                }
                var view = _glViewField.GetValue(geo);
                var modules = view != null ? _gvModulesField.GetValue(view) : null;
                var module = modules != null ? _gmSiteEncModuleField.GetValue(modules) : null;
                if (module == null)
                {
                    Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " → FALLBACK guard=module-null"
                              + " view=" + (view != null) + " modules=" + (modules != null));
                    return false;
                }

                var liveEvent = _encGeoEventField.GetValue(module);
                if (liveEvent == null)
                {
                    Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " → FALLBACK guard=liveEvent-null (host modal not showing an event)");
                    return false;   // host modal not showing an event → fall back
                }

                // Must be THIS occurrence: prefer exact instance identity via the occId reverse-lookup, else match
                // the def-name. A mismatch means the host is on a different/closed event → fall back (model-only).
                bool isThisOccurrence;
                if (Multiplayer.Harmony.Sync.EventOccurrenceIds.TryGetEvent(occurrenceId, out var byId) && byId != null)
                    isThisOccurrence = ReferenceEquals(byId, liveEvent);
                else
                    isThisOccurrence = !string.IsNullOrEmpty(eventId) && GetEventId(liveEvent) == eventId;
                if (!isThisOccurrence)
                {
                    Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " eventId=" + eventId
                              + " → FALLBACK guard=not-this-occurrence (host on a different/closed event) liveEventId=" + GetEventId(liveEvent)
                              + " byIdMapped=" + (byId != null));
                    return false;
                }

                // Already resolved (a prior click won the last-write race) → native no-op; treat as handled.
                if (_isCompletedProp != null && _isCompletedProp.GetValue(liveEvent, null) is bool done && done)
                {
                    Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " → already IsCompleted (no-op)");
                    return true;
                }
                // Still paging multi-page description text → OnChoiceSelected would only advance a page, not select.
                // Fall back to model-only so state converges; the host stays on its (paging) modal.
                if (_encPagingField != null && _encPagingField.GetValue(module) is bool paging && paging)
                {
                    Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " → FALLBACK guard=paging (host still paging description text; OnChoiceSelected would only page)");
                    return false;
                }

                // Pick the GeoEventChoice at the client's index off the LIVE event's own Choices.
                var data = _eventDataProp?.GetValue(liveEvent, null);
                var choices = _choicesField?.GetValue(data) as IList;
                if (choices == null || choiceIndex < 0 || choiceIndex >= choices.Count)
                {
                    Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " → FALLBACK guard=choiceIndex-out-of-range"
                              + " choiceIndex=" + choiceIndex + " choiceCount=" + (choices == null ? -1 : choices.Count));
                    return false;   // invalid index → fall back
                }
                object choice = choices[choiceIndex];
                if (choice == null)
                {
                    Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " → FALLBACK guard=choice-null choiceIndex=" + choiceIndex);
                    return false;
                }

                // Drive the EXACT native click handler (handles SelectChoice→CompleteEvent→SetClosingEncounter
                // result+rewards→broadcast). Same entry a real host button click hits.
                _encOnChoiceSelected.Invoke(module, new[] { choice });
                Debug.Log("[Multiplayer] TryHostNativeResolve occId=" + occurrenceId + " eventId=" + eventId
                          + " choiceIndex=" + choiceIndex + " → drove native OnChoiceSelected (host result page + broadcast)");
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.TryHostNativeResolve failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// HOST: a CLIENT OK'd its single-choice PROMPT mirror (EventAdvanceRequest 0x6B) — advance the host's
        /// OWN open prompt exactly as a local host click would. The event auto-completed at trigger
        /// (GeoscapeEventSystem.cs:651-655), so <see cref="TryHostNativeResolve"/> is useless here (it
        /// early-returns on IsCompleted without touching the UI) and NOTHING may be re-completed (native
        /// CompleteEvent throws on IsCompleted). Drives the native <c>OnChoiceSelected(Choices[0])</c>:
        /// SelectChoice sees IsCompleted → no CompleteEvent → SetClosingEncounter renders the result page →
        /// <c>SingleChoiceAdvancePatch</c> broadcasts EventAdvanceResult to everyone.
        ///
        /// Idempotent first-wins via <see cref="SingleChoiceAdvanceGate.ShouldDriveHostAdvance"/> +
        /// <c>EventOccurrenceIds.MarkAdvanced</c>: the module keeps the SAME <c>_geoEvent</c> on its result page
        /// (SetClosingEncounter never reassigns it, decompile UIModuleSiteEncounters.cs:324-359), so the
        /// advanced-occurrence mark — set by SingleChoiceAdvancePatch on the host's own click AND here after a
        /// driven advance — is what makes a raced host click / duplicate transport delivery a no-op.
        /// Modal not showing / different occId / not completed / multi-choice / paging → logged no-op (the
        /// client's own localClose already happened; EventAdvanceResult reaches it whenever the host advances).
        /// Returns true iff the native advance was driven.
        /// </summary>
        public static bool TryHostNativeAdvanceSingleChoice(GeoRuntime rt, ushort occurrenceId, string eventId)
        {
            try
            {
                Ensure();
                // Distinct tag per failed reflection lookup (NativeDriveGuard) — the old combined
                // not-ready/missing-member tag hid the 2026-07-03 wrong-namespace root cause
                // (missing-GeoscapeModulesData.SiteEncountersModule) behind a blanket false-negative.
                string missingMember = NativeDriveMissingMemberTag();
                if (missingMember != null)
                {
                    Debug.Log("[Multiplayer] TryHostNativeAdvance occId=" + occurrenceId + " → NO-OP guard=" + missingMember);
                    return false;
                }
                var geo = rt?.GeoLevel();
                var view = geo != null ? _glViewField.GetValue(geo) : null;
                var modules = view != null ? _gvModulesField.GetValue(view) : null;
                var module = modules != null ? _gmSiteEncModuleField.GetValue(modules) : null;
                if (module == null)
                {
                    Debug.Log("[Multiplayer] TryHostNativeAdvance occId=" + occurrenceId + " → NO-OP guard=module-null (host modal not showing)");
                    return false;
                }

                // Must be THIS occurrence's live event on the module (same match as TryHostNativeResolve).
                var liveEvent = _encGeoEventField.GetValue(module);
                bool isThisOccurrence;
                if (liveEvent == null)
                    isThisOccurrence = false;
                else if (Multiplayer.Harmony.Sync.EventOccurrenceIds.TryGetEvent(occurrenceId, out var byId) && byId != null)
                    isThisOccurrence = ReferenceEquals(byId, liveEvent);
                else
                    isThisOccurrence = !string.IsNullOrEmpty(eventId) && GetEventId(liveEvent) == eventId;

                bool isCompleted = liveEvent != null && _isCompletedProp != null
                                   && _isCompletedProp.GetValue(liveEvent, null) is bool done && done;
                int choiceCount = liveEvent != null ? GetChoiceCount(liveEvent) : -1;
                bool paging = _encPagingField != null && _encPagingField.GetValue(module) is bool p && p;
                bool alreadyAdvanced = Multiplayer.Harmony.Sync.EventOccurrenceIds.WasAdvanced(occurrenceId);

                if (!State.SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
                        isHost: true, modalShowingThisOccurrence: isThisOccurrence, isCompleted: isCompleted,
                        choiceCount: choiceCount, paging: paging, alreadyAdvanced: alreadyAdvanced))
                {
                    Debug.Log("[Multiplayer] TryHostNativeAdvance occId=" + occurrenceId + " eventId=" + eventId
                              + " → NO-OP (showingThisOcc=" + isThisOccurrence + " isCompleted=" + isCompleted
                              + " choiceCount=" + choiceCount + " paging=" + paging
                              + " alreadyAdvanced=" + alreadyAdvanced + ") liveEventId=" + GetEventId(liveEvent));
                    return false;
                }

                // The lone REAL choice off the live event's own EventData.Choices (never a reconstruction).
                var data = _eventDataProp?.GetValue(liveEvent, null);
                var choices = _choicesField?.GetValue(data) as IList;
                object choice = choices != null && choices.Count == 1 ? choices[0] : null;
                if (choice == null)
                {
                    Debug.Log("[Multiplayer] TryHostNativeAdvance occId=" + occurrenceId + " → NO-OP guard=choice-null");
                    return false;
                }

                // EXACT native click path: OnChoiceSelected → SelectChoice (IsCompleted → no CompleteEvent) →
                // SetClosingEncounter (result page) → SingleChoiceAdvancePatch.Postfix (mark + broadcast).
                _encOnChoiceSelected.Invoke(module, new[] { choice });
                Multiplayer.Harmony.Sync.EventOccurrenceIds.MarkAdvanced(occurrenceId);   // belt: patch postfix marks too
                Debug.Log("[Multiplayer] TryHostNativeAdvance occId=" + occurrenceId + " eventId=" + eventId
                          + " → drove native OnChoiceSelected (client-requested prompt→result advance)");
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.TryHostNativeAdvanceSingleChoice failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// HOST: resolve a choice CLAIM against the LIVE GeoscapeEvent keyed to <paramref name="occurrenceId"/>
        /// (via EventOccurrenceIds) and run the authoritative native CompleteEvent. Mutates the real event so
        /// CompleteEventDismissPatch.Postfix broadcasts the real reward/occId. <paramref name="choiceIndex"/>
        /// &lt; 0 selects the null/decline choice. No-op (logged) if the occurrence is unknown/collected — the
        /// host falls back to the FinishEncounter close path; the client is never left stuck.
        /// </summary>
        public static void CompleteEventByOccurrence(GeoRuntime rt, ushort occurrenceId, int choiceIndex)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var fac = rt?.PhoenixFaction();
                if (fac == null) return;
                if (!Multiplayer.Harmony.Sync.EventOccurrenceIds.TryGetEvent(occurrenceId, out var geoEvent) || geoEvent == null)
                {
                    Debug.Log("[Multiplayer] CompleteEventByOccurrence occId=" + occurrenceId + " → live event not found (claim dropped)");
                    return;
                }
                // Resolve the choice by index off the live event's own EventData.Choices (null when index < 0).
                object choice = null;
                if (choiceIndex >= 0)
                {
                    var data = _eventDataProp?.GetValue(geoEvent, null);
                    var choices = _choicesField?.GetValue(data) as IList;
                    if (choices != null && choiceIndex < choices.Count) choice = choices[choiceIndex];
                }
                _completeEvent.Invoke(geoEvent, new[] { choice, fac });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.CompleteEventByOccurrence failed: " + ex.Message); }
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
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.ResolveEventData failed: " + ex.Message); return null; }
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
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.GetSiteId failed: " + ex.Message); return -1; }
        }

        /// <summary>Host: the live <c>GeoSite</c> off <c>GeoscapeEvent.Context.Site</c>, or null. Used to snapshot
        /// the raise-time site identity for the absent-site client fallback.</summary>
        public static object GetSite(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return null;
            try
            {
                Ensure();
                var ctxField = AccessTools.Field(geoscapeEvent.GetType(), "Context");
                var ctx = ctxField?.GetValue(geoscapeEvent);
                if (ctx == null) return null;
                var siteField = AccessTools.Field(ctx.GetType(), "Site");
                return siteField?.GetValue(ctx);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.GetSite failed: " + ex.Message); return null; }
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
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.GetVehicleId failed: " + ex.Message); return -1; }
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
        public static object BuildEvent(GeoRuntime rt, string eventId, int siteId, int vehicleId = -1, GeoSiteState? identity = null, string wireTitle = null, string wireNarrative = null)
        {
            try
            {
                Ensure();
                if (!_ready || string.IsNullOrEmpty(eventId)) return null;

                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;

                object eventData = ResolveEventData(rt, eventId);
                if (eventData == null) return null;

                // HOST-authoritative wire texts (non-empty → preferred over local-def resolution): stamp the
                // host-resolved title / raise narrative onto the def data as literal binds — the SAME mutation
                // TFTV performs host-side for its runtime-narrative events (VoidOmen: Title/Description[0].General
                // = LocalizedTextBind(text, true), TFTVODIandVoidOmenRoll.cs:638-639), whose keys are "" on the
                // client so local resolution renders a BLANK window. Empty/absent wire text → def untouched
                // (existing local path, SDI_07 etc. byte-identical).
                ApplyWireTexts(eventData, wireTitle, wireNarrative);

                object resolvedSite = ResolveSiteById(rt, siteId);
                bool siteless = UsesSitelessContext(resolvedSite != null, siteId);
                if (_eventCtor == null) return null;

                // Vehicle fallback chain (only meaningful with a real site): broadcast id → a vehicle at the
                // resolved site → null. A siteless context never carries a vehicle.
                object vehicle = siteless ? null : (ResolveVehicleById(rt, vehicleId) ?? ResolveVehicleAtSite(resolvedSite));

                Debug.Log("[Multiplayer] BuildEvent eventId=" + eventId + " siteId=" + siteId +
                          " siteResolved=" + (resolvedSite != null) + " siteless=" + siteless +
                          " hasIdentity=" + identity.HasValue +
                          " vehicleId=" + vehicleId + " vehicleResolved=" + (vehicle != null));

                object context;
                if (siteless)
                {
                    // Genuinely siteless: native renders title-only (no StartingBase subtitle, no NRE). When an
                    // identity block was pushed, best-effort stamp the subtitle-relevant fields onto the context
                    // so an absent-site event still shows the right owner/name text (degrades to title-only).
                    if (_contextCtorSiteless == null) return null;
                    context = _contextCtorSiteless.Invoke(new[] { fac, fac });
                    if (identity.HasValue) ApplySitelessIdentity(context, identity.Value);
                }
                else if (vehicle != null && _contextCtor3 != null)
                {
                    context = _contextCtor3.Invoke(new[] { resolvedSite, fac, vehicle });
                }
                else if (_contextCtor != null)
                {
                    context = _contextCtor.Invoke(new[] { resolvedSite, fac });
                }
                else
                {
                    return null;
                }

                object geoEvent = _eventCtor.Invoke(new[] { eventData, context });
                // Client-only display reconstruction: stamp a Completed record so a local dismiss does NOT
                // hit UIStateGeoscapeEvent.ExitState's null-Record NRE / force-complete (INFO local-hide fix).
                AttachCompletedRecord(geoEvent);
                return geoEvent;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.BuildEvent failed: " + ex.Message); return null; }
        }

        // Diagnostics-only, fully guarded: a short "owner=… type=…" tag for a resolved GeoSite. Reads
        // GeoSite.Owner (GeoFaction) + GeoSite.Type (GeoSiteType enum) via reflection wrapped so a value
        // read that NREs/throws degrades to a placeholder — never affects BuildEvent's control flow.
        private static string DescribeSite(object site)
        {
            if (site == null) return "site=null";
            try
            {
                var ownerProp = AccessTools.Property(site.GetType(), "Owner"); // public GeoFaction Owner (GeoSite.cs:139)
                var typeProp = AccessTools.Property(site.GetType(), "Type");   // public GeoSiteType Type (GeoSite.cs:151)
                object owner = null, type = null;
                try { owner = ownerProp?.GetValue(site, null); } catch { /* deref may NRE on a partial site */ }
                try { type = typeProp?.GetValue(site, null); } catch { }
                return "siteOwner=" + (owner == null ? "null" : owner.ToString()) +
                       " siteType=" + (type == null ? "null" : type.ToString());
            }
            catch { return "siteDescribe=err"; }
        }

        // Best-effort: stamp the absent-site IDENTITY (owner faction + site-name loc-key) onto a SITELESS
        // context so an absent-site event's subtitle/token text degrades gracefully instead of being empty.
        // Never throws into the render; a failed stamp just leaves the title-only siteless context.
        private static void ApplySitelessIdentity(object context, GeoSiteState identity)
        {
            try
            {
                if (context == null) return;
                // VariableChanged is the cheapest faithful carrier of the site-name loc-key for token text;
                // we do NOT fabricate a GeoSite (it is a MonoBehaviour and cannot be synthesized). The native
                // subtitle path keys off Context.Site (null here → title-only), which is the correct host-
                // matching render for an absent site; the identity is logged for diagnosis.
                Debug.Log("[Multiplayer] BuildEvent siteless-identity owner=" + identity.OwnerFactionDefGuid +
                          " type=" + identity.SiteType + " state=" + identity.State +
                          " name=" + identity.SiteName + " enc=" + identity.EncounterID);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.ApplySitelessIdentity failed: " + ex.Message); }
        }

        // ─── HOST-authoritative wire texts (blank-window fix for runtime-narrative defs) ──────────

        // Client: stamp NON-EMPTY host-resolved texts onto the def data as literal binds (doNotLocalize:true).
        //   • Title → data.Title = LocalizedTextBind(wireTitle, true) (native reads Title?.Localize(),
        //     UIModuleSiteEncounters.ShowEncounter :199 — a literal bind returns the string as-is).
        //   • Narrative → Description.LAST variation's .General only (the exact entry the host resolved via
        //     Description.Last().GetText). The variation OBJECT is kept (its Voiceover stays wired); earlier
        //     pages of a multi-page def are untouched so paging still resolves locally. Tokens are NOT baked:
        //     the wire text is the GetText output, and the native render still runs ReplaceEventTokens on it.
        // Best-effort: any failure leaves the local def as-is (never throws into BuildEvent).
        private static void ApplyWireTexts(object eventData, string wireTitle, string wireNarrative)
        {
            try
            {
                if (eventData == null || _localizedTextCtor2 == null) return;
                if (UseWireText(wireTitle) && _edTitleField != null)
                    _edTitleField.SetValue(eventData, _localizedTextCtor2.Invoke(new object[] { wireTitle, true }));
                if (UseWireText(wireNarrative) && _edDescriptionField != null && _tvGeneralField != null)
                {
                    var descList = _edDescriptionField.GetValue(eventData) as IList;
                    if (descList != null && descList.Count > 0)
                    {
                        var lastVariation = descList[descList.Count - 1];
                        if (lastVariation != null)
                            _tvGeneralField.SetValue(lastVariation, _localizedTextCtor2.Invoke(new object[] { wireNarrative, true }));
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.ApplyWireTexts failed: " + ex.Message); }
        }

        /// <summary>
        /// Host: the raise title exactly as the native header renders it — <c>EventData.Title?.Localize() ?? ""</c>
        /// (UIModuleSiteEncounters.ShowEncounter :199). For a TFTV runtime-narrative def (VoidOmen) Title is a
        /// literal bind set by the host-side def mutation, so Localize() returns the runtime text. Returns "" on
        /// any failure (client keeps its local fallback).
        /// </summary>
        public static string ResolveLiveTitle(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return "";
            try
            {
                Ensure();
                var data = _eventDataProp?.GetValue(geoscapeEvent, null);
                var title = data != null ? _edTitleField?.GetValue(data) : null;
                if (title == null || _localizeMethod == null) return "";
                return _localizeMethod.Invoke(title, new object[] { null }) as string ?? "";
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.ResolveLiveTitle failed: " + ex.Message); return ""; }
        }

        /// <summary>
        /// Host: the raise narrative exactly as the native one-window/result fallback resolves it —
        /// <c>EventData.Description.Last().GetText(Context)</c> (UIModuleSiteEncounters.SetClosingEncounter :335) —
        /// with a never-blank fallback to the resolved Title when the Description resolves EMPTY (a TFTV runtime-
        /// narrative def whose flavor mutation is absent; see <see cref="ChooseWireNarrative"/>). Tokens are NOT
        /// replaced here (the client render runs ReplaceEventTokens itself). Returns "" on any failure (client keeps
        /// its local fallback).
        /// </summary>
        public static string ResolveLiveNarrative(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return "";
            try
            {
                Ensure();
                var data = _eventDataProp?.GetValue(geoscapeEvent, null);
                if (data == null) return "";
                var context = _ctxField?.GetValue(geoscapeEvent);
                string narrative = ResolveDescriptionText(data, context);
                // VoidOmen (and any runtime-narrative def) can broadcast with an EMPTY Description while the Title
                // still resolves → mirrored window would be blank. Fall back to the event's own Title text.
                return ChooseWireNarrative(narrative, ResolveLiveTitle(geoscapeEvent));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.ResolveLiveNarrative failed: " + ex.Message); return ""; }
        }

        /// <summary>
        /// Host: the picked choice's outcome text exactly as the native result page resolves it —
        /// <c>SelectedChoice.Outcome.OutcomeText.GetText(Context)</c> (UIModuleSiteEncounters.SetClosingEncounter
        /// :332). Empty for a one-window single-choice event (that is what triggers the native narrative
        /// fallback). Returns "" on any failure (client keeps its local fallback).
        /// </summary>
        public static string ResolveLiveOutcomeText(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return "";
            try
            {
                Ensure();
                var selProp = AccessTools.Property(geoscapeEvent.GetType(), "SelectedChoice"); // public get (GeoscapeEvent.cs:34)
                var choice = selProp?.GetValue(geoscapeEvent, null);
                if (choice == null) return "";
                var context = _ctxField?.GetValue(geoscapeEvent);
                return ResolveOutcomeText(choice, context);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.ResolveLiveOutcomeText failed: " + ex.Message); return ""; }
        }

        // ─── RESULT/OUTCOME follow-up page (host index → client text-only render) ──────────

        /// <summary>
        /// Host: index of <c>GeoscapeEvent.SelectedChoice</c> (GeoscapeEvent.cs:34, set inside CompleteEvent:96)
        /// within <c>EventData.Choices</c>. Returns &gt;= 0 for a real picked choice, or -1 for a null/decline
        /// choice or any introspection failure (callers treat -1 as close-only). Used by the host's dismiss
        /// broadcast to tell clients which choice's outcome page to rebuild.
        /// </summary>
        public static int GetSelectedChoiceIndex(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return -1;
            try
            {
                Ensure();
                var selProp = AccessTools.Property(geoscapeEvent.GetType(), "SelectedChoice"); // public get
                var choice = selProp?.GetValue(geoscapeEvent, null);
                if (choice == null) return -1;
                int idx = GetChoiceIndex(geoscapeEvent, choice);
                return idx >= 0 ? idx : -1;   // decline / lookup-failed → close-only
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.GetSelectedChoiceIndex failed: " + ex.Message); return -1; }
        }

        /// <summary>
        /// Host: the resolved <c>GeoscapeEvent.ChoiceReward</c> (a <c>GeoFactionReward</c>, public getter /
        /// private setter, set inside CompleteEvent:101). Non-null only AFTER an answer was applied (the dismiss
        /// postfix runs at that point). Used to snapshot the reward DISPLAY lines for the client result card.
        /// Returns null on a decline / pre-apply / introspection failure (caller → reward-less card).
        /// </summary>
        public static object GetChoiceReward(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return null;
            try
            {
                var prop = AccessTools.Property(geoscapeEvent.GetType(), "ChoiceReward"); // public get
                return prop?.GetValue(geoscapeEvent, null);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.GetChoiceReward failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// True iff <paramref name="choice"/> produces a follow-up RESULT/OUTCOME page, i.e. its
        /// <c>Outcome.OutcomeText.General.LocalizationKey</c> is non-empty — the same text the native
        /// <c>UIModuleSiteEncounters.SetClosingEncounter</c> renders (and the inverse of the empty-key test in
        /// <c>IsSingleChoiceEncounter</c>, :262). Used by the client lock so a single-choice click that WILL
        /// open an outcome page stays open (host-authoritative), while a pure-info choice dismisses locally.
        /// Returns false on null / unreadable (safe: a missing outcome page → local dismiss).
        /// </summary>
        public static bool ChoiceHasOutcomeText(object choice)
        {
            if (choice == null) return false;
            try
            {
                Ensure();
                var outcome = _choiceOutcomeField?.GetValue(choice);
                if (outcome == null) return false;
                var outcomeText = _outcomeTextField?.GetValue(outcome);
                if (outcomeText == null) return false;
                var general = _tvGeneralField?.GetValue(outcomeText);
                if (general == null) return false;
                var key = _localizedKeyField?.GetValue(general) as string;
                return !string.IsNullOrEmpty(key);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.ChoiceHasOutcomeText failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// PURE: classify an event as a MISSION-DEPLOY prompt from its per-choice "this choice starts a mission"
        /// flags. True iff ANY choice starts a tactical mission — that choice is a Deploy/Land button, so the whole
        /// window is a transient host-side PRE-DECISION prompt the client must NOT mirror (the mission itself rides
        /// the tactical deploy channel; the host's arrive→cancel/deploy decision is host-local). No choices /
        /// all-false (a plain narrative, scavenge, or diplomacy event) → NOT a deploy prompt → mirrored as normal.
        /// Null flags → false (fail OPEN to broadcast; never suppress a legit event on a read failure). Unit-tested.
        /// </summary>
        public static bool IsMissionDeployByOutcomes(bool[] choiceStartsMission)
        {
            if (choiceStartsMission == null) return false;
            for (int i = 0; i < choiceStartsMission.Length; i++)
                if (choiceStartsMission[i]) return true;
            return false;
        }

        /// <summary>
        /// True iff <paramref name="choice"/>'s outcome STARTS a tactical mission, i.e.
        /// <c>Outcome.StartMission.MissionTypeDef != null</c> — the exact condition the native reward builder uses
        /// to emit a <c>RewardStartCustomMission</c> (GeoEventChoiceOutcome.cs:315). Returns false on null /
        /// unreadable / unbound field (safe: a missed positive only means the event mirrors as before).
        /// </summary>
        public static bool ChoiceStartsMission(object choice)
        {
            if (choice == null) return false;
            try
            {
                Ensure();
                var outcome = _choiceOutcomeField?.GetValue(choice);
                if (outcome == null) return false;
                var startMission = _outcomeStartMissionField?.GetValue(outcome);
                if (startMission == null) return false;
                return _startMissionTypeDefField?.GetValue(startMission) != null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.ChoiceStartsMission failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// Host SOURCE-side exclusion predicate: true iff this live event is a mission-arrival/deploy prompt (ANY
        /// choice's outcome starts a mission). Such windows are the vehicle-arrival "Deploy / Leave" confirmation
        /// (e.g. story site PROG_AN2_MISS "Второе посвящение"): a host-local pre-decision prompt whose host-side
        /// cancel/deploy must produce NO client dialog. When true, <c>EventRaisedDisplayPatch</c> /
        /// <c>CompleteEventDismissPatch</c> SKIP the raise/dismiss broadcast, so the client never opens (nor
        /// synthesizes a phantom result page for) it. Fail OPEN: null / unreadable choices → false (broadcast as
        /// before — no regression to legit scavenge / narrative / diplomacy events). Best-effort (never throws).
        /// </summary>
        public static bool IsMissionDeployEvent(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return false;
            try
            {
                Ensure();
                var data = _eventDataProp?.GetValue(geoscapeEvent, null);
                var choices = _choicesField?.GetValue(data) as IList;
                if (choices == null) return false;   // unreadable → fail OPEN to broadcast
                var flags = new bool[choices.Count];
                for (int i = 0; i < choices.Count; i++)
                    flags[i] = ChoiceStartsMission(choices[i]);
                return IsMissionDeployByOutcomes(flags);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.IsMissionDeployEvent failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// Host: mirror native <c>UIModuleSiteEncounters.IsSingleChoiceEncounter()</c> (UIModuleSiteEncounters.cs:256-262)
        /// off the live raised event — true iff the event has EXACTLY ONE choice whose
        /// <c>Outcome.OutcomeText.General.LocalizationKey</c> is EMPTY. In that case the host shows the
        /// reward+narrative in ONE combined window (<c>SetSingleChoiceEncounter</c> → <c>SetClosingEncounter</c>),
        /// never a separate prompt then result. Stamped on the EventRaised wire (oneWindow bit) so the client
        /// SKIPS the phantom reward-less prompt and resolves straight to the result page (reusing the stashed
        /// reward), matching the host's single window. Returns false on null / non-single / non-empty outcome /
        /// unreadable — those keep the 2-window prompt-mirror+advance lockstep. Best-effort (never throws).
        /// </summary>
        public static bool IsOneWindowSingleChoice(object geoscapeEvent)
        {
            if (geoscapeEvent == null) return false;
            try
            {
                Ensure();
                var data = _eventDataProp?.GetValue(geoscapeEvent, null);
                var choices = _choicesField?.GetValue(data) as IList;
                if (choices == null || choices.Count != 1) return false;   // native requires Choices.Count == 1 exactly
                return !ChoiceHasOutcomeText(choices[0]);                   // empty outcome text → ONE combined window
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.IsOneWindowSingleChoice failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// Client: build a synthetic closing <c>GeoscapeEvent{EventID=""}</c> carrying the chosen choice's
        /// outcome text + a single OK button + the raise Title header — mirroring the TEXT half of
        /// <c>UIModuleSiteEncounters.SetClosingEncounter</c> (:326-358) while preserving the EncounterTitle the
        /// host keeps across the closing page (host-authoritative <paramref name="wireTitle"/> when supplied, else
        /// the source def's own Title). It deliberately does NOT touch
        /// <c>ChoiceReward</c> / <c>ShowReward</c> / <c>GeoFactionReward.Apply</c>: the reward STATE already
        /// converged via the wallet/research/items/diplomacy channels, and re-applying here would double-mutate.
        /// The synthetic event has EventID=""/no real Outcome (single choice) → <c>IsClientChoiceLocked==false</c>
        /// → the client can locally dismiss the result page with OK. Returns null on any failure (caller no-ops →
        /// the modal closes via the normal dismiss instead).
        /// </summary>
        /// <summary>
        /// PURE: pick the synthetic result page's single dismiss-button label key. Mirrors native
        /// <c>UIModuleSiteEncounters.SetClosingEncounter</c> (:347-350): use the native <c>OKTextKey</c> when its
        /// LocalizationKey is non-empty, else the literal "OK". The chosen choice's own Text is NEVER an input —
        /// labelling the dismiss button with choice.Text reproduced the clicked choice button (the in-game R7
        /// "duplicated choice button, no OK" symptom). Unit-tested (no Unity types).
        /// </summary>
        public static string ChooseResultOkLabelKey(string nativeOkKey)
            => string.IsNullOrEmpty(nativeOkKey) ? "OK" : nativeOkKey;

        /// <summary>
        /// PURE: pick the synthetic result page's BODY text. Mirrors native
        /// <c>UIModuleSiteEncounters.SetClosingEncounter</c> (:332-336): the body is the chosen choice's
        /// outcome text, but the SINGLE-CHOICE combined window (built with <c>useEventTexts:true</c>) falls
        /// back to the RAISE NARRATIVE (<c>Description.Last().GetText(context)</c>) when that outcome text is
        /// empty — so a one-window reward-less event (VoidOmen-style) renders the narrative the host saw
        /// instead of a blank parchment. A MULTI-choice close (useEventTexts:false) NEVER falls back: an
        /// empty-outcome multi-choice click <c>FinishEncounter()</c>s with no page, so <paramref name="narrativeText"/>
        /// is used ONLY when <paramref name="singleChoiceOneWindow"/> is true. Unit-tested (no Unity types).
        /// </summary>
        public static string ChooseResultBodyText(string outcomeText, string narrativeText, bool singleChoiceOneWindow)
        {
            if (string.IsNullOrEmpty(outcomeText) && singleChoiceOneWindow)
                return narrativeText ?? "";
            return outcomeText ?? "";
        }

        /// <summary>
        /// PURE: wire-text-aware body picker. The HOST resolves the result texts natively at broadcast time
        /// (SelectedChoice.Outcome.OutcomeText.GetText + Description.Last().GetText) and ships them on the
        /// EventDismiss wire; a NON-EMPTY wire string is preferred over the client's local-def resolution —
        /// which is EMPTY for runtime-narrative defs (TFTV VoidOmen_{0..19}: keys created "" and the narrative
        /// exists only as a HOST-side def mutation, so client-side resolution CANNOT work). Empty/absent wire
        /// text degrades to the local value, keeping the legacy 3-param picker's behavior byte-identical
        /// (SDI_07 etc.). Unit-tested (no Unity types).
        /// </summary>
        public static string ChooseResultBodyText(string wireOutcomeText, string wireNarrativeText, string outcomeText, string narrativeText, bool singleChoiceOneWindow)
        {
            string outcome = UseWireText(wireOutcomeText) ? wireOutcomeText : outcomeText;
            string narrative = UseWireText(wireNarrativeText) ? wireNarrativeText : narrativeText;
            return ChooseResultBodyText(outcome, narrative, singleChoiceOneWindow);
        }

        /// <summary>
        /// PURE: the wire-text preference gate shared by the raise-window override (<see cref="BuildEvent"/>)
        /// and the result-body picker — only a NON-EMPTY host-resolved string overrides local-def resolution;
        /// null/empty leaves the existing local path untouched. Unit-tested (no Unity types).
        /// </summary>
        public static bool UseWireText(string wireText) => !string.IsNullOrEmpty(wireText);

        /// <summary>
        /// PURE: the host-authoritative RAISE/RESULT narrative shipped on the wire — the resolved Description
        /// body text, or (when it is EMPTY) the resolved Title as a never-blank fallback. A TFTV runtime-narrative
        /// def can reach the broadcast with an EMPTY Description while its Title still resolves: the base
        /// <c>VoidOmen_{0..19}</c> def keeps <c>Description[0].General.LocalizationKey == ""</c> (TFTVDefsInjected
        /// OnlyOnce.Create_VoidOmen_Events → CreateNewEvent description arg ""), and the flavor text is a runtime-only
        /// literal-bind mutation (TFTVODIandVoidOmenRoll.GenerateVoidOmenEvent :639) that does NOT survive a
        /// save/load or a non-GenerateVoidOmenEvent re-raise — so <c>Description.Last().GetText</c> returns "" and the
        /// mirrored client window renders BLANK. Degrade to the event's own Title (the RCA-identified VOID_OMEN_TITLE_n
        /// text — a REAL text source, NOT invented placeholder) so the window is never empty. A normal event always
        /// carries a non-empty Description, so its wire narrative is returned unchanged. Unit-tested (no Unity types).
        /// </summary>
        public static string ChooseWireNarrative(string descriptionText, string titleText)
            => !string.IsNullOrEmpty(descriptionText) ? descriptionText : (titleText ?? "");

        /// <summary>
        /// PURE: pick the client RESULT page's EncounterTitle. The host keeps the raise title across its closing
        /// page (<c>ShowEncounter</c> sets <c>EncounterTitle</c> once at :217; <c>SetClosingEncounter</c> never
        /// clears it), so the mirrored result page must carry it too — omitting it dropped the whole yellow
        /// НОВОЕ:/ЗАВЕРШЕНО: header for TFTV runtime-narrative defs (VoidOmen). Prefer the HOST-resolved
        /// <paramref name="wireTitle"/> (VoidOmen's Title is a runtime host-side def mutation → <paramref name="defTitleText"/>
        /// resolves EMPTY on the client's own def); else the local def's own title (a normal event's real loc key,
        /// or the literal <see cref="ApplyWireTexts"/> stamped in the raise-first path). Unit-tested (no Unity types).
        /// </summary>
        public static string ChooseResultTitle(string wireTitle, string defTitleText)
            => UseWireText(wireTitle) ? wireTitle : (defTitleText ?? "");

        /// <summary>
        /// Read the live module's native OK-button LocalizationKey (GeoLevelController.View → GeoscapeView.
        /// GeoscapeModules → SiteEncountersModule → OKTextKey.LocalizationKey), or null if any link is missing.
        /// Lets the synthetic result page reuse the SAME localized dismiss label the native result page uses.
        /// </summary>
        private static string GetNativeOkLabelKey(GeoRuntime rt)
        {
            try
            {
                var geo = rt?.GeoLevel();
                if (geo == null || _glViewField == null || _gvModulesField == null
                    || _gmSiteEncModuleField == null || _encOkTextKeyField == null || _localizedKeyField == null) return null;
                var view = _glViewField.GetValue(geo);
                if (view == null) return null;
                var modules = _gvModulesField.GetValue(view);
                if (modules == null) return null;
                var module = _gmSiteEncModuleField.GetValue(modules);
                if (module == null) return null;
                var okBind = _encOkTextKeyField.GetValue(module);
                if (okBind == null) return null;
                return _localizedKeyField.GetValue(okBind) as string;
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] EventReflection.GetNativeOkLabelKey best-effort failed: " + ex.Message); return null; }
        }

        public static object BuildResultEvent(GeoRuntime rt, string eventId, int choiceIndex, int siteId = -1, string wireOutcome = null, string wireNarrative = null, string wireTitle = null)
        {
            try
            {
                Ensure();
                if (!_ready || string.IsNullOrEmpty(eventId) || choiceIndex < 0)
                { Debug.Log("[Multiplayer] BuildResultEvent NULL guard=not-ready/empty-id/neg-index eventId=" + eventId + " choiceIndex=" + choiceIndex + " ready=" + _ready); return null; }
                if (_eventDataCtor == null || _eventCtor == null || _contextCtor == null
                    || _edDescriptionField == null || _choicesField == null || _tvGeneralField == null
                    || _localizedTextCtor2 == null || _textVariationType == null || _choiceType2 == null
                    || _choiceTextField == null)
                { Debug.Log("[Multiplayer] BuildResultEvent NULL guard=missing-reflection-member eventId=" + eventId); return null; }

                var fac = rt?.PhoenixFaction();
                if (fac == null)
                { Debug.Log("[Multiplayer] BuildResultEvent NULL guard=faction-null eventId=" + eventId); return null; }

                object srcData = ResolveEventData(rt, eventId);
                if (srcData == null)
                { Debug.Log("[Multiplayer] BuildResultEvent NULL guard=def-not-found eventId=" + eventId); return null; }

                var srcChoices = _choicesField.GetValue(srcData) as IList;
                if (srcChoices == null || choiceIndex >= srcChoices.Count)
                { Debug.Log("[Multiplayer] BuildResultEvent NULL guard=choiceIndex-out-of-range eventId=" + eventId + " choiceIndex=" + choiceIndex + " choiceCount=" + (srcChoices == null ? -1 : srcChoices.Count)); return null; }
                object choice = srcChoices[choiceIndex];
                if (choice == null)
                { Debug.Log("[Multiplayer] BuildResultEvent NULL guard=choice-null eventId=" + eventId + " choiceIndex=" + choiceIndex); return null; }

                // Result page: resolve the real site, else a SITELESS context (NEVER StartingBase). The outcome
                // text usually needs only faction/site tokens; a missing site degrades to title-only text.
                object site = ResolveSiteById(rt, siteId);
                object context;
                if (site != null)
                {
                    context = _contextCtor.Invoke(new[] { site, fac });
                }
                else if (_contextCtorSiteless != null)
                {
                    context = _contextCtorSiteless.Invoke(new[] { fac, fac });
                }
                else
                {
                    Debug.Log("[Multiplayer] BuildResultEvent NULL guard=no-context eventId=" + eventId + " siteId=" + siteId);
                    return null;
                }

                // Body text = choice.Outcome.OutcomeText.GetText(context) (UIModuleSiteEncounters.cs:334), but a
                // SINGLE-CHOICE one-window event (Choices.Count==1 + empty outcome text = native IsSingleChoiceEncounter)
                // falls back to the RAISE NARRATIVE (Description.Last) exactly as native SetClosingEncounter's
                // useEventTexts:true branch (:333-335) — otherwise a reward-less VoidOmen-style event renders a BLANK
                // parchment on the client while the host showed the narrative. A multi-choice empty outcome keeps ""
                // (host FinishEncounters with no page — OnChoiceSelected :584-591). Then tokens replaced.
                string outcomeText = ResolveOutcomeText(choice, context);
                bool singleChoiceOneWindow = srcChoices.Count == 1 && !ChoiceHasOutcomeText(choice);
                string narrativeText = singleChoiceOneWindow ? ResolveDescriptionText(srcData, context) : null;
                // Wire-text preference: NON-EMPTY host-resolved outcome/narrative (shipped on the dismiss/raise
                // wire) beats the local-def resolution — which is EMPTY for runtime-narrative defs (VoidOmen).
                string text = ChooseResultBodyText(wireOutcome, wireNarrative, outcomeText, narrativeText, singleChoiceOneWindow);
                string text2 = ReplaceTokens(context, text);
                // DIAG (blank-result-page visibility): local-def desc entries + resolved lengths on every path.
                var srcDescList = _edDescriptionField.GetValue(srcData) as IList;
                Debug.Log("[Multiplayer] BuildResultEvent texts eventId=" + eventId
                          + " descCount=" + (srcDescList == null ? -1 : srcDescList.Count)
                          + " outLen=" + (outcomeText?.Length ?? 0) + " narrLen=" + (narrativeText?.Length ?? 0)
                          + " wireOutLen=" + (wireOutcome?.Length ?? 0) + " wireNarrLen=" + (wireNarrative?.Length ?? 0)
                          + " bodyLen=" + (text2?.Length ?? 0) + " oneWindow=" + singleChoiceOneWindow);

                // Synthetic closing data: EventID="" (so it is never re-broadcast / re-keyed), one OK button.
                object data = _eventDataCtor.Invoke(null);
                _edEventIdField?.SetValue(data, "");
                if (_edFlavourField != null) _edFlavourField.SetValue(data, GetEventDataString(srcData, _edFlavourField));
                if (_edLeaderField != null) _edLeaderField.SetValue(data, GetEventDataString(srcData, _edLeaderField));

                // Title (the yellow NEW/COMPLETED header): ShowEncounter renders EncounterTitle =
                // EventData.Title?.Localize() (UIModuleSiteEncounters:199/217) and SetClosingEncounter never clears
                // it, so the host's result page keeps the raise title — but this synthetic closing data used to omit
                // Title, so the client result page dropped the whole header block (TFTV VoidOmen: the yellow
                // НОВОЕ:/ЗАВЕРШЕНО: lines). Prefer the HOST wire title (VoidOmen's Title is a runtime host-side def
                // mutation, "" on the client's own def, so local resolution can't recover it); else the source def's
                // own title — a real loc key for a normal event, or the literal ApplyWireTexts already stamped onto
                // the shared client def in the raise-first path. Stamped as a doNotLocalize literal bind exactly like
                // native SetClosingEncounter's resolved-text bind (:342). Best-effort: null field/ctor leaves it blank.
                string titleText = ChooseResultTitle(wireTitle, ResolveTitleText(srcData));
                if (_edTitleField != null && _localizedTextCtor2 != null && !string.IsNullOrEmpty(titleText))
                    _edTitleField.SetValue(data, _localizedTextCtor2.Invoke(new object[] { titleText, true }));

                // Description = [ EventTextVariation{ General = LocalizedTextBind(text2, doNotLocalize:true) } ].
                object general = _localizedTextCtor2.Invoke(new object[] { text2, true });
                object variation = Activator.CreateInstance(_textVariationType);
                _tvGeneralField.SetValue(variation, general);
                var descList = MakeTypedList(_textVariationType);
                descList.Add(variation);
                _edDescriptionField.SetValue(data, descList);

                // Choices = [ GeoEventChoice{ Text = native OKTextKey (localized) | literal "OK" } ]. Mirrors native
                // SetClosingEncounter (:347-350): the dismiss button uses the module's serialized OKTextKey, NEVER the
                // chosen choice's Text (labelling it with choice.Text reproduced the clicked button → the in-game R7
                // "duplicated choice button, no OK" symptom). Prefer the live native key (engine localizes it →
                // matches the native button exactly); if unreadable, fall back to the literal "OK" (doNotLocalize).
                string nativeOkKey = GetNativeOkLabelKey(rt);
                string okKey = ChooseResultOkLabelKey(nativeOkKey);
                bool okIsLiteral = string.IsNullOrEmpty(nativeOkKey);   // fallback "OK" is a literal, not a loc key
                object okText = _localizedTextCtor2.Invoke(new object[] { okKey, okIsLiteral });
                object okChoice = Activator.CreateInstance(_choiceType2);
                _choiceTextField.SetValue(okChoice, okText);
                var choiceList = MakeTypedList(_choiceType2);
                choiceList.Add(okChoice);
                _choicesField.SetValue(data, choiceList);

                object geoEvent = _eventCtor.Invoke(new[] { data, context });
                // Stamp a Completed record so the local OK dismiss does not NRE / force-complete (same as BuildEvent).
                AttachCompletedRecord(geoEvent);
                Debug.Log("[Multiplayer] BuildResultEvent ok eventId=" + eventId + " choiceIndex=" + choiceIndex);
                return geoEvent;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReflection.BuildResultEvent failed: " + ex.Message); return null; }
        }

        private static string ResolveOutcomeText(object choice, object context)
        {
            try
            {
                var outcome = _choiceOutcomeField?.GetValue(choice);
                if (outcome == null) return "";
                var outcomeText = _outcomeTextField?.GetValue(outcome);
                if (outcomeText == null) return "";
                if (_tvGetTextMethod != null)
                    return _tvGetTextMethod.Invoke(outcomeText, new[] { context }) as string ?? "";
                // Fallback: General.Localize() via reflection if GetText is unavailable.
                var general = _tvGeneralField?.GetValue(outcomeText);
                return general?.ToString() ?? "";
            }
            catch { return ""; }
        }

        // Raise NARRATIVE for the single-choice one-window fallback: mirrors native
        // UIModuleSiteEncounters.SetClosingEncounter (:335) `geoEvent.EventData.Description.Last().GetText(context)`.
        // Reads the source def's Description (List<EventTextVariation>), takes its LAST variation and resolves it
        // against the rebuilt context. Returns "" on null/empty/unreadable (caller degrades to a blank body).
        // Result-page title fallback source: the source def's own Title text, mirroring the native header read
        // `EventData.Title?.Localize()` (UIModuleSiteEncounters:199). Resolves a real loc key (normal event) OR
        // the literal doNotLocalize bind ApplyWireTexts stamped on the shared client def in the raise-first path.
        // Returns "" on null/unreadable (caller then relies on the host wire title). Best-effort, never throws.
        private static string ResolveTitleText(object srcData)
        {
            try
            {
                var title = srcData != null ? _edTitleField?.GetValue(srcData) : null;
                if (title == null || _localizeMethod == null) return "";
                return _localizeMethod.Invoke(title, new object[] { null }) as string ?? "";
            }
            catch { return ""; }
        }

        private static string ResolveDescriptionText(object srcData, object context)
        {
            try
            {
                var descList = _edDescriptionField?.GetValue(srcData) as IList;
                if (descList == null || descList.Count == 0) return "";
                var lastVariation = descList[descList.Count - 1];
                if (lastVariation == null || _tvGetTextMethod == null) return "";
                return _tvGetTextMethod.Invoke(lastVariation, new[] { context }) as string ?? "";
            }
            catch { return ""; }
        }

        private static string ReplaceTokens(object context, string text)
        {
            try
            {
                if (_replaceTokensMethod != null && context != null)
                    return _replaceTokensMethod.Invoke(context, new object[] { text }) as string ?? text;
            }
            catch { /* token replace is best-effort */ }
            return text;
        }

        private static string GetEventDataString(object eventData, FieldInfo field)
        {
            try { return field?.GetValue(eventData) as string; } catch { return null; }
        }

        /// <summary>Create a concrete <c>List&lt;elementType&gt;</c> (the game's Description/Choices field types).</summary>
        private static IList MakeTypedList(Type elementType)
        {
            var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(elementType);
            return (IList)Activator.CreateInstance(listType);
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
