using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Base.Serialization.General;
using Multiplayer.Network.Sync;
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
                // NOT a rail root (ARCHITECTURE.md "Named next steps"). Seeded because the closure is
                // DECLARED-type-only while the live walk types every hop by obj.GetType():
                // GeoSite.SerializationData is declared ActorInstanceData but IS a GeoSiteInstaceData at
                // runtime, so the walk really does descend PhoenixBaseData -> Layout -> Facilities and
                // reach this type. Until now its classification -- notably N4's refusal of the readonly
                // `_components` array (GeoPhoenixFacility.cs:48) -- was argued in review but never executed.
                typeof(PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility),
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
            sb.Append("roots: Timing | TimingInstanceData (\"TA\" clock anchor) | GeoFaction | GeoSite | GeoCharacter | GeoVehicle (IdentityResolver.Roots kinds)\n");
            sb.Append("seeded (not roots — types the live walk reaches only through a runtime subtype): GeoPhoenixFacility\n");
            sb.Append("polymorphic-codec: " + (polymorphicCodec ? "yes" : "no") + "\n");
            sb.Append("types: " + types.Count + "\n\n");

            int cov = 0, exc = 0, geoItemDicts = 0;
            var blobbable = new SortedDictionary<string, Type>(StringComparer.Ordinal);
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
                    if (f.Class == FieldClass.GeoItemDict) geoItemDicts++;
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
            foreach (var f in planted)
            {
                object a = f.GetValue(src), b = f.GetValue(back[0]);
                if (Equals(a, b)) continue;
                laws.Add("L6 entitylist-round-trip-value: " + t.FullName + "." + f.Name + " " + (a ?? "null") + " -> " + (b ?? "null"));
                return "MISMATCH:" + f.Name;
            }
            return "ok(" + planted.Count + ")";
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
        }

        private sealed class ListHolder
        {
            public LinkedList<int> Linked = new LinkedList<int>();
            public HashSet<int> Set = new HashSet<int>();
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
