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
using HarmonyLib;
using Multiplayer.Network.Sync;
using Multiplayer.Util;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;
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
            laws.AddRange(TextBindCodecLaw());
            laws.AddRange(OwnerBackRefCodecLaw());
            laws.AddRange(ExitWriteBackLaw(types));
            laws.AddRange(HandleSweepLaw());
            laws.AddRange(CrcBackstopLaw());
            laws.AddRange(FragmentLaw());
            laws.AddRange(ReadOnlyFacadeLaw());
            laws.AddRange(EventRaiseLaw());
            laws.AddRange(PreAnsweredEventLaw());
            laws.AddRange(ReplayVisualsLaw());
            laws.AddRange(DisplayOrderLaw());
            laws.AddRange(OwnAnswerLaw());
            laws.AddRange(WindowCoverageLaw(game));
            laws.AddRange(ModalCoverageLaw());
            laws.AddRange(AnswerValidatorLaw());
            laws.AddRange(FunnelCoverageLaw(game));
            laws.AddRange(EventTokenDerefLaw());
            laws.AddRange(VehicleIntentLaw());
            laws.AddRange(VehicleGestureLaw(game));
            laws.AddRange(DerivedPoseLaw(game));
            laws.AddRange(HuskContentLaw(game));
            laws.AddRange(RootOwnershipLaw(types));
            laws.AddRange(RootCoverageLaw());
            laws.AddRange(StructuralDescendLaw(game, types));
            laws.AddRange(StructuralActorLaw());
            laws.AddRange(RepaintRelevanceLaw(types, game));
            laws.AddRange(SlicedWalkLaw());
            laws.AddRange(AugmentRepaintGuardLaw());
            laws.AddRange(AugmentClickParityLaw());
            laws.AddRange(DerivedExplorationLaw());
            laws.AddRange(HostSelfRepaintLaw());
            laws.AddRange(DerivedObjectivesLaw(game));
            laws.AddRange(SiteStatusTwinLaw());
            laws.AddRange(RejectScopeLaw());
            laws.AddRange(PeerLocalContainmentLaw(game));
            laws.AddRange(MistCoverageLaw(game));
            laws.AddRange(PersistentHudLaw(game));
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
                typeof(Multiplayer.Network.Sync.MistState),      // root "M#mist" (mist coverage, L59)
                // Ref-addressable SUB-entities (IdentityResolver.IsRefAddressableType). Their state ships as
                // elements of the collection that OWNS them, but that collection lives in a TWIN table
                // (GeoHaven <= InstanceData . Zones) and the expansion below only follows a type's own
                // Descend/element fields — so nothing enqueues them and their coverage table would silently
                // vanish from the snapshot. It rode here by accident while GeoStealAircraftMission._zone was
                // still a Descend; seeded explicitly now that the field is a ref.
                typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoHavenZone),
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
        /// <summary>The def-laundering vector L11 belts, by TYPE. Asked by FullName rather than typeof so the
        /// harness keeps compiling if the game ever drops the type — a missing vector is L35's problem.</summary>
        private static bool IsTextBind(Type t) => t?.FullName == "Base.UI.LocalizedTextBind";

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
                      " + mod-state roots registered at runtime: ScrapCartState (\"M#cart\"), MistState (\"M#mist\")\n");
            sb.Append("seeded (not roots — types the live walk reaches only through a runtime subtype): GeoPhoenixFacility" +
                      " | structural-descend concretions of: " +
                      string.Join(", ", DiffEngine.StructuralDescendKinds.Select(k => k.Name)) + "\n");
            sb.Append("polymorphic-codec: " + (polymorphicCodec ? "yes" : "no") + "\n");
            sb.Append("def-ownership law: RUNTIME-ONLY — DefOwnership's reference-identity set needs a live DefRepository,\n");
            sb.Append("  so walk-time def-aliasing (an instance reachable from BOTH a live entity and the def graph) is\n");
            sb.Append("  INVISIBLE here; this harness asserts only the static belt (L11: a LocalizedTextBind may ride ONLY as\n");
            sb.Append("  a leaf VALUE — never Descend/EntityList, which would write into the instance the def graph shares;\n");
            sb.Append("  ItemDef.GetDisplayName returns def state by ref. L35 keeps the leaf codec itself non-vacuous).\n");
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
                    // ViewElementDef.DisplayName1/2 by reference, decompile ItemDef.cs:165-173), so writing
                    // INTO one is a def-state write on the client. The real law is reference identity over a
                    // live DefRepository — untestable headless (see header).
                    //
                    // NARROWED (was: "no bind rides covered at all") the commit that added
                    // LeafKind.TextBind, which is the N7 exit this law's own note pointed at. What made a
                    // covered bind dangerous was never the coverage, it was the CLASS: Descend /
                    // EntityCollection / EntityList / a Composite member all make the client write into the
                    // instance it already holds — the shared one. A LEAF replaces the entity's reference with
                    // a freshly-constructed bind and never touches the shared instance, which is the exact
                    // argument both ownership arms already exempt leaves by (DiffEngine.cs:844-848,
                    // GenericApplier.cs:624-635). So the belt now asserts the part that is still true: a bind
                    // may ride ONLY as a leaf value. Falsify by deleting the LeafKindOf arm — every bind
                    // field classifies Descend/EntityList again and this fires on all of them.
                    if (IsTextBind(f.ValueType) && f.Class != FieldClass.Leaf)
                        laws.Add("L11 text-bind-written-in-place: " + t.FullName + "." + f.Name +
                                 " carries LocalizedTextBind as " + f.Class + " — anything but Leaf writes INTO the " +
                                 "instance the client holds, which the def graph may share");
                    if (IsTextBind(f.ElemType) && f.Class != FieldClass.LeafList)
                        laws.Add("L11 text-bind-written-in-place: " + t.FullName + "." + f.Name +
                                 " carries LocalizedTextBind ELEMENTS as " + f.Class + " — only a LeafList rebuilds " +
                                 "them as fresh values instead of writing into shared instances");
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
                // Blob OWNER edges, harvested from the same walk (the sweep already knows every one of them):
                // "which types can a blob of this type be nested under". The L15 owner-back-ref arm below is
                // the only consumer — it needs the whole ancestry, not just the frame that enqueued a type.
                var parents = new Dictionary<Type, HashSet<Type>>();
                void Blob(Type child, Type owner)
                {
                    if (!parents.TryGetValue(child, out var ps)) parents[child] = ps = new HashSet<Type>();
                    ps.Add(owner);
                    queue.Enqueue(child);
                }
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
                        if (f.Class == FieldClass.Descend && !RailMeta.LeafKindOf(f.ValueType, out _)) Blob(f.ValueType, nt);
                        if (f.ElemType != null && !RailMeta.LeafKindOf(f.ElemType, out _)) Blob(f.ElemType, nt);
                        if (f.DictValType != null && !RailMeta.LeafKindOf(f.DictValType, out _)) Blob(f.DictValType, nt);
                    }
                }
                lines.Sort(StringComparer.Ordinal);
                foreach (var l in lines) sb.Append(l + "\n");
                sb.Append("  (types swept: " + visited.Count + ")\n");
                laws.AddRange(OwnerBackRefLaw(visited, parents, new HashSet<Type>(l15Seeds)));
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
            // Live types the apply surface addresses that are NOT in the classifier closure — the twin walk's
            // nested-component dispatch targets (GeoHaven / GeoAlienBase arrive as HavenData / AlienBaseData
            // slots and are applied via GetComponent, so they never enter `types` yet DO land in the applier's
            // touched set). Published here so L38 asks the same question this section answers instead of
            // re-deriving the walk and drifting from it.
            int twinRes = 0, twinGap = 0, twinDispatch = 0;
            var twinPairs = new List<(Type live, Type dto)>();
            foreach (var t in types)
                if (RailType.Get(t)?.FieldByName("SerializationData") != null && RailMeta.FindBridge(t) != null)
                    twinPairs.Add((t, RailMeta.FindBridge(t)));
            var twinSeen = new HashSet<string>(StringComparer.Ordinal);
            for (int p = 0; p < twinPairs.Count; p++)
            {
                var (live, dto) = twinPairs[p];
                BridgedApplyTargets.Add(live);
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
                    // L11, twin-table arm — same narrowing as the direct arm above. GeoSiteInstaceData.Motto
                    // and GeoPhoenixBase+InstanceData.LocationDescription are the two binds that ride here,
                    // and they are precisely the ones the deleted type-name refusal dropped (the (d) NOTEXT
                    // haven names/mottos symptom): as leaves they now mirror, without a def write.
                    if (IsTextBind(f.ValueType) && f.Class != FieldClass.Leaf)
                        laws.Add("L11 text-bind-written-in-place: " + live.FullName + "<=" + dto.Name + "." + f.Name +
                                 " carries LocalizedTextBind as " + f.Class + " — anything but Leaf writes INTO the " +
                                 "instance the client holds, which the def graph may share");
                    if (IsTextBind(f.ElemType) && f.Class != FieldClass.LeafList)
                        laws.Add("L11 text-bind-written-in-place: " + live.FullName + "<=" + dto.Name + "." + f.Name +
                                 " carries LocalizedTextBind ELEMENTS as " + f.Class + " — only a LeafList rebuilds " +
                                 "them as fresh values instead of writing into shared instances");
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

        // ─── L15 owner-back-ref probe types (the shape CommonItemData/AmmoManager have) ───
        // Synthetic on purpose: a real GeoItem is `roundtrip=unconstructible` here (its custom create needs an
        // ItemDef, i.e. a ScriptableObject), so the ONE thing the static arm cannot check — that the decoder
        // actually re-wires — needs a type the harness can build. Same move as L30/L31 for the create frames.
        /// <summary>[SerializeType] on the INTERFACE is what makes the serializer discover an interface-typed
        /// member at all (Serializer.cs:308 IsSerializeableType) — `ICommonItem` carries it for the same
        /// reason. Without it the member is invisible and the probe would silently test nothing.</summary>
        [Base.Serialization.General.SerializeType]
        private interface IOwnerProbe { }

        [Base.Serialization.General.SerializeType]
        private sealed class OwnerProbe : IOwnerProbe
        {
            [Base.Serialization.General.SerializeMember] public int N;
            [Base.Serialization.General.SerializeMember] public ChildProbe Child;
        }

        [Base.Serialization.General.SerializeType]
        private sealed class ChildProbe
        {
            /// <summary>CommonItemData.OwnerItem's shape: interface-typed, so "untyped/interface member".</summary>
            [Base.Serialization.General.SerializeMember] public IOwnerProbe Back;
            [Base.Serialization.General.SerializeMember] public GrandChildProbe Deep;
        }

        [Base.Serialization.General.SerializeType]
        private sealed class GrandChildProbe
        {
            /// <summary>AmmoManager.ParentItem's shape: the assignable owner is TWO frames up, because the
            /// frame in between (ChildProbe / CommonItemData) is not assignable to the interface.</summary>
            [Base.Serialization.General.SerializeMember] public IOwnerProbe Back;
            [Base.Serialization.General.SerializeMember] public int M;
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

        /// <summary>L15, owner-back-ref arm — a back-ref the husk gate COUNTS must really have an owner.
        ///
        /// <c>RailMeta.SalvagesOwnerBackRef</c> makes an untypeable member (an interface / bare object) count
        /// as CARRIED because the blob decoder re-wires it from the frame the child is nested in. That count
        /// is a promise, and the runtime cannot check it: the decoder sees ONE path (the one it is decoding)
        /// and can only say "no assignable owner above me" after the fact, in a log line, with the member
        /// already null. The static question — is an assignable owner above this type on EVERY blob path? —
        /// is answerable only here, from the sweep's own owner edges. Same division of labour as L16: the
        /// applier keys on the marker, the harness proves the marker's premise.
        ///
        /// A type with no owner edge at all is a blob ROOT (an EntityList element): nothing is above it, so a
        /// back-ref member on one is a REAL husk that the count would hide. Computed as a monotone fixpoint
        /// rather than a recursion so an owner CYCLE (a back-ref is exactly what loops) terminates.</summary>
        private static IEnumerable<string> OwnerBackRefLaw(HashSet<Type> swept,
                                                          Dictionary<Type, HashSet<Type>> parents,
                                                          HashSet<Type> seeds)
        {
            foreach (var t in swept.OrderBy(x => x.FullName, StringComparer.Ordinal))
            {
                var rt = RailType.Get(t);
                if (rt == null) continue;
                foreach (var f in rt.Fields)
                {
                    if (!RailMeta.SalvagesOwnerBackRef(t, f)) continue;
                    if (OwnerOnEveryPath(t, f.ValueType, swept, parents, seeds)) continue;
                    yield return "L15 ownerbackref-no-owner: " + t.FullName + "." + f.Name + " (" + f.ValueType.Name +
                                 ") is counted as CARRIED by the owner re-wire, but this type " +
                                 (seeds.Contains(t) ? "IS a blob ROOT (nothing is above it)" : "has a blob path with no " +
                                  f.ValueType.Name + "-assignable owner above it") +
                                 " — the member lands NULL there and the husk gate licenses the blob anyway";
                }
            }
        }

        /// <summary>Does every blob path to <paramref name="t"/> pass through an owner assignable to
        /// <paramref name="iface"/>? Monotone fixpoint over the sweep's owner edges — "uncovered" starts as
        /// the blob ROOTS (nothing above them) and spreads to any type one of whose owners is neither
        /// assignable nor covered itself. A fixpoint rather than recursion because an owner CYCLE is exactly
        /// what a back-ref creates.</summary>
        private static bool OwnerOnEveryPath(Type t, Type iface, HashSet<Type> swept,
                                             Dictionary<Type, HashSet<Type>> parents, HashSet<Type> seeds)
        {
            var uncovered = new HashSet<Type>();
            foreach (var x in swept)
                if (seeds.Contains(x) || !parents.TryGetValue(x, out var ps) || ps.Count == 0) uncovered.Add(x);
            for (bool grew = true; grew; )
            {
                grew = false;
                foreach (var x in swept)
                {
                    if (uncovered.Contains(x) || !parents.TryGetValue(x, out var ps)) continue;
                    foreach (var p in ps)
                        if (!iface.IsAssignableFrom(p) && uncovered.Contains(p)) { uncovered.Add(x); grew = true; break; }
                }
            }
            return !uncovered.Contains(t);
        }

        /// <summary>L15, owner-back-ref EXECUTED arm — the re-wire must actually run.
        ///
        /// The static arm above proves an assignable owner is reachable; it cannot prove the decoder uses it,
        /// and a mechanism nobody executes is the silent-swallow shape this project fights (green build, green
        /// harness, null back-ref in game). So this drives the real encode→decode over a synthetic blob whose
        /// shape IS the game's:
        ///   • <c>ChildProbe.Back</c> is left NULL on the "host" — the encode side's reference match (case (b))
        ///     writes nothing for a null, so ONLY the decode re-wire can fill it. That is the arm that closes
        ///     `CommonItemData.OwnerItem` for real rather than depending on the host's own pointer.
        ///   • <c>GrandChildProbe.Back</c> sits under a frame that is NOT assignable to the interface, exactly
        ///     like `AmmoManager` under `CommonItemData`: the re-wire has to walk PAST it. An "own owner only"
        ///     implementation passes the first arm and fails this one.</summary>
        private static IEnumerable<string> OwnerBackRefCodecLaw()
        {
            var backF = RailType.Get(typeof(ChildProbe))?.FieldByName("Back");
            if (backF == null || !RailMeta.SalvagesOwnerBackRef(typeof(ChildProbe), backF))
            {
                yield return "L15 ownerbackref-uncounted: ChildProbe.Back is not recognised as an owner back-ref (" +
                             (backF == null ? "<absent from the table>" : backF.Class + "/" + backF.Exclude) +
                             ") — the probe cannot test the re-wire and the real back-refs are husks again";
                yield break;
            }

            var f = new RailField
            {
                Name = "probe", Class = FieldClass.EntityList,
                ValueType = typeof(List<OwnerProbe>), ElemType = typeof(OwnerProbe),
            };
            var owner = new OwnerProbe { N = 3 };
            owner.Child = new ChildProbe { Back = null, Deep = new GrandChildProbe { M = 4, Back = null } };

            List<object> back = null;
            string err = null;
            try { back = RailMeta.DecodeEntityList(RailMeta.EncodeEntityList(f, new List<OwnerProbe> { owner }), f, null); }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
            if (err != null) { yield return "L15 ownerbackref-round-trip threw " + err; yield break; }

            var got = back != null && back.Count == 1 ? back[0] as OwnerProbe : null;
            if (got?.Child == null)
            { yield return "L15 ownerbackref-round-trip: the probe blob did not come back with its child"; yield break; }

            if (!ReferenceEquals(got.Child.Back, got))
                yield return "L15 ownerbackref-not-rewired: ChildProbe.Back came back as " +
                             (got.Child.Back == null ? "NULL" : "a foreign object") +
                             " — a blob-rebuilt child keeps a null owner back-ref and NREs on first use " +
                             "(CommonItemData.ItemDef => OwnerItem.ItemDef)";
            if (got.Child.Deep == null)
                yield return "L15 ownerbackref-round-trip: the probe's grandchild did not come back";
            else if (!ReferenceEquals(got.Child.Deep.Back, got))
                yield return "L15 ownerbackref-not-transitive: GrandChildProbe.Back came back as " +
                             (got.Child.Deep.Back == null ? "NULL" : "a foreign object") +
                             " — the re-wire did not look PAST the non-assignable frame between them, which is " +
                             "exactly AmmoManager.ParentItem under CommonItemData";
        }

        /// <summary>L35 — the text-bind codec class: a member whose CONTENT the rail used to refuse and now
        /// CARRIES must round-trip its localization KEY, and a bind that rebuilds without a resolvable key
        /// must be named rather than handed to the UI as a null.
        ///
        /// Why a law and not just a round-trip case in L4: the coverage this codec unblocks is a RE-INCLUSION,
        /// exactly like L22's. Four subsystems (the geoscape log, faction diplomacy state, sabotage faction
        /// requests, the displayed faction objectives) were refused ONLY because their blob leaves were
        /// LocalizedTextBind — the husk gate refuses a blob whose content the rail refuses (43ac747). Delete
        /// the one LeafKindOf arm and all four go back to Excluded with no rail file otherwise changing and
        /// nothing failing: silence, which is this project's dominant bug class. So non-vacuity is asserted
        /// FIRST, then the bytes.
        ///
        /// The bind is TWO members, not one (decompile Base.UI/LocalizedTextBind.cs:11/:13): the key, and the
        /// private `_doNotLocalize` that decides whether the key is a key or a literal (Localize:37-41 returns
        /// it verbatim when set). A codec that carried only the key would turn every literal-text bind into a
        /// failed lookup, so the flag is round-tripped in both states here.</summary>
        private static IEnumerable<string> TextBindCodecLaw()
        {
            var bindT = typeof(Base.UI.LocalizedTextBind);

            // (a) NON-VACUITY — the leaf kind exists, so the four subsystems above stay unblocked.
            if (!RailMeta.LeafKindOf(bindT, out var kind) || kind != LeafKind.TextBind)
            {
                yield return "L35 text-bind-not-a-leaf: LocalizedTextBind is not LeafKind.TextBind (" +
                             kind + ") — it classifies as a class again, the husk gate re-refuses every blob " +
                             "containing one, and the geoscape log / diplomacy state / faction requests stop " +
                             "mirroring in SILENCE";
                yield break; // every arm below would report the same one cause
            }

            // The tag must stay outside the marker space it shares the first byte with. L7 does this for the
            // two dict sentinels; the three list markers are not LeafKinds but occupy the same slot.
            foreach (var (mname, mval) in new[]
            {
                ("EntityListMarker", RailMeta.EntityListMarker),
                ("OrderVectorMarker", RailMeta.OrderVectorMarker),
                ("DictCensusMarker", RailMeta.DictCensusMarker),
                ("DictTombstone", RailMeta.DictTombstone),
            })
                if ((byte)LeafKind.TextBind == mval)
                    yield return "L35 text-bind-tag-collision: LeafKind.TextBind encodes to the same first byte as " +
                                 mname + " (" + mval + ")";

            // (b) the KEY and the flag survive, in both flag states; (c) a null bind stays absent; (f) an
            // EMPTY key still rebuilds a NON-NULL bind — GeoscapeLogEntry.GenerateMessage:23-25 dereferences
            // Text and Parameters unconditionally, and its catch is FormatException only, so a null there is
            // the NRE this whole codec exists to prevent.
            foreach (var (key, noLoc) in new[] { ("KEY_ABC", false), ("literal text", true), ("", false) })
            {
                var back = RoundTripLeaf(bindT, new Base.UI.LocalizedTextBind(key, noLoc)) as Base.UI.LocalizedTextBind;
                if (back == null)
                { yield return "L35 text-bind-lost: a bind with key '" + key + "' decoded to null"; continue; }
                if (back.LocalizationKey != key)
                    yield return "L35 text-bind-key-round-trip: '" + key + "' came back as '" + back.LocalizationKey + "'";
                if (BindNoLocalize(back) != noLoc)
                    yield return "L35 text-bind-flag-round-trip: _doNotLocalize " + noLoc + " came back as " +
                                 BindNoLocalize(back) + " for key '" + key + "' — a literal would become a failed lookup";
            }
            if (RoundTripLeaf(bindT, null) != null)
                yield return "L35 text-bind-null: an absent bind decodes as a present one";

            // (d) the ELEMENT form — GeoscapeLogEntry.Parameters is a LocalizedTextBind[], which rides as a
            // LeafList of this kind. Array shape on purpose: that is the declaration in the game.
            var pf = new RailField
            {
                Name = "Parameters", Class = FieldClass.LeafList,
                ValueType = typeof(Base.UI.LocalizedTextBind[]), ElemType = bindT,
            };
            var src = new[] { new Base.UI.LocalizedTextBind("P0"), new Base.UI.LocalizedTextBind("P1", true) };
            var got = RailMeta.DecodeFieldValue(RailMeta.EncodeFieldValue(pf, src), pf, null, out _) as List<object>;
            if (got == null || got.Count != 2)
                yield return "L35 text-bind-list: a 2-element bind array decoded as " +
                             (got == null ? "null" : got.Count + " elements");
            else
                for (int i = 0; i < 2; i++)
                    if ((got[i] as Base.UI.LocalizedTextBind)?.LocalizationKey != src[i].LocalizationKey)
                        yield return "L35 text-bind-list-order: element " + i + " is '" +
                                     (got[i] as Base.UI.LocalizedTextBind)?.LocalizationKey + "', expected '" +
                                     src[i].LocalizationKey + "'";

            // (e) the codec is only worth its bytes if the blob it unblocks is actually licensed. The log
            // entry is the witness found BY HAND in 43ac747: every member carried, husk EMPTY, so the husk
            // gate lets the keyless entry list ride.
            var entry = typeof(PhoenixPoint.Geoscape.Levels.GeoscapeLogEntry);
            var ert = RailType.Get(entry);
            var textF = ert?.FieldByName("Text");
            if (textF == null || textF.Class != FieldClass.Leaf || textF.Leaf != LeafKind.TextBind)
                yield return "L35 log-entry-text-refused: GeoscapeLogEntry.Text rides as " +
                             (textF == null ? "<absent>" : textF.Class + "/" + textF.Leaf) +
                             " — a rebuilt entry carries Text=null and GenerateMessage() NREs on it";
            var eh = RailMeta.HuskMembers(entry);
            if (eh.Count > 0)
                yield return "L35 log-entry-husked: GeoscapeLogEntry still husks " + string.Join(",", eh) +
                             " — the husk gate refuses the log's keyless entry list and the log does not mirror";
        }

        private static object RoundTripLeaf(Type declared, object v)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) RailMeta.EncodeLeaf(w, declared, v);
                ms.Position = 0;
                using (var r = new BinaryReader(ms, Encoding.UTF8, true)) return RailMeta.DecodeLeaf(r, declared, null);
            }
        }

        /// <summary>Reads the bind's private flag. NOT a static field: a static initializer touching a game
        /// type runs before Main installs the AssemblyResolve handler, and the whole class then fails to load
        /// (the same hazard the NoInlining on Run guards).</summary>
        private static bool BindNoLocalize(Base.UI.LocalizedTextBind b) =>
            (bool)typeof(Base.UI.LocalizedTextBind)
                .GetField("_doNotLocalize", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b);

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
            if (fHp == null || fHp.Class != FieldClass.Leaf || (fHp.HopFi?.Length != 1 || fHp.HopFi[0].Name != "Stats") || fHp.Fi?.Name != "HitPoints")
                yield return "L14 twin-coercion: GeoVehicle.HitPoints no longer routes through Stats.HitPoints";
            if (fName == null || fName.Class != FieldClass.Leaf || fName.Fi?.Name != "_vehicleName")
                yield return "L14 twin-coercion: GeoVehicle.Name no longer lands in _vehicleName (the Name property substitutes a localized default)";
            if (fOwnerS == null || fOwnerS.Class != FieldClass.Leaf || !fOwnerS.FactionRef)
                yield return "L14 twin-coercion: GeoSite.OwnerFactionDef lost the def→GeoFaction coercion";
            // The REDUCED-DTO rung (RailMeta.LiveTypeWins): the game records a def as its string Id and an
            // enum as its underlying int, and the rail retypes the field to the LIVE type so the existing
            // DefRef/Enum leaf codec carries it with no id⇄def or int⇄enum machinery. Both members below were
            // DECLARED GAPS until the rung landed; without it they silently fall back to "dto-twin
            // unresolved" (a haven's assigned research topic and a scavenging site's type stop mirroring).
            foreach (var (liveT, dtoMember, want) in new[]
                     {
                         (typeof(PhoenixPoint.Geoscape.Entities.GeoHaven), "AssignedResearchId",
                          typeof(PhoenixPoint.Geoscape.Entities.Research.ResearchDef)),
                         (typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoScavengingSite), "ScavengingSiteType",
                          typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoScavengingSiteType)),
                     })
            {
                var bridge = RailMeta.FindBridge(liveT);
                var fRed = bridge == null ? null : RailType.GetBridged(liveT, bridge)?.FieldByName(dtoMember);
                if (fRed == null || fRed.Class != FieldClass.Leaf || fRed.ValueType != want)
                    yield return "L14 twin-coercion: " + liveT.Name + "." + dtoMember + " lost the reduced-DTO rung — " +
                                 "expected a Leaf of " + want.Name + ", got " +
                                 (fRed == null ? "no field at all" : fRed.Class + " of " + fRed.ValueType?.Name) +
                                 "; the member stops mirroring";
            }
            // An aircraft's position rides through a PROPERTY hop (GeoActor.Surface is an auto-property,
            // decompile GeoVehicle.cs:89), so a field-only ResolveAliasChain silently classifies both
            // carriers "dto-twin unresolved". Both are now DELIBERATELY excluded — the client derives its
            // own pose from the mirrored order (L43) — which means this law's job INVERTED but did not go
            // away: it now guards the DIFFERENCE between the two ways a member stops riding. A declared
            // opt-out is a decision; an unresolvable twin is a bug that would hide behind the opt-out's
            // reason string forever. So assert BOTH — excluded by exactly the declared reason, and the
            // mapping underneath still resolving through Surface onto Transform's own member.
            foreach (var (dtoName, leafName, valType) in new[]
                     {
                         ("SurfacePos", "position", typeof(UnityEngine.Vector3)),
                         ("SurfaceRot", "rotation", typeof(UnityEngine.Quaternion)),
                     })
            {
                var fSurf = twinV?.FieldByName(dtoName);
                var declared = RailMeta.OptOutReason(typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle), dtoName);
                if (declared == null)
                    yield return "L14 twin-coercion: GeoVehicle." + dtoName + " has no declared pose opt-out — if it is " +
                                 "riding again the client's own navigation fights the host's pose deltas, and if it is " +
                                 "excluded the reason is now an accident rather than a decision";
                else if (fSurf == null || fSurf.Class != FieldClass.Excluded || fSurf.Exclude != declared)
                    yield return "L14 twin-coercion: GeoVehicle." + dtoName + " is not excluded by the declared pose " +
                                 "opt-out (" + (fSurf == null ? "absent" : fSurf.Class + " / " + fSurf.Exclude) +
                                 ") — a mirrored pose makes the client step at the rail's walk cadence (L43)";
                else
                {
                    MemberInfo surfLive = null; MemberInfo[] hop = null;
                    try { surfLive = RailMeta.ResolveLive(typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle), dtoName, valType, out _, out hop); }
                    catch { }
                    if (hop?.Length != 1 || !(hop[0] is PropertyInfo) || hop[0].Name != "Surface" || surfLive == null || surfLive.Name != leafName)
                        yield return "L14 twin-coercion: GeoVehicle." + dtoName + " no longer resolves through the Surface " +
                                     "property onto Transform." + leafName + " — the opt-out is now masking a real twin " +
                                     "gap, so removing it would restore nothing";
                }
            }
            // Hop mechanics, PROPERTY arm: the hop is only ever READ, so a get-only property must work.
            var propHop = new HopHolder();
            var synPropHop = new RailField
            {
                Name = "HitPoints", ValueType = typeof(int), Class = FieldClass.Leaf, Leaf = LeafKind.Int64,
                HopFi = new MemberInfo[] { typeof(HopHolder).GetProperty("StatsProp") },
                Fi = typeof(PhoenixPoint.Geoscape.Core.GeoVehicleStats).GetField("HitPoints")
            };
            synPropHop.SetValue(propHop, 41);
            if (!(synPropHop.HopFi[0] is PropertyInfo) || propHop.Stats.HitPoints != 41 ||
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
                HopFi = new MemberInfo[] { typeof(HopHolder).GetField("Stats") },
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

        /// <summary>See the twin-tables section of <see cref="Snapshot"/>, which fills this.</summary>
        private static readonly HashSet<Type> BridgedApplyTargets = new HashSet<Type>();

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

        /// <summary>L58 — PEER-LOCAL CONTAINMENT. Hashing a walk that CONTAINS a peer-local element must equal
        /// hashing the same walk with that element ABSENT: a peer-local element may contribute neither its own
        /// entries nor its KEY to any ancestor's order-vector / census.
        ///
        /// The half that was missing, and it cost three permanently diverged base roots (S#98/S#99/S#170,
        /// both clients agreeing and the HOST alone disagreeing, never healing): the opt-out was declared as a
        /// subtree PREFIX only (`…_facilities#&lt;id&gt;`), but a keyed collection also ships an ORDER VECTOR at
        /// the OWNER path — which is SHORTER than that prefix, so <see cref="DiffEngine.CrcOfEntries"/>'s
        /// whole-segment PrefixMatch can never reach it. Every peer therefore hashed its own locally-minted
        /// corridor ids, and the same vector on the wire made ReorderByKeys shuffle the client's corridors
        /// against host ids it does not own.
        ///
        /// Three halves, all driven through the REAL functions:
        /// (a) live anchor — <c>DiffEngine.IsPeerLocal</c> recognizes a real corridor <c>GeoPhoenixFacility</c>
        ///     and rejects a plain one (a predicate matching nothing makes the law vacuous);
        /// (b) emit site — <c>DiffEngine.VisitEntity</c> must CONSULT it. Falsifier: move the decision back
        ///     below the element-collection point and the peer-local key is already in `elems`, riding
        ///     AddKeyOrder no matter what the prefix set says;
        /// (c) containment on the real key-order codec + the real Crc32 — the owner's vector built through
        ///     IsPeerLocal, plus the peer-local element's own entry excluded by its declared prefix, hashes
        ///     EXACTLY as the walk that never saw the element; and the prefix alone provably CANNOT rescue an
        ///     unfiltered vector (the mechanism check that says why the fix has to live at the emit site).
        /// Falsify (c) by making IsPeerLocal return false.</summary>
        private static IEnumerable<string> PeerLocalContainmentLaw(Assembly game)
        {
            var isPeerLocal = typeof(DiffEngine).GetMethod("IsPeerLocal", AllMembers);
            var visit = typeof(DiffEngine).GetMethod("VisitEntity", AllMembers);
            if (isPeerLocal == null || visit == null)
            {
                yield return "L58 peer-local-predicate-absent: DiffEngine.IsPeerLocal/VisitEntity no longer resolve — " +
                             "the per-peer opt-out has no single decision point and nothing keeps it out of the owner's vector";
                yield break;
            }
            var facType = game.GetType("PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility");
            object corridor = null, plain = null;
            if (facType != null)
                try
                {
                    corridor = Activator.CreateInstance(facType, nonPublic: true);
                    plain = Activator.CreateInstance(facType, nonPublic: true);
                    facType.GetField("IsCorridor", AllMembers).SetValue(corridor, true);
                    facType.GetField("FacilityId", AllMembers).SetValue(corridor, 8u);
                    facType.GetField("FacilityId", AllMembers).SetValue(plain, 7u);
                }
                catch { corridor = plain = null; }
            if (corridor == null || plain == null)
            {
                yield return "L58 anchor-unconstructible: GeoPhoenixFacility no longer constructs with IsCorridor/" +
                             "FacilityId — the only peer-local declaration the rail makes cannot be exercised and " +
                             "this law is vacuous";
                yield break;
            }

            // (a) the predicate is alive and narrow.
            if (!DiffEngine.IsPeerLocal(corridor))
                yield return "L58 predicate-blind: DiffEngine.IsPeerLocal does not recognize a corridor GeoPhoenixFacility " +
                             "— the rail's only per-peer opt-out matches nothing and every base site mirrors ghost corridors";
            if (DiffEngine.IsPeerLocal(plain))
                yield return "L58 predicate-overbroad: DiffEngine.IsPeerLocal claims a NON-corridor facility is per-peer — " +
                             "real facilities would drop out of the order vector and never reorder on any client";

            // (b) the emit site must consult it BEFORE it builds the order vector. ORDER, not mere presence:
            // the pre-fix rail DID test for a corridor, one rung too low — in the per-element descend arm,
            // by which point AddKeyOrder had already shipped the key — and a presence-only check calls that
            // green. This is the half whose absence WAS the S#98/S#99/S#170 divergence.
            var seq = CalleeSequence(visit).Select(c => c.Name).ToList();
            int atPredicate = seq.IndexOf(isPeerLocal.Name);
            int atVector = seq.IndexOf("AddKeyOrder");
            if (atPredicate < 0 || atVector < 0)
                yield return "L58 emit-site-unfiltered: DiffEngine.VisitEntity's IL reaches IsPeerLocal=" +
                             (atPredicate >= 0) + " AddKeyOrder=" + (atVector >= 0) + " — the walk either stopped " +
                             "asking which elements are peer-local or stopped shipping a key vector, and either way " +
                             "nothing keeps a locally-minted key out of the owner's vector";
            else if (atPredicate > atVector)
                yield return "L58 opt-out-too-low: DiffEngine.VisitEntity consults IsPeerLocal only AFTER AddKeyOrder — " +
                             "the element is already in the container's element list, so its locally-minted key rides " +
                             "the owner's ORDER VECTOR, which sits above every peer-local prefix and can never be excluded";

            // (c) containment, through the real codec and the real hash.
            const string owner = "S#7.Layout._facilities";
            var prefixes = new[] { owner + "#8" };
            var kept = new List<string>();
            foreach (var e in new[] { plain, corridor })
                if (!DiffEngine.IsPeerLocal(e)) kept.Add(IdentityResolver.KeyOf(e));
            var walkWith = new List<DiffEngine.Entry> { Vec(owner, kept), Ent(owner + "#8._health", 4, "", 3) };
            var walkWithout = new List<DiffEngine.Entry> { Vec(owner, new List<string> { IdentityResolver.KeyOf(plain) }) };
            if (DiffEngine.CrcOfEntries(walkWith, prefixes) != DiffEngine.CrcOfEntries(walkWithout, null))
                yield return "L58 containment-broken: a walk containing a peer-local element does not hash equal to the " +
                             "same walk without it — the peers' own corridor ids report permanent false divergence on " +
                             "every base site and the CRC backstop re-emits forever without ever healing";
            var unfiltered = new List<DiffEngine.Entry>
            {
                Vec(owner, new List<string> { IdentityResolver.KeyOf(plain), IdentityResolver.KeyOf(corridor) }),
                Ent(owner + "#8._health", 4, "", 3),
            };
            if (DiffEngine.CrcOfEntries(unfiltered, prefixes) == DiffEngine.CrcOfEntries(walkWithout, null))
                yield return "L58 prefix-overreach: declaring the peer-local PREFIX now also neutralizes the OWNER's " +
                             "order vector — PrefixMatch has grown past whole-segment containment, which would silently " +
                             "drop whole containers from the hash instead of just the opted-out element";
        }

        /// <summary>L59 — MIST COVERAGE MUST RIDE. <c>_mistData</c> is a per-frame GPU accumulator
        /// (<c>MistFrameUpdate</c> blits the spread shader 4× EVERY FRAME, at the peer's OWN frame rate),
        /// so a client can never re-derive it from mirrored inputs — it must be shipped. Gameplay, not
        /// cosmetics: <c>IsInMist</c> reads that same CPU array.
        ///
        /// Every arm guards a SILENT failure — the rail's dominant bug class. Nothing here would throw:
        ///   • <b>api-drift</b> — the whole feature is reflection-bound + native-method reuse. A renamed
        ///     field or a reshaped DTO means <c>MistSync.HostTick</c> returns early forever and no line
        ///     anywhere says the mist stopped shipping.
        ///   • <b>accumulator-claim-false / frame-driver-lost</b> — the law's own PREMISE, executed. If
        ///     <c>FrameUpdate</c> stopped reaching <c>MistFrameUpdate</c>, or it stopped blitting, or it
        ///     stopped being started per-frame, then mist WOULD be derivable and this whole root is dead
        ///     weight shipping ~900 KB a hop. A law that cannot become false is not a law.
        ///   • <b>encoder-order-drift / encoder-hand-rolled</b> — the host builds <c>MistData</c> itself
        ///     (it may not pay <c>RecordInstanceData</c>'s second 8 MB ToArray + deflate on the main
        ///     thread), so the encoding must stay BYTE-compatible with the consumer. Asserted as an
        ///     ORDERED IL sequence on both sides: deflate-then-base64, using the GAME's own
        ///     <c>Compress</c>. Swap the two, or hand-roll a DeflateStream, and
        ///     <c>ProcessInstanceData</c> would decode garbage into the mist texture in silence.
        ///   • <b>repeller-guard-gone</b> — shipping <c>RepellerData</c> is refused (Arc-4: it redraws
        ///     from the already-mirrored <c>MistRepeller.Range.Range</c>), which is only safe because
        ///     <c>ProcessInstanceData</c> null-TESTS the member before decoding it. That branch is the
        ///     contract; asserted in the iterator's IL, not assumed from the decompile.
        ///   • <b>root-*</b> — registration and classification, non-vacuously: the key really resolves to
        ///     the very instance the walk writes, and its members really carry (a root covering nothing
        ///     is entered and emits nothing, silently).
        ///   • <b>wiring-* / boundary-order</b> — both peers must register in the ONE ctor, both must
        ///     tick, and the mod-root contract (IdentityResolver.cs:205-206) requires the state be
        ///     EMPTY before <c>DiffEngine</c> takes its post-reload baseline. ORDER, not presence:
        ///     clearing AFTER the baseline snapshot leaves the host holding a value it will never emit
        ///     against a client instance that is empty forever.</summary>
        private static IEnumerable<string> MistCoverageLaw(Assembly game)
        {
            var sys = game.GetType("PhoenixPoint.Geoscape.MistRendererSystem");
            var dto = sys?.GetNestedType("MistRendererInstanceData", AllMembers);
            var mistData = sys?.GetField("_mistData", AllMembers);
            var record = sys?.GetMethod("RecordInstanceData", AllMembers, null, Type.EmptyTypes, null);
            var process = dto == null ? null : sys.GetMethod("ProcessInstanceData", AllMembers, null, new[] { dto }, null);
            var frameUpdate = sys?.GetMethod("FrameUpdate", AllMembers);
            var mistFrame = sys?.GetMethod("MistFrameUpdate", AllMembers);
            var startUpd = sys?.GetMethod("StartUpdatingMist", AllMembers);
            var compress = sys?.GetMethod("Compress", AllMembers, null, new[] { typeof(byte[]) }, null);

            // ─── (a) the bound surface, by name AND shape ───
            var drift = new List<string>();
            if (sys == null) drift.Add("MistRendererSystem");
            if (mistData == null || mistData.FieldType.Name != "NativeArray`1" ||
                mistData.FieldType.GetGenericArguments().FirstOrDefault() != typeof(byte) ||
                mistData.FieldType.GetMethod("ToArray", Type.EmptyTypes)?.ReturnType != typeof(byte[]))
                drift.Add("_mistData:NativeArray<byte> with ToArray()->byte[]");
            if (sys?.GetField("_hoursPassed", AllMembers)?.FieldType != typeof(int)) drift.Add("_hoursPassed:int");
            if (record == null || record.ReturnType != dto) drift.Add("RecordInstanceData()->MistRendererInstanceData");
            if (process == null || !typeof(IEnumerable).IsAssignableFrom(process.ReturnType) &&
                                   process.ReturnType.Name != "IEnumerator`1")
                drift.Add("ProcessInstanceData(dto)->IEnumerator<NextUpdate>");
            if (compress == null || !compress.IsStatic || compress.ReturnType != typeof(byte[])) drift.Add("static Compress(byte[])->byte[]");
            if (sys?.GetProperty("ActiveGenerators", AllMembers) == null) drift.Add("ActiveGenerators");
            if (frameUpdate == null) drift.Add("FrameUpdate");
            if (mistFrame == null) drift.Add("MistFrameUpdate");
            if (startUpd == null) drift.Add("StartUpdatingMist");
            if (game.GetType("PhoenixPoint.Geoscape.Levels.GeoLevelController")?.GetField("MistRenderComponent", AllMembers)?.FieldType != sys)
                drift.Add("GeoLevelController.MistRenderComponent");
            // The four DTO members the client-side apply fills — a reshape here is a NullReference or a
            // silently unset member inside a coroutine, i.e. no stack anyone sees.
            foreach (var (n, t) in new[] { ("MistData", typeof(string)), ("RepellerData", typeof(string)), ("HoursPassed", typeof(int)) })
                if (dto?.GetField(n, AllMembers)?.FieldType != t) drift.Add("MistRendererInstanceData." + n + ":" + t.Name);
            if (dto?.GetField("ActiveMistGenerators", AllMembers)?.FieldType.Name != "List`1")
                drift.Add("MistRendererInstanceData.ActiveMistGenerators:List<GeoSite>");
            if (drift.Count > 0)
            {
                yield return "L59 mist-api-drift: the mist surface no longer matches what MistSync binds — " +
                             string.Join(", ", drift) + " — the host would stop shipping coverage (or the client " +
                             "stop applying it) with no exception and no log line anywhere";
                yield break; // every arm below reads these members; a drifted surface makes them noise
            }

            // ─── (b) the PREMISE: mist really is a per-frame, frame-rate-local accumulator ───
            if (!Callees(startUpd, game).Any(c => c.MetadataToken == frameUpdate.MetadataToken))
                yield return "L59 frame-driver-lost: MistRendererSystem.StartUpdatingMist no longer starts FrameUpdate — " +
                             "the mist surface is not driven per-frame any more, so L59's whole premise (a client cannot " +
                             "re-derive coverage) is unproven and the ~900 KB root may be dead weight";
            if (!Callees(frameUpdate, game).Any(c => c.MetadataToken == mistFrame.MetadataToken))
                yield return "L59 accumulator-claim-false: FrameUpdate no longer reaches MistFrameUpdate — mist would " +
                             "advance only on the mirrored hour tick, which every peer already has, and this root " +
                             "should be deleted rather than shipped";
            if (!CalleeSequence(mistFrame).Any(c => c.Name == "Blit"))
                yield return "L59 accumulator-claim-false: MistFrameUpdate no longer blits the spread surface — the " +
                             "per-frame divergence L59 exists to correct cannot happen, so the root is unjustified";

            // ─── (c) encoding stays byte-compatible with the game's own consumer ───
            bool IsCompress(MethodBase m) => m.MetadataToken == compress.MetadataToken && m.Module == compress.Module;
            bool IsBase64(MethodBase m) => m.Name == "ToBase64String" && m.DeclaringType == typeof(Convert);
            bool IsToArray(MethodBase m) => m.Name == "ToArray" && m.DeclaringType != null && m.DeclaringType.Name == "NativeArray`1";

            var rec = CalleeSequence(record);
            int rArr = rec.FindIndex(IsToArray), rCmp = rec.FindIndex(IsCompress), rB64 = rec.FindIndex(IsBase64);
            if (rArr < 0 || rCmp < 0 || rB64 < 0 || !(rArr < rCmp && rCmp < rB64))
                yield return "L59 encoder-order-drift: RecordInstanceData's IL no longer reads the native array, then " +
                             "Compress, then ToBase64String (got ToArray@" + rArr + " Compress@" + rCmp + " Base64@" + rB64 +
                             ") — the save's own encoding changed, so the string MistSync hands ProcessInstanceData would " +
                             "decode to garbage and paint a wrong mist map with no error";

            // Our side, swept across MistSync AND its compiler-generated closures (the deflate runs on a
            // worker lambda, so the pipeline is split across two methods by construction).
            var ourBodies = new[] { typeof(MistSync) }.Concat(typeof(MistSync).GetNestedTypes(AllMembers))
                .SelectMany(t => t.GetMethods(AllMembers).Cast<MethodBase>())
                .Where(m => { try { return m.GetMethodBody() != null; } catch { return false; } })
                .ToList();
            bool ourToArray = false, ourPipeline = false;
            foreach (var m in ourBodies)
            {
                var s = CalleeSequence(m);
                if (s.Any(IsToArray)) ourToArray = true;
                int c = s.FindIndex(IsCompress), b = s.FindIndex(IsBase64);
                if (c >= 0 && b > c) ourPipeline = true;
            }
            if (!ourToArray || !ourPipeline)
                yield return "L59 encoder-hand-rolled: MistSync no longer reads the native array through NativeArray.ToArray" +
                             " (" + ourToArray + ") and/or no longer encodes with the GAME's Compress followed by " +
                             "Convert.ToBase64String (" + ourPipeline + ") — a locally rolled deflate/base64 can differ from " +
                             "what ProcessInstanceData decodes, and the mismatch is invisible on the host";

            // ─── (d) the null-RepellerData contract, in the consumer's real IL ───
            var mover = process.ReturnType.GetMethod("MoveNext", AllMembers)
                        ?? sys.GetNestedTypes(AllMembers)
                              .Where(t => t.Name.IndexOf("ProcessInstanceData", StringComparison.Ordinal) >= 0)
                              .Select(t => t.GetMethod("MoveNext", AllMembers)).FirstOrDefault(x => x != null);
            var repellerField = dto.GetField("RepellerData", AllMembers);
            if (mover == null)
                yield return "L59 repeller-guard-unreadable: ProcessInstanceData's iterator body cannot be found, so the " +
                             "null-RepellerData contract MistSync relies on cannot be asserted at all";
            else if (!LoadsThenNullBranches(mover, repellerField))
                yield return "L59 repeller-guard-gone: ProcessInstanceData no longer null-tests RepellerData before " +
                             "decoding it — MistSync deliberately ships NULL there (the repeller redraws from the " +
                             "already-mirrored MistRepeller ranges), so every client apply would now throw inside a " +
                             "coroutine and the mist would silently stop updating";

            // ─── (e) the root: really registered, really covering something ───
            MistSync.Register(); // idempotent; the harness never builds a SyncEngine
            object resolved = null;
            var resolveRoot = typeof(IdentityResolver).GetMethod("ResolveRoot", AllMembers);
            try { resolved = resolveRoot?.Invoke(null, new object[] { null, MistSync.RootKey }); } catch { }
            if (!ReferenceEquals(resolved, MistSync.State))
                yield return "L59 root-unregistered: \"" + MistSync.RootKey + "\" does not resolve to MistSync.State — " +
                             "the client has no apply target, so every mist delta dies as \"entity not found\", one " +
                             "log line per path and never retried";
            var rt = RailType.Get(typeof(MistState));
            if (rt == null || rt.CoveredCount == 0)
                yield return "L59 root-covers-nothing: the value rail covers NONE of MistState's members — the walk " +
                             "enters \"" + MistSync.RootKey + "\" and emits nothing, so coverage never mirrors and " +
                             "nothing says so (vacuity guard: every arm above would still pass)";
            else
            {
                var f = rt.Fields.FirstOrDefault(x => x.Name == "MistData");
                if (f == null || f.Class != FieldClass.Leaf || f.Leaf != LeafKind.String)
                    yield return "L59 mistdata-not-leaf-string: MistState.MistData classifies " +
                                 (f == null ? "(absent)" : f.Class + "/" + f.Leaf) + " instead of Leaf/String — the " +
                                 "payload would ride some other codec (or be Excluded outright) and the client's mist " +
                                 "would simply never change";
            }

            // ─── (f) wiring: registered once for BOTH peers, ticked, cleared BEFORE the baseline ───
            var ctor = typeof(SyncEngine).GetConstructors(AllMembers).FirstOrDefault();
            if (ctor == null || !Callees(ctor, typeof(MistSync).Assembly).Any(c => c.DeclaringType == typeof(MistSync) && c.Name == "Register"))
                yield return "L59 wiring-unregistered: SyncEngine's ctor does not call MistSync.Register — the mod root " +
                             "exists in code and is registered on neither peer; the walk never sees it";
            if (!Callees(typeof(SyncEngine).GetMethod("Tick", AllMembers), typeof(MistSync).Assembly)
                    .Any(c => c.DeclaringType == typeof(MistSync) && c.Name == "Tick"))
                yield return "L59 wiring-unticked: SyncEngine.Tick does not call MistSync.Tick — the host never recomputes " +
                             "the payload and the client never hands it to the native loader; the root stays null forever";
            var boundary = CalleeSequence(typeof(SyncEngine).GetMethod("ResetForReloadBoundary", AllMembers));
            int atMist = boundary.FindIndex(c => c.DeclaringType == typeof(MistSync) && c.Name == "ResetForReloadBoundary");
            int atDiff = boundary.FindIndex(c => c.DeclaringType == typeof(DiffEngine) && c.Name == "ResetForReloadBoundary");
            if (atMist < 0 || atDiff < 0 || atMist > atDiff)
                yield return "L59 boundary-order: SyncEngine.ResetForReloadBoundary clears the mist root at " + atMist +
                             " and rebaselines DiffEngine at " + atDiff + " — the mod-root contract " +
                             "(IdentityResolver.cs:205-206) needs the state EMPTY when the baseline is taken, else the " +
                             "host holds a value it will never emit while the client's instance stays empty forever";
        }

        /// <summary>True when the method's IL loads <paramref name="target"/> and IMMEDIATELY branches on it —
        /// i.e. a real null test, not just a read. Used by L59: <c>ProcessInstanceData</c> reads
        /// <c>RepellerData</c> twice (guard, then decode), and only the guarded read is the contract.</summary>
        private static bool LoadsThenNullBranches(MethodBase m, FieldInfo target)
        {
            if (m == null || target == null) return false;
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) return false;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
            int i = 0;
            bool loaded = false;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) return false;
                    code = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeByValue.TryGetValue(code, out var op)) return false;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) return false;
                if (loaded && (op == OpCodes.Brfalse || op == OpCodes.Brfalse_S ||
                               op == OpCodes.Brtrue || op == OpCodes.Brtrue_S)) return true;
                loaded = false;
                if (op == OpCodes.Ldfld || op == OpCodes.Ldflda)
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i), typeArgs, null); } catch { }
                    loaded = f != null && f.MetadataToken == target.MetadataToken && f.Module == target.Module;
                }
                i += size;
            }
            return false;
        }

        private static DiffEngine.Entry Vec(string path, List<string> keys) =>
            new DiffEngine.Entry { Path = path, FieldIdx = 0, SubKey = "", Value = RailMeta.EncodeKeyOrder(keys, null),
                                   Key = path + "" + (ushort)0 + "" };

        // L41's own stand-in for a game type that exposes a mutable store through a read-only view — the
        // shape RailMeta.MutableBehind must see THROUGH, driven here without a live GeoVehicle.
        private sealed class OpaqueReadOnlyList : IList
        {
            public object this[int i] { get => null; set => throw new NotSupportedException(); }
            public bool IsReadOnly => true;
            public bool IsFixedSize => true;
            public int Count => 0;
            public object SyncRoot => this;
            public bool IsSynchronized => false;
            public int Add(object v) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Contains(object v) => false;
            public int IndexOf(object v) => -1;
            public void Insert(int i, object v) => throw new NotSupportedException();
            public void Remove(object v) => throw new NotSupportedException();
            public void RemoveAt(int i) => throw new NotSupportedException();
            public void CopyTo(Array a, int i) { }
            public IEnumerator GetEnumerator() { yield break; }
        }

        /// <summary>L41 — AN APPLY ONTO A READ-ONLY COLLECTION FAÇADE MUST SUCCEED OR FAIL LOUDLY, NEVER
        /// NO-OP. The game hands out mutable stores behind read-only views wherever it wants callers to go
        /// through its own mutators — <c>GeoVehicle.DestinationSites =&gt; _destinationSites.AsReadOnly()</c>
        /// (decompile GeoVehicle.cs:172) — while every <see cref="RailMeta.ApplyList"/> strategy mutates in
        /// place. The result was <c>NotSupportedException("Collection is read-only.")</c> on EVERY delta,
        /// caught by <c>GenericApplier</c> and deduped to ONE warning by <c>LogMissOnce</c>: after that,
        /// aircraft routes simply never landed on any client and nothing said so again.
        ///
        /// Asserted through the REAL function, and generically — the law never names
        /// <c>_destinationSites</c>, because the fix must not either: it asks the façade what it WRAPS.
        /// (a) a real <c>ReadOnlyCollection&lt;T&gt;</c> resolves to its own backing list, by reference;
        /// (b) an already-mutable container and an ARRAY pass through untouched (fixed-size is not
        /// read-only, and the array strategy assigns a fresh one); (c) a façade with no reachable backing
        /// store THROWS rather than silently doing nothing. Plus the live anchor: the twin member that
        /// actually failed must still classify covered with a strategy behind it.
        /// Falsify by returning <c>current</c> unchanged from <see cref="RailMeta.MutableBehind"/>.</summary>
        private static IEnumerable<string> ReadOnlyFacadeLaw()
        {
            var backing = new List<string> { "a", "b" };
            object resolved = null;
            string threw = null;
            try { resolved = RailMeta.MutableBehind(backing.AsReadOnly(), null, null); }
            catch (Exception ex) { threw = ex.GetType().Name; }
            if (threw != null)
                yield return "L41 facade-unresolved: a plain ReadOnlyCollection<T> — what List<T>.AsReadOnly() returns, " +
                             "the shape the game itself uses — threw " + threw + " instead of resolving to its backing " +
                             "list; every list behind a read-only view stops applying";
            else if (!ReferenceEquals(resolved, backing))
                yield return "L41 facade-unresolved: MutableBehind did not return the ReadOnlyCollection's OWN backing " +
                             "list (got " + (resolved == null ? "null" : resolved.GetType().Name) + ") — applying into a " +
                             "copy writes into nothing, which is the silent no-op this law exists to forbid";
            else if ((resolved as IList).IsReadOnly)
                yield return "L41 facade-unresolved: the resolved container is itself read-only — ApplyList's Clear+Add " +
                             "still throws on every delta";

            var plain = new List<string> { "x" };
            if (!ReferenceEquals(RailMeta.MutableBehind(plain, null, null), plain))
                yield return "L41 mutable-rewrapped: an ordinary mutable list did not pass through untouched — every " +
                             "list apply in the rail would go through a resolution it does not need";
            var arr = new string[2];
            if (!ReferenceEquals(RailMeta.MutableBehind(arr, null, null), arr))
                yield return "L41 array-unwrapped: an ARRAY was treated as a façade — arrays are fixed-size, not " +
                             "read-only, and ApplyList assigns a fresh one; unwrapping here would break that strategy";

            threw = null;
            try { RailMeta.MutableBehind(new OpaqueReadOnlyList(), null, null); }
            catch (Exception ex) { threw = ex.GetType().Name; }
            if (threw == null)
                yield return "L41 unresolvable-facade-silent: a read-only façade with NO reachable backing collection " +
                             "was accepted — ApplyList then Clear()s it, the NotSupportedException is deduped away by " +
                             "GenericApplier.LogMissOnce, and the field is stale forever with one stale log line";

            // The live anchor: the member that actually failed in-game. A law that only exercised synthetic
            // containers would stay green if the twin stopped classifying this field at all.
            var twin = RailType.GetBridged(typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle),
                                           typeof(PhoenixPoint.Geoscape.Entities.GeoVehicleInstanceData));
            var dest = twin?.FieldByName("DestinationSites");
            if (dest == null || dest.Class == FieldClass.Excluded)
                yield return "L41 anchor-lost: GeoVehicle's DestinationSites twin no longer classifies covered (" +
                             (dest == null ? "no such member" : dest.Exclude) + ") — the aircraft-route field this law " +
                             "was written for stopped riding at all, so the law is asleep";
            else if (RailMeta.ListApplyStrategy(dest) == null)
                yield return "L41 anchor-unappliable: DestinationSites has no ApplyList strategy — it would reach the " +
                             "final throw on every delta regardless of the façade";

            // The twin gaps closed alongside it (same type, same log): a member that goes back to being
            // unresolved is a silent regression, since the warning it produces is deduped after one line.
            foreach (var name in new[] { "StartExplorationTime" })
            {
                var f = twin?.FieldByName(name);
                if (f == null || f.Class == FieldClass.Excluded)
                    yield return "L41 twin-gap-reopened: GeoVehicleInstanceData." + name + " has no live counterpart again (" +
                                 (f == null ? "no such member" : f.Exclude) + ") — it is not mirrored, and GenericApplier " +
                                 "says so exactly once per session";
            }
            // `Rot` was the other gap this batch closed, and it is now DELIBERATELY excluded again: it aliases
            // ActorComponent.Rot -> transform.rotation, i.e. the very PIVOT the client's own nav routine writes
            // (GeoNavComponent.cs:111), so mirroring it fights the derivation (L43). Same split as L14's Surface
            // arm — the decision must be the declared opt-out, and the mapping under it must still resolve, so a
            // genuinely re-opened gap cannot masquerade as the decision.
            var fRot = twin?.FieldByName("Rot");
            var rotOptOut = RailMeta.OptOutReason(typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle), "Rot");
            if (rotOptOut == null || fRot == null || fRot.Class != FieldClass.Excluded || fRot.Exclude != rotOptOut)
                yield return "L41 twin-gap-reopened: GeoVehicleInstanceData.Rot is not excluded by the declared pose " +
                             "opt-out (" + (rotOptOut == null ? "none declared" : fRot == null ? "no such member"
                                 : fRot.Class + " / " + fRot.Exclude) + ") — either the pivot is being mirrored against " +
                             "the client's own navigation, or the twin gap re-opened and the opt-out is hiding it";
            else
            {
                MemberInfo rotLive = null;
                try { rotLive = RailMeta.ResolveLive(typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle), "Rot", typeof(UnityEngine.Quaternion), out _, out _); }
                catch { }
                if (rotLive == null || rotLive.Name != "rotation")
                    yield return "L41 twin-gap-reopened: GeoVehicleInstanceData.Rot no longer resolves onto " +
                                 "transform.rotation — the opt-out is masking a real gap, so removing it would " +
                                 "restore nothing";
            }
            // GeoSite must KEEP riding it: the pose opt-out is scoped to actors whose nav the client runs, and a
            // site has no routine recomputing its transform. A reason that leaked one level up to ActorComponent
            // would silently stop mirroring every site's placement.
            var siteRot = RailType.GetBridged(typeof(PhoenixPoint.Geoscape.Entities.GeoSite),
                                              typeof(PhoenixPoint.Geoscape.Entities.GeoSiteInstaceData))?.FieldByName("Rot");
            if (siteRot != null && siteRot.Class == FieldClass.Excluded && siteRot.Exclude == rotOptOut && rotOptOut != null)
                yield return "L41 pose-optout-overreached: GeoSite.Rot is excluded by the NAVIGATING-actor pose opt-out — " +
                             "a site has no nav routine to re-derive its transform, so its placement would simply stop " +
                             "mirroring; key the opt-out on the vehicle, never on ActorComponent";
        }

        /// <summary>L40 — NO VALUE MAY LEAVE A BATCH UNSHIPPED AND UNANNOUNCED. The wire caps one entry at
        /// <see cref="DiffEngine.MaxValueBytes"/> (a u16 length inside a u16 envelope), and an entry that
        /// outgrew it used to be <c>continue</c>d: one deduped warning on the first tick, then silence on
        /// every tick after, with no <c>TouchRoot</c>, so the law-7 CRC backstop read the root as QUIESCENT
        /// and hashed a host truth the wire had never carried. Measured shape: a campaign-long
        /// <c>GeoscapeEventSystem.EncounterRecords</c> blob at 8636 B — the whole event ledger stopped
        /// mirroring, permanently, and the clients' event windows could never resolve.
        ///
        /// The cap is not raised and the ledger is not special-cased. It is a TRANSPORT bound, and every
        /// keyless <see cref="FieldClass.EntityList"/> ships one canonical blob BY CONSTRUCTION (that is what
        /// distinguishes it from EntityCollection), so any of them can reach it as a campaign grows — the fix
        /// therefore has to live at the envelope, where it covers all of them at once, keyable or not.
        ///
        /// Both halves are driven as the REAL functions: the host's split
        /// (<see cref="DiffEngine.FragmentForWire"/>) against the client's reassembly
        /// (<c>GenericApplier.Reassemble</c>) — a harness that re-implemented either could agree with itself
        /// while disagreeing with the wire. Falsify by restoring the <c>continue</c>, or by dropping the
        /// <c>TouchRoot</c> from <see cref="DiffEngine.NoteUndeliverable"/>.</summary>
        private static IEnumerable<string> FragmentLaw()
        {
            // ── the marker's own namespace ────────────────────────────────
            var taken = Enum.GetValues(typeof(LeafKind)).Cast<LeafKind>().Select(k => (byte)k)
                .Concat(new[] { RailMeta.EntityListMarker, RailMeta.OrderVectorMarker,
                                RailMeta.DictCensusMarker, RailMeta.DictTombstone, (byte)14 /* ListMarker */ })
                .ToList();
            if (taken.Contains(RailMeta.FragmentMarker))
                yield return "L40 marker-collision: FragmentMarker " + RailMeta.FragmentMarker + " is already a LeafKind " +
                             "or another value marker — a fragment would decode as a value (or a delete) and the client " +
                             "would write the header bytes into the game's model";

            // ── the split, and its reassembly ─────────────────────────────
            var big = new byte[DiffEngine.MaxValueBytes * 3 + 137];
            new System.Random(7).NextBytes(big);
            var oversized = new DiffEngine.Entry { KindId = 0, Path = "ES", FieldIdx = 2, SubKey = "", Value = big, Key = "ES2" };
            var neighbour = Ent("S#7", 0, "", 1);
            var wire = DiffEngine.FragmentForWire(new List<DiffEngine.Entry> { neighbour, oversized });

            var frags = wire.Where(e => e.Path == "ES").ToList();
            if (frags.Count < 2)
                yield return "L40 oversized-dropped: an entry of " + big.Length + " B left FragmentForWire as " + frags.Count +
                             " wire entr(y/ies) — it was dropped or passed through whole, so the client either never " +
                             "receives the field again or the packet writer refuses it, silently, on every tick forever";
            if (wire.Count == 0 || wire[0].Path != neighbour.Path)
                yield return "L40 batch-reordered: fragmenting moved the batch's other entries — the delta stream is " +
                             "canonical by law 6 and its order is what makes creates land before the values that need them";
            foreach (var e in wire)
                if (e.Value.Length > DiffEngine.MaxValueBytes)
                {
                    yield return "L40 fragment-oversized: a wire entry still carries " + e.Value.Length + " B (cap " +
                                 DiffEngine.MaxValueBytes + ") — Emit's residual drop path is reachable again and the " +
                                 "value never ships";
                    break;
                }

            byte[] whole = null;
            int premature = 0;
            for (int i = 0; i < frags.Count; i++)
            {
                var got = GenericApplier.Reassemble(oversized.Path, oversized.FieldIdx, oversized.SubKey, frags[i].Value);
                if (i < frags.Count - 1 && got != null) premature++;
                if (i == frags.Count - 1) whole = got;
            }
            if (premature > 0)
                yield return "L40 partial-applied: reassembly returned a value " + premature + " time(s) BEFORE the last " +
                             "fragment — a half-filled buffer would be handed to the decoder as if it were the field";
            if (whole == null || whole.Length != big.Length || !RailMeta.BytesEqual(whole, big))
                yield return "L40 reassembly-lossy: the fragments did not reassemble to the host's exact bytes (" +
                             (whole == null ? "never completed" : whole.Length + " B vs " + big.Length + " B") +
                             ") — the client decodes a corrupt value instead of the state, which is worse than the drop";

            // A value AT the cap must not be split: fragmenting what already fits would double the traffic of
            // every large-but-legal field and is how a "fix" quietly becomes a regression.
            var atCap = new DiffEngine.Entry { Path = "ES", FieldIdx = 3, SubKey = "", Value = new byte[DiffEngine.MaxValueBytes], Key = "k" };
            var plain = new List<DiffEngine.Entry> { atCap };
            if (!ReferenceEquals(DiffEngine.FragmentForWire(plain), plain))
                yield return "L40 fragments-what-fits: a batch whose largest value is exactly at the cap was rebuilt — " +
                             "every ordinary tick then pays a copy of its whole changed list";

            // ── the residual drop path: loud AND non-quiescent ────────────
            const uint dropSeq = 4242u;
            DiffEngine.NoteUndeliverable(new DiffEngine.Entry
            {
                KindId = 0, Path = "S#77.SerializationData.Storage", FieldIdx = 9, SubKey = "",
                Value = new byte[DiffEngine.MaxValueBytes + 1], Key = "drop",
            }, dropSeq);
            if (DiffEngine.RootTouchedAt("S#77") != dropSeq)
                yield return "L40 drop-untouched: the undeliverable path did not record root 'S#77' as touched at the " +
                             "seq it failed on — the CRC backstop then treats the subtree as QUIESCENT, compares a host " +
                             "hash that includes the value the wire never carried, spends its ONE heal on a re-emit that " +
                             "drops the same value again, and gives up; the divergence becomes permanent and invisible";
        }

        // Root pairs where a LATER root's declared-type closure reaches an EARLIER root's type. Legal —
        // the earlier root is walked first and OWNS the instance — but never by accident: the later root
        // emits NOTHING for it (a silent return in VisitEntity), so the coverage must be known to ride
        // under the earlier path. Grammar: "<later>-><earlier>". A row that stops being observed is a
        // violation too, so this cannot rot into a permanent allowance list.
        private static readonly HashSet<string> _declaredRootReach = new HashSet<string>(StringComparer.Ordinal)
        {
        };

        // L34 witnesses — types whose own CONTENT the rail refuses, and the member(s) HuskScan must NAME.
        // Grammar: "<type FullName>" -> the member names, ordinal-sorted, comma-joined.
        //
        // These are the members that were INVISIBLE until the scan fix: HuskScan re-added every
        // GetSerializedMembers name to `carried` after the rt-table had already refused it (a direct type's
        // table is BUILT from that same member list), so a member the rail EXCLUDES certified as carried and
        // the gate could never see that a blob's leaves are refused. GeoscapeLogEntry is the witness that
        // was found BY HAND — a rebuilt entry arrives with Text=null and GenerateMessage() NREs on it.
        //
        // Non-rotting on purpose (same shape as _declaredRootReach): a witness that stops being observed is
        // a violation too, so this cannot decay into a permanent allowance list. It is a belt on the SCAN's
        // semantics only — DISCOVERY of new refused-content types is L15's recursive sweep, which is exactly
        // what the fix restored. Keeping the two separate is deliberate: re-deriving HuskScan's skip rules
        // here would be the "two independently-written copies disagree" bug the rail keeps paying for.
        private static readonly Dictionary<string, string> _huskContentWitnesses =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // RETIRED with the refusal they witnessed, not silenced: GeoscapeLogEntry (Parameters,Text),
            // FactionAggressionRequest (Description,Summary), FactionDiplomacyState (Description,Title) and
            // GeoFactionObjective (Description,Title) were all rows about the SAME refused content — a
            // LocalizedTextBind. LeafKind.TextBind carries it, so those members are genuinely CARRIED now and
            // a witness demanding they be named as husks would be asserting something false (and would fail
            // as "husk-content-uncounted" the moment the codec works). RailCheck L35 is what guards that
            // coverage from silently reverting; the rows below keep L34's own semantics under test.
            // AmmoManager.ParentItem / CommonItemData.OwnerItem left this table the same way: the rail now
            // CARRIES them (RailMeta.SalvagesOwnerBackRef — the blob decoder re-wires a child's owner
            // back-ref from the frame it is nested in), so demanding that HuskScan name them would demand the
            // null back. What replaced the witness is the L15 owner-back-ref arm below, which proves the
            // owner really is reachable on every blob path instead of trusting the count.
            //
            // The two witnesses that remain are refused CONTENT of a different kind — a member the rail
            // excludes for its own reasons while the game's serializer still discovers it, which is precisely
            // the overrule L34 exists to catch. Both were VERIFIED by running the pre-43ac747 scan: with the
            // unconditional `carried.Add` restored, `BaseStat (Modifications,Owner,StatsRepo)` collapses to
            // `(Owner,StatsRepo)` and `GeoscapeRaid (ParticleEffect,PostRaidReturnActor)` to
            // `(ParticleEffect)` — i.e. these two names are exactly the ones the blind scan loses.
            { "Base.Entities.Statuses.BaseStat", "Modifications" },
            { "PhoenixPoint.Geoscape.Levels.GeoscapeRaid", "PostRaidReturnActor" },
        };

        /// <summary>L34 — a member whose own CONTENT the rail refuses must be NAMED, never counted as carried.
        ///
        /// The husk gate's whole job is to refuse a blob that would land hollow, and it was blind to the one
        /// case where the hollowness is in the CONTENT rather than in the absence: <c>HuskScan</c> treated
        /// every name the game's serializer discovers as carried, which for a direct type is every name its
        /// rail table has — including the ones the rail EXCLUDED. So a blob whose leaves are rail-refused
        /// classes certified as fine and arrived with nulls, which is the silent-swallow class this project
        /// exists to kill, sitting inside the tool meant to catch it.
        ///
        /// Asserted from the SCAN's answer, not from a re-derivation of its rules, and in BOTH directions:
        /// a witness the scan stops naming is a regression, and a witness that stops applying is a stale
        /// declaration. Falsify by restoring the unconditional <c>carried.Add</c>.</summary>
        private static IEnumerable<string> HuskContentLaw(Assembly game)
        {
            foreach (var kv in _huskContentWitnesses)
            {
                var t = game.GetType(kv.Key);
                if (t == null)
                {
                    yield return "L34 husk-witness-type-absent: " + kv.Key + " is no longer a game type — the " +
                                 "witness guards nothing and the next reader will trust it";
                    continue;
                }
                var named = new SortedSet<string>(RailMeta.HuskScan(t).Select(m => m.Name), StringComparer.Ordinal);
                foreach (var want in kv.Value.Split(','))
                    if (!named.Contains(want))
                        yield return "L34 husk-content-uncounted: " + kv.Key + "." + want + " is a member the rail " +
                                     "REFUSES, so a blob rebuild of this type lands it null — but HuskScan reports it " +
                                     "as CARRIED. The husk gate then licenses the blob and the client gets a hollow " +
                                     "object with no log line anywhere (named husks now: " +
                                     (named.Count == 0 ? "<none>" : string.Join(",", named)) + ")";
            }
        }

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
        private static IEnumerable<string> RootOwnershipLaw(List<Type> types)
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
            // "GeoscapeLog" left this list when LeafKind.TextBind landed: its opt-out reason string named a
            // text-key codec as the missing piece, and with the codec the log RIDES (Descend -> _entries
            // EntityList, GeoscapeLogEntry covered 4/4). It is no longer a landmine, so demanding an opt-out
            // for it would be demanding the refusal back.
            foreach (var name in new[] { "TacUnits", "ModData", "ContextHelpData" })
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

            foreach (var v in SubEntityRefArm(types)) yield return v;
        }

        /// <summary>L28, sub-entity arm — a ref key that is a PATH must still be a path that RESOLVES.
        ///
        /// A ROOT ref is self-validating: "S#5" is one segment and <c>ResolveRoot</c> owns it. A SUB-entity
        /// ref (<c>IdentityResolver.IsRefAddressableType</c> minus the roots) is named by the rail path its
        /// OWNER addresses it by, so its key silently depends on four independent facts holding at once —
        /// the owner chain's member names, the terminal field still being a KEYED collection, that
        /// collection's element type, and the element still being <c>KeyOf</c>-addressable. Any one of them
        /// moving turns every such ref into a wire NULL that the client writes over a live reference: the
        /// exact silent-swallow class, and invisible to every other law because the FIELD still classifies
        /// fine as a Leaf.
        ///
        /// So the law re-derives the template through the real metadata tables — the same hops
        /// <c>IdentityResolver.Resolve</c> takes at runtime (IInstanceData -> twin DTO, a DTO slot whose
        /// declaring type is a Component -> GetComponent) — instead of trusting the string. Falsify it by
        /// renaming a segment, flipping the terminal collection's class, or dropping the element's id probe.
        ///
        /// What it CANNOT assert, stated rather than implied: that a live haven actually holds the zone.
        /// The harness has no GeoLevelController (see Main) — resolution itself is the in-game gate.</summary>
        private static IEnumerable<string> SubEntityRefArm(List<Type> types)
        {
            // Rows: sub-entity type -> the owner path template RootRef pastes its element key onto, and the
            // ROOT kind the template hangs off. One row per non-root ref-addressable type; a type that gains
            // ref-addressability without a row here is itself a violation (last arm).
            var rows = new (Type Sub, string Root, string Path)[]
            {
                (typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoHavenZone), "S#", IdentityResolver.HavenZoneOwnerPath),
            };

            foreach (var (sub, root, path) in rows)
            {
                if (!IdentityResolver.IsRefAddressableType(sub) || IdentityResolver.IsRootEntityType(sub))
                {
                    yield return "L28 subentity-ref-row-stale: " + sub.FullName + " is declared here as a sub-entity ref " +
                                 "kind but IsRefAddressableType no longer names it (or it became a root) — the row " +
                                 "asserts nothing and the next reader will trust it";
                    continue;
                }
                if (!IdentityResolver.TypeKeyable(sub))
                {
                    yield return "L28 subentity-ref-unkeyable: " + sub.FullName + " rides as an EntityRef but " +
                                 "IdentityResolver.KeyOf can no longer derive its element key (id-probe table) — " +
                                 "RootRef returns null and every reference to one ships as a wire NULL";
                    continue;
                }
                var rootType = IdentityResolver.RootKinds.FirstOrDefault(r => r.Key == root).Type;
                if (rootType == null)
                {
                    yield return "L28 subentity-ref-root-absent: root kind '" + root + "' is gone from " +
                                 "IdentityResolver.RootKinds, so no " + sub.Name + " ref can ever resolve";
                    continue;
                }

                // Walk the template exactly as Resolve does, but over types instead of instances.
                Type cur = rootType, twinDto = null, terminalElem = null;
                string fail = null, terminalClass = null;
                foreach (var seg in path.Split('.'))
                {
                    RailField f;
                    if (twinDto != null)
                    {
                        f = RailType.GetBridged(cur, twinDto)?.FieldByName(seg);
                        if (f == null) { fail = "no bridged member '" + seg + "' on " + cur.Name + " <= " + twinDto.Name; break; }
                        if (f.Fi == null && f.Pi == null)
                        {
                            var decl = f.ValueType?.DeclaringType;
                            if (decl == null || !typeof(UnityEngine.Component).IsAssignableFrom(decl))
                            { fail = "'" + seg + "' resolves to no live member and dispatches to no Component"; break; }
                            cur = decl; twinDto = f.ValueType; continue;   // Resolve's GetComponent hop
                        }
                        twinDto = null;
                    }
                    else
                    {
                        f = RailType.Get(cur)?.FieldByName(seg);
                        if (f == null) { fail = "no member '" + seg + "' on " + cur.Name; break; }
                        if (typeof(Base.Core.IInstanceData).IsAssignableFrom(f.ValueType))
                        { twinDto = RailMeta.FindBridge(cur) ?? f.ValueType; continue; }  // Resolve's DTO hop
                    }
                    terminalClass = f.Class.ToString();
                    terminalElem = f.ElemType;
                    cur = f.ValueType;
                }
                if (fail != null)
                {
                    yield return "L28 subentity-ref-path-broken: the owner path '" + root + "." + path + "' that names a " +
                                 sub.Name + " no longer walks the metadata — " + fail + ". Every ref to one ships as a " +
                                 "wire NULL and the client clears its live reference, with the FIELD still classifying " +
                                 "as a perfectly good Leaf";
                    continue;
                }
                if (terminalClass != FieldClass.EntityCollection.ToString() || terminalElem != sub)
                    yield return "L28 subentity-ref-terminal: the owner path '" + root + "." + path + "' ends in a " +
                                 terminalClass + " of " + (terminalElem?.Name ?? "<none>") + ", not an EntityCollection of " +
                                 sub.Name + " — only a KEYED collection is addressable by '#key', so the ref cannot " +
                                 "resolve (and if it turned into a list of refs, the path is circular: nothing would " +
                                 "descend into an element and the sub-entity's own state would stop shipping)";
            }

            // The reverse direction: a type may not become ref-addressable without a row above.
            foreach (var t in types)
                if (IdentityResolver.IsRefAddressableType(t) && !IdentityResolver.IsRootEntityType(t) &&
                    !rows.Any(r => r.Sub.IsAssignableFrom(t)))
                    yield return "L28 subentity-ref-undeclared: " + t.FullName + " rides as an EntityRef but is not a " +
                                 "root and has no owner-path row in SubEntityRefArm — its key shape is unasserted, so " +
                                 "a broken owner path would only ever surface in-game as a cleared reference";
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

        /// <summary>L39 — the geoscape EVENT-WINDOW RAISE family (surface 0xB6), which REPLACED the
        /// record-derived backlog this slot used to hold (L26, deleted 2026-07-30 together with the engine
        /// it asserted). TWO silent failures put it here, and both are falsified below.
        ///
        /// (a) A window raised against a context the peer cannot rebuild. <c>GeoscapeEventRecord</c> persists
        /// id/timestamps/state/_selectedChoice/_triggerCount and NOTHING else, so a record-derived window had
        /// nothing to build a <c>GeoscapeEventContext</c> from — measured: 54 of 94 replayed raises had
        /// <c>Site==null</c>. Every token replacer dereferences <c>context.Site</c>/<c>.Vehicle</c> unguarded
        /// (that is L37's subject), and the NRE lands inside <c>UIStateGeoscapeEvent.EnterState</c> AFTER the
        /// raise has already logged SUCCESS, leaving a correctly localized title over the scene's baked dev
        /// placeholder text and four live-looking dead buttons — clicking through reached an active START
        /// MISSION. <see cref="EventPopup.ContextRefusal"/> is the decision that must refuse such a raise
        /// OUTRIGHT rather than render it, and it is pure precisely so it can be falsified right here.
        ///
        /// (b) A pre-join event replayed. The bound was a per-peer PlayerPrefs cursor with a one-sided clamp,
        /// so any peer that had ever persisted one bypassed the first-sight floor and re-raised the campaign's
        /// entire history (measured: a 94-record backlog, 97 windows, on BOTH clients at join). The fix is not
        /// a better cursor — it is that a window is a LIVE host→client message with no history behind it at
        /// all. That is a STRUCTURAL property, so it is asserted structurally: no API may exist that turns the
        /// mirrored record set into windows. A future reader who re-adds one re-arms the flood, and this is
        /// the line that stops them.
        ///
        /// (c) The host's TEXT written into the shared DEF. The defs are one graph the whole session reads, so
        /// a raise that stamps the host's title/narrative onto <c>GeoscapeEventData</c> rewrites every FUTURE
        /// raise of that event — the exact clobber <see cref="DefOwnership"/> exists to refuse, except this
        /// write bypassed it by not going through the rail at all. It cannot be guarded by "stamp only when
        /// the local def is EMPTY" either: the stamp is a <c>doNotLocalize</c> bind, which localizes to its own
        /// literal (LocalizedTextBind.cs:37-41), so the guard is false from the first stamp onward and a def
        /// the host rewrites PER ROLL (TFTV VoidOmen) freezes on roll one. The window therefore gets a PRIVATE
        /// COPY of the data, which is asserted below to leave the def bit-identical while still carrying the
        /// host text — and to keep sharing the def's <c>Choices</c> list, whose element identity the buttons
        /// and <c>CompleteEvent</c> both key on.
        ///
        /// (d) A window dismissed by an OLD answer. The raise (0xB6) and the record (0xAC) are separate
        /// surfaces flushing on their own cycles, so a RE-triggered event's window opens while
        /// <c>GetEventRecord</c> still returns the previous trigger's Completed record. Read naively that says
        /// "already answered": the fresh picker opens fully greyed and the next ES delta closes it under the
        /// player. The open window is therefore BOUND to the raise that opened it and only a resolution at
        /// that trigger or later may freeze or dismiss it.
        ///
        /// Plus the wire itself (a dropped payload field is failure (a) one layer down: the window is built
        /// against the WRONG context), the deploy-prompt exclusion (a host-local arrival decision mirrored as
        /// a second window the other peer can never close), and the surface id's own uniqueness.</summary>
        private static IEnumerable<string> EventRaiseLaw()
        {
            // ── (a) context validity ──────────────────────────────────────
            if (EventPopup.ContextRefusal("", false, "", false) != null)
                yield return "L39 siteless-event-refused: a payload carrying NO site and NO vehicle was REFUSED — a " +
                             "site-less event legitimately has neither and the host rendered it that way too, so " +
                             "every such window would be dropped instead of mirrored";
            if (EventPopup.ContextRefusal("S#5", true, "V#2@abc", true) != null)
                yield return "L39 resolved-context-refused: a payload whose site AND vehicle both resolve was " +
                             "REFUSED — no client could ever be shown anything";
            if (string.IsNullOrEmpty(EventPopup.ContextRefusal("S#5", false, "", true)))
                yield return "L39 contextless-window-raised: a payload naming site S#5, which does NOT resolve on " +
                             "this peer, was accepted (or refused with a BLANK reason) — the rebuilt context would " +
                             "carry Site==null, every [HavenName]/[HavenLeader] token would deref null inside " +
                             "EnterState, and the window would render half-built over the scene's placeholder text " +
                             "with nothing in any log saying it broke";
            if (string.IsNullOrEmpty(EventPopup.ContextRefusal("", true, "V#2@abc", false)))
                yield return "L39 contextless-window-raised: a payload naming aircraft V#2@abc, which does NOT " +
                             "resolve on this peer, was accepted (or refused with a BLANK reason) — an " +
                             "[AircraftName] token would deref a null Vehicle inside EnterState, same half-built " +
                             "window, same silence";

            // ── (b) no replay: the record→window derivation must not come back ──
            foreach (var gone in new[] { "Backlog", "Mode", "FirstSightCursor", "SeedCursor", "ClientTick", "Sync",
                                         "LoadCursor", "StoreCursor", "FormatCursor", "ParseCursor", "CursorKey",
                                         "RetireClosed" })
                if (typeof(EventPopup).GetMember(gone, AllMembers).Length != 0)
                    yield return "L39 backlog-derivation-returned: EventPopup." + gone + " exists again — a window is " +
                                 "a LIVE host→client raise with no history behind it, so any function that derives " +
                                 "windows from the mirrored records re-arms the pre-join flood (97 windows on every " +
                                 "joining client) against records that cannot rebuild a context anyway";

            // ── the wire ──────────────────────────────────────────────────
            var p = new EventPopup.Raise
            {
                EventId = "EV_test",
                SiteRef = "S#7",
                VehicleRef = "V#3@abc",
                Title = "Haven under attack",
                Narrative = "[HavenName] calls for help",
            };
            var back = EventPopup.Decode(EventPopup.Encode(42u, p), out uint seq);
            if (seq != 42u)
                yield return "L39 raise-round-trip: seq came back as " + seq + ", not 42 — the idempotence guard reads " +
                             "a wrong number and either drops live windows or lets one re-delivery open a second one";
            if (back.EventId != p.EventId || back.SiteRef != p.SiteRef || back.VehicleRef != p.VehicleRef ||
                back.Title != p.Title || back.Narrative != p.Narrative)
                yield return "L39 raise-round-trip: the payload came back as (" + back.EventId + "," + back.SiteRef +
                             "," + back.VehicleRef + "," + back.Title + "," + back.Narrative + ") — a dropped or " +
                             "shifted field means the client builds the window against the WRONG context";
            var siteless = EventPopup.Decode(EventPopup.Encode(1u, new EventPopup.Raise { EventId = "EV_siteless" }), out _);
            if (siteless.SiteRef != "" || siteless.VehicleRef != "" || siteless.Title != "" || siteless.Narrative != "")
                yield return "L39 raise-round-trip: a site-less payload's null fields did not come back as EMPTY " +
                             "strings — ContextRefusal keys on IsNullOrEmpty, so garbage there would refuse every " +
                             "site-less window in the game";

            // ── the deploy-prompt exclusion ───────────────────────────────
            if (!EventPopup.IsPureDeployPrompt(new[] { true, false }, new[] { true, true }))
                yield return "L39 deploy-prompt-mirrored: 'Deploy / Leave' (one mission choice + one bare decline) is " +
                             "not classified as a pure deploy prompt — the host's LOCAL arrival decision mirrors as a " +
                             "second window on the other peer, which its host-side cancel never closes";
            if (EventPopup.IsPureDeployPrompt(new[] { true, false }, new[] { true, false }))
                yield return "L39 story-event-suppressed: a mission choice MIXED with a real rewarded alternative was " +
                             "classified as a pure deploy prompt — that whole story event is then never mirrored " +
                             "(v1 9e80b24 regression, and the client simply never learns the window existed)";
            if (EventPopup.IsPureDeployPrompt(new[] { false, false }, new[] { true, true }))
                yield return "L39 story-event-suppressed: an event with NO mission choice at all was classified as a " +
                             "deploy prompt — ordinary narrative windows would stop mirroring";

            // ── the wire TEXT: a private copy, never the shared def ───────
            var titleBind = new Base.UI.LocalizedTextBind("KEY_TITLE");
            var bodyBind = new Base.UI.LocalizedTextBind("KEY_BODY");
            var altBind = new Base.UI.LocalizedTextBind("KEY_ALT");
            var variation = new EventTextVariation { General = bodyBind, Alt = altBind };
            var def = new PhoenixPoint.Geoscape.Events.Eventus.GeoscapeEventData
            {
                EventID = "EV_test",
                Title = titleBind,
                Description = { variation },
                Choices = { new GeoEventChoice() },
            };
            var defChoices = def.Choices;
            var shown = EventPopup.WithWireTexts(def, "HOST TITLE", "HOST BODY");
            var shownLast = shown.Description[shown.Description.Count - 1];

            if (ReferenceEquals(shown, def))
                yield return "L39 wire-text-clobbers-def: the host text was applied to the DEF ITSELF (the same instance " +
                             "came back) — a def is SHARED state on this peer, which is the whole reason DefOwnership " +
                             "refuses to descend into one, so a client raise would permanently rewrite the title and " +
                             "narrative of every FUTURE raise of that event, session-wide and with no undo";
            if (!ReferenceEquals(def.Title, titleBind) || def.Title.LocalizationKey != "KEY_TITLE" ||
                def.Description.Count != 1 || !ReferenceEquals(def.Description[0], variation) ||
                !ReferenceEquals(variation.General, bodyBind) || variation.General.LocalizationKey != "KEY_BODY" ||
                !ReferenceEquals(variation.Alt, altBind))
                yield return "L39 wire-text-clobbers-def: the host text reached the DEF's own Title/Description binds — " +
                             "and because a doNotLocalize bind localizes to its LITERAL (LocalizedTextBind.cs:37-41), " +
                             "nothing downstream can ever tell that text from the def's own again: the clobber is " +
                             "invisible, permanent and self-poisoning";
            if (ReferenceEquals(shownLast, variation))
                yield return "L39 wire-text-clobbers-def: the copy's LAST description variation is the DEF's own " +
                             "EventTextVariation instance — writing its General is the same shared-def mutation one " +
                             "level down";
            if (shown.Title?.LocalizationKey != "HOST TITLE" || shownLast.General?.LocalizationKey != "HOST BODY")
                yield return "L39 wire-text-lost: the raised window did not carry the host's resolved text (title=\"" +
                             shown.Title?.LocalizationKey + "\", body=\"" + shownLast.General?.LocalizationKey + "\") — a " +
                             "def whose text exists ONLY as a host-side runtime mutation (TFTV VoidOmen) renders blank, " +
                             "and one the host composed over a valid static key (TFTVBaseDefenseGeoscape.cs:1227 then " +
                             ":1250) renders the generic version while the host reads the composed one";
            if (shownLast.Alt != null)
                yield return "L39 wire-text-overridden-by-alt: the copy kept the def's Alt variation — GetText prefers " +
                             "Alt for a female haven leader (EventTextVariation.cs:21-27), so on those havens the host " +
                             "text is silently replaced by this peer's own def text again";
            if (!ReferenceEquals(shown.Choices, defChoices))
                yield return "L39 wire-text-breaks-choice-identity: the data handed to the window carries a DIFFERENT " +
                             "Choices list than the def — the buttons hold the def's own GeoEventChoice instances " +
                             "(SiteBaseChoiceButton.cs:43-47), so ShowingRealChoices stops recognising the picker and " +
                             "CompleteEvent's EventData.Choices.Contains(choice) (GeoscapeEvent.cs:92) throws";
            if (!ReferenceEquals(EventPopup.WithWireTexts(def, "", ""), def))
                yield return "L39 wire-text-copies-for-nothing: a raise carrying no host text still allocated a copy — " +
                             "with nothing to apply the window must ride the def's own data";

            // The self-poison shape: a def whose keys are EMPTY here because the host wrote its text at
            // ROLL time. Applied twice, the second raise must show the SECOND text.
            var runtimeDef = new PhoenixPoint.Geoscape.Events.Eventus.GeoscapeEventData
            {
                EventID = "EV_voidomen",
                Title = new Base.UI.LocalizedTextBind(""),
                Description = { new EventTextVariation { General = new Base.UI.LocalizedTextBind("") } },
            };
            EventPopup.WithWireTexts(runtimeDef, "ROLL 1", "ROLL 1 BODY");
            var roll2 = EventPopup.WithWireTexts(runtimeDef, "ROLL 2", "ROLL 2 BODY");
            if (roll2.Title?.LocalizationKey != "ROLL 2" ||
                roll2.Description[roll2.Description.Count - 1].General?.LocalizationKey != "ROLL 2 BODY")
                yield return "L39 wire-text-self-poisoned: the SECOND raise of the same def kept the FIRST raise's text " +
                             "— the 'apply only where the local def resolves EMPTY' rule is back, and one application " +
                             "makes the def permanently non-empty; TFTV rewrites VoidOmen_{roll} per roll " +
                             "(TFTVODIandVoidOmenRoll.cs:638-639), so every later roll would show the first roll's story";

            // ── the open window is bound to ITS raise (stale-record race) ─
            if (EventPopup.RaiseTriggerCount(GeoscapeEventRecordState.Completed, 3) != 4)
                yield return "L39 raise-binding-inherits-stale-answer: a window raised while GetEventRecord still returns " +
                             "the PREVIOUS trigger's Completed record was bound to that old trigger — the fresh picker " +
                             "opens fully greyed and the next ES delta closes it under the player";
            if (EventPopup.RaiseTriggerCount(GeoscapeEventRecordState.Triggered, 4) != 4)
                yield return "L39 raise-binding-overshoots: a window raised over an already-OPEN (Triggered) record was " +
                             "bound to the NEXT trigger — its own answer would then never count, so the picker never " +
                             "freezes and never closes and both peers can resolve it";
            if (EventPopup.IsResolvedForRaise(GeoscapeEventRecordState.Completed, 3, 4, false))
                yield return "L39 stale-resolution-dismisses: a record Completed at trigger #3 was applied to the window " +
                             "raised for trigger #4 — that is the re-trigger race (the record's 0xAC delta lands " +
                             "0.25-0.75 s after the 0xB6 raise, separate surfaces), and RepaintDialog answers a " +
                             "resolution with view.FinishQueriedState(): the window the player is looking at vanishes";
            if (!EventPopup.IsResolvedForRaise(GeoscapeEventRecordState.Completed, 4, 4, false))
                yield return "L39 live-resolution-ignored: the answer to the very raise the window shows (#4) was treated " +
                             "as stale — the picker another peer just answered stays open with every button dead and Esc " +
                             "as its only exit, which is the state the dismiss exists to prevent";
            if (EventPopup.IsResolvedForRaise(GeoscapeEventRecordState.Triggered, 4, 4, false))
                yield return "L39 open-record-frozen: a still-OPEN (Triggered) record was reported resolved — every " +
                             "picker in the game would open greyed and close on the next delta";
            if (!EventPopup.IsResolvedForRaise(GeoscapeEventRecordState.Completed, 9, 0, false))
                yield return "L39 unbound-window-unfrozen: a window with NO raise binding (the host's own, or one restored " +
                             "from a save) stopped honouring its resolved record — the freeze and the dismiss are " +
                             "peer-symmetric by law, so that turns them off on the host";

            // ── the surface id ────────────────────────────────────────────
            int declaring = typeof(SurfaceIds).GetFields(BindingFlags.Public | BindingFlags.Static)
                                              .Count(f => f.FieldType == typeof(byte) &&
                                                          (byte)f.GetValue(null) == SurfaceIds.GeoEventRaise);
            if (declaring != 1)
                yield return "L39 surface-id-collision: 0x" + SurfaceIds.GeoEventRaise.ToString("X2") + " is declared " +
                             "by " + declaring + " constants — two senders on one id mis-route silently on the peer";
            int raiseId = SurfaceIds.GeoEventRaise;   // via a local: a const compare folds and the arm reads as dead code
            if (raiseId < 0xA0 || raiseId > 0xBF)
                yield return "L39 surface-id-partition: the raise surface 0x" + SurfaceIds.GeoEventRaise.ToString("X2") +
                             " sits outside the geoscape partition 0xA0-0xBF, where the tactical fast-path is " +
                             "consulted FIRST and would consume it";
        }

        /// <summary>L44 — a window the GAME answered itself must never be read as a lost race. The game
        /// auto-completes a single-choice event at TRIGGER time, before the window is queued
        /// (GeoscapeEventSystem.cs:651-655), so its record is Completed on every peer the instant the window
        /// opens, with no user input anywhere. Read as "somebody else won", that froze the HOST's own picker
        /// click-dead and auto-dismissed the CLIENTS' copy ~0.2 s after opening — one root cause, two faces,
        /// and the peers left on different events. Headless because the whole thing is a race no manual test
        /// reproduces on demand, and its only symptom is a window that will not close.</summary>
        private static IEnumerable<string> PreAnsweredEventLaw()
        {
            // ── the game's own predicate, mirrored ────────────────────────
            foreach (var n in new[] { 0, 1 })
                if (!EventPopup.PreAnsweredAtTrigger(n, false))
                    yield return "L44 pre-answer-unrecognised: an event with " + n + " choice(s) was not recognised as " +
                                 "one the game answers ITSELF at trigger — GeoscapeEventData.HasSingleChoice is " +
                                 "Choices.Count <= 1 (GeoscapeEventData.cs:65) and GeoscapeEventSystem.cs:651 completes " +
                                 "exactly those before raising them, so its already-Completed record is read as another " +
                                 "peer's answer: the host's own picker freezes click-dead and every client's copy is " +
                                 "dismissed under the player";
            if (EventPopup.PreAnsweredAtTrigger(2, false))
                yield return "L44 open-picker-treated-as-pre-answered: a REAL 2-choice picker was classified as " +
                             "pre-answered at trigger — the freeze and the dismiss then never fire for it, so both " +
                             "peers can answer the same event and the reward lands twice";
            if (EventPopup.PreAnsweredAtTrigger(1, true))
                yield return "L44 marketplace-treated-as-pre-answered: a marketplace event was classified as " +
                             "pre-answered — GeoscapeEventSystem.cs:651 excludes it explicitly (IsEventTheMarketplace), " +
                             "because the shop is an N-purchase window whose record stays open across every purchase";

            // ── THE gate: both directions, so neither arm can pass vacuously ─
            if (EventPopup.IsResolvedForRaise(GeoscapeEventRecordState.Completed, 1, 1, true))
                yield return "L44 pre-completed-window-frozen: the record the game completed AT TRIGGER was applied to " +
                             "the very window that trigger opened — that is the whole bug: FreezeChoiceButtons marks the " +
                             "winner IsSelected, PhoenixGeneralButton.OnPointerClick:327 refuses a selected button, and " +
                             "since the host applies no deltas nothing ever unsticks it (host W2); on a client " +
                             "RepaintDialog answers the same record with FinishQueriedState and the window vanishes (W1)";
            if (!EventPopup.IsResolvedForRaise(GeoscapeEventRecordState.Completed, 1, 1, false))
                yield return "L44 live-resolution-ignored: a resolution that DID arrive after the raise stopped counting " +
                             "— the gate now answers no to everything, so no picker is ever frozen and two peers can " +
                             "answer the same event";

            // Non-vacuity: one gate, one shape. A surviving 3-argument overload would let a call site ask the
            // old question and freeze a window the game answered itself, with this law still green.
            var gates = typeof(EventPopup).GetMethods(AllMembers).Where(m => m.Name == "IsResolvedForRaise").ToList();
            if (gates.Count != 1 || gates[0].GetParameters().Length != 4)
                yield return "L44 freeze-gate-bypassable: EventPopup.IsResolvedForRaise exists in " + gates.Count +
                             " shape(s) with " + (gates.Count == 0 ? 0 : gates[0].GetParameters().Length) + " parameter(s)" +
                             " — the pre-answered arm is only law if EVERY caller must pass it, and an overload without " +
                             "it silently restores the old behaviour on whichever seam still calls it";

            // Non-vacuity: the predicate must still be moored to the game metadata it mirrors. If
            // HasSingleChoice moved, the arms above are asserting a rule about nothing.
            if (typeof(PhoenixPoint.Geoscape.Events.Eventus.GeoscapeEventData).GetProperty("HasSingleChoice") == null)
                yield return "L44 pre-answer-predicate-unmoored: GeoscapeEventData no longer exposes HasSingleChoice — " +
                             "the trigger-time auto-answer this law mirrors (GeoscapeEventSystem.cs:651) has moved, so " +
                             "the predicate is guessing and every arm above passes against a rule the game dropped";
        }

        /// <summary>L45 — when a resolution DOES legitimately arrive after the raise, the other peers must
        /// SEE it replayed, not have the window yanked. Winner highlighted and still clickable, losers greyed,
        /// one click to the native result page. The single field v2 dropped —
        /// <c>PhoenixGeneralButton.IsNonInteractableWhenSelected</c>:37 — is what makes the difference between
        /// a replay and a hang, because :327 early-returns on <c>IsSelected &amp;&amp; IsNonInteractableWhenSelected</c>.</summary>
        private static IEnumerable<string> ReplayVisualsLaw()
        {
            var open = EventPopup.PaintChoice(decided: false, isWinner: false);
            if (open.Selected || !open.Interactable || !open.DeadWhenSelected)
                yield return "L45 pooled-button-poisoned: an UNDECIDED picker's button came back selected=" +
                             open.Selected + " interactable=" + open.Interactable + " deadWhenSelected=" +
                             open.DeadWhenSelected + " — the choice buttons are POOLED and reused by the next window " +
                             "(AddChoicesButtons:67-75 builds the list once), so a flag left behind deadens that slot " +
                             "in the NEXT picker, including the OK button of a closing page, whose click is the only " +
                             "way out of it";

            var win = EventPopup.PaintChoice(decided: true, isWinner: true);
            if (!win.Selected)
                yield return "L45 winner-not-highlighted: the winning choice was not marked IsSelected — the other " +
                             "peers cannot see WHICH choice was taken, which is the whole point of replaying it";
            if (!win.Interactable)
                yield return "L45 winner-not-clickable: the winning choice was left non-interactable — " +
                             "OnPointerClick:327 also requires IsEnabled, so the player has no way to reach the result " +
                             "page and Esc is the only exit";
            if (win.DeadWhenSelected)
                yield return "L45 winner-click-dead: the winning choice kept IsNonInteractableWhenSelected — " +
                             "PhoenixGeneralButton.OnPointerClick:327 early-returns on (IsSelected && " +
                             "IsNonInteractableWhenSelected), so a HIGHLIGHTED winner is click-dead: the window can " +
                             "never be closed by clicking it (host W2), and the omission is what makes a merely " +
                             "cosmetic freeze fatal";

            var lose = EventPopup.PaintChoice(decided: true, isWinner: false);
            if (lose.Selected || lose.Interactable)
                yield return "L45 loser-still-live: a LOSING choice came back selected=" + lose.Selected +
                             " interactable=" + lose.Interactable + " — a second peer can then answer an event that is " +
                             "already resolved, and the losing peer pays its cost (UIModuleSiteEncounters.cs:571-573 " +
                             "charges before any funnel a guard can sit on)";

            // ── the dismiss arm: only when there is genuinely nothing to click ──
            if (EventPopup.DismissOnResolution(hasWinner: true, winnerHasOutcome: true))
                yield return "L45 replay-hard-dismissed: a decided picker WITH a clickable winning choice was closed by " +
                             "view.FinishQueriedState() instead of repainted as a replay — that is the client-side face " +
                             "of the bug: the window vanishes with no user input, the peer skips the event the host is " +
                             "still looking at, and from there on the two are on different events";
            if (!EventPopup.DismissOnResolution(hasWinner: false, winnerHasOutcome: false))
                yield return "L45 dead-picker-stranded: a decided picker with NO winner (native's -1 'no choice', " +
                             "UIModuleSiteEncounters.cs:562-566) was kept open — every button on it is dead and Esc is " +
                             "the only exit, which is exactly what the dismiss exists to prevent";
            if (!EventPopup.DismissOnResolution(hasWinner: true, winnerHasOutcome: false))
                yield return "L45 outcome-page-nre: a winning choice with a NULL Outcome was routed to the replay page — " +
                             "SetClosingEncounter:333 dereferences closingChoice.Outcome.OutcomeText unguarded, so the " +
                             "throw lands inside the click handler and the window is left half-repainted";

            // Non-vacuity: every field above is a REAL native widget member. If one is gone, the paint is a
            // rule about writes that no longer land, and the law would stay green while the picker stayed dead.
            var pgb = typeof(PhoenixPoint.Common.View.ViewControllers.PhoenixGeneralButton);
            foreach (var f in new[] { "IsSelected", "IsNonInteractableWhenSelected" })
                if (pgb.GetField(f)?.FieldType != typeof(bool))
                    yield return "L45 paint-unmoored: PhoenixGeneralButton." + f + " is not a bool field on the shipped " +
                                 "widget any more — the replay writes it by name, so the arms above assert a rule about " +
                                 "a write that no longer reaches the button";
            if (pgb.GetMethod("SetInteractable", new[] { typeof(bool) }) == null)
                yield return "L45 paint-unmoored: PhoenixGeneralButton.SetInteractable(bool) is gone — the greying half " +
                             "of the replay has no way to run";
            if (typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters).GetMethod(
                    "SetClosingEncounter", AllMembers, null,
                    new[] { typeof(GeoscapeEvent), typeof(GeoEventChoice), typeof(bool) }, null) == null)
                yield return "L45 replay-page-unreachable: UIModuleSiteEncounters.SetClosingEncounter(GeoscapeEvent, " +
                             "GeoEventChoice, bool) did not resolve — the replayed click has no native result page to " +
                             "land on, so the only remaining behaviour is the blunt dismiss this law forbids";
        }

        /// <summary>L46 — every peer must show the queued windows in the HOST's order. Windows are displayed
        /// one at a time out of a priority-ordered queue (GeoscapeViewSwitchQuery.QueryStateSwitch:77-82
        /// inserts before the first lower-priority entry, GetNextQueriedStateSwitch:111 pops [0]) and
        /// GeoscapeView.cs:2044/:2049/:2057 assigns 0, 10 or 15 — so a mirror that queues everything at 0
        /// puts the peers on different events the moment two windows are pending, with nothing in any log
        /// saying so.</summary>
        private static IEnumerable<string> DisplayOrderLaw()
        {
            // ── it survives the wire ──────────────────────────────────────
            foreach (var prio in new[] { 0, 10, 15 })   // :2044 default / TriggeredByEvent / :2049 supersede
            {
                var wire = EventPopup.Decode(EventPopup.Encode(7u, new EventPopup.Raise
                { EventId = "EV_prio", Priority = prio }), out uint prioSeq);
                if (wire.Priority != prio || prioSeq != 7u)
                    yield return "L46 display-order-dropped: a raise queued at priority " + prio + " came back as " +
                                 wire.Priority + " (seq " + prioSeq + ") — the client then inserts it at the wrong " +
                                 "place in GeoscapeViewSwitchQuery's list and shows a different window than the host";
            }

            // ── and BOTH ends actually touch it ───────────────────────────
            // The round-trip above is green for a field nobody fills and nobody reads, which is precisely the
            // failure mode: the wire would carry a faithful 0 for every window. Existence is compile-checked;
            // USE is not, so it is asserted on the IL of the two seams. Both are private — named, because a
            // rename that dodges this law is a rename that has to walk past it.
            var seams = new[]
            {
                // FILLS it in (stfld, not ldfld: the host also logs the value, and a log line must not be
                // able to satisfy a law about what goes on the wire)
                new { Seam = "HostBroadcast", Op = OpCodes.Stfld, Verb = "never FILLS IN",
                      Cost = "the host ships a constant 0 as its queue position for every window" },
                // READS it back out to queue with
                new { Seam = "RaiseMirrored", Op = OpCodes.Ldfld, Verb = "never READS",
                      Cost = "the client throws the host's queue position away and queues every mirrored window " +
                             "at GeoscapeViewStateSwitchRequest's 0 default" },
            };
            foreach (var s in seams)
            {
                var m = typeof(EventPopup).GetMethod(s.Seam, AllMembers);
                if (m == null)
                    yield return "L46 display-order-seam-gone: EventPopup." + s.Seam + " no longer exists, so the law " +
                                 "cannot see whether the host's queue position is still captured and honoured — the " +
                                 "round-trip arms above stay green either way";
                else if (!FieldRefs(m, s.Op).Any(f => f.DeclaringType == typeof(EventPopup.Raise) && f.Name == "Priority"))
                    yield return "L46 display-order-ignored: EventPopup." + s.Seam + " " + s.Verb + " Raise.Priority — " +
                                 s.Cost + ", so the peers show queued windows in DIFFERENT orders the moment two are " +
                                 "pending (GeoscapeView.cs:2044 gives an event-triggered raise 10 and :2049/:2057 bump " +
                                 "a superseding one to 15) and nothing in any log says so";
            }
        }

        /// <summary>L47 — the peer whose OWN click produced a resolution must not be asked to click again.
        /// A client's choice click is BLOCKED and relayed as a 0xB4 intent, and the answer comes back as an
        /// ordinary record delta that names the CHOICE and never the CHOOSER — so the repaint painted the
        /// answerer the same replay it paints observers (winner highlighted, still clickable) and the player
        /// had to confirm its own answer with a SECOND click. The memo
        /// (<c>EventPopup.NoteOwnAnswer</c>/<c>AnswerIsOurs</c>) is the only thing that tells initiator from
        /// observer; headless because the distinction only exists for the ~0.25-0.75 s between the click and
        /// the delta, and its symptom is one extra click nobody logs.</summary>
        private static IEnumerable<string> OwnAnswerLaw()
        {
            const string ev = "EV_pick", other = "EV_other";
            const int trig = 3, choice = 1;

            // ── the four axes, both directions, so no arm can pass vacuously ──
            if (!EventPopup.AnswerIsOurs(ev, trig, choice, ev, trig, choice))
                yield return "L47 own-answer-unrecognised: the peer that answered was not recognised as the author of " +
                             "the resolution it produced — it gets the OBSERVER replay (winner highlighted and still " +
                             "clickable) and has to click its own answer a SECOND time to reach the result page, which " +
                             "is the whole bug this law guards";
            if (EventPopup.AnswerIsOurs(null, 0, EventPopup.NothingPending, ev, 0, choice))
                yield return "L47 observer-fast-forwarded: a peer that never answered was treated as the author — every " +
                             "OBSERVING peer is then yanked past the replay to a result page for a choice it did not " +
                             "make, which is the dismiss bug the replay visuals (L45) exist to prevent";
            // The arm above states the OUTCOME for a real observer, and an empty memo fails the event axis
            // too — so it stays green if the sentinel guard is deleted. This one ISOLATES that guard: every
            // other axis matches, and only "nothing is pending" may reject it. Without it the guard is a belt
            // nothing holds to the rule, and it rots the moment the clear stops nulling the event id with it.
            if (EventPopup.AnswerIsOurs(ev, trig, EventPopup.NothingPending, ev, trig, EventPopup.NothingPending))
                yield return "L47 sentinel-answerable: 'nothing pending' was accepted as an answer that matched — the " +
                             "empty memo is only ever rejected because the CLEAR also nulls the event id, so this peer " +
                             "silently depends on two fields agreeing instead of on the sentinel that names the state";
            if (EventPopup.AnswerIsOurs(ev, trig, choice, other, trig, choice))
                yield return "L47 cross-event-fast-forwarded: a memo left by ANOTHER event answered for this one — the " +
                             "geoscape queues windows one at a time, so the very next window would open already " +
                             "fast-forwarded to a result page belonging to a different event";
            if (EventPopup.AnswerIsOurs(ev, trig, choice, ev, trig + 1, choice))
                yield return "L47 stale-answer-rearmed: a memo from a PREVIOUS raise of the same event answered for the " +
                             "fresh one — a re-triggered event opens a new window (EventPopup.RaiseTriggerCount) and " +
                             "fast-forwarding it on the old click answers a question the player was never shown";
            if (EventPopup.AnswerIsOurs(ev, trig, choice, ev, trig, choice + 1))
                yield return "L47 race-loser-fast-forwarded: a peer whose answer LOST the race was treated as the author " +
                             "— its intent was rejected and somebody else's choice won, so jumping it straight to that " +
                             "choice's result page shows it an outcome for a choice it did not pick";

            // -1 is native's REAL 'no choice' answer (UIModuleSiteEncounters.cs:562-566), not an absence. If
            // the 'nothing pending' sentinel ever collapses onto it, every no-choice answerer is silently
            // demoted to an observer and the double-click comes back for exactly that case.
            if (!EventPopup.AnswerIsOurs(ev, trig, -1, ev, trig, -1))
                yield return "L47 no-choice-answer-unrecognised: a peer that answered with native's -1 'no choice' was " +
                             "not recognised as the author — -1 is a real answer, so it must be tellable from 'this " +
                             "peer never answered'";
            // Read as METADATA, not as the constant: `EventPopup.NothingPending >= -1` is folded at compile
            // time, so the arm would be deleted from this method rather than checked (the compiler says so —
            // CS0162). Reflection also catches the sentinel being removed or retyped, which the fold cannot.
            var sentinel = typeof(EventPopup).GetField("NothingPending", AllMembers)?.GetRawConstantValue();
            if (!(sentinel is int nothing) || nothing >= -1)
                yield return "L47 sentinel-collides: EventPopup.NothingPending is " + (sentinel ?? "gone") +
                             ", which is a choice index a peer can actually answer with (-1 = native's 'no choice', " +
                             "0..n = the def's choices) — 'nothing pending' must be outside that range or the arm " +
                             "above is asserting two things that are the same value";

            // ── the seams actually USE it (existence is compile-checked; use is not) ──
            var ours = typeof(EventPopup).Assembly;
            var noteOwn = typeof(EventPopup).GetMethod("NoteOwnAnswer", AllMembers);
            var replay = typeof(EventPopup).GetMethod("ReplayResolution", AllMembers);
            var notOurs = typeof(EventPopup).GetMethod("ResolutionIsNotOurs", AllMembers);
            var capture = typeof(EventChoiceClientLock).GetMethod("Prefix", AllMembers);
            var repaint = typeof(EventPopup).GetMethod("RepaintDialog", AllMembers);

            if (noteOwn == null || replay == null || notOurs == null || capture == null || repaint == null)
            {
                yield return "L47 seam-gone: one of EventPopup.NoteOwnAnswer / .ReplayResolution / " +
                             ".ResolutionIsNotOurs / .RepaintDialog / EventChoiceClientLock.Prefix no longer resolves, " +
                             "so the wiring arms below cannot run and the pure arms above stay green against a rule " +
                             "nothing applies";
                yield break;
            }

            var captureCalls = Callees(capture, ours, directCallsOnly: true).ToList();
            var repaintCalls = Callees(repaint, ours, directCallsOnly: true).ToList();
            // Non-vacuity for every IL arm below: the walk ABANDONS the method on the first opcode it cannot
            // decode (Callees yield-breaks), and an abandoned walk returns an empty set that satisfies every
            // 'must NOT call' arm and fails no 'must call' arm loudly enough to be read as a wiring bug.
            if (captureCalls.Count == 0 || repaintCalls.Count == 0)
                yield return "L47 il-walk-abandoned: the IL scan found " + captureCalls.Count + " call(s) in " +
                             "EventChoiceClientLock.Prefix and " + repaintCalls.Count + " in EventPopup.RepaintDialog " +
                             "— both bodies really do call into this assembly, so an empty set means the walk gave up " +
                             "and every wiring arm in this law is asserting nothing";

            if (!captureCalls.Any(c => Same(c, noteOwn)))
                yield return "L47 initiator-unmarked: EventChoiceClientLock.Prefix does not call " +
                             "EventPopup.NoteOwnAnswer — the click is relayed as a 0xB4 intent with nothing remembering " +
                             "that THIS peer made it, so the memo is never armed, AnswerIsOurs is always false and the " +
                             "answering peer is painted the observer replay: the second click is back";
            if (!captureCalls.Any(c => Same(c, notOurs)))
                yield return "L47 double-charge-guard-dropped: EventChoiceClientLock.Prefix no longer calls " +
                             "EventPopup.ResolutionIsNotOurs — that guard is what keeps a click on an ALREADY-decided " +
                             "picker off the native path, whose Wallet.Take (UIModuleSiteEncounters.cs:571-573) runs " +
                             "two calls before any funnel EventCompleteArbiter can guard, so the peer pays a second time";
            if (!repaintCalls.Any(c => Same(c, replay)))
                yield return "L47 initiator-not-advanced: EventPopup.RepaintDialog does not call " +
                             "EventPopup.ReplayResolution — the answering peer's window is only ever repainted, so the " +
                             "delta that carries its OWN answer leaves it staring at a highlighted winner it must click " +
                             "again";

            // Non-vacuity, the OTHER direction: prove the scan DISCRIMINATES rather than matching anything.
            // HostBroadcast is a seam that provably must never arm the memo (the host's click is not blocked
            // and it has no answer in flight) — a scan that reported the memo there would be reporting noise.
            var hostSeam = typeof(EventPopup).GetMethod("HostBroadcast", AllMembers);
            if (hostSeam == null || Callees(hostSeam, ours, directCallsOnly: true).Any(c => Same(c, noteOwn)))
                yield return "L47 scan-indiscriminate: EventPopup.HostBroadcast is missing or appears to arm the answer " +
                             "memo — it is the control for the arms above (a host never blocks its own click, so it has " +
                             "nothing pending), and a scan that finds the memo there is matching noise rather than calls";

            // ── charged exactly once: the fast-forward is a PRESENTATION replay, never a synthetic re-click ──
            var game = typeof(GeoscapeEvent).Assembly;
            var completeEvent = typeof(GeoscapeEvent).GetMethod("CompleteEvent", AllMembers);
            var answerHandler = typeof(EventSync).GetMethod("HandleAnswer", AllMembers);
            if (completeEvent == null || answerHandler == null)
                yield return "L47 charge-probe-unmoored: GeoscapeEvent.CompleteEvent or EventSync.HandleAnswer did not " +
                             "resolve, so the 'the fast-forward resolves nothing' arm below cannot be trusted either way";
            // The positive control FIRST: a method that really does resolve an event must be SEEN to, or the
            // negative arm below is green because the probe cannot see resolutions at all.
            else if (!Callees(answerHandler, game, directCallsOnly: true).Any(c => Same(c, completeEvent)))
                yield return "L47 charge-probe-blind: EventSync.HandleAnswer — the ONE place that legitimately resolves " +
                             "a relayed answer — did not show up as calling GeoscapeEvent.CompleteEvent, so the probe " +
                             "cannot see a resolution and the arm below would pass for a RepaintDialog that made one";
            else if (Callees(repaint, game, directCallsOnly: true).Any(c => Same(c, completeEvent)))
                yield return "L47 fast-forward-resolves: EventPopup.RepaintDialog calls GeoscapeEvent.CompleteEvent — " +
                             "advancing the answering peer must be pure PRESENTATION (SetClosingEncounter over an " +
                             "instance marked resolved). Re-resolving locally re-runs GenerateFactionReward + " +
                             "ChoiceReward.Apply on a peer the host already charged, which is a second grant on top of " +
                             "the wallet delta already in flight";
        }

        /// <summary>L48 — EVERY window the game pushes at the player must have a reviewed answer to "does the
        /// other peer see this?". The rail captures ONE window kind (the event picker, at
        /// GeoscapeView.OnGeoscapeEventRaised) but the game pushes NINE through the same queue
        /// (GeoscapeViewSwitchQuery.QueryStateSwitch), and the eight others were neither mirrored nor
        /// declared — they simply appeared on one screen and nowhere else, with nothing in any log saying so.
        /// This law derives the reachable set from the GAME'S OWN IL rather than from a hand-typed list, so a
        /// kind added by a patch, a DLC or a mod fails the harness instead of silently desyncing a session.</summary>
        private static IEnumerable<string> WindowCoverageLaw(Assembly game)
        {
            var queue = typeof(GeoscapeViewSwitchQuery).GetMethod("QueryStateSwitch", AllMembers);
            var stateBase = typeof(GeoscapeViewState);
            if (queue == null)
            {
                yield return "L48 chokepoint-gone: GeoscapeViewSwitchQuery.QueryStateSwitch did not resolve — the one " +
                             "queue every PUSHED geoscape window goes through has moved, so neither the runtime gate " +
                             "nor this law can see what the game shows the player any more";
                yield break;
            }

            // The reachable set, from the game's IL: every type constructed inside a method that queues a
            // window. Derived, never listed — a hand-typed universe is a universe that goes stale silently,
            // which is the exact failure this law exists to make impossible.
            var reachable = new SortedDictionary<string, Type>(StringComparer.Ordinal);
            int queueCallers = 0;
            foreach (var t in game.GetTypes())
            {
                MethodBase[] members;
                try { members = t.GetMethods(AllMembers).Cast<MethodBase>().Concat(t.GetConstructors(AllMembers)).ToArray(); }
                catch { continue; }
                foreach (var m in members)
                {
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    List<MethodBase> calls;
                    try { calls = Callees(m, game).ToList(); } catch { continue; }
                    if (!calls.Any(c => Same(c, queue))) continue;
                    queueCallers++;
                    foreach (var c in calls)
                        if (c.IsConstructor && c.DeclaringType != null && stateBase.IsAssignableFrom(c.DeclaringType))
                            reachable[c.DeclaringType.FullName] = c.DeclaringType;
                }
            }

            // ── non-vacuity, both halves ────────────────────────────────────
            // An IL walk that finds nothing satisfies "every reachable kind is declared" perfectly.
            if (queueCallers == 0 || reachable.Count == 0)
                yield return "L48 scan-empty: the IL sweep found " + queueCallers + " method(s) calling " +
                             "QueryStateSwitch and " + reachable.Count + " window type(s) among them — the shipped " +
                             "game queues windows from nine call sites in GeoscapeView, so an empty result means the " +
                             "sweep is broken and every coverage arm below is asserting nothing";
            // And the ONE kind the rail provably does mirror must come out of that sweep, or the sweep is
            // finding a set that has nothing to do with the windows this rail actually handles.
            else if (!reachable.ContainsKey(typeof(UIStateGeoscapeEvent).FullName))
                yield return "L48 scan-unmoored: the sweep did not find UIStateGeoscapeEvent among the queued window " +
                             "types, yet GeoscapeView.OnGeoscapeEventRaised:2062 queues exactly that — so the derived " +
                             "universe is wrong and 'everything reachable is declared' is a statement about the wrong set";
            if (GeoWindowCoverage.Declared.Count == 0)
                yield return "L48 table-empty: GeoWindowCoverage.Declared is empty, so every window kind resolves to " +
                             "'undeclared' at runtime and this law degenerates into a list of everything";

            // ── the law: reachable ⊆ declared, and declared ⊆ reachable ─────
            foreach (var kv in reachable)
                if (GeoWindowCoverage.RuleFor(kv.Value) == null)
                    yield return "L48 window-undeclared: " + kv.Key + " is queued at " +
                                 "GeoscapeViewSwitchQuery.QueryStateSwitch but GeoWindowCoverage.Declared says nothing " +
                                 "about it — so nobody has decided whether the other peer should see this window, and " +
                                 "the default is that it appears on ONE screen and the peers drift apart looking at " +
                                 "different things. Declare it Mirrored / LocalOnly / Gap with a reason";
            foreach (var declared in GeoWindowCoverage.Declared.Keys.OrderBy(t => t.FullName, StringComparer.Ordinal))
                if (!reachable.ContainsKey(declared.FullName))
                    yield return "L48 window-declaration-stale: GeoWindowCoverage.Declared holds " + declared.FullName +
                                 ", which nothing queues at the chokepoint any more — a table carrying reasons for " +
                                 "windows the game no longer pushes is a table nobody trusts to be current, and the " +
                                 "REAL kind that replaced it is sitting undeclared";
            foreach (var kv in GeoWindowCoverage.Declared.OrderBy(k => k.Key.FullName, StringComparer.Ordinal))
                if (string.IsNullOrWhiteSpace(kv.Value?.Why))
                    yield return "L48 window-unreasoned: GeoWindowCoverage.Declared[" + kv.Key.Name + "] carries no " +
                                 "reason — the declaration is the REVIEW, and a bare verdict with nothing behind it is " +
                                 "how a wrong one survives";

            // ── and the gate is actually WIRED to the chokepoint ────────────
            // The table above is a document until something reads it on the live queue; without the patch a
            // window kind the game gains between builds is invisible until a player reports two screens.
            var gate = typeof(GeoWindowCoverageGate);
            var attr = gate.GetCustomAttributes(typeof(HarmonyPatch), inherit: false)
                           .Cast<HarmonyPatch>().Select(a => a.info).FirstOrDefault();
            if (attr == null || attr.declaringType != typeof(GeoscapeViewSwitchQuery) || attr.methodName != "QueryStateSwitch")
                yield return "L48 gate-unpatched: GeoWindowCoverageGate does not patch " +
                             "GeoscapeViewSwitchQuery.QueryStateSwitch (declaringType=" +
                             (attr?.declaringType?.Name ?? "none") + ", method=" + (attr?.methodName ?? "none") +
                             ") — the declared table is then never consulted at runtime and a NEW window kind is " +
                             "announced by nothing at all";
            var post = gate.GetMethod("Postfix", AllMembers);
            var announce = typeof(GeoWindowCoverage).GetMethod("Announce", AllMembers);
            if (post == null || announce == null ||
                !Callees(post, typeof(GeoWindowCoverage).Assembly, directCallsOnly: true).Any(c => Same(c, announce)))
                yield return "L48 gate-mute: the coverage gate's Postfix does not call GeoWindowCoverage.Announce — " +
                             "the patch is attached to the chokepoint and reports nothing, which is worse than no " +
                             "patch: it looks covered";
            if (post != null && gate.GetMethod("Prefix", AllMembers) != null)
                yield return "L48 gate-suppresses: GeoWindowCoverageGate grew a Prefix — the gate must OBSERVE the " +
                             "queue, never gate it. Suppressing an un-mirrored window on the host hides the host's own " +
                             "game from it to make two screens agree, which is not what syncing them means";
        }

        /// <summary>L49 — the MODAL family, the biggest window kind on the rail: 43 <c>ModalType</c>s ride the
        /// single <c>UIStateGeoModal</c> view state, so L48's per-view-state verdict can only ever say "modals
        /// are handled". This law asserts the SECOND axis — the per-ModalType table — plus the two properties
        /// that make a mirrored modal safe to render on a peer that owns none of the state behind it.
        ///
        /// (1) TOTALITY over the enum, derived from <c>typeof(ModalType)</c>'s own members rather than a
        /// hand-typed list: a modal added by a patch, a DLC or a mod fails the build instead of appearing on
        /// one screen and nowhere else. Non-vacuous in both directions — the derived universe must contain the
        /// anchors this rail actually reasons about, and the table must declare at least one kind Mirrored, or
        /// "everything is declared" is satisfied perfectly by declaring everything LocalOnly and shipping
        /// nothing.
        ///
        /// (2) THE CLIENT'S COPY IS NON-AUTHORITATIVE, asserted on IL because it is a property of the
        /// CONSTRUCTION and nothing at runtime would ever complain. Two halves, both silent if broken:
        /// a mirrored modal must be built with a NULL <c>DialogCallback</c> (every button funnels through
        /// <c>UIStateGeoModal.FinishDialog</c>:82 → <c>_dialogHandler?.Invoke</c>, so a handler is the ONLY
        /// thing standing between a client click and <c>LaunchMission</c>/<c>GeoAbility.Activate</c>), and it
        /// must never be marked <c>Persistent</c> — a persistent modal is save-restored through
        /// <c>RestoreContext.RegenerateState</c>:36, which rebuilds it with the game's OWN
        /// <c>level.View.ModalResultCallback</c> closure and hands a reloaded client exactly the authoritative
        /// buttons this file took away.
        ///
        /// (3) The seams, the route and the wire: POSTFIX-only capture on both native openers (suppressing a
        /// window on the host to make two screens agree is not what syncing them means — L48's rule, same
        /// reason), a payload round-trip, the refusal that keeps a half-built prefab off the screen, and the
        /// repaint-table entry without which the Exit+Enter fallback fires the HOST's own callback with
        /// <c>ModalResult.Close</c> on a window nobody closed.</summary>
        private static IEnumerable<string> ModalCoverageLaw()
        {
            // ── (1) totality over the enum, and it is the REAL enum ────────
            var universe = Enum.GetValues(typeof(ModalType)).Cast<ModalType>().Distinct()
                               .OrderBy(m => (int)m).ToList();
            if (universe.Count == 0)
                yield return "L49 enum-empty: ModalType has no members — the universe this law derives totality from " +
                             "resolved to nothing, so every arm below is asserting something about the empty set";
            foreach (var anchor in new[] { ModalType.GeoResearchComplete, ModalType.GeoHavenAttackBrief,
                                           ModalType.None, ModalType._CustomMission })
                if (!universe.Contains(anchor))
                    yield return "L49 enum-unmoored: ModalType." + anchor + " is not among the values this law swept, " +
                                 "yet the rail reasons about it by name — the derived universe is the wrong set and " +
                                 "'every modal is declared' is a statement about something else";
            foreach (var m in universe)
                if (GeoWindowCoverage.RuleForModal(m) == null)
                    yield return "L49 modal-undeclared: ModalType." + m + " (" + (int)m + ") is not in " +
                                 "GeoWindowCoverage.DeclaredModals — nobody has decided whether the other peer should " +
                                 "see that window, and the default is that it appears on ONE screen while the peers " +
                                 "drift apart looking at different things. Declare it Mirrored / LocalOnly / Gap with " +
                                 "a reason";
            foreach (var kv in GeoWindowCoverage.DeclaredModals.OrderBy(k => (int)k.Key))
            {
                if (!Enum.IsDefined(typeof(ModalType), kv.Key))
                    yield return "L49 modal-declaration-stale: GeoWindowCoverage.DeclaredModals holds " + (int)kv.Key +
                                 ", which is not a ModalType any more — a table carrying reasons for windows the game " +
                                 "no longer has is a table nobody trusts, and the REAL kind that replaced it is " +
                                 "sitting undeclared";
                if (string.IsNullOrWhiteSpace(kv.Value?.Why))
                    yield return "L49 modal-unreasoned: GeoWindowCoverage.DeclaredModals[" + kv.Key + "] carries no " +
                                 "reason — the declaration IS the review, and a bare verdict with nothing behind it is " +
                                 "how a wrong one survives";
            }
            // Non-vacuity: a table that declares everything LocalOnly passes every arm above while the mirror
            // ships not one window, which is indistinguishable from the gap this whole family exists to close.
            if (!GeoWindowCoverage.DeclaredModals.Values.Any(r => r != null && r.Sync == WindowSync.Mirrored))
                yield return "L49 nothing-mirrored: not one ModalType is declared Mirrored — the coverage table is " +
                             "total and the 0xB7 surface carries nothing, so every modal in the game is still a " +
                             "window one peer sees and the other does not";

            // ── (2) the client's copy cannot run game logic ────────────────
            var mirror = typeof(GeoModalMirror);
            // Callees filters by the DEFINING assembly of the callee (`callee.Module.Assembly == asm`), so a
            // game-side target has to be looked for with the game assembly and a mod-side one with ours.
            var gameAsm = typeof(GeoscapeView).Assembly;
            var raise = mirror.GetMethod("RaiseMirrored", AllMembers);
            var modalCtor = typeof(UIStateGeoModal).GetConstructors(AllMembers).FirstOrDefault();
            if (raise == null || modalCtor == null)
                yield return "L49 raise-seam-gone: GeoModalMirror.RaiseMirrored or the UIStateGeoModal constructor did " +
                             "not resolve — the law cannot see how the client builds a mirrored modal, and every arm " +
                             "below it stays green whatever it does";
            else
            {
                // POSITIVE CONTROL for the whole IL half: the one call this method provably makes. If the
                // walk cannot find it, the forbidden-callee arms below are asserting nothing.
                if (!Callees(raise, gameAsm).Any(c => c.IsConstructor && c.DeclaringType == typeof(UIStateGeoModal)))
                    yield return "L49 raise-il-blind: the IL sweep of GeoModalMirror.RaiseMirrored does not find the " +
                                 "UIStateGeoModal constructor it demonstrably calls — the callee walk is broken here, " +
                                 "so the 'no authoritative callee' arms below prove nothing";
            }
            // Nothing in this file may reach an authoritative funnel. ModalResultCallback is THE one that
            // matters (it is the closure the game itself would install, and it dispatches to LaunchMission /
            // mission.Cancel / GeoAbility.Activate / reward.Apply); FinishDialog is the other way in — driving
            // the native dialog's own completion from mod code would invoke whatever handler it holds.
            var forbidden = new[]
            {
                (M: (MethodBase)typeof(GeoscapeView).GetMethod("ModalResultCallback", AllMembers), Name: "GeoscapeView.ModalResultCallback",
                 Cost: "that is exactly the authoritative closure the game installs, and it dispatches Confirm to " +
                       "LaunchMission / mission.Cancel / GeoAbility.Activate on a peer that owns none of it"),
                (M: (MethodBase)typeof(UIStateGeoModal).GetMethod("FinishDialog", AllMembers), Name: "UIStateGeoModal.FinishDialog",
                 Cost: "driving the native dialog's own completion invokes whatever DialogCallback it holds, which " +
                       "on the HOST's copy is the authoritative one"),
                (M: (MethodBase)typeof(GeoscapeView).GetMethod("LaunchMission", AllMembers), Name: "GeoscapeView.LaunchMission",
                 Cost: "a client launching a tactical mission is law 5 quarantine breached from the presentation seam"),
            };
            foreach (var f in forbidden)
            {
                if (f.M == null)
                {
                    yield return "L49 forbidden-callee-unresolved: " + f.Name + " did not resolve, so this law cannot " +
                                 "tell whether the mirror calls it — the arm is asleep, not satisfied";
                    continue;
                }
                foreach (var m in mirror.GetMethods(AllMembers).Cast<MethodBase>()
                                        .Concat(mirror.GetConstructors(AllMembers))
                                        .Where(m => !m.IsAbstract && m.GetMethodBody() != null))
                    if (Callees(m, gameAsm).Any(c => Same(c, f.M)))
                        yield return "L49 client-runs-host-logic: GeoModalMirror." + m.Name + " calls " + f.Name +
                                     " — " + f.Cost + ". A mirrored modal is a PICTURE of the host's window; the only " +
                                     "thing that keeps it one is that its DialogCallback is null and nothing here " +
                                     "reaches around it";
            }
            var persistent = typeof(UIStateGeoModal).GetField("Persistent", AllMembers);
            if (persistent == null)
                yield return "L49 persistent-field-gone: UIStateGeoModal.Persistent did not resolve — the law that " +
                             "keeps a reloaded client from getting the game's own authoritative callback back " +
                             "(RestoreContext.RegenerateState:36) cannot be checked at all";
            else
                foreach (var m in mirror.GetMethods(AllMembers).Cast<MethodBase>().Where(m => m.GetMethodBody() != null))
                    if (FieldRefs(m, OpCodes.Stfld).Any(fld => fld == persistent))
                        yield return "L49 mirrored-modal-persisted: GeoModalMirror." + m.Name + " writes " +
                                     "UIStateGeoModal.Persistent — a persistent modal is SAVE-RESTORED through " +
                                     "RestoreContext.RegenerateState:36, which rebuilds it with the game's own " +
                                     "level.View.ModalResultCallback closure, so a client that reloads gets back " +
                                     "exactly the authoritative buttons the null handler took away";
            // No text, therefore no shared-def mutation: this family renders from the client's OWN defs, and
            // the moment someone "just stamps the host's string in" it becomes EventPopup.WithWireTexts'
            // problem (a def is shared state on this peer, and LocalizedTextBind(text, doNotLocalize).Localize()
            // returns the literal FOREVER). Positive control first, so the arm cannot pass on a blind walk.
            var stores = mirror.GetMethods(AllMembers).Cast<MethodBase>()
                               .Where(m => m.GetMethodBody() != null)
                               .SelectMany(m => FieldRefs(m, OpCodes.Stfld)).ToList();
            if (stores.Count == 0)
                yield return "L49 field-il-blind: the Stfld sweep of GeoModalMirror found no field writes at all, yet " +
                             "it fills a Raise struct field by field — the field walk is broken here, so the " +
                             "shared-def and Persistent arms prove nothing";
            foreach (var m in mirror.GetMethods(AllMembers).Cast<MethodBase>().Where(m => m.GetMethodBody() != null))
            {
                if (Callees(m, typeof(Base.UI.LocalizedTextBind).Assembly)
                        .Any(c => c.IsConstructor && c.DeclaringType == typeof(Base.UI.LocalizedTextBind)))
                    yield return "L49 wire-text-returned: GeoModalMirror." + m.Name + " constructs a LocalizedTextBind " +
                                 "— this family deliberately ships NO text (every mirrored modal's renderer paints " +
                                 "from the data object alone), and a host-resolved string is one step from being " +
                                 "stamped onto a shared def, which is session-permanent with no undo";
                foreach (var fld in FieldRefs(m, OpCodes.Stfld))
                    if (fld.DeclaringType != null && typeof(Base.Defs.BaseDef).IsAssignableFrom(fld.DeclaringType))
                        yield return "L49 shared-def-mutated: GeoModalMirror." + m.Name + " writes " +
                                     fld.DeclaringType.Name + "." + fld.Name + ", a field on a DEF — defs are shared " +
                                     "state on this peer (DefOwnership exists to stop the rail descending into one) " +
                                     "and a write there is session-permanent";
            }

            // ── (3) the seams: both openers, POSTFIX only, and each announces ──
            var announce = typeof(GeoWindowCoverage).GetMethod("AnnounceModal", AllMembers);
            var broadcast = mirror.GetMethod("HostBroadcast", AllMembers);
            var seams = new[]
            {
                (T: typeof(GeoModalOpenMirror), Method: "OpenModal"),
                (T: typeof(GeoModalOpenPersistentMirror), Method: "OpenModalPersistent"),
            };
            foreach (var s in seams)
            {
                var attr = s.T.GetCustomAttributes(typeof(HarmonyPatch), inherit: false)
                              .Cast<HarmonyPatch>().Select(a => a.info).FirstOrDefault();
                if (attr == null || attr.declaringType != typeof(GeoscapeView) || attr.methodName != s.Method)
                    yield return "L49 opener-unpatched: " + s.T.Name + " does not patch GeoscapeView." + s.Method +
                                 " (declaringType=" + (attr?.declaringType?.Name ?? "none") + ", method=" +
                                 (attr?.methodName ?? "none") + ") — that opener is one of only TWO places the " +
                                 "shipped game constructs a UIStateGeoModal, so a kind raised through it is captured " +
                                 "by nothing and announced by nothing";
                if (s.T.GetMethod("Prefix", AllMembers) != null)
                    yield return "L49 opener-suppresses: " + s.T.Name + " grew a Prefix — the capture must OBSERVE the " +
                                 "opener, never gate it. Suppressing a window on the host hides the host's own game " +
                                 "from it to make two screens agree, which is the opposite of the fix";
                var post = s.T.GetMethod("Postfix", AllMembers);
                if (post == null)
                    yield return "L49 opener-mute: " + s.T.Name + " has no Postfix — the patch is attached to the " +
                                 "opener and does nothing, which is worse than no patch: it looks covered";
                else if (announce == null ||
                         !Callees(post, mirror.Assembly, directCallsOnly: true).Any(c => Same(c, announce)))
                    yield return "L49 opener-unannounced: " + s.T.Name + ".Postfix does not call " +
                                 "GeoWindowCoverage.AnnounceModal — a modal kind nobody declared then opens on one " +
                                 "screen with nothing in any log saying the other peer never got it";
            }
            var mirrorSeam = typeof(GeoModalOpenMirror).GetMethod("Postfix", AllMembers);
            if (broadcast == null || mirrorSeam == null ||
                !Callees(mirrorSeam, mirror.Assembly, directCallsOnly: true).Any(c => Same(c, broadcast)))
                yield return "L49 raise-never-sent: GeoModalOpenMirror.Postfix does not call " +
                             "GeoModalMirror.HostBroadcast — the coverage table says these windows are Mirrored and " +
                             "the wire carries nothing, which is the declared-but-absent state this law exists to " +
                             "make impossible";
            // The surface is actually ROUTED: a raise that reaches no inbound hook is dropped by SurfaceRouter
            // as forward-compat, silently. Swept over the whole mod assembly because the route is a lambda in
            // SyncEngine's constructor and Callees sees call/callvirt only.
            var inbound = mirror.GetMethod("HandleInbound", AllMembers);
            if (inbound == null)
                yield return "L49 inbound-gone: GeoModalMirror.HandleInbound did not resolve — nothing can consume " +
                             "0x" + SurfaceIds.GeoModalRaise.ToString("X2");
            else if (!DeclaredTypes(mirror.Assembly).Where(t => t != mirror)
                         .SelectMany(t => { try { return t.GetMethods(AllMembers).Cast<MethodBase>(); } catch { return Enumerable.Empty<MethodBase>(); } })
                         .Where(m => { try { return m.GetMethodBody() != null; } catch { return false; } })
                         .Any(m => Callees(m, mirror.Assembly).Any(c => Same(c, inbound))))
                yield return "L49 surface-unrouted: nothing in the mod calls GeoModalMirror.HandleInbound, so every " +
                             "0x" + SurfaceIds.GeoModalRaise.ToString("X2") + " raise falls through SurfaceRouter's " +
                             "forward-compat drop and the client shows no modal at all — with no error anywhere, " +
                             "because dropping an unknown surface is by design";
            int declaringModal = typeof(SurfaceIds).GetFields(BindingFlags.Public | BindingFlags.Static)
                                                   .Count(f => f.FieldType == typeof(byte) &&
                                                               (byte)f.GetValue(null) == SurfaceIds.GeoModalRaise);
            if (declaringModal != 1)
                yield return "L49 surface-id-collision: 0x" + SurfaceIds.GeoModalRaise.ToString("X2") + " is declared " +
                             "by " + declaringModal + " constants — two senders on one id mis-route silently on the peer";
            // The repaint table entry is not cosmetic: without it OpenUiRepaint falls back to Exit+Enter, and
            // UIStateGeoModal.ExitState:116 invokes the HOST's own DialogCallback with ModalResult.Close.
            if (!UiNativeRepaint.Table.ContainsKey(typeof(UIStateGeoModal)))
                yield return "L49 modal-repaint-unregistered: UIStateGeoModal is not in UiNativeRepaint.Table, so a " +
                             "delta arriving while ANY modal is open repaints it by Exit+Enter — which runs " +
                             "ExitState:116 and invokes the host's own DialogCallback with ModalResult.Close on a " +
                             "window nobody closed, then re-fires GeoscapeView.ModalClosed";

            // ── the wire ──────────────────────────────────────────────────
            var p = new GeoModalMirror.Raise
            {
                ModalType = (int)ModalType.DiplomacyResearchBrief,
                Shape = GeoModalMirror.DataShape.DiplomacyReward,
                Ref = "F#abc-guid",
                Keys = new[] { "RES_one", "RES_two" },
                Num = 3,
                Priority = 99,
            };
            var back = GeoModalMirror.Decode(GeoModalMirror.Encode(11u, p), out uint seq);
            if (seq != 11u || back.ModalType != p.ModalType || back.Shape != p.Shape || back.Ref != p.Ref ||
                back.Num != p.Num || back.Priority != p.Priority ||
                back.Keys == null || !back.Keys.SequenceEqual(p.Keys))
                yield return "L49 modal-round-trip: the payload came back as (seq " + seq + ", type " + back.ModalType +
                             ", shape " + back.Shape + ", ref " + back.Ref + ", keys " +
                             (back.Keys == null ? "null" : string.Join("/", back.Keys)) + ", num " + back.Num +
                             ", priority " + back.Priority + ") — a dropped or shifted field means the client builds " +
                             "the modal against the wrong faction, with the wrong ids, or queues it in the wrong " +
                             "place, and every one of those renders as a window that merely looks right";
            var bare = GeoModalMirror.Decode(GeoModalMirror.Encode(1u, new GeoModalMirror.Raise
            { ModalType = (int)ModalType.GeoPhoenixBaseOutcome }), out _);
            if (bare.Ref != "" || bare.Keys == null || bare.Keys.Length != 0)
                yield return "L49 modal-round-trip: a data-less payload's null fields did not come back as an EMPTY " +
                             "string and an EMPTY array — DataRefusal and BuildData both key on those, so garbage " +
                             "there refuses or mis-builds every data-less modal in the game";

            // ── the refusal (pure, and non-vacuous in BOTH directions) ────
            if (GeoModalMirror.DataRefusal(GeoModalMirror.DataShape.None, false, 0, 0) != null)
                yield return "L49 dataless-modal-refused: a payload with NO data was REFUSED — GeoPhoenixBaseOutcome " +
                             "legitimately carries none and the host rendered it that way too, so that window would " +
                             "never be mirrored at all";
            if (GeoModalMirror.DataRefusal(GeoModalMirror.DataShape.ResearchComplete, true, 1, 1) != null)
                yield return "L49 resolved-modal-refused: a payload whose root AND every id resolve was REFUSED — no " +
                             "client could ever be shown anything";
            foreach (var bad in new[]
            {
                (Root: false, Want: 1, Got: 0, Case: "a root this peer cannot resolve"),
                (Root: true, Want: 0, Got: 0, Case: "no ids at all for a shape that needs one"),
                (Root: true, Want: 3, Got: 2, Case: "only part of the shipped ids resolving"),
            })
                if (string.IsNullOrWhiteSpace(GeoModalMirror.DataRefusal(GeoModalMirror.DataShape.DiplomacyReward,
                                                                        bad.Root, bad.Want, bad.Got)))
                    yield return "L49 halfbuilt-modal-raised: " + bad.Case + " was accepted (or refused with a BLANK " +
                                 "reason) — the prefab's data-bind casts modal.Data and dereferences it unguarded " +
                                 "(GeoReseatchCompleteDataBind.cs:97), the throw lands inside " +
                                 "UIStateGeoModal.EnterState, and what stays on screen is a half-built prefab over " +
                                 "the designers' baked placeholder text, logged as a success";

            // ── the shape derivation is by RUNTIME TYPE, and it fails LOUD ─
            if (GeoModalMirror.Describe(null).Shape != GeoModalMirror.DataShape.None)
                yield return "L49 dataless-shape-wrong: Describe(null) is not DataShape.None — a modal the game opens " +
                             "with no data (GeoscapeView.cs:1965) would be reported unsupported and never sent";
            if (GeoModalMirror.Describe("not a modal payload").Shape != GeoModalMirror.DataShape.Unsupported)
                yield return "L49 unknown-shape-silent: Describe() gave an object it has no description for something " +
                             "other than DataShape.Unsupported — the host would ship a payload the client cannot " +
                             "rebuild, and a modal whose data is null renders as an empty prefab instead of an error";
            var researchShape = GeoModalMirror.Describe(new PhoenixPoint.Geoscape.View.ViewControllers.Modal
                                                            .GeoResearchCompleteData());
            if (researchShape.Shape != GeoModalMirror.DataShape.ResearchComplete)
                yield return "L49 research-shape-wrong: Describe(GeoResearchCompleteData) is not " +
                             "DataShape.ResearchComplete — the one modal this family exists for stops being " +
                             "describable and the host silently declines every research-complete window";
        }

        /// <summary>Fields an IL body touches (InlineField operands), optionally narrowed to given opcodes —
        /// the same guarded walk <see cref="Callees"/> does for calls, abandoning the method the same way
        /// rather than guessing. Exists because "the type has the field" is compile-checked and therefore
        /// worthless as a law, while "this seam actually fills it in / reads it back" is checked by nothing
        /// else; the opcode filter is what stops a mere log line from satisfying a law about the wire.</summary>
        private static IEnumerable<FieldInfo> FieldRefs(MethodBase m, params OpCode[] only)
        {
            byte[] il = null;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
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
                if (op.OperandType == OperandType.InlineField &&
                    (only.Length == 0 || only.Any(o => o.Value == op.Value)))
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i), typeArgs, null); } catch { }
                    if (f != null) yield return f;
                }
                i += size;
            }
        }

        /// <summary>L57 — A FORCED RE-EMIT SCOPE MUST NAME A PATH THE WALK CAN ACTUALLY PRODUCE.
        /// <c>IntentRail.Reject</c> converges the losing peer with <c>DiffEngine.ForceReemit(prefix)</c>; a
        /// prefix matching no covered path re-emits nothing, so the reject converges nobody and the only
        /// symptom is an absence. <see cref="EventSync"/> shipped <c>"ES.EncounterRecords#&lt;eventId&gt;"</c>
        /// — the <c>&lt;path&gt;.&lt;Field&gt;#&lt;key&gt;</c> form the walk emits ONLY for
        /// <see cref="FieldClass.EntityCollection"/> fields (DiffEngine.cs, the keyed-element arm).
        /// <c>EncounterRecords</c> is an <see cref="FieldClass.EntityList"/>: one canonical blob at its
        /// OWNER's path, so the only prefix that reaches it is the "ES" root key itself. Both halves are
        /// pinned here, and the law INVERTS if the classification does — a reclassification to
        /// EntityCollection makes the root scope the wrong (over-broad) answer, and says so. The runtime belt
        /// is DiffEngine's zero-match log; this is the static one, because a reject is the one path nobody
        /// exercises by hand. Falsify by restoring the element form, or by removing "ES" from
        /// <c>IdentityResolver.RootKinds</c>.</summary>
        private static IEnumerable<string> RejectScopeLaw()
        {
            const string root = "ES";
            if (!IdentityResolver.RootKinds.Any(r => r.Key == root))
                yield return "L57 reject-scope: root key '" + root + "' is gone from IdentityResolver.RootKinds — " +
                             "EventSync.RecordScope names a path the walk never emits, so a rejected event answer " +
                             "re-emits nothing and the losing peer keeps its open picker";
            var recF = RailType.Get(typeof(GeoscapeEventSystem))?.FieldByName("EncounterRecords");
            if (recF == null)
                yield return "L57 reject-scope: GeoscapeEventSystem.EncounterRecords is no longer in the rail table — " +
                             "EventSync.RecordScope cannot be checked against the paths the walk produces";
            else if (recF.Class == FieldClass.EntityCollection)
            {
                if (EventSync.RecordScope == root)
                    yield return "L57 reject-scope: EncounterRecords now classifies EntityCollection, so every record " +
                                 "HAS its own element path — narrow EventSync.RecordScope to \"" + root +
                                 ".EncounterRecords#<eventId>\" instead of re-emitting the whole ledger";
            }
            else if (EventSync.RecordScope != root)
                yield return "L57 reject-scope: EncounterRecords rides as " + recF.Class + " (one blob at the OWNER's " +
                             "path, no element paths), but EventSync.RecordScope is '" + EventSync.RecordScope +
                             "' — that prefix matches ZERO covered paths and a rejected answer never converges";
        }

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

        /// <summary>L36 — the GeoscapeEvent completion-funnel SET, not one funnel. The class has TWO ways to
        /// grant a <c>GeoFactionReward</c> — <c>CompleteEvent</c> (GeoscapeEvent.cs:86) and
        /// <c>CompleteMarketplaceEvent</c> (:74) — and for one session only the first was covered, so a client
        /// could buy from the marketplace: <c>Wallet.Take</c> out of the replicated wallet plus a local
        /// <c>ChoiceReward.Apply</c>, which the diff can never correct (it compares host-now against
        /// host-before and never mentions a change only the client made). Both arms below are asserted PER
        /// FUNNEL, so the third funnel the game adds is named RED instead of shipping unguarded.
        ///
        /// The funnel set is DISCOVERED, never declared: a public instance method of <c>GeoscapeEvent</c> that
        /// takes a <c>GeoEventChoice</c>. That is the semantic signature (you cannot resolve a choice without
        /// receiving it) and it selects exactly the two above out of the class's five methods — a name
        /// heuristic ("Complete*") or a hard-coded pair would both stay green through a rename.
        ///
        /// Arm (a) ARBITER: our own prefix on the funnel itself. Arm (b) CAPTURE: a prefix of ours on a
        /// PRESENTATION method from which the funnel is reachable through presentation-only calls. Arm (b)
        /// exists because the arbiter is structurally too late — the gesture charges the shared wallet BEFORE
        /// the funnel (UIModuleSiteEncounters.cs:571-573, UIModuleTheMarketplace.cs:215) — and it is proven by
        /// IL, not by a table naming which seam belongs to which funnel (law: a comment is not evidence).
        /// Presentation is the game's own namespace split, the same discriminator <see cref="PatchesPresentationOnly"/>
        /// and L21 use.
        ///
        /// LIMITATION (upgrade path, not a waiver): arm (b) proves that a guarded path EXISTS, not that every
        /// path is guarded — a second, unpatched UI route into the same funnel is invisible here. Catching that
        /// wants the REVERSE closure (every presentation caller of a funnel must have one of our prefixes above
        /// it), which means an IL walk of every presentation type in Assembly-CSharp rather than the handful of
        /// methods we patch. Arm (a) is the backstop that makes the gap survivable: whatever path is taken, the
        /// funnel itself still refuses on a client.</summary>
        // ponytail: forward closure from our seams; reverse closure over all presentation types if a second
        // unguarded route into a funnel ever actually appears.
        private static IEnumerable<string> FunnelCoverageLaw(Assembly game)
        {
            var funnels = typeof(GeoscapeEvent).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                               .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(GeoEventChoice)))
                                               .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
            if (funnels.Count == 0)
            {
                yield return "L36 funnels-undiscovered: no public GeoscapeEvent method takes a GeoEventChoice — the " +
                             "discovery rule no longer matches the game (renamed or re-signatured funnel), so this law " +
                             "is asleep and every funnel is unchecked";
                yield break;
            }

            var asm = typeof(IntentRail).Assembly;
            Type[] declared;
            try { declared = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { declared = ex.Types.Where(t => t != null).ToArray(); }

            var arbitrated = new HashSet<int>();   // funnel tokens carrying a prefix of ours ON the funnel
            var seams = new List<MethodBase>();    // our prefixed PRESENTATION targets = the block-first capture set
            foreach (var t in declared)
            {
                var attrs = t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false).Cast<HarmonyLib.HarmonyPatch>().ToList();
                if (attrs.Count == 0 || t.GetMethod("Prefix", AllMembers) == null) continue;
                foreach (var a in attrs)
                {
                    var info = a.info;
                    if (info?.declaringType == null || string.IsNullOrEmpty(info.methodName)) continue;
                    MethodBase target = null;
                    try { target = HarmonyLib.AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes); }
                    catch { }
                    if (target == null) continue;   // getter/setter targets and unresolvable signatures: L23's beat
                    if (info.declaringType == typeof(GeoscapeEvent)) arbitrated.Add(target.MetadataToken);
                    else if (IsPresentation(info.declaringType)) seams.Add(target);
                }
            }

            // ONE forward closure from every capture seam at once, expanding through presentation methods only
            // (a click handler reaches the model funnel in 1-2 UI hops: OnChoiceSelected → SelectChoice →
            // CompleteEvent). Direct call/callvirt edges only — a delegate LOAD references a method it never
            // runs, which would invent coverage.
            var captured = new HashSet<int>();
            var seen = new HashSet<int>();
            var queue = new Queue<MethodBase>();
            foreach (var s in seams) if (seen.Add(s.MetadataToken)) queue.Enqueue(s);
            while (queue.Count > 0)
                foreach (var callee in Callees(queue.Dequeue(), game, directCallsOnly: true))
                {
                    if (callee.DeclaringType == typeof(GeoscapeEvent)) captured.Add(callee.MetadataToken);
                    else if (callee.DeclaringType != null && IsPresentation(callee.DeclaringType) &&
                             seen.Add(callee.MetadataToken)) queue.Enqueue(callee);
                }

            foreach (var f in funnels)
            {
                if (!arbitrated.Contains(f.MetadataToken))
                    yield return "L36 funnel-unarbitrated: GeoscapeEvent." + f.Name + " carries no Harmony PREFIX of " +
                                 "ours — on a client it resolves locally and grants the entire reward off the rail, and " +
                                 "on any peer it re-grants over an already-resolved record (IsCompleted is per-INSTANCE, " +
                                 "GeoscapeEvent.cs:36)";
                if (!captured.Contains(f.MetadataToken))
                    yield return "L36 funnel-uncaptured: no block-first presentation seam of ours reaches GeoscapeEvent." +
                                 f.Name + " — the gesture that resolves it charges the SHARED wallet before the funnel " +
                                 "(UIModuleSiteEncounters.cs:571-573, UIModuleTheMarketplace.cs:215), so the arbiter " +
                                 "alone cannot stop a client from spending";
            }
        }

        /// <summary>L37 — the UNGUARDED-DEREF class: a window we SYNTHESIZE renders through a native helper
        /// that dereferences our context with no null check, so the throw lands inside the game's own state
        /// machine and leaves a HALF-BUILT window whose unwritten Text widgets still show the placeholder
        /// text the designers BAKED into the scene and the prefab. It looks rendered, and no log line says
        /// otherwise — the dominant silent-swallow shape wearing a disguise.
        ///
        /// Measured instance: the token table is five bare <c>context.Site…</c> / <c>context.Vehicle…</c>
        /// lambdas (decompile GeoscapeEventContext.cs:20-40) while <see cref="EventPopup"/>'s raise
        /// legitimately passes a NULL Site (EventPopup.cs:472 — a historical record's site no longer carries
        /// the encounter). A <c>[HavenName]</c> description therefore NRE'd inside <c>ReplaceEventTokens</c>
        /// (:224-239): UIModuleSiteEncounters:217 had already set the title, but :308's description write and
        /// :321's <c>SetChoices</c> never ran, so a correctly localized title sat over "Fasdasdsadasg…"
        /// (baked in level6) and four "Really long choice description btw…" buttons (baked in
        /// UIMainButton_HPriority_Encounters) — while our own line logged <c>raised … mode=outcome</c> as a
        /// success.
        ///
        /// Asserted: (a) the table still EXISTS and is non-empty — if the game moves or empties it, the
        /// reasoning is stale and this law would otherwise fall asleep GREEN; (b) every replacer still takes
        /// the context, i.e. still has the shape that can be handed a null one; (c)
        /// <c>ReplaceEventTokens(string)</c> still resolves; (d) a Harmony FINALIZER OF OURS covers it,
        /// DISCOVERED from our own assembly the way L36 discovers prefixes — never a table asserting so.
        ///
        /// LIMITATION (upgrade path, not a waiver): (a)+(b) establish that the replacers RECEIVE a nullable
        /// context; they do not prove by IL that each deref is unguarded. That proof is the live stack trace
        /// cited above. Tightening it wants an IL field-read walk of the cctor's lambda bodies.</summary>
        // ponytail: signature + coverage assertion; IL deref proof if a replacer ever grows a real null check.
        private static IEnumerable<string> EventTokenDerefLaw()
        {
            var ctx = typeof(GeoscapeEventContext);
            var tableField = ctx.GetField("_tokenReplacers", AllMembers);
            if (tableField == null)
            {
                yield return "L37 token-table-gone: GeoscapeEventContext._tokenReplacers no longer exists — the " +
                             "deref set this law reasons about moved, so the law is asleep and every synthesized " +
                             "window's token path is unchecked";
                yield break;
            }
            var table = tableField.GetValue(null) as System.Collections.IDictionary;
            if (table == null || table.Count == 0)
            {
                yield return "L37 token-table-empty: GeoscapeEventContext._tokenReplacers read as " +
                             (table == null ? "a non-dictionary" : "0 entries") + " — nothing left to guard means " +
                             "this law can no longer fail, which is exactly how it would go stale unnoticed";
                yield break;
            }
            foreach (System.Collections.DictionaryEntry e in table)
            {
                var ps = (e.Value as Delegate)?.Method.GetParameters();
                if (ps == null || ps.Length != 1 || ps[0].ParameterType != ctx)
                    yield return "L37 token-replacer-reshaped: the replacer for '" + e.Key + "' no longer takes a " +
                                 "single GeoscapeEventContext — it can no longer be reasoned about as 'handed our " +
                                 "possibly-null context', so the guard below may be aimed at the wrong method";
            }

            var target = HarmonyLib.AccessTools.Method(ctx, "ReplaceEventTokens", new[] { typeof(string) });
            if (target == null)
            {
                yield return "L37 token-funnel-gone: GeoscapeEventContext.ReplaceEventTokens(string) does not " +
                             "resolve — the single funnel every replacer routes through was renamed or " +
                             "re-signatured, so our finalizer is bound to nothing";
                yield break;
            }

            var asm = typeof(IntentRail).Assembly;
            Type[] declared;
            try { declared = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { declared = ex.Types.Where(t => t != null).ToArray(); }

            foreach (var t in declared)
            {
                if (t.GetMethod("Finalizer", AllMembers) == null) continue;
                foreach (var a in t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false).Cast<HarmonyLib.HarmonyPatch>())
                {
                    var info = a.info;
                    if (info?.declaringType != ctx || info.methodName != "ReplaceEventTokens") continue;
                    MethodBase bound = null;
                    try { bound = HarmonyLib.AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes); }
                    catch { }
                    if (bound != null && bound.MetadataToken == target.MetadataToken) yield break;   // covered
                }
            }
            yield return "L37 token-deref-unswallowed: GeoscapeEventContext.ReplaceEventTokens carries no Harmony " +
                         "FINALIZER of ours — an unresolvable [Token] in a mirrored window's text throws NRE " +
                         "inside UIStateGeoscapeEvent.EnterState, PAST the raise path's own try/catch, and the " +
                         "half-built window then shows the placeholder text baked into level6 and " +
                         "UIMainButton_HPriority_Encounters while our log still reports the raise as a success";
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
                SiteExplorable = true, CanExploreSites = true, HasCrew = true, AlreadyExploring = false,
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
            if (seams < 3)
                yield return "L32 capture-missing: VehicleSync declares " + seams + " Harmony capture seam(s) — the family " +
                             "needs the route funnel (GeoVehicle.StartTravel), the loadout funnel (ReplaceEquipments) and " +
                             "the exploration funnel (StartExploringCurrentSite); a gesture that reaches no seam produces " +
                             "no log line at all";

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

        /// <summary>Every method in OUR assembly that a Harmony PREFIX of ours is bound to — from the
        /// attribute rows AND from the <c>TargetMethods()</c> iterators, which L23 explicitly cannot read
        /// statically, so they are DRIVEN. One collector, because a law that only knew about one of the two
        /// shapes would call a real gate missing (or a missing one covered).</summary>
        private static HashSet<int> OurPrefixTargets()
        {
            var set = new HashSet<int>();
            foreach (var t in DeclaredTypes(typeof(IntentRail).Assembly))
            {
                if (t.GetMethod("Prefix", AllMembers) == null) continue;
                foreach (var a in t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false).OfType<HarmonyLib.HarmonyPatch>())
                {
                    var info = a.info;
                    if (info?.declaringType == null || string.IsNullOrEmpty(info.methodName)) continue;
                    MethodBase m = null;
                    try { m = HarmonyLib.AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes); }
                    catch { }
                    if (m != null) set.Add(m.MetadataToken);
                }
                // SINGULAR TargetMethod() counts too. Harmony accepts either shape, and a gate written with
                // the singular one was invisible here — a blind spot that reads as "no prefix binds it", i.e.
                // this collector would call a real gate missing.
                var one = t.GetMethod("TargetMethod", AllMembers);
                if (one != null && typeof(MethodBase).IsAssignableFrom(one.ReturnType))
                {
                    MethodBase single = null;
                    try { single = (MethodBase)one.Invoke(null, null); } catch { }
                    if (single != null) set.Add(single.MetadataToken);
                }
                var tm = t.GetMethod("TargetMethods", AllMembers);
                if (tm == null || !typeof(IEnumerable<MethodBase>).IsAssignableFrom(tm.ReturnType)) continue;
                IEnumerable<MethodBase> yielded = null;
                try { yielded = (IEnumerable<MethodBase>)tm.Invoke(null, null); } catch { }
                if (yielded == null) continue;
                foreach (var m in yielded) if (m != null) set.Add(m.MetadataToken);
            }
            return set;
        }

        /// <summary>L43 — NO POSE LEAF OF A RAIL-MIRRORED NAVIGATING ACTOR MAY RIDE COVERED. Geoscape motion
        /// is CLOSED-FORM and reproducible: <c>GeoNavComponent.NavigateRoutine</c> recomputes
        /// <c>Ratio01(startTime, Timing.Now)</c> → <c>Slerp</c> → <c>PivotTransform.localRotation</c> from
        /// scratch every frame (GeoNavComponent.cs:104-116, rescheduled :126) off a def-fixed speed with no
        /// RNG, on a clock the client already tracks. So a mirrored actor's POSE is not state — it is the
        /// OUTPUT of a routine the client can run itself, and shipping it as periodic deltas can only make
        /// the icon STEP at the rail's walk cadence. The rail mirrors the ORDER; the client derives the pose.
        ///
        /// Three arms, because the fix is only correct as a whole — the pose can be dropped ONLY if the
        /// client actually runs the derivation, and the derivation is safe ONLY if the arrival OUTCOME stays
        /// host-side and the re-seed cannot fire per tick:
        ///   1. POSE EXCLUDED. The actor set is DISCOVERED (a rail-classified GeoActor that owns a
        ///      GeoNavComponent), never declared, so a newly-mirrored navigating actor arrives here as a
        ///      violation instead of as a stutter nobody traces.
        ///   2. OUTCOME GATED, DERIVATION NOT. <c>GeoVehicle.OnArrived</c> must carry a prefix of ours (its
        ///      body is all authoritative — <c>CurrentSite=</c>, <c>_destinationSites.RemoveAt(0)</c>,
        ///      <c>VehicleArrived</c>, <c>OnArrivedAtDestination</c>, plus the :335
        ///      "Sites desynchronized?!?" throw) and that prefix must read <c>IsHost</c>, because a gate the
        ///      HOST also obeys would strand every aircraft in the session. Conversely
        ///      <c>GeoNavComponent.Navigate</c> must carry NO prefix of ours: gating it is what froze the
        ///      client's navigation in the first place, so re-adding it turns this arm red.
        ///   3. RE-SEED ON ORDER CHANGE ONLY. <c>NavigateRoutine</c> opens with
        ///      <c>yield return NextUpdate.Seconds(5f)</c> (:89), so a re-seed keyed on anything that moves
        ///      per tick freezes the aircraft for five seconds each time. The order-leaf test must therefore
        ///      accept the order and REJECT the continuously-changing covered leaves, and a route whose
        ///      waypoint was merely consumed must be recognised as the SAME route (a normal
        ///      <c>TravelTo</c> fills DestinationSites with many sites and the host trims one per leg).
        /// Falsify by re-admitting any pose row, by removing the arrival gate or its IsHost arm, by
        /// re-adding a Navigate prefix, or by letting RangeRemaining count as an order leaf.</summary>
        private static IEnumerable<string> DerivedPoseLaw(Assembly game)
        {
            var poseNames = new[] { "SurfacePos", "SurfaceRot", "Rot" };
            var nav = typeof(PhoenixPoint.Geoscape.Entities.GeoNavComponent);
            var actors = DeclaredTypes(game)
                .Where(t => t != null && !t.IsAbstract &&
                            typeof(PhoenixPoint.Geoscape.Entities.GeoActor).IsAssignableFrom(t) &&
                            t.GetMembers(AllMembers).Any(mi =>
                                (mi is FieldInfo fi && fi.FieldType == nav) ||
                                (mi is PropertyInfo pi && pi.PropertyType == nav)))
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();

            // The POSE members live on the actor's save-DTO twin, so the table to read is the BRIDGED one
            // (RailMeta.FindBridge resolves the DTO generically — no hardcoded pair). Reading RailType.Get
            // instead finds no pose members at all and the arm passes vacuously, which is exactly how this
            // law would rot: green because it looked in the wrong place.
            var navigators = new List<KeyValuePair<Type, RailType>>();
            foreach (var t in actors)
            {
                RailType rt = null;
                try
                {
                    var dto = RailMeta.FindBridge(t);
                    rt = dto != null ? RailType.GetBridged(t, dto) : RailType.Get(t);
                }
                catch { }
                if (rt != null && rt.Fields.Count > 0) navigators.Add(new KeyValuePair<Type, RailType>(t, rt));
            }
            if (navigators.Count == 0)
                yield return "L43 navigators-undiscovered: no rail-classified GeoActor owns a GeoNavComponent — " +
                             "the discovery rule no longer matches the game, so arm 1 is asleep and a mirrored " +
                             "aircraft's pose could ride covered without anything noticing";

            int poseSeen = 0;
            foreach (var kv in navigators)
                foreach (var name in poseNames)
                {
                    var f = kv.Value.FieldByName(name);
                    if (f == null) continue; // the game does not carry it on this actor
                    poseSeen++;
                    if (f.Class != FieldClass.Excluded)
                        yield return "L43 pose-covered: " + kv.Key.FullName + "." + name + " rides as " + f.Class +
                                     " — it is the OUTPUT of GeoNavComponent.NavigateRoutine, which the client " +
                                     "recomputes closed-form every frame, so mirroring it makes the client's icon " +
                                     "step at the rail's walk cadence instead of gliding";
                }
            if (navigators.Count > 0 && poseSeen == 0)
                yield return "L43 pose-members-absent: not one of the " + navigators.Count + " navigating actors " +
                             "exposes SurfacePos/SurfaceRot/Rot on its twin table — the members moved or the bridge " +
                             "stopped resolving, so arm 1 is checking nothing and green here means nothing";

            // ── arm 2: the OUTCOME is gated, the DERIVATION is not ──
            var vehicle = typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle);
            var covered = OurPrefixTargets();
            var onArrived = HarmonyLib.AccessTools.Method(vehicle, "OnArrived",
                new[] { typeof(UnityEngine.Vector3), typeof(bool) });
            if (onArrived == null)
                yield return "L43 arrival-unresolved: GeoVehicle.OnArrived(Vector3,bool) does not resolve — the gate's " +
                             "TargetMethod yields NULL, PatchAll throws, and MultiplayerMain swallows the warning, " +
                             "killing every later patch in the same pass (L23's failure mode)";
            else if (!covered.Contains(onArrived.MetadataToken))
                yield return "L43 arrival-ungated: GeoVehicle.OnArrived carries no prefix of ours — a client that flies " +
                             "its own aircraft then runs the ARRIVAL outcome locally (CurrentSite=, " +
                             "_destinationSites.RemoveAt(0), CurrentSite.VehicleArrived, OnArrivedAtDestination) and " +
                             "can hit the :335 \"Sites desynchronized?!?\" throw inside a coroutine";
            else
            {
                var gate = DeclaredTypes(typeof(IntentRail).Assembly)
                    .FirstOrDefault(t => t.GetMethod("Prefix", AllMembers) != null &&
                                         t.GetMethod("TargetMethod", AllMembers) != null &&
                                         t.Name == "VehicleArrivalGate");
                var prefix = gate?.GetMethod("Prefix", AllMembers);
                var isHost = HarmonyLib.AccessTools.PropertyGetter(typeof(Multiplayer.Network.NetworkEngine), "IsHost");
                if (prefix == null)
                    yield return "L43 gate-type-missing: no VehicleArrivalGate with a Prefix binds GeoVehicle.OnArrived — " +
                                 "the arrival outcome's gate cannot be checked for its host arm";
                else if (isHost == null)
                    yield return "L43 ishost-unresolved: NetworkEngine.IsHost does not resolve, so the harness cannot " +
                                 "prove the arrival gate lets the HOST through";
                else if (!Callees(prefix, typeof(IntentRail).Assembly).Any(c => c.MetadataToken == isHost.MetadataToken))
                    yield return "L43 host-gated: VehicleArrivalGate.Prefix never reads NetworkEngine.IsHost — the HOST " +
                                 "would obey its own client gate, so no aircraft in the session ever completes a leg " +
                                 "(no CurrentSite, no DestinationSites trim, and nothing for the rail to mirror)";
            }

            var navigate = HarmonyLib.AccessTools.Method(nav, "Navigate", new[] { typeof(List<UnityEngine.Vector3>) });
            if (navigate != null && covered.Contains(navigate.MetadataToken))
                yield return "L43 derivation-gated: GeoNavComponent.Navigate(List<Vector3>) carries a prefix of ours — " +
                             "gating the DERIVATION is what made the client project the host's pose deltas and step at " +
                             "the walk cadence; with the pose leaves excluded it would strand the aircraft entirely";

            // ── arm 3: the re-seed fires on an order CHANGE, never per tick ──
            var isOrderLeaf = HarmonyLib.AccessTools.Method(typeof(GenericApplier), "IsOrderLeaf");
            var isSuffixOf = HarmonyLib.AccessTools.Method(typeof(GenericApplier), "IsSuffixOf");
            if (isOrderLeaf == null || isSuffixOf == null)
            {
                yield return "L43 reseed-unreachable: GenericApplier.IsOrderLeaf/IsSuffixOf do not resolve — the " +
                             "order-change rule is unverifiable, and a re-seed that fires per tick freezes the " +
                             "aircraft 5s each time (NavigateRoutine's opening NextUpdate.Seconds(5f))";
                yield break;
            }
            foreach (var name in new[] { "DestinationSites", "Travelling", "CurrentSite" })
                if (!(bool)isOrderLeaf.Invoke(null, new object[] { name }))
                    yield return "L43 order-leaf-dropped: '" + name + "' is not counted as an order leaf — the client " +
                                 "never re-derives its route when that value changes, so the aircraft keeps flying the " +
                                 "old order (or never starts)";
            // The per-tick traps: covered leaves of the SAME actor that move continuously in flight.
            foreach (var name in new[] { "RangeRemaining", "SurfacePos", "SurfaceRot", "Rot", "HitPoints" })
                if ((bool)isOrderLeaf.Invoke(null, new object[] { name }))
                    yield return "L43 reseed-per-tick: '" + name + "' counts as an order leaf, but it changes " +
                                 "continuously during a flight — every delta would re-issue Navigate and " +
                                 "NavigateRoutine's opening `yield return NextUpdate.Seconds(5f)` (GeoNavComponent.cs:89) " +
                                 "would freeze the aircraft for 5s on each one";
            object a = new object(), b = new object(), c = new object();
            var cases = new[]
            {
                // last route, current route, is-the-same-route (skip the re-seed)
                new { Last = new[] { a, b, c }, Cur = new[] { b, c }, Same = true,  What = "a consumed waypoint" },
                new { Last = new[] { a, b, c }, Cur = new[] { c },    Same = true,  What = "two consumed waypoints" },
                new { Last = new[] { a, b, c }, Cur = new[] { a, b, c }, Same = true, What = "an unchanged route" },
                new { Last = new[] { a, b, c }, Cur = new[] { b },    Same = false, What = "a redirect to an earlier leg" },
                new { Last = new[] { a, b },    Cur = new[] { c },    Same = false, What = "a redirect to a new site" },
                new { Last = new[] { a },       Cur = new[] { b, c }, Same = false, What = "a longer new route" },
            };
            foreach (var k in cases)
            {
                bool got = false; string threw = null;
                try { got = (bool)isSuffixOf.Invoke(null, new object[] { k.Last, k.Cur }); }
                catch (Exception ex) { threw = ex.Message; }
                if (threw != null) { yield return "L43 suffix-threw: " + k.What + " → " + threw; continue; }
                if (got != k.Same)
                    yield return "L43 reseed-" + (k.Same ? "per-leg" : "route-missed") + ": " + k.What + " is " +
                                 (k.Same
                                     ? "treated as a NEW route, so a normal multi-site TravelTo re-issues Navigate at " +
                                       "every waypoint the host trims and the aircraft stalls 5s per leg"
                                     : "treated as the SAME route, so the client keeps flying the old order and the " +
                                       "host's redirect is never obeyed");
            }
        }

        /// <summary>L53 — SITE-EXPLORATION PROGRESS IS DERIVED, NOT MIRRORED. The exact shape L43 found in
        /// the aircraft pose, one subsystem over: the fill the player watches is CLOSED-FORM.
        /// <c>GeoActorProgressionVisualController.Progression</c> is
        /// <c>(Timing.Now - Start).TotalMinutes / (End - Start).TotalMinutes</c>, recomputed from scratch in
        /// <c>Update()</c> every frame (:25-35, :53-56), and both inputs are already on the client —
        /// <c>Start</c> is the <c>StartExplorationTime</c> leaf and <c>End = Start + ExplorationTime</c>, a
        /// def-fixed <c>TimeUnit.FromHours(def.ExplorationTimeHours)</c> (GeoSite.cs:490) with no RNG. So the
        /// client re-seeds the game's own <c>ExploreCurrentSite</c>:448 (replaying what
        /// <c>ProcessInstanceData</c>:1126 does from a save) and draws the bar itself; before that it saw
        /// NOTHING until the host's counter completed.
        ///
        /// Five arms, because the derivation is only correct as a whole:
        ///   1. STILL CLOSED-FORM. Proven from IL, not from a comment: the getter must re-read
        ///      <c>Timing.Now</c>, and <c>Update</c> → <c>RefreshVisuals</c> → the getter must still be the
        ///      chain. A game update that LATCHES the ratio at <c>SetProgression</c> invalidates the whole
        ///      route, and this is the only arm that would notice.
        ///   2. THE ORDER RIDES, THE HANDLE DOES NOT. <c>StartExplorationTime</c>/<c>CurrentSite</c> covered;
        ///      <c>NextSiteExplorationUpdate</c> excluded BY THE DECLARED opt-out (never merely absent), so a
        ///      real coverage gap cannot hide behind the decision — L14/L41's inversion, same reason.
        ///   3. THE RE-SEED'S HANDLES BIND. The three private members it drives are read off the PRODUCTION
        ///      statics, so a renamed member is red here instead of a silently dead re-seed. Positive control:
        ///      a deliberately wrong <c>ExploreCurrentSite</c> signature must resolve to NULL, or "non-null"
        ///      proves nothing about the resolver.
        ///   4. RE-SEED ON ORDER CHANGE ONLY, the real <c>IsOrderLeaf</c> driven headlessly.
        ///   5. OUTCOME GATED, DERIVATION NOT. <c>GeoFaction.OnVehicleSiteExplored</c> (SetInspected +
        ///      UpdateVehicleSite) must carry a prefix of ours reading <c>IsHost</c>; <c>ExploreCurrentSite</c>
        ///      and <c>EndExploreCurrentSite</c> must carry NONE — gating the derivation is the mistake that
        ///      made the client project instead of run (L43's arm 2, learned the hard way).
        /// Falsify by mirroring the handle, by dropping the start-time leaf or its order-leaf row, by removing
        /// the outcome gate, or by adding a prefix to either half of the derivation.</summary>
        private static IEnumerable<string> DerivedExplorationLaw()
        {
            var vis = typeof(PhoenixPoint.Geoscape.View.GeoActorProgressionVisualController);
            var game = typeof(Base.Core.Timing).Assembly;

            // ── arm 1: the ratio is still recomputed, not latched ──
            var progression = HarmonyLib.AccessTools.PropertyGetter(vis, "Progression");
            var update = HarmonyLib.AccessTools.Method(vis, "Update");
            var refresh = HarmonyLib.AccessTools.Method(vis, "RefreshVisuals");
            var nowGetter = HarmonyLib.AccessTools.PropertyGetter(typeof(Base.Core.Timing), "Now");
            if (progression == null || update == null || refresh == null || nowGetter == null)
                yield return "L53 closed-form-unverifiable: GeoActorProgressionVisualController.Progression/Update/" +
                             "RefreshVisuals or Timing.Now does not resolve — the premise the whole derivation rests " +
                             "on cannot be checked, so nothing below means anything";
            else
            {
                var progCallees = Callees(progression, game).ToList();
                if (progCallees.Count == 0)
                    yield return "L53 il-unreadable: the Progression getter yields no callees — the IL scan is asleep " +
                                 "and every closed-form arm below would pass vacuously";
                else if (!progCallees.Any(c => c.MetadataToken == nowGetter.MetadataToken))
                    yield return "L53 progress-latched: GeoActorProgressionVisualController.Progression no longer reads " +
                                 "Timing.Now — the fill is a STORED value now, not a function of the clock, so a client " +
                                 "re-seeded from (start,end) would paint a frozen bar; mirror the value instead";
                if (!Callees(update, game).Any(c => c.MetadataToken == refresh.MetadataToken))
                    yield return "L53 not-per-frame: GeoActorProgressionVisualController.Update no longer drives " +
                                 "RefreshVisuals — the derived ratio would be computed once at SetProgression and never " +
                                 "again, so the client's bar would not advance";
                if (!Callees(refresh, game).Any(c => c.MetadataToken == progression.MetadataToken))
                    yield return "L53 not-re-derived: RefreshVisuals no longer reads Progression — whatever it writes to " +
                                 "the material is not the clock-derived ratio any more";
            }

            // ── arm 2: the ORDER rides covered, the scheduler HANDLE stays out ──
            var vehicle = typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle);
            var twin = RailType.GetBridged(vehicle, typeof(PhoenixPoint.Geoscape.Entities.GeoVehicleInstanceData));
            foreach (var name in new[] { "StartExplorationTime", "CurrentSite" })
            {
                var f = twin?.FieldByName(name);
                if (f == null || f.Class == FieldClass.Excluded)
                    yield return "L53 order-not-mirrored: GeoVehicle." + name + " does not ride covered (" +
                                 (f == null ? "no such member" : f.Exclude) + ") — it is an INPUT the client's own " +
                                 "exploration timer is seeded from, so without it the bar can never start (or starts " +
                                 "at the wrong time)";
            }
            var handle = twin?.FieldByName("NextSiteExplorationUpdate");
            var handleOptOut = RailMeta.OptOutReason(vehicle, "NextSiteExplorationUpdate");
            if (handleOptOut == null || handle == null || handle.Class != FieldClass.Excluded ||
                handle.Exclude != handleOptOut)
                yield return "L53 handle-not-declared-out: GeoVehicle.NextSiteExplorationUpdate is not excluded by the " +
                             "DECLARED exploration opt-out (" + (handleOptOut == null ? "none declared"
                                 : handle == null ? "no such member" : handle.Class + " / " + handle.Exclude) +
                             ") — either a scheduler handle is being shipped as state, or the row vanished and a real " +
                             "coverage gap is hiding behind the decision";

            // ── arm 3: the re-seed's PRODUCTION handles bind ──
            var applier = typeof(GenericApplier);
            var handleNames = new[]
            {
                "ExplorationStartField", "ExploreCurrentSiteMethod", "EndExploreCurrentSiteMethod",
            };
            foreach (var n in handleNames)
            {
                var slot = applier.GetField(n, AllMembers);
                object bound = null;
                try { bound = slot?.GetValue(null); } catch { }
                if (slot == null || bound == null)
                    yield return "L53 reseed-handle-dead: GenericApplier." + n + " is " +
                                 (slot == null ? "gone" : "NULL") + " — the exploration re-seed cannot drive the " +
                                 "game's own ExploreCurrentSite/EndExploreCurrentSite, so a client shows no progress " +
                                 "at all and nothing in the log says why";
            }
            // Positive control for the three above: AccessTools must DISCRIMINATE, or "non-null" is noise.
            if (HarmonyLib.AccessTools.Method(vehicle, "ExploreCurrentSite",
                                              new[] { typeof(Base.Core.TimeUnit) }) != null)
                yield return "L53 resolver-indiscriminate: AccessTools resolved ExploreCurrentSite with ONE TimeUnit " +
                             "parameter, a signature the game does not declare — so the arm above proving the real " +
                             "two-parameter handle binds proves nothing";

            // ── arm 4: the re-seed fires on an ORDER change, never per tick ──
            var isOrderLeaf = HarmonyLib.AccessTools.Method(applier, "IsOrderLeaf");
            if (isOrderLeaf == null)
                yield return "L53 order-rule-unreachable: GenericApplier.IsOrderLeaf does not resolve — whether the " +
                             "exploration re-seed is edge-driven cannot be checked headlessly";
            else
            {
                if (!(bool)isOrderLeaf.Invoke(null, new object[] { "StartExplorationTime" }))
                    yield return "L53 start-not-an-order: 'StartExplorationTime' is not counted as an order leaf — the " +
                                 "host issuing an exploration reaches the client as a value nobody acts on, so the " +
                                 "client's own timer never starts and the bar appears only when the host finishes";
                foreach (var n in new[] { "RangeRemaining", "HitPoints", "SurfacePos" })
                    if ((bool)isOrderLeaf.Invoke(null, new object[] { n }))
                        yield return "L53 reseed-per-tick: '" + n + "' counts as an order leaf, but it moves " +
                                     "continuously — every delta would re-evaluate the re-seed at tick rate";
            }

            // ── arm 5: the OUTCOME is gated, the DERIVATION is not ──
            var covered = OurPrefixTargets();
            var outcome = HarmonyLib.AccessTools.Method(typeof(PhoenixPoint.Geoscape.Levels.GeoFaction),
                                                        "OnVehicleSiteExplored");
            if (outcome == null)
                yield return "L53 outcome-unresolved: GeoFaction.OnVehicleSiteExplored does not resolve — the gate's " +
                             "HarmonyPatch attribute names a method that no longer exists, PatchAll warns, and " +
                             "MultiplayerMain swallows it, killing every later patch in the same pass (L23)";
            else if (!covered.Contains(outcome.MetadataToken))
                yield return "L53 outcome-ungated: GeoFaction.OnVehicleSiteExplored carries no prefix of ours — a client " +
                             "running its own exploration timer also produces the RESULT (SetInspected + " +
                             "UpdateVehicleSite) on a projector, and the diff is host-now vs host-before, so that write " +
                             "is never corrected";
            else
            {
                var gate = DeclaredTypes(typeof(IntentRail).Assembly)
                    .FirstOrDefault(t => t.Name == "SiteExploredOutcomeGate" && t.GetMethod("Prefix", AllMembers) != null);
                var prefix = gate?.GetMethod("Prefix", AllMembers);
                var isHost = HarmonyLib.AccessTools.PropertyGetter(typeof(Multiplayer.Network.NetworkEngine), "IsHost");
                if (prefix == null)
                    yield return "L53 gate-type-missing: no SiteExploredOutcomeGate with a Prefix binds " +
                                 "GeoFaction.OnVehicleSiteExplored — its host arm cannot be checked";
                else if (isHost == null)
                    yield return "L53 ishost-unresolved: NetworkEngine.IsHost does not resolve, so the harness cannot " +
                                 "prove the exploration-outcome gate lets the HOST through";
                else if (!Callees(prefix, typeof(IntentRail).Assembly).Any(c => c.MetadataToken == isHost.MetadataToken))
                    yield return "L53 host-gated: SiteExploredOutcomeGate.Prefix never reads NetworkEngine.IsHost — the " +
                                 "HOST would obey its own client gate, so NO site in the session is ever marked " +
                                 "inspected and every exploration silently produces nothing";
            }
            foreach (var n in new[] { "ExploreCurrentSite", "EndExploreCurrentSite" })
            {
                var m = HarmonyLib.AccessTools.Method(vehicle, n);
                if (m != null && covered.Contains(m.MetadataToken))
                    yield return "L53 derivation-gated: GeoVehicle." + n + " carries a prefix of ours — that is the " +
                                 "DERIVATION, not the outcome; gating it is exactly what left the client with no " +
                                 "progress to show (L43 arm 2, same mistake one subsystem over)";
            }
        }

        /// <summary>L54 — THE HOST MUST REPAINT ITS OWN APPLIED STATE, AND THE PERSISTENT HUD IS PART OF THE
        /// SCREEN. Two halves of one defect: a client changed the active research, the host mutated its own
        /// graph on the intent — and the host's top-right tracker kept the OLD text until the player walked
        /// into the research screen and back.
        ///
        /// The tracker (<c>UIModuleFactionAgendaTracker</c>) paints current research, current manufacturing,
        /// every aircraft ACTION in progress and every facility being built — all rail state — yet it is NOT a
        /// <c>GeoscapeViewState</c>, so no <c>UiNativeRepaint.Table</c> entry can reach it, and its own
        /// refresh loop is a GAME-CLOCK coroutine (<c>Init</c>:100 <c>Timing.Start</c>) that does not run
        /// while the geoscape is paused. Setting <c>_needsRefresh</c> and waiting therefore repaints "once
        /// somebody unpauses", which is what the second client did and the host never did at all.
        ///
        /// Arms: the host's ONE intent dispatch must mark its own UI dirty; the ONE universal repaint must
        /// reach the persistent HUD; the client's research path must reach the SAME primitive (no
        /// subsystem-private nudge); the module's own rebuild must still hang off <c>_needsRefresh</c>, and
        /// its refresh must still be clock-driven rather than a per-frame <c>Update</c> — the two facts that
        /// make driving it ourselves necessary and sufficient. Falsify by deleting the host mark, by pulling
        /// RefreshPersistentHud out of the repaint, or by re-privatising the nudge into ResearchSync.</summary>
        private static IEnumerable<string> HostSelfRepaintLaw()
        {
            var ours = typeof(IntentRail).Assembly;
            var game = typeof(Base.Core.Timing).Assembly;
            var repaint = typeof(OpenUiRepaint);
            var tracker = typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleFactionAgendaTracker);

            var markDirty = HarmonyLib.AccessTools.Method(repaint, "MarkDirty", Type.EmptyTypes);
            var refreshHud = HarmonyLib.AccessTools.Method(repaint, "RefreshPersistentHud");
            var dispatch = HarmonyLib.AccessTools.Method(typeof(IntentRail), "HandleInbound");
            var universal = HarmonyLib.AccessTools.Method(repaint, "RepaintOpenGeoscapeScreen");
            var researchRepaint = HarmonyLib.AccessTools.Method(typeof(ResearchSync), "RepaintResearchUi");

            if (markDirty == null || refreshHud == null || dispatch == null || universal == null || researchRepaint == null)
            {
                yield return "L54 seams-unreachable: one of OpenUiRepaint.MarkDirty()/RefreshPersistentHud/" +
                             "RepaintOpenGeoscapeScreen, IntentRail.HandleInbound or ResearchSync.RepaintResearchUi " +
                             "does not resolve — the reactivity chain cannot be checked at all";
                yield break;
            }
            // Positive control: if the IL scan reads nothing out of our own assembly, every arm below is vacuous.
            if (Callees(dispatch, ours).Count() == 0)
                yield return "L54 il-unreadable: IntentRail.HandleInbound yields no callees in our own assembly — the " +
                             "scan is asleep and the arms below would all pass green on an empty method";

            // ORDER, not presence: HandleInbound marks dirty TWICE — once in the client reject-nudge branch
            // it returns from early, once in the HOST branch after dispatching. Asking merely "does it call
            // MarkDirty" is green with the host mark deleted (verified: the client's mark alone satisfies it).
            // So anchor on DiffEngine.FlushNow, which only the host branch reaches, and demand a mark AFTER it.
            var flushNow = HarmonyLib.AccessTools.Method(typeof(DiffEngine), "FlushNow");
            var seq = CalleeSequence(dispatch);
            int flushAt = flushNow == null ? -1 : seq.FindIndex(c => c.MetadataToken == flushNow.MetadataToken);
            if (flushNow == null || flushAt < 0)
                yield return "L54 dispatch-anchor-lost: IntentRail.HandleInbound no longer calls DiffEngine.FlushNow — " +
                             "the host branch either stopped shipping the intent's own deltas this frame or was " +
                             "restructured, and the host-repaint arm below has nothing to anchor on";
            else if (seq.FindIndex(flushAt + 1, c => c.MetadataToken == markDirty.MetadataToken) < 0)
                yield return "L54 host-blind: IntentRail.HandleInbound dispatches a client's intent and never marks its " +
                             "OWN open UI dirty afterwards — the host mutates its graph and repaints NOTHING of its " +
                             "own, so its screen keeps the pre-intent text until the player leaves and comes back " +
                             "(the clients repaint from the delta, which is why only the host looks stuck)";
            if (!Callees(universal, ours).Any(c => c.MetadataToken == refreshHud.MetadataToken))
                yield return "L54 hud-unreached: OpenUiRepaint.RepaintOpenGeoscapeScreen never calls " +
                             "RefreshPersistentHud — the universal repaint covers the open VIEW STATE only, and the " +
                             "top-right tracker belongs to no view state, so nothing repaints it on any peer";
            if (!Callees(researchRepaint, ours).Any(c => c.MetadataToken == refreshHud.MetadataToken))
                yield return "L54 nudge-reprivatised: ResearchSync.RepaintResearchUi no longer routes to " +
                             "OpenUiRepaint.RefreshPersistentHud — a subsystem-private tracker nudge is back, so the " +
                             "fix stops applying to manufacturing, aircraft actions and facility builds";

            // The tracker must NOT be reachable as a screen — that is WHY the universal path has to own it.
            if (typeof(GeoscapeViewState).IsAssignableFrom(tracker) || UiNativeRepaint.Table.ContainsKey(tracker))
                yield return "L54 hud-is-a-screen: UIModuleFactionAgendaTracker is now a view state / a " +
                             "UiNativeRepaint.Table key — the persistent-HUD special case is obsolete and the two " +
                             "repaint paths would both drive it";

            // The two facts that make driving UpdateData() ourselves necessary AND sufficient.
            var updateData = HarmonyLib.AccessTools.Method(tracker, "UpdateData", Type.EmptyTypes);
            var initialSetup = HarmonyLib.AccessTools.Method(tracker, "InitialSetup");
            var init = HarmonyLib.AccessTools.Method(tracker, "Init");
            var needsRefresh = HarmonyLib.AccessTools.Field(tracker, "_needsRefresh");
            var timingStart = game.GetType("Base.Core.Timing")?.GetMethods(AllMembers)
                                  .FirstOrDefault(m => m.Name == "Start");
            if (updateData == null || initialSetup == null || init == null || needsRefresh == null)
                yield return "L54 tracker-members-gone: UIModuleFactionAgendaTracker's UpdateData()/InitialSetup/Init/" +
                             "_needsRefresh no longer resolve — RefreshPersistentHud drives nothing and says nothing";
            else
            {
                // Read the PRODUCTION handle, not a fresh lookup: the overload trap is real —
                // UpdateData(UIFactionDataTrackerElement) exists next to UpdateData(), and grabbing it would
                // tick ONE row instead of rebuilding the module.
                var prodUpdate = repaint.GetField("TrackerUpdateData", AllMembers)?.GetValue(null) as MethodBase;
                if (prodUpdate != null && prodUpdate.GetParameters().Length != 0)
                    yield return "L54 overload-grabbed: OpenUiRepaint.TrackerUpdateData is not the no-argument " +
                                 "UpdateData — UpdateData(UIFactionDataTrackerElement) is a per-ROW tick, not the " +
                                 "module rebuild, so the refresh would repaint one element and drop the rest";
                if (!Callees(updateData, game).Any(c => c.MetadataToken == initialSetup.MetadataToken))
                    yield return "L54 flag-does-nothing: UpdateData() no longer calls InitialSetup — setting " +
                                 "_needsRefresh stopped meaning 'rebuild the rows', so the refresh runs and the stale " +
                                 "research/manufacturing/aircraft rows stay exactly as they were";
                if (timingStart == null || !Callees(init, game).Any(c => c.Name == "Start" &&
                                                                        c.DeclaringType == typeof(Base.Core.Timing)))
                    yield return "L54 hud-self-refreshes: UIModuleFactionAgendaTracker.Init no longer starts its poll " +
                                 "on the game Timing — if the module now refreshes off the frame loop it repaints while " +
                                 "paused by itself and this whole seam is dead weight to delete";
                if (HarmonyLib.AccessTools.Method(tracker, "Update") != null)
                    yield return "L54 hud-has-update: UIModuleFactionAgendaTracker declares a MonoBehaviour Update — it " +
                                 "repaints per frame on its own now, so driving UpdateData() from the rail is redundant";
            }

            // The production handles RefreshPersistentHud actually caches.
            foreach (var n in new[] { "TrackerUpdateData", "TrackerNeedsRefresh" })
            {
                var slot = repaint.GetField(n, AllMembers);
                object bound = null;
                try { bound = slot?.GetValue(null); } catch { }
                if (slot == null || bound == null)
                    yield return "L54 hud-handle-dead: OpenUiRepaint." + n + " is " + (slot == null ? "gone" : "NULL") +
                                 " — RefreshPersistentHud returns immediately and the tracker stays stale on every peer";
            }
        }

        /// <summary>L42 — A CLIENT GESTURE THAT MUTATES HOST-AUTHORITATIVE STATE MUST HAVE AN INTENT, OR BE
        /// GATED. A geoscape ability IS the player gesture: <c>GeoAbility.ActivateInternal</c> calls a native
        /// mutator on the actor, and on a rail-mirrored <c>GeoVehicle</c> that write is authoritative — the
        /// diff is host-now vs host-before, so a client-local mutation is NEVER corrected. <c>ClientSimGate</c>
        /// gated only the HOURLY tick, so every one of these ran locally on a client for free: the measured
        /// one is <c>ExploreSiteAbility</c> → <c>StartExploringCurrentSite</c> → a per-vehicle
        /// <c>Timing.Start</c> (GeoVehicle.cs:451) that the client's unfrozen clock runs to completion,
        /// exploring a site with no aircraft of the host's there.
        ///
        /// The set is DISCOVERED, never declared: every <c>ActivateInternal</c> override in the game assembly,
        /// its direct callees, those declared on <c>GeoVehicle</c ,> minus property accessors. So a NEW ability
        /// (or a mod-visible one this batch never saw) arrives here as a violation instead of as a divergence
        /// nobody notices. Coverage is likewise discovered from our own assembly — attribute rows and
        /// <c>TargetMethods()</c> iterators alike — never from a table asserting that a gate exists.
        ///
        /// A gesture may satisfy this EITHER way: an intent (the player's order happens, on the host) or a
        /// gate (the write is refused and the host's own result mirrors). Which one each takes, and why, is
        /// argued at <c>VehicleGestureGate</c>. Falsify by removing the explore capture patch or a
        /// <c>GatedGestures</c> row.</summary>
        private static IEnumerable<string> VehicleGestureLaw(Assembly game)
        {
            var vehicle = typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle);
            var activations = DeclaredTypes(game)
                .Where(t => t != null && !t.IsAbstract &&
                            typeof(PhoenixPoint.Geoscape.Entities.Abilities.GeoAbility).IsAssignableFrom(t))
                .Select(t => t.GetMethod("ActivateInternal", AllMembers | BindingFlags.DeclaredOnly))
                .Where(m => m != null)
                .OrderBy(m => m.DeclaringType.Name, StringComparer.Ordinal).ToList();
            if (activations.Count == 0)
            {
                yield return "L42 abilities-undiscovered: no concrete GeoAbility declares ActivateInternal — the " +
                             "discovery rule no longer matches the game, so this law is asleep and every aircraft " +
                             "gesture is unchecked";
                yield break;
            }

            var gestures = new SortedDictionary<string, MethodBase>(StringComparer.Ordinal);
            foreach (var a in activations)
                foreach (var c in Callees(a, game, directCallsOnly: true))
                    if (c.DeclaringType == vehicle && !c.IsSpecialName && !c.IsConstructor)
                        gestures[c.Name] = c;
            if (gestures.Count == 0)
            {
                yield return "L42 vacuous: not one of the " + activations.Count + " ability activations calls a " +
                             "GeoVehicle method — the callee scan found nothing, so a green result here means nothing";
                yield break;
            }

            var covered = OurPrefixTargets();
            foreach (var kv in gestures)
                if (!covered.Contains(kv.Value.MetadataToken))
                    yield return "L42 gesture-ungated: GeoVehicle." + kv.Key + " is reached by a player ability but " +
                                 "carries no Harmony PREFIX of ours — on a client it runs LOCALLY against rail-covered " +
                                 "aircraft/site state, and the host-now-vs-host-before diff can never correct it; give it " +
                                 "an intent (VehicleSync) or a gate (VehicleGestureGate)";

            // The gate's own rows, driven by name: EndCollectingFromCurrentSite is NOT ability-reachable (it is
            // called from TeleportToSite / StartTravel) but gating only its START half would leave a client
            // subtracting a harvesting force it never added — so the symmetric row must stay bound.
            foreach (var name in VehicleGestureGate.GatedGestures)
            {
                var m = HarmonyLib.AccessTools.Method(vehicle, name);
                if (m == null)
                    yield return "L42 gate-row-unbound: GeoVehicle." + name + " does not resolve — VehicleGestureGate " +
                                 "yields NULL from TargetMethods, PatchAll throws, and MultiplayerMain swallows the " +
                                 "warning, which kills every later patch in the same PatchAll (L23's failure mode)";
                else if (!covered.Contains(m.MetadataToken))
                    yield return "L42 gate-row-dropped: GeoVehicle." + name + " is declared gated but no prefix of ours " +
                                 "binds it — the client writes rail-covered state through it again";
            }
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
            if (op == VehicleSync.OpExploreSite)
            {
                f = legal; f.SiteExplorable = false;
                yield return new KeyValuePair<string, VehicleSync.Facts>("a site with nothing left to explore", f);
                f = legal; f.CanExploreSites = false;
                yield return new KeyValuePair<string, VehicleSync.Facts>("an aircraft that cannot explore", f);
                f = legal; f.HasCrew = false;
                yield return new KeyValuePair<string, VehicleSync.Facts>("an empty aircraft", f);
                f = legal; f.AlreadyExploring = true;
                yield return new KeyValuePair<string, VehicleSync.Facts>("an aircraft already exploring that site", f);
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

        /// <summary>L38 — REPAINT RELEVANCE, both arms of one class: a repaint must fire for exactly the
        /// kinds that can change what the open screen paints.
        ///
        /// Arm 1, OVER-repaint (the fps class): the blanket default in <c>UiEventMap.Fire</c> marked the open
        /// screen dirty for ANY touched kind, so 15 permanently-churning world kinds rebuilt the equip lists
        /// AND the whole perk tree on every rail batch (4-5 fps on UIStateEditSoldier). The fix is the
        /// declaration in <c>UiNativeRepaint.IgnoredKinds</c> plus the kind-carrying
        /// <c>OpenUiRepaint.MarkDirty(Type, GeoLevelController)</c>; this arm asserts that wiring exists and
        /// that no arm of <c>Fire</c> still reaches the parameterless blanket <c>MarkDirty()</c>, which would
        /// silently put that kind back outside the declaration. Plus declaration hygiene: an empty table is
        /// reported ASLEEP, a declared screen must be in <c>UiNativeRepaint.Table</c> (a screen repainted by
        /// the Exit+Enter fallback has un-audited reads, so the exclusion would rest on nothing), and a
        /// declared kind must be one the classifier covers — a typo or a renamed type silences no delta and
        /// is dead weight that reads like protection.
        ///
        /// Arm 2, the DANGEROUS INVERSE (stale screen, the silent-swallow class): a kind that CAN affect the
        /// screen must never be declared irrelevant, or the screen keeps painting pre-delta state with
        /// nothing red — strictly worse than the fps drop. DERIVED, not trusted: walk the screen's OWN native
        /// <c>EnterState</c> through presentation code (the same discriminators L21 uses) and reject any
        /// declared kind whose non-accessor methods that walk reaches. EnterState is the root because it is
        /// the screen's full native build and therefore a SUPERSET of the table entry's reseed path — error
        /// lands in the safe direction: more reads found = more declarations rejected.
        ///
        /// LIMITATIONS, stated: a read reached only through a field-held delegate is invisible (Callees takes
        /// call/callvirt only), and a kind read ONLY through property accessors passes — deliberately, since
        /// what a getter returns is either a derived value or a reference to an object that is its own
        /// replicated kind and marks dirty on its own account. Both are the same approximations L21 already
        /// ships. Neither can be closed statically, and the in-game gate is what catches a stale panel.</summary>
        private static IEnumerable<string> RepaintRelevanceLaw(List<Type> types, Assembly game)
        {
            var declared = UiNativeRepaint.IgnoredKinds;
            if (declared.Count == 0)
            {
                yield return "L38 vacuous: UiNativeRepaint.IgnoredKinds is empty — no screen declines any kind, " +
                             "so every touched kind dirties the open screen again and this law asserts nothing";
                yield break;
            }

            // ── Arm 1: the gate is WIRED, and nothing bypasses it ──
            var ours = typeof(UiEventMap).Assembly;
            var fire = typeof(UiEventMap).GetMethod("Fire", AllMembers);
            var kindMark = typeof(OpenUiRepaint).GetMethod("MarkDirty", AllMembers, null,
                                                           new[] { typeof(Type), typeof(PhoenixPoint.Geoscape.Levels.GeoLevelController) }, null);
            var blanketMark = typeof(OpenUiRepaint).GetMethod("MarkDirty", AllMembers, null, Type.EmptyTypes, null);
            if (fire == null || kindMark == null || blanketMark == null)
                yield return "L38 gate-unresolved: UiEventMap.Fire / OpenUiRepaint.MarkDirty(Type, GeoLevelController) / " +
                             "MarkDirty() did not all resolve — the relevance gate cannot be verified and the " +
                             "declaration below may be applying to nothing";
            else
            {
                // DIRECT callees only: the switch arms are inline in Fire, and a blanket MarkDirty reached
                // through a helper (the structural appliers, intent rejects, post-intent reseeds) is legal —
                // those carry no entity and are meant to repaint unconditionally.
                var direct = Callees(fire, ours, directCallsOnly: true).ToList();
                if (!direct.Any(c => Same(c, kindMark)))
                    yield return "L38 relevance-unwired: UiEventMap.Fire never calls OpenUiRepaint.MarkDirty(Type, " +
                                 "GeoLevelController) — the per-kind relevance declaration is consulted by nobody, so " +
                                 "every churning world kind marks the open screen dirty again";
                if (direct.Any(c => Same(c, blanketMark)))
                    yield return "L38 relevance-bypassed: UiEventMap.Fire calls the parameterless " +
                                 "OpenUiRepaint.MarkDirty() — that arm dirties the open screen for a kind nobody " +
                                 "checked, which is exactly the blanket default this law closes";
            }

            // ── Arm 2: no declared-irrelevant kind is one the screen's own native build READS ──
            var covered = new HashSet<Type>(types);
            foreach (var screen in declared.Keys.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (!UiNativeRepaint.Table.ContainsKey(screen))
                    yield return "L38 screen-unrepainted: " + screen.Name + " declares irrelevant kinds but is not in " +
                                 "UiNativeRepaint.Table — its repaint is the Exit+Enter fallback, whose reads were never " +
                                 "audited, so the exclusion rests on nothing";
                var enter = screen.GetMethod("EnterState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (enter == null || enter.GetMethodBody() == null)
                {
                    yield return "L38 vacuous: " + screen.Name + ".EnterState has no body — the read path this law " +
                                 "derives relevance from resolved to nothing and arm 2 is asleep for that screen";
                    continue;
                }
                var reads = ReadKinds(enter, game);
                foreach (var kind in declared[screen].OrderBy(t => t.FullName, StringComparer.Ordinal))
                {
                    // Addressable = in the classifier closure OR a twin-walk component-dispatch target
                    // (GeoHaven/GeoAlienBase are applied via GetComponent and never enter `types`, yet do
                    // land in the applier's touched set — so closure membership alone would cry wolf).
                    if (!covered.Contains(kind) && !BridgedApplyTargets.Contains(kind))
                        yield return "L38 kind-unaddressable: " + screen.Name + " declares " + kind.Name + " irrelevant, " +
                                     "but the rail neither classifies nor bridges that type — no delta ever carries it, " +
                                     "so the row silences nothing and only reads like protection";
                    if (reads.Contains(kind))
                        yield return "L38 relevant-kind-declared-irrelevant: " + screen.Name + ".EnterState reaches a " +
                                     "non-accessor method of " + kind.Name + ", so that kind CAN change what the screen " +
                                     "paints — declaring it irrelevant makes the screen go STALE on those deltas with " +
                                     "nothing red, which is worse than the repaint storm it saves";
                }
            }
        }

        /// <summary>A screen's READ SET, derived from IL: the non-presentation types whose own non-accessor
        /// methods its native build reaches. Same BFS shape and same discriminators as
        /// <see cref="FirstModelCommand"/> — descend through presentation, stop at the model boundary — but
        /// it collects the OWNERS instead of the first command, and keeps statics (a static helper on the kind
        /// is still that kind's code, and over-collecting only rejects more declarations).
        ///
        /// <paramref name="includeAccessors"/> flips the ONE approximation L38 documents. L38 lets a
        /// getter-only read pass because on a VIEW STATE the getter either returns a derived value or a
        /// reference to an object that is its own replicated kind and marks dirty on its own account —
        /// i.e. something else will repaint. L60 asks the question of a panel that the mark never reaches
        /// AT ALL, so nothing else will; there a derived getter IS the painted content
        /// (<c>vehicle.ExplorationTimeRemaining</c> is literally the tracker row's text), and excluding
        /// accessors would make that law read an empty set and call itself vacuous.</summary>
        private static HashSet<Type> ReadKinds(MethodBase root, Assembly game, bool includeAccessors = false)
        {
            var reads = new HashSet<Type>();
            var seen = new HashSet<int> { root.MetadataToken };
            var queue = new Queue<MethodBase>();
            queue.Enqueue(root);
            while (queue.Count > 0)
                foreach (var callee in Callees(queue.Dequeue(), game, directCallsOnly: true))
                {
                    var owner = callee.DeclaringType;
                    if (owner?.FullName == null || !seen.Add(callee.MetadataToken)) continue;
                    if (IsPresentation(owner)) { queue.Enqueue(callee); continue; }
                    if (!callee.IsConstructor && (includeAccessors || !IsAccessor(callee))) reads.Add(owner);
                }
            return reads;
        }

        /// <summary>L60 — THE PERSISTENT HUD IS NO SCREEN'S TO SILENCE. <c>UiNativeRepaint.IgnoredKinds</c>
        /// is a claim about ONE view state's own reads, and L38 proves it against exactly that state's
        /// <c>EnterState</c>. But the skip it drives lands on <c>OpenUiRepaint.MarkDirty(Type, ...)</c>, and
        /// what a mark eventually runs is <c>RepaintOpenGeoscapeScreen</c>, whose FIRST act is
        /// <c>RefreshPersistentHud</c> — the top-right agenda tracker, which belongs to no view state and
        /// therefore appears in no <c>EnterState</c> L38 ever walks. So a per-screen exclusion was silently
        /// buying its fps back out of a panel it never audited: the tracker's rebuild reads the aircraft
        /// actions (<c>UIModuleFactionAgendaTracker.InitialSetup</c>:162-168 →
        /// <c>VehicleActionsViewService.GetCurrentActionTime</c>) that UIStateEditSoldier declares
        /// irrelevant TO ITSELF. Split the flag: the screen keeps declining, the HUD always hears.
        ///
        /// Three call-shaped arms, because a <c>stfld</c> is invisible to a callee walk — which is why the
        /// production side spends one named method (<c>MarkHudDirty</c>) rather than an inline flag write.
        /// Arm 1: the declined branch must reach that method. Arm 2: <c>FlushIfDirty</c> must reach
        /// <c>RefreshPersistentHud</c> DIRECTLY — reaching it only through
        /// <c>RepaintOpenGeoscapeScreen</c> is the pre-fix world and proves nothing. Arm 3, anti-vacuity:
        /// the HUD's real refresh target (read from the same <c>MethodInfo</c> production resolves, never a
        /// name repeated here) must actually read a kind somebody declares irrelevant — otherwise this
        /// split guards a hole that does not exist and the law should say so out loud.</summary>
        private static IEnumerable<string> PersistentHudLaw(Assembly game)
        {
            var ours = typeof(OpenUiRepaint).Assembly;
            var flush = typeof(OpenUiRepaint).GetMethod("FlushIfDirty", AllMembers);
            var kindMark = typeof(OpenUiRepaint).GetMethod("MarkDirty", AllMembers, null,
                                                           new[] { typeof(Type), typeof(PhoenixPoint.Geoscape.Levels.GeoLevelController) }, null);
            var hudMark = typeof(OpenUiRepaint).GetMethod("MarkHudDirty", AllMembers);
            var refresh = typeof(OpenUiRepaint).GetMethod("RefreshPersistentHud", AllMembers);
            if (flush == null || kindMark == null || hudMark == null || refresh == null)
            {
                yield return "L60 wiring-unresolved: OpenUiRepaint.FlushIfDirty / MarkDirty(Type, GeoLevelController) / " +
                             "MarkHudDirty / RefreshPersistentHud did not all resolve — the split that keeps a " +
                             "per-screen exclusion from silencing the persistent HUD cannot be verified at all";
                yield break;
            }
            if (!Callees(kindMark, ours, directCallsOnly: true).Any(c => Same(c, hudMark)))
                yield return "L60 skip-drops-hud: OpenUiRepaint.MarkDirty(Type, GeoLevelController) does not call " +
                             "MarkHudDirty on the declared-irrelevant branch — a kind a SCREEN declines then also " +
                             "skips the persistent HUD, whose reads that screen's EnterState never covered, so the " +
                             "top-right tracker can drop a live row with nothing red";
            if (!Callees(flush, ours, directCallsOnly: true).Any(c => Same(c, refresh)))
                yield return "L60 hud-refresh-unreachable: OpenUiRepaint.FlushIfDirty never calls RefreshPersistentHud " +
                             "directly — the only remaining path is through RepaintOpenGeoscapeScreen, which runs " +
                             "solely for marks the open screen ACCEPTED, so every declined mark loses the HUD again";

            // The HUD's read set comes from the MethodInfo the mod itself invokes: if the game renames
            // UpdateData, RefreshPersistentHud's own null-guard turns the whole HUD refresh into a silent
            // no-op at runtime (OpenUiRepaint.cs, "if (TrackerUpdateData == null ...) return"), and a
            // static red here is the only warning anyone gets.
            var target = typeof(OpenUiRepaint).GetField("TrackerUpdateData", AllMembers)?.GetValue(null) as MethodBase;
            var flag = typeof(OpenUiRepaint).GetField("TrackerNeedsRefresh", AllMembers)?.GetValue(null);
            if (target == null || flag == null)
            {
                yield return "L60 hud-target-unresolved: UIModuleFactionAgendaTracker.UpdateData()/_needsRefresh no " +
                             "longer resolve — RefreshPersistentHud returns on its own null-guard, so the persistent " +
                             "HUD silently stops repainting on EVERY peer and no log line says so";
                yield break;
            }
            var declared = new HashSet<Type>();
            foreach (var kinds in UiNativeRepaint.IgnoredKinds.Values) declared.UnionWith(kinds);
            var hudReads = ReadKinds(target, game, includeAccessors: true);
            hudReads.IntersectWith(declared);
            if (hudReads.Count == 0)
                yield return "L60 vacuous: the persistent HUD's refresh path reads NO kind that any screen declares " +
                             "irrelevant, so keeping the HUD outside the per-screen exclusion guards nothing and this " +
                             "law asserts nothing — either a declaration shrank or the tracker stopped reading the " +
                             "world layer, and the split should be re-derived rather than trusted";
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

        /// <summary>Every callee a method invokes, IN IL ORDER, with NO assembly filter. <see cref="Callees"/>
        /// keeps to one assembly, which cannot express "our code calls into the game in THIS sequence" — and
        /// sequence is exactly what an ordering law asserts (L52's adopt-baseline-before-rebind arm).</summary>
        private static List<MethodBase> CalleeSequence(MethodBase m)
        {
            var seq = new List<MethodBase>();
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) return seq;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) break;
                    code = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeByValue.TryGetValue(code, out var op)) break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) break;
                if (op.OperandType == OperandType.InlineMethod && (op == OpCodes.Call || op == OpCodes.Callvirt))
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs); } catch { }
                    if (callee != null) seq.Add(callee);
                }
                i += size;
            }
            return seq;
        }

        /// <summary>True when the method's IL actually READS the given static field. This is why
        /// <c>DiffEngine.SliceBudgetMs</c> is a static readonly field and not a const: a const is inlined as
        /// a literal, so "does this loop consult a budget?" would be unanswerable from IL.</summary>
        private static bool ReadsField(MethodBase m, FieldInfo target)
        {
            if (m == null || target == null) return false;
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) return false;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) return false;
                    code = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeByValue.TryGetValue(code, out var op)) return false;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) return false;
                if (op.OperandType == OperandType.InlineField)
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i), typeArgs, methodArgs); } catch { }
                    if (f != null && f.MetadataToken == target.MetadataToken && f.Module == target.Module) return true;
                }
                i += size;
            }
            return false;
        }

        /// <summary>Every method our own assembly declares — the universe the caller-set laws quantify over,
        /// derived from metadata rather than hand-listed, so a new method cannot slip past them.</summary>
        private static List<MethodBase> OurMethods()
        {
            var ours = typeof(DiffEngine).Assembly;
            var all = new List<MethodBase>();
            foreach (var t in ours.GetTypes())
            {
                foreach (var m in t.GetMethods(AllMembers)) if (m.DeclaringType == t) all.Add(m);
                foreach (var c in t.GetConstructors(AllMembers)) all.Add(c);
            }
            return all;
        }

        /// <summary>Names of the methods in our assembly whose IL calls <paramref name="target"/>.</summary>
        private static List<string> CallersOf(MethodBase target, List<MethodBase> universe)
        {
            var hits = new List<string>();
            if (target == null) return hits;
            foreach (var m in universe)
                foreach (var callee in CalleeSequence(m))
                    if (callee.MetadataToken == target.MetadataToken && callee.Module == target.Module)
                    { hits.Add(m.DeclaringType.Name + "." + m.Name); break; }
            hits.Sort(StringComparer.Ordinal);
            return hits;
        }

        private static int IndexOfCall(List<MethodBase> seq, string name, string ownerContains = null)
        {
            for (int i = 0; i < seq.Count; i++)
                if (seq[i].Name == name &&
                    (ownerContains == null || (seq[i].DeclaringType?.Name ?? "").Contains(ownerContains)))
                    return i;
            return -1;
        }

        /// <summary>L50 — THE WALK CANNOT GO MONOLITHIC AGAIN.
        ///
        /// Measured defect (user host log 2026-07-30, 275 ticks): every forced flush ran a single-shot walk
        /// of the WHOLE graph inside one frame — walk p50=40 ms, p90=60 ms, max=95 ms — and the Unity frame
        /// counter on those same lines reads 60 fps across periodic sliced cycles but 10-37 fps across the
        /// windows bounded by forced ticks. The forced path is reached from IntentRail.ShouldRunNative, which
        /// EVERY capture seam calls on every invocation, so an equip/augment screen bought several whole-graph
        /// walks per click. The fix deleted the monolithic walker outright.
        ///
        /// Structural falsification, in three arms, none of which a comment can satisfy:
        ///   A. the root LIST may be enumerated in exactly one place (BeginCycle, which only snapshots it);
        ///   B. a root may be VISITED from exactly one place (RunSlice);
        ///   C. that one place must actually read the per-frame budget.
        /// Re-adding a monolithic walk has to break one of the three: a second Roots() enumeration trips A, a
        /// second VisitRoot caller trips B, and deleting the budget check inside RunSlice trips C.
        /// NON-VACUITY: the universe is every method our assembly declares (OurMethods, from metadata — not a
        /// hand-written list), and each arm demands its expected caller be FOUND. An empty caller set is a
        /// violation, so the law cannot pass by looking at nothing.</summary>
        private static IEnumerable<string> SlicedWalkLaw()
        {
            var universe = OurMethods();
            if (universe.Count == 0)
            {
                yield return "L50 vacuous: no methods found in the mod assembly — the caller-set arms scanned nothing";
                yield break;
            }
            var roots = typeof(IdentityResolver).GetMethod("Roots", AllMembers);
            var visitRoot = typeof(DiffEngine).GetMethod("VisitRoot", AllMembers);
            var runSlice = typeof(DiffEngine).GetMethod("RunSlice", AllMembers);
            var budget = typeof(DiffEngine).GetField("SliceBudgetMs", AllMembers);
            if (roots == null || visitRoot == null || runSlice == null || budget == null)
            {
                yield return "L50 unresolved: IdentityResolver.Roots / DiffEngine.VisitRoot / RunSlice / SliceBudgetMs " +
                             "did not all resolve — the sliced-walk law cannot be evaluated and asserts nothing";
                yield break;
            }

            // Arm A — the MONOLITHIC SHAPE itself is banned: no single method may both enumerate the root
            // list and walk roots. Shape, not identity, because enumerating Roots is legitimate on its own —
            // GenericApplier.ClientCrcTick reads the list to pick ONE root per second (it breaks at a
            // rotation cursor and never calls VisitRoot). What can never be legitimate again is the pair,
            // which is literally `foreach (root in Roots) VisitRoot(root)` = the deleted monolithic tick.
            var rootCallers = CallersOf(roots, universe);
            if (rootCallers.Count == 0 || !rootCallers.Contains("DiffEngine.BeginCycle"))
                yield return "L50 vacuous: IdentityResolver.Roots is enumerated by [" + string.Join(", ", rootCallers) +
                             "] — DiffEngine.BeginCycle is not among them, so the cycle-start snapshot this law " +
                             "assumes does not exist and arm A is asleep";
            foreach (var m in universe)
            {
                var seq = CalleeSequence(m);
                bool enumerates = seq.Any(c => c.MetadataToken == roots.MetadataToken && c.Module == roots.Module);
                bool walks = seq.Any(c => c.MetadataToken == visitRoot.MetadataToken && c.Module == visitRoot.Module);
                if (enumerates && walks)
                    yield return "L50 walk-all-roots: " + m.DeclaringType.Name + "." + m.Name + " both enumerates " +
                                 "IdentityResolver.Roots and calls VisitRoot — that one method can read the WHOLE graph " +
                                 "inside a single frame, which is the monolithic tick returning (measured 34-95 ms " +
                                 "main-thread stall per forced flush, host at ~1/3 the clients' framerate)";
            }

            // Arm B — one root-visiting loop, and it is the budget-gated slice.
            var visitCallers = CallersOf(visitRoot, universe);
            if (visitCallers.Count == 0)
                yield return "L50 vacuous: nothing calls DiffEngine.VisitRoot — arm B found no walk at all";
            else if (visitCallers.Count != 1 || visitCallers[0] != "DiffEngine.RunSlice")
                yield return "L50 unsliced-walk: DiffEngine.VisitRoot is called from [" + string.Join(", ", visitCallers) +
                             "] — RunSlice must be the only caller. Any other caller can visit roots outside the " +
                             "per-frame budget, i.e. a whole-graph walk inside one frame";

            // Arm C — the surviving loop genuinely consults the budget.
            if (!ReadsField(runSlice, budget))
                yield return "L50 budget-bypassed: DiffEngine.RunSlice does not read SliceBudgetMs — the slice loop " +
                             "no longer stops on the per-frame budget, so a cycle runs to completion in one frame " +
                             "and slicing is decoration";
        }

        /// <summary>L51 — A REPAINT MAY NOT TAKE AN ARMED AUGMENT SELECTION, AND MAY NOT REVERT THE MIRROR.
        ///
        /// The defect: UiNativeRepaint's augment entry called the native OnNewCharacter, which (decompile
        /// UIModuleBionics.cs:136-154) opens with RevertUnconfirmedChanges() = SetItems(CharacterOriginalItems)
        /// — stamping the STALE visit baseline over armour the rail just mirrored in, with no later delta to
        /// re-ship it — then re-snapshots ArmourItems into that baseline (baking a live preview in as a real
        /// augment → InitCharacterInfo counts MAX_AUGMENTATIONS → the slot locks with nothing committed) and
        /// finally nulls every section's _selectedMutationSlot, which inverts SelectMutation's toggle and
        /// blinks the confirm button off.
        ///
        /// Three arms:
        ///   A. the selection probe BINDS against the real game assembly (a renamed member is red, not a
        ///      guard that silently answers "nothing is armed" forever);
        ///   B. every caller of OnNewCharacter in our assembly also calls the guard;
        ///   C. ORDER: inside that caller the baseline adoption (AddRange onto the snapshot list) precedes
        ///      the OnNewCharacter call, and the guard precedes both.
        /// NON-VACUITY: each arm must FIND its subject — an unresolved member, an empty caller set or a
        /// missing call site is itself the violation, so nothing here can pass by matching nothing. Negating
        /// the fix trips it: drop the guard call → B and C; swap the two statements → C; rename the bound
        /// field → A.</summary>
        private static IEnumerable<string> AugmentRepaintGuardLaw()
        {
            // Arm A — binds resolve. Checked against the game assembly directly, so this law does not merely
            // re-read whatever the mod happened to bind.
            var bionics = typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleBionics);
            var mutate = typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutate);
            var section = typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutationSection);
            foreach (var (owner, name) in new[] { (bionics, "_augmentSections"), (mutate, "_augmentSections"),
                                                  (section, "_selectedMutationSlot") })
                if (owner.GetField(name, AllMembers) == null)
                    yield return "L51 bind-broken: " + owner.Name + "." + name + " no longer exists — the repaint's " +
                                 "mid-interaction guard cannot see an armed body-part selection and will silently go " +
                                 "back to clobbering it (double-click parity, blinking augment button, locked slot)";
            if (UiNativeRepaint.SectionSelectedSlot == null)
                yield return "L51 guard-blind: UiNativeRepaint.SectionSelectedSlot did not bind — SelectionArmed can " +
                             "only ever answer false, so the guard is a no-op";

            var guard = typeof(UiNativeRepaint).GetMethod("SelectionArmed", AllMembers);
            var repaint = typeof(UiNativeRepaint).GetMethod("RepaintAugmentScreen", AllMembers);
            if (guard == null || repaint == null)
            {
                yield return "L51 unresolved: UiNativeRepaint.SelectionArmed / RepaintAugmentScreen did not resolve — " +
                             "the guard law cannot be evaluated and asserts nothing";
                yield break;
            }

            // Arm B — nobody rebinds an augment module without asking the guard first.
            var universe = OurMethods();
            var onNewCharacter = bionics.GetMethod("OnNewCharacter", AllMembers);
            var rebinders = CallersOf(onNewCharacter, universe);
            if (onNewCharacter == null || rebinders.Count == 0)
                yield return "L51 vacuous: no caller of UIModuleBionics.OnNewCharacter was found in the mod — the " +
                             "destructive rebind this law polices is not where it thinks it is";
            foreach (var r in rebinders)
                if (r != "UiNativeRepaint.RepaintAugmentScreen")
                    yield return "L51 unguarded-rebind: " + r + " calls OnNewCharacter, but the mid-interaction guard " +
                                 "lives only in RepaintAugmentScreen — that path reverts mirrored armour and drops the " +
                                 "user's body-part selection";

            // Arm C — order inside the one legal caller: guard, then adopt the baseline, then rebind.
            var seq = CalleeSequence(repaint);
            int iGuard = IndexOfCall(seq, "SelectionArmed");
            int iAdopt = IndexOfCall(seq, "AddRange");
            int iRebind = IndexOfCall(seq, "OnNewCharacter");
            if (iGuard < 0)
                yield return "L51 guard-missing: RepaintAugmentScreen never calls SelectionArmed — a repaint arriving " +
                             "mid-interaction takes the armed selection, inverting SelectMutation's toggle (the " +
                             "'click it twice to deselect' report) and hiding the confirm button";
            if (iRebind < 0)
                yield return "L51 vacuous: RepaintAugmentScreen does not call OnNewCharacter — arm C found nothing to " +
                             "order and is asleep";
            if (iAdopt < 0)
                yield return "L51 baseline-not-adopted: RepaintAugmentScreen never AddRanges the live armour into the " +
                             "module snapshot — OnNewCharacter's opening RevertUnconfirmedChanges then stamps the stale " +
                             "visit baseline back over state the rail just mirrored in, and no later delta re-ships it";
            if (iGuard >= 0 && iRebind >= 0 && iGuard > iRebind)
                yield return "L51 guard-too-late: RepaintAugmentScreen calls SelectionArmed AFTER OnNewCharacter — the " +
                             "selection is already destroyed by the time the guard is asked";
            if (iAdopt >= 0 && iRebind >= 0 && iAdopt > iRebind)
                yield return "L51 adopt-too-late: RepaintAugmentScreen adopts the live armour AFTER OnNewCharacter — the " +
                             "revert has already run, so the mirrored value is gone and the re-snapshot bakes the preview " +
                             "in as a real augment (AugumentationLimitReached locks the slot with nothing committed)";
        }

        /// <summary>L52 — CLICK PARITY: an armed augment selection must leave its siblings clickable.
        /// Native SelectMutation (UIModuleMutationSection.cs:219) disables every sibling slot while one is
        /// selected, and PhoenixGeneralButton drops clicks on a disabled button — so switching from part A to
        /// part B is swallowed and the player has to click A a second time. The ported v1 fix (quarry cbb9b2c)
        /// is a postfix that restores each slot's enabled state to the game's own CanApplyAugumentation rule.
        /// Falsified structurally: the patch must exist, must target SelectMutation, must be a Postfix, and
        /// must reach both SetEnable and CanApplyAugumentation. NON-VACUITY: every one of those is a
        /// find-or-violate, and the target method is resolved from the GAME assembly.</summary>
        private static IEnumerable<string> AugmentClickParityLaw()
        {
            var patch = typeof(EquipSync).GetNestedType("AugmentSlotSwitchUnlockPatch", AllMembers);
            if (patch == null)
            {
                yield return "L52 patch-missing: EquipSync.AugmentSlotSwitchUnlockPatch is gone — augment body-part " +
                             "switching reverts to vanilla, where a selected slot disables its siblings and the next " +
                             "part cannot be clicked until the first is clicked again";
                yield break;
            }
            var section = typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutationSection);
            var target = section.GetMethod("SelectMutation", AllMembers);
            var canApply = section.GetMethod("CanApplyAugumentation", AllMembers);
            var setEnable = typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutationsSlot)
                            .GetMethod("SetEnable", AllMembers);
            if (target == null || canApply == null || setEnable == null)
                yield return "L52 bind-broken: UIModuleMutationSection.SelectMutation / .CanApplyAugumentation / " +
                             "UIModuleMutationsSlot.SetEnable no longer all exist — the click-parity patch is bound to " +
                             "nothing and silently does not run";
            var attr = patch.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false)
                            .Cast<HarmonyLib.HarmonyPatch>().FirstOrDefault();
            if (attr?.info?.methodName != "SelectMutation" || attr?.info?.declaringType != section)
                yield return "L52 patch-retargeted: AugmentSlotSwitchUnlockPatch no longer targets " +
                             "UIModuleMutationSection.SelectMutation — the sibling re-enable never runs";
            var post = patch.GetMethod("Postfix", AllMembers);
            if (post == null)
            {
                yield return "L52 postfix-missing: AugmentSlotSwitchUnlockPatch has no Postfix — a prefix or nothing at " +
                             "all cannot restore the slot enabled-state native just cleared";
                yield break;
            }
            var seq = CalleeSequence(post);
            if (IndexOfCall(seq, "SetEnable") < 0)
                yield return "L52 no-reenable: the click-parity postfix never calls UIModuleMutationsSlot.SetEnable — the " +
                             "sibling slots stay disabled and part-to-part switching is still swallowed";
            if (IndexOfCall(seq, "Invoke") < 0 && IndexOfCall(seq, "CanApplyAugumentation") < 0)
                yield return "L52 rule-bypassed: the click-parity postfix does not consult CanApplyAugumentation — it is " +
                             "re-enabling slots by its own rule instead of the game's, which would offer augments the " +
                             "faction has not unlocked";
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

        /// <summary>L55 — THE FACTION OBJECTIVES LIST IS DERIVED; IT MUST NOT RIDE. The left-hand geoscape
        /// objectives panel desyncs, and the tempting fix is to mirror the list. That is the wrong fix twice
        /// over.
        ///
        /// (a) It CANNOT ride. <c>GeoFactionObjective</c> is ABSTRACT (GeoFactionObjective.cs:13) and the blob
        /// codec is declared-type-only (<c>polymorphic-codec: no</c>), so every concrete element would ABORT
        /// AT ENCODE — an exclusion by exception, not by classification (boundary-law L-E), i.e. exactly the
        /// silent-swallow class this harness exists to outlaw. Today it is held out LOUDLY instead, by the
        /// husk gate on <c>_level</c>. That gate is the only thing standing between us and the silent abort,
        /// and <c>_level</c> is textbook self-healing (:29 <c>_level ?? (_level = GameUtl.CurrentLevel()…)</c>),
        /// so a future "tidy-up" waiver — the same row shape <c>GeoUnitDescriptor.__level</c> already has —
        /// would look obviously correct and would swap the loud exclusion for the silent one. This arm is the
        /// tripwire on that specific edit.
        ///
        /// (b) It NEED NOT ride. Every concrete <c>IsCompleted()</c> RECOMPUTES from live state that already
        /// mirrors — BuildFacility reads <c>_faction.Bases…Layout.Facilities</c> state (:41), Diplomatic reads
        /// <c>DiplomacyState.PointOfInterest</c> (:50), FindPhoenixBase reads <c>_base.GetVisited</c> (:38),
        /// ActiveSites/DiscoverSites read their site sets (:40 / :28). Mirror the roots, and the panel renders
        /// itself. Falsify by waiving the husk, or by letting the abstract element type through.</summary>
        private static IEnumerable<string> DerivedObjectivesLaw(Assembly game)
        {
            var objective = game.GetType("PhoenixPoint.Geoscape.Levels.Objectives.GeoFactionObjective");
            var factions = new[]
            {
                "PhoenixPoint.Geoscape.Levels.GeoFaction",
                "PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction",
                "PhoenixPoint.Geoscape.Levels.Factions.GeoAlienFaction",
            }.Select(n => game.GetType(n)).ToList();

            if (objective == null || factions.Any(f => f == null))
            {
                yield return "L55 types-unreachable: GeoFactionObjective or one of the three GeoFaction types does not " +
                             "resolve — the objectives arms cannot be checked at all";
                yield break;
            }

            // Positive control #1 — the premise of arm (a). If the codec ever becomes polymorphic, the
            // "it aborts at encode" reasoning dies and this whole law must be re-argued rather than kept green.
            if (!objective.IsAbstract)
                yield return "L55 no-longer-abstract: GeoFactionObjective is not abstract any more — the encode-abort " +
                             "argument behind the husk gate is void; re-derive whether the list may ride";

            // Positive control #2 — the husk gate must actually still SEE a husk here. If _level stopped being
            // a husk member (waived, or renamed), the exclusion below could be coming from somewhere else and
            // every arm would be passing for the wrong reason.
            var husk = RailMeta.HuskMembers(objective);
            if (!husk.Any(h => h.StartsWith("_level:", StringComparison.Ordinal)))
                yield return "L55 husk-gate-disarmed: GeoFactionObjective._level is no longer an unwaived husk member " +
                             "(husk = [" + string.Join(",", husk) + "]) — the loud exclusion that keeps the abstract " +
                             "element out of the encoder has been removed; the next step is a SILENT encode abort";

            // The real arm: on every faction table, Objectives must be classified Excluded. Not "absent" —
            // present and refused, so the refusal is visible in the baseline.
            int seen = 0;
            foreach (var f in factions)
            {
                var rt = RailType.Get(f);
                var fld = rt?.FieldByName("Objectives");
                if (fld == null) continue;      // counted by the vacuity guard below, not silently tolerated
                seen++;
                if (fld.Class != FieldClass.Excluded)
                    yield return "L55 objectives-now-ride: " + f.Name + ".Objectives is classified " + fld.Class +
                                 ", not Excluded — an abstract element type is being handed to a declared-type-only " +
                                 "codec (silent encode abort), and the panel is derived anyway (IsCompleted() " +
                                 "recomputes from mirrored roots)";
            }
            // Positive control #3 — non-vacuity. Zero rows examined = three green arms proving nothing.
            if (seen != factions.Count)
                yield return "L55 vacuous: only " + seen + "/" + factions.Count + " faction tables expose an " +
                             "'Objectives' member — the arms above examined nothing on the rest";
        }

        /// <summary>L56 — THE HAVEN / BASE / ALIEN-BASE STATUS TWINS MUST STAY RESOLVED. Capture status, haven
        /// alert, base assault protection, scanner reach and both mist radii are the state the derived
        /// objectives panel (L55) and the globe read back. All of them live on DTO members whose live carrier
        /// the name conventions cannot reach — the DTO name is not the storage name, or the carrier sits one
        /// or TWO hops down a component (<c>MistRepeller.Range.Range</c>, <c>SiteScanner.Range.Range</c>, the
        /// mapping the game itself performs at GeoHaven.cs:1518/1369 and GeoPhoenixBase.cs:1108/969).
        ///
        /// Each row below therefore rides ONLY because of an explicit alias, and an alias that stops resolving
        /// fails SILENTLY: <c>ResolveLive</c> breaks out to "dto-twin unresolved" and the member simply stops
        /// mirroring — no exception, no log. That is the failure this law converts into a red build. Falsify
        /// by deleting any alias row, by shortening the hop chain back to one hop, or by pointing an alias at
        /// a read-only view instead of the writable member behind it.</summary>
        private static IEnumerable<string> SiteStatusTwinLaw()
        {
            // (live type, DTO type, DTO member, expected live leaf, expected hop chain)
            var rows = new (Type Live, Type Dto, string Member, string Leaf, string[] Hops)[]
            {
                (typeof(PhoenixPoint.Geoscape.Entities.GeoHaven), typeof(PhoenixPoint.Geoscape.Entities.GeoHaven.InstanceData),
                    "MistRepellerRange", "Range", new[] { "MistRepeller", "Range" }),
                (typeof(PhoenixPoint.Geoscape.Entities.GeoHaven), typeof(PhoenixPoint.Geoscape.Entities.GeoHaven.InstanceData),
                    "AlertLevelCooldown", "AlertCooldownDaysLeft", null),
                (typeof(PhoenixPoint.Geoscape.Entities.GeoHaven), typeof(PhoenixPoint.Geoscape.Entities.GeoHaven.InstanceData),
                    "OfferedResources", "StockedResources", null),
                (typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase), typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase.InstanceData),
                    "MistRepellerRange", "Range", new[] { "MistRepeller", "Range" }),
                (typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase), typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase.InstanceData),
                    "SiteScannerRange", "Range", new[] { "SiteScanner", "Range" }),
                (typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase), typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase.InstanceData),
                    "ScannerEnabled", "ScannerEnabled", new[] { "SiteScanner" }),
                (typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase), typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase.InstanceData),
                    "AttackProtectionHours", "BaseAssaultProtectionHours", null),
                (typeof(PhoenixPoint.Geoscape.Entities.GeoAlienBase), typeof(PhoenixPoint.Geoscape.Entities.GeoAlienBase.InstanceData),
                    "BaseExpansion", "Range", new[] { "Range" }),
                (typeof(PhoenixPoint.Geoscape.Entities.GeoAlienBase), typeof(PhoenixPoint.Geoscape.Entities.GeoAlienBase.InstanceData),
                    "HavenAttackCounter", "_havenAttackCounter", null),
            };

            int checkedRows = 0, hopRows = 0;
            foreach (var r in rows)
            {
                var f = RailType.GetBridged(r.Live, r.Dto)?.FieldByName(r.Member);
                if (f == null)
                {
                    yield return "L56 twin-row-gone: " + r.Live.Name + "." + r.Member + " is not in the twin table at all";
                    continue;
                }
                checkedRows++;
                if (f.Class == FieldClass.Excluded)
                {
                    yield return "L56 status-stopped-riding: " + r.Live.Name + "." + r.Member + " is Excluded (" +
                                 f.Exclude + ") — capture/alert/scanner/mist status silently stops mirroring";
                    continue;
                }
                var leaf = ((MemberInfo)f.Fi ?? f.Pi)?.Name;
                if (leaf != r.Leaf)
                    yield return "L56 alias-moved: " + r.Live.Name + "." + r.Member + " resolves onto '" + leaf +
                                 "', expected '" + r.Leaf + "'";
                var hops = f.HopFi?.Select(h => h.Name).ToArray();
                if (r.Hops == null)
                {
                    if (hops != null)
                        yield return "L56 unexpected-hop: " + r.Live.Name + "." + r.Member + " grew a hop chain [" +
                                     string.Join(".", hops) + "] — it should resolve directly";
                }
                else
                {
                    hopRows++;
                    if (hops == null || !hops.SequenceEqual(r.Hops))
                        yield return "L56 hop-chain-broken: " + r.Live.Name + "." + r.Member + " hops [" +
                                     (hops == null ? "none" : string.Join(".", hops)) + "], expected [" +
                                     string.Join(".", r.Hops) + "] — the N-hop resolver has regressed";
                }
                // A resolved-but-unwritable alias is the ActorComponent.Rot trap: it reads fine and the apply
                // is a no-op, so the value looks mirrored on the host and never lands on the client.
                if (!f.IsWritable())
                    yield return "L56 alias-not-writable: " + r.Live.Name + "." + r.Member + " resolves onto a " +
                                 "read-only member — the apply silently does nothing";
            }

            // Positive control: the whole law is vacuous if GetBridged answers nothing (pre-init serializer),
            // and the TWO-hop rows are the only proof the N-hop resolver is exercised at all.
            if (checkedRows != rows.Length)
                yield return "L56 vacuous: only " + checkedRows + "/" + rows.Length + " twin rows were classified — " +
                             "the arms above examined an empty table";
            if (hopRows == 0)
                yield return "L56 no-hop-coverage: not one hop-chain row was checked — a single-hop regression " +
                             "would pass this law green";
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
