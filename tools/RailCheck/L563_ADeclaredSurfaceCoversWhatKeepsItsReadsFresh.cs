using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L563 — A DECLARED SURFACE'S PREFIXES ARE REAL ROOTS, AND ONE THAT READS A REBUILT ROLLUP DECLARES
    /// WHAT REBUILDS IT.
    ///
    /// <see cref="UiNativeRepaint.DeclaredPrefixes"/> is the one place in this repo where writing LESS makes
    /// a screen go stale. L541 owns the safe direction (no declaration = repaint on everything); this law
    /// owns the dangerous one — a row that IS present but does not cover what its own repaint arm reads.
    /// Both failure modes below are silent: the screen simply stops changing, with no log line and no
    /// violation anywhere else in the harness.
    ///
    /// (a) A PREFIX THAT MATCHES NO ROOT SUBSCRIBES TO NOTHING. The matcher is
    /// <c>path.StartsWith(prefix)</c> (OpenUiRepaint.SurfaceRepaints), so "Fx#" or "Site" is not a narrower
    /// declaration — it is a declaration that can never be hit, and a surface whose whole row is unhittable
    /// is a surface that never repaints again. Every prefix must therefore begin with a key
    /// <see cref="IdentityResolver.RootKinds"/> actually emits (or the "M#" mod-root family, which is
    /// registered at runtime and so has no static row to compare against).
    ///
    /// (b) A ROLLUP'S INPUTS ARE THE SURFACE'S INPUTS. Some of what a screen prints is not rail state at
    /// all: it is rebuilt on the receiving peer by <see cref="DerivedAggregateRefresh"/>, whose rows declare
    /// the rail roots their rebuild READS. The research screen is the live case — every queue row prints
    /// <c>Research.GetTotalTimeLeft</c>, whose rate is <c>Faction.ResourceIncome</c> (Research.cs:649-651),
    /// which no peer ever receives and which only the GeoFaction/UpdateProduction row rebuilds, from
    /// { "S#", "F#" }. A lab finished on another peer moves a SITE path and nothing under "F#", so a
    /// faction-only declaration would freeze every ETA on the screen while looking perfectly reasonable.
    /// The pairing is asserted here rather than trusted to the comment beside it.
    ///
    /// The <see cref="Reads"/> table is a CLAIM, in the same sense L38's rows are: a human read the arm and
    /// wrote down which rebuilt rollup it consumes. What the law removes is the chance for the two halves to
    /// drift — the row's input set may be widened in DerivedAggregateRefresh, or the surface's prefix set
    /// narrowed here, and either one goes RED.
    ///
    /// ROLES SEPARATED: nothing here is role-dependent — both tables are static declarations and
    /// <see cref="Covers"/> is a pure string decision.
    ///
    /// Falsify (compile-valid src mutations, each named): narrow
    /// <c>DeclaredPrefixes["UIStateResearch"]</c> to <c>{ "F#" }</c> → <c>L563 rollup-input-undeclared</c>;
    /// misspell any declared prefix (e.g. "F#" → "Fx#") → <c>L563 prefix-matches-no-root</c>; delete the
    /// GeoFaction/UpdateProduction row from DerivedAggregateRefresh.Table → <c>L563 premise-changed</c>;
    /// make <see cref="Covers"/> a constant → <c>L563 positive-control</c>.
    /// </summary>
    internal static class L563_ADeclaredSurfaceCoversWhatKeepsItsReadsFresh
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Declared surface ⇒ the <see cref="DerivedAggregateRefresh"/> row whose rebuilt value the
        /// surface's own repaint arm prints. Sparse on purpose: a surface that reads only replicated state
        /// has no row here and nothing to prove.</summary>
        private static readonly (string Surface, Type Owner, string Rebuild, string Readout)[] Reads =
        {
            ("UIStateResearch", typeof(GeoFaction), "UpdateProduction",
             "every research queue row's ETA — ResearchQueueItem.cs:121-124 prints " +
             "Research.GetTotalTimeLeft, whose rate is Faction.ResourceIncome (Research.cs:649-651), a " +
             "value no peer receives and only this row rebuilds"),
        };

        internal static IEnumerable<string> Check()
        {
            var declared = UiNativeRepaint.DeclaredPrefixes;
            var roots = IdentityResolver.RootKinds;
            var rows = typeof(DerivedAggregateRefresh).GetField("Table", Any)?.GetValue(null) as Array;
            if (declared == null || roots == null || roots.Length == 0 || rows == null || rows.Length == 0)
            {
                yield return "L563 premise-changed: UiNativeRepaint.DeclaredPrefixes, " +
                             "IdentityResolver.RootKinds or DerivedAggregateRefresh.Table no longer " +
                             "resolves. While this law cannot see them, a declared surface can be narrowed " +
                             "to a prefix set that matches nothing and the screen simply stops repainting — " +
                             "re-point the law, do not delete it.";
                yield break;
            }

            var rootKeys = new List<string>();
            foreach (var r in roots)
                if (!string.IsNullOrEmpty(r.Key)) rootKeys.Add(r.Key);
            rootKeys.Add("M#"); // mod-state roots are registered at runtime; the family prefix is the anchor

            // ── (a) every declared prefix is reachable at all ────────────────
            foreach (var kv in declared)
            {
                if (kv.Value == null) continue; // L541 owns the empty/null row
                foreach (var prefix in kv.Value)
                {
                    if (prefix != null && StartsWithAny(prefix, rootKeys)) continue;
                    yield return "L563 prefix-matches-no-root: surface '" + kv.Key + "' declares '" +
                                 (prefix ?? "<null>") + "', which begins with no key IdentityResolver ever " +
                                 "emits. SurfaceRepaints matches by StartsWith, so this is not a narrower " +
                                 "subscription — it is one that can never be hit, and a surface whose whole " +
                                 "row is unhittable never repaints again.";
                }
            }

            // ── (b) a rebuilt rollup's inputs are the surface's inputs ───────
            var rowType = rows.GetValue(0).GetType();
            var fOwner = rowType.GetField("Owner", Any);
            var fRebuild = rowType.GetField("Rebuild", Any);
            var fInputs = rowType.GetField("InputPrefixes", Any);
            if (fOwner == null || fRebuild == null || fInputs == null)
            {
                yield return "L563 premise-changed: DerivedAggregateRefresh.Row no longer carries " +
                             "Owner/Rebuild/InputPrefixes. InputPrefixes IS the claim this arm reads across " +
                             "to the declaration.";
                yield break;
            }

            foreach (var claim in Reads)
            {
                string[] prefixes;
                if (!declared.TryGetValue(claim.Surface, out prefixes) || prefixes == null ||
                    prefixes.Length == 0)
                {
                    yield return "L563 premise-changed: '" + claim.Surface + "' is no longer a declared " +
                                 "surface, so this law's row for it proves nothing. An undeclared surface " +
                                 "repaints on everything (L541) and is safe — but drop the row here too, " +
                                 "deliberately, rather than leaving a claim about a surface that is gone.";
                    continue;
                }

                string[] inputs = null;
                foreach (var row in rows)
                {
                    if (!Equals(fOwner.GetValue(row), claim.Owner)) continue;
                    if ((fRebuild.GetValue(row) as string) != claim.Rebuild) continue;
                    inputs = fInputs.GetValue(row) as string[];
                    break;
                }
                if (inputs == null || inputs.Length == 0)
                {
                    yield return "L563 premise-changed: DerivedAggregateRefresh has no recompute row " +
                                 claim.Owner.Name + "/" + claim.Rebuild + " with declared inputs. That row " +
                                 "is what keeps '" + claim.Surface + "' honest about " + claim.Readout + ".";
                    continue;
                }

                foreach (var input in inputs)
                {
                    if (input == null || Covers(prefixes, input)) continue;
                    yield return "L563 rollup-input-undeclared: surface '" + claim.Surface + "' does not " +
                                 "declare '" + input + "', an input of the " + claim.Owner.Name + "/" +
                                 claim.Rebuild + " rebuild it prints — " + claim.Readout + ". That value " +
                                 "reaches no peer as rail state, so a batch touching only '" + input +
                                 "' would leave the readout frozen on an already-open screen with nothing " +
                                 "anywhere saying so.";
                }
            }

            // ── (c) positive control: neither decision is a constant ─────────
            if (Covers(new[] { "F#" }, "S#") || !Covers(new[] { "F#", "S#" }, "S#") ||
                !Covers(new[] { "S#" }, "S#12.SerializationData") || Covers(new[] { "S#12" }, "S#") ||
                StartsWithAny("Fx#", rootKeys) || !StartsWithAny("F#", rootKeys))
                yield return "L563 positive-control: Covers or StartsWithAny stopped rejecting its " +
                             "falsifying rows, so arms (a) and (b) are measuring a function that always " +
                             "says yes and every surface above is unprotected.";
        }

        /// <summary>Does the declaration hear about this rail path prefix? A declared prefix covers an input
        /// only when it is a PREFIX OF it — declaring "F#12.Research" does not cover the root "F#", because
        /// the input names a whole subtree the surface would then stop hearing about.</summary>
        private static bool Covers(string[] declared, string input)
        {
            foreach (var d in declared)
                if (d != null && input.StartsWith(d, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool StartsWithAny(string value, List<string> keys)
        {
            foreach (var k in keys)
                if (value.StartsWith(k, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
