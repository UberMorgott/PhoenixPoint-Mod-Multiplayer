using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
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

            var gvidType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicleInstanceData");
            var actorDataType = AccessTools.TypeByName("Base.Entities.ActorInstanceData");
            if (gvidType == null || actorDataType == null) return default(GeoVehicleStateRecord);

            // FRESH isolated snapshot target; the native void override fills it in place.
            var data = Activator.CreateInstance(gvidType);
            AccessTools.Method(vehicle.GetType(), "RecordInstanceData", new[] { actorDataType })
                       ?.Invoke(vehicle, new[] { data });

            var record = new GeoVehicleStateRecord();

            var owner = AccessTools.Property(vehicle.GetType(), "Owner")?.GetValue(vehicle);
            record.FactionGuid = owner != null ? FactionGuid(owner) : "";

            var vehicleId = AccessTools.Field(gvidType, "VehicleID")?.GetValue(data);
            record.VehicleID = vehicleId is int vid ? vid : 0;

            var pos = AccessTools.Field(gvidType, "SurfacePos")?.GetValue(data);
            if (pos is Vector3 p) { record.PosX = p.x; record.PosY = p.y; record.PosZ = p.z; }

            var rot = AccessTools.Field(gvidType, "SurfaceRot")?.GetValue(data);
            if (rot is Quaternion q) { record.RotX = q.x; record.RotY = q.y; record.RotZ = q.z; record.RotW = q.w; }

            var range = AccessTools.Field(gvidType, "RangeRemaining")?.GetValue(data);
            record.RangeRemaining = range is float rr ? rr : 0f;

            var travelling = AccessTools.Field(gvidType, "Travelling")?.GetValue(data);
            record.Travelling = travelling is bool tv && tv;

            // GeoVehicleInstanceData.HitPoints is an INT (cs:26); the wire record carries it as float.
            var hp = AccessTools.Field(gvidType, "HitPoints")?.GetValue(data);
            record.HitPoints = hp is int hpi ? hpi : 0;

            var currentSite = AccessTools.Field(gvidType, "CurrentSite")?.GetValue(data);
            record.CurrentSiteId = SiteIdInt(currentSite); // -1 when no current site

            var destSites = AccessTools.Field(gvidType, "DestinationSites")?.GetValue(data) as IEnumerable;
            var destIds = new List<int>();
            if (destSites != null)
                foreach (var s in destSites)
                    destIds.Add(SiteIdInt(s));
            record.DestinationSiteIds = destIds.ToArray();

            return record;
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
            var surface = AccessTools.Property(vt, "Surface")?.GetValue(vehicle) as Transform;
            if (surface != null)
            {
                if ((mask & GeoStateMask.SurfacePos) != 0)
                    surface.position = new Vector3(r.PosX, r.PosY, r.PosZ);
                if ((mask & GeoStateMask.SurfaceRot) != 0)
                    surface.rotation = new Quaternion(r.RotX, r.RotY, r.RotZ, r.RotW);
            }

            // 4) RangeRemaining via new EarthUnits(value) (public setter).
            if ((mask & GeoStateMask.RangeRemaining) != 0)
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
