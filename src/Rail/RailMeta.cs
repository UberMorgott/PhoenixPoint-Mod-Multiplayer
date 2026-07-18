using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Defs;
using Base.Serialization;
using Base.Serialization.General;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Shared rail metadata layer (law 6): per-type persistent-field tables derived from the game's OWN
    /// save-serializer metadata (<c>Serializer.GetSerializedMembers</c> on the configured serializer —
    /// its field discovery IS our enumerability; TFTV fields ride free), plus the canonical leaf value
    /// codec. Both the host <see cref="DiffEngine"/> and the client <see cref="GenericApplier"/> derive
    /// the SAME table (fields sorted by metadata name, ordinal) so a u16 field index is stable on the wire.
    ///
    /// Two generic sources per type, NO subsystem knowledge anywhere:
    ///   • direct — the type itself has serialized members ([SerializeType]/[SerializeMember]).
    ///   • bridge — the type persists through a sibling <c>*InstanceData</c> DTO (GeoFaction, GeoVehicle,
    ///     GeoSite ("GeoSiteInstaceData" — the game's own typo), Timing…): the DTO's metadata provides the
    ///     member NAMES, each resolved onto the LIVE type by same-name (then unique-same-class-type)
    ///     match. Unresolvable/read-only members are EXCLUDED and visible in the coverage report —
    ///     the opt-out guarantee replacing discover-by-bug.
    /// </summary>
    public enum FieldClass : byte
    {
        Leaf = 0,            // plain value → snapshot leaf
        Descend = 1,         // keyless object → child entity at path.Name
        EntityCollection = 2,// collection of keyable entities → descend per element at path.Name#key
        LeafList = 3,        // collection of leaf-encodable elements → ONE canonical list value
        LeafDict = 4,        // dictionary with simple keys → per-subKey leaf entries
        Excluded = 5,        // not on the rail; reason in RailField.Exclude, surfaced by the report
        GeoItemDict = 6,     // Dictionary<BaseDef, GeoItem> (ItemStorage._storageItems) → per-def structural entry (GeoItemCodec)
        EntityList = 7       // list of KEYLESS entities → the WHOLE list is ONE canonical value blob; element
                             // order rides INSIDE the payload, never in the path (law 2 forbids index addresses)
    }

    public enum LeafKind : byte
    {
        Null = 0, Bool = 1, Int64 = 2, UInt64 = 3, Single = 4, Double = 5, String = 6, Enum = 7,
        TimeSpanTicks = 8, Vector3 = 9, Quaternion = 10, DefRef = 11, EntityRef = 12, Composite = 13
    }

    public sealed class RailField
    {
        public string Name;          // metadata (bridge) name — wire identity
        public Type ValueType;       // declared type used by the codec (identical on both peers)
        public FieldClass Class;
        public LeafKind Leaf;        // when Class == Leaf
        public Type ElemType;        // LeafList / EntityCollection element type
        public bool Unordered;       // LeafList canonicalization (HashSet semantics → sort)
        public Type KeyType;         // LeafDict key type
        public Type DictValType;     // LeafDict value type
        public string Exclude;       // reason when Class == Excluded
        public string LiveAlias;     // set when bridge resolution matched by unique type, not name

        internal FieldInfo Fi;
        internal PropertyInfo Pi;

        public bool CanRead => Fi != null || (Pi != null && Pi.CanRead);

        public object GetValue(object o) => Fi != null ? Fi.GetValue(o) : Pi.GetValue(o, null);

        public void SetValue(object o, object v)
        {
            if (Fi != null) Fi.SetValue(o, v);
            else Pi.SetValue(o, v, null);
        }

        internal bool IsWritable()
        {
            if (Fi != null) return !Fi.IsInitOnly;
            return Pi != null && Pi.GetSetMethod(true) != null;
        }
    }

    public sealed class RailType
    {
        public Type Type;
        public string Source;                 // "direct" | "bridge:<DtoName>" | "none"
        public List<RailField> Fields;        // sorted by Name (ordinal) — u16 index = wire field id
        private Dictionary<string, RailField> _byName;

        public RailField FieldByName(string name)
        {
            RailField f = null;
            _byName?.TryGetValue(name, out f);
            return f;
        }

        public int CoveredCount => Fields.Count(f => f.Class != FieldClass.Excluded);

        private static readonly Dictionary<Type, RailType> Cache = new Dictionary<Type, RailType>();

        public static void ClearCache() => Cache.Clear();

        public static RailType Get(Type t)
        {
            if (t == null) return null;
            if (Cache.TryGetValue(t, out var rt)) return rt;
            var ser = RailMeta.GameSerializer;
            if (ser == null) return null; // not in game yet — do NOT cache the miss
            rt = Build(t, ser);
            Cache[t] = rt;
            return rt;
        }

        private static RailType Build(Type t, Serializer ser)
        {
            var rt = new RailType { Type = t, Fields = new List<RailField>(), _byName = new Dictionary<string, RailField>(StringComparer.Ordinal) };
            var raw = new List<(string name, Type valType, MemberInfo live, string alias, string fail)>();

            var direct = RailMeta.SerializedMembers(ser, t);
            if (direct.Count > 0)
            {
                rt.Source = "direct";
                foreach (var mi in direct)
                    raw.Add((mi.Name, RailMeta.MemberType(mi), mi, null, null));
            }
            else
            {
                var bridge = RailMeta.FindBridge(t);
                if (bridge == null)
                {
                    rt.Source = "none";
                    return rt;
                }
                rt.Source = "bridge:" + bridge.Name;
                foreach (var dtoMi in RailMeta.SerializedMembers(ser, bridge))
                {
                    var valType = RailMeta.MemberType(dtoMi);
                    var live = RailMeta.ResolveLive(t, dtoMi.Name, valType, out var alias);
                    raw.Add((dtoMi.Name, valType, live, alias, live == null ? "bridge-unresolved" : null));
                }
            }

            foreach (var e in raw.OrderBy(e => e.name, StringComparer.Ordinal))
            {
                if (rt._byName.ContainsKey(e.name)) continue; // hierarchy duplicate — first wins
                var f = BuildField(e.name, e.valType, e.live, e.alias, e.fail);
                rt.Fields.Add(f);
                rt._byName[e.name] = f;
            }
            return rt;
        }

        private static RailField BuildField(string name, Type valType, MemberInfo live, string alias, string fail)
        {
            var f = new RailField { Name = name, ValueType = valType, LiveAlias = alias, Fi = live as FieldInfo, Pi = live as PropertyInfo };
            if (fail != null || live == null) { f.Class = FieldClass.Excluded; f.Exclude = fail ?? "no live member"; return f; }
            if (!f.CanRead) { f.Class = FieldClass.Excluded; f.Exclude = "unreadable"; return f; }
            var optOut = RailMeta.OptOutReason(live.DeclaringType, name);
            if (optOut != null) { f.Class = FieldClass.Excluded; f.Exclude = optOut; return f; }

            // Leaf?
            if (RailMeta.LeafKindOf(valType, 0, out var kind))
            {
                if (!f.IsWritable()) { f.Class = FieldClass.Excluded; f.Exclude = "read-only"; return f; }
                f.Class = FieldClass.Leaf; f.Leaf = kind; return f;
            }

            // Dictionary?
            var dictArgs = RailMeta.GenericInterfaceArgs(valType, typeof(IDictionary<,>));
            if (dictArgs != null)
            {
                if (RailMeta.IsSimpleKey(dictArgs[0]) && RailMeta.LeafKindOf(dictArgs[1], 0, out _))
                { f.Class = FieldClass.LeafDict; f.KeyType = dictArgs[0]; f.DictValType = dictArgs[1]; return f; }
                if (GeoItemCodec.Handles(dictArgs[0], dictArgs[1]))
                { f.Class = FieldClass.GeoItemDict; f.KeyType = dictArgs[0]; f.DictValType = dictArgs[1]; return f; }
                f.Class = FieldClass.Excluded; f.Exclude = "dictionary with non-simple key/value (" + dictArgs[0].Name + "," + dictArgs[1].Name + ")";
                return f;
            }

            // Collection?
            var elem = RailMeta.ElemTypeOf(valType);
            if (elem != null)
            {
                if (RailMeta.LeafKindOf(elem, 0, out _))
                {
                    f.Class = FieldClass.LeafList; f.ElemType = elem;
                    f.Unordered = valType.IsGenericType && valType.GetGenericTypeDefinition() == typeof(HashSet<>);
                    if (!f.IsWritable() && valType.IsArray) { f.Class = FieldClass.Excluded; f.Exclude = "read-only array"; }
                    return f;
                }
                if (elem.IsClass && RailMeta.HasPersistentMembers(elem))
                {
                    // Keyable element type → per-element descend at path.Name#key. Keyless (GeoItem,
                    // AbilityTrack…) → the whole list is ONE canonical value blob (EntityList); the walk
                    // additionally falls back per instance when a keyable-looking list turns out
                    // unkeyable/duplicate at runtime (DiffEngine.EntityCollection case).
                    //
                    // Keyability is asked of IdentityResolver — THE identity table — never of a second
                    // predicate living here (see IdentityResolver.TypeKeyable for why that mattered).
                    f.ElemType = elem;
                    if (IdentityResolver.TypeKeyable(elem)) { f.Class = FieldClass.EntityCollection; return f; }

                    // N4 — rebuildability is decided HERE, at classify time, not consulted at ship time.
                    // EntityList is applied by ApplyList, whose only strategy for an ARRAY container is
                    // "allocate a new array and assign it" — impossible on a read-only field, so it would
                    // reach the final `throw new InvalidOperationException("no list apply strategy")` on
                    // every single apply. Same shape as the LeafList guard six lines up. Deciding it here
                    // means DiffEngine needs zero knowledge of it, and a field changing state shows up as
                    // a reviewable diff in docs/rail-baseline.txt instead of as a silent runtime throw.
                    // Concrete case: GeoPhoenixFacility._components (GeoPhoenixFacility.cs:48, readonly array).
                    if (!f.IsWritable() && valType.IsArray)
                    { f.Class = FieldClass.Excluded; f.Exclude = "read-only array container"; return f; }

                    f.Class = FieldClass.EntityList;
                    return f;
                }
                f.Class = FieldClass.Excluded; f.Exclude = "collection of un-keyable/unsupported " + elem.Name;
                return f;
            }

            // Plain object ref?
            if (valType == typeof(object) || valType.IsInterface)
            { f.Class = FieldClass.Excluded; f.Exclude = "untyped/interface member"; return f; }
            if (typeof(UnityEngine.Object).IsAssignableFrom(valType))
            { f.Class = FieldClass.Excluded; f.Exclude = "Unity scene object"; return f; }
            if (valType.IsClass && RailMeta.HasPersistentMembers(valType))
            { f.Class = FieldClass.Descend; return f; }

            f.Class = FieldClass.Excluded; f.Exclude = "no persistent members (" + valType.Name + ")";
            return f;
        }
    }

    public static class RailMeta
    {
        // ─── Game serializer access (typed; the ONE configured instance) ───

        /// <summary>Headless seam for tools/RailCheck ONLY — the game component does not exist outside the
        /// game process, but the serializer's METADATA discovery (GetSerializedMembers) is pure attribute
        /// reflection and works on a bare <c>new Serializer(null)</c>. Always null in game.</summary>
        internal static Serializer SerializerOverride;

        public static Serializer GameSerializer
        {
            get
            {
                if (SerializerOverride != null) return SerializerOverride;
                try { return GameUtl.GameComponent<SerializationComponent>()?.Serializer; }
                catch { return null; }
            }
        }

        internal static Type MemberType(MemberInfo mi) => (mi as FieldInfo)?.FieldType ?? ((PropertyInfo)mi).PropertyType;

        // ─── Explicit member opt-out ───────────────────────────────────────
        // Same principle as the presentation refusal above, one granularity down (PRIME DIRECTIVE: mirror
        // everything by default, then OPT OUT what we don't want). An entry earns its place only with a
        // reason about the VALUE's own nature — never a subsystem's convenience — and it is visible in the
        // coverage report and the RailCheck baseline like any other exclusion.
        private static readonly Dictionary<string, string> _optOutMembers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Timing.Now = StartTime + OwnNow (decompile Base.Core/Timing.cs:55) where OwnNow accrues from
            // LOCAL realtime on every peer — the client's sim is deliberately unfrozen (see ClientSimGate).
            // So mirroring the host's raw base on top of the client's own accrual double-counts: the two
            // clocks keep the same pace and never agree on the value.
            //
            // THIS EXCLUSION IS LOAD-BEARING RIGHT NOW, not documentation: before TimeUnit became a leaf
            // (this same commit) both members fell out as "no persistent members", so they never rode. The
            // leaf change would ADMIT them, i.e. start mirroring the host's raw clock base — the exact
            // double-count above. The opt-out keeps today's behavior and states the reason for it.
            //
            // The reason strings name the intended replacement, the TimeAnchor "TA" root, which is NOT in
            // the tree yet — it lands in batch 7 (37af665). Until then the clock VALUE simply does not
            // mirror; batch 1's N3 already closed the pause/speed LATENCY, which is a different thing.
            { "Base.Core.Timing.StartTime", "clock base — rides as the TimeAnchor \"TA\" root (a raw mirror double-counts local accrual)" },
            { "Base.Core.Timing.StartFixedTime", "clock base — rides as the TimeAnchor \"TA\" root (a raw mirror double-counts local accrual)" },
        };

        /// <summary>Exclusion reason for an explicitly opted-out member, or null when it rides normally.</summary>
        internal static string OptOutReason(Type owner, string name)
        {
            if (owner == null) return null;
            _optOutMembers.TryGetValue(owner.FullName + "." + name, out var why);
            return why;
        }

        /// <summary>Persistent members of a type per the game's own discovery (ReadWrite mode only).</summary>
        internal static List<MemberInfo> SerializedMembers(Serializer ser, Type t)
        {
            var result = new List<MemberInfo>();
            try
            {
                foreach (var mwa in ser.GetSerializedMembers(t))
                {
                    var mode = mwa.MemberAttr?.SerializeMode ?? SerializeMode.ReadWrite;
                    if (mode != SerializeMode.ReadWrite) continue; // WriteOnly = create param, DoNotSerialize = opted out
                    if (mwa.MemberInfo != null) result.Add(mwa.MemberInfo);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] SerializedMembers(" + t.Name + ") failed: " + ex.Message); }
            return result;
        }

        private static readonly Dictionary<Type, bool> _hasMembersCache = new Dictionary<Type, bool>();

        internal static bool HasPersistentMembers(Type t)
        {
            if (_hasMembersCache.TryGetValue(t, out var v)) return v;
            var ser = GameSerializer;
            if (ser == null) return false; // don't cache pre-init misses
            v = SerializedMembers(ser, t).Count > 0 || FindBridge(t) != null;
            _hasMembersCache[t] = v;
            return v;
        }

        // ─── InstanceData bridge discovery (generic: name pattern, walks base types) ───
        internal static Type FindBridge(Type t)
        {
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var dto = cur.Assembly.GetType(cur.FullName + "InstanceData")
                          ?? cur.Assembly.GetType(cur.FullName + "InstaceData"); // game typo: GeoSiteInstaceData
                if (dto != null && dto != t) return dto;
            }
            return null;
        }

        /// <summary>Resolve a bridge member name onto the live type: same name first, then the UNIQUE live
        /// member of the identical class type (catches renames like ManufactureQueue → Manufacture).</summary>
        internal static MemberInfo ResolveLive(Type live, string name, Type valType, out string alias)
        {
            alias = null;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fi = HarmonyLib.AccessTools.Field(live, name);
            if (fi != null && fi.FieldType == valType) return fi;
            var pi = HarmonyLib.AccessTools.Property(live, name);
            if (pi != null && pi.PropertyType == valType) return pi;

            if (!valType.IsClass || valType == typeof(string)) return null; // unique-type fallback: class refs only
            MemberInfo unique = null;
            for (var cur = live; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var m in cur.GetFields(F | BindingFlags.DeclaredOnly).Cast<MemberInfo>()
                                     .Concat(cur.GetProperties(F | BindingFlags.DeclaredOnly)))
                {
                    if (m.Name[0] == '<') continue; // compiler backing field
                    if (MemberType(m) != valType) continue;
                    if (unique != null && unique.Name != m.Name) return null; // ambiguous
                    unique = unique ?? m;
                }
            }
            if (unique != null) alias = unique.Name;
            return unique;
        }

        // ─── Type shape helpers ─────────────────────────────────────────────
        internal static Type[] GenericInterfaceArgs(Type t, Type genericDef)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == genericDef) return t.GetGenericArguments();
            foreach (var i in t.GetInterfaces())
                if (i.IsGenericType && i.GetGenericTypeDefinition() == genericDef) return i.GetGenericArguments();
            return null;
        }

        internal static Type ElemTypeOf(Type t)
        {
            if (t == typeof(string)) return null;
            if (t.IsArray) return t.GetElementType();
            var args = GenericInterfaceArgs(t, typeof(IEnumerable<>));
            return args?[0];
        }

        internal static bool IsSimpleKey(Type t) =>
            t.IsEnum || t == typeof(int) || t == typeof(long) || t == typeof(string);

        /// <summary>Leaf-encodable check + kind. Depth caps composite recursion (structs of structs).</summary>
        internal static bool LeafKindOf(Type t, int depth, out LeafKind kind)
        {
            kind = LeafKind.Null;
            if (t == typeof(bool)) { kind = LeafKind.Bool; return true; }
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
                t == typeof(int) || t == typeof(long) || t == typeof(char)) { kind = LeafKind.Int64; return true; }
            if (t == typeof(uint) || t == typeof(ulong)) { kind = LeafKind.UInt64; return true; }
            if (t == typeof(float)) { kind = LeafKind.Single; return true; }
            if (t == typeof(double)) { kind = LeafKind.Double; return true; }
            if (t == typeof(string)) { kind = LeafKind.String; return true; }
            if (t.IsEnum) { kind = LeafKind.Enum; return true; }
            // TimeUnit rides BESIDE TimeSpan rather than as its own kind: it is a struct wrapping ONE
            // readonly TimeSpan (decompile Base.Core/TimeUnit.cs:16-17), so BuildField excludes that member
            // as read-only, the Composite gate below then fails (no Leaf field left) and every TimeUnit in
            // the game fell out as "no persistent members" — research/travel/manufacture ETAs, site timers.
            // Ticks are the entire value, so the existing TimeSpanTicks codec already expresses it exactly.
            if (t == typeof(TimeSpan) || t == typeof(TimeUnit)) { kind = LeafKind.TimeSpanTicks; return true; }
            if (t == typeof(Vector3)) { kind = LeafKind.Vector3; return true; }
            if (t == typeof(Quaternion)) { kind = LeafKind.Quaternion; return true; }
            if (typeof(BaseDef).IsAssignableFrom(t)) { kind = LeafKind.DefRef; return true; }
            if (IdentityResolver.IsRootEntityType(t)) { kind = LeafKind.EntityRef; return true; }
            if (t.IsValueType && !t.IsPrimitive && depth < 3)
            {
                var rt = RailType.Get(t);
                if (rt != null && rt.Fields.Count > 0 &&
                    rt.Fields.All(f => f.Class == FieldClass.Leaf))
                { kind = LeafKind.Composite; return true; }
            }
            return false;
        }

        // ─── Canonical leaf codec ───────────────────────────────────────────

        public static byte[] EncodeFieldValue(RailField f, object v)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                if (f.Class == FieldClass.LeafList) EncodeList(w, f, v);
                else EncodeLeaf(w, f.Class == FieldClass.LeafDict ? f.DictValType : f.ValueType, v);
                return ms.ToArray();
            }
        }

        public static void EncodeLeaf(BinaryWriter w, Type declared, object v)
        {
            if (v == null) { w.Write((byte)LeafKind.Null); return; }
            LeafKindOf(declared, 0, out var kind);
            if (kind == LeafKind.EntityRef)
            {
                var key = IdentityResolver.RootRef(v);
                if (key == null) { w.Write((byte)LeafKind.Null); return; }
                w.Write((byte)kind); w.Write(key); return;
            }
            w.Write((byte)kind);
            switch (kind)
            {
                case LeafKind.Bool: w.Write((bool)v); break;
                case LeafKind.Int64: w.Write(Convert.ToInt64(v, CultureInfo.InvariantCulture)); break;
                case LeafKind.UInt64: w.Write(Convert.ToUInt64(v, CultureInfo.InvariantCulture)); break;
                case LeafKind.Single: w.Write((float)v); break;
                case LeafKind.Double: w.Write((double)v); break;
                case LeafKind.String: w.Write((string)v); break;
                case LeafKind.Enum: w.Write(Convert.ToInt64(v, CultureInfo.InvariantCulture)); break;
                case LeafKind.TimeSpanTicks: w.Write((v is TimeUnit tu ? tu.TimeSpan : (TimeSpan)v).Ticks); break;
                case LeafKind.Vector3: { var x = (Vector3)v; w.Write(x.x); w.Write(x.y); w.Write(x.z); break; }
                case LeafKind.Quaternion: { var q = (Quaternion)v; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); break; }
                case LeafKind.DefRef: w.Write(((BaseDef)v).Guid ?? ""); break;
                case LeafKind.Composite:
                {
                    var rt = RailType.Get(declared);
                    w.Write((byte)rt.Fields.Count);
                    for (int i = 0; i < rt.Fields.Count; i++)
                    {
                        w.Write((ushort)i);
                        EncodeLeaf(w, rt.Fields[i].ValueType, rt.Fields[i].GetValue(v));
                    }
                    break;
                }
                default: throw new InvalidOperationException("unencodable leaf " + declared.Name);
            }
        }

        private static void EncodeList(BinaryWriter w, RailField f, object v)
        {
            if (v == null) { w.Write((byte)LeafKind.Null); return; }
            w.Write(ListMarker); // distinct from every LeafKind
            var items = new List<byte[]>();
            foreach (var e in (IEnumerable)v)
            {
                using (var ms = new MemoryStream())
                using (var iw = new BinaryWriter(ms, System.Text.Encoding.UTF8))
                {
                    EncodeLeaf(iw, f.ElemType, e);
                    items.Add(ms.ToArray());
                }
            }
            if (f.Unordered) items.Sort(CompareBytes); // canonical: HashSet iteration order is nondeterministic
            if (items.Count > ushort.MaxValue && _loggedTruncations.Add(f.Name))
                Debug.LogWarning("[Multiplayer][rail] EncodeList: '" + f.Name + "' has " + items.Count +
                                 " elements — wire caps at " + ushort.MaxValue + "; tail dropped (this field will desync)");
            w.Write((ushort)Math.Min(items.Count, ushort.MaxValue));
            for (int i = 0; i < items.Count && i < ushort.MaxValue; i++) w.Write(items[i]);
        }

        public static object DecodeFieldValue(byte[] bytes, RailField f, GeoLevelController geo, out bool isNull)
        {
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8))
            {
                if (f.Class == FieldClass.LeafList) { var list = DecodeList(r, f, geo); isNull = list == null; return list; }
                var v = DecodeLeaf(r, f.Class == FieldClass.LeafDict ? f.DictValType : f.ValueType, geo);
                isNull = v == null;
                return v;
            }
        }

        public static object DecodeLeaf(BinaryReader r, Type declared, GeoLevelController geo)
        {
            var kind = (LeafKind)r.ReadByte();
            switch (kind)
            {
                case LeafKind.Null: return null;
                case LeafKind.Bool: return Coerce(r.ReadBoolean(), declared);
                case LeafKind.Int64: return Coerce(r.ReadInt64(), declared);
                case LeafKind.UInt64: return Coerce(r.ReadUInt64(), declared);
                case LeafKind.Single: return Coerce(r.ReadSingle(), declared);
                case LeafKind.Double: return Coerce(r.ReadDouble(), declared);
                case LeafKind.String: return r.ReadString();
                case LeafKind.Enum: return Enum.ToObject(declared, r.ReadInt64());
                case LeafKind.TimeSpanTicks:
                {
                    // Keyed on the DECLARED type (both peers derive it from the same table), because the
                    // wire form is identical for TimeSpan and TimeUnit — ticks.
                    var ts = new TimeSpan(r.ReadInt64());
                    return declared == typeof(TimeUnit) ? (object)TimeUnit.FromTimeSpan(ts) : ts;
                }
                case LeafKind.Vector3: return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                case LeafKind.Quaternion: return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                case LeafKind.DefRef:
                {
                    var guid = r.ReadString();
                    var def = GameUtl.GameComponent<DefRepository>()?.GetDef(guid);
                    if (def == null) Debug.LogWarning("[Multiplayer][rail] DecodeLeaf: unknown def guid " + guid);
                    return def;
                }
                case LeafKind.EntityRef:
                {
                    var key = r.ReadString();
                    var e = IdentityResolver.Resolve(geo, key, null);
                    if (e == null) Debug.LogWarning("[Multiplayer][rail] DecodeLeaf: unresolved entity ref " + key);
                    return e;
                }
                case LeafKind.Composite:
                {
                    var rt = RailType.Get(declared);
                    var box = Activator.CreateInstance(declared);
                    int n = r.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        int idx = r.ReadUInt16();
                        var field = idx < rt.Fields.Count ? rt.Fields[idx] : null;
                        var v = DecodeLeaf(r, field?.ValueType ?? typeof(object), geo);
                        field?.SetValue(box, v);
                    }
                    return box;
                }
                default: throw new IOException("bad leaf kind " + kind);
            }
        }

        private const byte ListMarker = 14;

        /// <summary>LeafDict subkey deletion sentinel. A first byte that is never a valid encoded leaf
        /// (LeafKinds 0-13, list marker 14), so an explicit dict-key delete stays distinguishable on the
        /// wire from a genuine present-null value (LeafKind.Null = 0).</summary>
        public const byte DictTombstone = 0xFF;

        private static readonly HashSet<string> _loggedTruncations = new HashSet<string>(StringComparer.Ordinal);

        private static List<object> DecodeList(BinaryReader r, RailField f, GeoLevelController geo)
        {
            var marker = r.ReadByte();
            if (marker == (byte)LeafKind.Null) return null;
            int n = r.ReadUInt16();
            var list = new List<object>(n);
            for (int i = 0; i < n; i++) list.Add(DecodeLeaf(r, f.ElemType, geo));
            return list;
        }

        private static object Coerce(object v, Type declared)
        {
            if (declared.IsInstanceOfType(v)) return v;
            if (declared.IsEnum) return Enum.ToObject(declared, Convert.ToInt64(v, CultureInfo.InvariantCulture));
            try { return Convert.ChangeType(v, declared, CultureInfo.InvariantCulture); }
            catch { return v; }
        }

        internal static int CompareBytes(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) { int c = a[i].CompareTo(b[i]); if (c != 0) return c; }
            return a.Length.CompareTo(b.Length);
        }

        internal static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ─── Dictionary subKey codec (LeafDict) ────────────────────────────
        internal static string EncodeDictKey(object k) =>
            k is string s ? s : Convert.ToInt64(k, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

        internal static object DecodeDictKey(string s, Type keyType)
        {
            if (keyType == typeof(string)) return s;
            var l = long.Parse(s, CultureInfo.InvariantCulture);
            if (keyType.IsEnum) return Enum.ToObject(keyType, l);
            return Convert.ChangeType(l, keyType, CultureInfo.InvariantCulture);
        }

        // ─── EntityList: keyless-entity list codec ─────────────────────────
        // The whole list travels as ONE canonical value blob; element order lives inside the payload
        // (law 2: a path may never contain an element index). Elements are reconstructed the way the
        // game's OWN serializer does it: the type's [SerializeCustomCreate] static method, whose
        // parameters (after SerializedObjectData) are serialized members matched BY NAME — that is how
        // WriteOnly "create param" members (GeoItem.ItemDef) travel: as constructor arguments
        // (SerializationType.InitCustomCreate/CustomCreateObject). No custom create → the game uses
        // Activator.CreateInstance(nonPublic) — so do we. Post-read callbacks are fired afterwards
        // (AbilityTrack rebinds its slot back-refs there).

        internal const byte EntityListMarker = 15; // distinct from LeafKinds 0-13 and ListMarker 14

        private const byte TagNull = 0, TagLeaf = 1, TagBlob = 2, TagBackRef = 3, TagList = 4, TagLeafList = 5;
        private const int MaxBlobDepth = 8;

        private struct Fixup { public object Target; public RailField Field; public int Idx; }

        private sealed class CreateInfo { public MethodDelegate Method; public MemberInfo[] Params; }
        private static readonly Dictionary<Type, CreateInfo> _createInfoCache = new Dictionary<Type, CreateInfo>();
        private static readonly Dictionary<Type, bool> _blobContentCache = new Dictionary<Type, bool>();
        private static readonly HashSet<string> _blobWarned = new HashSet<string>(StringComparer.Ordinal);

        private static void WarnOnce(string msg)
        {
            if (_blobWarned.Count < 200 && _blobWarned.Add(msg))
                Debug.LogWarning("[Multiplayer][rail] " + msg);
        }

        /// <summary>Custom-create contract of a type per the game's serializer, or null when it has none.
        /// A null slot in Params means a parameter name matched no serialized member → not reconstructible.</summary>
        private static CreateInfo CreateInfoOf(Serializer ser, Type t)
        {
            if (_createInfoCache.TryGetValue(t, out var ci)) return ci;
            var md = ser.GetTypeCustomCreateMethod(t, out _);
            if (md?.Method != null)
            {
                var byName = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
                foreach (var mwa in ser.GetSerializedMembers(t)) // ALL modes — WriteOnly create params included
                    if (mwa.MemberInfo != null && !byName.ContainsKey(mwa.MemberInfo.Name))
                        byName.Add(mwa.MemberInfo.Name, mwa.MemberInfo);
                var ps = Serializer.CustomCreateParameterNames(md.Method)
                                   .Select(n => byName.TryGetValue(n, out var mi) ? mi : null).ToArray();
                ci = new CreateInfo { Method = md, Params = ps };
            }
            _createInfoCache[t] = ci;
            return ci;
        }

        private static object GetMemberValue(MemberInfo mi, object o) =>
            mi is FieldInfo fi ? fi.GetValue(o) : ((PropertyInfo)mi).GetValue(o, null);

        /// <summary>True when a blob of this type would carry anything (covered fields, salvageable
        /// read-only leaf fields, or create params). Contentless sub-objects (AmmoManager: only
        /// interface-typed members) are dropped rather than reconstructed empty.</summary>
        private static bool HasBlobContent(Serializer ser, Type t)
        {
            if (_blobContentCache.TryGetValue(t, out var v)) return v;
            v = CreateInfoOf(ser, t) != null;
            var rt = RailType.Get(t);
            if (!v && rt != null)
                foreach (var f in rt.Fields)
                    if (f.Class == FieldClass.Leaf || f.Class == FieldClass.LeafList || f.Class == FieldClass.Descend ||
                        f.Class == FieldClass.EntityCollection || f.Class == FieldClass.EntityList ||
                        (f.Class == FieldClass.Excluded && f.Fi != null && f.Fi.IsInitOnly && LeafKindOf(f.ValueType, 0, out _)))
                    { v = true; break; }
            _blobContentCache[t] = v;
            return v;
        }

        public static byte[] EncodeEntityList(RailField f, object listVal)
        {
            var ser = GameSerializer ?? throw new InvalidOperationException("no game serializer");
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                w.Write(EntityListMarker);
                if (listVal == null) { w.Write(false); return ms.ToArray(); }
                w.Write(true);
                var items = new List<object>();
                foreach (var e in (IEnumerable)listVal) items.Add(e);
                if (items.Count > ushort.MaxValue) throw new InvalidOperationException("entity list too large (" + items.Count + ")");
                w.Write((ushort)items.Count);
                foreach (var e in items)
                    EncodeValue(w, ser, f.ElemType, e, new List<object>(), 0); // locals scope = one element
                return ms.ToArray();
            }
        }

        private static void EncodeValue(BinaryWriter w, Serializer ser, Type declared, object v, List<object> locals, int depth)
        {
            if (v == null) { w.Write(TagNull); return; }
            if (LeafKindOf(declared, 0, out _)) { w.Write(TagLeaf); EncodeLeaf(w, declared, v); return; }
            for (int i = 0; i < locals.Count; i++)
                if (ReferenceEquals(locals[i], v)) { w.Write(TagBackRef); w.Write((ushort)i); return; }
            var t = v.GetType();
            if (t != declared) throw new NotSupportedException("polymorphic value " + t.Name + " as " + declared.Name);
            if (depth >= MaxBlobDepth) throw new NotSupportedException("blob depth cap at " + t.Name);
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) throw new NotSupportedException("Unity object " + t.Name);
            if (!HasBlobContent(ser, t))
            {
                // Nothing expressible inside (e.g. AmmoManager: interface members only) — drop rather than
                // reconstruct an EMPTY husk that would shadow saner defaults (Ammo=null → _charges fallback).
                WarnOnce("blob: " + t.Name + " has no encodable content — dropped");
                w.Write(TagNull);
                return;
            }
            w.Write(TagBlob);
            EncodeObjectBody(w, ser, t, v, locals, depth);
        }

        private static void EncodeObjectBody(BinaryWriter w, Serializer ser, Type t, object o, List<object> locals, int depth)
        {
            locals.Add(o); // registration order is the deterministic back-ref index space (mirrored on decode)

            var ci = CreateInfoOf(ser, t);
            if (ci != null)
            {
                foreach (var p in ci.Params)
                    if (p == null) throw new NotSupportedException(t.Name + " create param unmatched");
                w.Write((byte)ci.Params.Length);
                foreach (var mi in ci.Params)
                    EncodeValue(w, ser, MemberType(mi), GetMemberValue(mi, o), locals, depth + 1);
            }
            else w.Write((byte)0);

            var rt = RailType.Get(t);
            using (var fms = new MemoryStream())
            using (var fw = new BinaryWriter(fms, System.Text.Encoding.UTF8))
            {
                int n = 0;
                for (int i = 0; i < rt.Fields.Count; i++)
                {
                    var f = rt.Fields[i];
                    if (!f.CanRead) continue;
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    switch (f.Class)
                    {
                        case FieldClass.Leaf:
                            fw.Write((ushort)i); fw.Write(TagLeaf); EncodeLeaf(fw, f.ValueType, v); n++;
                            break;
                        case FieldClass.LeafList:
                            fw.Write((ushort)i); fw.Write(TagLeafList); EncodeList(fw, f, v); n++;
                            break;
                        case FieldClass.Descend:
                            fw.Write((ushort)i); EncodeValue(fw, ser, f.ValueType, v, locals, depth + 1); n++;
                            break;
                        case FieldClass.EntityCollection:
                        case FieldClass.EntityList:
                        {
                            if (IdentityResolver.IsRootEntityType(f.ElemType))
                            { WarnOnce("blob: root-entity list " + t.Name + "." + f.Name + " not carried (identity creation is structural, law 3)"); break; }
                            fw.Write((ushort)i);
                            if (v == null) fw.Write(TagNull);
                            else
                            {
                                var elems = new List<object>();
                                foreach (var e in (IEnumerable)v) elems.Add(e);
                                fw.Write(TagList); fw.Write((ushort)elems.Count);
                                foreach (var e in elems) EncodeValue(fw, ser, f.ElemType, e, locals, depth + 1);
                            }
                            n++;
                            break;
                        }
                        case FieldClass.Excluded:
                        {
                            // ponytail: this is the ONE place where classification is re-decided outside
                            // BuildField, which boundary-law.md L-D says should not happen. Kept anyway
                            // because removing it may break element rebuild from ctor-set readonly leaves,
                            // and WHICH fields depend on it is UNVERIFIED. Moving the two cases below into
                            // BuildField as real classes is scoped work, not a drive-by — do that before
                            // adding a third case here.
                            //
                            // Two salvage cases the game's own serializer handles that flat classification
                            // cannot: (a) read-only leaf FIELDS (readonly AbilityTrack.Source) — the game
                            // sets those via FieldInfo.SetValue (SerializationMember.CanSetValue), so can
                            // we; (b) interface/object refs pointing BACK at an object already inside this
                            // blob (CommonItemData.OwnerItem → its GeoItem) — the game restores them as
                            // graph references; within one element blob a local back-index is the same
                            // thing. Anything else stays excluded (never null-stomped: absent ≠ null).
                            if (f.Fi != null && f.Fi.IsInitOnly && LeafKindOf(f.ValueType, 0, out _))
                            { fw.Write((ushort)i); fw.Write(TagLeaf); EncodeLeaf(fw, f.ValueType, v); n++; break; }
                            if (v != null && (f.ValueType.IsInterface || f.ValueType == typeof(object)))
                                for (int b = 0; b < locals.Count; b++)
                                    if (ReferenceEquals(locals[b], v))
                                    { fw.Write((ushort)i); fw.Write(TagBackRef); fw.Write((ushort)b); n++; break; }
                            break;
                        }
                        // LeafDict / GeoItemDict inside a blob: none exist on current element types.
                        // ponytail: not carried (warned once); add sub-key encode here when one appears.
                        case FieldClass.LeafDict:
                        case FieldClass.GeoItemDict:
                            WarnOnce("blob: dict field " + t.Name + "." + f.Name + " not carried");
                            break;
                    }
                }
                w.Write((byte)n);
                w.Write(fms.ToArray());
            }
        }

        public static List<object> DecodeEntityList(byte[] bytes, RailField f, GeoLevelController geo)
        {
            var ser = GameSerializer ?? throw new InvalidOperationException("no game serializer");
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8))
            {
                if (r.ReadByte() != EntityListMarker) throw new IOException("bad entity-list marker");
                if (!r.ReadBoolean()) return null;
                int n = r.ReadUInt16();
                var list = new List<object>(n);
                for (int i = 0; i < n; i++)
                {
                    var locals = new List<object>();
                    var fixups = new List<Fixup>();
                    var v = DecodeValue(r, ser, f.ElemType, geo, locals, fixups, 0);
                    foreach (var fx in fixups)
                    {
                        try { fx.Field.SetValue(fx.Target, locals[fx.Idx]); }
                        catch (Exception ex) { WarnOnce("blob fixup failed on " + fx.Target.GetType().Name + "." + fx.Field.Name + ": " + ex.Message); }
                    }
                    foreach (var lo in locals) InvokePostReadSafe(ser, lo);
                    list.Add(v);
                }
                return list;
            }
        }

        private static void InvokePostReadSafe(Serializer ser, object o)
        {
            if (o == null) return;
            try { ser.GetSerializationType(o.GetType())?.InvokePostRead(o, null); }
            catch (Exception ex) { WarnOnce("blob post-read on " + o.GetType().Name + " threw " + ex.GetType().Name); }
        }

        private static object DecodeValue(BinaryReader r, Serializer ser, Type declared, GeoLevelController geo,
                                          List<object> locals, List<Fixup> fixups, int depth)
        {
            byte tag = r.ReadByte();
            switch (tag)
            {
                case TagNull: return null;
                case TagLeaf: return DecodeLeaf(r, declared, geo);
                case TagBackRef:
                {
                    int bi = r.ReadUInt16();
                    var v = bi < locals.Count ? locals[bi] : null;
                    if (v == null) WarnOnce("blob back-ref to unconstructed local #" + bi + " (" + declared.Name + ") — null substituted");
                    return v;
                }
                case TagBlob: return DecodeObjectBody(r, ser, declared, geo, locals, fixups, depth);
                default: throw new IOException("bad blob tag " + tag);
            }
        }

        private static object DecodeObjectBody(BinaryReader r, Serializer ser, Type t, GeoLevelController geo,
                                               List<object> locals, List<Fixup> fixups, int depth)
        {
            int myIdx = locals.Count;
            locals.Add(null); // placeholder: mirrors encode registration order; backfilled after construction

            int cpCount = r.ReadByte();
            var ci = CreateInfoOf(ser, t);
            object o;
            if (ci != null && ci.Params.Length == cpCount)
            {
                var args = new object[cpCount + 1]; // [0] = SerializedObjectData (null — creates like GeoItem's ignore it)
                for (int i = 0; i < cpCount; i++)
                    args[i + 1] = DecodeValue(r, ser, MemberType(ci.Params[i]), geo, locals, fixups, depth + 1);
                o = ci.Method.Invoke(args);
                if (o == null) throw new IOException("custom create returned null for " + t.Name);
            }
            else
            {
                for (int i = 0; i < cpCount; i++) DecodeValue(r, ser, typeof(object), geo, locals, fixups, depth + 1); // drift: consume
                o = Activator.CreateInstance(t, true);
            }
            locals[myIdx] = o;

            var rt = RailType.Get(t);
            int fCount = r.ReadByte();
            for (int i = 0; i < fCount; i++)
            {
                int idx = r.ReadUInt16();
                if (rt == null || idx >= rt.Fields.Count) throw new IOException("blob field idx " + idx + " out of range for " + t.Name);
                var f = rt.Fields[idx];
                byte tag = r.ReadByte();
                switch (tag)
                {
                    case TagNull:
                        if (!f.ValueType.IsValueType) f.SetValue(o, null);
                        break;
                    case TagLeaf:
                        f.SetValue(o, DecodeLeaf(r, f.ValueType, geo));
                        break;
                    case TagBackRef:
                        fixups.Add(new Fixup { Target = o, Field = f, Idx = r.ReadUInt16() });
                        break;
                    case TagBlob:
                        f.SetValue(o, DecodeObjectBody(r, ser, f.ValueType, geo, locals, fixups, depth + 1));
                        break;
                    case TagList:
                    {
                        if (f.ElemType == null) throw new IOException("blob list tag on non-list field " + t.Name + "." + f.Name);
                        int en = r.ReadUInt16();
                        var elems = new List<object>(en);
                        for (int j = 0; j < en; j++)
                            elems.Add(DecodeValue(r, ser, f.ElemType, geo, locals, fixups, depth + 1));
                        ApplyList(o, f, elems);
                        break;
                    }
                    case TagLeafList:
                    {
                        if (f.ElemType == null) throw new IOException("blob leaf-list tag on non-list field " + t.Name + "." + f.Name);
                        ApplyList(o, f, DecodeList(r, f, geo));
                        break;
                    }
                    default: throw new IOException("bad blob field tag " + tag);
                }
            }
            return o;
        }

        /// <summary>In-place list rebuild (the game exposes most lists by reference); assignment fallback.
        /// Shared by the client applier (top-level LeafList/EntityList fields) and the blob codec
        /// (nested lists on freshly constructed elements).</summary>
        internal static void ApplyList(object entity, RailField field, List<object> items)
        {
            // Unresolved EntityRef/DefRef elements decode to null (referent not spawned / def unknown on the
            // client). A null in a live game list can NRE native code that dereferences elements — drop the
            // holes rather than inserting null (the structural layer / a later diff re-adds them once resolvable).
            if (items != null && (IdentityResolver.IsRootEntityType(field.ElemType) ||
                                  typeof(BaseDef).IsAssignableFrom(field.ElemType)))
                items.RemoveAll(it => it == null);
            var current = field.GetValue(entity);
            if (current is IList list && !(current is Array))
            {
                list.Clear();
                if (items != null) foreach (var it in items) list.Add(it);
                return;
            }
            if (current != null && !(current is Array))
            {
                // ICollection<T>-shaped (HashSet, LinkedList, ResourcePack…). Resolve Clear/Add through the
                // INTERFACE whenever the container implements it: LinkedList<T> implements ICollection<T>.Add
                // EXPLICITLY, so a name probe on the concrete type finds nothing at all (the explicit impl is
                // a private method named "System.Collections.Generic.ICollection<T>.Add") and the field would
                // throw on apply — the same failure class as the GeoFacilityComponent[] resync storm.
                // Interface dispatch resolves explicit implementations at runtime, so this covers both shapes.
                // The name probe stays as the fallback for containers that expose Clear/Add without
                // implementing ICollection<T> (ResourcePack).
                var ct = current.GetType();
                var icoll = typeof(ICollection<>).MakeGenericType(field.ElemType);
                bool viaInterface = icoll.IsInstanceOfType(current);
                var clear = viaInterface ? icoll.GetMethod("Clear") : HarmonyLib.AccessTools.Method(ct, "Clear");
                var add = viaInterface ? icoll.GetMethod("Add") : HarmonyLib.AccessTools.Method(ct, "Add", new[] { field.ElemType });
                if (clear != null && add != null)
                {
                    clear.Invoke(current, null);
                    if (items != null) foreach (var it in items) add.Invoke(current, new[] { it });
                    return;
                }
            }
            if (field.IsWritable() && field.ValueType.IsArray)
            {
                var arr = Array.CreateInstance(field.ElemType, items?.Count ?? 0);
                if (items != null) for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                field.SetValue(entity, arr);
                return;
            }
            throw new InvalidOperationException("no list apply strategy for " + field.ValueType.Name);
        }
    }
}
