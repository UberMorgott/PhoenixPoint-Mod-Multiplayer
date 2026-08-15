using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L557 — NO PEER READS A ROLLUP ITS OWN RAIL SILENTLY STALED.
    ///
    /// THE REPORT (2026-08-15): the geoscape agenda strip's MANUFACTURE row was missing on the client.
    /// The diag proved the row was BUILT there — identical signature to the host's, faction bound,
    /// rebuild=True — and then torn down every tick. <c>UIModuleFactionAgendaTracker.UpdateData</c>
    /// disposes any element whose remaining time is <c>&lt;= TimeUnit.Zero</c> (dispose loop :191-199 over
    /// the per-element test :271-309) and <c>TimeUnit.Invalid</c> IS <c>TimeSpan.MinValue</c>, so an
    /// UNCOMPUTABLE duration reads as "already elapsed". <c>ItemManufacturing.GetTotalTime</c> (:405-417)
    /// returns that <c>Invalid</c> whenever
    /// <c>_faction.ResourceIncome.GetTotalResouce(Production).Value &lt;= 0f</c>. The client's production
    /// income was zero and had always been zero.
    ///
    /// THE DEFECT IS THE RAIL'S, NOT THE ROW'S — AND THE FIRST READING OF IT WAS WRONG, which is worth
    /// recording because the wrong reading is the plausible one. It looked like
    /// <see cref="ClientSimGate"/> skipping the hourly tick. The gate does skip it, and correctly, but the
    /// tick does not refresh this rollup: it only CONSUMES it
    /// (<c>ResourceIncome.Apply(Wallet)</c>, GeoLevelController.cs:795). The refresh is
    /// <c>GeoFaction.UpdateProduction</c> (GeoFaction.cs:694-701), and it is driven by an EVENT —
    /// <c>site.ProductionChanged</c>, subscribed at :676, handler <c>OnSiteProductionChanged</c> at
    /// :1774-1777. So the general statement is one rung above sim gating and is a property of the rail:
    ///
    ///     THE RAIL WRITES MODEL STATE AS FIELDS, SO A NATIVE CHANGE EVENT NEVER FIRES ON A PEER. Every
    ///     stored aggregate whose only refresh is such an event is frozen at its constructed value, and
    ///     every duration, capacity, ETA or percentage derived from it degrades silently.
    ///
    /// The same shape was already WRITTEN DOWN in this repo one surface over and fixed only halfway:
    /// <c>src/Rail/OpenUiRepaint.cs</c>:624-631 records that the top-right strip's percentages come from a
    /// stored field "which the rail writes DIRECTLY, so none of the module's native subscriptions ever
    /// fire on a client". There the VALUE was right and only the paint was missing, so driving the paint
    /// was the whole fix. Here the value itself is wrong, and painting a stale zero faster is not a fix.
    ///
    /// WHY THIS IS A LAW AND NOT A PATCH. Fixing <c>GetTotalTime</c>, or special-casing production income,
    /// leaves every sibling rollup broken and unfindable: the enumeration behind this law found the same
    /// construction on base stats, faction capacities and haven zone stats, each with its own UI readers.
    /// So the harness DERIVES the class instead of listing it, from the rail's own declaration of what it
    /// writes (<c>IdentityResolver.RootKinds</c>) crossed with the shipped game assembly's own event
    /// wiring. A handler another mod or a DLC adds tomorrow is classified the day it ships.
    ///
    /// ARMS:
    ///   (a) unclassified-refresh-handler — THE TOTALITY ARM, EXECUTED over the shipped Assembly-CSharp:
    ///       every model-change handler a rail-written type registers on its own events must be
    ///       classified in <c>DerivedAggregateRefresh.Table</c> as <c>Recompute</c> (a pure rollup the
    ///       peer re-runs) or <c>Carried</c> (host-minted; it arrives as rail state). Nothing may be
    ///       undeclared. This is the arm that would have caught the reported defect on the day
    ///       <c>GeoFaction</c> went on the rail.
    ///   (b) recompute-is-the-games-own — a <c>Recompute</c> row must name a real method ON A GAME TYPE
    ///       that THE CALLER SUPPLIES NO INFORMATION TO (every parameter carries a default), never a mod
    ///       re-implementation. Two halves, one argument: a private second copy of a rollup drifts from
    ///       the game's definition, and a required argument is a value this peer would have to INVENT —
    ///       an invented input is the divergence re-running the rollup locally exists to avoid. A
    ///       defaulted parameter is the GAME'S own answer, so <c>UpdateStats(bool x = false)</c> passes
    ///       while anything with a required argument does not.
    ///   (c) recompute-declares-its-inputs — a <c>Recompute</c> row must name at least one rail root key
    ///       that actually exists in <c>IdentityResolver.RootKinds</c>. "Recompute on everything" is not a
    ///       classification, and a key no root mints is a row that never arms.
    ///   (d) the-derivation-is-a-derivation — the derived set must be taken from
    ///       <c>IdentityResolver.RootKinds</c> and be NON-EMPTY and NON-CONSTANT. A walk that finds
    ///       nothing would make arm (a) vacuously green forever, which is the exact failure mode a
    ///       coverage law is written to avoid.
    ///   (e) an-unclassified-handler-is-loud — <c>DerivedAggregateRefresh.Unclassified</c> must exist and
    ///       reach the logger. The harness catches the build-time case; this catches the one that only
    ///       exists at runtime.
    ///   (f) the-recompute-repaints — P11/REACTIVITY: <c>ClientTick</c> must reach
    ///       <c>OpenUiRepaint</c>, or a corrected value would sit in the model behind an already-open
    ///       strip until the player left the screen and came back, which is the defect this file forbids
    ///       wearing a different hat.
    ///   (g) nothing-waits-on-a-peer — NO QUORUM: the refresh may not consult a roster, a readiness set
    ///       or a peer list. It reads this peer's own role and its own touched paths.
    ///   (h) a-verdict-names-its-witness — every row must cite the <c>file:line</c> its verdict was read
    ///       from. Whether a handler mints information is a judgement no IL walk decides, and an arm that
    ///       pretended to would hand out false accusations (the first draft of this one did, against two
    ///       verdicts that are correct). What is checkable is that the judgement was made against
    ///       SOMETHING. It is also the table's one guard against a silent off switch: flipping a row to
    ///       <c>Carried</c> is compile-valid and leaves (a)-(c) green, but it cannot keep a citation that
    ///       says the opposite, so the edit has to be argued in the diff.
    ///
    /// ROLES SEPARATED (§C.3): every arm is a statement about the shipped assemblies or a pure call.
    ///
    /// Falsify (compile-valid src mutations, each named): drop the <c>UpdateProduction</c> row from
    /// <c>Table</c> → (a); point that row's <c>Owner</c> at a mod type → (b); empty its
    /// <c>InputPrefixes</c> or write a key no root mints → (c); delete the <c>MpLog</c> call from
    /// <c>Unclassified</c> → (e); delete the <c>OpenUiRepaint</c> call from <c>ClientTick</c> → (f).
    /// </summary>
    internal static class L557_NoPeerReadsARollupItsOwnRailStaled
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>Depth of the write-reachability probe below. Two hops is what separates a handler that
        /// REFRESHES something (<c>OnSiteProductionChanged</c> → <c>UpdateProduction</c> →
        /// <c>SetOutput</c>) from one that merely forwards a notification. Bounded on purpose: the
        /// geoscape model reaches most of the assembly within four hops, so an unbounded walk would
        /// classify every handler as a writer and say nothing.</summary>
        private const int WriteProbeDepth = 2;

        internal static IEnumerable<string> Check()
        {
            var rootKindsField = typeof(IdentityResolver).GetField("RootKinds", Any);
            if (rootKindsField == null)
            {
                yield return "L557 the-derivation-is-a-derivation: IdentityResolver.RootKinds did not " +
                             "resolve. The class of stale rollups is DERIVED from the rail's own " +
                             "declaration of what it writes as fields — without that declaration this law " +
                             "would degrade into a hand-list, which is the thing it exists to replace.";
                yield break;
            }

            var rootTypes = RootTypes(rootKindsField);
            if (rootTypes.Count == 0)
            {
                yield return "L557 the-derivation-is-a-derivation: no rail root type resolved out of " +
                             "IdentityResolver.RootKinds into the game assembly.";
                yield break;
            }

            var handlers = DerivedHandlers(rootTypes);

            // ── (d) NON-VACUOUS ──────────────────────────────────────────────
            if (handlers.Count == 0)
                yield return "L557 the-derivation-is-a-derivation: the walk over " + rootTypes.Count +
                             " rail-written type(s) found ZERO model-change handlers. Arm (a) would then " +
                             "be vacuously green forever while every rollup on the rail staled silently. " +
                             "Either the IL probe broke or the roots stopped resolving — both are red.";

            var classify = typeof(DerivedAggregateRefresh).GetMethod("Classify", Any);
            var table = typeof(DerivedAggregateRefresh).GetField("Table", Any);
            if (classify == null || table == null)
            {
                yield return "L557 unclassified-refresh-handler: DerivedAggregateRefresh.Classify/Table " +
                             "did not resolve — nothing classifies the derived set at all.";
                yield break;
            }

            // ── (a) TOTALITY ─────────────────────────────────────────────────
            var unclassified = new List<string>();
            foreach (var h in handlers)
            {
                var row = classify.Invoke(null, new object[] { h.DeclaringType, h.Name });
                if (row == null) unclassified.Add(h.DeclaringType.Name + "." + h.Name);
            }
            if (unclassified.Count > 0)
            {
                unclassified.Sort(StringComparer.Ordinal);
                yield return "L557 unclassified-refresh-handler: " + unclassified.Count + " of " +
                             handlers.Count + " model-change handler(s) registered by rail-written types " +
                             "are not classified in DerivedAggregateRefresh.Table. The rail writes those " +
                             "types' state as FIELDS, so none of these ever fires on a peer; each one that " +
                             "rebuilds a stored aggregate leaves every value derived from it stale there. " +
                             "Classify each as Recompute (a pure rollup over mirrored inputs, re-run " +
                             "locally) or Carried (host-minted, arrives as rail state): " +
                             string.Join(", ", unclassified.ToArray());
            }

            // ── (b)(c) EACH ROW IS A REAL, INPUT-DECLARING RECOMPUTE ─────────
            foreach (var problem in RowProblems(table)) yield return problem;

            // ── POSITIVE CONTROL ─────────────────────────────────────────────
            // Arm (a) reports what Classify REJECTS, so a Classify that accepted everything would print a
            // clean green over a table covering nothing at all — the exact false verdict this file's
            // subject is made of. Ask it about a handler nobody declared, and ask the arming decision
            // about a batch that touched nothing the row reads. Both must say no.
            if (DerivedAggregateRefresh.Classify(typeof(GeoFaction), "OnRailCheckHandlerNobodyDeclared") != null)
                yield return "L557 positive-control: Classify accepted a handler name that appears in no " +
                             "row, so the totality arm above can never fail and its GREEN means nothing.";
            if (DerivedAggregateRefresh.Classify(null, null) != null)
                yield return "L557 positive-control: Classify accepted a null handler.";
            var probe = new DerivedAggregateRefresh.Row(
                typeof(GeoFaction), "RailCheckProbe", DerivedAggregateRefresh.Kind.Recompute,
                "UpdateProduction", new[] { "S#" }, "railcheck positive control");
            if (DerivedAggregateRefresh.Arms(probe, new[] { "U#a.Progression", "T.Now" }))
                yield return "L557 positive-control: a batch of soldier and clock paths armed a row that " +
                             "declared only sites, so arming is unconditional and the input declaration " +
                             "required by arm (c) is decorative.";
            if (!DerivedAggregateRefresh.Arms(probe, new[] { "S#4.SiteProduction" }))
                yield return "L557 positive-control: a batch that touched a site did NOT arm the " +
                             "site-declaring row — the engine would never rebuild anything.";

            // ── (e) LOUD ─────────────────────────────────────────────────────
            var loud = typeof(DerivedAggregateRefresh).GetMethod("Unclassified", Any);
            if (loud == null)
                yield return "L557 an-unclassified-handler-is-loud: DerivedAggregateRefresh.Unclassified " +
                             "does not exist. A handler the engine cannot classify at RUNTIME — another " +
                             "mod's, a DLC's — must be announced, never swallowed (law 1).";
            else if (!ReachesLogger(loud))
                yield return "L557 an-unclassified-handler-is-loud: Unclassified reaches no logger, so an " +
                             "unclassifiable refresh handler is a silent swallow.";

            // ── (f)(g) THE PEER-SIDE ENTRY ───────────────────────────────────
            var tick = typeof(DerivedAggregateRefresh).GetMethod("ClientTick", Any);
            if (tick == null)
            {
                yield return "L557 the-recompute-repaints: DerivedAggregateRefresh.ClientTick does not " +
                             "exist, so no classified Recompute row is ever re-run on the peer that needs " +
                             "it and the whole table is documentation.";
            }
            else
            {
                var reach = Il.Closure(tick, 2, typeof(DerivedAggregateRefresh).Assembly);
                if (!reach.Any(m => m.DeclaringType == typeof(OpenUiRepaint)))
                    yield return "L557 the-recompute-repaints: ClientTick never reaches OpenUiRepaint. A " +
                                 "corrected rollup that does not repaint leaves the player looking at the " +
                                 "stale number until they leave the screen and come back — REACTIVITY is " +
                                 "postulate 1, and a value fix without it is half a fix.";
                var quorum = reach.FirstOrDefault(m =>
                    m.Name.IndexOf("Roster", StringComparison.Ordinal) >= 0 ||
                    m.Name.IndexOf("Ready", StringComparison.Ordinal) >= 0);
                if (quorum != null)
                    yield return "L557 nothing-waits-on-a-peer: ClientTick reaches " +
                                 quorum.DeclaringType.Name + "." + quorum.Name + ". A local recompute over " +
                                 "state this peer already holds must never consult a roster or a readiness " +
                                 "set — that is a quorum, and one player's numbers may never depend on " +
                                 "another player acting.";
            }
        }

        /// <summary>The rail's declared roots, plus every game type assignable to one: a handler that
        /// stales a subclass's rollup (<c>GeoPhoenixFaction</c> under the <c>GeoFaction</c> root) is the
        /// same defect, and reading only the exact declared types would miss it.</summary>
        private static List<Type> RootTypes(FieldInfo rootKindsField)
        {
            var declared = new List<Type>();
            try
            {
                // RootKinds is a ValueTuple[]: a VALUE-type array, which does NOT convert to
                // IEnumerable<object>, and the element NAMES (Key/Type/Get) are compile-time metadata
                // only — reflection sees Item1/Item2/Item3. Both facts have to be respected or this walk
                // silently reads nothing and arm (d) is the only thing that notices.
                var arr = (Array)rootKindsField.GetValue(null);
                for (int i = 0; i < arr.Length; i++)
                {
                    var entry = arr.GetValue(i);
                    var t = entry?.GetType().GetField("Item2")?.GetValue(entry) as Type;
                    if (t != null) declared.Add(t);
                }
            }
            catch { return declared; }

            var byAssembly = declared.Select(t => t.Assembly).Distinct().ToList();
            var all = new List<Type>();
            foreach (var asm in byAssembly)
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                foreach (var t in types)
                {
                    if (t == null || t.IsInterface) continue;
                    foreach (var root in declared)
                        if (root.IsAssignableFrom(t)) { all.Add(t); break; }
                }
            }
            return all;
        }

        /// <summary>THE DERIVATION. A rail-written type's own methods register handlers on its own model
        /// events (<c>x.E += H</c> = ldftn H, newobj, callvirt add_E). Those handlers are precisely the
        /// refreshes the rail's field writes bypass. Kept to the ones that actually WRITE something within
        /// <see cref="WriteProbeDepth"/> hops: a handler that only forwards a notification stales no
        /// aggregate, and demanding a classification row for it would be ceremony.</summary>
        private static List<MethodBase> DerivedHandlers(List<Type> rootTypes)
        {
            var found = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
            var owned = new HashSet<Type>(rootTypes);
            foreach (var t in rootTypes)
            {
                MethodInfo[] methods;
                try { methods = t.GetMethods(Any); } catch { continue; }
                foreach (var m in methods)
                {
                    if (!Il.Subscribes(m)) continue;
                    foreach (var h in Il.LoadedFunctions(m))
                    {
                        var owner = h.DeclaringType;
                        if (owner == null || !owned.Contains(owner)) continue;   // the type's OWN refresh
                        if (h.GetParameters().Length > 2) continue;              // not an event handler shape
                        if (!WritesWithin(h)) continue;                          // pure notification: stales nothing
                        found[owner.FullName + "." + h.Name] = h;
                    }
                }
            }
            return found.Values.ToList();
        }

        private static bool WritesWithin(MethodBase h)
        {
            foreach (var m in Il.Closure(h, WriteProbeDepth, h.DeclaringType.Assembly))
                if (Il.WrittenFields(m).Any()) return true;
            return false;
        }

        private static IEnumerable<string> RowProblems(FieldInfo table)
        {
            object[] rows;
            try { rows = ((Array)table.GetValue(null)).Cast<object>().ToArray(); }
            catch { yield break; }

            var rootKeys = RootKeys();
            foreach (var row in rows)
            {
                var rt = row.GetType();
                var owner = rt.GetField("Owner", Any)?.GetValue(row) as Type;
                var handler = rt.GetField("Handler", Any)?.GetValue(row) as string;
                var rebuild = rt.GetField("Rebuild", Any)?.GetValue(row) as string;
                var kind = rt.GetField("Class", Any)?.GetValue(row);
                var inputs = rt.GetField("InputPrefixes", Any)?.GetValue(row) as string[];
                string id = (owner == null ? "<null>" : owner.Name) + "." + (handler ?? "<null>");
                // ── (h) EVERY VERDICT NAMES ITS WITNESS ──────────────────────
                // Whether a handler mints information is a judgement read out of the decompile; no IL walk
                // decides it, and an arm that PRETENDED to would hand out false accusations (the first
                // draft of this one did, against two verdicts that are correct). What IS checkable is that
                // the judgement was actually made against something: every row must cite the file:line it
                // was read from. That is what stops the table's one silent off switch — flipping a row to
                // Carried is compile-valid and leaves the coverage arms green, but it cannot keep a
                // citation that says the opposite, so the edit has to be argued in the diff instead of
                // slipping through as a one-word change.
                var reason = rt.GetField("Reason", Any)?.GetValue(row) as string;
                if (reason == null || !System.Text.RegularExpressions.Regex.IsMatch(reason, @"\.cs:\d"))
                    yield return "L557 a-verdict-names-its-witness: " + id + " carries no file:line " +
                                 "citation. Recompute-vs-Carried decides whether this peer RE-RUNS game " +
                                 "code, so a verdict nobody can re-check against the decompile is the one " +
                                 "kind of row this table must not hold — a wrong Recompute diverges the " +
                                 "campaign and a wrong Carried leaves a value silently stale.";

                if (kind == null || kind.ToString() != "Recompute") continue;    // Carried rows declare nothing else

                // (b) it must be the GAME'S own rollup, resolvable and parameterless.
                var rebuildOn = owner == null || rebuild == null ? null : Resolve(owner, rebuild);
                if (owner == null || owner.Assembly == typeof(DerivedAggregateRefresh).Assembly)
                    yield return "L557 recompute-is-the-games-own: " + id + " names a mod type. A rollup " +
                                 "re-implemented in this assembly is a SECOND definition of the game's own " +
                                 "aggregate, and two definitions drift — silently, into exactly the wrong " +
                                 "number this law exists to remove.";
                else if (rebuildOn == null || rebuildOn.GetParameters().Any(p => !p.HasDefaultValue))
                    yield return "L557 recompute-is-the-games-own: " + id + " names rebuild '" +
                                 (rebuild ?? "<null>") + "', which does not resolve on " + owner.Name +
                                 " to a method THE CALLER SUPPLIES NO INFORMATION TO. Every parameter must " +
                                 "carry a default, because a default is the GAME'S own answer while a " +
                                 "required argument is a value this peer would have to invent — and an " +
                                 "invented input is precisely the divergence re-running the rollup locally " +
                                 "is supposed to avoid. A handler's own signature takes the event's " +
                                 "arguments, which is why the handler is never what gets invoked.";

                // (c) it must name inputs, and inputs the rail actually mints.
                if (inputs == null || inputs.Length == 0)
                    yield return "L557 recompute-declares-its-inputs: " + id + " declares no input prefix. " +
                                 "'Recompute on everything' re-rolls every aggregate on every rail batch " +
                                 "and is not a classification — the row must say which mirrored state it " +
                                 "reads, so a batch that touched none of it costs nothing.";
                else
                    foreach (var prefix in inputs)
                        if (prefix == null || !rootKeys.Any(k => prefix.StartsWith(k, StringComparison.Ordinal)))
                            yield return "L557 recompute-declares-its-inputs: " + id + " declares input '" +
                                         (prefix ?? "<null>") + "', which no IdentityResolver root key " +
                                         "mints. A prefix no path can start with is a row that never arms, " +
                                         "i.e. the stale rollup with a table entry in front of it.";
            }
        }

        /// <summary>A rebuild may be declared on a base type and overridden below it (GeoAlienFaction
        /// overrides UpdateProduction), so the lookup walks the chain rather than demanding
        /// DeclaredOnly.</summary>
        private static MethodInfo Resolve(Type owner, string name)
        {
            for (var t = owner; t != null && t != typeof(object); t = t.BaseType)
            {
                var m = t.GetMethod(name, Any);
                if (m != null) return m;
            }
            return null;
        }

        private static List<string> RootKeys()
        {
            var keys = new List<string>();
            try
            {
                var f = typeof(IdentityResolver).GetField("RootKinds", Any);
                var arr = (Array)f.GetValue(null);
                for (int i = 0; i < arr.Length; i++)
                {
                    var entry = arr.GetValue(i);
                    var k = entry?.GetType().GetField("Item1")?.GetValue(entry) as string;
                    if (k != null) keys.Add(k);
                }
            }
            catch { }
            return keys;
        }

        private static bool ReachesLogger(MethodBase m)
        {
            foreach (var callee in Il.CalledMethods(m))
                if (callee.DeclaringType != null &&
                    callee.DeclaringType.Name.IndexOf("MpLog", StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
