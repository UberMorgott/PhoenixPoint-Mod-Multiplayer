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
        Excluded = 5         // not on the rail; reason in RailField.Exclude, surfaced by the report
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
                { f.Class = FieldClass.EntityCollection; f.ElemType = elem; return f; }
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
        public static Serializer GameSerializer
        {
            get
            {
                try { return GameUtl.GameComponent<SerializationComponent>()?.Serializer; }
                catch { return null; }
            }
        }

        internal static Type MemberType(MemberInfo mi) => (mi as FieldInfo)?.FieldType ?? ((PropertyInfo)mi).PropertyType;

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
            if (t == typeof(TimeSpan)) { kind = LeafKind.TimeSpanTicks; return true; }
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
                case LeafKind.TimeSpanTicks: w.Write(((TimeSpan)v).Ticks); break;
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
                case LeafKind.TimeSpanTicks: return new TimeSpan(r.ReadInt64());
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
    }
}
