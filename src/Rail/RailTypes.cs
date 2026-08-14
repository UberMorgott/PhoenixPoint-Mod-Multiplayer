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
using Base.Entities.Statuses;
using Base.Serialization;
using Base.Serialization.General;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// The VOCABULARY of the rail metadata layer: the four types <see cref="RailMeta"/> builds its per-type
    /// tables out of, and every other rail file reads them through.
    ///
    ///   • <see cref="FieldClass"/> — how one field rides: a leaf value, a descended child, a keyed
    ///     collection, a canonical list, a per-subKey dict.
    ///   • <see cref="LeafKind"/> — the leaf codec's first byte. It shares its number space with the
    ///     EntityList / OrderVector / DictCensus markers, and RailCheck L7 asserts the disjointness.
    ///   • <see cref="RailField"/> — one field's wire identity plus the cached accessor that reads it.
    ///   • <see cref="RailType"/> — one type's whole table.
    ///
    /// These are DERIVED, not declared: <see cref="RailMeta"/> fills them from the game's own save-serializer
    /// metadata. Nothing here carries subsystem knowledge, and nothing here should — see RailMeta.cs for what
    /// the derivation is and why a field index is stable on both peers.
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
        DateTimeTicks = 14,
        // 15/16/17 are NOT free: EntityListMarker = 15, OrderVectorMarker = 16, DictCensusMarker = 17 all
        // live in the same first-byte space as a LeafKind, and RailCheck L7 asserts the disjointness.
        TextBind = 18
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
        internal MemberInfo[] HopFi; // alias chain "A.B.Member": entity → hop object → … → Fi/Pi.
                                     // FIELD or PROPERTY: the game exposes carriers through auto-properties
                                     // (GeoActor.Surface, decompile GeoVehicle.cs:89) — a field-only hop left
                                     // GeoVehicle.SurfacePos/SurfaceRot "dto-twin unresolved", i.e. a client
                                     // aircraft that never moved. Read-only here (writes land on Fi/Pi).
                                     // N hops, not one: the game's own Record/Process mapping reaches TWO
                                     // deep on the range carriers — GeoHaven.cs:1518/1369 and
                                     // GeoPhoenixBase.cs:1108/969 both read and write `X.Range.Range` — and a
                                     // single-hop resolver left every one of them "dto-twin unresolved"
                                     // (mist-repeller + site-scanner radius never mirrored at all).
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
            if (HopFi != null)
                foreach (var h in HopFi)
                    if ((o = RailMeta.GetMemberValue(h, o)) == null) return null;
            var v = (_get ?? (_get = RailMeta.BuildGetter(this)))(o);
            if (v == null) return null;
            if (WrapFi != null) return WrapFi.GetValue(v);
            if (FactionRef) return RailMeta.DefOfFaction(v, ValueType);
            return v;
        }

        public void SetValue(object o, object v)
        {
            // Any hop null = the carrier was never constructed (GeoHaven.MistRepeller is created lazily and
            // ONLY when the save carried a non-zero range, GeoHaven.cs:1362-1370) — nothing to write into.
            if (HopFi != null)
                foreach (var h in HopFi)
                    if ((o = RailMeta.GetMemberValue(h, o)) == null) return;
            if (WrapFi != null && v != null)
            {
                var boxed = Activator.CreateInstance(RailMeta.MemberType((MemberInfo)Fi ?? Pi));
                WrapFi.SetValue(boxed, v);
                v = boxed;
            }
            // REACTIVITY, at the one seam every mirrored value passes: see EchoStatChange. Read BEFORE the
            // write, echo AFTER it — a BaseStat carries its value in a plain field, so nothing else would.
            var stat = o as BaseStat;
            float before = stat == null ? 0f : (float)stat.Value;
            if (Fi != null) Fi.SetValue(o, v);
            else Pi.SetValue(o, v, null);
            if (stat != null) EchoStatChange(stat, before);
        }

        /// <summary>
        /// THE SEAM IS THE STAT ITSELF — the geoscape twin of <c>TacticalUiRepaint</c>'s postfix on
        /// <c>BaseStat.OnStatChange</c>, and the reason this file needs no per-panel repaint anywhere.
        ///
        /// <c>BaseStat.Value</c> is a public FIELD (decompile Base.Entities.Statuses/BaseStat.cs:21), so a
        /// mirrored write lands it directly and never passes <c>BaseStat.Set</c>:95 →
        /// <c>OnStatChange</c>:111. That method raises <c>StatChangeEvent</c>:50, which is what EVERY
        /// stat-driven widget in the game subscribes to and the ONLY thing that repaints most of them:
        /// the flying aircraft's crew strip (<c>AircraftCrewController</c>:90-95 sets
        /// <c>CrewBarsNeedRefresh</c> at :216, its own <c>Update</c>:235-242 then re-runs
        /// <c>RefreshCrewBars</c>:172-203 — the health and stamina sliders), the corruption report
        /// (<c>UIModuleCorruptionReport</c>:181), the roster (<c>UIStateGeoRoster</c>:343-344), the
        /// edit-soldier screen (<c>UIStateEditSoldier</c>:340) and the geoscape log
        /// (<c>GeoscapeLog</c>:612-615). On a non-authoritative peer the numbers were arriving and correct
        /// in the model — the rail carries <c>GeoCharacter._health</c> and <c>_fatigue._stamina</c>, and
        /// the client log's "UiEventMap: StatusStat rides the universal open-screen repaint" is that
        /// traffic landing — and every one of those widgets sat frozen on the paint it was built with.
        /// One echo here unfreezes all of them, on every screen, for every stat, with no screen re-enter
        /// (which a stat stream must never trigger — law L63) and no UI code that knows a rail exists.
        ///
        /// VALUE ONLY, deliberately. Min/Max ride the same struct but move only on augmentation and
        /// level-up, both of which tear their screen down anyway; a Min/Max write leaves Value untouched,
        /// compares equal here and raises nothing. Widen the day a Max-only change is seen going stale.
        ///
        /// Subscribers are game code and may throw; a throw would abort the rest of the apply batch, so it
        /// is caught and logged once per session.
        /// </summary>
        private static void EchoStatChange(BaseStat stat, float before)
        {
            if (_raiseStatChange == null) return;
            float now = stat.Value;
            if (now == before) return;
            try { _raiseStatChange(stat, StatChangeType.Value, before, now); }
            catch (Exception ex)
            {
                if (_statEchoWarned) return;
                _statEchoWarned = true;
                MpLog.LogWarning("[Multiplayer][rail] a StatChangeEvent subscriber threw on a mirrored " +
                                 "stat write (" + stat.Name + "); stat-driven bars may be stale until the " +
                                 "screen is re-entered (logged once): " + ex);
            }
        }

        // Open-instance delegate over the protected raiser, built once. Null only if the game renames it,
        // which is a silent no-echo — L497 asserts the resolve so that cannot pass unnoticed.
        private static readonly Action<BaseStat, StatChangeType, float, float> _raiseStatChange =
            BuildStatRaiser();

        private static bool _statEchoWarned;

        private static Action<BaseStat, StatChangeType, float, float> BuildStatRaiser()
        {
            var mi = typeof(BaseStat).GetMethod("OnStatChange",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (mi == null) return null;
            try
            {
                return (Action<BaseStat, StatChangeType, float, float>)Delegate.CreateDelegate(
                    typeof(Action<BaseStat, StatChangeType, float, float>), mi);
            }
            catch { return null; }
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
            var raw = new List<(string name, Type valType, MemberInfo live, string alias, string fail, MemberInfo[] hop)>();
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
            var raw = new List<(string name, Type valType, MemberInfo live, string alias, string fail, MemberInfo[] hop)>();

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

        private static RailField BuildField(Type owner, string name, Type valType, MemberInfo live, string alias, string fail, MemberInfo[] hop = null)
        {
            var f = new RailField { Name = name, ValueType = valType, LiveAlias = alias, Fi = live as FieldInfo, Pi = RailMeta.DeclaredView(live as PropertyInfo), HopFi = hop };
            // DECLARED opt-out FIRST, ahead of the unresolved bail. An opt-out on a member no convention
            // resolves was DEAD CODE before (the bail below printed "bridge-unresolved" instead), which
            // makes such an exclusion a comment asserting an invariant rather than a mechanism: it would
            // arm itself silently the day the convention starts resolving. Keyed on the OWNER type (whose
            // table this is) + base types, never on the resolved member — there may be no member.
            var optOut = RailMeta.OptOutReason(owner, name);
            if (optOut != null) { f.Class = FieldClass.Excluded; f.Exclude = optOut; return f; }
            if (fail != null || live == null) { f.Class = FieldClass.Excluded; f.Exclude = fail ?? "no live member"; return f; }
            if (!f.CanRead) { f.Class = FieldClass.Excluded; f.Exclude = "unreadable"; return f; }

            // ResolveLive may accept a live member of a DIFFERENT type than the DTO declares (the codec
            // always speaks the DTO type — wire parity); record which coercion bridges the two. Container
            // shape (List↔HashSet↔GameTagsList) needs none: ApplyList dispatches on the live container.
            var liveT = RailMeta.MemberType(live);
            if (liveT != valType)
            {
                // The DTO records a REDUCED form of the live member (a def as its string Id, an enum as its
                // underlying int): retype the field to the LIVE type and let the existing leaf codec carry
                // it — see RailMeta.LiveTypeWins. Nothing downstream changes.
                if (RailMeta.LiveTypeWins(liveT, valType)) { valType = liveT; f.ValueType = liveT; }
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
                // A list of REFERENCES is a leaf list — but only where the element is owned SOMEWHERE ELSE.
                // A ref-addressable SUB-entity (IdentityResolver.IsRefAddressableType) is owned by THIS
                // collection: its ref key is the path THROUGH the collection, so classifying the container
                // as refs is circular — nothing would ever descend into an element and the element's own
                // state would stop shipping entirely (GeoHaven.Zones: _state/Health/ZoneCount). Root
                // entities live in their own registry, so a list of them genuinely is refs (GeoSite in
                // _addons / AttackingSites). Falls through to the keyable-element rung below, which makes it
                // the EntityCollection it already was.
                if (RailMeta.LeafKindOf(elem, out var elemKind) &&
                    (elemKind != LeafKind.EntityRef || IdentityResolver.IsRootEntityType(elem)))
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
            { f.Class = FieldClass.Excluded; f.Exclude = RailMeta.UntypedMemberExclusion; return f; }
            if (typeof(UnityEngine.Object).IsAssignableFrom(valType))
            { f.Class = FieldClass.Excluded; f.Exclude = "Unity scene object"; return f; }
            if (valType.IsClass && RailMeta.HasPersistentMembers(valType))
            { f.Class = FieldClass.Descend; return f; }

            f.Class = FieldClass.Excluded; f.Exclude = "no persistent members (" + valType.Name + ")";
            return f;
        }
    }
}
