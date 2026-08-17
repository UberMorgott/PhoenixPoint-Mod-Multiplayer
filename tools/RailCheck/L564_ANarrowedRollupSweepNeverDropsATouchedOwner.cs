using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L564 — A NARROWED ROLLUP SWEEP NEVER DROPS AN OWNER WHOSE ROOT THE BATCH TOUCHED.
    ///
    /// <see cref="DerivedAggregateRefresh.Targets"/> used to sweep EVERY faction → EVERY site →
    /// <c>GetComponent&lt;GeoPhoenixBase&gt;()</c> → <c>Layout.Facilities</c>, plus EVERY character, on
    /// EVERY armed batch, with no relation to what had actually changed — 3-4 times a second on every
    /// client, because that is the rate host batches arrive at (DiffEngine.cs:44-59). It is now narrowed to
    /// the owners whose OWN rail root the batch touched, which is O(changed) instead of O(all).
    ///
    /// THAT NARROWING IS A REACTIVITY DECISION, NOT A PERFORMANCE ONE, and it fails silently in the
    /// dangerous direction: an owner wrongly dropped is a rollup nobody rebuilds, i.e. the stale zero the
    /// whole engine exists to remove, with a table entry in front of it reading as covered. So the decision
    /// is not trusted to review — it is EXECUTED here, over the real
    /// <see cref="DerivedAggregateRefresh.Table"/>, for every combination of the inputs each row declares.
    ///
    /// THE CONTRACT, IN ONE LINE: <see cref="DerivedAggregateRefresh.Row.InputPrefixes"/> IS THE AUTHORITY.
    /// A rollup is an OWNER-LOCAL function of the roots its row declares, so
    ///   • a batch that moved an input OFF the sweep axis (a faction-level value feeding every base's
    ///     stats, say) is owner-INDEPENDENT and every owner must rebuild — no narrowing at all;
    ///   • a batch that moved only on-axis inputs may narrow, and then every root it named must still be
    ///     rebuilt, while a root it did not name must not be.
    /// Arm (b) drives exactly that, per row, over every non-empty subset of the row's declared inputs, so
    /// widening a row's inputs or moving <see cref="DerivedAggregateRefresh.SweepKind"/> under it goes RED
    /// rather than quietly shrinking what gets rebuilt.
    ///
    /// ROLES SEPARATED: nothing here is role-dependent — the table is a static declaration and all four
    /// decisions under test are pure string/set functions with no live level.
    ///
    /// Falsify (compile-valid src mutations, each named): drop the <c>declaresAxis</c> requirement from
    /// <c>MayNarrow</c> (return true for a row that declares no axis input) → <c>L564 sweep-drops-a-touched-owner</c>;
    /// make <c>MayNarrow</c> ignore the off-axis test (delete the inner touched-path loop) →
    /// <c>L564 off-axis-input-still-narrows</c>; give <c>SweepKind</c> a key <c>IdentityResolver</c> never
    /// emits → <c>L564 sweep-axis-is-not-a-root</c>; drop the <c>roots</c> parameter from <c>Targets</c> →
    /// <c>L564 narrowing-unwired</c>; make <c>Included</c> a constant → <c>L564 positive-control</c>.
    /// </summary>
    internal static class L564_ANarrowedRollupSweepNeverDropsATouchedOwner
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>A synthetic path under a root prefix. Deep enough that <c>RootOf</c> has something to
        /// cut, and shaped like the real thing (no segment is ever an index, DiffEngine.cs:1231).</summary>
        private static string PathUnder(string prefix, string id) =>
            prefix + id + ".SerializationData.SomeField";

        internal static IEnumerable<string> Check()
        {
            var rows = DerivedAggregateRefresh.Table;
            var roots = IdentityResolver.RootKinds;
            if (rows == null || rows.Length == 0 || roots == null || roots.Length == 0)
            {
                yield return "L564 premise-changed: DerivedAggregateRefresh.Table or " +
                             "IdentityResolver.RootKinds no longer resolves. While this law cannot see " +
                             "them, the peer-side rollup sweep can be narrowed to a set that silently " +
                             "excludes an owner whose root DID move — re-point the law, do not delete it.";
                yield break;
            }
            var rootKeys = new HashSet<string>(roots.Where(r => !string.IsNullOrEmpty(r.Key))
                                                    .Select(r => r.Key), StringComparer.Ordinal);

            // ── (a) the sweep axis is a root the rail actually emits ─────────
            foreach (var row in rows)
            {
                if (row.Class != DerivedAggregateRefresh.Kind.Recompute) continue;
                var sweep = DerivedAggregateRefresh.SweepKind(row.Owner);
                if (sweep == null)
                {
                    // A single-instance owner (the level, the marketplace) has nothing to narrow, and the
                    // narrowing must refuse it rather than compare its non-existent root against a set.
                    if (DerivedAggregateRefresh.MayNarrow(row, null, new[] { "S#1" }))
                        yield return "L564 sweep-drops-a-touched-owner: " + row.Owner.Name + "/" +
                                     row.Rebuild + " has no sweep axis (a single-instance owner), yet " +
                                     "MayNarrow said yes. Its target would then be compared against a root " +
                                     "set it can never appear in and the rollup would never rebuild again.";
                    continue;
                }
                if (!rootKeys.Contains(sweep))
                    yield return "L564 sweep-axis-is-not-a-root: DerivedAggregateRefresh.SweepKind returns '" +
                                 sweep + "' for " + row.Owner.Name + ", which IdentityResolver.RootKinds " +
                                 "never emits. The narrowing compares that key against RootRef strings, so " +
                                 "no owner could ever match and every rollup on this axis would stop " +
                                 "rebuilding — silently, on the peer that needs it most.";
            }

            // ── (b) the core property, per row, per input subset ─────────────
            foreach (var row in rows)
            {
                if (row.Class != DerivedAggregateRefresh.Kind.Recompute) continue;
                var sweep = DerivedAggregateRefresh.SweepKind(row.Owner);
                if (sweep == null) continue;
                var inputs = row.InputPrefixes;
                if (inputs.Length == 0 || inputs.Length > 16) continue; // no declaration: (c) owns it
                for (int mask = 1; mask < (1 << inputs.Length); mask++)
                {
                    var paths = new List<string>();
                    var onAxis = new List<string>();
                    bool offAxisTouched = false;
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        if ((mask & (1 << i)) == 0 || inputs[i] == null) continue;
                        paths.Add(PathUnder(inputs[i], "7"));
                        if (sweep.StartsWith(inputs[i], StringComparison.Ordinal)) onAxis.Add(inputs[i] + "7");
                        else offAxisTouched = true;
                    }
                    if (paths.Count == 0) continue;
                    var touchedRoots = DerivedAggregateRefresh.TouchedRoots(paths);
                    bool narrow = DerivedAggregateRefresh.MayNarrow(row, sweep, paths);

                    if (offAxisTouched && narrow)
                        yield return "L564 off-axis-input-still-narrows: " + row.Owner.Name + "/" +
                                     row.Rebuild + " narrowed its sweep although this batch moved an input " +
                                     "it declares OFF its own sweep axis (" + string.Join(", ", paths) +
                                     "). Such an input is owner-INDEPENDENT — a faction value feeding every " +
                                     "base's stats is the live case — so every owner owes a rebuild and " +
                                     "narrowing drops all but the coincidentally-touched few.";

                    // NEVER DROP A TOUCHED OWNER. Trivially satisfied when nothing narrowed, and that is
                    // the point: the assertion is the same one either way.
                    foreach (var owner in onAxis)
                        if (!DerivedAggregateRefresh.Included(narrow, owner, touchedRoots))
                            yield return "L564 sweep-drops-a-touched-owner: " + row.Owner.Name + "/" +
                                         row.Rebuild + " would NOT rebuild owner '" + owner + "' on a batch " +
                                         "that touched exactly its root (" + string.Join(", ", paths) +
                                         "). A dropped owner is a rollup nobody rebuilds — the stale zero " +
                                         "this engine exists to remove, with a table entry in front of it.";

                    if (narrow && DerivedAggregateRefresh.Included(true, sweep + "999", touchedRoots))
                        yield return "L564 positive-control: " + row.Owner.Name + "/" + row.Rebuild +
                                     " included an owner whose root this batch never touched, so the " +
                                     "narrowing above is measuring a function that always says yes.";
                }
            }

            // ── (c) an undeclared row never narrows ──────────────────────────
            var undeclared = new DerivedAggregateRefresh.Row(
                typeof(PhoenixPoint.Geoscape.Levels.GeoFaction), "L564Probe",
                DerivedAggregateRefresh.Kind.Recompute, "L564Probe", new string[0], "railcheck probe");
            if (DerivedAggregateRefresh.MayNarrow(undeclared, "F#", new[] { "F#1.X" }))
                yield return "L564 sweep-drops-a-touched-owner: a row that declares NO input prefixes " +
                             "narrowed its sweep. Arms() arms such a row unconditionally precisely because " +
                             "nothing is known about what it reads, so the sweep behind it must stay whole.";
            var offAxisOnly = new DerivedAggregateRefresh.Row(
                typeof(PhoenixPoint.Geoscape.Levels.GeoFaction), "L564Probe",
                DerivedAggregateRefresh.Kind.Recompute, "L564Probe", new[] { "S#" }, "railcheck probe");
            if (DerivedAggregateRefresh.MayNarrow(offAxisOnly, "F#", new[] { "S#1.X" }))
                yield return "L564 off-axis-input-still-narrows: a row whose only declared input is OFF its " +
                             "sweep axis narrowed anyway, so every owner but the coincidentally-touched " +
                             "ones stops rebuilding.";
            // The same row, on a batch that moved NOTHING it declares. Nothing forbids narrowing here, so
            // this is the arm that measures the POSITIVE requirement rather than the veto: a row must
            // declare its own sweep axis to be narrowed along it at all. Without that requirement a row
            // reading only "V#" would be filtered by SITE roots, i.e. by a set its owners never appear in.
            var wrongAxisOnly = new DerivedAggregateRefresh.Row(
                typeof(PhoenixPoint.Geoscape.Levels.GeoFaction), "L564Probe",
                DerivedAggregateRefresh.Kind.Recompute, "L564Probe", new[] { "V#" }, "railcheck probe");
            if (DerivedAggregateRefresh.MayNarrow(wrongAxisOnly, "S#", new[] { "U#1.X" }))
                yield return "L564 sweep-drops-a-touched-owner: a row that declares no input on its own " +
                             "sweep axis narrowed along that axis anyway. Its owners would be filtered by a " +
                             "root set nothing they read ever lands in, and the rollup stops rebuilding.";
            // A path the mark site could not name is the conservative direction of the same question: Arms
            // treats an unknown path as arming everything, so the sweep behind it must stay whole too.
            if (DerivedAggregateRefresh.MayNarrow(rows.First(r => r.Class == DerivedAggregateRefresh.Kind.Recompute &&
                                                                  r.InputPrefixes.Length > 0),
                                                  "S#", new string[] { null }))
                yield return "L564 sweep-drops-a-touched-owner: an UNKNOWN (null) touched path still " +
                             "narrowed the sweep. Arms treats such a path as arming every row precisely " +
                             "because nothing is known about it; narrowing on it drops every owner whose " +
                             "root the unnameable change actually moved.";

            // ── (d) the narrowing is actually wired into the sweep ───────────
            var engine = typeof(DerivedAggregateRefresh);
            var targets = engine.GetMethod("Targets", Any);
            var tick = engine.GetMethod("ClientTick", Any);
            var mayNarrow = engine.GetMethod("MayNarrow", Any);
            var ps = targets?.GetParameters();
            if (targets == null || ps.Length != 3 || ps[2].ParameterType != typeof(HashSet<string>))
                yield return "L564 narrowing-unwired: DerivedAggregateRefresh.Targets no longer takes the " +
                             "touched-root set, so it is back to sweeping every faction, every site, every " +
                             "facility and every character on every armed batch — the O(all) walk that " +
                             "stalled client frames 3-4 times a second (worst=repaint 35..43ms, 2026-08-18).";
            else if (tick == null || mayNarrow == null ||
                     !Program.Callees(tick, engine.Assembly, true)
                             .Any(m => m.MetadataToken == mayNarrow.MetadataToken &&
                                       m.Module == mayNarrow.Module))
                yield return "L564 narrowing-unwired: ClientTick never calls MayNarrow, so whatever it " +
                             "hands Targets is not the decision this law proves. Every arm above would " +
                             "stay green while the live sweep narrows on some other rule entirely.";

            // ── (e) positive control: the decisions are not constants ────────
            var probeRoots = DerivedAggregateRefresh.TouchedRoots(new[] { "U#7.Progression.Level" });
            if (DerivedAggregateRefresh.RootOf("U#7.Progression.Level") != "U#7" ||
                DerivedAggregateRefresh.RootOf("U#7") != "U#7" ||
                DerivedAggregateRefresh.RootOf(null) != null ||
                !probeRoots.Contains("U#7") || probeRoots.Contains("U#8") ||
                !DerivedAggregateRefresh.Included(false, null, null) ||
                !DerivedAggregateRefresh.Included(true, "U#7", probeRoots) ||
                DerivedAggregateRefresh.Included(true, "U#8", probeRoots) ||
                DerivedAggregateRefresh.Included(true, null, probeRoots))
                yield return "L564 positive-control: RootOf, TouchedRoots or Included stopped rejecting " +
                             "their falsifying rows, so every arm above is measuring a function that " +
                             "always says yes and no sweep is protected at all.";
        }
    }
}
