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

        // Vehicle id == GeoVehicle.VehicleID (public int field) rendered as string by the codec.
        public static object FindVehicleById(object geoLevel, string vehicleId)
        {
            var faction = AccessTools.Property(geoLevel.GetType(), "PhoenixFaction")?.GetValue(geoLevel);
            var vehicles = AccessTools.Property(faction?.GetType(), "Vehicles")?.GetValue(faction) as IEnumerable;
            if (vehicles == null) return null;
            foreach (var v in vehicles)
                if (VehicleId(v) == vehicleId) return v;
            return null;
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
