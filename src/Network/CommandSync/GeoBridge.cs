using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;

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
    }
}
