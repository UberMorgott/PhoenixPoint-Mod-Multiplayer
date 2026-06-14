using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // PERF (INC-3a host-lag fix): a cheap, alloc-free managed signature of a GeoVehicle's CONTINUOUS
    // pos/rot/range plus the cheap DISCRETE trigger fields (Travelling/CurrentSite/HitPoints). Read WITHOUT
    // the native RecordInstanceData fill or any Activator allocation, so the host's per-tick dirty pre-check
    // can skip the expensive RecordVehicleState for a vehicle that has not moved. It is a struct (no heap
    // alloc), identity = (FactionGuid,VehicleID). DestinationSites is intentionally NOT in the cheap signature
    // — a pure re-route that flips neither Travelling nor CurrentSite is detected on the next discrete change
    // (or once it actually moves); DestinationSites is a cosmetic path preview, so the tiny latency is fine.
    // RangeRemaining IS included (defense-in-depth on the lossless mirror): a future parked/gradual-refuel
    // path could change range without any pos/rot/discrete move, and must never be silently dropped.
    internal struct GeoVehicleCheapSig
    {
        public string FactionGuid;
        public int VehicleID;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float RangeRemaining;
        public bool Travelling;
        public int CurrentSiteId;
        public int HitPoints;
    }
    // Engine-side id<->entity bridge for command apply. Uses AccessTools reflection so the mod never
    // hard-references game types at compile time (matching the CampaignPatches stub strategy).
    //
    // Decompile-confirmed member shapes (E:\DEV\PhoenixPoint\decompiled\...):
    //   * GeoLevelController has NO static Instance. Resolve via GameUtl.CurrentLevel() (Base.Core,
    //     returns Base.Levels.Level, a Component) -> GetComponent<GeoLevelController>().
    //   * GeoLevelController.PhoenixFaction  -> property (GeoLevelController.cs:225).
    //   * GeoLevelController.Map             -> public FIELD (GeoLevelController.cs:97), NOT a property.
    //   * GeoFaction.Vehicles                -> property, IEnumerable<GeoVehicle> (GeoFaction.cs:137).
    //   * GeoMap.AllSites                    -> property, IList<GeoSite> (GeoMap.cs:251).
    //   * GeoVehicle.VehicleID               -> public int FIELD (GeoVehicle.cs:51) = stable vehicle id.
    //   * GeoSite.SiteId                     -> public int FIELD (GeoSite.cs:45)   = stable site id.
    internal static class GeoBridge
    {
        // ─── PERF: cached reflection handles for the host snapshot hot path (INC-3a lag fix) ───
        // RecordVehicleState used to AccessTools.TypeByName/Method/Field/Property on EVERY call (12+ lookups
        // per vehicle per frame across all factions). The shapes are FIXED game types, so resolve them ONCE
        // (lazy, defensively — a missing type leaves the handles null and the callers fall back exactly as
        // before). All Field/Method/Property handles are read-only after init. _reflectReady gates the lazy
        // resolve; resolution failures (type not yet loaded) leave it false so a later tick retries.
        private static bool _reflectReady;
        private static Type _gvidType;          // PhoenixPoint.Geoscape.Entities.GeoVehicleInstanceData (native fill target)
        private static MethodInfo _recordInstanceData; // GeoVehicle.RecordInstanceData(ActorInstanceData) — native fill
        private static PropertyInfo _ownerProp;        // GeoVehicle.Owner
        private static FieldInfo _fInstVehicleID;       // GeoVehicleInstanceData.VehicleID
        private static FieldInfo _fSurfacePos, _fSurfaceRot, _fRangeRemaining, _fTravellingData, _fHitPointsData;
        private static FieldInfo _fCurrentSite, _fDestinationSites;
        // Cheap (managed, no-native-fill) signature handles, all on the live GeoVehicle / its members:
        private static FieldInfo _fVehicleID;          // GeoVehicle.VehicleID (public int field)
        private static PropertyInfo _surfaceProp;      // GeoVehicle.Surface (Transform)
        private static PropertyInfo _travellingProp;   // GeoVehicle.Travelling (bool)
        private static PropertyInfo _currentSiteProp;  // GeoVehicle.CurrentSite (GeoSite)
        private static PropertyInfo _rangeRemainingProp; // GeoVehicle.RangeRemaining (EarthUnits)
        private static FieldInfo _euValueField;        // EarthUnits.Value (public float field, EarthUnits.cs:21)
        private static FieldInfo _statsField;          // GeoVehicle.Stats
        private static FieldInfo _statsHitPoints;      // GeoVehicleStats.HitPoints (int)
        private static FieldInfo _siteIdField;         // GeoSite.SiteId (int)
        // INC-3a globe-icon placement: GeoActor.SetOrientedGlobeWorldPosition(Vector3) — the native primitive
        // that orients the on-globe icon by rotating PivotTransform.localRotation from a WORLD position
        // (GeoActor.cs:66-77; same effect NavigateRoutine produces per-frame at GeoNavComponent.cs:117-119).
        // Inherited from GeoActor by GeoVehicle, so AccessTools.Method on the GeoVehicle type resolves it.
        private static MethodInfo _setOrientedGlobeWorldPos; // void GeoActor.SetOrientedGlobeWorldPosition(Vector3)
        // INC-D P3 (client nose/heading): the native heading math on GeoNavComponent. The visible nose is
        // Surface.localEulerAngles.z, set EVERY FRAME by NavigateRoutine via UpdateHeading(GetHeadingTowardsTarget
        // (worldTarget), instant:false) (GeoNavComponent.cs:201-264) — a coroutine the client never runs. We REUSE
        // these two PRIVATE methods directly (no movement routine, single-writer intact) to drive the nose toward
        // the streamed destination. GeoVehicle.Navigation is a public GeoNavComponent field (GeoVehicle.cs:39),
        // GeoSite.WorldPosition is the world target (GeoActor.WorldPosition => Surface.position, GeoActor.cs:25).
        private static FieldInfo _navigationField;        // GeoVehicle.Navigation (GeoNavComponent)
        private static MethodInfo _getHeadingTowardsTarget; // float GeoNavComponent.GetHeadingTowardsTarget(Vector3) [private]
        private static MethodInfo _updateHeading;           // bool GeoNavComponent.UpdateHeading(float, bool) [private]
        private static FieldInfo _destinationSitesField;   // GeoVehicle._destinationSites (List<GeoSite>)
        private static PropertyInfo _siteWorldPosProp;     // GeoSite.WorldPosition (Vector3, inherited from GeoActor)

        // DIAG-NAV ~1/sec-per-id throttle for the decisive per-tick travel-progress probe.
        private static readonly Dictionary<(string, int), float> _navProbeNextLogTime =
            new Dictionary<(string, int), float>();
        // DIAG-NAV previous sample (WorldPosition + RangeRemaining) per id, to report PER-SECOND DELTAS so the
        // reader sees instantly whether a craft is advancing without diffing log lines by hand.
        private static readonly Dictionary<(string, int), (Vector3 pos, float range)> _navProbeLast =
            new Dictionary<(string, int), (Vector3, float)>();

        // Clear the DIAG-NAV probe state for one identity (vehicle removal) or all (session Reset). Pure DIAG
        // bookkeeping — no behavior depends on it.
        public static void ResetNavProbe((string, int)? identity = null)
        {
            if (identity.HasValue)
            {
                _navProbeNextLogTime.Remove(identity.Value);
                _navProbeLast.Remove(identity.Value);
            }
            else
            {
                _navProbeNextLogTime.Clear();
                _navProbeLast.Clear();
            }
        }

        // DECISIVE PROBE (marker DIAG-NAV): for a TRAVELLING vehicle, sample the PUBLIC native travel-progress
        // signals the game's own UI reads (UIStateVehicleSelected.cs:1384 uses GeoMap.Distance(vehicle.WorldPosition,
        // site.WorldPosition) + vehicle.RangeRemaining). Per id (~1/sec) logs def name (player PP/NA_Manticore vs
        // AI NJ/SYN/ANU), the actual rendered WorldPosition (= Surface.position), RangeRemaining, distance to the
        // FinalDestination, Speed, AND the per-second DELTAS of pos+range. Reading guide:
        //   - posDelta > 0 && rangeDelta < 0  -> craft IS advancing (baseline 'moving', expect for AI).
        //   - posDelta ~0  && rangeDelta < 0  -> routine RUNS (range decremented at GeoNavComponent.cs:124) but the
        //                                        rendered position is frozen/reverted elsewhere (H1: engine-freeze
        //                                        / a mod re-snap), NOT a time/num issue.
        //   - posDelta ~0  && rangeDelta ~0   -> routine NOT progressing: num=0 (Ratio01=0, startTime>=Now) (H2: time).
        //   - both tiny but nonzero            -> glacial: totalTime inflated (H3).
        // Read-only; ~1/sec per id; never throws. Reflection via cached helpers (VehicleDefNameOf/ReadRangeRemaining).
        public static void DiagNavObserve(object vehicle, (string, int) identity)
        {
            try
            {
                var vt = vehicle.GetType();
                var travelling = AccessTools.Property(vt, "Travelling")?.GetValue(vehicle);
                if (!(travelling is bool tb && tb)) return; // only travelling crafts are interesting

                float now = UnityEngine.Time.realtimeSinceStartup;
                _navProbeNextLogTime.TryGetValue(identity, out var next);
                if (now < next) return;
                _navProbeNextLogTime[identity] = now + 1.0f;

                // WorldPosition (= Surface.position): the ACTUAL rendered globe position.
                var surface = AccessTools.Property(vt, "Surface")?.GetValue(vehicle) as Transform;
                Vector3 pos = surface != null ? surface.position : Vector3.zero;
                float range = ReadRangeRemaining(vehicle);
                string defName = VehicleDefNameOf(vehicle);

                // Distance to FinalDestination via the SAME public API the UI uses (GeoMap.Distance(Vector3,Vector3)).
                float distToDest = -1f;
                try
                {
                    var finalDest = AccessTools.Property(vt, "FinalDestination")?.GetValue(vehicle);
                    var destPos = finalDest != null ? AccessTools.Property(finalDest.GetType(), "WorldPosition")?.GetValue(finalDest) : null;
                    if (destPos is Vector3 dp)
                    {
                        var geoMapType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoMap");
                        var distM = geoMapType != null ? AccessTools.Method(geoMapType, "Distance", new[] { typeof(Vector3), typeof(Vector3) }) : null;
                        var d = distM?.Invoke(null, new object[] { pos, dp });
                        // GeoMap.Distance returns EarthUnits -> read .Value (meters) via the cached EarthUnits field.
                        var dv = d != null ? _euValueField?.GetValue(d) : null;
                        if (dv is float dfm) distToDest = dfm;
                    }
                }
                catch { /* leave distToDest=-1 (absent) */ }

                // Speed (EarthUnits.Value meters/h) — totalTime proxy alongside distance.
                float speed = 0f;
                try
                {
                    var sp = AccessTools.Property(vt, "Speed")?.GetValue(vehicle);
                    var spv = sp != null ? _euValueField?.GetValue(sp) : null;
                    if (spv is float spf) speed = spf;
                }
                catch { /* leave 0 */ }

                // Per-second deltas vs the previous sample (first sample => deltas absent).
                Vector3 posDelta = Vector3.zero; float rangeDelta = 0f; bool haveDelta = false;
                if (_navProbeLast.TryGetValue(identity, out var prev))
                {
                    posDelta = pos - prev.pos;
                    rangeDelta = range - prev.range;
                    haveDelta = true;
                }
                _navProbeLast[identity] = (pos, range);

                UnityEngine.Debug.Log($"[Multipleer] DIAG-NAV id {identity.Item1}#{identity.Item2} def={defName} " +
                    $"pos={pos} range={range:F1} distToDest={distToDest:F1} speed={speed:F1} " +
                    (haveDelta
                        ? $"posDelta={posDelta.magnitude:F4} rangeDelta={rangeDelta:F2}"
                        : "posDelta=NA rangeDelta=NA"));
            }
            catch { /* never throw from a DIAG read */ }
        }

        // Resolve the fixed game-type handles once. Defensive: any failure leaves _reflectReady false so the
        // next tick retries (the geoscape types may not be loaded the first time this runs).
        private static void EnsureReflect()
        {
            if (_reflectReady) return;
            try
            {
                var gvidType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicleInstanceData");
                var actorDataType = AccessTools.TypeByName("Base.Entities.ActorInstanceData");
                var geoVehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
                var statsType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicleStats");
                var siteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
                if (gvidType == null || actorDataType == null || geoVehicleType == null) return;

                _gvidType = gvidType;
                _recordInstanceData = AccessTools.Method(geoVehicleType, "RecordInstanceData", new[] { actorDataType });
                _ownerProp = AccessTools.Property(geoVehicleType, "Owner");

                _fInstVehicleID = AccessTools.Field(gvidType, "VehicleID");
                _fSurfacePos = AccessTools.Field(gvidType, "SurfacePos");
                _fSurfaceRot = AccessTools.Field(gvidType, "SurfaceRot");
                _fRangeRemaining = AccessTools.Field(gvidType, "RangeRemaining");
                _fTravellingData = AccessTools.Field(gvidType, "Travelling");
                _fHitPointsData = AccessTools.Field(gvidType, "HitPoints");
                _fCurrentSite = AccessTools.Field(gvidType, "CurrentSite");
                _fDestinationSites = AccessTools.Field(gvidType, "DestinationSites");

                _fVehicleID = AccessTools.Field(geoVehicleType, "VehicleID");
                _surfaceProp = AccessTools.Property(geoVehicleType, "Surface");
                _travellingProp = AccessTools.Property(geoVehicleType, "Travelling");
                _currentSiteProp = AccessTools.Property(geoVehicleType, "CurrentSite");
                _rangeRemainingProp = AccessTools.Property(geoVehicleType, "RangeRemaining");
                // EarthUnits.Value (the property's struct return type holds a public float Value field).
                var euType = _rangeRemainingProp?.PropertyType ?? AccessTools.TypeByName("PhoenixPoint.Common.Core.EarthUnits");
                _euValueField = euType != null ? AccessTools.Field(euType, "Value") : null;
                _statsField = AccessTools.Field(geoVehicleType, "Stats");
                _statsHitPoints = statsType != null ? AccessTools.Field(statsType, "HitPoints") : null;
                _siteIdField = siteType != null ? AccessTools.Field(siteType, "SiteId") : null;
                // GeoActor.SetOrientedGlobeWorldPosition(Vector3) — inherited by GeoVehicle; resolve on the
                // GeoVehicle type so the icon-placement primitive is cached alongside the apply handles.
                _setOrientedGlobeWorldPos = AccessTools.Method(geoVehicleType,
                    "SetOrientedGlobeWorldPosition", new[] { typeof(Vector3) });

                // INC-D P3 native heading reuse: GeoVehicle.Navigation field + the two PRIVATE GeoNavComponent
                // heading methods + GeoVehicle._destinationSites + GeoSite.WorldPosition. AccessTools resolves
                // non-public members; any null handle makes UpdateVehicleHeadingTowards a no-op (nose unchanged).
                var navType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoNavComponent");
                _navigationField = AccessTools.Field(geoVehicleType, "Navigation");
                _getHeadingTowardsTarget = navType != null
                    ? AccessTools.Method(navType, "GetHeadingTowardsTarget", new[] { typeof(Vector3) }) : null;
                _updateHeading = navType != null
                    ? AccessTools.Method(navType, "UpdateHeading", new[] { typeof(float), typeof(bool) }) : null;
                _destinationSitesField = AccessTools.Field(geoVehicleType, "_destinationSites");
                _siteWorldPosProp = siteType != null ? AccessTools.Property(siteType, "WorldPosition") : null;

                _reflectReady = true;
            }
            catch { /* leave _reflectReady false; a later tick retries */ }
        }

        // GameUtl.CurrentLevel().GetComponent<GeoLevelController>() — the active geoscape controller,
        // or null when no geoscape level is loaded (single-menu / tactical / between-loads).
        public static object GetGeoLevelController()
        {
            var geoLevelType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            if (geoLevelType == null) return null;

            var gameUtlType = AccessTools.TypeByName("Base.Core.GameUtl");
            var currentLevel = gameUtlType?.GetMethod("CurrentLevel", AccessTools.all)?.Invoke(null, null);
            if (currentLevel == null) return null;

            // Level derives from a Unity Component, so GetComponent(Type) is available.
            var getComponent = AccessTools.Method(currentLevel.GetType(), "GetComponent", new[] { typeof(Type) });
            return getComponent?.Invoke(currentLevel, new object[] { geoLevelType });
        }

        // LEGACY Phoenix-only resolver: scans ONLY PhoenixFaction.Vehicles. Vehicle id == GeoVehicle.VehicleID
        // (public int FIELD, GeoVehicle.cs:51) rendered as string by the codec. KEPT for legacy Phoenix
        // callers (the 0x34/StartTravel-input paths where the craft is always Phoenix-manufactured). For an
        // all-faction state diff use FindVehicleByFactionAndId instead — a non-Phoenix craft never resolves here.
        public static object FindVehicleById(object geoLevel, string vehicleId)
        {
            var faction = AccessTools.Property(geoLevel.GetType(), "PhoenixFaction")?.GetValue(geoLevel);
            var vehicles = AccessTools.Property(faction?.GetType(), "Vehicles")?.GetValue(faction) as IEnumerable;
            if (vehicles == null) return null;
            foreach (var v in vehicles)
                if (VehicleId(v) == vehicleId) return v;
            return null;
        }

        // ALL-FACTIONS resolver (INC-3a): resolve a GeoVehicle by its real (factionGuid, VehicleID) identity.
        // THE live-bug fix — the Phoenix-only FindVehicleById can never find a host-moved non-Phoenix craft
        // (e.g. a New Jericho Thunderbird), so a faction-keyed state record could not be applied on the client.
        // Resolves the owning faction STRICTLY (FindFactionByGuidStrict: NO Phoenix fallback when a non-empty
        // guid is unmatched — a stale/unknown non-Phoenix guid must NOT silently mirror onto a Phoenix craft),
        // then scans THAT faction's GeoFaction.Vehicles (IEnumerable<GeoVehicle>, GeoFaction.cs:137) for the
        // matching VehicleID. Returns the GeoVehicle or null (faction not found, or no such id in that faction).
        public static object FindVehicleByFactionAndId(object geoLevel, string factionGuid, int vehicleID)
        {
            var faction = FindFactionByGuidStrict(geoLevel, factionGuid);
            if (faction == null) return null;
            var vehicles = AccessTools.Property(faction.GetType(), "Vehicles")?.GetValue(faction) as IEnumerable;
            if (vehicles == null) return null;
            var idStr = vehicleID.ToString();
            foreach (var v in vehicles)
                if (VehicleId(v) == idStr) return v;
            return null;
        }

        // ALL-FACTIONS host snapshot (INC-3a): record one GeoVehicle's authoritative durable state into a
        // pure GeoVehicleStateRecord for the 0x35 GeoStateDiff mirror. Mirrors TimeBridge.RecordHostState's
        // native-RecordInstanceData pattern (TimeBridge.cs:85-103): allocate a FRESH GeoVehicleInstanceData
        // and invoke the native protected GeoVehicle.RecordInstanceData(ActorInstanceData) (GeoVehicle.cs:1053,
        // a void override that FILLS the passed data) so the snapshot is isolated and has zero side-effects on
        // game state — never the shared SerializationData getter (ActorComponent.cs:55-66, which mutates the
        // persisted instance). Then read the durable fields off OUR filled instance:
        //   SurfacePos (Vector3, GeoVehicleInstanceData.cs:17) -> PosX/Y/Z
        //   SurfaceRot (Quaternion, cs:19)                     -> RotX/Y/Z/W
        //   RangeRemaining (float, cs:23)                      -> RangeRemaining (set from EarthUnits.Value at cs:1065)
        //   Travelling (bool, cs:32)
        //   HitPoints (INT field, cs:26; set from Stats.HitPoints at cs:1072) -> cast to record's float
        //   CurrentSite (GeoSite, cs:28) -> CurrentSiteId via GeoSite.SiteId int field (GeoSite.cs:45); -1 if none
        //   DestinationSites (List<GeoSite>, cs:30) -> ordered int[] of SiteId
        //   VehicleID (int, cs:45; set at cs:1076)
        // FactionGuid comes from the live GeoVehicle.Owner (property, GeoVehicle.cs:111) via FactionGuid().
        // Seq + ChangedMask are LEFT DEFAULT (0) — the host broadcaster/differ (Task 7/10) assigns them.
        // Returns default(GeoVehicleStateRecord) if the vehicle or the InstanceData type is unavailable.
        public static GeoVehicleStateRecord RecordVehicleState(object vehicle)
        {
            if (vehicle == null) return default(GeoVehicleStateRecord);

            EnsureReflect();
            if (_gvidType == null) return default(GeoVehicleStateRecord);

            // FRESH isolated snapshot target; the native void override fills it in place.
            var data = Activator.CreateInstance(_gvidType);
            _recordInstanceData?.Invoke(vehicle, new[] { data });

            var record = new GeoVehicleStateRecord();

            var owner = _ownerProp?.GetValue(vehicle);
            record.FactionGuid = owner != null ? FactionGuid(owner) : "";

            var vehicleId = _fInstVehicleID?.GetValue(data);
            record.VehicleID = vehicleId is int vid ? vid : 0;

            var pos = _fSurfacePos?.GetValue(data);
            if (pos is Vector3 p) { record.PosX = p.x; record.PosY = p.y; record.PosZ = p.z; }

            var rot = _fSurfaceRot?.GetValue(data);
            if (rot is Quaternion q) { record.RotX = q.x; record.RotY = q.y; record.RotZ = q.z; record.RotW = q.w; }

            var range = _fRangeRemaining?.GetValue(data);
            record.RangeRemaining = range is float rr ? rr : 0f;

            var travelling = _fTravellingData?.GetValue(data);
            record.Travelling = travelling is bool tv && tv;

            // GeoVehicleInstanceData.HitPoints is an INT (cs:26); the wire record carries it as float.
            var hp = _fHitPointsData?.GetValue(data);
            record.HitPoints = hp is int hpi ? hpi : 0;

            var currentSite = _fCurrentSite?.GetValue(data);
            record.CurrentSiteId = SiteIdInt(currentSite); // -1 when no current site

            var destSites = _fDestinationSites?.GetValue(data) as IEnumerable;
            var destIds = new List<int>();
            if (destSites != null)
                foreach (var s in destSites)
                    destIds.Add(SiteIdInt(s));
            record.DestinationSiteIds = destIds.ToArray();

            return record;
        }

        // PERF (INC-3a lag fix): build a CHEAP managed signature of a GeoVehicle for the host's per-tick dirty
        // pre-check, WITHOUT the native RecordInstanceData fill or any Activator allocation that RecordVehicleState
        // does. Reads only live managed members via the cached handles: Surface.position/rotation (pure Transform
        // reads), VehicleID (field), Owner→Def.Guid (FactionGuid), RangeRemaining (prop→EarthUnits.Value),
        // Travelling (prop), CurrentSite→SiteId, Stats.HitPoints. Returns false (and a default sig) if the
        // core reflection handles aren't resolved yet — the
        // caller then falls back to the full RecordVehicleState path. The sig is a struct (out param), so the
        // steady-state no-change path allocates nothing on the heap except the small boxing of the bool/int
        // reflected reads (a ~95% cut vs the full native snapshot — full zero-alloc would need typed accessors,
        // not worth the complexity here).
        public static bool TryGetCheapVehicleSignature(object vehicle, out GeoVehicleCheapSig sig)
        {
            sig = default(GeoVehicleCheapSig);
            if (vehicle == null) return false;

            EnsureReflect();
            if (_surfaceProp == null || _fVehicleID == null) return false;

            var owner = _ownerProp?.GetValue(vehicle);
            sig.FactionGuid = owner != null ? FactionGuid(owner) : "";

            var vid = _fVehicleID.GetValue(vehicle);
            sig.VehicleID = vid is int i ? i : 0;

            var surface = _surfaceProp.GetValue(vehicle) as Transform;
            if (surface != null)
            {
                var pos = surface.position; sig.PosX = pos.x; sig.PosY = pos.y; sig.PosZ = pos.z;
                var rot = surface.rotation; sig.RotX = rot.x; sig.RotY = rot.y; sig.RotZ = rot.z; sig.RotW = rot.w;
            }

            // RangeRemaining is an EarthUnits struct; read its public float Value field (managed, no native fill).
            var range = _rangeRemainingProp?.GetValue(vehicle);
            var rangeVal = range != null ? _euValueField?.GetValue(range) : null;
            sig.RangeRemaining = rangeVal is float rv ? rv : 0f;

            var travelling = _travellingProp?.GetValue(vehicle);
            sig.Travelling = travelling is bool tv && tv;

            var currentSite = _currentSiteProp?.GetValue(vehicle);
            sig.CurrentSiteId = currentSite != null && _siteIdField?.GetValue(currentSite) is int csid ? csid : -1;

            var stats = _statsField?.GetValue(vehicle);
            var hp = stats != null ? _statsHitPoints?.GetValue(stats) : null;
            sig.HitPoints = hp is int hpi2 ? hpi2 : 0;

            return true;
        }

        // ALL-FACTIONS client mirror — LIGHT path (INC-3a): apply a 0x35 GeoStateDiff Vehicle record onto a
        // live GeoVehicle by writing ONLY the fields whose ChangedMask bit is set, via direct property/field
        // setters (no Stats re-clone, no equipment/unit rebuild — cheap enough to run per UNRELIABLE pos tick).
        // The net field writes mirror GeoVehicle.ProcessInstanceData (decompile GeoVehicle.cs:1082-1140) for
        // exactly these 7 synced fields:
        //   SurfacePos  -> Surface.position   (GeoVehicle.Surface is a UnityEngine.Transform property, cs:89; ProcessInstanceData sets .position at cs:1089)
        //   SurfaceRot  -> Surface.rotation   (cs:1090)
        //   RangeRemaining -> new EarthUnits(value) assigned to the RangeRemaining property (public setter does Range.Range=value, cs:156-166; ProcessInstanceData cs:1093)
        //   Travelling  -> Travelling property (public setter; side-effect: set TRUE clears CurrentSite via VehicleLeft, cs:201-219 / cs:1097)
        //   CurrentSite -> CurrentSite property (PRIVATE setter, reflected; cs:168 / cs:1094)
        //   DestinationSites -> _destinationSites private List<GeoSite> field cleared+refilled (cs:53 / cs:1095-1096)
        //   HitPoints   -> Stats.HitPoints int field (GeoVehicle.Stats field cs:41, GeoVehicleStats.HitPoints int cs:19 / cs:1072)
        // ORDERING (matches ProcessInstanceData's net result + the Travelling side-effect at cs:212-216 where
        // Travelling=true clears CurrentSite): set Travelling FIRST, then CurrentSite, then the rest — so an
        // arrival record (Travelling=false + CurrentSite=site) lands CurrentSite without the depart side-effect
        // nulling it, and a departure record (Travelling=true) clears CurrentSite as the engine would.
        // DestinationSites is SKIPPED (left untouched) if any of its site ids cannot resolve yet (the site is
        // not synced on the client) — a later periodic push self-heals once that site arrives.
        // EarthUnits is PhoenixPoint.Common.Core.EarthUnits (decompile EarthUnits.cs:8-36, ctor(float)) — NOT
        // Base.Utils.EarthUnits (no such type; grounding correction vs the plan).
        // Reflection-only over live types; null-guarded; never throws on a missing member.
        public static void ApplyVehicleState(object vehicle, GeoVehicleStateRecord r)
        {
            if (vehicle == null) return;
            var vt = vehicle.GetType();
            int mask = r.ChangedMask;
            object geoLevel = null; // resolved lazily only if a site lookup is needed

            // PRE-APPLY Travelling snapshot (read BEFORE step 1 writes the diff's Travelling bit). Used to gate
            // the RangeRemaining write below: while the client craft is locally Travelling, its OWN native
            // NavigateRoutine OWNS RangeRemaining (it decrements it each frame at GeoNavComponent.cs:124), so the
            // host's streamed RangeRemaining must NOT clobber it mid-flight (DIAG-NAV saw a +1482 backward jump,
            // 510->1992). Reading PRE-apply ensures an ARRIVAL diff still lands the authoritative range: a craft
            // that is no longer travelling (arrived/idle) has wasTravellingPreApply=false -> range applies.
            EnsureReflect();
            bool wasTravellingPreApply = _travellingProp?.GetValue(vehicle) is bool wt && wt;

            // 1) Travelling FIRST (its setter may clear CurrentSite, cs:212-216).
            if ((mask & GeoStateMask.Travelling) != 0)
                AccessTools.Property(vt, "Travelling")?.SetValue(vehicle, r.Travelling);

            // 2) CurrentSite (private setter; -1 => null).
            if ((mask & GeoStateMask.CurrentSite) != 0)
            {
                object site = null;
                if (r.CurrentSiteId >= 0)
                {
                    geoLevel = geoLevel ?? GetGeoLevelController();
                    if (geoLevel != null) site = FindSiteById(geoLevel, r.CurrentSiteId);
                }
                AccessTools.Property(vt, "CurrentSite")?.SetValue(vehicle, site);
            }

            // 3) SurfacePos / SurfaceRot on the Transform (public getter, public Transform members).
            // PIVOT Step A: with command-replication the client runs its OWN NavigateRoutine off the synced clock,
            // which positions the craft every frame. The 0x35 per-tick transform stream would FIGHT that native
            // sim, so the POSITION apply (SurfacePos/SurfaceRot + interpolator push) is gated off when
            // !USE_TRANSFORM_STREAM. Discrete state (Travelling/CurrentSite/DestinationSites/HitPoints) above +
            // below STILL applies — only the position fight is removed. Flip USE_TRANSFORM_STREAM=true to restore
            // the legacy transform-stream mirror.
            if (Multipleer.Network.NetworkEngine.USE_TRANSFORM_STREAM)
            {
                var surface = AccessTools.Property(vt, "Surface")?.GetValue(vehicle) as Transform;
                if (surface != null)
                {
                    if ((mask & GeoStateMask.SurfacePos) != 0)
                        surface.position = new Vector3(r.PosX, r.PosY, r.PosZ);
                    if ((mask & GeoStateMask.SurfaceRot) != 0)
                        surface.rotation = new Quaternion(r.RotX, r.RotY, r.RotZ, r.RotW);
                    // Writing Surface.position alone does NOT move the on-globe ICON (placed by the pivot rotation,
                    // normally driven each frame by NavigateRoutine which the client never runs). Push this applied
                    // transform sample into the interpolator's per-identity ring buffer; the interpolator's own Tick
                    // is the SOLE writer that renders the icon at now − InterpDelay by lerp/slerp (INC-C smoothing).
                    if ((mask & GeoStateMask.SurfacePos) != 0)
                        ClientVehicleInterpolator.SetTarget(
                            vehicle, (r.FactionGuid ?? "", r.VehicleID),
                            new Vector3(r.PosX, r.PosY, r.PosZ),
                            new Quaternion(r.RotX, r.RotY, r.RotZ, r.RotW),
                            // HostSendTime is valid only when its mask bit is set; 0 → arrival-time fallback. double
                            // (geoscape clock ~6.4e10) — see GeoVehicleStateRecord.HostSendTime.
                            (mask & GeoStateMask.HostSendTime) != 0 ? r.HostSendTime : 0.0);
                }
            }

            // 4) RangeRemaining via new EarthUnits(value) (public setter). GATED on !wasTravellingPreApply:
            // while the client craft is LOCALLY travelling, its OWN native NavigateRoutine owns + monotonically
            // decrements RangeRemaining off the synced clock (GeoNavComponent.cs:124); the host's ~per-diff
            // streamed value lags/leads and CLOBBERS it mid-flight (DIAG-NAV: +1482 backward jump 510->1992),
            // poisoning the local travel. So during local flight we DEFER to the routine and skip the write.
            // When NOT travelling (arrived/idle — wasTravellingPreApply=false) the authoritative host range
            // MUST land so the native reachability check (GeoFaction.CalculateRemainingPossibleRange /
            // vehicle.RangeRemaining) stays correct (else destinations show "too far"). Pre-apply read means an
            // arrival diff (Travelling true->false) lands range only once the craft is actually no longer flying.
            if ((mask & GeoStateMask.RangeRemaining) != 0 && !wasTravellingPreApply)
            {
                var eu = MakeEarthUnits(r.RangeRemaining);
                if (eu != null) AccessTools.Property(vt, "RangeRemaining")?.SetValue(vehicle, eu);
            }

            // 5) DestinationSites: rebuild _destinationSites only if EVERY id resolves; else skip (self-heals).
            if ((mask & GeoStateMask.DestinationSites) != 0)
            {
                geoLevel = geoLevel ?? GetGeoLevelController();
                if (geoLevel != null && TryResolveSites(geoLevel, r.DestinationSiteIds, out var sites))
                {
                    var dest = AccessTools.Field(vt, "_destinationSites")?.GetValue(vehicle) as IList;
                    if (dest != null && sites is IEnumerable resolved)
                    {
                        dest.Clear();
                        foreach (var s in resolved) dest.Add(s);
                    }
                }
            }

            // 6) HitPoints onto Stats.HitPoints (int field; record carries it as float).
            if ((mask & GeoStateMask.HitPoints) != 0)
            {
                var stats = AccessTools.Field(vt, "Stats")?.GetValue(vehicle);
                if (stats != null)
                    AccessTools.Field(stats.GetType(), "HitPoints")?.SetValue(stats, (int)r.HitPoints);
            }

            // DECISIVE PROBE (DIAG-NAV): host streams 0x35 for EVERY craft it sees travelling (incl. a player
            // craft frozen on the client), so this per-record light path is hit for both player + AI crafts.
            // Sample the public travel-progress signals here to compare advancing (AI) vs frozen (player).
            DiagNavObserve(vehicle, (r.FactionGuid ?? "", r.VehicleID));
        }

        // ALL-FACTIONS client mirror — HEAVY path (INC-3a): force the full authoritative state onto a live
        // GeoVehicle through the native GeoVehicle.ProcessInstanceData (decompile GeoVehicle.cs:1082) — the
        // literal mirror of TimeBridge.ApplyTimeState (TimeBridge.cs:107-122). Used ONLY for the FIRST mirror
        // of a vehicle on the client + a CRC-heal correction (it re-clones Stats at cs:1092 and rebuilds
        // equipment — too heavy to run per pos tick).
        // CRITICAL: ProcessInstanceData clobbers EVERYTHING it reads from the data — Name, Owner, weapons,
        // modules, tac units (cs:1088-1138). The wire record carries ONLY the 7 synced fields, so to avoid
        // WIPING the vehicle's existing weapons/units/name/owner we first RECORD the live vehicle's current
        // full state into a fresh instance (native void RecordInstanceData fill, GeoVehicle.cs:1053 — same
        // pattern as RecordVehicleState), THEN overwrite ONLY the 7 synced fields, THEN ProcessInstanceData.
        // CurrentSite/DestinationSites are resolved from int site ids; an unresolved DestinationSites set is
        // left as the live-recorded value (best-effort, consistent with the light path's skip).
        // HitPoints: we set GVID.InstanceDataVersion=3 before ProcessInstanceData so it takes the DIRECT HP branch
        // (cs:1130) instead of the legacy invert branch (cs:1124-1127) — see the inline note below.
        // Reflection-only over live types; null-guarded; never throws.
        public static void ApplyVehicleStateFull(object vehicle, GeoVehicleStateRecord r)
        {
            if (vehicle == null) return;

            var gvidType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicleInstanceData");
            var actorDataType = AccessTools.TypeByName("Base.Entities.ActorInstanceData");
            if (gvidType == null || actorDataType == null) return;

            // Fill a fresh instance with the vehicle's CURRENT full state so unsynced fields (weapons/units/
            // name/owner) survive the round-trip through ProcessInstanceData.
            var data = Activator.CreateInstance(gvidType);
            AccessTools.Method(vehicle.GetType(), "RecordInstanceData", new[] { actorDataType })
                       ?.Invoke(vehicle, new[] { data });

            // Overwrite ONLY the 7 synced wire fields onto the recorded data.
            AccessTools.Field(gvidType, "SurfacePos")?.SetValue(data, new Vector3(r.PosX, r.PosY, r.PosZ));
            AccessTools.Field(gvidType, "SurfaceRot")?.SetValue(data, new Quaternion(r.RotX, r.RotY, r.RotZ, r.RotW));
            AccessTools.Field(gvidType, "RangeRemaining")?.SetValue(data, r.RangeRemaining);
            AccessTools.Field(gvidType, "Travelling")?.SetValue(data, r.Travelling);
            AccessTools.Field(gvidType, "HitPoints")?.SetValue(data, (int)r.HitPoints); // GVID.HitPoints is INT (cs:26)

            // CRITICAL: force the CURRENT-FORMAT HitPoints branch in ProcessInstanceData. A fresh
            // Activator.CreateInstance'd GVID has InstanceDataVersion=0 (the field is [DoNotSerialize], cs:54, and
            // is ONLY assigned in the PostRead OnDeserialized callback to serObj.SerializedVersion, cs:66-70 — a
            // hand-built instance never runs that). With version<3, ProcessInstanceData takes the LEGACY branch
            // (GeoVehicle.cs:1124-1127) Stats.HitPoints = Clamp(MaxHitPoints - gvData.HitPoints), INVERTING our
            // direct HP — a first-mirror full-HP craft (HitPoints==MaxHitPoints) would land at HP=0 and trip
            // OnAircraftBreakingDown. Our HitPoints (both the RecordInstanceData-filled value at cs:1072 and the
            // overwritten wire r.HitPoints) are DIRECT current-HP, i.e. the current format (SerializeType
            // Version=3, cs:12), so set InstanceDataVersion=3 to select the DIRECT branch (cs:1130)
            // Stats.HitPoints = Clamp(gvData.HitPoints). No-op on the light path (it sets Stats.HitPoints directly).
            AccessTools.Field(gvidType, "InstanceDataVersion")?.SetValue(data, 3);

            var geoLevel = GetGeoLevelController();
            if (geoLevel != null)
            {
                object site = r.CurrentSiteId >= 0 ? FindSiteById(geoLevel, r.CurrentSiteId) : null;
                AccessTools.Field(gvidType, "CurrentSite")?.SetValue(data, site);

                if (TryResolveSites(geoLevel, r.DestinationSiteIds, out var sites))
                    AccessTools.Field(gvidType, "DestinationSites")?.SetValue(data, sites);
                // else: leave the live-recorded DestinationSites (best-effort, self-heals on next push).
            }

            AccessTools.Method(vehicle.GetType(), "ProcessInstanceData", new[] { actorDataType })
                       ?.Invoke(vehicle, new[] { data });

            // (REVERTED) The per-vehicle monotonic Now-clamp that used to run here was confirmed DEAD
            // (backwardPrevented=False always; vehNow rose for all crafts) and is removed — the native
            // ProcessInstanceData rebase is left exactly as stock.

            // The native ProcessInstanceData sets Surface.position/.rotation (GeoVehicle.cs:1089-1090) but
            // does NOT re-orient the globe ICON pivot. Seed the interpolator's ring buffer with this first
            // transform sample; with a single sample the interpolator renders it directly (Direct mode) so the
            // craft appears at the right spot exactly, then smooths once ≥2 samples have arrived (INC-C).
            ClientVehicleInterpolator.SetTarget(
                vehicle, (r.FactionGuid ?? "", r.VehicleID),
                new Vector3(r.PosX, r.PosY, r.PosZ),
                new Quaternion(r.RotX, r.RotY, r.RotZ, r.RotW),
                // First-mirror record is a FULL mask → HostSendTime bit is set; 0 → arrival-time fallback. double
                // (geoscape clock ~6.4e10) — see GeoVehicleStateRecord.HostSendTime.
                (r.ChangedMask & GeoStateMask.HostSendTime) != 0 ? r.HostSendTime : 0.0);
        }

        // INC-3a globe-icon placement (PURE MIRROR — no client sim): orient the on-globe vehicle ICON from an
        // arbitrary WORLD position by invoking the native GeoActor.SetOrientedGlobeWorldPosition (GeoActor.cs:
        // 66-77), which sets PivotTransform.localRotation from that position (FromToRotation(forward, worldPos -
        // geoscapeCenter) — DIRECTION only). Writing Surface.position alone does NOT move the icon: on the
        // geoscape the icon is positioned by the pivot rotation, normally driven each frame by GeoNavComponent
        // .NavigateRoutine (GeoNavComponent.cs:117-119) — a coroutine the client never runs (INC-3a retired the
        // client StartTravel/Navigate + the geo producers are suppressed). Called every frame by
        // ClientVehicleInterpolator with the EASED position between two ~10Hz host snapshots (smooth flight),
        // and on snap with the exact host position. Works for ANY faction's mirrored craft; the separately-
        // mirrored Surface.rotation (in-plane heading) is preserved — this only sets the pivot orientation that
        // carries the icon to the right spot on the globe. Reflection-only, null-guarded, never throws — a
        // missing handle is a no-op (icon stays where it was, no worse than before).
        public static void PlaceGlobeIconAt(object vehicle, Vector3 worldPos)
        {
            if (vehicle == null) return;
            EnsureReflect();
            if (_setOrientedGlobeWorldPos == null) return;
            try { _setOrientedGlobeWorldPos.Invoke(vehicle, new object[] { worldPos }); }
            catch { }
        }

        // INC-C (P4) legacy: write the whole Surface.rotation world quaternion. SUPERSEDED for the per-frame
        // nose by UpdateVehicleHeadingTowards (INC-D P3): the streamed world quat does NOT encode the in-plane
        // heading (the visible nose = Surface.localEulerAngles.z, which the host sets via NavigateRoutine's
        // UpdateHeading, NOT captured by SurfaceRot), so writing it left the client nose pointing "up". The
        // interpolator no longer calls this per frame; kept for completeness/first-mirror parity. Reflection-only.
        public static void SetSurfaceRotation(object vehicle, Quaternion worldRot)
        {
            if (vehicle == null) return;
            EnsureReflect();
            if (_surfaceProp == null) return;
            try
            {
                var surface = _surfaceProp.GetValue(vehicle) as Transform;
                if (surface != null) surface.rotation = worldRot;
            }
            catch { }
        }

        // INC-D P3 (client nose/heading, PURE MIRROR — no movement routine): point the mirrored craft's nose
        // along its travel direction by REUSING the native heading math, exactly as NavigateRoutine does each
        // frame (GeoNavComponent.cs:201-264): heading = GetHeadingTowardsTarget(destWorldPos); UpdateHeading(
        // heading, instant:false) writes Surface.localEulerAngles.z (the visible nose) smoothing from the current
        // pivot orientation. We call this from ClientVehicleInterpolator.Tick AFTER PlaceGlobeIconAt (which sets
        // PivotTransform.localRotation with Z ZEROED, GeoActor.cs:76 — placement only, never the nose), so the two
        // never clash. destWorldPos = DestinationSites[0].WorldPosition (the next waypoint the host streamed; the
        // native code aims at segment.End which is that same next waypoint). This is NOT a second simulator: it
        // runs no NavigateRoutine, integrates no position, and only orients the nose the host already implies via
        // the streamed DestinationSites. No DestinationSites (parked) → no-op (nose held). Reflection-only,
        // null-guarded, never throws — any missing handle leaves the nose unchanged (no worse than before).
        public static void UpdateVehicleHeadingTowards(object vehicle)
        {
            if (vehicle == null) return;
            EnsureReflect();
            if (_navigationField == null || _getHeadingTowardsTarget == null || _updateHeading == null
                || _destinationSitesField == null || _siteWorldPosProp == null) return;
            try
            {
                var nav = _navigationField.GetValue(vehicle);
                if (nav == null) return;

                // Next waypoint = first destination site. The native loop heads toward segment.End (the next
                // waypoint); DestinationSites[0] is that next site for the current leg.
                var destSites = _destinationSitesField.GetValue(vehicle) as IList;
                if (destSites == null || destSites.Count == 0) return; // parked / no path → leave nose as-is
                var firstSite = destSites[0];
                if (firstSite == null) return;
                if (!(_siteWorldPosProp.GetValue(firstSite) is Vector3 targetWorldPos)) return;

                // NOT-ARRIVED guard (mirror analogue of native NavigateRoutine's `if (num < 1f)` gate,
                // GeoNavComponent.cs:117): when the craft is essentially ON its next waypoint, the target
                // direction (targetPos − WorldPosition) collapses to ~zero and GetHeadingTowardsTarget's
                // Cross/normalized → NaN heading, which would write a NaN Surface.localEulerAngles.z that can
                // stick in the Transform until the next push dequeues the site. Skip the heading update for that
                // frame (nose held); the host's next 0x35 drops the reached site so this self-heals immediately.
                var surfaceForPos = _surfaceProp?.GetValue(vehicle) as Transform;
                if (surfaceForPos != null
                    && (targetWorldPos - surfaceForPos.position).sqrMagnitude < 1e-6f) return;

                // GetHeadingTowardsTarget expects a WORLD position (it subtracts NavActor.Actor.WorldPosition);
                // GeoSite.WorldPosition is already world, so pass it directly (no NavigationParent.TransformPoint,
                // which native uses only because its segment.End is in NavigationParent-local space).
                var headingObj = _getHeadingTowardsTarget.Invoke(nav, new object[] { targetWorldPos });
                if (!(headingObj is float heading) || float.IsNaN(heading) || float.IsInfinity(heading)) return;

                // instant:false → smooth-rotate toward the target each Tick (the geoscape Timing still ticks on
                // the client, host-synced via 0x34, so UpdateHeading's Timing.Delta-based slerp is well-defined),
                // matching the native per-frame feel without re-running the travel coroutine.
                _updateHeading.Invoke(nav, new object[] { heading, false });
            }
            catch { }
        }

        // FIX B (client nose/heading, PURE MIRROR along the INTERPOLATED TRAVEL DIRECTION): point the mirrored
        // craft's nose along its instantaneous motion vector — the delta between the two interpolation-bracket
        // samples (worldDir = pos[i1] − pos[i0]) — instead of aiming at the far DestinationSites[0] waypoint.
        //
        // WHY NOT the streamed SurfaceRot: a prior iteration (INC-D P3) tried writing the streamed Surface world
        // quaternion to the nose and it left the craft pointing "up" — the visible nose is Surface.localEulerAngles.z
        // (set host-side by NavigateRoutine.UpdateHeading) and is NOT encoded in the SurfaceRot world quat, so the
        // wire quat cannot drive the nose. That route is documented-broken; we do not revisit it.
        //
        // WHY this over the waypoint aim: aiming at the fixed far waypoint with instant:false slerps the nose
        // toward a constant bearing while the interpolated position jitters → the slerp chases a moving error =
        // side-to-side wobble. Aiming along the per-frame interpolated travel direction with instant:TRUE snaps the
        // nose exactly onto the host's actual motion vector every frame (no slerp to fight), and with FIX A the
        // samples are now dense so the direction is smooth → straight, wobble-free flight that mirrors the host.
        // Reuses the SAME native heading math (GetHeadingTowardsTarget builds the bearing from a world target, which
        // it offsets by NavActor.Actor.WorldPosition): we synthesize a target one travel-step AHEAD of the craft's
        // current (just-placed) world position. Near-zero delta (parked / arrived / between identical samples) →
        // hold the nose (skip), mirroring the native not-arrived guard and avoiding a NaN heading. Reflection-only,
        // null-guarded, never throws — any missing handle leaves the nose unchanged.
        public static void UpdateVehicleHeadingAlong(object vehicle, Vector3 currentWorldPos, Vector3 worldDir)
        {
            if (vehicle == null) return;
            // Sub-epsilon motion: no reliable direction this frame → hold nose (also dodges the GetHeadingTowardsTarget
            // NaN when the direction normalizes from ~zero). 1e-6 matches the not-arrived guard in the sibling method.
            if (worldDir.sqrMagnitude < 1e-6f) return;
            EnsureReflect();
            if (_navigationField == null || _getHeadingTowardsTarget == null || _updateHeading == null) return;
            try
            {
                var nav = _navigationField.GetValue(vehicle);
                if (nav == null) return;

                // Target = a point one travel-step ahead of where we just placed the icon. GetHeadingTowardsTarget
                // subtracts the craft's own WorldPosition internally, so only the DIRECTION of (target − pos) matters.
                var targetWorldPos = currentWorldPos + worldDir;
                var headingObj = _getHeadingTowardsTarget.Invoke(nav, new object[] { targetWorldPos });
                if (!(headingObj is float heading) || float.IsNaN(heading) || float.IsInfinity(heading)) return;

                // instant:TRUE — snap straight onto the travel bearing each frame (no slerp lag fighting the
                // interpolated position). With FIX A's dense samples the bearing itself is smooth.
                _updateHeading.Invoke(nav, new object[] { heading, true });
            }
            catch { }
        }

        // PIVOT Step A: read a single GeoVehicle's RangeRemaining as a float (meters via EarthUnits.Value),
        // reusing the cached _rangeRemainingProp/_euValueField the full snapshot uses. Stamped into the
        // host-origin StartTravel command (StartRangeRemaining) so the client's NavigateRoutine progress can
        // be reconciled against the host's range origin. 0 on any missing handle (caller treats 0 as absent).
        public static float ReadRangeRemaining(object vehicle)
        {
            if (vehicle == null) return 0f;
            EnsureReflect();
            var range = _rangeRemainingProp?.GetValue(vehicle);
            var rangeVal = range != null ? _euValueField?.GetValue(range) : null;
            return rangeVal is float rv ? rv : 0f;
        }

        // Build a EarthUnits (PhoenixPoint.Common.Core.EarthUnits, ctor(float) at EarthUnits.cs:33) boxed as
        // object for reflective property assignment. Null if the type is unavailable.
        private static object MakeEarthUnits(float value)
        {
            var euType = AccessTools.TypeByName("PhoenixPoint.Common.Core.EarthUnits");
            if (euType == null) return null;
            return Activator.CreateInstance(euType, new object[] { value });
        }

        // Resolve int site ids to a typed List<GeoSite> (as object), in order. Returns true with the list ONLY
        // if EVERY id resolves (a missing site = not yet synced on the client); false otherwise so the caller
        // can skip the DestinationSites apply and let a later push self-heal. An empty/null id array resolves
        // to an empty list (true) — a cleared destination set is a valid synced state.
        private static bool TryResolveSites(object geoLevel, int[] siteIds, out object list)
        {
            list = null;
            var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (geoSiteType == null) return false;
            var listType = typeof(List<>).MakeGenericType(geoSiteType);
            var typed = (IList)Activator.CreateInstance(listType);
            if (siteIds != null)
            {
                foreach (var id in siteIds)
                {
                    var site = FindSiteById(geoLevel, id);
                    if (site == null) return false; // not synced yet
                    typed.Add(site);
                }
            }
            list = typed;
            return true;
        }

        // GeoSite.SiteId (public int FIELD, default -1, GeoSite.cs:45) read as int. Returns -1 for a null site
        // (no current site / unresolved) or if the field is unreadable — matches the engine's own -1 default.
        private static int SiteIdInt(object site)
        {
            if (site == null) return -1;
            var v = AccessTools.Field(site.GetType(), "SiteId")?.GetValue(site);
            return v is int i ? i : -1;
        }

        // Build a List<GeoSite> from string ids, in order. Returns the typed list as object, or null
        // if the map/sites are unavailable or any id fails to resolve (caller aborts the apply).
        public static object BuildSitePath(object geoLevel, string[] siteIds)
        {
            var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (geoSiteType == null) return null;
            var listType = typeof(List<>).MakeGenericType(geoSiteType);
            var list = (IList)Activator.CreateInstance(listType);

            // GeoLevelController.Map is a FIELD, not a property.
            var map = AccessTools.Field(geoLevel.GetType(), "Map")?.GetValue(geoLevel);
            var sites = AccessTools.Property(map?.GetType(), "AllSites")?.GetValue(map) as IEnumerable;
            if (sites == null) return null;

            var byId = new Dictionary<string, object>();
            foreach (var s in sites) byId[SiteId(s)] = s;

            foreach (var id in siteIds)
                if (byId.TryGetValue(id, out var site)) list.Add(site);
                else return null;
            return list;
        }

        // GeoVehicle.VehicleID — public int FIELD.
        public static string VehicleId(object vehicle)
            => AccessTools.Field(vehicle.GetType(), "VehicleID")?.GetValue(vehicle)?.ToString() ?? "";

        // [DIAG2] TEMPORARY diagnostic (logging only, no behavior change). Build a compact, fully
        // null-guarded snapshot of EVERY faction's whole vehicle set, each entry keyed by the real
        // (factionGuid,VehicleID) identity: "factionGuid#id:defname, factionGuid#id:defname, ..." plus
        // the total count across all factions. Walks geoLevel.Factions (the same IList<GeoFaction> field
        // FindFactionByGuid reads at cs:160) -> per faction GeoFaction.Vehicles (the property
        // FindVehicleById uses). This exposes the live bug: the host moves a faction-keyed non-Phoenix
        // craft (e.g. a New Jericho Thunderbird) that the client's Phoenix-only FindVehicleById can never
        // resolve. Def name = GeoVehicle.VehicleDef (property) -> UnityEngine.Object.name (e.g.
        // "NA_Manticore_GeoVehicleDef"); falls back to the def Guid, then the runtime type name.
        // Defensive at every step so it can never throw or change control flow.
        public static (int Count, string List) DescribeVehicles(object geoLevel)
        {
            if (geoLevel == null) return (0, "<no-geoLevel>");
            IEnumerable factions;
            try { factions = AccessTools.Field(geoLevel.GetType(), "Factions")?.GetValue(geoLevel) as IEnumerable; }
            catch { return (0, "<factions-err>"); }
            if (factions == null) return (0, "<no-factions>");

            var sb = new System.Text.StringBuilder();
            int count = 0;
            foreach (var faction in factions)
            {
                if (faction == null) continue;
                string fguid;
                try { fguid = FactionGuid(faction); } catch { fguid = "?"; }

                IEnumerable vehicles;
                try { vehicles = AccessTools.Property(faction.GetType(), "Vehicles")?.GetValue(faction) as IEnumerable; }
                catch { continue; }
                if (vehicles == null) continue;

                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    if (count > 0) sb.Append(", ");
                    string id, name;
                    try { id = VehicleId(v); } catch { id = "?"; }
                    try { name = VehicleDefNameOf(v); } catch { name = "?"; }
                    sb.Append(fguid).Append('#').Append(id).Append(':').Append(name);
                    count++;
                }
            }
            return (count, sb.ToString());
        }

        // DIAG-A1 TEMP (strip after RCA) — lockstep readout helper. Scans all factions' Vehicles for the FIRST
        // one with Travelling==true and reports routineActive=true + its globe Surface.position. routineActive is
        // a proxy for "a native NavigateRoutine is live on this client" (Travelling is set true by the routine's
        // InitiateTravelling and cleared at arrival). craftPos is the rendered on-globe position. (false, zero) if
        // none travelling / unreachable. Best-effort, never throws (mirrors DescribeVehicles' defensive walk).
        public static void DescribeFirstTravellingVehicle(out bool routineActive, out Vector3 craftPos)
        {
            routineActive = false;
            craftPos = Vector3.zero;
            var geoLevel = GetGeoLevelController();
            if (geoLevel == null) return;
            EnsureReflect();
            IEnumerable factions;
            try { factions = AccessTools.Field(geoLevel.GetType(), "Factions")?.GetValue(geoLevel) as IEnumerable; }
            catch { return; }
            if (factions == null) return;

            foreach (var faction in factions)
            {
                if (faction == null) continue;
                IEnumerable vehicles;
                try { vehicles = AccessTools.Property(faction.GetType(), "Vehicles")?.GetValue(faction) as IEnumerable; }
                catch { continue; }
                if (vehicles == null) continue;

                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    bool travelling;
                    try { travelling = _travellingProp?.GetValue(v) is bool tb && tb; }
                    catch { continue; }
                    if (!travelling) continue;

                    routineActive = true;
                    try
                    {
                        var surface = AccessTools.Property(v.GetType(), "Surface")?.GetValue(v) as Transform;
                        if (surface != null) craftPos = surface.position;
                    }
                    catch { /* leave craftPos zero */ }
                    return; // first travelling vehicle is enough for the lockstep readout
                }
            }
        }

        // [DIAG2] TEMPORARY. Best-effort human-readable def name for a GeoVehicle. Tries VehicleDef
        // (property) -> name; then VehicleDef -> Guid; then the vehicle's runtime type. Never throws.
        public static string VehicleDefNameOf(object vehicle)
        {
            if (vehicle == null) return "<null>";
            object def = null;
            try { def = AccessTools.Property(vehicle.GetType(), "VehicleDef")?.GetValue(vehicle); }
            catch { /* fall through */ }
            if (def != null)
            {
                try
                {
                    // BaseDef : ScriptableObject -> UnityEngine.Object.name is a property.
                    var n = AccessTools.Property(def.GetType(), "name")?.GetValue(def) as string;
                    if (!string.IsNullOrEmpty(n)) return n;
                }
                catch { /* fall through */ }
                var guid = DefGuid(def);
                if (!string.IsNullOrEmpty(guid)) return guid;
            }
            return vehicle.GetType().Name;
        }

        // GeoSite.SiteId — public int FIELD.
        public static string SiteId(object site)
            => AccessTools.Field(site.GetType(), "SiteId")?.GetValue(site)?.ToString() ?? "";

        // DefRepository.GetDef(string guid) -> BaseDef, via GameUtl.GameComponent<DefRepository>().
        // Returns the def object (ComponentSetDef-derived for vehicles) or null.
        public static object FindDefByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            var defRepoType = AccessTools.TypeByName("Base.Defs.DefRepository");
            var gameUtlType = AccessTools.TypeByName("Base.Core.GameUtl");
            if (defRepoType == null || gameUtlType == null) return null;
            // GameUtl.GameComponent<DefRepository>() — generic static; make + invoke.
            var generic = AccessTools.Method(gameUtlType, "GameComponent");
            var repo = generic?.MakeGenericMethod(defRepoType)?.Invoke(null, null);
            if (repo == null) return null;
            return AccessTools.Method(repo.GetType(), "GetDef", new[] { typeof(string) })
                              ?.Invoke(repo, new object[] { guid });
        }

        // Resolve a GeoFaction by its Def.Guid from GeoLevelController.Factions; falls back to
        // PhoenixFaction (the common INC-2 case: manufactured aircraft) when guid is empty/unmatched.
        public static object FindFactionByGuid(object geoLevel, string factionGuid)
        {
            var phoenix = AccessTools.Property(geoLevel.GetType(), "PhoenixFaction")?.GetValue(geoLevel);
            if (string.IsNullOrEmpty(factionGuid)) return phoenix;
            var factions = AccessTools.Field(geoLevel.GetType(), "Factions")?.GetValue(geoLevel) as IEnumerable;
            if (factions == null) return phoenix;
            foreach (var f in factions)
                if (FactionGuid(f) == factionGuid) return f;
            return phoenix;
        }

        // STRICT faction resolver (INC-3a): like FindFactionByGuid but with NO Phoenix fallback for a
        // non-empty-but-unmatched guid. An EMPTY/null guid still resolves to PhoenixFaction (the INC-2
        // convention for Phoenix-manufactured aircraft that carry no owner guid on the wire); a NON-EMPTY
        // guid that matches no faction returns NULL rather than silently falling back to Phoenix. Required by
        // the all-faction (factionGuid,VehicleID) resolver: a stale/unknown non-Phoenix guid must never
        // mis-resolve to a Phoenix craft of the same VehicleID. Used by FindVehicleByFactionAndId.
        public static object FindFactionByGuidStrict(object geoLevel, string factionGuid)
        {
            if (string.IsNullOrEmpty(factionGuid))
                return AccessTools.Property(geoLevel.GetType(), "PhoenixFaction")?.GetValue(geoLevel);
            var factions = AccessTools.Field(geoLevel.GetType(), "Factions")?.GetValue(geoLevel) as IEnumerable;
            if (factions == null) return null;
            foreach (var f in factions)
                if (FactionGuid(f) == factionGuid) return f;
            return null;
        }

        // GeoFaction.Def.Guid (Def -> BaseDef.Guid). Empty string if unresolved.
        public static string FactionGuid(object faction)
        {
            var def = AccessTools.Property(faction.GetType(), "Def")?.GetValue(faction);
            if (def == null) return "";
            return AccessTools.Field(def.GetType(), "Guid")?.GetValue(def)?.ToString() ?? "";
        }

        // BaseDef.Guid of an arbitrary def object. Used to broadcast the ComponentSetDef the create method
        // received (its 2nd arg), NOT GeoVehicle.VehicleDef (a GeoVehicleDef — a SIBLING of ComponentSetDef
        // under BaseDef, so it would NOT resolve to a ComponentSetDef on the client). ComponentSetDef :
        // ObjectDef : BaseDef, and BaseDef.Guid is a public string FIELD, so this reads on any def.
        public static string DefGuid(object def)
        {
            if (def == null) return "";
            return AccessTools.Field(def.GetType(), "Guid")?.GetValue(def)?.ToString() ?? "";
        }

        // GeoSite by int SiteId (string key), scanning GeoMap.AllSites. Null if not found.
        public static object FindSiteById(object geoLevel, int siteId)
        {
            var map = AccessTools.Field(geoLevel.GetType(), "Map")?.GetValue(geoLevel);
            var sites = AccessTools.Property(map?.GetType(), "AllSites")?.GetValue(map) as IEnumerable;
            if (sites == null) return null;
            foreach (var s in sites)
                if (SiteId(s) == siteId.ToString()) return s;
            return null;
        }

        // GeoFaction.CreateVehicle(GeoSite, ComponentSetDef) — runs the full native lifecycle
        // (Instantiate -> DoEnterPlay -> OnLevelStart -> TeleportToSite -> VehicleAdded). Returns the
        // new GeoVehicle, or null if the method/types are unresolved.
        public static object CreateVehicleAtSite(object faction, object site, object vehicleDef)
        {
            var siteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var csdType = AccessTools.TypeByName("Base.Core.ComponentSetDef");
            if (siteType == null || csdType == null) return null;
            var m = AccessTools.Method(faction.GetType(), "CreateVehicle", new[] { siteType, csdType });
            return m?.Invoke(faction, new[] { site, vehicleDef });
        }

        // GeoFaction.CreateVehicleAtPosition(Vector3, ComponentSetDef) — full native lifecycle (pos path).
        public static object CreateVehicleAtPosition(object faction, Vector3 pos, object vehicleDef)
        {
            var csdType = AccessTools.TypeByName("Base.Core.ComponentSetDef");
            if (csdType == null) return null;
            var m = AccessTools.Method(faction.GetType(), "CreateVehicleAtPosition",
                new[] { typeof(Vector3), csdType });
            return m?.Invoke(faction, new object[] { pos, vehicleDef });
        }

        // Reconcile the new vehicle's id to the host's authoritative VehicleID and clamp the faction's
        // private _lastVehicleIndex so it never re-issues that id (collision-free, §9/C8).
        public static void ReconcileVehicleId(object faction, object vehicle, int authoritativeId)
        {
            AccessTools.Field(vehicle.GetType(), "VehicleID")?.SetValue(vehicle, authoritativeId);
            var fld = AccessTools.Field(faction.GetType(), "_lastVehicleIndex");
            var cur = fld?.GetValue(faction);
            if (cur is int c && authoritativeId > c)
                fld.SetValue(faction, authoritativeId);
        }
    }
}
