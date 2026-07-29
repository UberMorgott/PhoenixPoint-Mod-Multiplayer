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
using PhoenixPoint.Geoscape.Events;
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
            laws.AddRange(ValueRecordLaw());
            laws.AddRange(ExitWriteBackLaw(types));
            laws.AddRange(HandleSweepLaw());
            laws.AddRange(CrcBackstopLaw());
            laws.AddRange(BacklogLaw());
            laws.AddRange(AnswerValidatorLaw());
            laws.AddRange(VehicleIntentLaw());
            laws.AddRange(RootOwnershipLaw());
            laws.AddRange(RootCoverageLaw());
            laws.AddRange(StructuralDescendLaw(game, types));
            laws.AddRange(StructuralActorLaw());
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
            // The rail's OWN ordered root table is the seed list — one source of truth for "what the walk
            // enters through" (it used to be re-typed here, so a new root row could land with the harness
            // still sweeping the old set). "TA" = TimeAnchor's latched clock DTO, which otherwise reaches
            // the closure only incidentally through ActorInstanceData.TimingData; "ES"/"MG" classify
            // [none] (visible), "MK" rides the GeoMarketplaceInstanceData bridge, "GL" the
            // GeoLevelInstanceData bridge.
            var rootKinds = IdentityResolver.RootKinds.Select(r => r.Type).Concat(new[]
            {
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
            })
            // Structurally-enabled Descend families (DiffEngine.StructuralDescendKinds): the live walk
            // reaches their CONCRETIONS by obj.GetType() while this closure is declared-type-only, and the
            // declared family is ABSTRACT (GeoMission) — so the coverage of the very types the structural
            // layer now CREATES on a client would otherwise be the one thing the baseline cannot see. Read
            // off the rail's own table rather than re-typed here, for the same reason as the root list.
            .Concat(DiffEngine.StructuralDescendKinds.SelectMany(k => Concretions(game, k)))
            .ToList();

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

        /// <summary>The field classes whose apply/decode writes INTO a live container instead of assigning a
        /// value — i.e. every class that needs one to exist. Arrays are excluded by the caller: those are
        /// assigned wholesale (ApplyList's array-assign strategy), so a null one is normal.</summary>
        private static bool IsContainerClass(FieldClass c) =>
            c == FieldClass.LeafDict || c == FieldClass.GeoItemDict || c == FieldClass.LeafList ||
            c == FieldClass.EntityList || c == FieldClass.EntityCollection;

        // ─── Snapshot (the reviewable artifact) ─────────────────────────────

        private static string Snapshot(List<Type> types, bool polymorphicCodec, List<string> laws)
        {
            var ser = RailMeta.SerializerOverride;
            var sb = new StringBuilder();
            sb.Append("RAIL BASELINE — generated by tools/RailCheck (no timestamp: this file is diffed, not dated)\n");
            // Read off IdentityResolver.RootKinds in table ORDER, not re-typed here: the hand-written copy
            // this replaces had already gone stale (it never mentioned the "GL" root that landed in 30b6155),
            // which is the same drift the RootKinds table itself exists to prevent. Mod-state roots are
            // registered at runtime (IdentityResolver.RegisterModRoot), so they are named separately.
            sb.Append("roots (IdentityResolver.RootKinds, walk order): " +
                      string.Join(" | ", IdentityResolver.RootKinds.Select(r => "\"" + r.Key + "\" " + r.Type.Name)) +
                      " + ScrapCartState (\"M#cart\" mod-state root, registered at runtime)\n");
            sb.Append("seeded (not roots — types the live walk reaches only through a runtime subtype): GeoPhoenixFacility" +
                      " | structural-descend concretions of: " +
                      string.Join(", ", DiffEngine.StructuralDescendKinds.Select(k => k.Name)) + "\n");
            sb.Append("polymorphic-codec: " + (polymorphicCodec ? "yes" : "no") + "\n");
            sb.Append("def-ownership law: RUNTIME-ONLY — DefOwnership's reference-identity set needs a live DefRepository,\n");
            sb.Append("  so walk-time def-aliasing (an instance reachable from BOTH a live entity and the def graph) is\n");
            sb.Append("  INVISIBLE here; this harness asserts only the static belt (L11: no LocalizedTextBind field/element\n");
            sb.Append("  rides covered — the known def-laundering vector, ItemDef.GetDisplayName returns def state by ref).\n");
            sb.Append("types: " + types.Count + "\n\n");

            int cov = 0, exc = 0, geoItemDicts = 0;
            // L20 audit: covered collection fields that are NULL on a freshly constructed instance — the
            // shape every decoder/applier used to assume away ("the ctor built one"). Committed so a field
            // acquiring or losing its initializer is a reviewable diff, not a silent behaviour change.
            var nullOnCtor = new List<string>();
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
                object probe = null; bool probed = false; // L20, built lazily the way the blob codec builds elements
                foreach (var f in rt.Fields)
                {
                    if (f.Class == FieldClass.Excluded)
                    { sb.Append("  - EXCLUDED " + f.Name + " (" + f.ValueType.Name + "): " + f.Exclude + "\n"); exc++; continue; }
                    cov++;
                    // ─── L20 — no decode/apply path may rely on the ctor having built the collection ───
                    // GeoUnitDescriptor.ProgressionDescriptor.PersonalAbilities (readonly, no initializer,
                    // assigned only by a non-default ctor) arrived NULL on every blob-built element and the
                    // decoder dropped its entries in silence → recruit-list NRE under a MessageBox handler,
                    // client UI frozen (2026-07-29). The instance question is asked the way the codec asks
                    // it — construct with the parameterless ctor and look. Own plain fields only: a hop
                    // alias or a property getter on a bare instance says nothing about the ctor.
                    if (IsContainerClass(f.Class) && f.Fi != null && f.HopFi == null && !f.ValueType.IsArray)
                    {
                        if (!probed) { probed = true; try { probe = Activator.CreateInstance(t, true); } catch { probe = null; } }
                        object live = null;
                        if (probe != null) { try { live = f.GetValue(probe); } catch { live = null; } }
                        if (probe != null && live == null)
                        {
                            nullOnCtor.Add(t.Name + "." + f.Name + " (" + f.Class + ")");
                            if (RailMeta.MaterializeContainer(probe, f) == null)
                                laws.Add("L20 unmaterializable-container: " + t.FullName + "." + f.Name + " (" + f.ValueType.Name +
                                         ") is null on a freshly constructed instance and cannot be materialized — " +
                                         "every entry the decoder produces for it is dropped");
                        }
                    }
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
                // Value element (GeoItemCodec.IsValueElementType): declared abstract/interface, yet it does
                // NOT abort at encode — it rides an ordinal (defGuid,count,charges,malfunction) record and
                // is rebuilt through the public ctor, so none of the blob laws below (create params, husk,
                // Activator round-trip) are the right questions to ask of it. L22 checks it instead.
                if (GeoItemCodec.IsValueElementType(t))
                {
                    sb.Append("  " + kv.Key + " VALUE-RECORD (defGuid+count+charges+malfunction, ordinal) — " +
                              "public-ctor rebuild, not blob-reconstructed\n");
                    continue;
                }
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

            sb.Append("\nnull-on-construct containers (L20 — the decoder MUST materialize these, never assume a ctor did): " +
                      (nullOnCtor.Count == 0 ? "none" : string.Join(", ", nullOnCtor)) + "\n");
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

#pragma warning disable 649 // D is assigned by reflection only — that is exactly the shape under test
        /// <summary>L20's probe: the GeoUnitDescriptor.ProgressionDescriptor.PersonalAbilities shape —
        /// readonly dictionary, NO field initializer, filled only by a ctor the blob codec never calls.</summary>
        [Base.Serialization.General.SerializeType]
        private sealed class DictElem
        {
            [Base.Serialization.General.SerializeMember] public int N;
            [Base.Serialization.General.SerializeMember] public readonly Dictionary<int, string> D;
        }
#pragma warning restore 649

        /// <summary>Does the blob codec carry runtime types (5a056cd) or abort on a declared/runtime
        /// mismatch (its own exclusion law)? The closure above depends on the answer, so ask the code
        /// rather than assume it.</summary>
        private static bool ProbePolymorphicCodec()
        {
            var f = new RailField { Name = "probe", Class = FieldClass.EntityList, ValueType = typeof(List<PolyBase>), ElemType = typeof(PolyBase) };
            try { RailMeta.EncodeEntityList(f, new List<PolyBase> { new PolyDerived { A = 1 } }); return true; }
            catch (NotSupportedException) { return false; }
        }

        /// <summary>L22 — the ordinal VALUE-RECORD codec (GeoItemCodec.WriteRec/ReadRec) and the coverage it
        /// exists for. Non-vacuity first, exactly like L9: AmmoManager.LoadedMagazines riding covered is a
        /// re-INCLUSION (the generic classifier excludes an interface-element collection), so it can go back
        /// to Excluded without a single rail file changing — and then every loaded weapon silently ships
        /// with Ammo == null again and CurrentCharges falls back to _charges (CommonItemData.cs:33), which is
        /// the bug this class of codec was added to end. Then the codec itself: charges and ORDER must
        /// survive, and an ABSENT collection must stay distinguishable from an EMPTY one (a null list means
        /// "the owner has no ammo manager state", an empty one means "loaded magazines: none" — collapsing
        /// them re-creates the silent substitution). Honest gap, same one GeoItemDict has (Program.cs:484):
        /// a real element needs an ItemDef, and ItemDef is a ScriptableObject — so the wire's element TAG
        /// path is in-game-gated, while the record bytes are checked here for real.</summary>
        private static IEnumerable<string> ValueRecordLaw()
        {
            var rt = RailType.Get(typeof(PhoenixPoint.Common.Entities.AmmoManager));
            var lm = rt?.Fields.FirstOrDefault(f => f.Name == "LoadedMagazines");
            if (lm == null)
                yield return "L22 value-list-vacuous: AmmoManager.LoadedMagazines is not in the rail table at all";
            else if (lm.Class != FieldClass.EntityList || !GeoItemCodec.IsValueElementType(lm.ElemType))
                yield return "L22 value-list-vacuous: AmmoManager.LoadedMagazines rides as " + lm.Class +
                             " (elem " + lm.ElemType?.Name + ") — a loaded weapon ships EMPTY again";
            else if (RailMeta.ListApplyStrategy(lm) == null)
                yield return "L22 value-list-unappliable: AmmoManager.LoadedMagazines has no ApplyList strategy";

            // Record bytes: order + every field, including a 0-charge (spent) and a partial magazine.
            var src = new[]
            {
                new GeoItemCodec.ItemRec { Guid = "mag-A", Count = 1, Charges = 12, Malfunction = -100 },
                new GeoItemCodec.ItemRec { Guid = "mag-B", Count = 3, Charges = 0,  Malfunction = 7 },
                new GeoItemCodec.ItemRec { Guid = "mag-A", Count = 1, Charges = 40, Malfunction = -100 },
            };
            var back = new List<GeoItemCodec.ItemRec>();
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                    foreach (var rec in src) GeoItemCodec.WriteRec(w, rec);
                ms.Position = 0;
                using (var r = new BinaryReader(ms, Encoding.UTF8, true))
                    for (int i = 0; i < src.Length; i++) back.Add(GeoItemCodec.ReadRec(r));
            }
            for (int i = 0; i < src.Length; i++)
                if (back[i].Guid != src[i].Guid || back[i].Count != src[i].Count ||
                    back[i].Charges != src[i].Charges || back[i].Malfunction != src[i].Malfunction)
                    yield return "L22 value-record-round-trip: element " + i + " came back as (" + back[i].Guid + "," +
                                 back[i].Count + "," + back[i].Charges + "," + back[i].Malfunction + ") not (" +
                                 src[i].Guid + "," + src[i].Count + "," + src[i].Charges + "," + src[i].Malfunction + ")";

            // Absent vs empty, through the real field codec (zero elements → no def resolution needed).
            var vf = new RailField
            {
                Name = "v",
                Class = FieldClass.EntityList,
                ValueType = typeof(List<PhoenixPoint.Common.Entities.ICommonItem>),
                ElemType = typeof(PhoenixPoint.Common.Entities.ICommonItem),
            };
            if (RailMeta.DecodeEntityList(RailMeta.EncodeEntityList(vf, null), vf, null) != null)
                yield return "L22 value-list-null: a null collection decodes as a present one";
            var emptyBack = RailMeta.DecodeEntityList(
                RailMeta.EncodeEntityList(vf, new List<PhoenixPoint.Common.Entities.ICommonItem>()), vf, null);
            if (emptyBack == null || emptyBack.Count != 0)
                yield return "L22 value-list-empty: an empty collection decodes as " +
                             (emptyBack == null ? "null" : emptyBack.Count + " elements");
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
                // DateTime is the kind that makes Base.Utils.UnityDateTime carry anything at all (its ONE
                // serialized member) — without it UnityDateTime classified covered=0/1 and GeoMission
                // .GlobalTime could not be mirrored. Ticks, exactly like TimeSpan above.
                (typeof(DateTime), new DateTime(2026, 7, 29, 13, 12, 13, DateTimeKind.Utc)),
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
                                        SurfaceIds.GeoBaseIntent, SurfaceIds.GeoEquipIntent,
                                        SurfaceIds.GeoEventIntent, SurfaceIds.GeoVehicleIntent })
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
            // An aircraft's position rides through a PROPERTY hop (GeoActor.Surface is an auto-property,
            // decompile GeoVehicle.cs:89), so a field-only ResolveAliasChain silently classifies both
            // carriers "dto-twin unresolved" — the client's aircraft then never moves and NOTHING says so
            // (the host never ships the field, so GenericApplier's dto-twin-gap line never fires either).
            // Assert COVERED, and assert the hop is the Surface property landing on Transform's own member.
            foreach (var (dtoName, leafName) in new[] { ("SurfacePos", "position"), ("SurfaceRot", "rotation") })
            {
                var fSurf = twinV?.FieldByName(dtoName);
                if (fSurf == null || fSurf.Class != FieldClass.Leaf)
                    yield return "L14 twin-coercion: GeoVehicle." + dtoName + " is not a covered Leaf (" +
                                 (fSurf?.Exclude ?? "absent") + ") — a client aircraft cannot mirror the host's position";
                else if (!(fSurf.HopFi is PropertyInfo) || fSurf.HopFi.Name != "Surface" || fSurf.Pi?.Name != leafName)
                    yield return "L14 twin-coercion: GeoVehicle." + dtoName + " no longer routes through the Surface property onto Transform." + leafName;
            }
            // Hop mechanics, PROPERTY arm: the hop is only ever READ, so a get-only property must work.
            var propHop = new HopHolder();
            var synPropHop = new RailField
            {
                Name = "HitPoints", ValueType = typeof(int), Class = FieldClass.Leaf, Leaf = LeafKind.Int64,
                HopFi = typeof(HopHolder).GetProperty("StatsProp"),
                Fi = typeof(PhoenixPoint.Geoscape.Core.GeoVehicleStats).GetField("HitPoints")
            };
            synPropHop.SetValue(propHop, 41);
            if (!(synPropHop.HopFi is PropertyInfo) || propHop.Stats.HitPoints != 41 ||
                !(synPropHop.GetValue(propHop) is int ph) || ph != 41)
                yield return "L14 hop-mechanics: a PROPERTY hop does not set/get round-trip — GeoVehicle.SurfacePos/SurfaceRot cannot ride";
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

            // L19's REPEAT half. Blocking the client's native write leaves the canon untouched, so the equip
            // screen's next content flush recomputes the SAME body — one drag shipped 7 identical intents
            // (nonce=1..7 bytes=510, log 2026-07-29). IntentDedup cannot help: by its (peer, surface, nonce)
            // key those ARE distinct intents. EquipSync.AlreadySent is the memo that stops them, and it must
            // retire itself the moment the model moves, or the gesture could never be repeated.
            var canonA = EquipSync.EncodeBody(false, cur);
            var bodyA = EquipSync.ChangedBody(false, new[] { Slots(("a", 1, 0)), null, null }, cur);
            if (EquipSync.AlreadySent(7, bodyA, canonA))
                yield return "L19 repeat-first-blocked: the FIRST send of a gesture was suppressed — the intent never leaves the client";
            // The screen's NEXT flush RE-ENCODES: equal content, different arrays. Passing the same instances
            // twice would let a reference compare pass for a byte compare, so the repeat is built from scratch.
            if (!EquipSync.AlreadySent(7, EquipSync.ChangedBody(false, new[] { Slots(("a", 1, 0)), null, null }, cur),
                                          EquipSync.EncodeBody(false, cur)))
                yield return "L19 repeat-unblocked: the same body over an unmoved canon shipped twice — one drag emits an intent per screen flush";
            // Host echo (or a reject reconverge) moves the model: the memo must retire with it.
            var canonB = EquipSync.EncodeBody(false, new[] { Slots(("a", 1, 0)), cur[1], cur[2] });
            if (EquipSync.AlreadySent(7, bodyA, canonB))
                yield return "L19 repeat-stuck: the memo outlived the model change — after the echo the same gesture could never be made again";
            if (EquipSync.AlreadySent(8, bodyA, canonA))
                yield return "L19 repeat-cross-character: one character's memo suppressed another character's intent";

            // ─── L20's RUNNABLE half — entries must survive into a container the ctor never built ────
            // The static half (Snapshot) only asks whether such a field COULD be materialized; this asks
            // whether the decode path actually does it. Both halves exist because the failure was silent
            // on BOTH counts: no throw, no log, a "successful" decode and a null field.
            var de = new DictElem { N = 3 };
            var dFi = typeof(DictElem).GetField("D");
            string setErr = null;
            // Doubles as the empirical proof that FieldInfo.SetValue writes an initonly INSTANCE field —
            // the assumption the whole materialize path rests on (RailField.IsWritable).
            try { dFi.SetValue(de, new Dictionary<int, string> { { 1, "a" }, { 2, "b" } }); }
            catch (Exception ex) { setErr = ex.GetType().Name + ": " + ex.Message; }
            if (setErr != null)
                yield return "L20 readonly-unwritable: FieldInfo.SetValue refused an initonly instance field (" + setErr +
                             ") — materializing a readonly container is impossible on this runtime and the fix is void";
            else
            {
                var dField = RailType.Get(typeof(DictElem))?.FieldByName("D");
                if (dField == null || dField.Class != FieldClass.LeafDict)
                    yield return "L20 vacuous: the probe's dict field classified as " +
                                 (dField == null ? "absent" : dField.Class.ToString()) + ", not LeafDict — this law checked nothing";
                else
                {
                    var def2 = new RailField { Name = "d", Class = FieldClass.EntityList, ValueType = typeof(List<DictElem>), ElemType = typeof(DictElem) };
                    List<object> rtd = null;
                    string derr = null;
                    try { rtd = RailMeta.DecodeEntityList(RailMeta.EncodeEntityList(def2, new List<DictElem> { de }), def2, null); }
                    catch (Exception ex) { derr = ex.GetType().Name + ": " + ex.Message; }
                    var back2 = rtd != null && rtd.Count == 1 ? ((DictElem)rtd[0]).D : null;
                    if (derr != null)
                        yield return "L20 round-trip threw " + derr;
                    else if (back2 == null || back2.Count != 2 || back2[1] != "a" || back2[2] != "b")
                        yield return "L20 null-container-swallow: a dict field the ctor never built came back " +
                                     (back2 == null ? "NULL" : "with " + back2.Count + "/2 entries") +
                                     " — the decoder dropped entries into a null container in silence (the recruit-hire freeze shape)";
                }
            }

            // ─── L24 — a host-side replay may not INVENT a shared-resource split ───────────────────
            // A spend that drains the soldier's own SP pool takes the rest off the SHARED faction pool
            // (ChangeCharacterStat:891-905), and the native UNDO pays each point back to the pool it
            // came from, using the panel's per-VISIT baseline `_startingFactionPoints` as the boundary
            // (:915-931). A host replaying a peer's gesture owns no such baseline, so the split has to
            // RIDE the gesture. The day it does not, charge-then-undo quietly moves points ACROSS the
            // two pools with the total still conserved: no exception, no log line, nothing red — the
            // pools just drift apart every time somebody undoes a stat (RCA 2026-07-29). Two halves:
            //   (a) the split must be an exact inverse of Charge for every spill shape;
            //   (b) the baseline it is derived FROM must survive a repaint, or the gesture ships a
            //       split that was already wrong at the source.
            foreach (var (personal, shared, cost) in new[] { (20, 77, 56), (0, 41, 12), (56, 41, 27), (5, 0, 5), (97, 3, 97) })
            {
                int spill = cost > personal ? cost - personal : 0;         // Charge:1198-1203 == native :891-905
                int backToShared = PersonnelSync.SharedShare(cost, spill); // what the gesture carried
                int p = (personal - cost) + spill + (cost - backToShared);
                int s = (shared - spill) + backToShared;
                if (p != personal || s != shared)
                    yield return "L24 refund-provenance: charging " + cost + " SP against pools " + personal + "/" + shared +
                                 " and undoing it left them at " + p + "/" + s +
                                 " — the host invented the refund split instead of applying the gesture's";
            }
            if (PersonnelSync.SharedShare(10, 99) != 10 || PersonnelSync.SharedShare(10, -5) != 0)
                yield return "L24 refund-unclamped: a shipped split outside [0, amount] was applied verbatim — a peer could move the pool boundary at will";
            UiNativeRepaint.StageBaselines.TryGetValue(
                typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression), out var progPairs);
            var poolPair = (progPairs ?? new UiNativeRepaint.StagePair[0]).FirstOrDefault(x => x.Baseline?.Name == "_startingFactionPoints");
            if (poolPair == null)
                yield return "L24 split-baseline-undeclared: _startingFactionPoints is not in UiNativeRepaint.StageBaselines — " +
                             "every rail repaint reseeds it to the live pool, so native's split reads 'the shared pool was never " +
                             "touched' and the whole refund lands on the soldier's own pool";
            else if (!poolPair.Ceiling)
                yield return "L24 split-baseline-polarity: the shared-pool baseline is declared as a FLOOR — the restore clamps it " +
                             "DOWN to the live pool, and native's cap (:928-931) then writes that lower number back INTO the pool: " +
                             "SP destroyed, not just mis-split";
            if (UiNativeRepaint.ClampBaseline(41, 12, ceiling: true) != 41)
                yield return "L24 ceiling-clamp: a pool baseline was not restored ABOVE the reseeded value — this visit's spill is " +
                             "forgotten and the refund can no longer find its way back to the shared pool";
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
                        if (!callers.TryGetValue(callee.MetadataToken, out var l)) callers[callee.MetadataToken] = l = new List<MethodBase>();
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

        /// <summary>L21 — a screen whose <c>ExitState</c> WRITES BACK into the live model must be declared in
        /// <c>UiNativeRepaint.Table</c>. OpenUiRepaint's fallback for an undeclared screen is Exit+Enter, and
        /// Exit runs ExitState: a teardown that flushes its widget lists into the model (the leak this law was
        /// written for — UIStateVehicleRoster.cs:128-131 → GeoVehicle.ReplaceEquipments and
        /// AircraftItemStorage.RemoveItems/AddItems) therefore UNDOES the very delta the repaint was triggered
        /// by, inside the apply scope, with no echo to put it back. A declared screen gets its own
        /// read-direction rebuild instead and Exit never runs.
        ///
        /// "Writes back" is decided from METADATA + IL, never from method names: walk out of ExitState through
        /// PRESENTATION methods only (the game's own split — presentation lives under <c>*.View.*</c> / Base.UI,
        /// the same discriminator <see cref="PatchesPresentationOnly"/> uses) and report the first edge that
        /// leaves presentation into a VOID INSTANCE method of a type the rail replicates (the closure this
        /// harness classified). Void = command, non-void = query: that is what separates ReplaceEquipments /
        /// RemoveItems from GetAircraftInfo / Except / CreateUIData, with no name matching anywhere.
        ///
        /// LIMITATION (the honest approximation): a void command is PRESUMED to write — nothing here proves a
        /// store happens, only that the teardown can COMMAND replicated state; and edges are direct call /
        /// callvirt only, so a write reached through a field-held delegate is invisible (a delegate LOAD is
        /// deliberately not an edge: `Event -= Handler` in a teardown references gesture handlers it never
        /// runs). Both directions are chosen so a red line is always worth reading: the fix for a
        /// false positive is to declare the screen, which is what a repainted screen wants anyway. The call
        /// NAMED is simply the first one the walk reaches, so it can be a mutation of a freshly built element
        /// (GeoVehicleEquipment.DamageHitPoints, built inside UpdateVehicleEquipments) rather than the flush
        /// that lands in the model (GeoVehicle.ReplaceEquipments): the finding is the SCREEN, not the call.</summary>
        private static IEnumerable<string> ExitWriteBackLaw(List<Type> types)
        {
            var covered = new HashSet<Type>(types);
            var game = typeof(PhoenixPoint.Geoscape.View.GeoscapeViewState).Assembly;
            Type[] declared;
            try { declared = game.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { declared = ex.Types.Where(t => t != null).ToArray(); }

            int analyzed = 0;
            foreach (var screen in declared
                         .Where(t => !t.IsAbstract && typeof(PhoenixPoint.Geoscape.View.GeoscapeViewState).IsAssignableFrom(t))
                         .OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var exit = screen.GetMethod("ExitState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (exit == null || exit.GetMethodBody() == null) continue;
                analyzed++;
                var command = FirstModelCommand(exit, game, covered);
                if (command == null || UiNativeRepaint.Table.ContainsKey(screen)) continue;
                yield return "L21 exit-writeback-unrepainted: " + screen.Name + ".ExitState reaches " + command +
                             " (a void command on rail-covered " + command.Split('.')[0] + ") but the screen is not in " +
                             "UiNativeRepaint.Table — its repaint falls back to Exit+Enter, which runs that write-back " +
                             "and rolls the just-applied delta straight back out of the model";
            }
            if (analyzed == 0)
                yield return "L21 vacuous: no GeoscapeViewState with an ExitState body was analyzed — the walk resolved nothing and this law is asleep";

            // Same law, the VALUE-RETURNING teardown write-back. The walk above is blind to
            // UIStateGeoscapeEvent.ExitState:61-65 -> GeoscapeEvent.CompleteEvent on BOTH of its gates at
            // once: CompleteEvent returns GeoFactionReward (non-void = query, the test that keeps this law
            // from crying wolf), and GeoscapeEvent is deliberately NOT in the rail's classified closure --
            // it is a session-local instance over the REPLICATED GeoscapeEventRecord
            // (docs/rail-baseline.txt:240), even though it holds that record as a serialized member and its
            // CompleteEvent resolves the ledger + grants the whole reward (GeoscapeEvent.cs:86-118).
            // Widening either gate generically is not available: dropping void promotes every query to a
            // command, and "declares a covered-typed member" promotes GeoLevelController to a model type --
            // both flood, and a flooding law gets baselined and stops being read. So this arm resolves that
            // ONE edge from IL and asserts the conclusion the law reaches everywhere else: the screen must be
            // in UiNativeRepaint.Table. It reports itself asleep if the edge ever disappears.
            var eventScreen = typeof(PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoscapeEvent);
            var eventExit = eventScreen.GetMethod("ExitState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var completeEvent = typeof(GeoscapeEvent).GetMethod("CompleteEvent", AllMembers);
            if (eventExit == null || completeEvent == null ||
                !Callees(eventExit, game, directCallsOnly: true).Any(c => Same(c, completeEvent)))
                yield return "L21 vacuous: UIStateGeoscapeEvent.ExitState no longer reaches GeoscapeEvent.CompleteEvent " +
                             "— the value-returning teardown edge this arm was written for is gone and the arm is asleep";
            else if (!UiNativeRepaint.Table.ContainsKey(eventScreen))
                yield return "L21 exit-writeback-unrepainted: UIStateGeoscapeEvent.ExitState reaches GeoscapeEvent.CompleteEvent " +
                             "(a value-returning command that resolves the replicated GeoscapeEventRecord and applies the " +
                             "reward) but the screen is not in UiNativeRepaint.Table — its repaint falls back to Exit+Enter, " +
                             "which auto-answers a still-Triggered event with Choices.Last() on whichever peer happens to " +
                             "have the dialog open";

            // Same law, static half: a table entry whose reflection handle went null is a DEAD entry — the
            // screen declines, falls back to Exit+Enter and the leak is back with nothing red. Sweeps every
            // MemberInfo handle UiNativeRepaint holds, so no entry can add an unchecked one.
            foreach (var f in typeof(UiNativeRepaint).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                                                     .Where(f => typeof(MemberInfo).IsAssignableFrom(f.FieldType))
                                                     .OrderBy(f => f.Name, StringComparer.Ordinal))
                if (f.GetValue(null) == null)
                    yield return "L21 repaint-handle-unbound: UiNativeRepaint." + f.Name + " resolved to null — that " +
                                 "table entry is dead, its screen silently falls back to Exit+Enter";
        }

        /// <summary>L23 — L21's UiNativeRepaint handle sweep, generalized to the WHOLE rail: every static
        /// reflection handle the sync layer holds must RESOLVE. <c>AccessTools.Method</c> returns null on an
        /// exact-signature miss (a base-typed parameter is not a derived one) and every user of the handle
        /// then silently does nothing — the patch never binds, the native derive never runs, the seam is
        /// dead with nothing red. That is the failure mode this repo has already paid for, and it is
        /// statically checkable in full: read the field, look for null.
        ///
        /// Deliberately NOT attempted (the class this law does not cover): "a native mutating funnel with
        /// no capture-or-block seam". Statically it would need the set of funnels — every void method in
        /// the game assembly that stores to a rail-covered field — and nearly all of them are legally
        /// unpatched, because the client's sim is gated upstream at LevelHourlyUpdateCrt instead. A law
        /// whose red lines are ~99% legal is a law that gets baselined and stops being read (the harness's
        /// own "a law that cries wolf is a law that gets ignored"). The in-game gate finds those; this
        /// sweep guarantees the seams we DID write are alive.</summary>
        /// <summary>L25 — the law-7 drift backstop. Two halves, both statically checkable:
        /// (a) it must hash through THE canonical walk, not a copy of it. A second "CRC walk" would drift
        ///     from the very rail it polices, and the drift would be invisible (both sides would agree with
        ///     each other and disagree with the wire), so the IL of RootCrc must reach VisitEntity and the
        ///     one shared hash, and nothing else in DiffEngine may hash rail entries.
        /// (b) it must DETECT the class it exists for: a subtree entry that VANISHED. That is the whole B1/A4
        ///     bug — a removed path emits no entry and no tombstone (only dict subkeys are tombstoned), so a
        ///     hash blind to a missing entry would make the backstop decorative. Value change and reorder ride
        ///     along, plus the per-peer exclusion (corridors) actually excluding, and the message byte not
        ///     colliding with the three that already share surface 0xAC.
        /// Honest gap (this harness never has a live GeoLevelController): that the two peers' WALKS produce the
        /// same entries is in-game only — asserted here only as "same code path".</summary>
        private static IEnumerable<string> CrcBackstopLaw()
        {
            var asm = typeof(DiffEngine).Assembly;
            var rootCrc = typeof(DiffEngine).GetMethod("RootCrc", AllMembers);
            var hash = typeof(DiffEngine).GetMethod("CrcOfEntries", AllMembers);
            var walk = typeof(DiffEngine).GetMethod("VisitEntity", AllMembers);
            if (rootCrc == null || hash == null || walk == null)
            {
                yield return "L25 crc-backstop-absent: DiffEngine.RootCrc/CrcOfEntries/VisitEntity no longer " +
                             "resolve — the drift backstop is gone and divergence is invisible again";
                yield break;
            }
            var callees = Callees(rootCrc, asm).Select(m => m.Name).ToList();
            if (!callees.Contains(walk.Name))
                yield return "L25 crc-walk-forked: DiffEngine.RootCrc does not call the canonical VisitEntity — a " +
                             "parallel CRC walk drifts from the rail it is supposed to police";
            if (!callees.Contains(hash.Name))
                yield return "L25 crc-hash-bypassed: DiffEngine.RootCrc does not call CrcOfEntries — the shipped " +
                             "hash is then not the one this law checks";
            foreach (var m in typeof(DiffEngine).GetMethods(AllMembers))
                if (m.Name != hash.Name && Callees(m, asm).Any(c => c.DeclaringType == typeof(Crc32)))
                    yield return "L25 crc-second-hash: DiffEngine." + m.Name + " hashes outside CrcOfEntries — two " +
                                 "hashes of the same subtree cannot both be the canonical one";

            // (b) the hash's detection duties, on synthetic entries (the walk needs a live level; the HASH does not).
            var full = new List<DiffEngine.Entry>
            {
                Ent("S#7", 0, "", 1), Ent("S#7.SerializationData.ActiveMission", 3, "", 2), Ent("S#7.Storage", 5, "k", 3),
            };
            uint crcFull = DiffEngine.CrcOfEntries(full, null);
            if (DiffEngine.CrcOfEntries(new List<DiffEngine.Entry>(full), null) != crcFull)
                yield return "L25 crc-unstable: the same entry list hashes differently twice — no compare can ever settle";
            var removed = new List<DiffEngine.Entry> { full[0], full[2] }; // the Descend subtree entry vanished
            if (DiffEngine.CrcOfEntries(removed, null) == crcFull)
                yield return "L25 crc-removal-blind: a subtree entry that VANISHED does not change the CRC — the removal " +
                             "class the backstop exists for (completed mission, scrapped item, dead subtree) stays invisible";
            var changed = new List<DiffEngine.Entry> { full[0], Ent("S#7.SerializationData.ActiveMission", 3, "", 9), full[2] };
            if (DiffEngine.CrcOfEntries(changed, null) == crcFull)
                yield return "L25 crc-value-blind: a changed entry value does not change the CRC";
            var reordered = new List<DiffEngine.Entry> { full[2], full[1], full[0] };
            if (DiffEngine.CrcOfEntries(reordered, null) == crcFull)
                yield return "L25 crc-order-blind: walk order is canonical state (law 6) but does not change the CRC";
            // Per-peer subtrees (base corridors) must be excluded, else every base site hashes unequal forever.
            var withLocal = new List<DiffEngine.Entry>(full) { Ent("S#7.Layout._facilities#42.Id", 1, "", 7) };
            if (DiffEngine.CrcOfEntries(withLocal, new[] { "S#7.Layout._facilities#42" }) != crcFull)
                yield return "L25 crc-peerlocal-hashed: a declared PER-PEER subtree still rides the CRC — the two peers' " +
                             "own corridor ids would report permanent false divergence";

            var msgBytes = new[] { DiffEngine.MsgDelta, DiffEngine.MsgResyncRequest, DiffEngine.MsgStructural, DiffEngine.MsgCrcReport };
            if (msgBytes.Distinct().Count() != msgBytes.Length)
                yield return "L25 msg-byte-collision: the CRC report shares its leading byte with another 0xAC message " +
                             "kind — it would be parsed as that one, silently";
        }

        // Root pairs where a LATER root's declared-type closure reaches an EARLIER root's type. Legal —
        // the earlier root is walked first and OWNS the instance — but never by accident: the later root
        // emits NOTHING for it (a silent return in VisitEntity), so the coverage must be known to ride
        // under the earlier path. Grammar: "<later>-><earlier>". A row that stops being observed is a
        // violation too, so this cannot rot into a permanent allowance list.
        private static readonly HashSet<string> _declaredRootReach = new HashSet<string>(StringComparer.Ordinal)
        {
        };

        /// <summary>L28 — root ownership: ONE instance, ONE root path.
        ///
        /// The walk's reference `visited` set (<c>DiffEngine.VisitEntity</c>) makes the SECOND arrival at
        /// an instance a silent return: no entries, no tombstone, no incident. So when two declared roots
        /// can reach the same instance, the ORDER in <c>IdentityResolver.RootKinds</c> silently decides
        /// which path owns every field under it — and reordering the table moves coverage with no other
        /// visible effect. This law makes that a static, named fact:
        ///   • an EARLIER root must not reach a LATER root's own type (the later root's paths would never
        ///     exist on the wire, while the client still resolves them — RCA gap B3 trap T1);
        ///   • the reverse direction is legal but must be DECLARED (and a stale declaration is red);
        ///   • "GL" — the only root whose closure spans the whole level — must be LAST;
        ///   • the GL bridge's landmine members must be EXPLICIT opt-outs, not accidents of a lookup that
        ///     happens to fail today (<c>TacUnits</c> would rebuild the U#-owned registry from a blob).
        /// Closure is over DECLARED types (deterministic, same on both peers); the reach test compares
        /// with assignability in both directions so a declared base (GeoActor) counts as reaching its
        /// concretions, which is what the live walk really does (it types every hop by obj.GetType()).</summary>
        private static IEnumerable<string> RootOwnershipLaw()
        {
            var kinds = IdentityResolver.RootKinds;
            var last = kinds[kinds.Length - 1];
            if (last.Key != "GL")
                yield return "L28 gl-not-last: IdentityResolver.RootKinds ends with '" + last.Key + "' (" + last.Type.Name +
                             "), not 'GL' — the level root is the one whose member closure spans the whole level, so " +
                             "walking it earlier makes other roots' own visits a silent return";

            var closures = new Dictionary<string, HashSet<Type>>(StringComparer.Ordinal);
            foreach (var r in kinds) closures[r.Key] = TypeClosure(r.Type);
            var observed = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < kinds.Length; i++)
                for (int j = i + 1; j < kinds.Length; j++)
                {
                    var earlier = kinds[i];
                    var later = kinds[j];
                    var hitLater = ReachedBy(closures[earlier.Key], later.Type);
                    if (hitLater != null)
                        yield return "L28 root-owned-instance-two-paths: root '" + later.Key + "' (" + later.Type.Name +
                                     ") is reachable from the EARLIER root '" + earlier.Key + "' via " + hitLater.FullName +
                                     " — the earlier walk consumes the instance and '" + later.Key + "' emits nothing, " +
                                     "with no tombstone and no incident";
                    var hitEarlier = ReachedBy(closures[later.Key], earlier.Type);
                    if (hitEarlier == null) continue;
                    var row = later.Key + "->" + earlier.Key;
                    observed.Add(row);
                    if (!_declaredRootReach.Contains(row))
                        yield return "L28 undeclared-root-reach: root '" + earlier.Key + "' (" + earlier.Type.Name +
                                     ") is reachable from the LATER root '" + later.Key + "' via " + hitEarlier.FullName +
                                     " — legal ('" + earlier.Key + "' is walked first and owns it) but '" + later.Key +
                                     "' silently emits nothing for it: declare \"" + row + "\" or reorder the table";
                }
            foreach (var row in _declaredRootReach)
                if (!observed.Contains(row))
                    yield return "L28 stale-root-reach-declaration: \"" + row + "\" is declared but no longer observed — " +
                                 "the allowance now hides nothing and would mask a real overlap later";

            // The GL bridge's declared opt-outs. Asserted through OptOutReason, so an accidental
            // "bridge-unresolved" (a convention that merely fails TODAY) does not satisfy the law.
            foreach (var name in new[] { "TacUnits", "ModData", "ContextHelpData", "GeoscapeLog" })
            {
                var owner = typeof(PhoenixPoint.Geoscape.Levels.GeoLevelController);
                var declared = RailMeta.OptOutReason(owner, name);
                var f = RailType.Get(owner)?.FieldByName(name);
                if (f == null)
                    yield return "L28 declared-exclusion-absent: GeoLevelController." + name + " is not in the GL table at " +
                                 "all — the bridge no longer carries it, so the opt-out guards nothing";
                else if (declared == null || f.Class != FieldClass.Excluded || f.Exclude != declared)
                    yield return "L28 undeclared-exclusion: GeoLevelController." + name + " is " + f.Class + " (reason='" +
                                 (f.Exclude ?? "<none>") + "') — it must be an EXPLICIT opt-out in RailMeta._optOutMembers, " +
                                 "not a lookup that happens to fail today";
            }
        }

        // Root kinds that legitimately ride with an EMPTY covered set, with the reason. Grammar: root key.
        // A row that stops being observed (the root gains coverage) is a violation too, so this cannot rot
        // into a permanent allowance list — same non-rotting shape as _declaredRootReach.
        private static readonly Dictionary<string, string> _declaredEmptyRoots =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "MG", "GeoMissionGenerator is genuinely stateless — its def-derived caches are rebuilt in " +
                    "Start() and the save carries no generator data, so \"no persistent members\" is the " +
                    "TRUE answer, not a classification failure (IdentityResolver.Roots)" },
        };

        /// <summary>L33 — a declared root that ships NOTHING.
        ///
        /// Declaring a root is two independent acts: adding the row, and the classifier actually finding
        /// members under it. When the second silently fails the root is INERT — the walk enters, classifies
        /// zero covered members, emits no entry, and nothing anywhere says the subsystem does not mirror.
        /// That is this repo's dominant bug class with a root-shaped face, and it is exactly what the RCA
        /// could only PREDICT about the "ST" statistics root: <c>PhoenixStatistics</c> rides "direct" off
        /// <c>[SerializeType(SerializeAll, Embedded)]</c> (PhoenixStatistics.cs:10) — lose that attribute,
        /// or hand the row the accessor's DECLARED return type (<c>BaseStatistics</c>, which has no members
        /// at all), and the root reports covered=0/0 while looking perfectly healthy.
        ///
        /// <c>L31 actor-root-uncovered</c> asks this question for UnityEngine.Object roots that are
        /// structurally enabled. This is the same question for EVERY root, including the ones whose
        /// coverage arrives through a bridge DTO ("MK", "GL"), and it is the arm that makes an empty root a
        /// DECLARED fact with a reason ("MG" is genuinely stateless) instead of an accident.
        ///
        /// Scope, deliberately not hidden: runtime-registered mod-state roots
        /// (<c>IdentityResolver.RegisterModRoot</c>) are not in the static table and are not swept.</summary>
        private static IEnumerable<string> RootCoverageLaw()
        {
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in IdentityResolver.RootKinds)
            {
                // RailType.Get already RESOLVES the bridge (that is what the "[bridge:X]" source in the
                // baseline means), so this one number is the root's whole covered set, DTO members included.
                int covered = RailType.Get(r.Type)?.CoveredCount ?? 0;
                bool declared = _declaredEmptyRoots.ContainsKey(r.Key);
                if (covered == 0)
                {
                    observed.Add(r.Key);
                    if (!declared)
                        yield return "L33 root-covers-nothing: root '" + r.Key + "' (" + r.Type.Name + ") is declared in " +
                                     "IdentityResolver.RootKinds but the value rail covers NONE of its members — the walk " +
                                     "enters it and emits nothing, so the state under it never mirrors and no line anywhere " +
                                     "says so: fix the classification or declare it in _declaredEmptyRoots with the reason";
                }
                else if (declared)
                    yield return "L33 stale-empty-root-declaration: root '" + r.Key + "' (" + r.Type.Name + ") is declared " +
                                 "EMPTY but now covers " + covered + " member(s) — the reason string is false and the " +
                                 "allowance would mask a real regression to zero later";
            }
            foreach (var key in _declaredEmptyRoots.Keys)
                if (!observed.Contains(key) && !IdentityResolver.RootKinds.Any(r => string.Equals(r.Key, key, StringComparison.Ordinal)))
                    yield return "L33 stale-empty-root-declaration: \"" + key + "\" is declared empty but is not a root kind " +
                                 "in IdentityResolver.RootKinds at all — the row guards nothing";
        }

        /// <summary>L29 — the structural DESCEND shape: a Descend field going null↔non-null.
        ///
        /// This was the set-diff's blind third shape, and it swallowed BOTH directions in silence. On
        /// null→obj the walk descended and shipped value entries for an object the client does not have,
        /// so each one died at <c>GenericApplier</c>'s "entity not found" — one line per path, never
        /// retried. On obj→null NOTHING was emitted at all: the tombstone loop only ever deletes dict
        /// SUBkeys (<c>DiffEngine.cs:467-468</c>), a vanished path produces no entry and no tombstone, and
        /// the Descend arm's <c>val == null</c> just broke — no incident either. A finished mission stayed
        /// on the client's map forever with nothing anywhere saying so.
        ///
        /// The law's job is that a create/destroy the layer CANNOT express is named here rather than
        /// silently skipped. Arms, each a distinct failure:
        ///   • <b>path-shape</b> — the discriminator itself. <c>DiffEngine.IsDescendPath</c> is what picks
        ///     the host's create PAYLOAD and the client's apply BRANCH; get it wrong and a Descend create
        ///     is handed to the facility applier (or a facility create ships a type name). Table-driven.
        ///   • <b>declaration-dead / carrier-not-writable</b> — an enabled family must actually be carried
        ///     by a covered Descend field somewhere, and every carrier must be WRITABLE: create and
        ///     destroy are both a write of that member, so a read-only carrier means the marker can
        ///     neither appear nor disappear. This is the arm that asserts "a mission's arrival AND its
        ///     disappearance are expressible" — one member, both directions.
        ///   • <b>create-unconstructible</b> — the client builds the object the way LOAD does
        ///     (<c>RailMeta.ConstructLikeLoad</c>). Most GeoMission subclasses have NO parameterless ctor,
        ///     so if their <c>[SerializeCustomCreate]</c> ever goes away the mission can never appear.
        ///   • <b>create-param-uncarriable</b> — the honest limit of the create frame, now that the frame
        ///     CARRIES params (<c>RailMeta.EncodeDescendCreate</c>: type name + one leaf per param). A
        ///     custom create's params are WriteOnly members, so the value rail will never fill them
        ///     (SerializedMembers is ReadWrite-only) and the create packet is the only chance — it takes
        ///     that chance through the ordinary leaf codec, so the residual is exactly the LEAF codec's
        ///     reach: a param is carriable iff <c>RailMeta.LeafKindOf</c> knows its declared type. Scalars,
        ///     DefRef Guids and EntityRef stable ids all ride; a plain composite class with no stable id
        ///     cannot, because there is nothing to name it by. Named statically here and logged once at
        ///     apply time from the SAME predicate (<c>RailMeta.UncarriableCreateParams</c>), so the two
        ///     cannot drift.
        ///   • <b>nullable-unenabled</b> — recursive completeness. Enabling a family fixes ITS create, not
        ///     the create of a nullable Descend INSIDE it, which is the identical swallow one level down.
        ///     Asked the way the codec asks it: build a fresh instance and look.
        ///
        /// L30 rides this same sweep (same <c>Concretions</c> + <c>ConstructLikeLoad</c> work, so it is one
        /// pass, not two) but is a different question: L29 asks whether the object can be CREATED, L30 asks
        /// whether creating it MEANS anything.
        ///   • <b>descend-enabled-uncovered</b> — an enabled concretion the value rail covers NOTHING of.
        ///     The create then ships an object holding CLR defaults and no line anywhere says the values
        ///     are missing: a phantom. Enabling <c>Base.Utils.UnityDateTime</c> while its only member was
        ///     still excluded as "no persistent members (DateTime)" would have shipped a 01/01/0001 mission
        ///     clock exactly this way — which is why <c>LeafKind.DateTimeTicks</c> had to land first.
        ///   • <b>descend-create-frame-*</b> — the create frame driven encoder→decoder for real. This is
        ///     the arm L29's create-param predicate structurally cannot supply: that predicate is static
        ///     over <c>LeafKindOf</c>, so it stays GREEN if the payload reverts to a bare type name while
        ///     every create param silently goes back to arriving NULL.
        ///
        /// Scope note, deliberately not hidden: the nullable-unenabled sweep runs over the enabled
        /// families' own tables, not over the whole closure. Every other nullable Descend in the rail is
        /// the same latent class and is NOT swept here. The frame arms round-trip through a HEADLESS
        /// decode: a param's slot is asserted present and readable, but a DefRef/EntityRef param cannot be
        /// asserted to RESOLVE without a live DefRepository/GeoLevelController — the same documented gap
        /// class as L13's DefRef note.</summary>
        private static IEnumerable<string> StructuralDescendLaw(Assembly game, List<Type> types)
        {
            foreach (var probe in new[]
            {
                "T|root", "S#12|root", "V#3@abcd|root", "M#cart|root",
                "S#12.SerializationData.ActiveMission|descend", "F#g.Wallet|descend", "U#7.Progression|descend",
                "S#12.SerializationData.PhoenixBaseData.Layout._facilities#5|element",
                "S#12.SerializationData.PhoenixBaseData.Layout._facilities#5.ItemStorage|descend",
            })
            {
                var parts = probe.Split('|');
                var got = DiffEngine.IsDescendPath(parts[0]) ? "descend"
                        : parts[0].IndexOf('.') >= 0 ? "element" : "root";
                if (got != parts[1])
                    yield return "L29 descend-path-shape: '" + parts[0] + "' classifies as " + got + ", not " +
                                 parts[1] + " — IsDescendPath picks both the host's create payload and the " +
                                 "client's apply branch, so a misread ships a graph blob as a type name (or " +
                                 "hands a Descend create to the facility applier) with no log line";
            }

            // Every classified table the rail can hand the walk a Descend field from: the closure, plus the
            // recorded-DTO tables (the HOST walks the DTO object, so ITS table is what the structural gate
            // reads) and their live-twin tables (what the CLIENT writes through). Same derivation as the
            // snapshot's twin section, so the two cannot disagree about what exists.
            var tables = new List<(string Owner, RailType Table)>();
            foreach (var t in types)
            {
                var rt = RailType.Get(t);
                if (rt != null) tables.Add((t.FullName, rt));
                var dto = RailType.Get(t)?.FieldByName("SerializationData") != null ? RailMeta.FindBridge(t) : null;
                if (dto == null) continue;
                var host = RailType.Get(dto);
                if (host != null) tables.Add((dto.FullName + " (host DTO walk)", host));
                var bridged = RailType.GetBridged(t, dto);
                if (bridged != null) tables.Add((t.FullName + " <= " + dto.Name, bridged));
            }

            foreach (var fam in DiffEngine.StructuralDescendKinds)
            {
                int carriers = 0;
                foreach (var (owner, tab) in tables)
                    foreach (var f in tab.Fields)
                    {
                        if (f.Class != FieldClass.Descend || f.ValueType != fam) continue;
                        carriers++;
                        if (!f.IsWritable())
                            yield return "L29 descend-carrier-not-writable: " + owner + "." + f.Name + " (" +
                                         fam.Name + ") is structurally enabled but the member cannot be written — " +
                                         "create and destroy are both a write of it, so the object can neither " +
                                         "appear nor disappear on a client";
                    }
                if (carriers == 0)
                    yield return "L29 descend-declaration-dead: " + fam.FullName + " is declared in " +
                                 "DiffEngine.StructuralDescendKinds but NO covered Descend field carries it — " +
                                 "the row guards nothing and the walk records no path for it";

                var nullableSeen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ct in Concretions(game, fam))
                {
                    object made = null; string err = null;
                    try { made = RailMeta.ConstructLikeLoad(ct); }
                    catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
                    if (made == null)
                        yield return "L29 descend-create-unconstructible: " + ct.FullName + " cannot be built the " +
                                     "way LOAD builds it (custom create, else parameterless ctor) — " +
                                     (err ?? "the create returned null") + ". A client can never make this one appear";
                    var cp = RailMeta.UncarriableCreateParams(ct);
                    if (cp.Length > 0)
                        yield return "L29 descend-create-param-uncarriable: " + ct.FullName + " has custom-create " +
                                     "params (" + string.Join(",", cp) + ") whose declared type the leaf codec " +
                                     "cannot express (not a scalar, DefRef or EntityRef), so the create frame " +
                                     "ships NULL for them and the value rail never fills them either " +
                                     "(WriteOnly is outside SerializedMembers)";
                    // ─── L30 — enabling a family is only real if the client can FILL what it builds ───
                    // Rides this sweep rather than its own (same Concretions/ConstructLikeLoad work), but it
                    // is a different violation class: L29 asks whether the object can be CREATED, L30 asks
                    // whether creating it means anything.
                    string frameName = null; object[] frameArgs = null; string frameErr = null;
                    if (made != null)
                    {
                        try
                        {
                            var frame = RailMeta.EncodeDescendCreate(made);
                            frameName = RailMeta.DescendCreateTypeName(frame);
                            frameArgs = RailMeta.DecodeCreateArgs(frame, ct, null);
                        }
                        catch (Exception ex) { frameErr = ex.GetType().Name + ": " + ex.Message; }
                        if (frameErr != null)
                            yield return "L30 descend-create-frame-threw: " + ct.FullName + " create frame " +
                                         "encode→decode threw " + frameErr + " — the client would decline every create";
                        else if (frameName != ct.FullName)
                            yield return "L30 descend-create-frame-typename: " + ct.FullName + " round-tripped as '" +
                                         (frameName ?? "<null>") + "' — the client resolves the concrete type from " +
                                         "this string and validates it against the carrier field, so it would decline";
                        else if (RailMeta.HasCreateParams(ct) && frameArgs == null)
                            yield return "L30 descend-create-params-not-carried: " + ct.FullName + " takes " +
                                         "custom-create params but the frame carried no slot for them, so they arrive " +
                                         "NULL on every client-created instance. This is the arm L29's static " +
                                         "create-param predicate CANNOT see: it stays green on a payload that " +
                                         "reverted to a bare type name, which is exactly how _enemyFaction arrived empty";
                    }
                    var crt = made == null ? null : RailType.Get(ct);
                    if (crt == null) continue;
                    // A create the value rail can never fill is a PHANTOM: the object appears on the client
                    // holding CLR defaults, and no log line anywhere says the values are missing. Enabling
                    // Base.Utils.UnityDateTime while its only member was still excluded as "no persistent
                    // members (DateTime)" would have shipped a 01/01/0001 mission clock exactly this way.
                    if (crt.CoveredCount == 0)
                        yield return "L30 descend-enabled-uncovered: " + ct.FullName + " is structurally enabled " +
                                     "but the value rail covers NONE of its " + crt.Fields.Count + " member(s) — the " +
                                     "create would ship an object the client can never fill, i.e. a default-valued " +
                                     "phantom, silently";
                    foreach (var f in crt.Fields)
                    {
                        if (f.Class != FieldClass.Descend || f.HopFi != null) continue;
                        if (DiffEngine.IsStructuralDescendType(f.ValueType)) continue;
                        object live = null;
                        try { live = f.GetValue(made); } catch { continue; }
                        if (live != null) continue;
                        // Dedup on the DECLARING owner: a member inherited by 12 concretions is ONE gap.
                        var declOwner = (f.Fi != null ? f.Fi.DeclaringType : f.Pi?.DeclaringType) ?? ct;
                        if (!nullableSeen.Add(declOwner.FullName + "." + f.Name)) continue;
                        yield return "L29 descend-nullable-unenabled: " + declOwner.FullName + "." + f.Name + " (" +
                                     f.ValueType.Name + ") is NULL on a freshly built " + fam.Name + " and is not " +
                                     "structurally enabled — when the host fills it the client gets value entries " +
                                     "for an object it has no way to create, and when the host clears it the " +
                                     "client is never told at all";
                    }
                }
            }
        }

        /// <summary>L31 — the structural ACTOR shape: a ROOT whose entity is a MonoBehaviour-bound
        /// <c>UnityEngine.Object</c>. Third of the three structural shapes to get a law (L29 = the Descend
        /// field, L28 = root ownership), and the one where the wrong payload is CATASTROPHIC rather than
        /// merely lossy: the game's own spawners run inside the deserializer
        /// (<c>[SerializeCustomCreate] ActorComponent.CreateActor</c>, decompile ActorComponent.cs:336-377,
        /// inherited by GeoVehicle.cs:26 → GeoActor.cs:15), so decoding a native graph blob of a GeoVehicle
        /// would CREATE a duplicate actor on the client — and every actor its members reach — instead of
        /// mirroring one (law 3 "never replace a MonoBehaviour-bound instance"). That is the apply-side twin
        /// of <c>L3 unity-object-blobbed</c>, and it is a mistake available in ONE character: adding "V#" to
        /// <c>StructuralPrefixes</c> without the payload arm.
        ///
        /// Arms, each a distinct failure:
        ///   • <b>actor-root-blobbed</b> — the host's payload CHOICE, driven through the very function
        ///     <c>EmitStructural</c> switches on (<c>DiffEngine.PayloadFor</c>), never re-derived here. A
        ///     predicate over types would stay green if the emit arm were reverted; this goes red. Table
        ///     driven over all three shapes at once, so a change that fixes the actor arm by breaking the
        ///     Descend or blob arm cannot pass either.
        ///   • <b>actor-root-undeclared</b> — every actor-typed root kind must be either structurally enabled
        ///     or a DECLARED opt-out carrying its reason. This is the arm that keeps "which roots can appear
        ///     or vanish at runtime" a reviewable table instead of an accident: an actor root that CAN vanish
        ///     and is neither enabled nor declared is precisely the swallow this batch closed for "V#" (one
        ///     "not enabled" line at create, then every value delta under it dies at "entity not found"
        ///     forever, with nothing ever retrying).
        ///   • <b>actor-optout-stale</b> — an opt-out row for a prefix that is not an actor root kind, or that
        ///     IS enabled, guards nothing. Same shape as L28's stale-root-reach-declaration: a declaration is
        ///     a claim, so it decays and must be swept.
        ///   • <b>actor-param-not-defref</b> — the frame carries its ONE param, the spawn
        ///     <c>ComponentSetDef</c>, through the ordinary leaf codec. That only stays a REFERENCE while
        ///     <c>LeafKindOf</c> answers DefRef; if it ever answered Composite the frame would start walking
        ///     a def graph onto the wire, i.e. lose the very property that makes an actor create safe.
        ///   • <b>actor-create-frame-typename / -shape</b> — the frame driven for real. The type name must
        ///     survive through <c>DescendCreateTypeName</c> (the ONE reader both create frames share, which is
        ///     why breaking it breaks two shapes at once), and the bytes the writer emits must be exactly what
        ///     the reader's slot-count check demands: <c>[typeName][1][one leaf]</c>, consumed to the last
        ///     byte.
        ///   • <b>actor-root-uncovered</b> — L30's question for this shape: an enabled actor root the value
        ///     rail covers NOTHING of would be spawned holding CLR defaults, a phantom aircraft, silently.
        ///
        /// Honest gap, same class as L13's DefRef note: a real def cannot be asserted to RESOLVE here.
        /// <c>DecodeLeaf</c>'s DefRef arm needs a live <c>DefRepository</c> (RailMeta.cs:1207) and the host
        /// read needs a live <c>ComponentSet</c> component, so the frame arms drive a NULL def — they prove
        /// the frame's shape and its decline paths, not that a GeoVehicle's def survives a round trip.</summary>
        private static IEnumerable<string> StructuralActorLaw()
        {
            // The host's payload choice, driven through DiffEngine.PayloadFor — all three shapes in one
            // table so no arm can be "fixed" by breaking another.
            foreach (var probe in new (string Key, Type Type, DiffEngine.CreatePayload Want)[]
            {
                ("V#3@abcd", typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle), DiffEngine.CreatePayload.ActorFrame),
                ("S#12", typeof(PhoenixPoint.Geoscape.Entities.GeoSite), DiffEngine.CreatePayload.ActorFrame),
                ("U#7", typeof(PhoenixPoint.Geoscape.Entities.GeoCharacter), DiffEngine.CreatePayload.GraphBlob),
                ("S#12.SerializationData.PhoenixBaseData.Layout._facilities#5",
                 typeof(PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility), DiffEngine.CreatePayload.GraphBlob),
                ("S#12.SerializationData.ActiveMission",
                 typeof(PhoenixPoint.Geoscape.Entities.GeoMission), DiffEngine.CreatePayload.DescendFrame),
                // An ACTOR reached through a Descend FIELD is still an actor: GeoStealAircraftMission
                // carries _stealAircraft (GeoVehicle), so shape order matters, not just type.
                ("S#12.SerializationData.ActiveMission._stealAircraft",
                 typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle), DiffEngine.CreatePayload.ActorFrame),
            })
            {
                var got = DiffEngine.PayloadFor(probe.Key, probe.Type);
                if (got != probe.Want)
                    yield return "L31 actor-root-blobbed: DiffEngine.PayloadFor('" + probe.Key + "', " +
                                 probe.Type.Name + ") picks " + got + ", not " + probe.Want + " — this is the " +
                                 "function EmitStructural switches on, so a MonoBehaviour actor landing on " +
                                 "GraphBlob means the client DECODES a blob and re-CREATES that actor plus every " +
                                 "actor its members reach, instead of mirroring one (law 3, L3's apply-side twin)";
            }

            var kinds = IdentityResolver.RootKinds;
            var prefixes = DiffEngine.StructuralRootPrefixes;
            var optOuts = DiffEngine.StructuralRootOptOuts;

            foreach (var r in kinds)
            {
                if (!DiffEngine.IsActorPayloadType(r.Type)) continue;
                bool enabled = prefixes.Any(p => r.Key.StartsWith(p, StringComparison.Ordinal));
                bool declared = optOuts.Any(o => string.Equals(o.Prefix, r.Key, StringComparison.Ordinal));
                if (enabled && declared)
                    yield return "L31 actor-optout-stale: root '" + r.Key + "' (" + r.Type.Name + ") is BOTH " +
                                 "structurally enabled and listed in DiffEngine.StructuralRootOptOuts — the " +
                                 "opt-out row guards nothing and its stated reason is now false";
                else if (!enabled && !declared)
                    yield return "L31 actor-root-undeclared: root '" + r.Key + "' (" + r.Type.Name + ") is a " +
                                 "UnityEngine.Object actor root that is neither structurally enabled nor a " +
                                 "declared opt-out in DiffEngine.StructuralRootOptOuts — if it can appear or " +
                                 "vanish at runtime the client gets ONE 'not enabled' line and then every value " +
                                 "delta under it dies at 'entity not found' forever, with nothing retrying";
                if (!enabled) continue;
                var rt = RailType.Get(r.Type);
                var dto = RailMeta.FindBridge(r.Type);
                var bridged = dto == null ? null : RailType.GetBridged(r.Type, dto);
                if ((rt?.CoveredCount ?? 0) + (bridged?.CoveredCount ?? 0) == 0)
                    yield return "L31 actor-root-uncovered: root '" + r.Key + "' (" + r.Type.Name + ") is " +
                                 "structurally enabled but the value rail covers NONE of its members — the " +
                                 "create would spawn an actor the client can never fill, i.e. a default-valued " +
                                 "phantom, silently";
            }

            // Stale rows the loop above cannot see: a prefix matching no actor root kind AT ALL.
            foreach (var o in optOuts)
                if (!kinds.Any(r => DiffEngine.IsActorPayloadType(r.Type) &&
                                    string.Equals(r.Key, o.Prefix, StringComparison.Ordinal)))
                    yield return "L31 actor-optout-stale: \"" + o.Prefix + "\" is declared in " +
                                 "DiffEngine.StructuralRootOptOuts but no UnityEngine.Object root kind in " +
                                 "IdentityResolver.RootKinds has that key — the row guards nothing";

            // The frame's ONE param must stay a REFERENCE, not a walked graph.
            RailMeta.LeafKindOf(typeof(Base.Core.ComponentSetDef), out var setDefKind);
            if (setDefKind != LeafKind.DefRef)
                yield return "L31 actor-param-not-defref: ComponentSetDef classifies as " + setDefKind +
                             ", not DefRef — the actor create frame carries the spawn def through the ordinary " +
                             "leaf codec, so anything but a Guid reference means the frame started EMITTING def " +
                             "state instead of naming it, and an actor create can embed a graph again";

            // The frame, driven encoder→decoder for real (null def — see the honest gap above).
            const string vehicleType = "PhoenixPoint.Geoscape.Entities.GeoVehicle";
            var readBack = RailMeta.DescendCreateTypeName(RailMeta.WriteActorCreateFrame(vehicleType, null));
            if (readBack != vehicleType)
                yield return "L31 actor-create-frame-typename: the actor frame round-tripped as '" +
                             (readBack ?? "<null>") + "', not '" + vehicleType + "' — the client resolves the " +
                             "concrete type from this string and validates it before spawning, so it would " +
                             "decline every create. DescendCreateTypeName is the ONE reader both create frames " +
                             "share, so breaking it breaks the Descend shape in the same stroke";

            // The frame the WRITER produces must be exactly what the READER's slot-count check demands:
            // [string typeName][byte 1][one leaf], consumed to the last byte. DecodeActorCreateDef rejects
            // any n != 1, so a writer that drifts to a different arity makes every create decline — and a
            // writer that appends a second leaf leaves trailing bytes the reader would never see. Asserted by
            // re-reading the real frame with the same primitives rather than by feeding the decoder a bogus
            // payload: headless there is no DefRepository, so DecodeActorCreateDef can only ever return null
            // (DecodeLeaf's DefRef arm yields Unresolved, RailMeta.cs:1212) and a return-value assertion
            // would be unfalsifiable decoration.
            int slots; long left;
            using (var ms = new MemoryStream(RailMeta.WriteActorCreateFrame(vehicleType, null)))
            using (var r = new BinaryReader(ms))
            {
                r.ReadString();
                slots = ms.Position < ms.Length ? r.ReadByte() : -1;
                RailMeta.DecodeLeaf(r, typeof(Base.Core.ComponentSetDef), null);
                left = ms.Length - ms.Position;
            }
            if (slots != 1 || left != 0)
                yield return "L31 actor-create-frame-shape: the actor frame declares " + slots +
                             " create-param slot(s) and leaves " + left + " trailing byte(s) — the reader " +
                             "(DecodeActorCreateDef) rejects any count but 1, so this arity makes the client " +
                             "decline every actor create, and trailing bytes are payload it will never read";
        }

        /// <summary>Types a root can reach through its own classified tables — Descend / element types /
        /// composite leaves, i.e. exactly the edges <c>DiffEngine.VisitEntity</c> follows. Excluded fields
        /// are not edges (nothing rides them). Declared types only, seed itself not included.</summary>
        private static HashSet<Type> TypeClosure(Type seed)
        {
            var seen = new HashSet<Type>();
            var queue = new Queue<Type>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var rt = RailType.Get(queue.Dequeue());
                if (rt?.Fields == null) continue;
                foreach (var f in rt.Fields)
                {
                    if (f.Class == FieldClass.Excluded) continue;
                    Type next = null;
                    switch (f.Class)
                    {
                        case FieldClass.Descend: next = f.ValueType; break;
                        case FieldClass.EntityCollection:
                        case FieldClass.EntityList: next = f.ElemType; break;
                        case FieldClass.Leaf when f.Leaf == LeafKind.Composite: next = f.ValueType; break;
                    }
                    if (next != null && next != seed && seen.Add(next)) queue.Enqueue(next);
                }
            }
            return seen;
        }

        /// <summary>The closure member that reaches <paramref name="rootType"/>, or null. Assignability in
        /// BOTH directions: a declared base reaches its concretions (the live walk types by obj.GetType()),
        /// and a declared subclass is reached by its base root kind.</summary>
        private static Type ReachedBy(HashSet<Type> closure, Type rootType)
        {
            foreach (var c in closure)
                if (c == rootType || c.IsAssignableFrom(rootType) || rootType.IsAssignableFrom(c)) return c;
            return null;
        }

        private static DiffEngine.Entry Ent(string path, ushort fieldIdx, string subKey, byte value) =>
            new DiffEngine.Entry { Path = path, FieldIdx = fieldIdx, SubKey = subKey, Value = new[] { value },
                                   Key = path + "" + fieldIdx + "" + subKey };

        /// <summary>L26 — the event-window BACKLOG derivation, headless. <c>EventPopup.Backlog</c> and
        /// <c>EventPopup.Mode</c> are pure functions over <c>GeoscapeEventRecord</c>, which has a public
        /// ctor and public Trigger/SelectChoice/Complete/Reset and touches no Unity and no level — so the
        /// one thing the rail cannot state as metadata ("which windows does a peer still owe the player,
        /// in what order") is assertable right here. Every arm is one of the silent swallows this engine
        /// replaced: the seed that ate a joiner's whole backlog with no log line, a picker offered for a
        /// record somebody already resolved (= a double grant on click), a cursor that retires nothing
        /// or too much, a Reset record replayed forever, and dictionary-order output (law 6).</summary>
        private static IEnumerable<string> BacklogLaw()
        {
            var open = new PhoenixPoint.Geoscape.Events.GeoscapeEventRecord("EV_open", Ticks(100));
            var done = new PhoenixPoint.Geoscape.Events.GeoscapeEventRecord("EV_done", Ticks(200));
            done.SelectChoice(1);
            done.Complete(Ticks(210));
            // Insertion order is the REVERSE of the required output order, so a dict walk cannot pass by luck.
            var records = new Dictionary<string, PhoenixPoint.Geoscape.Events.GeoscapeEventRecord>(StringComparer.Ordinal)
                { { done.EventId, done }, { open.EventId, open } };

            var backlog = EventPopup.Backlog(records, 0);
            if (backlog.Count != 2)
                yield return "L26 backlog-swallowed-on-seed: cursor 0 over 1 Triggered + 1 Completed record yields " +
                             backlog.Count + " window(s), not 2 — a peer's accumulated history is eaten with no log line";
            else if (backlog[0].EventId != "EV_open" || backlog[1].EventId != "EV_done")
                yield return "L26 backlog-nondeterministic: order came back as " + backlog[0].EventId + "," +
                             backlog[1].EventId + " — that is dictionary order, not (LastTriggerAt, EventId) ascending";

            if (EventPopup.Mode(PhoenixPoint.Geoscape.Events.GeoscapeEventRecordState.Triggered) != "picker")
                yield return "L26 picker-lost: a Triggered record no longer maps to 'picker' — an OPEN decision would " +
                             "be shown as read-only history and nobody could answer it";
            foreach (var s in new[] { PhoenixPoint.Geoscape.Events.GeoscapeEventRecordState.SelectedChoice,
                                      PhoenixPoint.Geoscape.Events.GeoscapeEventRecordState.Completed,
                                      PhoenixPoint.Geoscape.Events.GeoscapeEventRecordState.MigratedCompleted })
                if (EventPopup.Mode(s) != "outcome")
                    yield return "L26 picker-for-resolved: state " + s + " maps to '" + EventPopup.Mode(s) + "', not " +
                                 "'outcome' — the peer is offered a choice that is already frozen, and taking it " +
                                 "re-grants the reward (GeoscapeEvent.IsCompleted is per-instance)";
            if (EventPopup.Mode(PhoenixPoint.Geoscape.Events.GeoscapeEventRecordState.Reset) != null)
                yield return "L26 reset-replayed: a Reset record is displayable — ReEneableEvent (GeoscapeEvent.cs:103-106) " +
                             "puts the event back in the pool, so that window would re-raise forever";

            var resolvedOnly = new Dictionary<string, PhoenixPoint.Geoscape.Events.GeoscapeEventRecord>(StringComparer.Ordinal)
                { { done.EventId, done } };
            long at = done.LastTriggerAt.TimeSpan.Ticks;
            if (EventPopup.Backlog(resolvedOnly, at).Count != 0)
                yield return "L26 backlog-not-idempotent: a record at the cursor still rides — the same window re-raises " +
                             "on every pump for the rest of the session";
            if (EventPopup.Backlog(resolvedOnly, at - 1).Count != 1)
                yield return "L26 cursor-overshoots: a record NEWER than the cursor was retired — a window the player " +
                             "never saw is gone for good";
            // An UNRESOLVED record is never retired by the cursor: the host's currently-OPEN window is the one
            // thing the save transfer does not carry (GeoscapeViewSwitchQuery.GetRestorableData:28 walks only
            // the pending list), so the derivation is all that can bring it back.
            if (EventPopup.Backlog(records, long.MaxValue).Count != 1)
                yield return "L26 open-decision-retired: a still-Triggered record dropped out of the backlog at a maxed " +
                             "cursor — a host window open at join time would then reach the joiner never";

            // The cursor is per-peer LOCAL progress and it is the only thing standing between a reload and a
            // popup storm, so it outlives the process as a STRING (PlayerPrefs) — which makes its CODEC the
            // whole contract. A locale-sensitive write or read hands back 0 = "replay this peer's entire
            // event history", with nothing logged as wrong: the silent-swallow class again, one layer down.
            // Both halves are asserted here because both are pure; the PlayerPrefs I/O around them cannot be
            // (a method that merely references an ECall extern fails to JIT headless, ClientIdentity.cs:82-88).
            foreach (var ticks in new[] { 0L, 1L, at, long.MaxValue })
            {
                var text = EventPopup.FormatCursor(ticks);
                long back = EventPopup.ParseCursor(text);
                if (back != ticks)
                    yield return "L26 cursor-round-trip: " + ticks + " persists as '" + text + "' and reads back as " +
                                 back + " — after a reload this peer either replays every window it already " +
                                 "clicked through or is owed none of them, and the only symptom is doubles";
            }
            if (EventPopup.Backlog(resolvedOnly, EventPopup.ParseCursor(EventPopup.FormatCursor(at))).Count != 0)
                yield return "L26 cursor-round-trip: a RESTORED cursor does not retire the record the live cursor " +
                             "retired — the window the player just closed comes back on every reload";
            if (EventPopup.ParseCursor("") != 0 || EventPopup.ParseCursor(null) != 0)
                yield return "L26 cursor-round-trip: an ABSENT stored cursor does not read back as 0 — a peer's first " +
                             "join would take a garbage cursor instead of falling back to the record seed";
        }

        private static Base.Core.TimeUnit Ticks(long t) => Base.Core.TimeUnit.FromTimeSpan(TimeSpan.FromTicks(t));

        /// <summary>L27 — the event-answer arbiter, which is what makes "the first choice is frozen for
        /// everyone" true. <see cref="EventSync.Validate"/> is deliberately pure (record STATE + index +
        /// choice count, no live level) precisely so this law can exist headless: the in-game window where
        /// it matters is two peers clicking inside one RTT, which no manual test reproduces on demand, and
        /// a double grant is silent — the reward simply lands twice.</summary>
        private static IEnumerable<string> AnswerValidatorLaw()
        {
            const int count = 3;
            var frozen = new[] { GeoscapeEventRecordState.SelectedChoice, GeoscapeEventRecordState.Completed,
                                 GeoscapeEventRecordState.MigratedCompleted, GeoscapeEventRecordState.Reset };

            // An OPEN decision with a legal index must be accepted, or nobody can ever answer anything.
            foreach (var idx in new[] { -1, 0, count - 1 })
            {
                var why = EventSync.Validate(GeoscapeEventRecordState.Triggered, idx, count);
                if (why != null)
                    yield return "L27 valid-answer-refused: an open (Triggered) record refused choice index " + idx +
                                 " of " + count + " — \"" + why + "\"; every peer's click would be rejected and no " +
                                 "event could ever be answered";
            }

            // The freeze itself: a record that already carries an answer must never accept a second one.
            // GeoscapeEvent.IsCompleted is per-INSTANCE (GeoscapeEvent.cs:36), so nothing downstream would
            // catch this — a synthesised instance re-runs ChoiceReward.Apply and grants the whole reward
            // again (wallet, sites, diplomacy, created soldiers GeoEventChoiceOutcome:296/305).
            foreach (var st in frozen)
            {
                var why = EventSync.Validate(st, 0, count);
                if (why == null)
                    yield return "L27 double-answer-accepted: state " + st + " accepted a second answer — the loser of " +
                                 "a two-peer race re-grants the entire reward and the first choice is not frozen";
                else if (why.Trim().Length == 0)
                    yield return "L27 silent-reject: state " + st + " was refused with a BLANK reason — IntentRail.Reject " +
                                 "would log an empty line, which is the swallow class this family exists to kill";
            }

            // Range: the index comes off a peer's own mirror, so a stale or def-mismatched one must be
            // named, not indexed into (data.Choices[index] would throw IndexOutOfRange) and not silently
            // coerced to the "no choice" resolution.
            foreach (var idx in new[] { count, count + 7, -2, int.MinValue })
            {
                var why = EventSync.Validate(GeoscapeEventRecordState.Triggered, idx, count);
                if (why == null)
                    yield return "L27 index-unchecked: choice index " + idx + " passed validation against " + count +
                                 " choices — the host would throw inside the handler or resolve a choice nobody clicked";
                else if (why.Trim().Length == 0)
                    yield return "L27 silent-reject: index " + idx + " was refused with a BLANK reason — the rejected " +
                                 "click leaves no readable trace";
            }
            // A def with no choices at all: only the "no choice" resolution is addressable.
            if (EventSync.Validate(GeoscapeEventRecordState.Triggered, 0, 0) == null)
                yield return "L27 index-unchecked: index 0 passed validation against an EMPTY choice list";
        }

        /// <summary>L32 — the AIRCRAFT intent family (RCA gap A2). Three properties, all headless:
        /// (a) EVERY DECLARED op has a validator that reads replicated facts — the op set is read off
        /// <see cref="VehicleSync"/>'s own <c>Op*</c> constants, so an op added without a
        /// <see cref="VehicleSync.Validate"/> case goes RED here instead of shipping an unchecked gesture
        /// (a law over a table stays green when the real switch is reverted; this one drives the switch);
        /// (b) a rejected op REJECTS — a non-blank reason and the vehicle's own root key as the re-emit
        /// scope, never a throw and never a blank line; (c) capture is BLOCK-FIRST — asserted positively
        /// on this family's own patch classes, which is the thing L19 cannot promise: <c>ResultShipLaw</c>
        /// exempts any patch class whose Harmony targets are all <c>*.View.*</c> types. (This family's
        /// seams happen to sit on <c>GeoVehicle</c>, a MODEL type, so L19 does cover them today — the arm
        /// keeps that true if a seam ever moves onto a screen.)
        /// Plus the slot assignment, the one piece of real arithmetic in the family, driven as the REAL
        /// generic function with plain guid strings (a live GeoVehicleEquipment needs a DefRepository).
        /// UNCOVERED and in-game only: <c>GeoNavComponent.FindPath</c> (needs a live navigation graph),
        /// so "no route" is the one refusal this law cannot exercise.</summary>
        private static IEnumerable<string> VehicleIntentLaw()
        {
            var legal = new VehicleSync.Facts
            {
                Resolved = true, OwnedByPlayer = true, CanRedirect = true, TargetResolved = true,
                TargetIsIdleCurrentSite = false, Docked = true, SlotCountDelta = 0,
            };

            var ops = typeof(VehicleSync).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.IsLiteral && f.FieldType == typeof(byte) && f.Name.StartsWith("Op", StringComparison.Ordinal))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => new { f.Name, Op = (byte)f.GetRawConstantValue() }).ToList();
            if (ops.Count == 0)
                yield return "L32 vacuous: VehicleSync declares no Op* constant — the validator law checked nothing";

            foreach (var o in ops)
            {
                var why = VehicleSync.Validate(o.Op, legal);
                if (why != null)
                    yield return "L32 unvalidated-op: " + o.Name + " (op " + o.Op + ") refused a fully LEGAL gesture — \"" + why +
                                 "\"; either the op has no case in Validate (the default arm refuses by design) or its " +
                                 "gates are inverted, and every peer's click on it is rejected forever";
                foreach (var c in StaleVehicleCases(o.Op, legal))
                {
                    var w = VehicleSync.Validate(o.Op, c.Value);
                    if (w == null)
                        yield return "L32 stale-accepted: " + o.Name + " accepted " + c.Key + " — the native call runs on a " +
                                     "stale or forged target, which is exactly the divergence this validator exists to stop";
                    else if (w.Trim().Length == 0)
                        yield return "L32 silent-reject: " + o.Name + " refused " + c.Key + " with a BLANK reason — " +
                                     "IntentRail.Reject would log an empty line, the swallow class again";
                }
            }

            // ─── the slot assignment (setEquipment's arithmetic), through the real function ──────────
            Func<string, string> id = s => s;

            // Vehicle-FIRST is not a preference: with the same def on the aircraft and in storage, taking it
            // out of storage would remove-and-re-add an item every re-flush, so the loadout never settles.
            var onV = new List<string> { "a", "b" };
            var inS = new List<string> { "a", "c" };
            var taken = new List<string>(); var fromS = new List<bool>();
            var err = VehicleSync.TakeSlots(new[] { "a", "c" }, onV, inS, id, taken, fromS);
            if (err != null)
                yield return "L32 pool-exact-refused: a fully satisfiable slot list was refused — \"" + err +
                             "\"; no client could ever change an aircraft's loadout";
            else if (fromS.Count != 2 || fromS[0] || !fromS[1])
                yield return "L32 pool-churn: a def already on the aircraft was taken from STORAGE instead of reused in " +
                             "place — every re-flush then removes and re-adds it and the loadout never settles";
            else if (onV.Count != 1 || onV[0] != "b")
                yield return "L32 pool-leftover: the UNEQUIPPED slot did not stay in the vehicle pool — the handler puts " +
                             "the leftovers back into storage, so the item is destroyed instead of stored";
            else if (inS.Count != 1 || inS[0] != "a")
                yield return "L32 pool-overdraw: storage did not end up holding exactly what no slot asked for — either a " +
                             "consumed item stayed on the shelf (the host duplicated it) or an untouched one vanished";

            onV = new List<string> { "a" }; inS = new List<string>();
            taken = new List<string>(); fromS = new List<bool>();
            err = VehicleSync.TakeSlots(new[] { "zz" }, onV, inS, id, taken, fromS);
            if (err == null)
                yield return "L32 pool-missing-accepted: a def on NEITHER the aircraft nor the host's storage was assigned " +
                             "anyway — the host equips an item nobody owns, out of a stale client mirror";
            else if (err.Trim().Length == 0)
                yield return "L32 silent-reject: an unavailable def was refused with a BLANK reason";

            onV = new List<string> { "a" }; inS = new List<string>();
            taken = new List<string>(); fromS = new List<bool>();
            err = VehicleSync.TakeSlots(new[] { "", "a" }, onV, inS, id, taken, fromS);
            if (err != null || taken.Count != 2 || taken[0] != null || taken[1] != "a")
                yield return "L32 pool-empty-slot: an EMPTY slot did not survive as a null placeholder — " +
                             "ReplaceEquipments needs the nulls (GeoVehicle.cs:899-910 AddNullModule), otherwise every " +
                             "later slot shifts up and the aircraft loses a slot";

            var onW = new List<string>(); var onM = new List<string>(); inS = new List<string> { "a" };
            var tW = new List<string>(); var fW = new List<bool>();
            var tM = new List<string>(); var fM = new List<bool>();
            err = VehicleSync.TakeSlots(new[] { "a" }, onW, inS, id, tW, fW)
                  ?? VehicleSync.TakeSlots(new[] { "a" }, onM, inS, id, tM, fM);
            if (err == null)
                yield return "L32 pool-double-spend: ONE stored item satisfied TWO slots — the weapon and module passes do " +
                             "not share the storage pool, so the host duplicates equipment out of thin air";

            // ─── the reject scope, and block-first as a positive assertion ───────────────────────────
            if (VehicleSync.Scope("V#3@guid") != "V#3@guid" || VehicleSync.Scope(null) != null || VehicleSync.Scope("") != null)
                yield return "L32 reject-unscoped: the reject scope is not the aircraft's own root key — IntentRail.Reject " +
                             "then re-emits the WHOLE covered graph (or nothing at all) instead of the vehicle subtree";

            int seams = 0;
            foreach (var t in typeof(VehicleSync).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                                                 .OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                if (t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false).Length == 0) continue;
                seams++;
                if (t.GetMethod("Prefix", AllMembers) == null)
                    yield return "L32 capture-not-block-first: " + t.Name + " declares no Prefix — a seam that cannot " +
                                 "block lets the client write the model first, which is a result-ship";
                if (t.GetMethod("Postfix", AllMembers) != null)
                    yield return "L32 capture-postfix: " + t.Name + " declares a Postfix — the native mutation has " +
                                 "already happened by then, so the intent ships a RESULT (L19's rule, asserted here " +
                                 "because ResultShipLaw exempts view-only patch classes)";
            }
            if (seams < 2)
                yield return "L32 capture-missing: VehicleSync declares " + seams + " Harmony capture seam(s) — gap A2 needs " +
                             "both the route funnel (GeoVehicle.StartTravel) and the loadout funnel (ReplaceEquipments); " +
                             "a gesture that reaches no seam produces no log line at all";

            // The loadout gesture's STORAGE half rides EquipStorageGate.TargetMethods() — the exact shape L23
            // says it cannot read ("a TargetMethods() body is not [readable]"), so drive the real iterator: a
            // null yielded from it makes PatchAll THROW into the one warning MultiplayerMain swallows, which
            // kills every later patch in the same PatchAll.
            var gate = typeof(IntentRail).Assembly.GetType("Multiplayer.Network.Sync.EquipStorageGate");
            var targets = gate == null ? null : gate.GetMethod("TargetMethods", AllMembers);
            if (targets == null)
                yield return "L32 storage-gate-missing: EquipStorageGate.TargetMethods did not resolve — the client's own " +
                             "storage write-back has no gate at all";
            else
            {
                var yielded = ((IEnumerable<MethodBase>)targets.Invoke(null, null)).ToList();
                for (int i = 0; i < yielded.Count; i++)
                    if (yielded[i] == null)
                        yield return "L32 storage-gate-unbound: EquipStorageGate.TargetMethods yields NULL at index " + i +
                                     " — a mistyped or drifted method name; PatchAll throws and the warning is swallowed";
                if (!yielded.Any(m => m != null && m.Name == "UpdateAircraftStorage"))
                    yield return "L32 storage-gate-unbound: UIStateVehicleRoster.UpdateAircraftStorage is not among " +
                                 "EquipStorageGate's targets — the client's own AircraftItemStorage write-back is ungated " +
                                 "and diverges permanently (the loadout gesture's other half)";
            }

            // The AUTO-MANUFACTURE gate row. L23 checks that a DECLARED target resolves; it cannot know a
            // required gate is MISSING, and this one is only reachable through a structural V# destroy —
            // an in-game path nobody stumbles into by hand. So assert the row exists, by the method it must
            // cover rather than by the class that covers it.
            const string autoManufacture = "UpdateManufacturing";
            if (HarmonyLib.AccessTools.Method(typeof(PhoenixPoint.Geoscape.Levels.GeoFaction), autoManufacture) == null)
                yield return "L32 automanufacture-gate-unbound: GeoFaction." + autoManufacture + " does not resolve — the " +
                             "funnel the gate row covers has drifted and the gate now patches nothing";
            else if (!DeclaredTypes(typeof(IntentRail).Assembly).Any(t => t
                         .GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false)
                         .OfType<HarmonyLib.HarmonyPatch>()
                         .Any(a => a.info != null && a.info.declaringType == typeof(PhoenixPoint.Geoscape.Levels.GeoFaction) &&
                                   a.info.methodName == autoManufacture)))
                yield return "L32 automanufacture-gate-missing: nothing patches GeoFaction." + autoManufacture +
                             " — _automanufactureVehicles is set on BOTH peers (GeoFaction.cs:392), so a client applying a " +
                             "V# structural destroy enqueues a replacement aircraft locally (:2095-2098), an authoritative " +
                             "client-side write the diff rail cannot correct";
        }

        private static Type[] DeclaredTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
        }

        private static IEnumerable<KeyValuePair<string, VehicleSync.Facts>> StaleVehicleCases(byte op, VehicleSync.Facts legal)
        {
            // Universal: no op may act on an aircraft the host does not have, or on one that is not the
            // shared player faction's (a client must never order an alien or haven vehicle).
            var f = legal; f.Resolved = false;
            yield return new KeyValuePair<string, VehicleSync.Facts>("an unresolved vehicle root key", f);
            f = legal; f.OwnedByPlayer = false;
            yield return new KeyValuePair<string, VehicleSync.Facts>("a foreign-faction aircraft", f);

            if (op == VehicleSync.OpTravelTo)
            {
                f = legal; f.TargetResolved = false;
                yield return new KeyValuePair<string, VehicleSync.Facts>("a destination that does not exist host-side", f);
                f = legal; f.CanRedirect = false;
                yield return new KeyValuePair<string, VehicleSync.Facts>("an aircraft that cannot be redirected", f);
                f = legal; f.TargetIsIdleCurrentSite = true;
                yield return new KeyValuePair<string, VehicleSync.Facts>("a route to the site it is already idle at", f);
            }
            if (op == VehicleSync.OpSetEquipment)
            {
                f = legal; f.Docked = false;
                yield return new KeyValuePair<string, VehicleSync.Facts>("an aircraft that is not docked at a Phoenix base", f);
                f = legal; f.SlotCountDelta = 1;
                yield return new KeyValuePair<string, VehicleSync.Facts>("one slot too many", f);
                f = legal; f.SlotCountDelta = -2;
                yield return new KeyValuePair<string, VehicleSync.Facts>("two slots too few", f);
            }
        }

        private static IEnumerable<string> HandleSweepLaw()
        {
            var asm = typeof(IntentRail).Assembly;
            Type[] declared;
            try { declared = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { declared = ex.Types.Where(t => t != null).ToArray(); }

            int swept = 0;
            foreach (var t in declared.Where(t => t.Namespace == typeof(IntentRail).Namespace)
                                      .OrderBy(t => t.FullName, StringComparer.Ordinal))
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                   .Where(f => typeof(MemberInfo).IsAssignableFrom(f.FieldType))
                                   .OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    object v = null; string err = null;
                    try { v = f.GetValue(null); } catch (Exception ex) { err = (ex.InnerException ?? ex).GetType().Name; }
                    if (err != null)
                    { yield return "L23 handle-cctor-threw: " + t.Name + "." + f.Name + " — static init threw " + err; continue; }
                    swept++;
                    if (v == null)
                        yield return "L23 handle-unbound: " + t.Name + "." + f.Name + " resolved to null — the seam it " +
                                     "feeds (patch target, native derive or repaint) is dead and fails silently";
                }
            if (swept == 0)
                yield return "L23 vacuous: no static reflection handle was read — the sweep is asleep";

            // Same law, the OTHER handle kind: a class-level [HarmonyPatch(type, "name")] whose target does
            // not resolve. This one is worse than a null MethodInfo — Harmony THROWS at PatchAll, and
            // MultiplayerMain.cs:40-42 catches it into a single LogWarning, so ONE typo'd private-method
            // name silently kills every patch after it in the same PatchAll. Only attribute-declared
            // targets are readable statically (a TargetMethods() body is not); MethodType.Normal only.
            int checkedTargets = 0;
            foreach (var t in declared.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                Type owner = null; string method = null; Type[] argTypes = null; bool normal = true, any = false;
                try
                {
                    foreach (HarmonyLib.HarmonyPatch a in t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false))
                    {
                        any = true;
                        owner = owner ?? a.info?.declaringType;
                        method = method ?? a.info?.methodName;
                        argTypes = argTypes ?? a.info?.argumentTypes;
                        if (a.info?.methodType != null && a.info.methodType != HarmonyLib.MethodType.Normal) normal = false;
                    }
                }
                catch { continue; } // unloadable attribute type (cross-mod target absent here) — not our null
                if (!any || !normal || owner == null || method == null) continue;
                checkedTargets++;
                if (HarmonyLib.AccessTools.Method(owner, method, argTypes) == null)
                    yield return "L23 patch-target-unresolved: " + t.Name + " patches " + owner.Name + "." + method +
                                 " which does not resolve — PatchAll throws and MultiplayerMain swallows it into a " +
                                 "warning, killing every later patch in the same PatchAll";
            }
            if (checkedTargets == 0)
                yield return "L23 vacuous: no attribute-declared Harmony target was resolved — the target check is asleep";
        }

        /// <summary>BFS from a screen teardown through presentation code; returns "Type.Method" of the first
        /// void instance command it can reach on a rail-covered type, else null. Constructors are not
        /// commands — a fresh instance is not the live model.</summary>
        private static string FirstModelCommand(MethodBase root, Assembly game, HashSet<Type> covered)
        {
            var seen = new HashSet<int> { root.MetadataToken };
            var queue = new Queue<MethodBase>();
            queue.Enqueue(root);
            while (queue.Count > 0)
                foreach (var callee in Callees(queue.Dequeue(), game, directCallsOnly: true))
                {
                    var owner = callee.DeclaringType;
                    if (owner?.FullName == null || !seen.Add(callee.MetadataToken)) continue;
                    if (IsPresentation(owner)) { queue.Enqueue(callee); continue; }
                    if (!callee.IsConstructor && !callee.IsStatic && !IsAccessor(callee) && covered.Contains(owner) &&
                        (callee as MethodInfo)?.ReturnType == typeof(void))
                        return owner.Name + "." + callee.Name;
                }
            return null;
        }

        /// <summary>Property or event ACCESSOR, resolved through the declaring type's PropertyInfo/EventInfo
        /// tables (metadata, not a name prefix). Two things drop out of the law with it, both measured, not
        /// assumed: event add/remove — `Faction.ScannerCapacityChanged -= Handler` is subscription bookkeeping
        /// on a covered type, not state (it flagged 9 screens); and property SETTERS — the one real case,
        /// UIStateDiplomacy, captures `_wasPaused` in EnterState:26 and writes it back at ExitState:39, so an
        /// Exit+Enter cycle provably ends where it started. LIMITATION: a screen that commits a genuine edit
        /// through a property setter in its teardown is therefore invisible to this law.</summary>
        private static bool IsAccessor(MethodBase m)
        {
            if (!m.IsSpecialName || m.DeclaringType == null) return false;
            foreach (var p in m.DeclaringType.GetProperties(AllMembers))
                if (Same(p.GetGetMethod(true), m) || Same(p.GetSetMethod(true), m)) return true;
            foreach (var e in m.DeclaringType.GetEvents(AllMembers))
                if (Same(e.GetAddMethod(true), m) || Same(e.GetRemoveMethod(true), m)) return true;
            return false;
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        private static bool IsPresentation(Type t) =>
            t.FullName.IndexOf(".View.", StringComparison.Ordinal) >= 0 ||
            t.FullName.StartsWith("Base.UI", StringComparison.Ordinal);

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
        private static IEnumerable<MethodBase> Callees(MethodBase m, Assembly asm, bool directCallsOnly = false)
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
                // directCallsOnly = call/callvirt only: a delegate LOAD (ldftn, e.g. `x.Event -= Handler`)
                // references a method without ever running it, which L21 must not read as an edge.
                if (op.OperandType == OperandType.InlineMethod &&
                    (!directCallsOnly || op == OpCodes.Call || op == OpCodes.Callvirt))
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs); } catch { }
                    if (callee != null && callee.Module.Assembly == asm) yield return callee;
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
            // The PROPERTY-hop arm of L14: GeoActor.Surface is an auto-property, and Transform is not
            // constructible headless, so the MECHANICS are exercised on a stand-in of the same shape
            // (get-only class-typed property) while the real Surface.position wiring is asserted on the
            // live GeoVehicle metadata above.
            public PhoenixPoint.Geoscape.Core.GeoVehicleStats StatsProp => Stats;
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
