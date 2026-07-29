using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Base.Serialization.General;
using Multiplayer.Network.Sync;
using Multiplayer.Util;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// Stage-1 rail gate (CLAUDE.md "Verification"). NOT a simulation: it never boots the game and
    /// never touches a live GeoLevelController. It asserts the rail's OWN laws — classification,
    /// blob reconstructability, list-apply reachability, leaf codec round-trip — over the real game
    /// assembly's real type metadata, plus a committed snapshot so any change to the rail's coverage
    /// is a reviewable diff instead of a silent side effect (boundary-law L-F).
    ///
    /// Why it can run headless: Serializer.GetSerializedMembers is pure attribute reflection
    /// (Serializer.cs:296 — GetTypeSerializeAttribute / ShouldSerializeMember / GetAllMembers), so a
    /// bare `new Serializer(null)` yields byte-identical field discovery to the game's configured
    /// instance. Only VALUE serialization needs the game (SerializationComponent + Timing pump).
    /// </summary>
    internal static class Program
    {
        private const string DefaultManaged = @"D:\Steam\steamapps\common\Phoenix Point\PhoenixPointWin64_Data\Managed";
        private static string _managed = DefaultManaged;

        private static int Main(string[] args)
        {
            System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            var i = Array.IndexOf(args, "--managed");
            if (i >= 0 && i + 1 < args.Length) _managed = args[i + 1];
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var p = Path.Combine(_managed, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
            try { return Run(args); }
            catch (Exception ex) { Console.Error.WriteLine("RailCheck CRASHED: " + ex); return 2; }
        }

        // NoInlining: the JIT resolves a method's type references on entry, so every game type must
        // stay out of Main until the resolver above is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            // UnityEngine.Debug's default handler is a native icall — outside the player it throws
            // SecurityException, so the rail's own warnings would abort the walk. Swap in a sink.
            Debug.unityLogger.logHandler = new Sink();

            // The game builds its serializer in TWO steps (SerializationComponent.Initialize:81-83):
            // `new Serializer(this)` registers the built-in custom type data, then the PUBLIC STATIC
            // InitCustomTypes adds Bounds/Vector2/Vector2Int/Vector3/Vector3Int/Quaternion/Defineable/
            // ScriptableObject. Only the first step was reproduced here, and the second is NOT cosmetic:
            // GetSerializedMembers yields a member only `if (IsSerializeableType(memberType))`
            // (Serializer.cs:308), and for a struct that reduces to IsComplexTypeSerializeable ->
            // GetTypeSerializeAttribute -> GetCustomDataForType (Serializer.cs:160). So without this call
            // every Vector2Int/Vector2/Vector3Int/Bounds-typed member is invisible to the harness while the
            // live rail classifies it — silent UNDER-reporting of coverage, i.e. exactly the "forgot the
            // field" hazard the baseline exists to make reviewable. Nothing in it touches Unity state.
            RailMeta.SerializerOverride = new Serializer(null);
            Base.Serialization.SerializationComponent.InitCustomTypes(RailMeta.SerializerOverride);
            var game = typeof(Base.Core.Timing).Assembly;

            bool polymorphicCodec = ProbePolymorphicCodec();
            var types = Closure(game, polymorphicCodec);
            var laws = new List<string>();
            var sb = new StringBuilder(Snapshot(types, polymorphicCodec, laws));
            laws.AddRange(RoundTrip());
            laws.Sort(StringComparer.Ordinal);

            // Violations live INSIDE the snapshot on purpose: the gate is then a single comparison, and a
            // law the rail breaks TODAY is a committed, reviewable fact rather than a permanently red
            // build everyone learns to ignore. A NEW violation changes this file; so does a fixed one.
            sb.Append("\nknown law violations (" + laws.Count + ") — each one is a rail bug, not a harness limit:\n");
            foreach (var v in laws) sb.Append("  ! " + v + "\n");
            var snapshot = sb.ToString().Replace("\r\n", "\n");

            foreach (var v in laws) Console.Error.WriteLine("LAW VIOLATION  " + v);

            var baseline = Path.Combine(RepoRoot(), "docs", "rail-baseline.txt");
            if (args.Contains("--update"))
            {
                File.WriteAllText(baseline, snapshot);
                Console.WriteLine("baseline updated: " + baseline + " (REVIEW the diff before committing)");
                return 0;
            }
            if (!File.Exists(baseline))
            {
                Console.Error.WriteLine("NO BASELINE at " + baseline + " — run with --update once, then review+commit it.");
                return 1;
            }

            var have = File.ReadAllText(baseline).Replace("\r\n", "\n");
            if (have != snapshot)
            {
                Console.Error.WriteLine("RAILCHECK RED — coverage drift vs docs/rail-baseline.txt:");
                foreach (var d in Diff(have, snapshot).Take(80)) Console.Error.WriteLine(d);
                Console.Error.WriteLine("Intended? Re-run with --update and commit the baseline WITH the change.");
                return 1;
            }
            Console.WriteLine("RAILCHECK GREEN — types=" + types.Count +
                              " polymorphic-codec=" + (polymorphicCodec ? "yes" : "no") +
                              " known-violations=" + laws.Count + " (baselined, see docs/rail-baseline.txt)");
            return 0;
        }

        // ─── The type closure the rail can reach ────────────────────────────
        // Seeded from IdentityResolver.Roots' entity kinds (the rail's one hand-written root table),
        // then expanded through exactly the classes the walk descends through.

        private static List<Type> Closure(Assembly game, bool polymorphicCodec)
        {
            var rootKinds = new[]
            {
                typeof(Base.Core.Timing),
                // Root "TA" — TimeAnchor's latched clock DTO. Seeded explicitly: it reaches the closure
                // today only incidentally, through ActorInstanceData.TimingData.
                typeof(Base.Core.TimingInstanceData),
                typeof(PhoenixPoint.Geoscape.Levels.GeoFaction),
                typeof(PhoenixPoint.Geoscape.Entities.GeoSite),
                typeof(PhoenixPoint.Geoscape.Entities.GeoCharacter),
                typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle),
                // Roots "ES"/"MG"/"MK" — level-scope singleton components (IdentityResolver.Roots):
                // ES/MG classify [none] (visible), MK rides via the GeoMarketplaceInstanceData bridge.
                typeof(PhoenixPoint.Geoscape.Events.GeoscapeEventSystem),
                typeof(PhoenixPoint.Geoscape.Levels.GeoMissionGenerator),
                typeof(Assets.Code.PhoenixPoint.Geoscape.Entities.Sites.TheMarketplace.GeoMarketplace),
                // NOT a rail root (ARCHITECTURE.md "Named next steps"). Seeded because the closure is
                // DECLARED-type-only while the live walk types every hop by obj.GetType():
                // GeoSite.SerializationData is declared ActorInstanceData but IS a GeoSiteInstaceData at
                // runtime, so the walk really does descend PhoenixBaseData -> Layout -> Facilities and
                // reach this type. Until now its classification -- notably N4's refusal of the readonly
                // `_components` array (GeoPhoenixFacility.cs:48) -- was argued in review but never executed.
                typeof(PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility),
                // Mod-state roots (IdentityResolver.RegisterModRoot): MOD-owned classes riding the same
                // walk. Sealed → Concretions never scans the game assembly for them.
                typeof(Multiplayer.Network.Sync.ScrapCartState), // root "M#cart" (shared scrap cart)
            };

            var seen = new HashSet<Type>();
            var queue = new Queue<Type>();
            foreach (var k in rootKinds) foreach (var t in Concretions(game, k)) if (seen.Add(t)) queue.Enqueue(t);

            while (queue.Count > 0)
            {
                var rt = RailType.Get(queue.Dequeue());
                if (rt?.Fields == null) continue;
                foreach (var f in rt.Fields)
                {
                    Type next = null;
                    switch (f.Class)
                    {
                        case FieldClass.Descend: next = f.ValueType; break;
                        case FieldClass.EntityCollection:
                        case FieldClass.EntityList: next = f.ElemType; break;
                        case FieldClass.Leaf when f.Leaf == LeafKind.Composite: next = f.ValueType; break;
                    }
                    if (next == null) continue;
                    // The codec encodes against the DECLARED type and refuses a runtime mismatch, so a
                    // subclass is effectively excluded — UNTIL the codec starts carrying runtime types,
                    // at which point every concretion rides and must satisfy the same laws. That switch
                    // is the "ship side widened" event; the closure has to follow it or the gate lies.
                    foreach (var t in polymorphicCodec ? Concretions(game, next) : new[] { next })
                        if (!t.IsAbstract && seen.Add(t)) queue.Enqueue(t);
                }
            }
            return seen.Where(t => !t.IsAbstract).OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        }

        private static readonly Dictionary<Type, Type[]> _concretions = new Dictionary<Type, Type[]>();

        private static Type[] Concretions(Assembly game, Type baseType)
        {
            if (_concretions.TryGetValue(baseType, out var c)) return c;
            c = baseType.IsSealed || baseType.IsValueType
                ? new[] { baseType }
                : game.GetTypes().Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && baseType.IsAssignableFrom(t))
                      .Concat(baseType.IsAbstract ? Type.EmptyTypes : new[] { baseType })
                      .Distinct().OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            _concretions[baseType] = c;
            return c;
        }

        // ─── Laws ───────────────────────────────────────────────────────────

        // HuskMembers now lives in RailMeta (ARCHITECTURE.md "Husk-gated blob licensing"): the classifier
        // REFUSES an EntityList whose element type has a non-empty husk, so the table decides coverage and
        // this report merely displays it. A private copy here would be two tables free to disagree — the
        // exact shape of the GeoItem/TypeKeyable bug.

        // ─── Snapshot (the reviewable artifact) ─────────────────────────────

        private static string Snapshot(List<Type> types, bool polymorphicCodec, List<string> laws)
        {
            var ser = RailMeta.SerializerOverride;
            var sb = new StringBuilder();
            sb.Append("RAIL BASELINE — generated by tools/RailCheck (no timestamp: this file is diffed, not dated)\n");
            sb.Append("roots: Timing | TimingInstanceData (\"TA\" clock anchor) | GeoFaction | GeoSite | GeoCharacter | GeoVehicle | GeoscapeEventSystem (\"ES\") | GeoMissionGenerator (\"MG\") | GeoMarketplace (\"MK\") | ScrapCartState (\"M#cart\" mod-state root) (IdentityResolver.Roots kinds)\n");
            sb.Append("seeded (not roots — types the live walk reaches only through a runtime subtype): GeoPhoenixFacility\n");
            sb.Append("polymorphic-codec: " + (polymorphicCodec ? "yes" : "no") + "\n");
            sb.Append("def-ownership law: RUNTIME-ONLY — DefOwnership's reference-identity set needs a live DefRepository,\n");
            sb.Append("  so walk-time def-aliasing (an instance reachable from BOTH a live entity and the def graph) is\n");
            sb.Append("  INVISIBLE here; this harness asserts only the static belt (L11: no LocalizedTextBind field/element\n");
            sb.Append("  rides covered — the known def-laundering vector, ItemDef.GetDisplayName returns def state by ref).\n");
            sb.Append("types: " + types.Count + "\n\n");

            int cov = 0, exc = 0, geoItemDicts = 0;
            var blobbable = new SortedDictionary<string, Type>(StringComparer.Ordinal);
            // L15 seeds: only EntityList elements are BLOB-REBUILT at top level. A top-level
            // EntityCollection is element-ADDRESSED (leaves written into existing client elements —
            // its husk list is informational, not a rebuild risk); nested inside a blob it TagList-
            // encodes, which the sweep's recursion reaches on its own.
            var l15Seeds = new List<Type>();
            // L16 inputs, harvested from the SAME pass: every EntityList's (live owner, element) pair, and
            // every type a covered Descend field can hand the applier as a LIVE owner.
            var listOwners = new List<(Type Owner, Type Elem)>();
            var descendTypes = new HashSet<Type>();
            foreach (var t in types)
            {
                var rt = RailType.Get(t);
                if (rt == null) continue;
                sb.Append(t.FullName + "  [" + rt.Source + "]  covered=" + rt.CoveredCount + "/" + rt.Fields.Count + "\n");
                foreach (var f in rt.Fields)
                {
                    if (f.Class == FieldClass.Excluded)
                    { sb.Append("  - EXCLUDED " + f.Name + " (" + f.ValueType.Name + "): " + f.Exclude + "\n"); exc++; continue; }
                    cov++;
                    // L11 — the static belt of the RUNTIME ownership law (src/Rail/DefOwnership.cs):
                    // LocalizedTextBind instances are routinely def-OWNED (ItemDef.GetDisplayName returns
                    // ViewElementDef.DisplayName1/2 by reference, decompile ItemDef.cs:165-173), so a
                    // covered bind field is a def-state write vector on the client. The real law is
                    // reference identity over a live DefRepository — untestable headless (see header);
                    // this asserts that the classify-time refusal (RailMeta.IsPresentation) stays intact.
                    // If that belt is ever replaced by the runtime law alone (N7 exit criterion), revise
                    // L11 in the same commit — the baseline diff makes that reviewable.
                    if (f.ValueType.FullName == "Base.UI.LocalizedTextBind" ||
                        (f.ElemType != null && f.ElemType.FullName == "Base.UI.LocalizedTextBind"))
                        laws.Add("L11 def-laundering-vector-rides: " + t.FullName + "." + f.Name +
                                 " carries LocalizedTextBind as " + f.Class + " — def-owned binds would be written on clients");
                    if (f.Class == FieldClass.GeoItemDict) geoItemDicts++;
                    if (f.Class == FieldClass.Descend) descendTypes.Add(f.ValueType);
                    var extra = "";
                    if (f.Class == FieldClass.LeafList || f.Class == FieldClass.EntityList || f.Class == FieldClass.EntityCollection)
                    {
                        // THE strategy predicate, not a mirror of it: L1 and the classifier's own N4 guard
                        // now ask RailMeta the same question, so the harness can no longer report a
                        // capability the applier does not have (or miss one it does).
                        var strat = RailMeta.ListApplyStrategy(f);
                        // Unordered is printed for EVERY list class, not just LeafList where it started
                        // life: 7ef0a30 reused it to decide which keyed collections ship a whole-list blob,
                        // i.e. it silently widened the set of types the codec reconstructs. Printing the raw
                        // table field is what turns that into a reviewable diff (boundary-law L-F).
                        extra = " unordered=" + (f.Unordered ? "yes" : "no") + " apply=" + (strat ?? "NONE");
                        if (strat == null)
                            laws.Add("L1 no-list-apply-strategy: " + t.FullName + "." + f.Name +
                                     " (" + f.ValueType.Name + ") rides as " + f.Class + " but ApplyList would throw");
                        if (f.Class != FieldClass.LeafList) blobbable[f.ElemType.FullName] = f.ElemType;
                        if (f.Class == FieldClass.EntityList) { l15Seeds.Add(f.ElemType); listOwners.Add((t, f.ElemType)); }
                    }
                    sb.Append("  + " + f.Class + " " + f.Name + " (" + f.ValueType.Name + ")" +
                              (f.LiveAlias != null ? " -> live " + f.LiveAlias : "") + extra + "\n");
                }
            }

            // Blob-reconstructed element types. `husk` = reference members the blob does NOT carry; the
            // codec builds elements with Activator.CreateInstance(nonPublic) and fills only the table's
            // fields, so each husk member lands NULL on the client while the game's own load path
            // re-Init's them. A non-empty husk on a type that ships is the 7ef0a30 NOTEXT shape and must
            // be argued for in review — that is what committing this list buys.
            sb.Append("\nblob-reconstructed element types (Activator.CreateInstance + table fields):\n");
            foreach (var kv in blobbable)
            {
                var t = kv.Value;
                if (t.IsAbstract)
                {
                    // Declared abstract + declared-type-only codec = every concrete element aborts at
                    // encode. An exclusion by exception, not by classification (boundary-law L-E).
                    sb.Append("  " + kv.Key + " ABSTRACT — every element aborts at encode" +
                              (polymorphicCodec ? " ... except the codec now carries runtime types" : "") + "\n");
                    if (polymorphicCodec)
                        laws.Add("L5 abstract-elem-now-rides: " + kv.Key +
                                 " is declared abstract and the codec carries runtime types — concretions must be classified");
                    continue;
                }

                // L2 — EncodeObjectBody throws "create param unmatched" when a [SerializeCustomCreate]
                // parameter name matches no serialized member: an encode-time abort doing exclusion duty.
                var unmatched = UnmatchedCreateParams(ser, t);
                if (unmatched.Count > 0)
                    laws.Add("L2 create-param-unmatched: " + kv.Key + " -> " + string.Join(",", unmatched));
                // L3 — EncodeValue throws on a Unity object; classification must have excluded it first.
                if (typeof(UnityEngine.Object).IsAssignableFrom(t))
                    laws.Add("L3 unity-object-blobbed: " + kv.Key + " reaches the blob codec, which refuses it");

                var husk = RailMeta.HuskMembers(t);
                sb.Append("  " + kv.Key + " keyable=" + (IdentityResolver.TypeKeyable(t) ? "yes" : "no") +
                          " customCreate=" + (HasCustomCreate(ser, t) ? "yes" : "no") +
                          " husk=" + (husk.Count == 0 ? "none" : string.Join(",", husk)) +
                          " roundtrip=" + EntityListRoundTrip(t, laws) + "\n");
            }

            // ─── L15 — RECURSIVE husk sweep over the blob closure ─────────────────────────────────
            // The 2026-07-26 recruit-screen freeze: GeoUnitDescriptor passed the TOP-level husk gate
            // ("husk=none") while its Descend-carried AbilityTrack had an excluded slots array — the
            // blob shipped a half-built object into live UI. The top-level gate cannot see nesting, so
            // this walks the WHOLE reachable graph of every blob-carried type: each uncarried reference
            // member at ANY depth must be WAIVED with a JUSTIFIED reason — "self-heal*" (the game's own
            // PostRead restores it) or "null at rest" (transient scratch) — anything else is a law
            // violation, not a note. Cycle-safe by a visited-TYPE set (backrefs are exactly what loops).
            sb.Append("\nnested husk sweep (recursive over the blob closure; waived = justified opt-out):\n");
            {
                var visited = new HashSet<Type>();
                var queue = new Queue<Type>(l15Seeds);
                var lines = new List<string>();
                while (queue.Count > 0)
                {
                    var nt = queue.Dequeue();
                    if (nt == null || !visited.Add(nt)) continue;
                    if (nt.IsAbstract || nt.IsInterface || nt == typeof(object)) continue;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(nt)) continue;
                    // A leaf collection terminates like a leaf: the codec's TagLeafList arm rebuilds a
                    // FRESH container (ctor + Add) instead of blob-reconstructing it, so its internals
                    // (List<T>._items/_syncRoot) are never husks on the client.
                    if (RailMeta.IsLeafCollection(nt)) continue;
                    if (RailMeta.IsKvpType(nt))
                    {
                        foreach (var a in nt.GetGenericArguments()) if (!RailMeta.LeafKindOf(a, out _)) queue.Enqueue(a);
                        continue;
                    }
                    foreach (var m in RailMeta.HuskScan(nt))
                    {
                        if (m.Waiver == null)
                            laws.Add("L15 nested-husk: " + nt.FullName + "." + m.Name + " (" + m.Type.Name +
                                     ") arrives null on the client — carry it or add a JUSTIFIED waiver");
                        else if (m.Waiver.IndexOf("self-heal", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 m.Waiver.IndexOf("null at rest", StringComparison.OrdinalIgnoreCase) < 0)
                            laws.Add("L15 unjustified-waiver: " + nt.FullName + "." + m.Name +
                                     " waived without a self-heal/null-at-rest argument: " + m.Waiver);
                        else
                            lines.Add("  ~ " + nt.FullName + "." + m.Name + " waived: " + m.Waiver);
                    }
                    var nrt = RailType.Get(nt);
                    if (nrt == null) continue;
                    foreach (var f in nrt.Fields)
                    {
                        if (f.Class == FieldClass.Excluded) continue;
                        // Leaves terminate (DefRef/EntityRef resolve against live state — not husks).
                        if (f.Class == FieldClass.Descend && !RailMeta.LeafKindOf(f.ValueType, out _)) queue.Enqueue(f.ValueType);
                        if (f.ElemType != null && !RailMeta.LeafKindOf(f.ElemType, out _)) queue.Enqueue(f.ElemType);
                        if (f.DictValType != null && !RailMeta.LeafKindOf(f.DictValType, out _)) queue.Enqueue(f.DictValType);
                    }
                }
                lines.Sort(StringComparer.Ordinal);
                foreach (var l in lines) sb.Append(l + "\n");
                sb.Append("  (types swept: " + visited.Count + ")\n");
            }

            // ─── L16 — an owner-PostRead waiver must be HEALED on the path the owner is reached by ───────
            // L15 accepts any "self-heal*" waiver as justified, but WHO heals decides WHERE it heals. The
            // owner-backref shape (element E waives a member whose TYPE is the list's owner T — precisely
            // AbilityTrackSlot.AbilityTrack on AbilityTrack.AbilitiesByLevel) heals for free only while T
            // is a blob LOCAL: DecodeEntityList fires post-read on constructed objects, nothing else. The
            // moment T is ALSO reachable by a live Descend field, ApplyList array-assigns fresh elements
            // into a LIVE T that no post-read ever touches, and the waiver silently becomes a null-backref
            // shipper (11bdb7d's regression: the waiver was argued from "nothing ever descends INTO an
            // AbilityTrack", which _personalAbilityTrack falsifies).
            //
            // The signature is read from METADATA, not from the reason string, deliberately: the reason is
            // what a human wrote and got wrong. The marker is only what the APPLIER keys on, so this law is
            // exactly "the two derivations agree" — remove RailMeta's marker or the ApplyList hook and the
            // law fires, which is what makes it a belt rather than a tautology.
            foreach (var (owner, elem) in listOwners)
                foreach (var m in RailMeta.HuskScan(elem))
                {
                    if (m.Waiver == null || !m.Type.IsAssignableFrom(owner)) continue;   // not a backref to the owner
                    if (!descendTypes.Any(d => owner.IsAssignableFrom(d))) continue;     // owner is only ever a blob local
                    if (!RailMeta.OwnerPostReadWaived(elem))
                        laws.Add("L16 owner-postread-waiver-unhealed: " + elem.FullName + "." + m.Name +
                                 " backrefs " + owner.FullName + ", which IS reachable by live Descend — the waiver's " +
                                 "healer never runs there. Fire the live owner's post-read in ApplyList (mark the waiver \"" +
                                 RailMeta.OwnerPostReadWaiver + "\") or carry the member.");
                }

            // ─── Twin tables (DTO live-twin resolution — ARCHITECTURE.md "DTO twin resolution") ────
            // The wire kind for an actor's SerializationData subtree IS the recorded *InstanceData DTO;
            // the client applies those entries onto the LIVE owner through RailType.GetBridged. These
            // tables are that apply surface: an EXCLUDED row here is a field the host SHIPS but the
            // client cannot mirror (the runtime "dto-twin gap" log) — committed so closing or opening
            // one is a reviewable diff instead of a log line nobody reads.
            sb.Append("\ntwin tables (GetBridged: *InstanceData wire entries -> live owner members):\n");
            int twinRes = 0, twinGap = 0, twinDispatch = 0;
            var twinPairs = new List<(Type live, Type dto)>();
            foreach (var t in types)
                if (RailType.Get(t)?.FieldByName("SerializationData") != null && RailMeta.FindBridge(t) != null)
                    twinPairs.Add((t, RailMeta.FindBridge(t)));
            var twinSeen = new HashSet<string>(StringComparer.Ordinal);
            for (int p = 0; p < twinPairs.Count; p++)
            {
                var (live, dto) = twinPairs[p];
                if (!twinSeen.Add(live.FullName + "|" + dto.FullName)) continue;
                var bt = RailType.GetBridged(live, dto);
                if (bt == null) continue;
                sb.Append(live.FullName + "  <=  " + dto.Name + "  resolved=" + bt.CoveredCount + "/" + bt.Fields.Count + "\n");
                foreach (var f in bt.Fields)
                {
                    // The resolver's nested-component dispatch (IdentityResolver.Resolve): no live member,
                    // but the DTO slot's declaring type is a Component — applied via GetComponent; its own
                    // twin table is chased below.
                    if (f.Fi == null && f.Pi == null && f.ValueType?.DeclaringType != null &&
                        typeof(UnityEngine.Component).IsAssignableFrom(f.ValueType.DeclaringType))
                    {
                        sb.Append("  > dispatch " + f.Name + " -> GetComponent(" + f.ValueType.DeclaringType.Name + ")\n");
                        twinDispatch++;
                        twinPairs.Add((f.ValueType.DeclaringType, f.ValueType));
                        continue;
                    }
                    if (f.Class == FieldClass.Excluded)
                    { sb.Append("  - EXCLUDED " + f.Name + " (" + f.ValueType.Name + "): " + f.Exclude + "\n"); twinGap++; continue; }
                    twinRes++;
                    if (f.ValueType.FullName == "Base.UI.LocalizedTextBind" ||
                        (f.ElemType != null && f.ElemType.FullName == "Base.UI.LocalizedTextBind"))
                        laws.Add("L11 def-laundering-vector-rides: " + live.FullName + "<=" + dto.Name + "." + f.Name +
                                 " carries LocalizedTextBind as " + f.Class + " — def-owned binds would be written on clients");
                    var textra = "";
                    if (f.Class == FieldClass.LeafList || f.Class == FieldClass.EntityList || f.Class == FieldClass.EntityCollection)
                    {
                        var strat = RailMeta.ListApplyStrategy(f);
                        textra = " unordered=" + (f.Unordered ? "yes" : "no") + " apply=" + (strat ?? "NONE");
                        if (strat == null)
                            laws.Add("L1 no-list-apply-strategy: " + live.FullName + "<=" + dto.Name + "." + f.Name +
                                     " (" + f.ValueType.Name + ") rides as " + f.Class + " but ApplyList would throw");
                    }
                    sb.Append("  + " + f.Class + " " + f.Name + " (" + f.ValueType.Name + ")" +
                              (f.LiveAlias != null ? " -> live " + f.LiveAlias : "") + textra + "\n");
                }
            }
            sb.Append("twin summary: resolved=" + twinRes + " gaps=" + twinGap + " dispatch=" + twinDispatch + "\n");

            // L9 — GeoItemDict is a re-INCLUSION: the generic classifier excludes a BaseDef-keyed dict, and
            // FieldClass.GeoItemDict is what puts faction/site inventory back on the rail. So the count going
            // to zero is silent, total loss of inventory sync, and it can happen without touching rail code
            // (ItemStorage._storageItems renamed, or its value type no longer GeoItem). The codec's own
            // encode/decode cannot be exercised here — GeoItem needs an ItemDef, and CommonItemData.SetOwnerItem
            // dereferences it immediately, while BaseDef is a ScriptableObject — so reachability is the part
            // that is honestly checkable offline.
            if (geoItemDicts == 0)
                laws.Add("L9 geoitemdict-vacuous: no field in the closure classifies as GeoItemDict — " +
                         "GeoItemCodec ships nothing and faction/site inventory is not mirrored");

            sb.Append("\nsummary: covered=" + cov + " excluded=" + exc + " blobbable=" + blobbable.Count +
                      " geoItemDicts=" + geoItemDicts + "\n");
            return sb.ToString();
        }

        /// <summary>L6 — the OFFLINE round-trip that DiffEngine.cs:420 already claims exists. 68cd934's
        /// SelfCheckEntityList ran this ON THE HOST (constructing real game objects and firing InvokePostRead
        /// inside the host's own walk); a6fd0a5 removed it and delegated the proof "to the stage-1 harness
        /// (L4)" — but L4 only ever round-tripped a synthetic local class, so no REAL element type was
        /// covered and the comment was false. This drives the actual codec over every blob-reconstructed
        /// element type in the closure, where a constructed object can hurt nothing.
        ///
        /// Values are planted generically from the metadata table (no per-type knowledge): every writable
        /// Leaf field whose kind has a headless sample. DefRef/EntityRef/Composite are left at default —
        /// a BaseDef is a ScriptableObject and an entity ref needs a live graph, so neither can be built
        /// outside the player; the count in `roundtrip=ok(n)` is how many fields actually carried a value,
        /// which is what keeps an empty pass from reading as a real one.</summary>
        private static string EntityListRoundTrip(Type t, List<string> laws)
        {
            object src;
            // Whole-dict pair element (KeyValuePair<K,V>): the codec DROPS a pair whose KEY decodes
            // null/unresolved by contract (an unaddressable dict slot must not become a null-keyed
            // entry). A default-constructed pair has exactly that null key, so for keys that need a
            // live graph (EntityRef roots) or a DefRepository the round-trip is LIVE-GATED — the same
            // honest gap the doc above records for DefRef/EntityRef leaf fields. Class-typed keys
            // construct headless and round-trip for real.
            if (RailMeta.IsKvpType(t))
            {
                var ka = t.GetGenericArguments();
                object key = RailMeta.LeafKindOf(ka[0], out var kk) ? SampleLeaf(kk, ka[0]) : TryConstruct(ka[0]);
                if (key == null) return "live-gated(pair key " + ka[0].Name + ")";
                object pv = RailMeta.LeafKindOf(ka[1], out var vk) ? SampleLeaf(vk, ka[1]) : TryConstruct(ka[1]);
                src = Activator.CreateInstance(t, key, pv); // null value side is legal — only the key gates
            }
            else
            // The codec itself builds elements with Activator.CreateInstance(nonPublic) — same call here.
            // A type it cannot construct is a HARNESS limit (recorded, reviewable), not a rail law breach.
            try { src = Activator.CreateInstance(t, nonPublic: true); }
            catch (Exception ex) { return "unconstructible:" + ex.GetType().Name; }

            var rt = RailType.Get(t);
            var planted = new List<RailField>();
            if (rt != null)
                foreach (var f in rt.Fields)
                {
                    if (f.Class != FieldClass.Leaf) continue;
                    var v = SampleLeaf(f.Leaf, f.ValueType);
                    if (v == null) continue;
                    try { f.SetValue(src, v); planted.Add(f); } catch { }
                }

            var lf = new RailField { Name = "rt", Class = FieldClass.EntityList, ElemType = t, ValueType = typeof(List<>).MakeGenericType(t) };
            var one = (IList)Activator.CreateInstance(lf.ValueType);
            one.Add(src);

            List<object> back;
            try { back = RailMeta.DecodeEntityList(RailMeta.EncodeEntityList(lf, one), lf, null); }
            catch (Exception ex)
            {
                laws.Add("L6 entitylist-round-trip-threw: " + t.FullName + " -> " + ex.GetType().Name + ": " + ex.Message);
                return "THREW";
            }
            if (back == null || back.Count != 1 || back[0] == null || back[0].GetType() != t)
            {
                laws.Add("L6 entitylist-round-trip-shape: " + t.FullName + " did not come back as exactly one " + t.Name);
                return "BADSHAPE";
            }
            if (RailMeta.IsKvpType(t))
            {
                // A pair's sides are not RailFields, so the planted-leaf comparison below never sees them.
                // This is the check that fails if the pair codec — or its leaf-collection value arm — stops
                // carrying content.
                var kp = t.GetProperty("Key"); var vp = t.GetProperty("Value");
                if (!SamePairValue(kp.GetValue(src, null), kp.GetValue(back[0], null)) ||
                    !SamePairValue(vp.GetValue(src, null), vp.GetValue(back[0], null)))
                {
                    laws.Add("L6 pair-round-trip: " + t.FullName + " key/value did not survive the wire");
                    return "PAIRMISMATCH";
                }
            }
            foreach (var f in planted)
            {
                object a = f.GetValue(src), b = f.GetValue(back[0]);
                if (Equals(a, b)) continue;
                laws.Add("L6 entitylist-round-trip-value: " + t.FullName + "." + f.Name + " " + (a ?? "null") + " -> " + (b ?? "null"));
                return "MISMATCH:" + f.Name;
            }
            return "ok(" + planted.Count + ")";
        }

        /// <summary>Headless best-effort construction of a pair side; null = cannot be built offline.</summary>
        private static object TryConstruct(Type t)
        {
            // Leaf collection (the codec's TagLeafList value arm): seed ONE element — an EMPTY list
            // survives even a codec that carries nothing, so it would test nothing.
            if (RailMeta.IsLeafCollection(t))
            {
                var lst = (IList)Activator.CreateInstance(t);
                var e = RailMeta.ElemTypeOf(t);
                var sv = RailMeta.LeafKindOf(e, out var ek) ? SampleLeaf(ek, e) : null;
                if (sv != null) lst.Add(sv);
                return lst;
            }
            if (!t.IsClass) return null;
            try { return Activator.CreateInstance(t, nonPublic: true); } catch { return null; }
        }

        /// <summary>Value equality for a PAIR side. A leaf collection compares element-wise (the only
        /// check that fails if the leaf-list value arm stops carrying content); a headless-constructed
        /// class side has no meaningful equality, so only presence is asserted.</summary>
        private static bool SamePairValue(object a, object b)
        {
            if (a is IList la && b is IList lb)
            {
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!Equals(la[i], lb[i])) return false;
                return true;
            }
            if (RailMeta.LeafKindOf((a ?? b)?.GetType() ?? typeof(object), out _)) return Equals(a, b);
            return a == null || b != null;
        }

        /// <summary>A deterministic non-default value for a leaf kind, or null when none can exist headless.</summary>
        private static object SampleLeaf(LeafKind kind, Type t)
        {
            switch (kind)
            {
                case LeafKind.Bool: return true;
                case LeafKind.Int64:
                case LeafKind.UInt64:
                    return t == typeof(char) ? (object)'r' : Convert.ChangeType(7, t, System.Globalization.CultureInfo.InvariantCulture);
                case LeafKind.Single: return 1.5f;
                case LeafKind.Double: return -2.25;
                case LeafKind.String: return "rt";
                case LeafKind.Enum:
                {
                    var vals = Enum.GetValues(t);
                    return vals.Length == 0 ? null : vals.GetValue(vals.Length - 1); // last ⇒ non-default where possible
                }
                case LeafKind.TimeSpanTicks:
                    return t == typeof(Base.Core.TimeUnit)
                        ? (object)Base.Core.TimeUnit.FromTimeSpan(TimeSpan.FromTicks(1234567))
                        : TimeSpan.FromTicks(1234567);
                case LeafKind.Vector3: return new Vector3(1f, -2f, 3.5f);
                case LeafKind.Quaternion: return new Quaternion(0f, .5f, 0f, .5f);
                default: return null; // DefRef (ScriptableObject) / EntityRef (live graph) / Composite
            }
        }

        private static bool HasCustomCreate(Serializer ser, Type t)
        {
            try { return ser.GetTypeCustomCreateMethod(t, out _)?.Method != null; } catch { return false; }
        }

        private static List<string> UnmatchedCreateParams(Serializer ser, Type t)
        {
            var bad = new List<string>();
            try
            {
                var md = ser.GetTypeCustomCreateMethod(t, out _);
                if (md?.Method == null) return bad;
                var names = new HashSet<string>(ser.GetSerializedMembers(t).Where(m => m.MemberInfo != null)
                                                  .Select(m => m.MemberInfo.Name), StringComparer.Ordinal);
                foreach (var p in Serializer.CustomCreateParameterNames(md.Method))
                    if (!names.Contains(p)) bad.Add(p);
            }
            catch (Exception ex) { bad.Add("<probe failed: " + ex.GetType().Name + ">"); }
            return bad;
        }

        // ─── Codec probes / round-trip ──────────────────────────────────────

        [Base.Serialization.General.SerializeType]
        private class PolyBase { [Base.Serialization.General.SerializeMember] public int A; }

        [Base.Serialization.General.SerializeType]
        private sealed class PolyDerived : PolyBase { }

        [Base.Serialization.General.SerializeType]
        private sealed class Elem
        {
            [Base.Serialization.General.SerializeMember] public int N;
            [Base.Serialization.General.SerializeMember] public string S;
            [Base.Serialization.General.SerializeMember] public List<int> L = new List<int>();
        }

        /// <summary>Does the blob codec carry runtime types (5a056cd) or abort on a declared/runtime
        /// mismatch (its own exclusion law)? The closure above depends on the answer, so ask the code
        /// rather than assume it.</summary>
        private static bool ProbePolymorphicCodec()
        {
            var f = new RailField { Name = "probe", Class = FieldClass.EntityList, ValueType = typeof(List<PolyBase>), ElemType = typeof(PolyBase) };
            try { RailMeta.EncodeEntityList(f, new List<PolyBase> { new PolyDerived { A = 1 } }); return true; }
            catch (NotSupportedException) { return false; }
        }

        private static IEnumerable<string> RoundTrip()
        {
            foreach (var (t, v) in new (Type, object)[]
            {
                (typeof(bool), true), (typeof(int), 42), (typeof(long), -9000000000L), (typeof(ulong), 18000000000000000000UL),
                (typeof(float), 1.5f), (typeof(double), -2.25), (typeof(string), "abc"),
                (typeof(PhoenixPoint.Geoscape.Entities.Research.ResearchState), PhoenixPoint.Geoscape.Entities.Research.ResearchState.Unlocked),
                (typeof(TimeSpan), TimeSpan.FromTicks(1234567)),
                (typeof(Base.Core.TimeUnit), Base.Core.TimeUnit.FromTimeSpan(TimeSpan.FromTicks(1234567))),
                (typeof(Vector3), new Vector3(1f, -2f, 3.5f)), (typeof(Quaternion), new Quaternion(0f, .5f, 0f, .5f)),
                (typeof(string), null),
            })
            {
                object back;
                using (var ms = new MemoryStream())
                {
                    using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) RailMeta.EncodeLeaf(w, t, v);
                    ms.Position = 0;
                    using (var r = new BinaryReader(ms, Encoding.UTF8, true)) back = RailMeta.DecodeLeaf(r, t, null);
                }
                if (!Equals(v, back)) yield return "L4 leaf-round-trip: " + t.Name + " " + (v ?? "null") + " -> " + (back ?? "null");
            }

            // LeafList, ordered and canonicalized-unordered.
            var lf = new RailField { Name = "l", Class = FieldClass.LeafList, ValueType = typeof(List<int>), ElemType = typeof(int) };
            var got = RailMeta.DecodeFieldValue(RailMeta.EncodeFieldValue(lf, new List<int> { 3, 1, 2 }), lf, null, out _) as List<object>;
            if (got == null || !got.Select(Convert.ToInt32).SequenceEqual(new[] { 3, 1, 2 }))
                yield return "L4 leaflist-round-trip: order not preserved";
            var uf = new RailField { Name = "u", Class = FieldClass.LeafList, ValueType = typeof(HashSet<string>), ElemType = typeof(string), Unordered = true };
            if (!RailMeta.BytesEqual(RailMeta.EncodeFieldValue(uf, new HashSet<string> { "b", "a" }),
                                     RailMeta.EncodeFieldValue(uf, new HashSet<string> { "a", "b" })))
                yield return "L4 leaflist-canonical: unordered list is not byte-identical for the same set (law 6)";

            // EntityList blob: encode -> decode -> field-for-field compare.
            var ef = new RailField { Name = "e", Class = FieldClass.EntityList, ValueType = typeof(List<Elem>), ElemType = typeof(Elem) };
            var src = new List<Elem> { new Elem { N = 7, S = "x", L = { 1, 2 } }, new Elem { N = -1, S = null } };
            List<object> rt2 = null;
            string err = null;
            try { rt2 = RailMeta.DecodeEntityList(RailMeta.EncodeEntityList(ef, src), ef, null); }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
            if (err != null) yield return "L4 entitylist-round-trip threw " + err;
            else if (rt2 == null || rt2.Count != 2) yield return "L4 entitylist-round-trip: count mismatch";
            else
            {
                var a = (Elem)rt2[0];
                var b = (Elem)rt2[1];
                if (a.N != 7 || a.S != "x" || !a.L.SequenceEqual(new[] { 1, 2 }) || b.N != -1 || b.S != null)
                    yield return "L4 entitylist-round-trip: value mismatch (" + a.N + "," + a.S + ",[" + string.Join(",", a.L) + "] / " + b.N + "," + b.S + ")";
            }

            // ApplyList EXECUTED, not mirrored. L1's ListStrategy only RESTATES what ApplyList would do, so
            // the two can drift; this runs the real applier. LinkedList<T> implements ICollection<T>.Add
            // EXPLICITLY, so a name probe on the concrete type finds no Add at all and the applier threw —
            // the same failure class as the GeoFacilityComponent[] resync storm. HashSet rides along to
            // prove the interface-first probe did not regress the containers that already worked.
            // L7 — the dict-key TOMBSTONE must stay undecodable as a value. DiffEngine ships a removal as the
            // single byte RailMeta.DictTombstone and GenericApplier discriminates on it BEFORE decoding
            // (GenericApplier.cs:186 LeafDict, :220 GeoItemDict). The only thing separating a delete from a
            // present-null (LeafKind.Null, also one byte) is that 0xFF is not a LeafKind — and LeafKinds are
            // assigned sequentially, so this is a real drift surface, not a constant.
            foreach (LeafKind k in Enum.GetValues(typeof(LeafKind)))
                if ((byte)k == RailMeta.DictTombstone)
                    yield return "L7 tombstone-collision: LeafKind." + k + " encodes to the delete sentinel byte";
            var tf = new RailField { Name = "t", Class = FieldClass.LeafDict, ValueType = typeof(int), KeyType = typeof(string), DictValType = typeof(int) };
            bool tombDecoded;
            try { RailMeta.DecodeFieldValue(new[] { RailMeta.DictTombstone }, tf, null, out _); tombDecoded = true; }
            catch { tombDecoded = false; }
            if (tombDecoded)
                yield return "L7 tombstone-decodable: the dict-delete sentinel decodes as a value — a delete could apply as one";

            // L7 (census) — DELIBERATE harness extension for the resync-only wire addition: the dict CENSUS
            // (present-key list, DiffEngine.AddCensus) rides forced re-emits so the client can prune EXTRA
            // local keys whose deletion tick it missed — the one divergence values + tombstones cannot reach.
            // Same discipline as the tombstone: the marker must collide with no LeafKind, must never decode
            // as a value, and the key list must round-trip.
            foreach (LeafKind k in Enum.GetValues(typeof(LeafKind)))
                if ((byte)k == RailMeta.DictCensusMarker)
                    yield return "L7 census-collision: LeafKind." + k + " encodes to the census marker byte";
            bool censusDecoded;
            try { RailMeta.DecodeFieldValue(RailMeta.EncodeDictCensus(new List<string> { "a" }), tf, null, out _); censusDecoded = true; }
            catch { censusDecoded = false; }
            if (censusDecoded)
                yield return "L7 census-decodable: the census decodes as a value — a prune could apply as one";
            var backC = RailMeta.DecodeDictCensus(RailMeta.EncodeDictCensus(new List<string> { "k1", "k2", "" }));
            if (backC.Length != 3 || backC[0] != "k1" || backC[1] != "k2" || backC[2] != "")
                yield return "L7 census-round-trip: key list mismatch";

            // L8 — delivery contract (law 7) on the shared SurfaceSeq: per-surface monotonic source, and a
            // client guard that is idempotent under redelivery and safe under reordering. Pure class, so the
            // real thing runs here; nothing else in this repo exercises it.
            var seq = new SurfaceSeq();
            if (seq.Next(1) != 1 || seq.Next(1) != 2 || seq.Next(2) != 1)
                yield return "L8 seq-not-monotonic-per-surface: Next must count 1,2,… independently per surface";
            seq.Mark(1, 5);
            if (seq.ShouldApply(1, 5)) yield return "L8 seq-replay: a redelivered seq would apply twice (law 7 idempotence)";
            if (seq.ShouldApply(1, 4)) yield return "L8 seq-out-of-order: a late seq would overwrite a newer one (law 7)";
            if (!seq.ShouldApply(1, 6)) yield return "L8 seq-stuck: the next seq after a mark would never apply";
            if (!seq.ShouldApply(2, 1)) yield return "L8 seq-cross-surface: one surface's seq suppressed another's";

            // L10 — ORDER is state (the "moved an item, peers see it auto-sorted" law; the deleted
            // SelfCheckEntityList's reorder pass, re-landed offline where a constructed object hurts nothing).
            // (a) an EntityList blob must round-trip element ORDER, not just membership;
            // (b) ReuseLiveElements must map value-equal decoded elements 1:1 onto LIVE instances, so a
            //     pure reorder moves existing objects instead of husking them (duplicates claim distinct
            //     instances);
            // (c) ReorderByKeys reorders in place by key: same instances, idempotent, unknown keys skipped,
            //     elements missing from the vector keep relative order at the tail;
            // (d) the order-vector codec round-trips and its marker collides with no LeafKind.
            var of = new RailField { Name = "o", Class = FieldClass.EntityList, ValueType = typeof(List<Elem>), ElemType = typeof(Elem) };
            var fwd = new List<Elem> { new Elem { N = 1 }, new Elem { N = 2 }, new Elem { N = 3 } };
            var backO = RailMeta.DecodeEntityList(RailMeta.EncodeEntityList(of, new List<Elem> { fwd[2], fwd[0], fwd[1] }), of, null);
            if (backO == null || backO.Count != 3 ||
                ((Elem)backO[0]).N != 3 || ((Elem)backO[1]).N != 1 || ((Elem)backO[2]).N != 2)
                yield return "L10 entitylist-order: a reordered list did not decode in its live order";

            var live = new List<Elem> { new Elem { N = 1, S = "a" }, new Elem { N = 2, S = "b" }, new Elem { N = 2, S = "b" } };
            var incoming = RailMeta.DecodeEntityList(RailMeta.EncodeEntityList(of, new List<Elem> { live[2], live[0], live[1] }), of, null);
            RailMeta.ReuseLiveElements(of, live, incoming);
            if (incoming == null || incoming.Count != 3 ||
                !ReferenceEquals(incoming[1], live[0]) ||
                !(ReferenceEquals(incoming[0], live[1]) || ReferenceEquals(incoming[0], live[2])) ||
                !(ReferenceEquals(incoming[2], live[1]) || ReferenceEquals(incoming[2], live[2])) ||
                ReferenceEquals(incoming[0], incoming[2]))
                yield return "L10 reuse-live: value-equal elements did not map 1:1 onto live instances";

            var k1 = new KeyedElem { Id = 1 };
            var k2 = new KeyedElem { Id = 2 };
            var k3 = new KeyedElem { Id = 3 };
            var klist = new List<KeyedElem> { k1, k2, k3 };
            if (!RailMeta.ReorderByKeys(klist, new[] { "3", "9", "1", "2" }) ||
                !ReferenceEquals(klist[0], k3) || !ReferenceEquals(klist[1], k1) || !ReferenceEquals(klist[2], k2))
                yield return "L10 reorder-by-keys: [3,9,1,2] over {1,2,3} must yield 3,1,2 (unknown key skipped)";
            if (RailMeta.ReorderByKeys(klist, new[] { "3", "1", "2" }))
                yield return "L10 reorder-idempotent: reapplying the same order must report no change";
            if (!RailMeta.ReorderByKeys(klist, new[] { "2" }) ||
                !ReferenceEquals(klist[0], k2) || !ReferenceEquals(klist[1], k3) || !ReferenceEquals(klist[2], k1))
                yield return "L10 reorder-tail: elements missing from the vector keep their relative order at the tail";

            // (e) SyncMembersByKeys (alias-collection membership half, run before ReorderByKeys):
            //     prunes local keys the vector no longer lists, adopts missing keys ONLY when the
            //     resolver yields a live instance (unresolvable keys wait), no-ops on a converged set,
            //     and declines fixed-size containers (arrays stay permute-only).
            var m1 = new KeyedElem { Id = 1 };
            var m2 = new KeyedElem { Id = 2 };
            var m3 = new KeyedElem { Id = 3 };
            var mlist = new List<KeyedElem> { m1, m3 };
            if (!RailMeta.SyncMembersByKeys(mlist, new[] { "1", "2", "3" }, k => k == "2" ? m2 : null) ||
                mlist.Count != 3 || !mlist.Contains(m2))
                yield return "L10 members-adopt: a missing key with a resolvable live instance must be added";
            if (RailMeta.SyncMembersByKeys(mlist, new[] { "1", "2", "3" }, k => null))
                yield return "L10 members-idempotent: a converged membership set must report no change";
            if (!RailMeta.SyncMembersByKeys(mlist, new[] { "2" }, k => null) ||
                mlist.Count != 1 || !ReferenceEquals(mlist[0], m2))
                yield return "L10 members-prune: local keys absent from the vector must be removed";
            if (RailMeta.SyncMembersByKeys(mlist, new[] { "2", "9" }, k => null) || mlist.Count != 1)
                yield return "L10 members-wait: an unresolvable key must be skipped without reporting a change";
            var marr = new[] { m1, m3 };
            if (RailMeta.SyncMembersByKeys(marr, new[] { "1" }, k => null) || marr.Length != 2)
                yield return "L10 members-fixed: a fixed-size container must decline membership sync";

            var vecBytes = RailMeta.EncodeKeyOrder(new List<string> { "a", "b", "c" }, null);
            var vecBack = RailMeta.DecodeKeyOrder(vecBytes);
            if (vecBack == null || !vecBack.SequenceEqual(new[] { "a", "b", "c" }))
                yield return "L10 order-vector-codec: encode→decode did not round-trip the key sequence";
            foreach (LeafKind k in Enum.GetValues(typeof(LeafKind)))
                if ((byte)k == RailMeta.OrderVectorMarker)
                    yield return "L10 order-marker-collision: LeafKind." + k + " encodes to the order-vector marker";

            var holder = new ListHolder();
            foreach (var fname in new[] { "Linked", "Set" })
            {
                var fi = typeof(ListHolder).GetField(fname);
                var af = new RailField { Name = fname, Class = FieldClass.LeafList, ValueType = fi.FieldType, ElemType = typeof(int), Fi = fi };
                string aerr = null;
                try { RailMeta.ApplyList(holder, af, new List<object> { 1, 2, 3 }); }
                catch (Exception ex) { aerr = ex.GetType().Name + ": " + ex.Message; }
                if (aerr != null) yield return "L4 applylist-" + fname + " threw " + aerr;
                else if (((IEnumerable<int>)fi.GetValue(holder)).Count() != 3)
                    yield return "L4 applylist-" + fname + ": expected 3 elements after apply";
            }

            // ─── L12 — IntentRail/IntentDedup: the separable halves (ARCHITECTURE.md "harness gaps") ──
            // IntentDedup is PURE by design ("no engine types → unit-tested", its own header) and the
            // envelope codec is BCL-only, so the REAL classes run here. Still in-game-only: the nonce
            // allocator, host dispatch and reject-reconverge (each needs a live NetworkEngine), and the
            // family BODY codecs (inline at capture/handler seams, against live game state).
            var dedup = new IntentDedup(16); // the constructor floor = smallest ring, so eviction is reachable
            if (!dedup.IsNew(1, SurfaceIds.GeoResearchIntent, 1))
                yield return "L12 dedup-first-drop: a never-seen (peer,surface,nonce) was dropped";
            if (dedup.IsNew(1, SurfaceIds.GeoResearchIntent, 1))
                yield return "L12 dedup-replay: a redelivered intent would double-apply (law 7 idempotence)";
            // Peer discriminator: client nonces are client-LOCAL counters — with 2+ clients both emit
            // nonce 1 on one surface and BOTH must apply (the key rationale in IntentDedup's header).
            if (!dedup.IsNew(2, SurfaceIds.GeoResearchIntent, 1))
                yield return "L12 dedup-peer-collision: a second client's nonce 1 was eaten by the first's";
            // Surface discriminator: ONE shared client counter feeds all families (IntentRail._nextNonce);
            // the same (peer,nonce) on another surface is a DIFFERENT intent.
            if (!dedup.IsNew(1, SurfaceIds.GeoManufactureIntent, 1))
                yield return "L12 dedup-surface-collision: same (peer,nonce) on another family was dropped";
            // Bounded ring: overflow evicts the OLDEST key, which is then accepted again — the window
            // semantics behind "a transport dupe arrives adjacent to its original, so 512 holds".
            for (uint n = 100; n < 116; n++) dedup.IsNew(1, SurfaceIds.GeoResearchIntent, n);
            if (!dedup.IsNew(1, SurfaceIds.GeoResearchIntent, 1))
                yield return "L12 dedup-ring-unbounded: capacity overflow did not evict the oldest key";
            // Rejoin (rca-3 audit b): ResetPeer drops ONE peer's window (its fresh engine restarts
            // nonces at 1) and must leave every other peer's intact.
            var dedup2 = new IntentDedup();
            dedup2.IsNew(1, SurfaceIds.GeoPersonnelIntent, 1);
            dedup2.IsNew(2, SurfaceIds.GeoPersonnelIntent, 1);
            dedup2.ResetPeer(1);
            if (!dedup2.IsNew(1, SurfaceIds.GeoPersonnelIntent, 1))
                yield return "L12 dedup-rejoin-eaten: a rejoining peer's restarted nonce 1 was dropped";
            if (dedup2.IsNew(2, SurfaceIds.GeoPersonnelIntent, 1))
                yield return "L12 dedup-reset-bleed: ResetPeer(1) also forgot peer 2's window";
            // Envelope round-trip, every intent family: [nonce:u32][op:u8][opaque body] riding
            // SyncKind.ActionRequest on the family's OWN surface (the surface byte IS the family
            // discriminator) must come back byte-identical, with the [nonce][op] prefix reading exactly
            // as IntentRail.HandleInbound does; and the reject nudge — a deliberately EMPTY envelope on
            // the same surface — must decode to an empty payload, never a failure.
            foreach (var sid in new[] { SurfaceIds.GeoResearchIntent, SurfaceIds.GeoManufactureIntent,
                                        SurfaceIds.GeoPersonnelIntent, SurfaceIds.GeoTimeIntent,
                                        SurfaceIds.GeoBaseIntent, SurfaceIds.GeoEquipIntent })
            {
                byte[] inner;
                using (var ims = new MemoryStream())
                using (var iw = new BinaryWriter(ims, Encoding.UTF8))
                {
                    iw.Write(0xDEADBEEFu); // nonce
                    iw.Write((byte)3);     // op
                    iw.Write("body");      // opaque family body (the engine never parses past [nonce][op])
                    iw.Write(-7);
                    inner = ims.ToArray();
                }
                if (!SyncProtocol.TryDecodeEnvelope(SyncProtocol.EncodeEnvelope(sid, SyncKind.ActionRequest, inner),
                        out var sid2, out var kind2, out var body) ||
                    sid2 != sid || kind2 != SyncKind.ActionRequest || !RailMeta.BytesEqual(body, inner))
                { yield return "L12 intent-envelope: surface 0x" + sid.ToString("X2") + " did not round-trip"; continue; }
                using (var ims = new MemoryStream(body))
                using (var ir = new BinaryReader(ims, Encoding.UTF8))
                    if (ir.ReadUInt32() != 0xDEADBEEFu || ir.ReadByte() != 3)
                        yield return "L12 intent-prefix: [nonce][op] on 0x" + sid.ToString("X2") +
                                     " did not decode as HandleInbound reads it";
                if (!SyncProtocol.TryDecodeEnvelope(SyncProtocol.EncodeEnvelope(sid, SyncKind.ActionRequest, null),
                        out _, out _, out var nudge) || nudge.Length != 0)
                    yield return "L12 reject-nudge: the empty reject envelope on 0x" + sid.ToString("X2") +
                                 " did not decode to an empty payload";
            }

            // ─── L13 — CRC(host)==CRC(client) after apply, at the FIELD-CODEC level ────────────────
            // (ARCHITECTURE.md "harness gaps".) The live-tree differential CRC still needs a
            // GeoLevelController and stays in-game; what IS separable is the identity the law-7 CRC
            // backstop rests on: re-encoding what the applier wrote must reproduce the host's EXACT
            // bytes — otherwise idle ticks re-emit phantom diffs and a subtree CRC compare can never
            // settle. L4/L6 assert decoded VALUE equality; this asserts re-encoded BYTE equality through
            // the real apply calls (DecodeFieldValue + SetValue / ApplyList — GenericApplier.cs:247-273's
            // exact pattern) and hashes with the real Crc32 (the save-transfer polynomial — one truth).
            var crcHost = new Elem { N = 42, S = "crc", L = { 5, 4, 3 } };
            var crcClient = new Elem { N = 0, S = null };
            int crcChecked = 0;
            foreach (var cf in RailType.Get(typeof(Elem)).Fields)
            {
                var hostBytes = RailMeta.EncodeFieldValue(cf, cf.GetValue(crcHost));
                if (cf.Class == FieldClass.LeafList)
                    RailMeta.ApplyList(crcClient, cf, RailMeta.DecodeFieldValue(hostBytes, cf, null, out _) as List<object>);
                else
                    cf.SetValue(crcClient, RailMeta.DecodeFieldValue(hostBytes, cf, null, out _));
                var clientBytes = RailMeta.EncodeFieldValue(cf, cf.GetValue(crcClient));
                if (Crc32.Compute(hostBytes) != Crc32.Compute(clientBytes) || !RailMeta.BytesEqual(hostBytes, clientBytes))
                    yield return "L13 crc-diverged: Elem." + cf.Name + " re-encodes differently after apply — a client would never converge";
                crcChecked++;
            }
            if (crcChecked < 3)
                yield return "L13 crc-vacuous: Elem stopped exposing its 3 fields — the law checked nothing";
            // Unordered set: host and client iterate a HashSet in ARBITRARY orders — the canonical sort
            // is what makes a set CRC-comparable at all, so the re-encode after apply must match too.
            var crcSetF = new RailField { Name = "Set", Class = FieldClass.LeafList, ValueType = typeof(HashSet<int>),
                                          ElemType = typeof(int), Unordered = true, Fi = typeof(ListHolder).GetField("Set") };
            var crcSetHost = new ListHolder { Set = { 3, 1, 2 } };
            var crcSetClient = new ListHolder();
            var setBytes = RailMeta.EncodeFieldValue(crcSetF, crcSetHost.Set);
            RailMeta.ApplyList(crcSetClient, crcSetF, RailMeta.DecodeFieldValue(setBytes, crcSetF, null, out _) as List<object>);
            var setReenc = RailMeta.EncodeFieldValue(crcSetF, crcSetClient.Set);
            if (Crc32.Compute(setBytes) != Crc32.Compute(setReenc) || !RailMeta.BytesEqual(setBytes, setReenc))
                yield return "L13 crc-unordered: a HashSet re-encodes differently after apply (canonical sort broken)";
            // EntityList blob, order included: decode → re-encode must reproduce the wire — a reorder
            // that applied but re-encoded differently would force-re-emit forever.
            var crcEf = new RailField { Name = "e", Class = FieldClass.EntityList, ValueType = typeof(List<Elem>), ElemType = typeof(Elem) };
            var crcWire = RailMeta.EncodeEntityList(crcEf, new List<Elem> { new Elem { N = 2, S = "b", L = { 9 } }, new Elem { N = 1, S = "a" } });
            var crcRelist = new List<Elem>();
            foreach (var o in RailMeta.DecodeEntityList(crcWire, crcEf, null)) crcRelist.Add((Elem)o);
            var crcRewire = RailMeta.EncodeEntityList(crcEf, crcRelist);
            if (Crc32.Compute(crcWire) != Crc32.Compute(crcRewire) || !RailMeta.BytesEqual(crcWire, crcRewire))
                yield return "L13 crc-entitylist: a decoded blob re-encodes to different bytes than the host sent";

            // ─── L14 — twin coercions are WIRED, not just resolved ─────────────────────────────────
            // The twin tables in the baseline show name RESOLUTION only; a member resolved onto a
            // live target of a DIFFERENT type without its coercion recorded would pass the baseline
            // and then throw ArgumentException on the first live apply. Assert the wiring on the real
            // GetBridged tables + exercise the wrapper/hop accessor mechanics on constructible types.
            // (FactionRef's WRITE half — RailMeta.FactionByDef — needs a live GeoLevelController and
            // stays in-game; the flag and the read half are what is honestly checkable here.)
            var twinV = RailType.GetBridged(typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle),
                                            typeof(PhoenixPoint.Geoscape.Entities.GeoVehicleInstanceData));
            var twinS = RailType.GetBridged(typeof(PhoenixPoint.Geoscape.Entities.GeoSite),
                                            typeof(PhoenixPoint.Geoscape.Entities.GeoSiteInstaceData));
            var fRange = twinV?.FieldByName("RangeRemaining");
            var fHp = twinV?.FieldByName("HitPoints");
            var fName = twinV?.FieldByName("Name");
            var fOwnerS = twinS?.FieldByName("OwnerFactionDef");
            if (fRange == null || fRange.Class != FieldClass.Leaf || fRange.WrapFi == null)
                yield return "L14 twin-coercion: GeoVehicle.RangeRemaining lost its EarthUnits wrapper — live apply would throw";
            if (fHp == null || fHp.Class != FieldClass.Leaf || fHp.HopFi?.Name != "Stats" || fHp.Fi?.Name != "HitPoints")
                yield return "L14 twin-coercion: GeoVehicle.HitPoints no longer routes through Stats.HitPoints";
            if (fName == null || fName.Class != FieldClass.Leaf || fName.Fi?.Name != "_vehicleName")
                yield return "L14 twin-coercion: GeoVehicle.Name no longer lands in _vehicleName (the Name property substitutes a localized default)";
            if (fOwnerS == null || fOwnerS.Class != FieldClass.Leaf || !fOwnerS.FactionRef)
                yield return "L14 twin-coercion: GeoSite.OwnerFactionDef lost the def→GeoFaction coercion";
            // Wrapper mechanics: SetValue must box+wrap the naked float, GetValue must unwrap it.
            var wrapHolder = new WrapHolder();
            var synWrap = new RailField
            {
                Name = "R", ValueType = typeof(float), Class = FieldClass.Leaf, Leaf = LeafKind.Single,
                Fi = typeof(WrapHolder).GetField("R"),
                WrapFi = RailMeta.WrapperField(typeof(PhoenixPoint.Common.Core.EarthUnits), typeof(float))
            };
            synWrap.SetValue(wrapHolder, 7.5f);
            if (synWrap.WrapFi == null || wrapHolder.R.Value != 7.5f || !(synWrap.GetValue(wrapHolder) is float rr) || rr != 7.5f)
                yield return "L14 wrap-mechanics: EarthUnits wrapper set/get round-trip failed";
            // Hop mechanics: read+write through the intermediate class member.
            var hopHolder = new HopHolder();
            var synHop = new RailField
            {
                Name = "HitPoints", ValueType = typeof(int), Class = FieldClass.Leaf, Leaf = LeafKind.Int64,
                HopFi = typeof(HopHolder).GetField("Stats"),
                Fi = typeof(PhoenixPoint.Geoscape.Core.GeoVehicleStats).GetField("HitPoints")
            };
            synHop.SetValue(hopHolder, 33);
            if (hopHolder.Stats.HitPoints != 33 || !(synHop.GetValue(hopHolder) is int hh) || hh != 33)
                yield return "L14 hop-mechanics: Stats.HitPoints hop set/get round-trip failed";

            // L17 — a duplicate ROOT key must be an INCIDENT, never a silent drop. Root keys are minted by
            // IdentityResolver (per-owner ids qualified by owner) and consumed by DiffEngine.WalkRoot; when
            // two entities land on one key the second one's whole subtree is eaten by the walk's first-wins
            // dedup, which is invisible unless the detector speaks. Runs on plain objects — the mechanism is
            // key-vs-key, no live GeoLevelController needed.
            int inc0 = DiffEngine.WalkIncidents.Count;
            object r1 = new object(), r2 = new object();
            DiffEngine.WalkRoot("V#1@fa", r1);
            DiffEngine.WalkRoot("V#1@fb", r2);   // same VehicleID, different owner = different roots
            DiffEngine.WalkRoot("V#1@fa", r1);   // same entity re-walked (slice retry) = not a collision
            if (DiffEngine.WalkIncidents.Count != inc0)
                yield return "L17 root-dup-false-positive: distinct root keys (or a re-walk of the same entity) raised an incident";
            DiffEngine.WalkRoot("V#1@fa", r2);   // two entities, one key
            if (DiffEngine.WalkIncidents.Count == inc0)
                yield return "L17 root-dup-undetected: a duplicate ROOT key was swallowed silently — the 'entity invisible to the rail' class is unreportable again";

            // ─── L18 — the UI-session baseline a repaint must not eat ──────────────────────────────
            // UiNativeRepaint.StageBaselines declares, per screen module, the fields holding the
            // player's per-VISIT undo floor; OpenUiRepaint saves them around a reseed and restores
            // ClampBaseline(saved, fresh). Two ways that silently dies, both checked here:
            //   (a) a decompile rename makes AccessTools.Field return null → the pair drops out and
            //       the undo floor is unprotected again with nothing red;
            //   (b) the clamp loses its direction — the only non-mechanical line in the mechanism.
            // The restore itself needs a live GeoscapeView + MonoBehaviour module and stays in-game.
            int pairsBound = 0;
            foreach (var kv in UiNativeRepaint.StageBaselines)
            {
                if (kv.Value.Length == 0)
                    yield return "L18 baseline-unbound: " + kv.Key.Name + " declares no bound stage pair — its undo floor is eaten by every repaint";
                foreach (var p in kv.Value)
                {
                    if (p.Baseline?.DeclaringType != kv.Key || p.Stage?.DeclaringType != kv.Key ||
                        p.Baseline.FieldType != typeof(int) || p.Stage.FieldType != typeof(int))
                        yield return "L18 baseline-drift: a stage pair on " + kv.Key.Name + " no longer binds to two int fields of that type";
                    pairsBound++;
                }
            }
            if (pairsBound < 3)
                yield return "L18 baseline-vacuous: fewer than the 3 declared stat pairs bound — the table checked nothing";
            // saved <= fresh: this visit's own spends — the undo window MUST stay open (the whole bug).
            if (UiNativeRepaint.ClampBaseline(10, 12) != 10)
                yield return "L18 clamp-window: the visit baseline was not restored below the reseeded value — the minus button greys out and the spend cannot be undone";
            // saved > fresh: those points are gone (foreign refund / respec / host reject) — never
            // restore a floor the model cannot back, or the peer refunds what it no longer owns.
            if (UiNativeRepaint.ClampBaseline(10, 8) != 8)
                yield return "L18 clamp-overclaim: a stale baseline above the model value was restored — refundable points that no longer exist";
            if (UiNativeRepaint.ClampBaseline(10, 10) != 10)
                yield return "L18 clamp-identity: an unchanged baseline did not survive the restore";

            // ─── L19 — BLOCK-FIRST is structural: no intent may be emitted from a POSTFIX ──────────
            // The equip family lived by RESULT-SHIP for a month: a gesture postfix set a bool and a
            // postfix on a LATER method (GeoCharacter.SetItems) turned that mark into the intent. When the
            // second method did not fire, the emission simply died — silently, with the patch-bind log
            // still cheerfully reporting "bound" (RCA 2026-07-29: zero intents all session, three peers).
            // A Harmony POSTFIX runs AFTER the native body, so an IntentRail.Send reachable from one means
            // the local model was mutated FIRST and the wire got a result — exactly the posture
            // IntentRail.ShouldRunNative's law forbids. Statically decidable, so it is decided here.
            foreach (var v in ResultShipLaw()) yield return v;

            // The RUNNABLE core of the same law. EquipSync.ChangedBody is what replaced the deleted marks,
            // and it is the piece that fails in SILENCE: too eager and every repaint's re-flush bounces
            // back as a fresh intent; too lazy and the gesture the family exists to carry never ships.
            // Pure SlotRefs, so it runs headless — GeoItem needs a live ItemDef (see L9).
            var cur = new[] { Slots(("a", 1, 0), ("b", 1, 5)), Slots(("w", 1, 3)), Slots(("i", 2, 0)) };
            if (EquipSync.ChangedBody(false, new[] { Slots(("a", 1, 0), ("b", 1, 5)), Slots(("w", 1, 3)), Slots(("i", 2, 0)) }, cur) != null)
                yield return "L19 noop-emit: an identical re-flush produced an intent — every host echo repaint would bounce back as new traffic";
            if (EquipSync.ChangedBody(false, new List<EquipSync.SlotRef>[3], cur) != null)
                yield return "L19 untouched-emit: an all-null (touches nothing) call produced an intent — null must resolve to the character's own content";
            // A list the call does not touch must be FILLED from the canon, not shipped as null: the body
            // is both the wire payload and the compare key, so the two must be the same bytes.
            var changed = EquipSync.ChangedBody(false, new[] { Slots(("a", 1, 0)), null, null }, cur);
            if (changed == null)
                yield return "L19 missed-emit: a real loadout change did not produce an intent — the gesture dies silently, which is the whole bug";
            else if (!RailMeta.BytesEqual(changed, EquipSync.EncodeBody(false, new[] { Slots(("a", 1, 0)), cur[1], cur[2] })))
                yield return "L19 untouched-fill: the untouched lists were not filled from the character's canon — wire body and compare key have diverged";
            // Order IS state (L10): a reposition that reorders the list is a real change and must ship.
            if (EquipSync.ChangedBody(false, new[] { Slots(("b", 1, 5), ("a", 1, 0)), cur[1], cur[2] }, cur) == null)
                yield return "L19 order-blind: a reordered loadout compared equal — slot order would never reach any peer";
            // Same-def siblings are told apart by (count, charges) — that is what the triple is FOR.
            if (EquipSync.ChangedBody(false, new[] { Slots(("a", 1, 0), ("b", 1, 4)), cur[1], cur[2] }, cur) == null)
                yield return "L19 charge-blind: a charges-only difference compared equal — same-def siblings would swap slots unnoticed";
            // freeReload is a mutation in its own right (GeoCharacter.cs:838-844 ReloadForFree), so an
            // otherwise-identical loadout MUST still ship it. This is the loadout-preset path.
            if (EquipSync.ChangedBody(true, new[] { Slots(("a", 1, 0), ("b", 1, 5)), cur[1], cur[2] }, cur) == null)
                yield return "L19 freereload-swallowed: a free reload over an identical loadout produced no intent — preset loads would never reload on any peer";
        }

        private static List<EquipSync.SlotRef> Slots(params (string guid, int count, int charges)[] items)
            => items.Select(i => new EquipSync.SlotRef { Guid = i.guid, Count = i.count, Charges = i.charges }).ToList();

        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        /// <summary>L19's static half: resolve every call token in the shipped assembly's IL and walk the
        /// call graph BACKWARDS from IntentRail.Send. Reaching a method named Postfix = result-ship.
        /// Honest gap: only direct calls and delegate loads (OperandType.InlineMethod covers call/callvirt/
        /// newobj/ldftn/ldvirtftn) are edges — an emit reached through a field-held delegate is invisible.</summary>
        private static IEnumerable<string> ResultShipLaw()
        {
            var asm = typeof(IntentRail).Assembly;
            var roots = typeof(IntentRail).GetMethods(AllMembers).Where(m => m.Name == "Send").ToList();
            if (roots.Count == 0)
            {
                yield return "L19 send-unresolved: IntentRail.Send did not resolve — the block-first law checked nothing";
                yield break;
            }

            Type[] declared;
            try { declared = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { declared = ex.Types.Where(t => t != null).ToArray(); }

            var callers = new Dictionary<int, List<MethodBase>>();
            foreach (var t in declared)
                foreach (var m in t.GetMethods(AllMembers).Cast<MethodBase>().Concat(t.GetConstructors(AllMembers)))
                    foreach (var callee in Callees(m, asm))
                    {
                        if (!callers.TryGetValue(callee, out var l)) callers[callee] = l = new List<MethodBase>();
                        l.Add(m);
                    }

            var seen = new HashSet<int>(roots.Select(r => r.MetadataToken));
            var queue = new Queue<int>(seen);
            var offenders = new List<string>();
            int reached = 0;
            while (queue.Count > 0)
            {
                if (!callers.TryGetValue(queue.Dequeue(), out var ups)) continue;
                foreach (var up in ups)
                {
                    if (!seen.Add(up.MetadataToken)) continue;
                    reached++;
                    // Report and stop: everything above a postfix is already condemned by the postfix.
                    if (up.Name == "Postfix") { if (!PatchesPresentationOnly(up.DeclaringType)) offenders.Add(up.DeclaringType.FullName); }
                    else queue.Enqueue(up.MetadataToken);
                }
            }
            if (reached == 0)
                yield return "L19 vacuous: nothing in the assembly reaches IntentRail.Send — the IL walk resolved no edges and this law is asleep";
            foreach (var o in offenders.OrderBy(o => o, StringComparer.Ordinal))
                yield return "L19 result-ship: " + o + ".Postfix reaches IntentRail.Send from a MODEL patch — a postfix runs " +
                             "AFTER the native mutation, so the local write already happened and this family ships RESULTS " +
                             "instead of blocking first (IntentRail.ShouldRunNative)";
        }

        /// <summary>The one line separating a forbidden result-ship from a legal observation: WHAT the
        /// postfix is attached to. A postfix on a MODEL method (GeoCharacter.SetItems) has already let the
        /// authoritative write through — there is nothing left to block, so any emit from it is a result.
        /// A postfix on a PRESENTATION method (UIModuleCharacterProgression's stat click, which stages into
        /// the module's own view-model; its MODEL commit CommitStatChanges is separately block-first) is
        /// observing staging, which the client-posture law explicitly permits. The game splits the two by
        /// namespace — presentation lives under PhoenixPoint.*.View.* — so the discriminator is grounded,
        /// not guessed. A patch whose targets cannot be read statically (attribute-less TargetMethods) is
        /// NOT presumed presentation: unknown target + emit-from-postfix is exactly what wants review.</summary>
        private static bool PatchesPresentationOnly(Type patchClass)
        {
            var targets = patchClass.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false)
                                    .Cast<HarmonyLib.HarmonyPatch>()
                                    .Select(a => a.info?.declaringType)
                                    .Where(t => t != null)
                                    .ToList();
            return targets.Count > 0 && targets.All(t => t.FullName.Contains(".View."));
        }

        private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodes();

        private static Dictionary<short, OpCode> BuildOpCodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); map[op.Value] = op; }
            return map;
        }

        /// <summary>In-assembly call targets of one method, by metadata token. Walks the IL with the real
        /// operand-size table — a naive byte scan for the call opcodes would match operand bytes and invent
        /// edges, and a law that cries wolf is a law that gets ignored. Anything unparseable ABANDONS the
        /// method rather than guessing (under-reporting is survivable here; a false red is not).</summary>
        private static IEnumerable<int> Callees(MethodBase m, Assembly asm)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeByValue.TryGetValue(code, out var op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                if (op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs); } catch { }
                    if (callee != null && callee.Module.Assembly == asm) yield return callee.MetadataToken;
                }
                i += size;
            }
        }

        private static int OperandSize(OperandType t, byte[] il, int pos)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return pos + 4 > il.Length ? -1 : 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return -1;
            }
        }

        private sealed class WrapHolder
        {
            public PhoenixPoint.Common.Core.EarthUnits R = default; // written via RailField.SetValue (reflection)
        }

        private sealed class HopHolder
        {
            public PhoenixPoint.Geoscape.Core.GeoVehicleStats Stats = new PhoenixPoint.Geoscape.Core.GeoVehicleStats();
        }

        private sealed class ListHolder
        {
            public LinkedList<int> Linked = new LinkedList<int>();
            public HashSet<int> Set = new HashSet<int>();
        }

        // "Id" is on IdentityResolver's probe table, so KeyOf/ReorderByKeys see this exactly as they see a
        // keyed game element — no serializer attributes needed (the order channel never encodes elements).
        private sealed class KeyedElem
        {
            public int Id;
        }

        // ─── Plumbing ───────────────────────────────────────────────────────

        private sealed class Sink : ILogHandler
        {
            public void LogFormat(LogType t, UnityEngine.Object c, string fmt, params object[] a) { }
            public void LogException(Exception e, UnityEngine.Object c) { }
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Multiplayer.csproj"))) d = d.Parent;
            return d?.FullName ?? Directory.GetCurrentDirectory();
        }

        private static IEnumerable<string> Diff(string a, string b)
        {
            var x = a.Split('\n');
            var y = b.Split('\n');
            var setX = new HashSet<string>(x, StringComparer.Ordinal);
            var setY = new HashSet<string>(y, StringComparer.Ordinal);
            foreach (var l in x) if (!setY.Contains(l)) yield return "  -" + l;
            foreach (var l in y) if (!setX.Contains(l)) yield return "  +" + l;
        }
    }
}
