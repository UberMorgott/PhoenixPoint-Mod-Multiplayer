using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        EntityCollection = 2,// collection of keyable entities → descend per element at path.Name#key;
                             // an ORDERED container (List<T>/T[]) additionally ships its live key
                             // sequence as ONE field-level order-vector entry (keys, never indices)
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
        // Twin coercions (bridge/twin tables only — a direct-source member always matches its own type):
        internal FieldInfo HopFi;    // alias chain "Hop.Member": entity → hop object (class) → Fi/Pi
        internal FieldInfo WrapFi;   // live member is a one-field wrapper struct around ValueType
                                     // (EarthUnits.Value ← float): unwrap on read, re-wrap on write
        internal bool FactionRef;    // live member is GeoFaction, ValueType its def — unwrap to the def
                                     // on read; the WRITE lookup needs a GeoLevelController and lives in
                                     // GenericApplier (RailMeta.FactionByDef)
        private Func<object, object> _get;   // compiled once per member — see RailMeta.BuildGetter

        public bool CanRead => Fi != null || (Pi != null && Pi.CanRead);

        /// <summary>The host walk reads ~22k members every tick, so this goes through a compiled accessor
        /// cached beside the (already cached) RailType metadata instead of raw reflection. Same member,
        /// same value — cost only. Twin coercions (hop/unwrap) run only on fields that carry them.</summary>
        public object GetValue(object o)
        {
            if (HopFi != null && (o = HopFi.GetValue(o)) == null) return null;
            var v = (_get ?? (_get = RailMeta.BuildGetter(this)))(o);
            if (v == null) return null;
            if (WrapFi != null) return WrapFi.GetValue(v);
            if (FactionRef) return RailMeta.DefOfFaction(v, ValueType);
            return v;
        }

        public void SetValue(object o, object v)
        {
            if (HopFi != null && (o = HopFi.GetValue(o)) == null) return; // hop not initialised — nothing to write into
            if (WrapFi != null && v != null)
            {
                var boxed = Activator.CreateInstance(RailMeta.MemberType((MemberInfo)Fi ?? Pi));
                WrapFi.SetValue(boxed, v);
                v = boxed;
            }
            if (Fi != null) Fi.SetValue(o, v);
            else Pi.SetValue(o, v, null);
        }

        internal bool IsWritable()
        {
            // A readonly (initonly) FIELD is writable to reflection — FieldInfo.SetValue is exactly how
            // the game's own serializer fills GeoscapeEventRecord.EventId / GeoEventTimer.ID on load, and
            // excluding them stripped the KEY member off blob-rebuilt dict elements. Only a setterless
            // PROPERTY is genuinely unwritable.
            if (Fi != null) return true;
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
        private static readonly Dictionary<string, RailType> BridgedCache = new Dictionary<string, RailType>(StringComparer.Ordinal);

        // Classify-time reentrancy guard: Build → BuildField → RailMeta.HuskMembers(elem) → Get(elem)
        // can re-enter Get for a type whose Build is still on the stack (mutually-recursive keyless
        // element types) — without this that is a StackOverflow at classify time.
        private static readonly HashSet<Type> _building = new HashSet<Type>();

        public static RailType Get(Type t)
        {
            if (t == null) return null;
            if (Cache.TryGetValue(t, out var rt)) return rt;
            var ser = RailMeta.GameSerializer;
            if (ser == null) return null; // not in game yet — do NOT cache the miss
            // Cycle: answer null to the in-progress caller, never a half-built table. HuskMembers then
            // reports from serializer metadata alone — for direct-source types the same carried set (its
            // rt-table names are a subset of GetSerializedMembers), so the licensing decision stays
            // deterministic across peers and the cycle surfaces as a visible husk/none exclusion.
            if (!_building.Add(t)) return null;
            try { rt = Build(t, ser); }
            finally { _building.Remove(t); }
            Cache[t] = rt;
            return rt;
        }

        /// <summary>Field table for applying a recorded <c>*InstanceData</c> DTO's wire entries onto its
        /// LIVE owner (the DTO twin). The DTO's own direct table and this one draw from the SAME member
        /// source (<see cref="RailMeta.SerializedMembers"/> of the DTO) with the SAME ordinal sort, so a
        /// wire fieldIdx addresses the same member in both — but here each member is resolved onto the
        /// live type through the same <see cref="RailMeta.ResolveLive"/> the GeoFaction bridge uses.
        /// Members with no live counterpart stay Excluded ("dto-twin unresolved") and are logged once at
        /// apply time, never silently dropped. The polymorphic-object flatten of <see cref="Build"/> is
        /// deliberately absent: it would add rows the DTO's direct table does not have and break the
        /// fieldIdx parity this table exists for.</summary>
        public static RailType GetBridged(Type live, Type dto)
        {
            if (live == null || dto == null || live == dto) return null;
            var key = live.FullName + "|" + dto.FullName;
            if (BridgedCache.TryGetValue(key, out var rt)) return rt;
            var ser = RailMeta.GameSerializer;
            if (ser == null) return null; // pre-init — do NOT cache the miss
            rt = new RailType { Type = live, Source = "twin:" + dto.Name, Fields = new List<RailField>(), _byName = new Dictionary<string, RailField>(StringComparer.Ordinal) };
            var raw = new List<(string name, Type valType, MemberInfo live, string alias, string fail, FieldInfo hop)>();
            foreach (var dtoMi in RailMeta.SerializedMembers(ser, dto))
            {
                var valType = RailMeta.MemberType(dtoMi);
                var liveMi = RailMeta.ResolveLive(live, dtoMi.Name, valType, out var alias, out var hop);
                raw.Add((dtoMi.Name, valType, liveMi, alias, liveMi == null ? "dto-twin unresolved" : null, hop));
            }
            foreach (var e in raw.OrderBy(e => e.name, StringComparer.Ordinal))
            {
                if (rt._byName.ContainsKey(e.name)) continue; // hierarchy duplicate — first wins
                var f = BuildField(e.name, e.valType, e.live, e.alias, e.fail, e.hop);
                rt.Fields.Add(f);
                rt._byName[e.name] = f;
            }
            BridgedCache[key] = rt;
            return rt;
        }

        private static RailType Build(Type t, Serializer ser)
        {
            var rt = new RailType { Type = t, Fields = new List<RailField>(), _byName = new Dictionary<string, RailField>(StringComparer.Ordinal) };
            var raw = new List<(string name, Type valType, MemberInfo live, string alias, string fail, FieldInfo hop)>();

            var direct = RailMeta.SerializedMembers(ser, t);
            if (direct.Count > 0)
            {
                rt.Source = "direct";
                foreach (var mi in direct)
                    raw.Add((mi.Name, RailMeta.MemberType(mi), mi, null, null, null));
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
                    var live = RailMeta.ResolveLive(t, dtoMi.Name, valType, out var alias, out var hop);

                    // A DTO slot declared `object` is POLYMORPHIC: the game fills it at record time with a
                    // type the LIVE type itself declares (GeoFactionInstanceData.ExtendedInstanceData:48 is
                    // filled by GeoPhoenixFaction.RecordExtendedInstanceData:2046 with its own nested
                    // ExtendedInstanceData:51). Nothing can resolve `object` onto a live member, so the slot
                    // was excluded "bridge-unresolved" and everything persisted ONLY through it — the shared
                    // Skillpoints pool, AncientSiteProbes, every faction's extended block — never rode.
                    //
                    // Descend it by that runtime type, discovered STATICALLY as the same-named nested type
                    // so both peers derive an identical table without an instance (law 6 canonical order).
                    // Its members are flattened onto the live type through the SAME ResolveLive as every
                    // other bridge member: no per-faction code, no new FieldClass, and each member that does
                    // not resolve stays a visible exclusion rather than a silent drop.
                    var nested = live == null && valType == typeof(object) ? RailMeta.FindNestedDto(t, dtoMi.Name) : null;
                    if (nested != null)
                    {
                        foreach (var nMi in RailMeta.SerializedMembers(ser, nested))
                        {
                            var nType = RailMeta.MemberType(nMi);
                            var nLive = RailMeta.ResolveLive(t, nMi.Name, nType, out var nAlias, out var nHop);
                            raw.Add((nMi.Name, nType, nLive, nAlias, nLive == null ? "bridge-unresolved" : null, nHop));
                        }
                        // The slot itself is not a field (there is no live member to read); keep the line so
                        // the baseline shows WHERE the flattened members came from.
                        raw.Add((dtoMi.Name, valType, null, null, "flattened onto live type (" + nested.FullName + ")", null));
                        continue;
                    }

                    raw.Add((dtoMi.Name, valType, live, alias, live == null ? "bridge-unresolved" : null, hop));
                }
            }

            foreach (var e in raw.OrderBy(e => e.name, StringComparer.Ordinal))
            {
                if (rt._byName.ContainsKey(e.name)) continue; // hierarchy duplicate — first wins
                var f = BuildField(e.name, e.valType, e.live, e.alias, e.fail, e.hop);
                rt.Fields.Add(f);
                rt._byName[e.name] = f;
            }
            return rt;
        }

        private static RailField BuildField(string name, Type valType, MemberInfo live, string alias, string fail, FieldInfo hop = null)
        {
            var f = new RailField { Name = name, ValueType = valType, LiveAlias = alias, Fi = live as FieldInfo, Pi = live as PropertyInfo, HopFi = hop };
            if (fail != null || live == null) { f.Class = FieldClass.Excluded; f.Exclude = fail ?? "no live member"; return f; }
            if (!f.CanRead) { f.Class = FieldClass.Excluded; f.Exclude = "unreadable"; return f; }
            if (RailMeta.IsPresentation(valType))
            { f.Class = FieldClass.Excluded; f.Exclude = "presentation-only type (" + valType.Name + ")"; return f; }
            var optOut = RailMeta.OptOutReason(live.DeclaringType, name);
            if (optOut != null) { f.Class = FieldClass.Excluded; f.Exclude = optOut; return f; }

            // ResolveLive may accept a live member of a DIFFERENT type than the DTO declares (the codec
            // always speaks the DTO type — wire parity); record which coercion bridges the two. Container
            // shape (List↔HashSet↔GameTagsList) needs none: ApplyList dispatches on the live container.
            var liveT = RailMeta.MemberType(live);
            if (liveT != valType)
            {
                f.WrapFi = RailMeta.WrapperField(liveT, valType);
                f.FactionRef = liveT == typeof(GeoFaction) && typeof(BaseDef).IsAssignableFrom(valType);
            }

            // Leaf?
            if (RailMeta.LeafKindOf(valType, out var kind))
            {
                if (!f.IsWritable()) { f.Class = FieldClass.Excluded; f.Exclude = "read-only"; return f; }
                f.Class = FieldClass.Leaf; f.Leaf = kind; return f;
            }

            // Dictionary?
            var dictArgs = RailMeta.GenericInterfaceArgs(valType, typeof(IDictionary<,>));
            if (dictArgs != null)
            {
                if (RailMeta.IsSimpleKey(dictArgs[0]) && RailMeta.LeafKindOf(dictArgs[1], out _))
                { f.Class = FieldClass.LeafDict; f.KeyType = dictArgs[0]; f.DictValType = dictArgs[1]; return f; }
                if (GeoItemCodec.Handles(dictArgs[0], dictArgs[1]))
                { f.Class = FieldClass.GeoItemDict; f.KeyType = dictArgs[0]; f.DictValType = dictArgs[1]; return f; }
                // Unkeyable-element dict → whole-dict REPLACED VALUE (EntityList over the PAIR type).
                // For dictionaries whose elements have no derivable identity at all (GeoUnitDescriptor
                // has no id member — the save re-identifies by graph position), per-key addressing is
                // impossible BY CONSTRUCTION; the dict rides as one canonical blob instead.
                // Dictionary<K,V> implements ICollection<KVP<K,V>> (explicit Add), so ListApplyStrategy
                // licenses it and ApplyList's pair route rebuilds in place; the pair codec is the
                // IsKvpType arm of EncodeValue/DecodeValue. Each class-typed side husk-gates exactly
                // like an EntityList element.
                {
                    var kOk = RailMeta.LeafKindOf(dictArgs[0], out _) || (dictArgs[0].IsClass && RailMeta.HasPersistentMembers(dictArgs[0]));
                    var vOk = RailMeta.LeafKindOf(dictArgs[1], out _) || (dictArgs[1].IsClass && RailMeta.HasPersistentMembers(dictArgs[1]));
                    if (kOk && vOk)
                    {
                        var huskSide = !RailMeta.LeafKindOf(dictArgs[0], out _) && RailMeta.HuskMembers(dictArgs[0]).Count > 0 ? dictArgs[0]
                                     : !RailMeta.LeafKindOf(dictArgs[1], out _) && RailMeta.HuskMembers(dictArgs[1]).Count > 0 ? dictArgs[1] : null;
                        if (huskSide != null)
                        { f.Class = FieldClass.Excluded; f.Exclude = "dict-blob husk on " + huskSide.Name + " (" + string.Join(",", RailMeta.HuskMembers(huskSide)) + ")"; return f; }
                        f.Class = FieldClass.EntityList;
                        f.ElemType = typeof(KeyValuePair<,>).MakeGenericType(dictArgs[0], dictArgs[1]);
                        f.KeyType = dictArgs[0]; f.DictValType = dictArgs[1];
                        f.Unordered = true; // dict enumeration order is not state
                        return f;
                    }
                }
                f.Class = FieldClass.Excluded; f.Exclude = "dictionary with non-simple key/value (" + dictArgs[0].Name + "," + dictArgs[1].Name + ")";
                return f;
            }

            // Collection?
            var elem = RailMeta.ElemTypeOf(valType);
            if (elem != null)
            {
                if (RailMeta.IsPresentation(elem))
                { f.Class = FieldClass.Excluded; f.Exclude = "collection of presentation-only " + elem.Name; return f; }
                if (RailMeta.LeafKindOf(elem, out _))
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
                    // ORDER IS STATE only where the container itself is ordered. List<T>/T[] iterate their
                    // element sequence deterministically; any other container may not, and signing its
                    // iteration order would churn the wire every walk (law 6). Read by DiffEngine to decide
                    // whether a keyed collection ships the order-vector entry.
                    f.Unordered = !(valType.IsArray ||
                                    (valType.IsGenericType && valType.GetGenericTypeDefinition() == typeof(List<>)));
                    if (IdentityResolver.TypeKeyable(elem)) { f.Class = FieldClass.EntityCollection; return f; }

                    // N4 — rebuildability is decided HERE, at classify time, not consulted at ship time.
                    // EntityList is applied by ApplyList; a container none of its strategies can rebuild
                    // would reach the final `throw new InvalidOperationException("no list apply strategy")`
                    // on every single apply. Deciding it here means DiffEngine needs zero knowledge of it,
                    // and a field changing state shows up as a reviewable diff in docs/rail-baseline.txt
                    // instead of as a silent runtime throw.
                    //
                    // Ask the ONE strategy predicate rather than restating its ladder here: the original
                    // guard tested `!IsWritable && IsArray`, which is only the array-assign rung, so any
                    // OTHER unrebuildable container shape (no IList, no ICollection<T>, no Clear+Add) still
                    // slipped through licensed. Concrete case it covers: GeoPhoenixFacility._components
                    // (GeoPhoenixFacility.cs:48, readonly array) -> array-assign needs a writable field.
                    //
                    // Deliberately AFTER the keyable early-return: an EntityCollection is element-addressed
                    // (per-element descend writes leaves into elements that already exist) and never rebuilds
                    // its container, so rebuildability is not a question that applies to it.
                    if (RailMeta.ListApplyStrategy(f) == null)
                    { f.Class = FieldClass.Excluded; f.Exclude = "no list apply strategy (" + valType.Name + ")"; return f; }

                    // Husk-gated blob licensing. An EntityList is REBUILT from a blob (Activator.CreateInstance
                    // + table fields), so every reference member the blob does not carry lands NULL on the
                    // client where the game's own load path would have re-Init'd it. Licensing such a type
                    // ships silent corruption, not a missing value — BaseStat.Owner/.StatsRepo null is the
                    // wrong-numbers roster card, ResearchElement.ResearchDef null was 7ef0a30's NOTEXT.
                    //
                    // The refusal has to happen HERE and not at ship/apply time: the damage is per-ELEMENT, and
                    // the only after-the-fact remedy would be dropping bad elements — which is unsafe by
                    // construction, because AbilityTrack.AbilitiesByLevel is an AbilityTrackSlot[] whose INDEX
                    // IS THE LEVEL (dropping holes shifts every ability up a level). Classify-time also makes
                    // the loss a reviewable docs/rail-baseline.txt diff instead of a runtime surprise.
                    //
                    // EntityCollection is deliberately NOT gated: it is element-ADDRESSED (per-element descend
                    // writes leaves into elements that already exist on the client) and never reconstructs an
                    // element, so a husk member is simply left alone rather than nulled.
                    var husk = RailMeta.HuskMembers(elem);
                    if (husk.Count > 0)
                    { f.Class = FieldClass.Excluded; f.Exclude = "blob husk on " + elem.Name + " (" + string.Join(",", husk) + ")"; return f; }

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

        // ─── Cached member accessor (see RailField.GetValue) ───────────────
        // A DynamicMethod owned by the member's own module with skipVisibility, the same move Harmony makes
        // for its field refs: nearly every serialized game member is private, so an anonymously hosted
        // delegate could not reach it. Reflection stays as the fallback if codegen is ever unavailable.
        internal static Func<object, object> BuildGetter(RailField f)
        {
            var m = (MemberInfo)f.Fi ?? f.Pi;
            var owner = m.DeclaringType;
            try
            {
                var dm = new DynamicMethod("railget_" + owner.Name + "_" + m.Name, typeof(object),
                                           new[] { typeof(object) }, owner.Module, true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(owner.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, owner);
                Type vt;
                if (f.Fi != null) { il.Emit(OpCodes.Ldfld, f.Fi); vt = f.Fi.FieldType; }
                else
                {
                    var g = f.Pi.GetGetMethod(true);
                    if (g == null) throw new InvalidOperationException("property has no getter");
                    il.Emit(g.IsVirtual && !owner.IsValueType ? OpCodes.Callvirt : OpCodes.Call, g);
                    vt = f.Pi.PropertyType;
                }
                if (vt.IsValueType) il.Emit(OpCodes.Box, vt);
                il.Emit(OpCodes.Ret);
                return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer][rail] accessor codegen failed for " + owner.Name + "." + m.Name +
                                 " (" + ex.Message + ") — falling back to reflection");
                var fi = f.Fi; var pi = f.Pi;
                return fi != null ? (Func<object, object>)(o => fi.GetValue(o)) : o => pi.GetValue(o, null);
            }
        }

        // ─── Presentation refusal (same principle as the Unity-scene-object refusal) ───
        // Rendering artifacts are never game state, so they never ride the rail — regardless of the fact
        // that the save serializer happens to persist them. LocalizedTextBind is a CLASS with a writable
        // public LocalizationKey (decompile Base.UI/LocalizedTextBind.cs:9-13), so it fails the IsValueType
        // composite test and would otherwise classify as Descend (member) or EntityList/EntityCollection
        // (element) — i.e. the client would WRITE localization keys. Live proof from the last full walk
        // (rail-coverage.txt:26-28): 874 instances reached, LocalizationKey + _doNotLocalize as leaves,
        // owned by GeoSiteInstaceData.Motto/.Name (428) and GeoPhoenixBase+InstanceData.LocationDescription
        // (18). Nothing on a client can legitimately need a localization key shipped to it, and where an
        // instance is shared with a def the write would land in shared def state.
        //
        // STOPGAP, WITH AN EXIT CRITERION — a type-name list is the shape this repo's mandate forbids, and
        // it is here only because it is one line against an OBSERVED symptom ((d) NOTEXT haven/site names
        // and mottos). The general law is reference identity: only that can tell a def-OWNED
        // LocalizedTextBind (shared with the def, must never be written) from a per-instance one (harmless).
        // DiffEngine's N7 falsifier measures whether def-aliased binds actually occur. Exit criterion,
        // binding:
        //   aliased == 0 on a fresh campaign  → this list stays as a permanent cheap stopgap, delete N7.
        //   aliased >  0                      → build the reference-identity index over DefRepositoryDef
        //                                       .AllDefs and DELETE this list; it is then strictly wrong,
        //                                       because it also drops Name/Motto on the ~428 sites whose
        //                                       binds are NOT def-owned (recorded as risk R10).
        // Do not add a second type to this set. A second entry means the general law is overdue.
        private static readonly HashSet<string> _presentationTypes = new HashSet<string>
        {
            "Base.UI.LocalizedTextBind",
        };

        internal static bool IsPresentation(Type t) => t != null && _presentationTypes.Contains(t.FullName);

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
            // THIS EXCLUSION IS LOAD-BEARING, not documentation: before TimeUnit became a leaf (a3177eb,
            // batch 3) both members fell out as "no persistent members", so they never rode. The leaf
            // change would ADMIT them, i.e. start mirroring the host's raw clock base — the exact
            // double-count above. The opt-out keeps the old behaviour and states the reason for it.
            //
            // The reason strings name the intended replacement, the TimeAnchor "TA" root, which lands in
            // batch 7 (37af665). Until then the clock VALUE simply does not mirror; batch 1's N3 closed the
            // pause/speed LATENCY, which is a different thing.
            { "Base.Core.Timing.StartTime", "clock base — rides as the TimeAnchor \"TA\" root (a raw mirror double-counts local accrual)" },
            { "Base.Core.Timing.StartFixedTime", "clock base — rides as the TimeAnchor \"TA\" root (a raw mirror double-counts local accrual)" },

            // Per-actor clock accrual. Every ActorComponent's SerializationData getter re-records its
            // Timing on each host walk (ActorComponent.RecordInstanceData:379-386 → Timing.
            // RecordInstanceData:209-220), and TimingInstanceData.OwnNow/OwnFixedNow advance with scaled
            // real time — so with geo time running EVERY site/vehicle changed 2 fields per 0.5 s tick
            // (~880 of the measured ~890-changed churn behind the geo-time freeze/jerk). The values are
            // also the wrong thing to mirror: client actor clocks tick locally under the level clock, and
            // the level clock rides the TimeAnchor "TA" root. Excluding the Descend member kills the
            // churn without touching TimingInstanceData's OWN table — the "TA" anchor kind stays 6/6.
            { "Base.Entities.ActorInstanceData.TimingData", "per-actor clock (OwnNow/OwnFixedNow accrue every walk on every actor — pure churn; client clocks tick locally, the level clock rides the TimeAnchor \"TA\" root)" },

            // NakedRecruits pair blob (whole-dict replaced value) — GeoUnitDescriptor's two non-carried
            // refs are SAFE nulls, argued from the decompile, so the husk gate must not veto the dict:
            //   __level: lazy self-healing singleton — LevelController getter re-resolves it on first
            //     touch (GeoUnitDescriptor.cs:214 `__level ?? (__level = GameUtl.CurrentLevel()...)`).
            //   _temporaryStatsModifier: generation-PREVIEW scratch — set and NULLED by a Dispose scope
            //     around stat rolls (GeoUnitDescriptor.cs:166-171); null at rest by construction.
            { "PhoenixPoint.Geoscape.Entities.GeoUnitDescriptor.__level", "lazy self-healing level ref (LevelController getter re-resolves, GeoUnitDescriptor.cs:214)" },
            { "PhoenixPoint.Geoscape.Entities.GeoUnitDescriptor._temporaryStatsModifier", "transient generation-preview scratch (Dispose-scoped, GeoUnitDescriptor.cs:166-171) — null at rest" },

            // 2026-07-26 recruit-screen regression root: this backref was AbilityTrackSlot's only husk
            // member, which kept AbilitiesByLevel excluded — so a blob-rebuilt AbilityTrack came back
            // with a NULL slots array, its own PostRead rebind then NRE'd (AbilityTrack.cs:138-144
            // dereferences the array), and UIStateRosterRecruits.PrepareRecruitListEntryData crashed on
            // `.Where(AbilitiesByLevel)` — half-built recruit lists on every peer. The backref is
            // SELF-HEALING: the owning track's PostRead loop calls SetAbilityTrack(this) on every slot.
            { "PhoenixPoint.Common.Entities.Characters.AbilityTrackSlot.AbilityTrack", "self-healing backref — owner's PostRead rebind sets it (AbilityTrack.OnDeserialized:138-144 SetAbilityTrack(this))" },

            // CharacterIdentity's shared-def refs: every consumer path calls InitSharedTags() first,
            // which lazily re-resolves all three from the DefRepository when _sharedGameTags is null
            // (CharacterIdentity.cs:131-139; callers :144/:156…) — textbook self-healing.
            { "PhoenixPoint.Common.Entities.Characters.CharacterIdentity._sharedData", "self-healing shared-def ref — InitSharedTags() lazily re-resolves (CharacterIdentity.cs:131-139)" },
            { "PhoenixPoint.Common.Entities.Characters.CharacterIdentity._sharedGameTags", "self-healing shared-def ref — InitSharedTags() lazily re-resolves (CharacterIdentity.cs:131-139)" },
            { "PhoenixPoint.Common.Entities.Characters.CharacterIdentity._humanCustomization", "self-healing shared-def ref — InitSharedTags() lazily re-resolves (CharacterIdentity.cs:131-139)" },

            // v<4 save-migration leftover (EventSystemInstanceData.OldTriggeredEncounters, PreviousNames
            // "TriggeredEncounters") with NO live member — ResolveLive's unique-type fallback lands it on
            // EmptyExplorationEventIds, the only other List<string> on GeoscapeEventSystem, which is
            // def-driven CONFIG, not state. Migrated records already ride EncounterRecords.
            { "PhoenixPoint.Geoscape.Events.GeoscapeEventSystem.OldTriggeredEncounters", "v<4 migration leftover — unique-type fallback would alias def-driven config (EmptyExplorationEventIds)" },
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
            // NESTED DTO fallback — only when NO sibling DTO exists anywhere in the hierarchy: some
            // singletons persist through a nested *InstanceData type the sibling probe cannot see
            // (GeoscapeEventSystem+EventSystemInstanceData — the name does not even start with the
            // owner's). A FALLBACK, not an interleaved rung: probing nested types inside the first loop
            // hijacked GeoPhoenixFaction+ExtendedInstanceData over the base GeoFactionInstanceData
            // (extended blocks ride the polymorphic-slot flatten instead — Build's FindNestedDto arm).
            // Metadata order is identical on every peer (same game DLL), so first match is canonical.
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                foreach (var n in cur.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    if (n.Name.EndsWith("InstanceData", StringComparison.Ordinal) && n != t) return n;
            return null;
        }

        /// <summary>The type a polymorphic <c>object</c> DTO slot actually carries: the same-named nested type
        /// declared by the live type or one of its bases. Same generic move as <see cref="FindBridge"/> — a
        /// name pattern over the game's own metadata, never a type list — and resolved from the TYPE, not from
        /// an instance, so host and client build identical tables (law 6).</summary>
        internal static Type FindNestedDto(Type live, string name)
        {
            for (var cur = live; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var nested = cur.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
                if (nested != null && nested != live) return nested;
            }
            return null;
        }

        // Per-field twin aliases — the game's OWN Record/ProcessInstanceData mapping (decompile
        // GeoVehicle.cs:1049-1129, GeoSite.cs:1528-1573) for members where neither name-, backing-field-
        // nor unique-type matching can derive the live target. DATA only: resolution, classification and
        // application still ride the one generic path. Key = declaring live type + DTO member name
        // (looked up through base types, so DummyGeoVehicle inherits GeoVehicle's rows).
        private static readonly Dictionary<string, string> _twinAliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "PhoenixPoint.Geoscape.Entities.GeoVehicle.Name", "_vehicleName" },         // GeoVehicle.RecordInstanceData:1051 (the Name PROPERTY substitutes a localized default — not the store)
            { "PhoenixPoint.Geoscape.Entities.GeoVehicle.HitPoints", "Stats.HitPoints" }, // GeoVehicle.ProcessInstanceData:1119 (v3 arm; MaxHitPoints clamp is host-side truth)
            { "PhoenixPoint.Geoscape.Entities.GeoSite.OwnerFactionDef", "Owner" },        // GeoSite.ProcessInstanceData:1552 — def⇄faction via FactionRef coercion
            { "PhoenixPoint.Geoscape.Events.GeoscapeEventSystem.EncounterRecords", "_records" },     // GeoscapeEventSystem.RecordInstanceData:666 (_records.Values.ToList())
            { "PhoenixPoint.Geoscape.Events.GeoscapeEventSystem.SupressEvents", "SuppressEvents" },  // GeoscapeEventSystem.RecordInstanceData:667 (game typo in the DTO member)
        };

        /// <summary>Resolve a bridge member name onto the live type: explicit twin alias first (the game's
        /// own DTO→live mapping), then same name with a COMPATIBLE type (exact / one-field wrapper struct /
        /// GeoFaction-for-def / same-element collection), then the `_name` backing-field convention (the
        /// game exposes storage through read-only or shape-changed properties: _weapons/_modules/_addons),
        /// then the UNIQUE live member of the identical class type (catches renames like
        /// ManufactureQueue → Manufacture).</summary>
        internal static MemberInfo ResolveLive(Type live, string name, Type valType, out string alias, out FieldInfo hop)
        {
            alias = null;
            hop = null;
            for (var cur = live; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                if (!_twinAliases.TryGetValue(cur.FullName + "." + name, out var target)) continue;
                var am = ResolveAliasChain(live, target, valType, out hop);
                if (am != null) { alias = target; return am; }
                hop = null;
                break; // an alias that no longer resolves must FAIL VISIBLY, not fall through to guessing
            }

            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fi = HarmonyLib.AccessTools.Field(live, name);
            if (fi != null && TwinTypeCompatible(fi.FieldType, valType)) return fi;
            var pi = HarmonyLib.AccessTools.Property(live, name);
            if (pi != null && TwinTypeCompatible(pi.PropertyType, valType)) return pi;

            if (name[0] != '_')
            {
                var bf = HarmonyLib.AccessTools.Field(live, "_" + char.ToLowerInvariant(name[0]) + name.Substring(1));
                if (bf != null && TwinTypeCompatible(bf.FieldType, valType)) { alias = bf.Name; return bf; }
            }

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

        /// <summary>An alias target, optionally one hop deep ("Stats.HitPoints"). The hop must be a
        /// class-typed FIELD — writes go through the hop object by reference.
        /// ponytail: single hop only; extend to a chain when a real mapping needs one.</summary>
        private static MemberInfo ResolveAliasChain(Type live, string target, Type valType, out FieldInfo hop)
        {
            hop = null;
            int dot = target.IndexOf('.');
            if (dot < 0)
            {
                var m = (MemberInfo)HarmonyLib.AccessTools.Field(live, target) ?? HarmonyLib.AccessTools.Property(live, target);
                return m != null && TwinTypeCompatible(MemberType(m), valType) ? m : null;
            }
            hop = HarmonyLib.AccessTools.Field(live, target.Substring(0, dot));
            if (hop == null || hop.FieldType.IsValueType) { hop = null; return null; }
            var leaf = target.Substring(dot + 1);
            var mm = (MemberInfo)HarmonyLib.AccessTools.Field(hop.FieldType, leaf) ?? HarmonyLib.AccessTools.Property(hop.FieldType, leaf);
            if (mm != null && TwinTypeCompatible(MemberType(mm), valType)) return mm;
            hop = null;
            return null;
        }

        /// <summary>Can a live member of type <paramref name="liveT"/> mirror a DTO value of type
        /// <paramref name="dtoT"/>? Exact match; a one-field wrapper struct (EarthUnits ← float);
        /// GeoFaction for its def (FactionRef coercion); or a same-element mutable collection the
        /// existing ApplyList runtime dispatch already handles (List↔HashSet↔GameTagsList — never an
        /// array, never a dictionary, and the container must actually be mutable in place).</summary>
        private static bool TwinTypeCompatible(Type liveT, Type dtoT)
        {
            if (liveT == dtoT) return true;
            if (WrapperField(liveT, dtoT) != null) return true;
            if (liveT == typeof(GeoFaction) &&
                (dtoT == typeof(GeoFactionDef) || dtoT == typeof(PhoenixPoint.Common.Core.PPFactionDef))) return true;
            var de = ElemTypeOf(dtoT);
            if (de == null) return false;
            // Live Dictionary<K,V> mirroring a DTO List<V> — the game's own Record/Process shape
            // (GeoscapeEventSystem: _records.Values.ToList() out, ToDictionary(s => s.EventId) back).
            // Licensed only when the dict key is re-derivable from the element: exactly ONE serialized
            // element member of the key type (DictKeyMember — EventId / GeoEventTimer.ID). The walk
            // enumerates Values (EncodeEntityList), the apply rebuilds entries keyed by that member.
            var da = GenericInterfaceArgs(liveT, typeof(IDictionary<,>));
            if (da != null) return da[1] == de && DictKeyMember(de, da[0]) != null;
            return !liveT.IsArray &&
                   ElemTypeOf(liveT) == de &&
                   GenericInterfaceArgs(dtoT, typeof(IDictionary<,>)) == null &&
                   (typeof(IList).IsAssignableFrom(liveT) ||
                    typeof(ICollection<>).MakeGenericType(de).IsAssignableFrom(liveT));
        }

        /// <summary>The UNIQUE serialized member of <paramref name="elem"/> with the dict's key type —
        /// how the game itself keys these containers (GeoscapeEventRecord.EventId, GeoEventTimer.ID).
        /// Ambiguous or absent → null → the field stays a visible exclusion, never a guessed key.</summary>
        private static readonly Dictionary<string, MemberInfo> _dictKeyCache = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        internal static MemberInfo DictKeyMember(Type elem, Type keyType)
        {
            var ck = elem.FullName + "|" + keyType.FullName;
            if (_dictKeyCache.TryGetValue(ck, out var hit)) return hit;
            var ser = GameSerializer;
            if (ser == null) return null; // pre-init — do NOT cache the miss
            MemberInfo unique = null;
            foreach (var mi in SerializedMembers(ser, elem))
            {
                if (MemberType(mi) != keyType) continue;
                if (unique != null) { unique = null; break; } // ambiguous
                unique = mi;
            }
            _dictKeyCache[ck] = unique;
            return unique;
        }

        /// <summary>The single serialized, writable field of a one-member wrapper struct — the game's DTOs
        /// store the naked value (GeoVehicleInstanceData.RangeRemaining: float) while the live member holds
        /// the wrapper (GeoVehicle.RangeRemaining: EarthUnits, whose one member is float Value).</summary>
        internal static FieldInfo WrapperField(Type liveT, Type dtoT)
        {
            if (!liveT.IsValueType || liveT.IsPrimitive || liveT.IsEnum) return null;
            var ser = GameSerializer;
            if (ser == null) return null;
            var ms = SerializedMembers(ser, liveT);
            return ms.Count == 1 && ms[0] is FieldInfo fi && !fi.IsInitOnly && fi.FieldType == dtoT ? fi : null;
        }

        /// <summary>Read half of the FactionRef coercion: the def the DTO records for a live GeoFaction
        /// member (GeoVehicle.RecordInstanceData:1054 `Owner.Def.PPFactionDef`, GeoSite's records `Owner.Def`).</summary>
        internal static object DefOfFaction(object faction, Type defType)
        {
            var def = (faction as GeoFaction)?.Def;
            if (def == null) return null;
            if (defType == typeof(PhoenixPoint.Common.Core.PPFactionDef)) return def.PPFactionDef;
            return defType.IsInstanceOfType(def) ? def : null;
        }

        /// <summary>Write half of the FactionRef coercion (GeoSite.ProcessInstanceData:1552 /
        /// GeoVehicle.ProcessInstanceData:1079 shape): the live faction for a decoded def. Null when the
        /// level has no such faction — the applier then keeps the client's live value (boundary-law L-C).</summary>
        internal static object FactionByDef(GeoLevelController geo, object def)
        {
            if (geo == null) return null;
            if (def is GeoFactionDef gfd) return geo.Factions.FirstOrDefault(f => f.Def == gfd); // GetFaction(GeoFactionDef) throws when missing
            if (def is PhoenixPoint.Common.Core.PPFactionDef ppd) return geo.GetFaction(ppd, canFail: true);
            return null;
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

        /// <summary>Leaf-encodable check + kind. Composite recursion (structs of structs) terminates by
        /// construction — a struct cannot contain itself (CS0523) — so no depth cap is needed.</summary>
        internal static bool LeafKindOf(Type t, out LeafKind kind)
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
            if (t.IsValueType && !t.IsPrimitive)
            {
                var rt = RailType.Get(t);
                if (rt != null && rt.Fields.Count > 0 &&
                    rt.Fields.All(f => f.Class == FieldClass.Leaf))
                { kind = LeafKind.Composite; return true; }
            }
            return false;
        }

        // ─── Canonical leaf codec ───────────────────────────────────────────

        // ONE writer per thread instead of a MemoryStream + BinaryWriter + ToArray PER FIELD: the host walk
        // encodes ~22k fields every 0.5 s and that per-field allocation was the bulk of the tick's garbage.
        // Nothing re-enters an encode in flight — EncodeLeaf recurses on the SAME writer, and the blob codec
        // (EncodeEntityList/EncodeObjectBody) runs on its own streams — so one scratch is enough.
        [ThreadStatic] private static MemoryStream _scratchMs;
        [ThreadStatic] private static BinaryWriter _scratchW;

        private static BinaryWriter Scratch()
        {
            if (_scratchW == null)
            {
                _scratchMs = new MemoryStream(256);
                _scratchW = new BinaryWriter(_scratchMs, System.Text.Encoding.UTF8);
            }
            _scratchMs.SetLength(0);
            _scratchMs.Position = 0;
            return _scratchW;
        }

        private static void EncodeFieldInto(BinaryWriter w, RailField f, object v)
        {
            if (f.Class == FieldClass.LeafList) EncodeList(w, f, v);
            else EncodeLeaf(w, f.Class == FieldClass.LeafDict ? f.DictValType : f.ValueType, v);
        }

        public static byte[] EncodeFieldValue(RailField f, object v)
        {
            EncodeFieldInto(Scratch(), f, v);
            return _scratchMs.ToArray();
        }

        /// <summary>Same bytes as <see cref="EncodeFieldValue(RailField,object)"/>, but returns
        /// <paramref name="prev"/> ITSELF when the encoding is byte-identical to it — the idle-tick case,
        /// where every field re-encodes to exactly what it already was. Costs no array and lets the diff
        /// short-circuit on reference equality. Encoded arrays are never mutated afterwards, so the old and
        /// new snapshot sharing one is safe.</summary>
        internal static byte[] EncodeFieldValue(RailField f, object v, byte[] prev)
        {
            EncodeFieldInto(Scratch(), f, v);
            var buf = _scratchMs.GetBuffer();
            int len = (int)_scratchMs.Length;
            if (prev != null && prev.Length == len)
            {
                int i = 0;
                while (i < len && prev[i] == buf[i]) i++;
                if (i == len) return prev;
            }
            var bytes = new byte[len];
            Buffer.BlockCopy(buf, 0, bytes, 0, len);
            return bytes;
        }

        public static void EncodeLeaf(BinaryWriter w, Type declared, object v)
        {
            if (v == null) { w.Write((byte)LeafKind.Null); return; }
            LeafKindOf(declared, out var kind);
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
            // Ordered list of known length: count + element bytes go straight into the writer, in exactly the
            // order the buffered path below would have produced them — byte-identical, minus a MemoryStream +
            // BinaryWriter + array PER ELEMENT. The buffered path stays for HashSets (the canonical sort needs
            // the encoded elements materialised) and for the u16 truncation case.
            if (!f.Unordered && v is ICollection col && col.Count <= ushort.MaxValue)
            {
                w.Write((ushort)col.Count);
                foreach (var e in (IEnumerable)v) EncodeLeaf(w, f.ElemType, e);
                return;
            }
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
                    // WarnOnce, not raw: a systematic miss (mod parity gap) repeats per entry per tick.
                    // Unresolved ≠ null: the host wrote a REAL ref (a genuine null is LeafKind.Null), so
                    // returning null here would let the applier clobber a valid live ref — and the host
                    // snapshot is unchanged, so it would never re-ship the correction.
                    if (def == null) { WarnOnce("DecodeLeaf: unknown def guid " + guid); return Unresolved; }
                    return def;
                }
                case LeafKind.EntityRef:
                {
                    var key = r.ReadString();
                    var e = IdentityResolver.Resolve(geo, key, null);
                    if (e == null) { WarnOnce("DecodeLeaf: unresolved entity ref " + key); return Unresolved; }
                    return e;
                }
                case LeafKind.Composite:
                {
                    var rt = RailType.Get(declared);
                    var box = Activator.CreateInstance(declared);
                    int n = r.ReadByte();
                    bool miss = false;
                    for (int i = 0; i < n; i++)
                    {
                        int idx = r.ReadUInt16();
                        var field = idx < rt.Fields.Count ? rt.Fields[idx] : null;
                        var v = DecodeLeaf(r, field?.ValueType ?? typeof(object), geo);
                        // Keep reading (stream integrity), but a partially-defaulted composite would
                        // clobber the live value — an unresolved member skips the WHOLE composite write.
                        if (ReferenceEquals(v, Unresolved)) { miss = true; continue; }
                        field?.SetValue(box, v);
                    }
                    return miss ? Unresolved : box;
                }
                default: throw new IOException("bad leaf kind " + kind);
            }
        }

        private const byte ListMarker = 14;

        /// <summary>LeafDict subkey deletion sentinel. A first byte that is never a valid encoded leaf
        /// (LeafKinds 0-13, list marker 14), so an explicit dict-key delete stays distinguishable on the
        /// wire from a genuine present-null value (LeafKind.Null = 0).</summary>
        public const byte DictTombstone = 0xFF;

        /// <summary>Decode-side sentinel (never on the wire): a NON-null EntityRef/DefRef whose referent
        /// this client cannot resolve yet (not spawned / mod parity gap). Distinct from a genuine wire
        /// null (LeafKind.Null, decodes to null): every applier SKIPS the write instead of clobbering a
        /// valid live ref — the host snapshot is unchanged, so a null written here would never be
        /// re-shipped and the divergence would be silent and permanent.</summary>
        public static readonly object Unresolved = new object();

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
        //
        // Post-read fires over the blob's own `locals` — EVERY object the decode CONSTRUCTED, at every
        // nesting depth (DecodeObjectBody registers each one) — and never on the LIVE OWNER of the field
        // being applied. That is the whole contract, and it is sufficient: AbilityTrack is itself a blob
        // element (CharacterProgression._abilityTracks is an EntityList, so nothing ever descends INTO an
        // AbilityTrack), so it is a local, so its OnDeserialized runs and its slots get SetAbilityTrack.
        //
        // Firing the live owner's post-read as well has been proposed and is REFUSED. Those callbacks are
        // save-load migrations, not re-link hooks: GeoCharacter.InitAfterDeserialiaztion (decompile
        // GeoCharacter.cs:1592) branches on serObj.SerializedVersion — which we have no honest value for —
        // and can call Init() and SetItems(), i.e. real gameplay side-effects on the client's authoritative
        // mirror, which law 3 forbids. Research.OnPostSerialize (Research.cs:1264) is the same shape.
        // The only owner post-read in the closure that would even run is CharacterProgression.Init(), which
        // just re-assigns a callback it already holds. Zero benefit, one law violated.

        internal const byte EntityListMarker = 15; // distinct from LeafKinds 0-13 and ListMarker 14

        private const byte TagNull = 0, TagLeaf = 1, TagBlob = 2, TagBackRef = 3, TagList = 4, TagLeafList = 5, TagLeafDict = 6;

        /// <summary>KeyValuePair&lt;,&gt; — the pair element type of a whole-dict blob (unkeyable-element
        /// dictionaries riding as EntityList; see BuildField's dict arm).</summary>
        internal static bool IsKvpType(Type t) =>
            t != null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
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
                        (f.Class == FieldClass.Excluded && f.Fi != null && f.Fi.IsInitOnly && LeafKindOf(f.ValueType, out _)))
                    { v = true; break; }
            _blobContentCache[t] = v;
            return v;
        }

        // ─── Keyed-collection ORDER channel ─────────────────────────────────
        //
        // An EntityCollection is addressed as a SET (path.Name#key), so a pure reorder changes no element
        // value and no key set — without this entry the walk emits NOTHING for it, and the client's list
        // keeps whatever order it happened to have ("moved an item, everyone else sees it auto-sorted").
        // The vector is the live KEY sequence (law 2: keys, never indices); it rides as an ordinary
        // snapshot entry, so it ships exactly when membership or order changed and is silent on idle ticks.

        internal const byte OrderVectorMarker = 16; // distinct from LeafKinds 0-13, ListMarker 14, EntityListMarker 15

        /// <summary>Canonical bytes of a live key sequence; returns <paramref name="prev"/> ITSELF when
        /// byte-identical (idle-tick zero-alloc, same contract as <see cref="EncodeFieldValue(RailField,object,byte[])"/>).</summary>
        internal static byte[] EncodeKeyOrder(List<string> keys, byte[] prev)
        {
            var w = Scratch();
            w.Write(OrderVectorMarker);
            w.Write((ushort)Math.Min(keys.Count, ushort.MaxValue));
            for (int i = 0; i < keys.Count && i < ushort.MaxValue; i++) w.Write(keys[i]);
            var buf = _scratchMs.GetBuffer();
            int len = (int)_scratchMs.Length;
            if (prev != null && prev.Length == len)
            {
                int i = 0;
                while (i < len && prev[i] == buf[i]) i++;
                if (i == len) return prev;
            }
            var bytes = new byte[len];
            Buffer.BlockCopy(buf, 0, bytes, 0, len);
            return bytes;
        }

        /// <summary>The client's own live key sequence in wire form, or null when any element is unkeyable
        /// (caller then treats the entry as changed — the apply side copes element-by-element).</summary>
        internal static byte[] EncodeKeyOrderOf(object container)
        {
            if (!(container is IEnumerable src)) return null;
            var keys = new List<string>();
            foreach (var e in src)
            {
                if (e == null) continue;
                var k = IdentityResolver.KeyOf(e);
                if (k == null) return null;
                keys.Add(k);
            }
            return EncodeKeyOrder(keys, null);
        }

        internal static string[] DecodeKeyOrder(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8))
            {
                if (r.ReadByte() != OrderVectorMarker) throw new IOException("bad order-vector marker");
                var keys = new string[r.ReadUInt16()];
                for (int i = 0; i < keys.Length; i++) keys[i] = r.ReadString();
                return keys;
            }
        }

        // ─── Dict census: resync-only present-key list ──────────────────────
        // Tombstones ride only the disappearance TICK; a client that missed that tick (load window)
        // keeps the phantom key through every resend, because a full resend re-emits PRESENT pairs
        // only. The census is the delete-side closure of forced re-emits: the host's full key set for
        // one dict field, shipped ONLY on the forced paths (full resend / ForceReemit), applied as
        // "prune everything I don't list". Normal ticks stay wire-identical.

        /// <summary>Census value marker — distinct from LeafKinds 0-13, ListMarker 14, EntityListMarker 15,
        /// OrderVectorMarker 16 and DictTombstone 0xFF, so a census can never decode as a value or a delete.
        /// A census entry additionally rides with SubKey "" (a real dict entry always carries its key).</summary>
        internal const byte DictCensusMarker = 17;

        /// <summary>[marker][count:u16][subKey:string]* — the host dict's present-key set at walk time.
        /// ponytail: plain strings — ~240 def-guid keys hit DiffEngine's 8 KB per-entry cap and the census
        /// is dropped with a visible incident; pack guids as 16 raw bytes if a storage that big matters.</summary>
        internal static byte[] EncodeDictCensus(List<string> subKeys)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                w.Write(DictCensusMarker);
                w.Write((ushort)Math.Min(subKeys.Count, ushort.MaxValue));
                for (int i = 0; i < subKeys.Count && i < ushort.MaxValue; i++) w.Write(subKeys[i]);
                return ms.ToArray();
            }
        }

        internal static string[] DecodeDictCensus(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8))
            {
                if (r.ReadByte() != DictCensusMarker) throw new IOException("bad census marker");
                var keys = new string[r.ReadUInt16()];
                for (int i = 0; i < keys.Length; i++) keys[i] = r.ReadString();
                return keys;
            }
        }

        /// <summary>Rearrange a LIVE keyed list to the host's key sequence — index writes on the existing
        /// container, never a rebuild (elements are live entities; destroying them is the husk bug).
        /// Unknown keys are skipped (element not spawned here yet), local elements missing from the vector
        /// keep their relative order at the tail. Returns true when any position actually moved.</summary>
        internal static bool ReorderByKeys(object container, string[] keys)
        {
            if (!(container is IList list) || list.Count < 2) return false;
            int n = list.Count;
            var localKeys = new string[n];
            for (int j = 0; j < n; j++) localKeys[j] = IdentityResolver.KeyOf(list[j]);
            var target = new List<object>(n);
            var used = new bool[n];
            // ponytail: O(n·m) scan — keyed collections here are tens of elements; index map if one ever isn't.
            foreach (var k in keys)
                for (int j = 0; j < n; j++)
                {
                    if (used[j] || !string.Equals(localKeys[j], k, StringComparison.Ordinal)) continue;
                    target.Add(list[j]);
                    used[j] = true;
                    break;
                }
            for (int j = 0; j < n; j++) if (!used[j]) target.Add(list[j]);
            bool changed = false;
            for (int i = 0; i < n; i++)
                if (!ReferenceEquals(list[i], target[i])) { list[i] = target[i]; changed = true; }
            return changed;
        }

        /// <summary>Substitute value-identical LIVE elements for freshly decoded ones before
        /// <see cref="ApplyList"/>: a pure reorder then MOVES the client's existing objects instead of
        /// replacing them, and state the blob does not carry (a loaded weapon's AmmoManager rides as
        /// TagNull) survives on every unchanged element. Match = canonical element-encoding byte equality —
        /// the exact "changed?" question the host itself asked, so no second equality table can drift.
        /// A value-duplicate pool maps 1:1 (each live instance claimed once).</summary>
        /// <summary>Element view of an EntityList container. A live Dictionary twin (DictKeyMember shape)
        /// enumerates its VALUES — raw dict enumeration yields KeyValuePairs, not the element type the
        /// codec speaks. EXCEPT when the pairs ARE the element type (whole-dict blob, ElemType = KVP):
        /// then the pair enumeration is exactly right.</summary>
        private static IEnumerable ElementsOf(object container, Type elemType) =>
            container is IDictionary d && !IsKvpType(elemType) ? d.Values : (IEnumerable)container;

        internal static void ReuseLiveElements(RailField f, object current, List<object> items)
        {
            if (items == null || items.Count == 0 || !(current is IEnumerable src)) return;
            src = ElementsOf(current, f.ElemType);
            var pool = new List<object>();
            foreach (var e in src) if (e != null) pool.Add(e);
            if (pool.Count == 0) return;
            var poolEnc = new byte[pool.Count][];
            for (int j = 0; j < pool.Count; j++)
                try { poolEnc[j] = EncodeElement(f, pool[j]); } catch { poolEnc[j] = null; }
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null) continue;
                byte[] enc;
                try { enc = EncodeElement(f, items[i]); } catch { continue; }
                for (int j = 0; j < pool.Count; j++)
                {
                    if (poolEnc[j] == null || !BytesEqual(poolEnc[j], enc)) continue;
                    items[i] = pool[j];
                    poolEnc[j] = null; // claimed
                    break;
                }
            }
        }

        /// <summary>One element through the same per-element codec <see cref="EncodeEntityList"/> uses
        /// (own locals scope), so two value-equal objects always yield identical bytes.</summary>
        internal static byte[] EncodeElement(RailField f, object e)
        {
            var ser = GameSerializer ?? throw new InvalidOperationException("no game serializer");
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                EncodeValue(w, ser, f.ElemType, e, new List<object>(), 0);
                return ms.ToArray();
            }
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
                foreach (var e in ElementsOf(listVal, f.ElemType)) items.Add(e);
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
            if (LeafKindOf(declared, out _)) { w.Write(TagLeaf); EncodeLeaf(w, declared, v); return; }
            if (IsKvpType(declared))
            {
                // Pair of a whole-dict blob: Key then Value through this same codec (leaf or blob per
                // side). TagBlob on the wire — DecodeValue branches on the declared pair type.
                var ka = declared.GetGenericArguments();
                w.Write(TagBlob);
                EncodeValue(w, ser, ka[0], declared.GetProperty("Key").GetValue(v, null), locals, depth + 1);
                EncodeValue(w, ser, ka[1], declared.GetProperty("Value").GetValue(v, null), locals, depth + 1);
                return;
            }
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
                            if (SalvagesInitOnlyLeaf(f))
                            { fw.Write((ushort)i); fw.Write(TagLeaf); EncodeLeaf(fw, f.ValueType, v); n++; break; }
                            if (v != null && (f.ValueType.IsInterface || f.ValueType == typeof(object)))
                                for (int b = 0; b < locals.Count; b++)
                                    if (ReferenceEquals(locals[b], v))
                                    { fw.Write((ushort)i); fw.Write(TagBackRef); fw.Write((ushort)b); n++; break; }
                            break;
                        }
                        case FieldClass.LeafDict:
                        {
                            // First real occupant: ProgressionDescriptor.PersonalAbilities inside the
                            // NakedRecruits pair blob — dropping it would husk every recruit's rolled
                            // abilities. Canonical order (law 6): entries sorted by encoded key.
                            if (!(v is IDictionary dict)) break; // null/absent → decode keeps ctor default
                            var des = new List<(string sub, object dv)>();
                            foreach (DictionaryEntry de in dict) des.Add((EncodeDictKey(de.Key), de.Value));
                            des.Sort((a, b) => string.CompareOrdinal(a.sub, b.sub));
                            fw.Write((ushort)i); fw.Write(TagLeafDict); fw.Write((ushort)des.Count);
                            foreach (var (sub, dv) in des)
                            { fw.Write(sub); EncodeValue(fw, ser, f.DictValType, dv, locals, depth + 1); }
                            n++;
                            break;
                        }
                        // GeoItemDict inside a blob: none exist on current element types.
                        // ponytail: not carried (warned once); add GeoItemCodec encode here when one appears.
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
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is NullReferenceException)
            {
                // Reflection-invoked callbacks wrap their throw — a serObj-dereferencing callback
                // (CharacterIdentity.PostRead reads SerializedVersion first line) arrived here as TIE
                // and dodged the NRE arm below, logging as an anonymous throw. Same shape, same answer.
                WarnOnce("blob post-read on " + o.GetType().Name + " dereferences SerializedObjectData (unavailable in rail decode) — callback aborted mid-run, its rebind/migration did NOT apply");
            }
            catch (NullReferenceException)
            {
                // The callback dereferenced its SerializedObjectData parameter — save-load-only context the
                // rail cannot supply (its ctor needs a live ReadStream section, SerializedObjectData.cs:22-30).
                // GeoVehicleInstanceData.OnDeserialized:66-70 (InstanceDataVersion = serObj.SerializedVersion)
                // is the known shape. Skipping by signature instead is NOT safe: AbilityTrack.OnDeserialized
                // takes the same parameter and never touches it, and its slot rebind must keep running.
                // Loud by design — a type of this shape riding a blob is a classification bug, not noise.
                WarnOnce("blob post-read on " + o.GetType().Name + " dereferences SerializedObjectData (unavailable in rail decode) — callback aborted mid-run, its rebind/migration did NOT apply");
            }
            catch (Exception ex)
            {
                // GENUINE failure (not the null-serObj shape): rethrow. The element decode then fails,
                // the whole-field apply is skipped with a LogMissOnce ERROR, and the client keeps its
                // stale-but-healthy value — loud staleness beats the AbilityTrack lesson (a swallowed
                // rebind failure shipped half-built objects into live UI and froze the game). The
                // NRE-shape arms above stay tolerated: CharacterIdentity.PostRead reads
                // serObj.SerializedVersion on line one (benign v>=5 early-return equivalence) and the
                // rail cannot supply a SerializedObjectData.
                throw new InvalidOperationException("blob post-read on " + o.GetType().Name + " FAILED — element not usable", ex);
            }
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
                case TagBlob:
                    if (IsKvpType(declared))
                    {
                        var ka = declared.GetGenericArguments();
                        var k = DecodeValue(r, ser, ka[0], geo, locals, fixups, depth);
                        var val = DecodeValue(r, ser, ka[1], geo, locals, fixups, depth);
                        // Unresolved on either side poisons the pair — the applier drops it (never a
                        // null-keyed dict entry); the host's next change re-ships the whole dict anyway.
                        if (ReferenceEquals(k, Unresolved) || ReferenceEquals(val, Unresolved) || k == null) return Unresolved;
                        return Activator.CreateInstance(declared, k, val);
                    }
                    return DecodeObjectBody(r, ser, declared, geo, locals, fixups, depth);
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
                {
                    var a = DecodeValue(r, ser, MemberType(ci.Params[i]), geo, locals, fixups, depth + 1);
                    // Unresolved ref as a create arg: pass null (fresh construction, nothing clobbered) —
                    // the sentinel itself would throw a type mismatch inside the create method.
                    args[i + 1] = ReferenceEquals(a, Unresolved) ? null : a;
                }
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
                    {
                        var lv = DecodeLeaf(r, f.ValueType, geo);
                        // Unresolved ref member: leave the freshly-constructed default (same as the old
                        // load-path gap, nothing clobbered) rather than writing the sentinel.
                        if (!ReferenceEquals(lv, Unresolved)) f.SetValue(o, lv);
                        break;
                    }
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
                    case TagLeafDict:
                    {
                        if (f.KeyType == null) throw new IOException("blob leaf-dict tag on non-dict field " + t.Name + "." + f.Name);
                        int dn = r.ReadUInt16();
                        var dict = f.GetValue(o) as IDictionary; // ctor-initialized on every current shape
                        dict?.Clear();
                        for (int j = 0; j < dn; j++)
                        {
                            var key = DecodeDictKey(r.ReadString(), f.KeyType);
                            var dv = DecodeValue(r, ser, f.DictValType, geo, locals, fixups, depth + 1);
                            if (dict != null && !ReferenceEquals(dv, Unresolved)) dict[key] = dv;
                        }
                        break;
                    }
                    default: throw new IOException("bad blob field tag " + tag);
                }
            }
            return o;
        }

        /// <summary>Reference-typed members of a type that a BLOB of it would NOT carry. The codec builds
        /// elements with <c>Activator.CreateInstance(nonPublic)</c> and fills only the table's fields, so each
        /// of these lands NULL on the client while the game's own load path would have re-<c>Init</c>'d it —
        /// the 7ef0a30 `ResearchElement` husk (ResearchDef null → NOTEXT), and the BaseStat.Owner/.StatsRepo
        /// nulls behind the wrong-numbers roster cards.
        ///
        /// ONE table, two callers: the classifier's EntityList refusal in <see cref="RailType"/> and the
        /// RailCheck baseline's <c>husk=</c> column. It lived in tools/RailCheck/Program.cs while it was only a
        /// REPORT; the moment it also decides classification, a second copy is the GeoItem/TypeKeyable
        /// two-tables-disagree bug by construction (ARCHITECTURE.md "Husk-gated blob licensing").</summary>
        /// <summary>The ONE predicate for "does the blob salvage carry this EXCLUDED field": initonly LEAF
        /// fields only (<see cref="EncodeObjectBody"/>'s Excluded case (a)). <see cref="HuskMembers"/> must
        /// ask the exact same question — it used to count ANY initonly field as carried, so an initonly
        /// NON-leaf ref member would slip the husk gate and still land null on the client. (Salvage case (b),
        /// the in-blob back-ref, is instance-dependent and deliberately NOT counted: the gate stays
        /// conservative — absent from the gate ⇒ reported as husk.)</summary>
        private static bool SalvagesInitOnlyLeaf(RailField f) =>
            f.Fi != null && f.Fi.IsInitOnly && LeafKindOf(f.ValueType, out _);

        internal static List<string> HuskMembers(Type t)
        {
            var husk = new List<string>();
            foreach (var m in HuskScan(t))
                if (m.Waiver == null) husk.Add(m.Name + ":" + m.Type.Name);
            husk.Sort(StringComparer.Ordinal);
            return husk;
        }

        /// <summary>ALL uncarried reference members of a type, each with its waiver (opt-out reason;
        /// null = a REAL husk). ONE enumeration, two consumers — the classifier's gate
        /// (<see cref="HuskMembers"/>: waived = not husk) and RailCheck's RECURSIVE husk sweep, which
        /// prints the waivers and fails on unwaived/unjustified ones at every nesting depth (the
        /// AbilityTrackSlot lesson: a nested husk is invisible to the top-level gate).</summary>
        internal static List<(string Name, Type Type, string Waiver)> HuskScan(Type t)
        {
            var husk = new List<(string, Type, string)>();
            var ser = GameSerializer;
            if (ser == null) return husk; // pre-init: no metadata yet, and nothing is classified either

            var carried = new HashSet<string>(StringComparer.Ordinal);
            var rt = RailType.Get(t);
            if (rt != null)
                foreach (var f in rt.Fields)
                    if (f.Class != FieldClass.Excluded || SalvagesInitOnlyLeaf(f)) carried.Add(f.Name);
            // ALL modes on purpose (not SerializedMembers, which keeps ReadWrite only): a WriteOnly member is
            // a custom-create PARAMETER, and the blob does carry those — GeoItem's ItemDef rides as a ctor arg.
            try
            {
                foreach (var mwa in ser.GetSerializedMembers(t))
                    if (mwa.MemberInfo != null) carried.Add(mwa.MemberInfo.Name);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] HuskMembers(" + t.Name + ") failed: " + ex.Message); }

            // …but a create param is matched to its FIELD by name nowhere: the create method assigns it, and
            // the field it fills is routinely named differently (GeoItem.cs:14,20,31 — create param `ItemDef`
            // fills `private readonly ItemDef _def`). Name-matching alone therefore reports `_def` as an
            // uncarried husk when the blob demonstrably carries it as a ctor argument, and gating on that
            // would drop all six GeoCharacter item lists. Which field a param fills is not knowable, but its
            // TYPE is — so a field whose type is a create param's type counts as carried. Scoped to create
            // params ONLY: same-type matching in general would erase real husks (BaseStat.Owner).
            var createParamTypes = new HashSet<Type>();
            var ci = CreateInfoOf(ser, t);
            if (ci != null)
                foreach (var p in ci.Params)
                    if (p != null) createParamTypes.Add(MemberType(p));

            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                foreach (var fi in cur.GetFields(F))
                {
                    if (fi.FieldType.IsValueType || fi.FieldType == typeof(string)) continue;
                    if (typeof(Delegate).IsAssignableFrom(fi.FieldType)) continue; // events are never state
                    if (createParamTypes.Contains(fi.FieldType)) continue;         // filled by the custom create
                    // An auto-property's backing field is named "<Prop>k__BackingField"; the serializer
                    // discovers the PROPERTY, so match on that name (ResearchElement.ResearchDef — the
                    // 7ef0a30 NOTEXT husk — is exactly this shape and was invisible without it).
                    var name = fi.Name[0] == '<' ? fi.Name.Substring(1, fi.Name.IndexOf('>') - 1) : fi.Name;
                    if (carried.Contains(name)) continue;
                    // A member the opt-out table names is a REVIEWED deliberate exclusion (with its safety
                    // argument in the reason string) — the husk gate exists to catch SILENT nulls, not to
                    // veto documented ones. The sweep re-checks the reason's JUSTIFICATION class.
                    husk.Add((name, fi.FieldType, OptOutReason(cur, name)));
                }
            return husk;
        }

        /// <summary>Which <see cref="ApplyList"/> strategy can rebuild a container of this field's DECLARED
        /// shape, or null when none can — i.e. every apply would reach ApplyList's final
        /// <c>throw new InvalidOperationException("no list apply strategy")</c>. That throw is the
        /// GeoFacilityComponent[] resync-storm shape: the host ships the field, the client throws on every
        /// apply, and a failed apply drives RequestResync.
        ///
        /// ONE predicate, three callers: the classifier (a field with no strategy is Excluded, not
        /// licensed), the harness's L1 law check, and the coverage report's <c>apply=</c> column. The ladder
        /// used to be restated in each of those places — and two independently-written copies of the
        /// keyability question is exactly how GeoItem ended up classified by a table that disagreed with
        /// IdentityResolver (see IdentityResolver.TypeKeyable). Same question, asked once.
        ///
        /// Declared-type only, deliberately: classification has no instance. ApplyList itself dispatches on
        /// the RUNTIME container (<c>current is IList</c>), which can only ever be MORE capable than the
        /// declared type — so a null here is a real dead end, never a false alarm.</summary>
        internal static string ListApplyStrategy(RailField f)
        {
            var vt = f.ValueType;
            if (!vt.IsArray && typeof(IList).IsAssignableFrom(vt)) return "IList";
            // Mirrors ApplyList's interface-first probe: an explicit ICollection<T>.Add (LinkedList<T>) is
            // invisible to a name probe on the concrete type, so asking the INTERFACE is what keeps this
            // from missing a strategy the applier actually has.
            if (!vt.IsArray && f.ElemType != null &&
                typeof(ICollection<>).MakeGenericType(f.ElemType).IsAssignableFrom(vt)) return "ICollection<T>";
            if (!vt.IsArray && f.ElemType != null &&
                HarmonyLib.AccessTools.Method(vt, "Clear") != null &&
                HarmonyLib.AccessTools.Method(vt, "Add", new[] { f.ElemType }) != null) return "Clear+Add";
            if (vt.IsArray && f.IsWritable()) return "array-assign";
            return null;
        }

        /// <summary>In-place list rebuild (the game exposes most lists by reference); assignment fallback.
        /// Shared by the client applier (top-level LeafList/EntityList fields) and the blob codec
        /// (nested lists on freshly constructed elements).</summary>
        internal static void ApplyList(object entity, RailField field, List<object> items)
        {
            // Unresolved EntityRef/DefRef elements decode to the Unresolved sentinel (referent not spawned /
            // def unknown on the client) — it can never be added to a typed live list, and a genuine null
            // element in an entity/def list can NRE native code that dereferences elements. Drop both kinds
            // of hole rather than inserting them (the structural layer / a later diff re-adds them once
            // resolvable).
            if (items != null)
            {
                items.RemoveAll(it => ReferenceEquals(it, Unresolved));
                if (IdentityResolver.IsRootEntityType(field.ElemType) || typeof(BaseDef).IsAssignableFrom(field.ElemType))
                    items.RemoveAll(it => it == null);
            }
            var current = field.GetValue(entity);
            if (current == null && items == null) return; // both absent — nothing to reconcile
            bool created = false;
            if (current == null && !field.ValueType.IsArray && field.IsWritable())
            {
                // Container never constructed on this instance: blob elements are built via the
                // parameterless ctor, so ctor-assigned containers arrive null (ManufacturableItem.
                // ManufacturePrice/ScrapReward = ResourcePack). Construct one generically, fill it
                // below through the normal strategies, then attach it to the field.
                try { current = Activator.CreateInstance(field.ValueType, true); created = true; }
                catch { /* no parameterless ctor — falls through to the strategy error below */ }
            }
            if (current is IDictionary dict)
            {
                // Whole-dict blob (ElemType = KVP): items ARE the entries — rebuild directly.
                if (IsKvpType(field.ElemType))
                {
                    var kProp = field.ElemType.GetProperty("Key");
                    var vProp = field.ElemType.GetProperty("Value");
                    dict.Clear();
                    if (items != null)
                        foreach (var it in items)
                        {
                            if (it == null) continue;
                            var k = kProp.GetValue(it, null);
                            if (k != null) dict[k] = vProp.GetValue(it, null);
                        }
                    return;
                }
                // Live Dictionary twin of a DTO List (TwinTypeCompatible's dict rung): rebuild entries
                // keyed by the element's own key member — the same mapping the game's Process side uses
                // (_records = EncounterRecords.ToDictionary(s => s.EventId)). Key member licensed at
                // resolve time; a null key element is dropped (no addressable slot), like the game's own
                // dict rebuilds. Clear+re-add keeps the LIVE container instance (aliases survive).
                var da = GenericInterfaceArgs(current.GetType(), typeof(IDictionary<,>));
                var keyMi = da == null ? null : DictKeyMember(field.ElemType, da[0]);
                if (keyMi == null) throw new InvalidOperationException("no dict key member for " + field.ElemType.Name);
                dict.Clear();
                if (items != null)
                    foreach (var it in items)
                    {
                        if (it == null) continue;
                        var k = GetMemberValue(keyMi, it);
                        if (k != null) dict[k] = it;
                    }
                return;
            }
            if (current is IList list && !(current is Array))
            {
                list.Clear();
                if (items != null) foreach (var it in items) list.Add(it);
                if (created) field.SetValue(entity, current);
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
                    if (created) field.SetValue(entity, current);
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
