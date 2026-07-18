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

            RailMeta.SerializerOverride = new Serializer(null);
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

        /// <summary>Mirror of RailMeta.ApplyList's strategy ladder on the DECLARED type. Null = the
        /// applier's final `throw new InvalidOperationException("no list apply strategy for ...")`.
        /// A licensed field with no strategy is the 2026-07-18 resync storm by construction: the host
        /// ships it, the client throws on every apply, and a failed apply drives RequestResync.</summary>
        private static string ListStrategy(RailField f)
        {
            var vt = f.ValueType;
            if (!vt.IsArray && typeof(IList).IsAssignableFrom(vt)) return "IList";
            // Mirrors ApplyList's interface-first probe: an explicit ICollection<T>.Add (LinkedList<T>) is
            // invisible to a name probe on the concrete type, so checking the interface is what keeps L1
            // from reporting a strategy the applier does not actually have — or missing one it does.
            if (!vt.IsArray && f.ElemType != null &&
                typeof(ICollection<>).MakeGenericType(f.ElemType).IsAssignableFrom(vt)) return "ICollection<T>";
            if (!vt.IsArray &&
                HarmonyLib.AccessTools.Method(vt, "Clear") != null &&
                HarmonyLib.AccessTools.Method(vt, "Add", new[] { f.ElemType }) != null) return "Clear+Add";
            if (vt.IsArray && f.IsWritable()) return "array-assign";
            return null;
        }

        /// <summary>Reference-typed members of a blob-reconstructed type that the blob does NOT carry.
        /// The codec builds elements with Activator.CreateInstance(nonPublic) and fills only the table's
        /// fields, so each of these lands null on the client while the game's own load path would have
        /// re-Init'd them — the 7ef0a30 `ResearchElement` husk (ResearchDef null -> NOTEXT).</summary>
        private static List<string> HuskMembers(Serializer ser, Type t)
        {
            var carried = new HashSet<string>(StringComparer.Ordinal);
            var rt = RailType.Get(t);
            if (rt != null)
                foreach (var f in rt.Fields)
                    if (f.Class != FieldClass.Excluded || (f.Fi != null && f.Fi.IsInitOnly)) carried.Add(f.Name);
            foreach (var mwa in ser.GetSerializedMembers(t)) if (mwa.MemberInfo != null) carried.Add(mwa.MemberInfo.Name);

            var husk = new List<string>();
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                foreach (var fi in cur.GetFields(F))
                {
                    if (fi.FieldType.IsValueType || fi.FieldType == typeof(string)) continue;
                    if (typeof(Delegate).IsAssignableFrom(fi.FieldType)) continue; // events are never state
                    // An auto-property's backing field is named "<Prop>k__BackingField"; the serializer
                    // discovers the PROPERTY, so match on that name (ResearchElement.ResearchDef — the
                    // 7ef0a30 NOTEXT husk — is exactly this shape and was invisible without it).
                    var name = fi.Name[0] == '<' ? fi.Name.Substring(1, fi.Name.IndexOf('>') - 1) : fi.Name;
                    if (carried.Contains(name)) continue;
                    husk.Add(name + ":" + fi.FieldType.Name);
                }
            husk.Sort(StringComparer.Ordinal);
            return husk;
        }

        // ─── Snapshot (the reviewable artifact) ─────────────────────────────

        private static string Snapshot(List<Type> types, bool polymorphicCodec, List<string> laws)
        {
            var ser = RailMeta.SerializerOverride;
            var sb = new StringBuilder();
            sb.Append("RAIL BASELINE — generated by tools/RailCheck (no timestamp: this file is diffed, not dated)\n");
            sb.Append("roots: Timing | TimingInstanceData (\"TA\" clock anchor) | GeoFaction | GeoSite | GeoCharacter | GeoVehicle (IdentityResolver.Roots kinds)\n");
            sb.Append("polymorphic-codec: " + (polymorphicCodec ? "yes" : "no") + "\n");
            sb.Append("types: " + types.Count + "\n\n");

            int cov = 0, exc = 0;
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
                    var extra = "";
                    if (f.Class == FieldClass.LeafList || f.Class == FieldClass.EntityList || f.Class == FieldClass.EntityCollection)
                    {
                        var strat = ListStrategy(f);
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

                var husk = HuskMembers(ser, t);
                sb.Append("  " + kv.Key + " keyable=" + (RailMeta.TypeKeyable(t) ? "yes" : "no") +
                          " customCreate=" + (HasCustomCreate(ser, t) ? "yes" : "no") +
                          " husk=" + (husk.Count == 0 ? "none" : string.Join(",", husk)) + "\n");
            }

            sb.Append("\nsummary: covered=" + cov + " excluded=" + exc + " blobbable=" + blobbable.Count + "\n");
            return sb.ToString();
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
