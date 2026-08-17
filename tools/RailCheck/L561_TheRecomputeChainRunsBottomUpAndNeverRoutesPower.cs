using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.PhoenixBases;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L561 — THE RECOMPUTE CHAIN RUNS BOTTOM-UP, AND ITS BOTTOM ROW NEVER ROUTES POWER.
    ///
    /// <see cref="DerivedAggregateRefresh"/> is a LADDER, and <c>Table</c> order IS iteration order
    /// (<c>ClientTick</c>). <c>cdfb077</c> added level 2 (<c>GeoFaction.UpdateProduction</c>) and level 1
    /// (<c>GeoPhoenixBase.UpdateStats</c>) and stopped there — but <c>PhoenixBaseStats.Update</c> only SUMS
    /// values each facility component has already CACHED in a <c>{get; private set;}</c> that only its own
    /// <c>UpdateOutput()</c> writes, driven by slot events a rail FIELD WRITE never fires. So level 1
    /// re-summed zeros and level 2 re-summed that. Level 0 is the missing rung.
    ///
    /// AND IT IS THE ONE RUNG THAT CANNOT TAKE THE GAME'S OWN DEFAULT ARGUMENT.
    /// <c>GeoPhoenixFacility.UpdateOutput(bool suppressEvents = false)</c> with <c>false</c> raises
    /// <c>OnFacilityOutputUpdated</c>, which reaches <c>GeoPhoenixBase.Facility_OnFacilityOutputUpdated</c>
    /// (GeoPhoenixBase.cs:702) -> <c>RoutePower()</c> -> <c>SetPowered</c>. This engine runs inside
    /// <c>SyncApplyScope</c>, where <c>FacilityPowerGate</c>'s apply arm (ClientSimGate.cs:190-196) is
    /// deliberately OPEN — so the default argument makes a client re-route its own base's power, i.e. the
    /// exact simulation that gate exists to refuse. <c>DefaultArgs</c> would pass it.
    ///
    /// ARMS
    ///   (a) <c>ladder-inverted</c> — EXECUTED over the real <c>Table</c>: the <c>GeoPhoenixFacility</c>
    ///       Recompute row must sit before the <c>GeoPhoenixBase</c> one, which must sit before the
    ///       <c>GeoFaction</c> one. Reordering the array is a silent, whole-defect regression.
    ///   (b) <c>level-0-routes-power</c> — that row must carry an explicit <c>Args</c> of exactly
    ///       <c>{ true }</c>, and it must be an OVERRIDE that changes something: read off the SHIPPED
    ///       GAME, <c>UpdateOutput</c>'s single parameter must be a <c>bool</c> whose own default is
    ///       <c>false</c>. If the game ever flips that default, the row's override is a no-op and the
    ///       sentence this law protects has quietly moved.
    ///   (c) <c>args-escape-unreasoned</c> — the escape stays NARROW: exactly one row in the whole table
    ///       may carry <c>Args</c>, and any row that does must carry a non-empty <c>ArgsReason</c>.
    ///   (d) <c>args-escape-unwired</c> — IL: <c>ClientTick</c> must READ <c>Row.Args</c>. A row that
    ///       declares the override while the tick still calls <c>DefaultArgs</c> passes every arm above
    ///       and routes power anyway.
    ///   (e) <c>owner-unreachable</c> — IL: <c>Targets</c> must reach <c>GeoPhoenixBase.Layout</c> and
    ///       <c>GeoPhoenixBaseLayout.Facilities</c>. Without the arm, the row resolves to NO target, the
    ///       engine ANNOUNCES it once, and the ladder reads as covered while its bottom rung never runs.
    ///
    /// NO QUORUM: everything here is this peer's own table, this peer's own model. Nothing waits.
    ///
    /// Falsify: move the facility row after the base row → (a); drop the <c>new object[] { true }</c> →
    /// (b); give a second row <c>Args</c>, or blank the reason → (c); make <c>ClientTick</c> call
    /// <c>DefaultArgs</c> unconditionally → (d); delete the <c>GeoPhoenixFacility</c> arm from
    /// <c>Targets</c> → (e).
    /// </summary>
    internal static class L561_TheRecomputeChainRunsBottomUpAndNeverRoutesPower
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        internal static IEnumerable<string> Check()
        {
            var engine = typeof(DerivedAggregateRefresh);
            var tableField = engine.GetField("Table", Any);
            var tick = engine.GetMethod("ClientTick", Any);
            var targets = engine.GetMethod("Targets", Any);
            var update = typeof(GeoPhoenixFacility).GetMethod("UpdateOutput", Any);
            var rows = tableField?.GetValue(null) as Array;

            if (rows == null || tick == null || targets == null || update == null)
            {
                yield return "L561 premise-changed: DerivedAggregateRefresh.Table / ClientTick / Targets or " +
                             "GeoPhoenixFacility.UpdateOutput no longer resolves. The recompute ladder is the " +
                             "only thing rebuilding cached facility outputs on a peer — re-point the law at " +
                             "whatever carries it now; do NOT delete it, because a stale level 0 silently " +
                             "zeroes every base stat and every faction income derived from it";
                yield break;
            }

            var rowType = rows.Length == 0 ? null : rows.GetValue(0).GetType();
            var fOwner = rowType?.GetField("Owner", Any);
            var fClass = rowType?.GetField("Class", Any);
            var fRebuild = rowType?.GetField("Rebuild", Any);
            var fArgs = rowType?.GetField("Args", Any);
            var fArgsReason = rowType?.GetField("ArgsReason", Any);
            if (fOwner == null || fClass == null || fRebuild == null || fArgs == null || fArgsReason == null)
            {
                yield return "L561 premise-changed: DerivedAggregateRefresh.Row no longer carries " +
                             "Owner/Class/Rebuild/Args/ArgsReason. Args is the narrow escape this law polices; " +
                             "a Row without it cannot express \"do LESS than the game's default\" at all.";
                yield break;
            }

            // ═══ (a) the ladder, bottom-up ═══
            int facilityRow = -1, baseRow = -1, factionRow = -1, argRows = 0;
            object level0 = null;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows.GetValue(i);
                if (fClass.GetValue(row)?.ToString() != "Recompute") continue;
                var owner = fOwner.GetValue(row) as Type;
                if (owner == null) continue;
                if (facilityRow < 0 && typeof(GeoPhoenixFacility).IsAssignableFrom(owner))
                { facilityRow = i; level0 = row; }
                else if (baseRow < 0 && typeof(GeoPhoenixBase).IsAssignableFrom(owner)) baseRow = i;
                else if (factionRow < 0 && typeof(GeoFaction).IsAssignableFrom(owner)) factionRow = i;
            }
            if (facilityRow < 0)
                yield return "L561 ladder-inverted: there is no Recompute row for GeoPhoenixFacility at all. " +
                             "Every facility component caches its output in a private setter only its own " +
                             "UpdateOutput writes, and the slot events that drive it never fire on a peer — " +
                             "so GeoPhoenixBase.UpdateStats above re-sums zeros and the faction's income " +
                             "below it re-sums that.";
            else if (baseRow >= 0 && facilityRow > baseRow)
                yield return "L561 ladder-inverted: the GeoPhoenixFacility row sits AFTER the GeoPhoenixBase " +
                             "row (index " + facilityRow + " > " + baseRow + "). Table order IS iteration " +
                             "order, so level 1 would sum the caches level 0 has not rebuilt yet — one frame " +
                             "of stale stats per rail batch, forever.";
            if (baseRow >= 0 && factionRow >= 0 && baseRow > factionRow)
                yield return "L561 ladder-inverted: the GeoPhoenixBase row sits AFTER the GeoFaction row " +
                             "(index " + baseRow + " > " + factionRow + "), so the faction's production is " +
                             "rolled up from Site.SiteProduction values the base has not rewritten yet.";

            // ═══ (b) the bottom rung does not route power ═══
            var updateParams = update.GetParameters();
            if (updateParams.Length != 1 || updateParams[0].ParameterType != typeof(bool) ||
                !updateParams[0].HasDefaultValue || !false.Equals(updateParams[0].DefaultValue))
                yield return "L561 level-0-routes-power: GeoPhoenixFacility.UpdateOutput is no longer " +
                             "`(bool suppressEvents = false)`. The whole point of this row's explicit " +
                             "argument is that the GAME'S OWN default lets OnFacilityOutputUpdated reach " +
                             "GeoPhoenixBase.Facility_OnFacilityOutputUpdated:702 -> RoutePower -> " +
                             "SetPowered, which FacilityPowerGate admits inside SyncApplyScope. If the " +
                             "signature moved, re-read the native tail before trusting the override.";
            else if (level0 != null)
            {
                var args = fArgs.GetValue(level0) as object[];
                if (args == null || args.Length != 1 || !true.Equals(args[0]))
                    yield return "L561 level-0-routes-power: the GeoPhoenixFacility row does not declare " +
                                 "Args = { true }, so DerivedAggregateRefresh falls back to DefaultArgs and " +
                                 "invokes UpdateOutput(false). On a client that raises the facility output " +
                                 "event inside SyncApplyScope, and GeoPhoenixBase.RoutePower then writes " +
                                 "SetPowered straight through FacilityPowerGate's open apply arm — a client " +
                                 "simulating its own base's power routing.";
            }

            // ═══ (c) the escape stays narrow, and it is argued ═══
            foreach (var row in rows)
            {
                if (fArgs.GetValue(row) == null) continue;
                argRows++;
                if (string.IsNullOrWhiteSpace(fArgsReason.GetValue(row) as string))
                    yield return "L561 args-escape-unreasoned: a row overrides the game's own default " +
                                 "argument (" + (fOwner.GetValue(row) as Type)?.Name + "." +
                                 (fRebuild.GetValue(row) as string) + ") with no ArgsReason. The safety " +
                                 "argument for this whole engine is that the CALLER SUPPLIES NO " +
                                 "INFORMATION; a row that steps outside it and does not say which native " +
                                 "tail it cuts, by file:line, is unreviewable.";
            }
            if (argRows > 1)
                yield return "L561 args-escape-unreasoned: " + argRows + " rows now carry explicit Args. The " +
                             "escape is deliberately for the one rebuild whose DEFAULT is unsafe on a peer; " +
                             "a table where several rows invent their own arguments is the caller supplying " +
                             "information again, which is the divergence this engine exists to avoid.";

            // ═══ (d)+(e) the escape and the target sweep are WIRED ═══
            if (!Program.FieldRefs(tick, OpCodes.Ldfld)
                    .Any(f => f.MetadataToken == fArgs.MetadataToken && f.Module == fArgs.Module))
                yield return "L561 args-escape-unwired: DerivedAggregateRefresh.ClientTick never READS " +
                             "Row.Args, so every row is invoked with DefaultArgs no matter what it declares. " +
                             "Arm (b) would still pass while the client routes power on every rail batch.";
            // Targets is an ITERATOR: its body lives in the compiler-generated state machine, so asking the
            // declared method its callees answers "none" for every arm. Walk MoveNext instead.
            var walk = MoveNextOf(engine, "Targets") ?? targets;
            var targetCallees = Program.Callees(walk, typeof(GeoPhoenixBase).Assembly).ToList();
            if (!targetCallees.Any(m => m.Name == "get_Layout" && m.DeclaringType == typeof(GeoPhoenixBase)) ||
                !targetCallees.Any(m => m.Name == "get_Facilities"))
                yield return "L561 owner-unreachable: DerivedAggregateRefresh.Targets no longer sweeps " +
                             "GeoPhoenixBase.Layout -> Facilities, so the GeoPhoenixFacility row resolves to " +
                             "NO target. That fails in the worst direction the file names itself: the row is " +
                             "announced once and then reads as covered, while its rebuild never runs.";
        }

        /// <summary>The state machine's <c>MoveNext</c> for an iterator method, or null. Matched on the
        /// C# compiler's own nesting convention (<c>&lt;Name&gt;d__N</c>) rather than on an index, so a
        /// re-ordered file does not silently point the arm at another method's body.</summary>
        private static MethodBase MoveNextOf(Type owner, string method)
        {
            foreach (var nested in owner.GetNestedTypes(Any))
                if (nested.Name.StartsWith("<" + method + ">d__", StringComparison.Ordinal))
                    return nested.GetMethod("MoveNext", Any);
            return null;
        }
    }
}
