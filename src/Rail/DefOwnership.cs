using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Base.Core;
using Base.Defs;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE WALK-TIME OWNERSHIP LAW (def-aliasing): an instance reachable from BOTH a mirrored live
    /// entity AND the def graph is DEF-OWNED — the rail writes IN PLACE into the live graph (the
    /// game's own serializer never does; its read path builds fresh graphs), so descending-into such
    /// an instance clobbers shared def state on the client. The live mine: ItemDef.GetDisplayName
    /// returns ViewElementDef.DisplayName1/2 BY REFERENCE (decompile ItemDef.cs:165-173), re-exposed
    /// via ManufacturableItem.Name and GeoMission.MissionName; GeoSiteInstaceData.Name/.Motto alias
    /// HavenSettingDbDef binds on a fresh campaign (DiffEngine's N7 probe measures how many).
    ///
    /// NO static signal can carry this law: GeoSiteInstaceData.Name (def-shared, must not write) and
    /// GeoFaction.Wallet (owned `{ get; } = new Wallet()`, MUST write) are indistinguishable by
    /// writability/type — ownership is about who ALLOCATED the instance, so the law is reference
    /// identity at walk time. ONE set: every non-def, non-Unity-asset instance reachable from
    /// DefRepository's def graph, keyed by reference. Consulted by DiffEngine (never ship/descend)
    /// and GenericApplier (refuse write — backstop for version skew).
    ///
    /// Defs are immutable at runtime, so the set builds ONCE (lazily, on the first walk/apply — off
    /// the per-tick hot path; each query after that is one O(1) hash lookup, zero allocation).
    /// Invalidated at the reload boundary only: loading a save can mint runtime defs
    /// (BaseDef.ResolveOrCreateBaseDef, decompile BaseDef.cs:121-135) whose subtrees the old set
    /// never saw. Headless (RailCheck) there is no DefRepository — every query is false, which is
    /// exactly why the harness documents this law as runtime-only (baseline header + L11 belt).
    /// </summary>
    public static class DefOwnership
    {
        private static HashSet<object> _set; // null until built; rebuilt after Invalidate()

        public static bool IsDefOwned(object o)
        {
            if (o == null) return false;
            if (_set == null) _set = Build(); // stays null pre-init/headless — never cache the miss
            return _set != null && _set.Contains(o);
        }

        /// <summary>Reload boundary: a loaded save can create runtime defs whose members alias live
        /// objects — drop the set, the next query rebuilds it over the post-load repository.</summary>
        public static void Invalidate() => _set = null;

        // ─── One-time reference walk of the def graph ───────────────────────

        private static HashSet<object> Build()
        {
            DefRepository repo;
            try { repo = GameUtl.GameComponent<DefRepository>(); }
            catch { return null; }
            if (repo == null) return null;

            var sw = Stopwatch.StartNew();
            var set = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            int defs = 0;
            try
            {
                foreach (var def in repo.GetAllDefs<BaseDef>()) // _guid2Def.Values: asset AND runtime defs
                    if (def != null && set.Add(def)) { stack.Push(def); defs++; }

                while (stack.Count > 0)
                {
                    var o = stack.Pop();
                    var fields = FieldsOf(o.GetType());
                    for (int i = 0; i < fields.Length; i++)
                    {
                        object v;
                        try { v = fields[i].GetValue(o); } catch { continue; }
                        Trace(v, set, stack);
                    }
                    // Collection elements (shared arrays/lists are aliasable too, and their elements —
                    // HavenSettingDbDef.HavenSettings[*].HavenName is the N7 vector). Element-type gate
                    // skips the pathological primitive containers (Dictionary._buckets int[]…).
                    if (o is IEnumerable en && !(o is string))
                    {
                        var et = o is Array ? o.GetType().GetElementType() : RailMeta.ElemTypeOf(o.GetType());
                        if (et == null || Interesting(et))
                            try { foreach (var e in en) Trace(e, set, stack); } catch { /* lazy/throwing enumerable — data collections don't */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][rail] DefOwnership build failed: " + ex);
                return null; // retry next query — a half-built set would silently license clobbers
            }
            Debug.Log("[Multiplayer][rail] DefOwnership: identity set built — defs=" + defs +
                      " ownedObjects=" + set.Count + " in " + sw.ElapsedMilliseconds + "ms");
            return set;
        }

        /// <summary>Classify one reachable value. Reference instances (except strings, Unity assets and
        /// reflection plumbing) enter the SET and the walk; boxed structs never enter the set (box
        /// identity is meaningless) but their reference fields are still walked — a struct can hold the
        /// shared bind.</summary>
        private static void Trace(object v, HashSet<object> set, Stack<object> stack)
        {
            if (v == null) return;
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string || v is decimal) return;
            if (v is Delegate || v is MemberInfo) return; // events/reflection are never rail state
            if (t.IsValueType) { if (StructHasRefs(t)) stack.Push(v); return; }
            // Unity assets (sprites, prefabs, audio…): the rail refuses UnityEngine.Object everywhere
            // already, and descending native-backed objects is neither safe nor useful. BaseDef IS a
            // ScriptableObject but is the graph being walked — exempt.
            if (v is UnityEngine.Object && !(v is BaseDef)) return;
            if (set.Add(v)) stack.Push(v);
        }

        /// <summary>Can a member of this DECLARED type lead to a reference we must record?</summary>
        private static bool Interesting(Type ft)
        {
            if (ft.IsPrimitive || ft.IsEnum || ft.IsPointer) return false;
            if (ft == typeof(string) || ft == typeof(decimal)) return false;
            if (typeof(Delegate).IsAssignableFrom(ft) || typeof(MemberInfo).IsAssignableFrom(ft)) return false;
            if (ft.IsValueType) return StructHasRefs(ft);
            return true; // class / interface / object / array
        }

        private static readonly Dictionary<Type, bool> _structRefs = new Dictionary<Type, bool>();

        /// <summary>Does this struct transitively hold any reference-typed field (beyond string)?
        /// Vector3/Color/TimeUnit… answer no and are skipped wholesale — the bulk of def data.</summary>
        private static bool StructHasRefs(Type t)
        {
            if (_structRefs.TryGetValue(t, out var v)) return v;
            _structRefs[t] = false; // recursion guard (structs cannot cycle, CS0523 — belt only)
            v = false;
            foreach (var fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ft = fi.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft.IsPointer || ft == typeof(string) || ft == typeof(decimal)) continue;
                if (ft.IsValueType) { if (StructHasRefs(ft)) { v = true; break; } continue; }
                v = true; break;
            }
            _structRefs[t] = v;
            return v;
        }

        private static readonly Dictionary<Type, FieldInfo[]> _fields = new Dictionary<Type, FieldInfo[]>();

        private static FieldInfo[] FieldsOf(Type t)
        {
            if (_fields.TryGetValue(t, out var fs)) return fs;
            var list = new List<FieldInfo>();
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                foreach (var fi in cur.GetFields(F))
                    if (Interesting(fi.FieldType))
                        list.Add(fi);
            fs = list.ToArray();
            _fields[t] = fs;
            return fs;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
