using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Reflection bridge for Phoenix base construction / repair / facility completion.
    ///
    /// Verified against the decompile (2026-06-15):
    ///   • construct: <c>GeoPhoenixBase.ConstructFacility(PhoenixFacilityDef, Vector2Int,
    ///                 PhoenixBaseLayoutRotation)</c> (GeoPhoenixBase.cs:230) — NOTE rotation is the
    ///                <c>PhoenixBaseLayoutRotation</c> enum (Rot0..Rot270 = 0..3), not an int.
    ///   • repair:    <c>GeoPhoenixBase.RepairFacility(GeoPhoenixFacility)</c> (:263).
    ///   • complete:  <c>GeoPhoenixFacility.CompleteFacility()</c> (GeoPhoenixFacility.cs:347).
    ///   • remove:    <c>GeoPhoenixBase.RemoveFacility(GeoPhoenixFacility, bool scrap = false)</c> (:277)
    ///                — demolition AND cancel-construction (one chokepoint for both).
    ///   • bases:     <c>GeoPhoenixFaction.Bases</c> (IEnumerable&lt;GeoPhoenixBase&gt;, :194).
    ///   • base id:   <c>GeoPhoenixBase.Site.SiteId</c> (int, GeoSite.cs:45, serialized/stable).
    ///   • layout:    <c>GeoPhoenixBase.Layout</c> (:95) → <c>GeoPhoenixBaseLayout.Facilities</c> (:62) +
    ///                <c>GetFacilityAtPosition(Vector2Int)</c> (:241).
    ///   • facility id: <c>GeoPhoenixFacility.FacilityId</c> (uint, :57, serialized/stable);
    ///                  fallback <c>GridPosition</c> (Vector2Int, :79).
    ///   • facility def: <c>GeoPhoenixFacility.Def</c> (PhoenixFacilityDef, :81); resolved by Guid.
    /// </summary>
    public static class BaseReflection
    {
        private static bool _ready;
        private static Type _baseType;          // GeoPhoenixBase
        private static Type _facilityType;       // GeoPhoenixFacility
        private static Type _facilityDefType;    // PhoenixFacilityDef
        private static Type _rotationType;       // PhoenixBaseLayoutRotation
        private static MethodInfo _construct;    // ConstructFacility(PhoenixFacilityDef, Vector2Int, PhoenixBaseLayoutRotation)
        private static MethodInfo _repair;       // RepairFacility(GeoPhoenixFacility)
        private static MethodInfo _complete;     // GeoPhoenixFacility.CompleteFacility()
        private static MethodInfo _remove;       // RemoveFacility(GeoPhoenixFacility, bool scrap) — demolition/cancel-construction (GeoPhoenixBase.cs:277)
        private static MethodInfo _getFacAtPos;  // GeoPhoenixBaseLayout.GetFacilityAtPosition(Vector2Int)
        private static PropertyInfo _layoutProp;     // GeoPhoenixBase.Layout
        private static PropertyInfo _facilitiesProp; // GeoPhoenixBaseLayout.Facilities
        private static PropertyInfo _siteProp;       // GeoPhoenixBase.Site
        private static FieldInfo _siteIdField;       // GeoSite.SiteId
        private static FieldInfo _facilityIdField;   // GeoPhoenixFacility.FacilityId
        private static PropertyInfo _gridPosProp;    // GeoPhoenixFacility.GridPosition
        private static PropertyInfo _facDefProp;     // GeoPhoenixFacility.Def
        private static PropertyInfo _basesProp;      // GeoPhoenixFaction.Bases

        private static void Ensure()
        {
            if (_ready) return;
            _baseType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase");
            _facilityType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility");
            _facilityDefType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.PhoenixFacilityDef");
            _rotationType = AccessTools.TypeByName("PhoenixPoint.Common.Core.PhoenixBaseLayoutRotation");
            var layoutType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixBaseLayout");
            var siteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (_baseType == null || _facilityType == null || _facilityDefType == null
                || _rotationType == null || layoutType == null || siteType == null) return;

            _construct = AccessTools.Method(_baseType, "ConstructFacility",
                new[] { _facilityDefType, typeof(Vector2Int), _rotationType });
            _repair = AccessTools.Method(_baseType, "RepairFacility", new[] { _facilityType });
            _complete = AccessTools.Method(_facilityType, "CompleteFacility", new Type[0]);
            // NOTE exact param match required (AccessTools.Method): signature is (GeoPhoenixFacility, bool).
            _remove = AccessTools.Method(_baseType, "RemoveFacility", new[] { _facilityType, typeof(bool) });
            _getFacAtPos = AccessTools.Method(layoutType, "GetFacilityAtPosition", new[] { typeof(Vector2Int) });
            _layoutProp = AccessTools.Property(_baseType, "Layout");
            _facilitiesProp = AccessTools.Property(layoutType, "Facilities");
            _siteProp = AccessTools.Property(_baseType, "Site");
            _siteIdField = AccessTools.Field(siteType, "SiteId");
            _facilityIdField = AccessTools.Field(_facilityType, "FacilityId");
            _gridPosProp = AccessTools.Property(_facilityType, "GridPosition");
            _facDefProp = AccessTools.Property(_facilityType, "Def");

            _ready = _construct != null && _repair != null && _complete != null && _remove != null
                     && _layoutProp != null && _facilitiesProp != null && _siteProp != null
                     && _siteIdField != null && _facilityIdField != null;
        }

        // ─── interceptor-side getters ─────────────────────────────────────

        /// <summary>Read the stable base id (<c>Site.SiteId</c>) off a <c>GeoPhoenixBase</c>, as string.</summary>
        public static string GetBaseId(object geoBase)
        {
            if (geoBase == null) return null;
            try
            {
                Ensure();
                var site = _siteProp?.GetValue(geoBase, null);
                if (site == null) return null;
                object idVal = _siteIdField?.GetValue(site);
                return idVal != null ? Convert.ToInt32(idVal).ToString() : null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.GetBaseId failed: " + ex.Message); return null; }
        }

        /// <summary>Read the stable facility id (<c>FacilityId</c>) off a <c>GeoPhoenixFacility</c>, as string.</summary>
        public static string GetFacilityId(object facility)
        {
            if (facility == null) return null;
            try
            {
                Ensure();
                object idVal = _facilityIdField?.GetValue(facility);
                return idVal != null ? Convert.ToUInt32(idVal).ToString() : null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.GetFacilityId failed: " + ex.Message); return null; }
        }

        /// <summary>Read the grid position (<c>GridPosition</c>) off a <c>GeoPhoenixFacility</c>.</summary>
        public static Vector2Int GetGridPosition(object facility)
        {
            if (facility == null) return default(Vector2Int);
            try
            {
                Ensure();
                var v = _gridPosProp?.GetValue(facility, null);
                return v is Vector2Int vi ? vi : default(Vector2Int);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.GetGridPosition failed: " + ex.Message); return default(Vector2Int); }
        }

        /// <summary>Read the facility def GUID off a <c>GeoPhoenixFacility</c>.</summary>
        public static string GetFacilityDefId(object facility)
        {
            if (facility == null) return null;
            try
            {
                Ensure();
                var def = _facDefProp?.GetValue(facility, null);
                return DefReflection.GetGuid(def);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.GetFacilityDefId failed: " + ex.Message); return null; }
        }

        /// <summary>The <c>GeoPhoenixBase</c> that owns a facility, found by scanning faction bases.</summary>
        public static object FindBaseOfFacility(GeoRuntime rt, object facility)
        {
            try
            {
                Ensure();
                if (!_ready || facility == null) return null;
                foreach (var b in EnumerateBases(rt))
                {
                    foreach (var f in EnumerateFacilities(b))
                        if (ReferenceEquals(f, facility)) return b;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.FindBaseOfFacility failed: " + ex.Message); }
            return null;
        }

        // ─── resolvers (Apply side) ───────────────────────────────────────

        private static IEnumerable EnumerateBases(GeoRuntime rt)
        {
            var fac = rt?.PhoenixFaction();
            if (fac == null) yield break;
            if (_basesProp == null || _basesProp.DeclaringType == null
                || !_basesProp.DeclaringType.IsInstanceOfType(fac))
                _basesProp = AccessTools.Property(fac.GetType(), "Bases");
            var bases = _basesProp?.GetValue(fac, null) as IEnumerable;
            if (bases == null) yield break;
            foreach (var b in bases) yield return b;
        }

        private static IEnumerable EnumerateFacilities(object geoBase)
        {
            if (geoBase == null) yield break;
            var layout = _layoutProp?.GetValue(geoBase, null);
            if (layout == null) yield break;
            var facs = _facilitiesProp?.GetValue(layout, null) as IEnumerable;
            if (facs == null) yield break;
            foreach (var f in facs) yield return f;
        }

        /// <summary>Resolve a base by its <c>Site.SiteId</c> string.</summary>
        public static object ResolveBase(GeoRuntime rt, string baseId)
        {
            if (string.IsNullOrEmpty(baseId)) return null;
            try
            {
                Ensure();
                foreach (var b in EnumerateBases(rt))
                    if (GetBaseId(b) == baseId) return b;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.ResolveBase failed: " + ex.Message); }
            return null;
        }

        /// <summary>Resolve a facility within a base by <c>FacilityId</c>, falling back to grid position.</summary>
        public static object ResolveFacility(object geoBase, string facilityId, int gridX, int gridY, bool useGrid)
        {
            try
            {
                Ensure();
                if (geoBase == null) return null;
                if (!string.IsNullOrEmpty(facilityId))
                {
                    foreach (var f in EnumerateFacilities(geoBase))
                        if (GetFacilityId(f) == facilityId) return f;
                }
                if (useGrid && _getFacAtPos != null)
                {
                    var layout = _layoutProp?.GetValue(geoBase, null);
                    if (layout != null)
                        return _getFacAtPos.Invoke(layout, new object[] { new Vector2Int(gridX, gridY) });
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.ResolveFacility failed: " + ex.Message); }
            return null;
        }

        // ─── apply operations ─────────────────────────────────────────────

        public static void Construct(GeoRuntime rt, string baseId, string facilityDefId, int x, int y, int rot)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var geoBase = ResolveBase(rt, baseId);
                if (geoBase == null) return;
                var def = DefReflection.GetDefByGuid(facilityDefId);
                if (def == null) return;
                object rotation = Enum.ToObject(_rotationType, rot);
                _construct.Invoke(geoBase, new object[] { def, new Vector2Int(x, y), rotation });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.Construct failed: " + ex.Message); }
        }

        public static void Repair(GeoRuntime rt, string baseId, string facilityId, int x, int y)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var geoBase = ResolveBase(rt, baseId);
                if (geoBase == null) return;
                var facility = ResolveFacility(geoBase, facilityId, x, y, useGrid: true);
                if (facility == null) return;
                _repair.Invoke(geoBase, new[] { facility });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.Repair failed: " + ex.Message); }
        }

        public static void Complete(GeoRuntime rt, string baseId, string facilityId, int x, int y)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var geoBase = ResolveBase(rt, baseId);
                if (geoBase == null) return;
                var facility = ResolveFacility(geoBase, facilityId, x, y, useGrid: true);
                if (facility == null) return;
                _complete.Invoke(facility, null);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.Complete failed: " + ex.Message); }
        }

        /// <summary>
        /// Demolish/cancel-construction: <c>GeoPhoenixBase.RemoveFacility(facility, scrap)</c>.
        /// Idempotent — a facility already absent (id unmatched + nothing at the grid position)
        /// resolves to null → no-op. A CannotDemolish facility makes the native call throw; that is
        /// swallowed here (host only ever broadcasts after ITS original succeeded, so this can only
        /// trip on a genuinely divergent replayer, where a no-op is the safe outcome).
        /// </summary>
        public static void Remove(GeoRuntime rt, string baseId, string facilityId, int x, int y, bool scrap)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var geoBase = ResolveBase(rt, baseId);
                if (geoBase == null) return;
                var facility = ResolveFacility(geoBase, facilityId, x, y, useGrid: true);
                if (facility == null) return;   // already removed → idempotent no-op
                _remove.Invoke(geoBase, new object[] { facility, scrap });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BaseReflection.Remove failed: " + ex.Message); }
        }
    }
}
