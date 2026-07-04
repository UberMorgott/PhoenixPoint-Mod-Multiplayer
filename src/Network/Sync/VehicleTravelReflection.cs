using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network.Sync.State;
using UnityEngine;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Reflection bridge for GeoVehicle TRAVEL — shared by the travel-intent relay
    /// (<see cref="Multipleer.Network.Sync.Actions.MoveVehicleAction"/> +
    /// <c>MoveVehiclePatch</c>) and the route-line metadata mirror (<c>GeoVehicleTravelMirror</c>). The mod has
    /// NO compile-time game references, so every member is resolved by name via <see cref="AccessTools"/> and
    /// cached (bind-once per session; mirrors <see cref="GeoSiteReflection"/> / <see cref="GeoRuntime"/>).
    ///
    /// Verified against the decompile (2026-07-04, <c>PhoenixPoint.Geoscape.Entities.GeoVehicle</c> /
    /// <c>PhoenixPoint.Geoscape.Entities.GeoSite</c> / <c>PhoenixPoint.Geoscape.Levels.GeoMap</c>):
    ///   • vehicles: <c>GeoLevelController.Map</c> (public FIELD) → <c>GeoMap.Vehicles</c> (public PROPERTY,
    ///     IList&lt;GeoVehicle&gt;); sites: <c>GeoMap.AllSites</c> (public PROPERTY, IEnumerable&lt;GeoSite&gt;).
    ///   • <c>GeoVehicle.VehicleID</c> : public int FIELD (:51, per-FACTION unique); <c>GeoVehicle.Owner</c> :
    ///     GeoFaction PROPERTY → <c>GeoFaction.Def</c> (GeoFactionDef : BaseDef : ScriptableObject).name →
    ///     <see cref="GeoVehiclePos.StableOwnerKey"/> (identical asset name on both instances). Composite key
    ///     (OwnerId, VehicleId) is shared with the 0xA5 position mirror.
    ///   • travel state: <c>GeoVehicle.Travelling</c> (bool PROP, backing FIELD <c>_traveling</c> :61),
    ///     <c>CurrentSite</c> ({ get; private set; } auto-prop, backing <c>&lt;CurrentSite&gt;k__BackingField</c>),
    ///     <c>DestinationSites</c> (ReadOnlyCollection PROP over private readonly List&lt;GeoSite&gt;
    ///     <c>_destinationSites</c> :53).
    ///   • command: <c>GeoVehicle.StartTravel(List&lt;GeoSite&gt;)</c> (:518) — THE single choke every
    ///     player-facing order flows through (MoveVehicleAbility.ActivateInternal :63 / TravelTo :556).
    ///   • <c>GeoSite.SiteId</c> : public int FIELD (:45, default -1).
    ///
    /// The HOST resolves + runs the authoritative <c>StartTravel</c>; the CLIENT (sim frozen) writes ONLY the
    /// display-feeding backing fields (no <c>Travelling</c> setter side-effects, no navigate) so the native
    /// route line reads correct state while the client never simulates. All reflection is null-safe: a missing
    /// member DEGRADES (best-effort) rather than throwing.
    /// </summary>
    public static class VehicleTravelReflection
    {
        private static bool _ready;
        private static Type _geoLevelType, _geoVehicleType, _geoSiteType, _geoSiteListType;
        private static FieldInfo _mapField;          // GeoLevelController.Map (GeoMap)
        private static PropertyInfo _vehiclesProp;   // GeoMap.Vehicles (IList<GeoVehicle>)
        private static PropertyInfo _allSitesProp;   // GeoMap.AllSites (IEnumerable<GeoSite>)
        private static FieldInfo _vehicleIdField;    // GeoVehicle.VehicleID (int)
        private static PropertyInfo _ownerProp;      // GeoVehicle.Owner (GeoFaction)
        private static PropertyInfo _factionDefProp; // GeoFaction.Def (GeoFactionDef)
        private static PropertyInfo _travellingProp; // GeoVehicle.Travelling (bool)
        private static PropertyInfo _currentSiteProp; // GeoVehicle.CurrentSite (GeoSite)
        private static PropertyInfo _destSitesProp;  // GeoVehicle.DestinationSites (ReadOnlyCollection<GeoSite>)
        private static FieldInfo _travelingField;    // GeoVehicle._traveling (bool backing)
        private static FieldInfo _destSitesField;    // GeoVehicle._destinationSites (List<GeoSite> backing)
        private static FieldInfo _currentSiteBacking; // GeoVehicle.<CurrentSite>k__BackingField (GeoSite)
        private static FieldInfo _siteIdField;       // GeoSite.SiteId (int)
        private static MethodInfo _startTravelMethod; // GeoVehicle.StartTravel(List<GeoSite>)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            _geoLevelType = geo.GetType();
            _mapField = AccessTools.Field(_geoLevelType, "Map");
            var map = _mapField?.GetValue(geo);
            if (map == null) return;
            _vehiclesProp = AccessTools.Property(map.GetType(), "Vehicles");
            _allSitesProp = AccessTools.Property(map.GetType(), "AllSites");

            _geoVehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            _geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (_geoVehicleType == null || _geoSiteType == null) return;

            _vehicleIdField = AccessTools.Field(_geoVehicleType, "VehicleID");
            _ownerProp = AccessTools.Property(_geoVehicleType, "Owner");
            _travellingProp = AccessTools.Property(_geoVehicleType, "Travelling");
            _currentSiteProp = AccessTools.Property(_geoVehicleType, "CurrentSite");
            _destSitesProp = AccessTools.Property(_geoVehicleType, "DestinationSites");
            _travelingField = AccessTools.Field(_geoVehicleType, "_traveling");
            _destSitesField = AccessTools.Field(_geoVehicleType, "_destinationSites");
            _currentSiteBacking = AccessTools.Field(_geoVehicleType, "<CurrentSite>k__BackingField");
            _siteIdField = AccessTools.Field(_geoSiteType, "SiteId");

            _geoSiteListType = typeof(List<>).MakeGenericType(_geoSiteType);
            // Exact param match disambiguates StartTravel(List<GeoSite>) from StartTravel(List<Vector3>).
            _startTravelMethod = AccessTools.Method(_geoVehicleType, "StartTravel", new[] { _geoSiteListType });

            _ready = _vehiclesProp != null && _vehicleIdField != null && _ownerProp != null
                     && _siteIdField != null && _startTravelMethod != null;
        }

        // ─── shared: composite (OwnerId, VehicleId) key off a live GeoVehicle ─────────────────────────────────

        /// <summary>Read a live vehicle's composite key halves: OwnerId =
        /// <see cref="GeoVehiclePos.StableOwnerKey"/> of the owner faction's def asset name, VehicleId =
        /// <c>GeoVehicle.VehicleID</c>. Symmetric on host and client (identical assets). False if unreadable.</summary>
        public static bool TryReadVehicleKey(object vehicle, out int ownerId, out int vehicleId)
        {
            ownerId = 0; vehicleId = 0;
            try
            {
                if (vehicle == null || _vehicleIdField == null || _ownerProp == null) return false;
                vehicleId = Convert.ToInt32(_vehicleIdField.GetValue(vehicle));
                object owner = _ownerProp.GetValue(vehicle, null);
                if (owner != null)
                {
                    if (_factionDefProp == null) _factionDefProp = AccessTools.Property(owner.GetType(), "Def");
                    var def = _factionDefProp?.GetValue(owner, null) as UnityEngine.Object;
                    ownerId = GeoVehiclePos.StableOwnerKey(def != null ? def.name : null);
                }
                return true;
            }
            catch { return false; }
        }

        // ─── interceptor side: read the ordered destination SiteIds off a List<GeoSite> path ──────────────────

        /// <summary>Read the ordered <c>GeoSite.SiteId</c>s from a <c>List&lt;GeoSite&gt;</c> travel path (the arg
        /// to the intercepted <c>StartTravel</c>). Returns an empty array on any failure (caller skips the relay).</summary>
        public static int[] ReadPathSiteIds(GeoRuntime rt, object path)
        {
            try
            {
                Ensure(rt);
                if (_siteIdField == null || !(path is IEnumerable sites)) return Array.Empty<int>();
                var ids = new List<int>();
                foreach (var s in sites)
                {
                    if (s == null) continue;
                    try { ids.Add(Convert.ToInt32(_siteIdField.GetValue(s))); } catch { }
                }
                return ids.ToArray();
            }
            catch { return Array.Empty<int>(); }
        }

        // ─── host apply: resolve the live vehicle + sites and run the authoritative StartTravel ───────────────

        /// <summary>The live <c>GeoMap.Vehicles</c> collection (all factions), or null when not in geoscape.</summary>
        private static IEnumerable ResolveVehicles(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null || _mapField == null) return null;
            object map = _mapField.GetValue(geo);
            if (map == null || _vehiclesProp == null) return null;
            return _vehiclesProp.GetValue(map, null) as IEnumerable;
        }

        /// <summary>Resolve the live <c>GeoVehicle</c> for a composite (OwnerId, VehicleId) key, or null.</summary>
        public static object ResolveVehicle(GeoRuntime rt, int ownerId, int vehicleId)
        {
            try
            {
                Ensure(rt);
                var vehicles = ResolveVehicles(rt);
                if (vehicles == null) return null;
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    if (TryReadVehicleKey(v, out int o, out int id) && o == ownerId && id == vehicleId)
                        return v;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>Resolve a live <c>GeoSite</c> by <c>SiteId</c> via <c>GeoMap.AllSites</c>, or null.</summary>
        public static object ResolveSite(GeoRuntime rt, int siteId)
        {
            try
            {
                Ensure(rt);
                if (siteId < 0 || _allSitesProp == null || _siteIdField == null) return null;
                var geo = rt?.GeoLevel();
                if (geo == null || _mapField == null) return null;
                object map = _mapField.GetValue(geo);
                if (map == null) return null;
                if (!(_allSitesProp.GetValue(map, null) is IEnumerable sites)) return null;
                foreach (var s in sites)
                {
                    if (s == null) continue;
                    try { if (Convert.ToInt32(_siteIdField.GetValue(s)) == siteId) return s; } catch { }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>HOST: resolve the vehicle + destination sites and run the authoritative
        /// <c>GeoVehicle.StartTravel(List&lt;GeoSite&gt;)</c>. Returns true if travel was started (≥1 site
        /// resolved). The resulting motion mirrors to clients via the 0xA5 position surface; the travel metadata
        /// via 0xA6. No-op (false) if the vehicle / all sites are unresolvable.</summary>
        public static bool StartTravel(GeoRuntime rt, int ownerId, int vehicleId, int[] destSiteIds)
        {
            try
            {
                Ensure(rt);
                if (!_ready || destSiteIds == null || destSiteIds.Length == 0) return false;
                object vehicle = ResolveVehicle(rt, ownerId, vehicleId);
                if (vehicle == null) return false;

                var typedList = (IList)Activator.CreateInstance(_geoSiteListType);
                foreach (var id in destSiteIds)
                {
                    var site = ResolveSite(rt, id);
                    if (site != null) typedList.Add(site);
                }
                if (typedList.Count == 0) return false;

                _startTravelMethod.Invoke(vehicle, new object[] { typedList });
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo] VehicleTravelReflection.StartTravel failed: " + ex.Message); return false; }
        }

        // ─── host read / client write: travel METADATA (route-line mirror) ────────────────────────────────────

        /// <summary>HOST: read a live vehicle's travel metadata into a pure <see cref="GeoVehicleTravelMeta"/>
        /// (composite key + Travelling + CurrentSite.SiteId + ordered DestinationSites' SiteIds). False if the
        /// key / travelling flag can't be read.</summary>
        public static bool TryReadTravelMeta(GeoRuntime rt, object vehicle, out GeoVehicleTravelMeta meta)
        {
            meta = default;
            try
            {
                Ensure(rt);
                if (vehicle == null || _travellingProp == null) return false;
                if (!TryReadVehicleKey(vehicle, out int ownerId, out int vehicleId)) return false;

                bool travelling = false;
                try { travelling = (bool)_travellingProp.GetValue(vehicle, null); } catch { }

                int currentSiteId = -1;
                try
                {
                    var cs = _currentSiteProp?.GetValue(vehicle, null);
                    if (cs != null && _siteIdField != null) currentSiteId = Convert.ToInt32(_siteIdField.GetValue(cs));
                }
                catch { currentSiteId = -1; }

                var dests = new List<int>();
                try
                {
                    if (_destSitesProp?.GetValue(vehicle, null) is IEnumerable sites && _siteIdField != null)
                        foreach (var s in sites)
                        {
                            if (s == null) continue;
                            try { dests.Add(Convert.ToInt32(_siteIdField.GetValue(s))); } catch { }
                        }
                }
                catch { }

                meta = new GeoVehicleTravelMeta(ownerId, vehicleId, travelling, currentSiteId, dests.ToArray());
                return true;
            }
            catch { return false; }
        }

        /// <summary>CLIENT (sim frozen): write <paramref name="meta"/> onto the resolved live vehicle's
        /// DISPLAY-feeding backing fields ONLY — <c>_traveling</c>, <c>&lt;CurrentSite&gt;k__BackingField</c>,
        /// and the contents of <c>_destinationSites</c>. Writing the backing fields directly avoids the
        /// <c>Travelling</c> setter's <c>CurrentSite.VehicleLeft</c> side-effect and NEVER navigates — pure
        /// mirror. Each field individually guarded. Returns true if the vehicle resolved.</summary>
        public static bool ApplyTravelMeta(GeoRuntime rt, GeoVehicleTravelMeta meta)
        {
            try
            {
                Ensure(rt);
                object vehicle = ResolveVehicle(rt, meta.OwnerId, meta.VehicleId);
                if (vehicle == null) return false;

                // Remaining destination waypoints → clear + refill the backing List<GeoSite> (the ReadOnlyCollection
                // DestinationSites wraps it, so the native line reads the fresh list). Unresolved ids are skipped.
                if (_destSitesField != null && _destSitesField.GetValue(vehicle) is IList destList)
                {
                    try
                    {
                        destList.Clear();
                        if (meta.DestSiteIds != null)
                            foreach (var id in meta.DestSiteIds)
                            {
                                var site = ResolveSite(rt, id);
                                if (site != null) destList.Add(site);
                            }
                    }
                    catch (Exception ex) { Debug.LogError("[Multipleer][geo] ApplyTravelMeta dests failed (skipped): " + ex.Message); }
                }

                // CurrentSite (backing field — no setter cascade). -1 / unresolved → null (in transit).
                if (_currentSiteBacking != null)
                {
                    try { _currentSiteBacking.SetValue(vehicle, meta.CurrentSiteId >= 0 ? ResolveSite(rt, meta.CurrentSiteId) : null); }
                    catch (Exception ex) { Debug.LogError("[Multipleer][geo] ApplyTravelMeta currentSite failed (skipped): " + ex.Message); }
                }

                // Travelling flag (backing field — avoids VehicleLeft side-effect). Set LAST so the line-draw gate
                // flips only after dests/origin are consistent.
                if (_travelingField != null)
                {
                    try { _travelingField.SetValue(vehicle, meta.Travelling); }
                    catch (Exception ex) { Debug.LogError("[Multipleer][geo] ApplyTravelMeta travelling failed (skipped): " + ex.Message); }
                }
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo] VehicleTravelReflection.ApplyTravelMeta failed: " + ex.Message); return false; }
        }

        /// <summary>Enumerate every live map vehicle (all factions) for the host poll, or null if not in geoscape.</summary>
        public static IEnumerable AllVehicles(GeoRuntime rt)
        {
            Ensure(rt);
            return ResolveVehicles(rt);
        }
    }
}
