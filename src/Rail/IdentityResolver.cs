using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// The ONLY place in the rail that knows how game objects are NAMED (law 2 stable keys). Everything
    /// else (DiffEngine walk, GenericApplier resolve) is metadata-generic and asks this class three
    /// questions:
    ///   • <see cref="KeyOf"/> — stable key of a collection element (small ID-member pattern table:
    ///     SiteId / VehicleID / ResearchID / Id / Def-GUID; NOT per-subsystem sync code).
    ///   • <see cref="RootRef"/> / <see cref="IsRootEntityType"/> — the 4 root-registry entity kinds
    ///     (faction/site/vehicle/character) that can be referenced ACROSS the tree as one-string keys.
    ///   • <see cref="Roots"/> — the geoscape entry points (level clock + the level's own actor
    ///     registries). This is the entire hand-written root table; the walk below it is generic.
    /// Path grammar (law 2 path addressing): segments joined by '.', each segment = memberName or
    /// memberName#elementKey; the first segment is a root key ("T", "F#&lt;guid&gt;", "S#&lt;id&gt;",
    /// "V#&lt;id&gt;", "U#&lt;id&gt;"). Resolution is symmetric: the client walks its own live graph with
    /// the same keys (<see cref="Resolve"/>).
    /// </summary>
    public static class IdentityResolver
    {
        // ─── ID-member probe table (the one pattern table, checked in order) ───
        private static readonly string[] IdProbes = { "SiteId", "VehicleID", "ResearchID", "Id", "Def" };
        private static readonly Dictionary<Type, Func<object, string>> _keyOfCache = new Dictionary<Type, Func<object, string>>();

        /// <summary>Stable key of an object (for collection-element addressing), or null when none derivable.</summary>
        public static string KeyOf(object o)
        {
            if (o == null) return null;
            var t = o.GetType();
            if (!_keyOfCache.TryGetValue(t, out var f))
            {
                f = BuildKeyOf(t);
                _keyOfCache[t] = f;
            }
            return f?.Invoke(o);
        }

        /// <summary>
        /// Type-level counterpart of <see cref="KeyOf"/>: can elements of this type be addressed by a
        /// stable key AT ALL? Asked once per field by the classifier — a keyable element type rides as
        /// <c>EntityCollection</c> (per-element descend at <c>path.Name#key</c>), a keyless one as
        /// <c>EntityList</c> (the whole list as one canonical blob).
        ///
        /// Deliberately the SAME probe table and the SAME cache as KeyOf, not a mirror of it. 68cd934
        /// shipped a second, independently-written keyability predicate in RailMeta; the two tables then
        /// disagreed about <c>GeoItem</c>, and inventory only worked BECAUSE they disagreed. One table,
        /// asked two ways: "which thing is this" (KeyOf) and "can this kind of thing be named" (here).
        /// </summary>
        public static bool TypeKeyable(Type t)
        {
            if (t == null) return false;
            if (IsRootEntityType(t)) return true;
            if (!_keyOfCache.TryGetValue(t, out var f))
            {
                f = BuildKeyOf(t);
                _keyOfCache[t] = f;
            }
            return f != null;
        }

        private static Func<object, string> BuildKeyOf(Type t)
        {
            foreach (var probe in IdProbes)
            {
                var fi = AccessTools.Field(t, probe);
                var pi = fi == null ? AccessTools.Property(t, probe) : null;
                if (fi == null && (pi == null || !pi.CanRead)) continue;
                Func<object, object> get = fi != null
                    ? (Func<object, object>)(o => fi.GetValue(o))
                    : o => pi.GetValue(o, null);
                return o =>
                {
                    object v;
                    try { v = get(o); } catch { return null; }
                    return FormatKeyValue(v);
                };
            }
            return null;
        }

        private static string FormatKeyValue(object v)
        {
            if (v == null) return null;
            if (v is BaseDef def) return Reserved(def.Guid);
            if (v is string s) return s.Length == 0 ? null : Reserved(s);
            if (v is int i) return i < 0 ? null : i.ToString(CultureInfo.InvariantCulture); // GeoSite.SiteId==-1 = unassigned
            // GeoTacUnitId and friends: implicit int conversion or plain ToString are both stable.
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); }
            catch { return Reserved(v.ToString()); }
        }

        // Path grammar reserves '.' (segment join) and '#' (element-key intro). A key containing either
        // would corrupt addressing → treat as no-key (null): the DiffEngine walk then Incident-excludes the
        // collection (visible in the coverage report), and the client Resolve simply never matches it.
        private static string Reserved(string key) =>
            key == null || key.IndexOf('.') >= 0 || key.IndexOf('#') >= 0 ? null : key;

        // ─── Root-registry entity kinds (cross-tree one-string refs) ───────

        public static bool IsRootEntityType(Type t) =>
            typeof(GeoSite).IsAssignableFrom(t) || typeof(GeoVehicle).IsAssignableFrom(t) ||
            typeof(GeoCharacter).IsAssignableFrom(t) || typeof(GeoFaction).IsAssignableFrom(t);

        /// <summary>One-string root reference ("S#5"), or null when the entity has no valid id yet.</summary>
        public static string RootRef(object o)
        {
            switch (o)
            {
                case null: return null;
                case GeoSite s: return s.SiteId < 0 ? null : "S#" + s.SiteId;
                case GeoVehicle v: return "V#" + v.VehicleID;
                case GeoCharacter c: return "U#" + (int)c.Id;
                case GeoFaction f: return f.Def == null ? null : "F#" + f.Def.Guid;
                default: return null;
            }
        }

        // ─── Roots — the geoscape entry points (the ONE hand-written table) ───

        private static readonly FieldInfo TacUnitsField = AccessTools.Field(typeof(GeoLevelController), "_tacUnits");

        /// <summary>Deterministic (key, object) root pairs: level clock + factions + sites + tac units + vehicles.</summary>
        public static IEnumerable<KeyValuePair<string, object>> Roots(GeoLevelController geo)
        {
            if (geo == null) yield break;
            if (geo.Timing != null) yield return new KeyValuePair<string, object>("T", geo.Timing);

            foreach (var f in geo.Factions.Where(f => f?.Def != null).OrderBy(f => f.Def.Guid, StringComparer.Ordinal))
                yield return new KeyValuePair<string, object>("F#" + f.Def.Guid, f);

            var map = geo.Map;
            if (map != null)
            {
                foreach (var s in map.AllSites.Where(s => s != null && s.SiteId >= 0).OrderBy(s => s.SiteId))
                    yield return new KeyValuePair<string, object>("S#" + s.SiteId, s);
            }

            if (TacUnitsField?.GetValue(geo) is IDictionary tacUnits)
            {
                var chars = new List<GeoCharacter>();
                foreach (var v in tacUnits.Values) if (v is GeoCharacter c) chars.Add(c);
                foreach (var c in chars.OrderBy(c => (int)c.Id))
                    yield return new KeyValuePair<string, object>("U#" + (int)c.Id, c);
            }

            if (map != null)
            {
                foreach (var v in map.Vehicles.Where(v => v != null).OrderBy(v => v.VehicleID))
                    yield return new KeyValuePair<string, object>("V#" + v.VehicleID, v);
            }
        }

        // ─── Client-side path resolution (symmetric to the host walk) ──────

        /// <summary>Resolve a rail path against the live client graph. Null when any hop fails.</summary>
        public static object Resolve(GeoLevelController geo, string path, Dictionary<string, object> cache)
        {
            if (geo == null || string.IsNullOrEmpty(path)) return null;
            if (cache != null && cache.TryGetValue(path, out var hit))
            {
                // Unity fake-null: a destroyed GeoSite/GeoVehicle is ref-nonnull but == null under Unity's
                // overloaded operator. Evict such entries (ReferenceEquals would keep the corpse cached).
                if (!(hit is UnityEngine.Object uo) || uo != null) return hit;
                cache.Remove(path);
            }

            var segments = path.Split('.');
            object cur = ResolveRoot(geo, segments[0]);
            for (int i = 1; cur != null && i < segments.Length; i++)
            {
                var seg = segments[i];
                string key = null;
                int hash = seg.IndexOf('#');
                if (hash >= 0) { key = seg.Substring(hash + 1); seg = seg.Substring(0, hash); }

                var rt = RailType.Get(cur.GetType());
                var field = rt?.FieldByName(seg);
                if (field == null) return null;
                object val;
                try { val = field.GetValue(cur); } catch { return null; }
                if (key == null) { cur = val; continue; }

                cur = null;
                if (val is IEnumerable col)
                {
                    foreach (var elem in col)
                    {
                        if (elem != null && KeyOf(elem) == key) { cur = elem; break; }
                    }
                }
            }
            // A freshly resolved but Unity-destroyed instance (still lingering in a live list) is fake-null
            // too — treat as unresolved, and never cache the corpse.
            if (cur is UnityEngine.Object fresh && fresh == null) return null;
            if (cur != null && cache != null) cache[path] = cur;
            return cur;
        }

        private static object ResolveRoot(GeoLevelController geo, string root)
        {
            if (root == "T") return geo.Timing;
            int hash = root.IndexOf('#');
            if (hash < 0) return null;
            string kind = root.Substring(0, hash), id = root.Substring(hash + 1);
            switch (kind)
            {
                case "F": return geo.Factions.FirstOrDefault(f => f?.Def != null && f.Def.Guid == id);
                case "S": return int.TryParse(id, out var sid) ? geo.Map?.AllSites.FirstOrDefault(s => s != null && s.SiteId == sid) : null;
                case "V": return int.TryParse(id, out var vid) ? geo.Map?.Vehicles.FirstOrDefault(v => v != null && v.VehicleID == vid) : null;
                case "U":
                    if (!int.TryParse(id, out var uid)) return null;
                    if (TacUnitsField?.GetValue(geo) is IDictionary tacUnits)
                        foreach (var v in tacUnits.Values)
                            if (v is GeoCharacter c && (int)c.Id == uid) return c;
                    return null;
                default: return null;
            }
        }
    }
}
