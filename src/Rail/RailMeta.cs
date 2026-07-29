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
        TimeSpanTicks = 8, Vector3 = 9, Quaternion = 10, DefRef = 11, EntityRef = 12, Composite = 13,
        DateTimeTicks = 14
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
        internal MemberInfo HopFi;   // alias chain "Hop.Member": entity → hop object (class) → Fi/Pi.
                                     // FIELD or PROPERTY: the game exposes carriers through auto-properties
                                     // (GeoActor.Surface, decompile GeoVehicle.cs:89) — a field-only hop left
                                     // GeoVehicle.SurfacePos/SurfaceRot "dto-twin unresolved", i.e. a client
                                     // aircraft that never moved. Read-only here (writes land on Fi/Pi).
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
            if (HopFi != null && (o = RailMeta.GetMemberValue(HopFi, o)) == null) return null;
            var v = (_get ?? (_get = RailMeta.BuildGetter(this)))(o);
            if (v == null) return null;
            if (WrapFi != null) return WrapFi.GetValue(v);
            if (FactionRef) return RailMeta.DefOfFaction(v, ValueType);
            return v;
        }

        public void SetValue(object o, object v)
        {
            if (HopFi != null && (o = RailMeta.GetMemberValue(HopFi, o)) == null) return; // hop not initialised — nothing to write into
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
            var raw = new List<(string name, Type valType, MemberInfo live, string alias, string fail, MemberInfo hop)>();
            foreach (var dtoMi in RailMeta.SerializedMembers(ser, dto))
            {
                var valType = RailMeta.MemberType(dtoMi);
                var liveMi = RailMeta.ResolveLive(live, dtoMi.Name, valType, out var alias, out var hop);
                raw.Add((dtoMi.Name, valType, liveMi, alias, liveMi == null ? "dto-twin unresolved" : null, hop));
            }
            foreach (var e in raw.OrderBy(e => e.name, StringComparer.Ordinal))
            {
                if (rt._byName.ContainsKey(e.name)) continue; // hierarchy duplicate — first wins
                var f = BuildField(live, e.name, e.valType, e.live, e.alias, e.fail, e.hop);
                rt.Fields.Add(f);
                rt._byName[e.name] = f;
            }
            BridgedCache[key] = rt;
            return rt;
        }

        private static RailType Build(Type t, Serializer ser)
        {
            var rt = new RailType { Type = t, Fields = new List<RailField>(), _byName = new Dictionary<string, RailField>(StringComparer.Ordinal) };
            var raw = new List<(string name, Type valType, MemberInfo live, string alias, string fail, MemberInfo hop)>();

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
                var f = BuildField(t, e.name, e.valType, e.live, e.alias, e.fail, e.hop);
                rt.Fields.Add(f);
                rt._byName[e.name] = f;
            }
            return rt;
        }

        /// <summary>Whole-dict blob: the dict rides as ONE canonical EntityList of KeyValuePair elements.
        /// Reached from the TWO declarations of the same state — a real IDictionary member, and the
        /// List&lt;KeyValuePair&lt;K,V&gt;&gt; a DTO records for it — so one classification serves both.
        /// Each side must be something the pair codec (the IsKvpType arm of EncodeValue/DecodeValue) can
        /// actually carry: a leaf, a leaf collection, or a blob-able class — and a class side husk-gates
        /// exactly like an EntityList element.</summary>
        private static RailField PairBlobField(RailField f, Type kt, Type vt)
        {
            if (!PairSideOk(kt) || !PairSideOk(vt))
            { f.Class = FieldClass.Excluded; f.Exclude = "dictionary with non-simple key/value (" + kt.Name + "," + vt.Name + ")"; return f; }
            var huskSide = HuskSideOf(kt) ?? HuskSideOf(vt);
            if (huskSide != null)
            { f.Class = FieldClass.Excluded; f.Exclude = "dict-blob husk on " + huskSide.Name + " (" + string.Join(",", RailMeta.HuskMembers(huskSide)) + ")"; return f; }
            f.Class = FieldClass.EntityList;
            f.ElemType = typeof(KeyValuePair<,>).MakeGenericType(kt, vt);
            f.KeyType = kt; f.DictValType = vt;
            f.Unordered = true; // dict enumeration order is not state
            return f;
        }

        private static bool PairSideOk(Type t) =>
            RailMeta.LeafKindOf(t, out _) || RailMeta.IsLeafCollection(t) ||
            (t.IsClass && RailMeta.HasPersistentMembers(t));

        private static Type HuskSideOf(Type t) =>
            !RailMeta.LeafKindOf(t, out _) && !RailMeta.IsLeafCollection(t) &&
            RailMeta.HuskMembers(t).Count > 0 ? t : null;

        private static RailField BuildField(Type owner, string name, Type valType, MemberInfo live, string alias, string fail, MemberInfo hop = null)
        {
            var f = new RailField { Name = name, ValueType = valType, LiveAlias = alias, Fi = live as FieldInfo, Pi = live as PropertyInfo, HopFi = hop };
            // DECLARED opt-out FIRST, ahead of the unresolved bail. An opt-out on a member no convention
            // resolves was DEAD CODE before (the bail below printed "bridge-unresolved" instead), which
            // makes such an exclusion a comment asserting an invariant rather than a mechanism: it would
            // arm itself silently the day the convention starts resolving. Keyed on the OWNER type (whose
            // table this is) + base types, never on the resolved member — there may be no member.
            var optOut = RailMeta.OptOutReason(owner, name);
            if (optOut != null) { f.Class = FieldClass.Excluded; f.Exclude = optOut; return f; }
            if (fail != null || live == null) { f.Class = FieldClass.Excluded; f.Exclude = fail ?? "no live member"; return f; }
            if (!f.CanRead) { f.Class = FieldClass.Excluded; f.Exclude = "unreadable"; return f; }
            if (RailMeta.IsPresentation(valType))
            { f.Class = FieldClass.Excluded; f.Exclude = "presentation-only type (" + valType.Name + ")"; return f; }

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
                return PairBlobField(f, dictArgs[0], dictArgs[1]);
            }

            // Collection?
            var elem = RailMeta.ElemTypeOf(valType);
            if (elem != null)
            {
                if (RailMeta.IsPresentation(elem))
                { f.Class = FieldClass.Excluded; f.Exclude = "collection of presentation-only " + elem.Name; return f; }
                // A collection OF PAIRS is the same whole-dict blob under its other declaration: the game
                // records a dict whose key is not a member of its value as `dict.ToList()` —
                // List<KeyValuePair<K,V>> (GeoscapeEventSystem.RecordInstanceData:665/669
                // CustomVariables / RemoveEventsAfterTimers). Same wire shape, same apply route (ApplyList
                // dispatches on the LIVE container, dict or list), so it takes the same classification
                // rather than falling through to "un-keyable element KeyValuePair`2".
                if (RailMeta.IsKvpType(elem))
                {
                    var pa = elem.GetGenericArguments();
                    return PairBlobField(f, pa[0], pa[1]);
                }
                if (RailMeta.LeafKindOf(elem, out _))
                {
                    f.Class = FieldClass.LeafList; f.ElemType = elem;
                    f.Unordered = valType.IsGenericType && valType.GetGenericTypeDefinition() == typeof(HashSet<>);
                    if (!f.IsWritable() && valType.IsArray) { f.Class = FieldClass.Excluded; f.Exclude = "read-only array"; }
                    return f;
                }
                // Value-element collection: the DECLARED element type is not blob-reconstructible (an
                // interface — no Activator, and EncodeValue refuses a runtime subtype), but the element's
                // whole state is a def address + a few ints, so the list rides ORDINALLY through
                // GeoItemCodec's record (see IsValueElementType). Husk-gating below does not apply: nothing
                // is Activator-rebuilt, the element is reconstructed through its PUBLIC ctor from the def —
                // exactly like a storage-dict entry. Without this arm the field falls to the "un-keyable
                // element" exclusion and everything under it is dropped in silence (AmmoManager).
                if (GeoItemCodec.IsValueElementType(elem))
                {
                    f.ElemType = elem;
                    f.Unordered = !(valType.IsArray ||
                                    (valType.IsGenericType && valType.GetGenericTypeDefinition() == typeof(List<>)));
                    if (RailMeta.ListApplyStrategy(f) == null)
                    { f.Class = FieldClass.Excluded; f.Exclude = "no list apply strategy (" + valType.Name + ")"; return f; }
                    f.Class = FieldClass.EntityList;
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

            // ─── The "GL" root (GeoLevelController via GeoLevelInstanceData) ───
            // Keyed on the LIVE owner, so they hold whether or not a convention resolves the member —
            // which is the point: all three are landmines that arm themselves the moment one does.
            //
            // TacUnits: DTO List<IGeoTacUnit> (GeoLevelInstanceData.cs:30) against the live registry
            // `private readonly Dictionary<GeoTacUnitId, IGeoTacUnit> _tacUnits` (GeoLevelController.cs:162).
            // The `_name` backing-field rung finds it and TwinTypeCompatible's live-dict-from-DTO-list rung
            // licenses that shape whenever DictKeyMember can re-derive the key (IGeoTacUnit.Id) — and then
            // the whole registry would be rebuilt from an EntityList blob: husked GeoCharacters replacing
            // live ones, colliding head-on with the "U#" STRUCTURAL applier that owns exactly this
            // dictionary (GenericApplier ApplyCharacterCreate / _tacUnits[unit.Id] = unit).
            { "PhoenixPoint.Geoscape.Levels.GeoLevelController.TacUnits", "the U# root registry — identity is STRUCTURAL (law 3); a value-rebuild from an EntityList blob would husk live GeoCharacters (GeoLevelController.cs:162 _tacUnits)" },
            // ModData: RCA gap B7, dangerous on BOTH ends, never as a side effect of this batch.
            // RECORD calls every mod's RecordGeoscapeInstanceData per walk (ModManager.cs:730-744 — TFTV
            // allocates a JSON graph and logs a line, TFTVGeoscape.cs:224-226); APPLY is the load-shaped
            // ProcessGeoscapeInstanceData over ALL mods at once (ModManager.cs:746-767), which TFTV
            // Harmony-patches (TFTVCriticalStuff.cs:111-151). Needs a change-driven cadence + per-mod
            // opt-in of its own.
            { "PhoenixPoint.Geoscape.Levels.GeoLevelController.ModData", "other mods' geoscape state — needs its own cadence + per-mod opt-in (record calls every mod per walk, apply is load-shaped ProcessGeoscapeInstanceData; RCA gap B7)" },
            // ContextHelpData resolves EXACTLY by name and type (GeoLevelController.cs:318), so it would
            // ride by accident: per-peer hint/tutorial progress, i.e. presentation — mirroring the host's
            // would hijack each player's own context help.
            { "PhoenixPoint.Geoscape.Levels.GeoLevelController.ContextHelpData", "per-peer context-help/hint progress (presentation) — each player owns their own (GeoLevelController.cs:318)" },
            // GeoscapeLog resolves onto live `Log` (GeoLevelController.cs:316) and its payload is the
            // KEYLESS `List<GeoscapeLogEntry> _entries` (GeoscapeLog.cs:36-37) — so it rides as ONE blob
            // that REBUILDS every entry with Activator + table fields. The entry's whole CONTENT is two
            // LocalizedTextBind members (GeoscapeLogEntry.cs:11-13) which the rail refuses by law (L11
            // def-laundering vector; both print "presentation-only" in this baseline), so a rebuilt entry
            // comes back with EventDate + HighPriority and Text = null — and LocalizedTextBind is a CLASS,
            // so GeoscapeLogEntry.GenerateMessage:23-25 NREs on it (its catch is FormatException only).
            // That is the 7ef0a30 ResearchElement husk exactly (DiffEngine's keyless-list refusal). The
            // husk gate does NOT catch it: HuskScan counts every GetSerializedMembers name as carried
            // (RailMeta.cs:2020-2023), so a rail-EXCLUDED ReadWrite member is never reported as a husk.
            // Mirroring the log needs a text-key codec for LocalizedTextBind first — its own batch.
            { "PhoenixPoint.Geoscape.Levels.GeoLevelController.GeoscapeLog", "keyless List<GeoscapeLogEntry> blob whose whole content is rail-refused LocalizedTextBind — rebuilt entries carry Text=null and GenerateMessage() NREs (GeoscapeLogEntry.cs:11-13/:23-25); needs a text-key codec first" },
        };

        /// <summary>Exclusion reason for an explicitly opted-out member, or null when it rides normally.
        /// Walks base types (same move as the <c>_twinAliases</c> lookup): a table is built for the
        /// DERIVED type but a row is written against the declaration that owns the member
        /// (ActorInstanceData.TimingData is inherited by GeoSiteInstaceData/GeoVehicleInstanceData).</summary>
        internal static string OptOutReason(Type owner, string name)
        {
            for (var cur = owner; cur != null && cur != typeof(object); cur = cur.BaseType)
                if (_optOutMembers.TryGetValue(cur.FullName + "." + name, out var why)) return why;
            return null;
        }

        /// <summary>Waiver CLASS marker. The reason string IS the class — the same mechanism RailCheck's
        /// L15 husk sweep already classifies with ("self-heal*" / "null at rest"), one rung finer: this
        /// names WHO heals, the LIVE OWNER's own post-read callback (AbilityTrackSlot.AbilityTrack above).
        ///
        /// Such a member heals for free while its owner is a blob LOCAL — <see cref="DecodeEntityList"/>
        /// fires post-read on every object the decode CONSTRUCTED. It heals for NOBODY when the list is
        /// applied onto a LIVE owner reached by Descend, which is the one case <see cref="ApplyList"/>
        /// now covers. RailCheck L16 is the belt: an owner-backref waiver on a live-Descend-reachable
        /// owner WITHOUT this marker is a law violation.</summary>
        internal const string OwnerPostReadWaiver = "owner's PostRead";

        private static readonly Dictionary<Type, bool> _ownerPostReadCache = new Dictionary<Type, bool>();

        /// <summary>Does rebuilding a list of this element type leave holes only the LIVE owner's post-read
        /// can fill — i.e. does any husk member of it carry an <see cref="OwnerPostReadWaiver"/> waiver?</summary>
        internal static bool OwnerPostReadWaived(Type elem)
        {
            if (elem == null) return false;
            if (_ownerPostReadCache.TryGetValue(elem, out var v)) return v;
            if (GameSerializer == null) return false; // pre-init: HuskScan is empty — don't cache the miss
            v = false;
            foreach (var m in HuskScan(elem))
                if (m.Waiver != null && m.Waiver.IndexOf(OwnerPostReadWaiver, StringComparison.OrdinalIgnoreCase) >= 0)
                { v = true; break; }
            _ownerPostReadCache[elem] = v;
            return v;
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
            // RECORD-CONTRACT rung — the type's OWN parameterless RecordInstanceData() when its return
            // type is an IInstanceData. The game's own persistence contract, so no name list and no
            // per-type code: it reaches DTOs the name conventions cannot (GeoLevelController's DTO is
            // PhoenixPoint.Geoscape.GeoLevelInstanceData — different NAME and different NAMESPACE, and
            // GeoLevelController declares no nested *InstanceData either, so both probes miss).
            // TYPE-LEVEL ONLY: ReturnType is read, the method is NEVER invoked. Invoking it would record
            // every faction + the event system and call every MOD's RecordGeoscapeInstanceData
            // (GeoLevelController.cs:389-393, :420) once per walk — RCA gap B7's flood.
            // AccessTools.Method with an EXACT empty param list: the overload that takes arguments is a
            // different contract (a DTO handed in), and matching it here would read the wrong ReturnType.
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var rec = HarmonyLib.AccessTools.Method(cur, "RecordInstanceData", new Type[0])?.ReturnType;
                if (rec != null && rec != t && typeof(IInstanceData).IsAssignableFrom(rec)) return rec;
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
            // The silent-swallow line: the GAME says this type persists (it has a record contract) and
            // NO probe reached its DTO — so it classifies with 0 members and VisitEntity drops it with a
            // "no persistent members" incident that only ever lands in rail-coverage.txt, and only if it
            // is a declared root. Once per type; the guard keeps it to the handful of types that really
            // declare RecordInstanceData (never the thousands that do not).
            if (_noBridgeLogged.Add(t))
            {
                var rec = HarmonyLib.AccessTools.Method(t, "RecordInstanceData", new Type[0])?.ReturnType;
                if (rec != null)
                    Debug.LogWarning("[Multiplayer][rail] FindBridge(" + t.FullName + "): no *InstanceData sibling/nested, and RecordInstanceData() returns " +
                                     rec.FullName + " which is not IInstanceData — this type classifies with 0 persistent members");
            }
            return null;
        }

        private static readonly HashSet<Type> _noBridgeLogged = new HashSet<Type>();

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
            // The two carriers of an aircraft's POSITION on the globe. Not presentation: GeoActor.cs:25
            // declares `WorldPosition => Surface.position`, so this member IS the actor's world position
            // for gameplay and UI alike, and the mesh hangs off the same transform (VisualsRoot =
            // Surface.gameObject, GeoVehicle.cs:316). World-space setters, so the client's stale
            // PivotTransform (which is what nav rotates to fly, GeoNavComponent.cs:111) cannot skew them.
            // Both hops go through the auto-property GeoActor.Surface — a PROPERTY hop, which is why
            // these rows only work alongside the MemberInfo hop above.
            { "PhoenixPoint.Geoscape.Entities.GeoVehicle.SurfacePos", "Surface.position" }, // GeoVehicle.RecordInstanceData:1052 / ProcessInstanceData:1077
            { "PhoenixPoint.Geoscape.Entities.GeoVehicle.SurfaceRot", "Surface.rotation" }, // GeoVehicle.RecordInstanceData:1053 / ProcessInstanceData:1078 — world rotation carries the rendered heading (UpdateHeading writes Surface.localEulerAngles, GeoNavComponent.cs:213)
            { "PhoenixPoint.Geoscape.Entities.GeoSite.OwnerFactionDef", "Owner" },        // GeoSite.ProcessInstanceData:1552 — def⇄faction via FactionRef coercion
            { "PhoenixPoint.Geoscape.Events.GeoscapeEventSystem.EncounterRecords", "_records" },     // GeoscapeEventSystem.RecordInstanceData:666 (_records.Values.ToList())
            { "PhoenixPoint.Geoscape.Events.GeoscapeEventSystem.SupressEvents", "SuppressEvents" },  // GeoscapeEventSystem.RecordInstanceData:667 (game typo in the DTO member)
            { "PhoenixPoint.Geoscape.Events.GeoscapeEventSystem.RemoveEventsAfterTimers", "_removeEventsAfterTimerExpires" }, // GeoscapeEventSystem.RecordInstanceData:669 — DTO name is not the storage name and no convention reaches it
        };

        /// <summary>Resolve a bridge member name onto the live type: explicit twin alias first (the game's
        /// own DTO→live mapping), then same name with a COMPATIBLE type (exact / one-field wrapper struct /
        /// GeoFaction-for-def / same-element collection), then the `_name` backing-field convention (the
        /// game exposes storage through read-only or shape-changed properties: _weapons/_modules/_addons),
        /// then the UNIQUE live member of the identical class type (catches renames like
        /// ManufactureQueue → Manufacture).</summary>
        internal static MemberInfo ResolveLive(Type live, string name, Type valType, out string alias, out MemberInfo hop)
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

        /// <summary>An alias target, optionally one hop deep ("Stats.HitPoints", "Surface.position").
        /// The hop must be a class-typed FIELD or PROPERTY — writes go through the hop object by
        /// reference, so a value-typed hop would write into a copy and is refused. A property hop is
        /// not a special case for vehicles: the game routinely exposes a carrier as an auto-property
        /// (GeoActor.Surface — decompile GeoVehicle.cs:89), and a field-only hop silently resolved to
        /// nothing there ("dto-twin unresolved") instead of failing visibly.
        /// ponytail: single hop only; extend to a chain when a real mapping needs one.</summary>
        private static MemberInfo ResolveAliasChain(Type live, string target, Type valType, out MemberInfo hop)
        {
            hop = null;
            int dot = target.IndexOf('.');
            if (dot < 0)
            {
                var m = (MemberInfo)HarmonyLib.AccessTools.Field(live, target) ?? HarmonyLib.AccessTools.Property(live, target);
                return m != null && TwinTypeCompatible(MemberType(m), valType) ? m : null;
            }
            var hopName = target.Substring(0, dot);
            hop = (MemberInfo)HarmonyLib.AccessTools.Field(live, hopName) ?? HarmonyLib.AccessTools.Property(live, hopName);
            if (hop == null) return null;
            var hopType = MemberType(hop);
            if (hopType.IsValueType) { hop = null; return null; }
            var leaf = target.Substring(dot + 1);
            var mm = (MemberInfo)HarmonyLib.AccessTools.Field(hopType, leaf) ?? HarmonyLib.AccessTools.Property(hopType, leaf);
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
            // …or a DTO List<KeyValuePair<K,V>> — the game's OTHER dict record shape, used when the key is
            // NOT a member of the value so nothing could re-derive it (GeoscapeEventSystem.
            // RecordInstanceData:665 `_customVariables.ToList()`, :669 the same for the timer→events map).
            // The pairs carry the key themselves, so no DictKeyMember is needed; BuildField classifies both
            // declarations into the identical whole-dict blob (PairBlobField).
            var da = GenericInterfaceArgs(liveT, typeof(IDictionary<,>));
            if (da != null)
                return de == typeof(KeyValuePair<,>).MakeGenericType(da[0], da[1]) ||
                       (da[1] == de && DictKeyMember(de, da[0]) != null);
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

        /// <summary>A rebuildable collection of LEAF elements (List&lt;string&gt;) — carryable as a naked
        /// VALUE by the TagLeafList arm of EncodeValue/DecodeValue, not only as a LeafList FIELD. Non-array
        /// IList only: decode reconstructs it with the parameterless ctor + Add.</summary>
        internal static bool IsLeafCollection(Type t)
        {
            if (t.IsArray || !typeof(IList).IsAssignableFrom(t)) return false;
            var e = ElemTypeOf(t);
            return e != null && LeafKindOf(e, out _);
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
            // DateTime rides ticks for the SAME reason TimeSpan does, and must be asked BEFORE the
            // Composite gate below: its only state is a private `_dateData` ulong that the classifier
            // does not reach, so the gate found "no Leaf field left" and every DateTime in the game fell
            // out as "no persistent members (DateTime)". The visible casualty was Base.Utils.UnityDateTime
            // — a class whose ONE serialized member is a DateTime (decompile UnityDateTime.cs:13-14), so it
            // classified covered=0/1: a walk-into with nothing to carry. That is what made
            // GeoMission.GlobalTime unmirrorable (RailCheck L29 nullable-unenabled) — enabling its
            // structural create without this would have built an empty UnityDateTime whose value never
            // arrives, i.e. a phantom 01/01/0001 mission clock. Ticks are the entire value.
            if (t == typeof(DateTime)) { kind = LeafKind.DateTimeTicks; return true; }
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
                case LeafKind.DateTimeTicks: w.Write(((DateTime)v).Ticks); break;
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
                case LeafKind.DateTimeTicks: return new DateTime(r.ReadInt64());
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
        // being applied.
        //
        // That was once claimed sufficient because "AbilityTrack is itself a blob element, so nothing ever
        // descends INTO an AbilityTrack". FALSE: GeoUnitDescriptor._personalAbilityTrack is Descend, and
        // the descriptor is reachable LIVE through the NewRecruit/DismambleUnit bridges — so the live
        // AbilityTrack gets its slots array-assigned with no post-read anywhere, and every fresh slot keeps
        // a NULL .AbilityTrack backref (UICharacterProgressionUtl:12, HumanAbilityTrackContainer:24,
        // CharacterProgression:164/178 all dereference it). ApplyList now fires the live owner's post-read
        // for exactly the element types whose waiver names that owner post-read as the healer
        // (OwnerPostReadWaiver / OwnerPostReadWaived); RailCheck L16 asserts the two stay in step.
        //
        // Firing the live owner's post-read UNCONDITIONALLY stays REFUSED. Those callbacks are
        // save-load migrations, not re-link hooks: GeoCharacter.InitAfterDeserialiaztion (decompile
        // GeoCharacter.cs:1592) branches on serObj.SerializedVersion — which we have no honest value for —
        // and can call Init() and SetItems(), i.e. real gameplay side-effects on the client's authoritative
        // mirror, which law 3 forbids. Research.OnPostSerialize (Research.cs:1264) is the same shape.
        // The only owner post-read in the closure that would even run is CharacterProgression.Init(), which
        // just re-assigns a callback it already holds. Zero benefit, one law violated.

        internal const byte EntityListMarker = 15; // distinct from LeafKinds 0-13 and ListMarker 14

        private const byte TagNull = 0, TagLeaf = 1, TagBlob = 2, TagBackRef = 3, TagList = 4, TagLeafList = 5,
                           TagLeafDict = 6, TagValueItem = 7;

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

        private static readonly string[] _noCreateParams = new string[0];

        private static Type MemberTypeOf(MemberInfo mi) =>
            mi is FieldInfo fi ? fi.FieldType : (mi as PropertyInfo)?.PropertyType;

        /// <summary>The `[SerializeCustomCreate]` param members of <paramref name="t"/> that the structural
        /// create frame CANNOT carry, as "name (Type)" — empty when it can carry them all (the normal case).
        ///
        /// Create params are WriteOnly members, so the value rail will never fill them
        /// (<c>SerializedMembers</c> is ReadWrite-only) — the create packet is the only chance. It takes
        /// that chance by encoding each one with the ordinary leaf codec
        /// (<see cref="EncodeDescendCreate"/>), which is exactly why the honest limit is now the LEAF
        /// codec's reach and no longer "a type-name payload carries nothing": a param is carriable iff
        /// <see cref="LeafKindOf"/> knows its declared type. That covers every param the rail has met —
        /// the three missions' `_enemyFaction` is a `PPFactionDef`, i.e. a `BaseDef`, i.e. a DefRef Guid
        /// (decompile GeoAmbushMission.cs:15, GeoScavengingMission.cs:14,
        /// GeoPhoenixBaseDefenseMission.cs:17) — and an entity-typed param would ride as an EntityRef
        /// stable id, never an embedded graph.
        ///
        /// What stays uncarriable is a param typed as a plain composite CLASS with no stable id: there is
        /// nothing to name it by. ONE predicate (<see cref="CreateInfoOf"/> + <see cref="LeafKindOf"/>)
        /// behind the host's encode warning, the applier's log line and RailCheck L29, so the three cannot
        /// drift apart.</summary>
        internal static string[] UncarriableCreateParams(Type t)
        {
            var ser = GameSerializer;
            var ci = ser == null ? null : CreateInfoOf(ser, t);
            if (ci == null || ci.Params.Length == 0) return _noCreateParams;
            List<string> bad = null;
            foreach (var mi in ci.Params)
            {
                var mt = mi == null ? null : MemberTypeOf(mi);
                if (mt != null && LeafKindOf(mt, out _)) continue;
                (bad = bad ?? new List<string>()).Add((mi?.Name ?? "?") + " (" + (mt?.Name ?? "?") + ")");
            }
            return bad == null ? _noCreateParams : bad.ToArray();
        }

        /// <summary>The structural CREATE payload for a Descend field (<c>DiffEngine.IsDescendPath</c>):
        /// <c>[string typeName][byte n][leaf x n]</c>, where the n values are the live instance's
        /// `[SerializeCustomCreate]` param members read through the SAME <see cref="CreateInfoOf"/> table
        /// the client decodes against.
        ///
        /// A type NAME rather than a native graph blob, deliberately and still: a mission's serialized
        /// members include `Site` (GeoSite) and `_stealAircraft` (GeoVehicle), both MonoBehaviour-bound
        /// actors with their own spawners, so decoding a blob would CREATE duplicate actors on the client
        /// (law 3). The params ride as leaves for the same reason — <see cref="EncodeLeaf"/> structurally
        /// CANNOT emit an object graph: its only recursive arm is Composite over a value-type's own leaf
        /// fields, a DefRef is a Guid string and an EntityRef is a stable game id
        /// (<c>IdentityResolver.RootRef</c>). So "carried as a reference, never as a graph" is a property
        /// of the mechanism here, not a rule a future caller has to remember.</summary>
        internal static byte[] EncodeDescendCreate(object instance)
        {
            var t = instance.GetType();
            var ser = GameSerializer;
            var ci = ser == null ? null : CreateInfoOf(ser, t);
            var ps = ci == null ? null : ci.Params;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms)) // default = UTF8, length-prefixed strings
            {
                w.Write(t.FullName);
                w.Write((byte)(ps == null ? 0 : ps.Length));
                for (int i = 0; ps != null && i < ps.Length; i++)
                {
                    var mt = ps[i] == null ? null : MemberTypeOf(ps[i]);
                    if (mt == null || !LeafKindOf(mt, out _))
                    {
                        // Law 1: the one thing this frame cannot carry is NAMED, never silently skipped.
                        // Still writes a slot, so the frame stays self-describing and the reader stays aligned.
                        WarnOnce("EncodeDescendCreate: " + t.Name + " create param '" + (ps[i]?.Name ?? ("#" + i)) +
                                 "' (" + (mt?.Name ?? "?") + ") is not leaf-encodable — shipped NULL");
                        w.Write((byte)LeafKind.Null);
                        continue;
                    }
                    EncodeLeaf(w, mt, GetMemberValue(ps[i], instance));
                }
                return ms.ToArray();
            }
        }

        /// <summary>The structural CREATE payload for a MonoBehaviour-bound ACTOR root (GeoVehicle today):
        /// the SAME <c>[string typeName][byte n][leaf x n]</c> frame as <see cref="EncodeDescendCreate"/>,
        /// read by the same <see cref="DescendCreateTypeName"/>, with n = 1. Only the param SOURCE differs,
        /// and it has to: an actor's `[SerializeCustomCreate]` is
        /// <c>ActorComponent.CreateActor(SerializedObjectData, ActorCreateData)</c> (decompile
        /// ActorComponent.cs:336-377, inherited by GeoVehicle.cs:26 -> GeoActor.cs:15), whose one param
        /// member is the get-only property <c>ActorComponent.ActorCreateData</c> (:51) typed as a plain
        /// composite CLASS (ActorCreateData.cs:8-15) — so <see cref="LeafKindOf"/> refuses it,
        /// <see cref="UncarriableCreateParams"/> names it, and <see cref="ConstructLikeLoad"/> would invoke
        /// <c>CreateActor(null, null)</c> and NRE on <c>ActorCreateData.ActorSetDef</c>.
        ///
        /// What the client actually needs is the ONE field of that composite the spawn branch reads:
        /// <c>ActorCreateData.ActorSetDef</c>, recorded as <c>GetComponent&lt;ComponentSet&gt;().SetDef</c>
        /// (ActorComponent.cs:323) and consumed as <c>Repo.Instantiate&lt;ActorComponent&gt;(ActorSetDef)</c>
        /// (:342). That is a <see cref="BaseDef"/>, i.e. a DefRef Guid, i.e. already a leaf — so the actor
        /// frame keeps the whole "carried as a reference, never as a graph" property of the mechanism, and
        /// never ships a native graph blob (law 3 / RailCheck L3: a blob of a GeoVehicle would re-CREATE
        /// MonoBehaviour actors on the client). Everything else about the actor rides elsewhere: its id and
        /// its owner are IN the root key (IdentityResolver.RootRef), every other member is a covered value
        /// arriving one packet behind the create.</summary>
        internal static byte[] EncodeActorCreate(object actor)
        {
            var t = actor.GetType();
            var setDef = ActorSpawnDef(actor);
            if (setDef == null)
                // Law 1: the one thing this frame cannot carry gets a line. A scene-PLACED actor has no
                // ComponentSetDef at all (ActorComponent.cs:323 records null when !IsSpawned) — load binds
                // it by SceneObjectId instead, which a client cannot do for an object its scene lacks.
                WarnOnce("EncodeActorCreate: " + t.Name + " has no ComponentSet.SetDef (scene-placed actor?) — " +
                         "the create frame ships NULL and the client will decline it");
            return WriteActorCreateFrame(t.FullName, setDef);
        }

        /// <summary>The actor frame's WRITER, split out from the live read above so the stage-1 harness can
        /// drive encoder→decoder for real (RailCheck L31) without a live Unity actor — the arm a static
        /// predicate over <see cref="LeafKindOf"/> structurally cannot supply, exactly as L30 is to L29.</summary>
        internal static byte[] WriteActorCreateFrame(string typeName, ComponentSetDef setDef)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms)) // default = UTF8, matching EncodeDescendCreate's writer
            {
                w.Write(typeName);
                w.Write((byte)1);
                EncodeLeaf(w, typeof(ComponentSetDef), setDef);
                return ms.ToArray();
            }
        }

        /// <summary>The `ComponentSetDef` the game spawns this actor from — the live read behind
        /// <see cref="EncodeActorCreate"/>, kept next to it so the host encode and RailCheck L31 ask the
        /// same question. Null for a scene-placed actor (see the warning above).</summary>
        internal static ComponentSetDef ActorSpawnDef(object actor) =>
            (actor as UnityEngine.Component)?.GetComponent<ComponentSet>()?.SetDef;

        /// <summary>Decode side of <see cref="EncodeActorCreate"/>: the spawn def, or null when the frame
        /// carried none / did not resolve on this peer. Every null says so once — a frame that silently
        /// decoded to null is how an actor would be spawned from a wrong def, or not at all.</summary>
        internal static ComponentSetDef DecodeActorCreateDef(byte[] payload, GeoLevelController geo)
        {
            if (payload == null || payload.Length == 0) return null;
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms)) // default = UTF8, matching EncodeActorCreate's writer
                {
                    var typeName = r.ReadString();
                    int n = ms.Position < ms.Length ? r.ReadByte() : -1;
                    if (n != 1)
                    {
                        WarnOnce("DecodeActorCreateDef: " + typeName + " frame declares " + n +
                                 " create-param slot(s), expected 1 — not an EncodeActorCreate frame; spawn declined");
                        return null;
                    }
                    var v = DecodeLeaf(r, typeof(ComponentSetDef), geo);
                    if (v == Unresolved)
                    {
                        WarnOnce("DecodeActorCreateDef: " + typeName + " spawn def did not resolve on this peer " +
                                 "(mod parity?) — spawn declined");
                        return null;
                    }
                    return v as ComponentSetDef;
                }
            }
            catch (Exception ex)
            {
                WarnOnce("DecodeActorCreateDef: create frame unreadable (" + ex.GetType().Name + ") — spawn declined");
                return null;
            }
        }

        /// <summary>Leading type name of an <see cref="EncodeDescendCreate"/> or
        /// <see cref="EncodeActorCreate"/> frame — the client resolves and validates it before constructing
        /// anything. ONE reader for both, because both write the same leading string.</summary>
        internal static string DescendCreateTypeName(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return null;
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms)) // default = UTF8, matching EncodeDescendCreate's writer
                    return r.ReadString();
            }
            catch { return null; }
        }

        /// <summary>True when <paramref name="t"/> is built through a `[SerializeCustomCreate]` static that
        /// takes params — i.e. when the create frame has something to carry and RailCheck L30 must see it
        /// carried.</summary>
        internal static bool HasCreateParams(Type t)
        {
            var ser = GameSerializer;
            var ci = ser == null ? null : CreateInfoOf(ser, t);
            return ci != null && ci.Params.Length > 0;
        }

        /// <summary>The create args carried by an <see cref="EncodeDescendCreate"/> frame, positional and
        /// sized by <paramref name="t"/>'s own create table. Null means "carried nothing usable" and the
        /// caller falls back to nulls — every such path says so once, because a frame that silently
        /// decoded to nulls is precisely how `_enemyFaction` used to arrive empty.
        ///
        /// The slot count is CHECKED against the create table rather than trusted: a frame that did not
        /// come from <see cref="EncodeDescendCreate"/> (a reverted payload shape, a peer built from other
        /// sources) would otherwise read a bogus length prefix and hand the game's create method garbage.
        /// An unresolvable ref decodes to the <see cref="Unresolved"/> sentinel, which must NOT reach that
        /// method either — it becomes null, named.</summary>
        internal static object[] DecodeCreateArgs(byte[] payload, Type t, GeoLevelController geo)
        {
            var ser = GameSerializer;
            var ci = ser == null ? null : CreateInfoOf(ser, t);
            if (ci == null || payload == null || payload.Length == 0) return null;
            var args = new object[ci.Params.Length];
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms)) // default = UTF8, matching EncodeDescendCreate's writer
                {
                    r.ReadString(); // type name — already resolved by the caller
                    int n = ms.Position < ms.Length ? r.ReadByte() : -1;
                    if (n != ci.Params.Length)
                    {
                        WarnOnce("DecodeCreateArgs: " + t.Name + " frame declares " + n + " create-param slot(s) but " +
                                 "the type takes " + ci.Params.Length + " — not an EncodeDescendCreate frame; params passed NULL");
                        return null;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        var mi = ci.Params[i];
                        var mt = mi == null ? null : MemberTypeOf(mi);
                        var v = DecodeLeaf(r, mt ?? typeof(object), geo);
                        if (v == Unresolved)
                        {
                            WarnOnce("DecodeCreateArgs: " + t.Name + " create param '" + (mi?.Name ?? ("#" + i)) +
                                     "' did not resolve on this peer — passed NULL");
                            v = null;
                        }
                        args[i] = v;
                    }
                }
            }
            catch (Exception ex)
            {
                WarnOnce("DecodeCreateArgs: " + t.Name + " create frame unreadable (" + ex.GetType().Name +
                         ") — params passed NULL");
                return null;
            }
            return args;
        }

        /// <summary>Construct an instance the way the game's own LOAD does — the identical two rungs in
        /// the identical order as the blob codec (<see cref="DecodeObjectBody"/>): the type's
        /// `[SerializeCustomCreate]` static first, `Activator.CreateInstance(nonPublic)` only as the
        /// fallback. The first rung is load-bearing rather than a nicety: most `GeoMission` subclasses
        /// have NO parameterless ctor at all, and their custom create IS their construction path
        /// (decompile GeoHavenDefenseMission.cs:156-160 → `new GeoHavenDefenseMission(null, null, null)`).
        /// <paramref name="createArgs"/> = what <see cref="DecodeCreateArgs"/> carried, positionally;
        /// null (RailCheck's constructibility probe) still passes nulls, which is what the create methods
        /// already tolerate — `Init` is a bare assignment (GeoAmbushMission.cs:34-45). Throws
        /// (MissingMethodException) when neither rung applies; the caller logs and declines.</summary>
        internal static object ConstructLikeLoad(Type t, object[] createArgs = null)
        {
            var ser = GameSerializer;
            var ci = ser == null ? null : CreateInfoOf(ser, t);
            if (ci == null) return Activator.CreateInstance(t, true);
            var args = new object[ci.Params.Length + 1]; // [0] = SerializedObjectData
            for (int i = 0; createArgs != null && i < ci.Params.Length && i < createArgs.Length; i++)
                args[i + 1] = createArgs[i];
            return ci.Method.Invoke(args);
        }

        internal static object GetMemberValue(MemberInfo mi, object o) =>
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

        /// <summary>MEMBERSHIP half of the order-vector apply, for ALIAS collections only (elements owned
        /// by another container — research queue ⊂ catalog; structurally-owned elements ride the
        /// create/destroy set-diff and never come here). The vector is the host's FULL live key sequence,
        /// so it is authoritative for the SET too: prune local elements the host no longer lists, adopt
        /// missing keys as LIVE instances via <paramref name="resolveMissing"/> (null = not resolvable
        /// here yet — skipped, the next vector/resync retries). Raw list ops only — a projector mirrors
        /// membership, it never runs the native add/remove logic (law 3). Fixed-size containers (arrays)
        /// are permute-only by construction. Order is <see cref="ReorderByKeys"/>' job, run after this.</summary>
        internal static bool SyncMembersByKeys(object container, string[] keys, Func<string, object> resolveMissing)
        {
            if (!(container is IList list) || list.IsFixedSize || list.IsReadOnly) return false;
            bool changed = false;
            var want = new HashSet<string>(keys, StringComparer.Ordinal);
            for (int j = list.Count - 1; j >= 0; j--)
            {
                var k = IdentityResolver.KeyOf(list[j]);
                if (k == null || !want.Contains(k)) { list.RemoveAt(j); changed = true; }
            }
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in list) have.Add(IdentityResolver.KeyOf(e));
            foreach (var k in keys)
            {
                if (have.Contains(k)) continue;
                var inst = resolveMissing?.Invoke(k);
                if (inst == null) continue;
                list.Add(inst);
                have.Add(k);
                changed = true;
            }
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
            // Value element (GeoItemCodec.IsValueElementType): an ordinal (defGuid, count, charges,
            // malfunction) record instead of a graph blob. Declared-type only, so a GeoItem riding under
            // its OWN declared type (the character's item lists) keeps the full blob with its nested Ammo —
            // it is the INTERFACE-declared occupant (AmmoManager.LoadedMagazines) that has no other route.
            if (GeoItemCodec.IsValueElementType(declared))
            { w.Write(TagValueItem); GeoItemCodec.WriteRec(w, GeoItemCodec.RecOf(v)); return; }
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
            if (IsLeafCollection(declared))
            {
                // A leaf collection as a naked VALUE (a pair side, a create-param): the same shape
                // FieldClass.LeafList carries for a MEMBER, minus the RailField. Order is written as-is —
                // a naked value has no Unordered flag, and every occupant is a List<T> whose order the
                // game itself wrote (GeoscapeEventSystem._removeEventsAfterTimerExpires' List<string>).
                var le = ElemTypeOf(declared);
                var items = new List<object>();
                foreach (var e in (IEnumerable)v) items.Add(e);
                if (items.Count > ushort.MaxValue) throw new InvalidOperationException("leaf list too large (" + items.Count + ")");
                w.Write(TagLeafList);
                w.Write((ushort)items.Count);
                foreach (var e in items) EncodeLeaf(w, le, e);
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
                            // Host-side null = nothing to state: the field is omitted (absent ≠ null), and the
                            // decoder leaves the fresh element's own container alone. Symmetric with the
                            // TagLeafDict decode, which materializes only when entries actually arrive.
                            if (!(v is IDictionary dict)) break;
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
                // Unknown def is LOUD inside FromRec; the sentinel makes ApplyList drop the hole rather
                // than insert a null element into a list native code dereferences (AmmoManager.cs:119-127).
                case TagValueItem: return GeoItemCodec.FromRec(GeoItemCodec.ReadRec(r)) ?? Unresolved;
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
                case TagLeafList:
                {
                    var le = ElemTypeOf(declared);
                    if (le == null) throw new IOException("blob leaf-list tag on non-collection " + declared.Name);
                    int n = r.ReadUInt16();
                    var lst = Activator.CreateInstance(declared, true) as IList;
                    for (int i = 0; i < n; i++)
                    {
                        var e = DecodeLeaf(r, le, geo);
                        if (lst != null && !ReferenceEquals(e, Unresolved)) lst.Add(e);
                    }
                    return lst;
                }
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
                        // NEVER "as IDictionary" on the raw field: a dict the ctor did not build is null on
                        // a blob-constructed element, and dropping the entries into that null is the silent
                        // failure MaterializeContainer exists to end (its header carries the RCA).
                        var dict = MaterializeContainer(o, f) as IDictionary;
                        if (dict == null)
                        {
                            if (dn > 0)
                                Debug.LogError("[Multiplayer][rail] blob leaf-dict " + t.Name + "." + f.Name +
                                               ": no live dictionary — " + dn + " decoded entries DROPPED");
                        }
                        else dict.Clear();
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

        /// <summary>THE one answer to "this field's container is null on this instance" — for EVERY
        /// collection class and every consumer (blob decode, LeafDict/GeoItemDict apply, ApplyList).
        /// Materializes the declared container type and ATTACHES it, then returns the live instance.
        ///
        /// The premise it replaces ("the ctor already built one") is false and was failing in SILENCE:
        /// blob elements are built with the parameterless <c>Activator</c>, so a field the game only ever
        /// fills from a NON-default ctor arrives null — <c>GeoUnitDescriptor.ProgressionDescriptor.
        /// PersonalAbilities</c> (decompile GeoUnitDescriptor.cs:106, `public readonly Dictionary&lt;int,
        /// TacticalAbilityDef&gt;` with NO initializer, assigned only by the 2-arg ctor at :123-127) is the
        /// proven shape. Every decoded entry was dropped into a null check, the field stayed null, and the
        /// decode reported success — which surfaced as an NRE inside
        /// <c>AbilityTrack.CreateFromDictionary</c> under a MessageBox click handler, i.e. a client UI
        /// frozen with no way out (2026-07-29 recruit-hire RCA).
        ///
        /// <c>readonly</c> is not an obstacle: <see cref="FieldInfo.SetValue"/> writes initonly INSTANCE
        /// fields, which is exactly how the game's own serializer fills them (see
        /// <see cref="RailField.IsWritable"/>). Arrays return null quietly — the caller assigns those
        /// wholesale. Anything else that cannot be attached is a LOUD error, never a silent drop.</summary>
        /// <summary>The concrete behind a collection INTERFACE member — what LOAD itself puts there, and
        /// what the BCL defaults to. A member declared as an interface used to be unmaterializable
        /// outright, so the decoder dropped every entry it produced for one (L20):
        /// `GeoHavenDefenseMissionInstanceData.AttackingSites` is `IList&lt;GeoSite&gt;`
        /// (decompile GeoHavenDefenseMissionInstanceData.cs:20) and the applier could already ADD into it
        /// (`apply=ICollection&lt;T&gt;`) — it just had nothing to add INTO. `List&lt;T&gt;` is not a guess:
        /// the game's own save path assigns `_attackingSites.ToList()` (GeoHavenDefenseMission.cs:170) and
        /// its load path reads it back with `.ToList()` (:187). Null for anything else (non-generic
        /// interfaces, abstract classes) so the caller's refusal + error line stays intact.</summary>
        private static Type ConcreteCollection(Type it)
        {
            if (!it.IsGenericType) return null;
            var g = it.GetGenericTypeDefinition();
            var a = it.GetGenericArguments();
            if (g == typeof(IList<>) || g == typeof(ICollection<>) || g == typeof(IEnumerable<>) ||
                g == typeof(IReadOnlyList<>) || g == typeof(IReadOnlyCollection<>))
                return typeof(List<>).MakeGenericType(a);
            if (g == typeof(ISet<>)) return typeof(HashSet<>).MakeGenericType(a);
            if (g == typeof(IDictionary<,>) || g == typeof(IReadOnlyDictionary<,>))
                return typeof(Dictionary<,>).MakeGenericType(a);
            return null;
        }

        internal static object MaterializeContainer(object owner, RailField field)
        {
            if (owner == null) return null;
            var cur = field.GetValue(owner);
            if (cur != null) return cur;
            var vt = field.ValueType;
            if (vt.IsArray) return null; // whole-array assignment is the caller's strategy
            if (vt.IsInterface) vt = ConcreteCollection(vt) ?? vt;
            if (!field.IsWritable() || vt.IsAbstract || vt.IsInterface)
            {
                Debug.LogError("[Multiplayer][rail] container " + owner.GetType().Name + "." + field.Name +
                               " (" + vt.Name + ") is null and cannot be materialized (not writable / not concrete) — entries would be dropped");
                return null;
            }
            object made;
            try { made = Activator.CreateInstance(vt, true); }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][rail] container " + owner.GetType().Name + "." + field.Name +
                               " (" + vt.Name + ") is null and has no usable parameterless ctor: " + ex.Message + " — entries would be dropped");
                return null;
            }
            field.SetValue(owner, made);
            // Re-read rather than trust: a hop alias whose intermediate object is null makes SetValue a
            // no-op, and returning the detached instance would be the same silent drop one level down.
            var back = field.GetValue(owner);
            if (back == null)
                Debug.LogError("[Multiplayer][rail] container " + owner.GetType().Name + "." + field.Name +
                               " did not attach after materialization — entries would be dropped");
            return back;
        }

        /// <summary>In-place list rebuild (the game exposes most lists by reference); assignment fallback.
        /// Shared by the client applier (top-level LeafList/EntityList fields) and the blob codec
        /// (nested lists on freshly constructed elements).</summary>
        internal static void ApplyList(object entity, RailField field, List<object> items)
        {
            ApplyListCore(entity, field, items);
            // The ONE live-owner post-read, licensed per ELEMENT TYPE by the reviewed waiver table
            // (OwnerPostReadWaiver) — never per call site. A rebuilt element whose only healer is
            // "the owner's PostRead" is healed by nobody here: post-read fires over the blob's own
            // locals (DecodeEntityList) and `entity` is the LIVE owner, never a local. Concrete shape
            // this closes: AbilityTrack reached by live Descend (GeoUnitDescriptor._personalAbilityTrack,
            // DismambleUnit/NewRecruit bridges) array-assigns fresh AbilityTrackSlots whose .AbilityTrack
            // backref stays NULL — the 2026-07-26 recruit-screen NRE, one level up.
            // Every OTHER owner post-read stays refused (see the EntityList codec header). Re-firing on a
            // blob local (nested-list caller) is harmless: this waiver class IS the idempotent rebind.
            var ser = GameSerializer;
            if (ser != null && entity != null && OwnerPostReadWaived(field.ElemType))
                InvokePostReadSafe(ser, entity);
        }

        private static void ApplyListCore(object entity, RailField field, List<object> items)
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
            // Container never constructed on this instance: blob elements are built via the parameterless
            // ctor, so ctor-ARG-assigned containers arrive null (ManufacturableItem.ManufacturePrice/
            // ScrapReward = ResourcePack). Materialize AND attach through the one shared helper — the
            // attach used to be a `created` flag re-tested per strategy, and the dict route below simply
            // forgot it, so a null Dictionary field was filled and then thrown away.
            if (current == null) current = MaterializeContainer(entity, field);
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
