using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities.PhoenixBases;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L291 — NO MIRRORED PER-ACTOR-CLOCK TIMESTAMP IS ALLOWED TO DECIDE STATE ON A CLIENT.
    ///
    /// THE CLASS, not the case. L290 fixes exploration; this law exists because exploration was never the
    /// only member. The rail mirrors the LEAF but explicitly excludes the CLOCK it is measured in
    /// (rail-baseline.txt:33 — "EXCLUDED TimingData (TimingInstanceData): per-actor clock … client clocks
    /// tick locally, the level clock rides the TimeAnchor 'TA' root"), so EVERY mirrored TimeUnit written
    /// from an <c>ActorComponent.Timing</c> arrives in an epoch this peer cannot read. Sweeping
    /// docs/rail-baseline.txt for covered TimeUnit leaves and tracing each writer in the decompile finds
    /// exactly three families, and they end in three different places:
    ///
    ///   1. <c>GeoVehicle.StartExplorationTime</c> (:727/:753) — written <c>base.Timing.Now</c>
    ///      (GeoVehicle.cs:423), and it DECIDED: <c>GenericApplier.ReseedExploration</c> compared it to the
    ///      local clock. That is L290, fixed by anchoring on delta ARRIVAL.
    ///   2. <c>GeoPhoenixFacility._nextUpdateTime</c> / <c>_prevUpdateTime</c> (:418/:420) — written
    ///      <c>PxBase.Site.Timing.Now</c> (GeoPhoenixFacility.cs:375-376), and it DECIDES:
    ///      <c>UpdateFacility</c>:366 flips a facility to Functioning on
    ///      <c>NextUpdateTime &lt;= PxBase.Site.Timing.Now</c>. On a client that would complete a
    ///      construction the host has not — minted state the diff can never correct, because the diff is
    ///      host-now vs host-before. It is inert TODAY for ONE reason and one only: the sole path to it is
    ///      <c>GeoPhoenixBase.BaseHourlyUpdate</c>:348 ← <c>GeoPhoenixFaction.UpdateBasesHourly</c>:1286 ←
    ///      <c>GeoLevelController.LevelHourlyUpdateCrt</c>:804, and <see cref="ClientSimGate"/> prefixes
    ///      that coroutine wholesale. Nothing states that dependency anywhere; a second caller, or a
    ///      narrowing of the hourly gate, silently arms it. THAT is what this law pins.
    ///   3. <c>GeoHarvestingSite.StartedHarvestingAt</c> (:775) — written <c>_timing.Now</c> where
    ///      <c>_timing = actor.Timing</c> (GeoHarvestingSite.cs:111/:136). It decides NOTHING: its only
    ///      consumers are <c>SetProgression</c> (:114/:168), pure presentation, and the completion is a
    ///      local updateable driven by the mirrored <c>HarvestingForce</c>, not by the timestamp. Recorded
    ///      here rather than asserted — a law over a field that reaches no decision would be decorative.
    ///      (<c>GeoSite.ExpiringTimerAt</c> reads like a fourth and is NOT one: it is assigned from
    ///      <c>GeoEventTimer.EndAt</c>, GeoscapeEventSystem.cs:535, a LEVEL-clock value, and the level
    ///      clock DOES ride the rail.)
    ///
    /// WHAT IS ASSERTED IS THE OUTCOME — that the facility construction clock cannot resolve on a client —
    /// and it is asserted as REACHABILITY, not as a call we make: the arms sweep the game's own geoscape
    /// namespaces for every caller of the deciding method and require the whole chain to stay inside the
    /// one coroutine the client gate blocks.
    ///
    /// Falsify: retarget ClientSimGate off LevelHourlyUpdateCrt → <c>hourly-gate-moved</c>; add a caller of
    /// UpdateFacility outside BaseHourlyUpdate (or of BaseHourlyUpdate outside UpdateBasesHourly) →
    /// <c>facility-clock-ungated</c>; break the IL sweep → <c>scan-empty</c>; drop
    /// <c>_nextUpdateTime</c>'s clock write → <c>premise-changed</c>.
    /// </summary>
    internal static class L291_AMirroredActorClockDecidesNothingLocally
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The one deciding member of family 2, and the chain that is allowed to reach it. Written
        /// as names because the assertion is "no OTHER method calls this", which is a set, not a call.</summary>
        private const string HourlyCoroutine = "LevelHourlyUpdateCrt";

        internal static IEnumerable<string> Check(Assembly game)
        {
            var facility = typeof(GeoPhoenixFacility);
            var update = facility.GetMethod("UpdateFacility", All);
            var nextUpdate = facility.GetField("_nextUpdateTime", All);
            var updateAfter = facility.GetMethod("UpdateAfter", All);
            var functioning = facility.GetMethod("SetFacilityFunctioning", All);
            var baseHourly = typeof(GeoPhoenixBase).GetMethod("BaseHourlyUpdate", All);
            var basesHourly = typeof(PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction)
                              .GetMethod("UpdateBasesHourly", All);
            var timingNow = typeof(Timing).GetProperty("Now", All)?.GetGetMethod(true);
            var harvestStart = typeof(GeoHarvestingSite).GetField("_startedHarvestingAt", All);

            if (update == null || nextUpdate == null || updateAfter == null || functioning == null ||
                baseHourly == null || basesHourly == null || timingNow == null || harvestStart == null)
            {
                yield return "L291 premise-changed: one of GeoPhoenixFacility.{UpdateFacility,_nextUpdateTime," +
                             "UpdateAfter,SetFacilityFunctioning}, GeoPhoenixBase.BaseHourlyUpdate, " +
                             "GeoPhoenixFaction.UpdateBasesHourly, Timing.Now or " +
                             "GeoHarvestingSite._startedHarvestingAt no longer resolves. The per-actor-clock " +
                             "family this law quantifies over has changed shape — re-derive it from " +
                             "docs/rail-baseline.txt before assuming the remaining members are still inert.";
                yield break;
            }

            // ── premise: _nextUpdateTime really is a per-actor clock reading that really does decide ─────
            if (!Program.Callees(updateAfter, game).Any(c => Same(c, timingNow)))
                yield return "L291 premise-changed: GeoPhoenixFacility.UpdateAfter no longer reads Timing.Now, " +
                             "so _nextUpdateTime is not an actor-clock stamp any more and the epoch argument " +
                             "above does not apply to it. Re-read the family before keeping this gate alive.";
            // The read goes through the NextUpdateTime property (:183), not the backing field — accept either,
            // because which of the two the getter is inlined to is a compiler decision, not a design one.
            var nextUpdateGet = facility.GetProperty("NextUpdateTime", All)?.GetGetMethod(true);
            if ((!Program.ReadsField(update, nextUpdate) &&
                 !Program.Callees(update, game).Any(c => Same(c, nextUpdateGet))) ||
                !Program.Callees(update, game).Any(c => Same(c, functioning)))
                yield return "L291 premise-changed: GeoPhoenixFacility.UpdateFacility no longer turns " +
                             "_nextUpdateTime into SetFacilityFunctioning. The construction completion has moved; " +
                             "find where it went before trusting that the hourly gate still contains it.";

            // ── the OUTCOME: the deciding method is unreachable on a client ───────────────────────────
            var universe = GeoscapeMethods(game);
            if (universe.Count < 200)
            {
                yield return "L291 scan-empty: the geoscape IL sweep found only " + universe.Count + " method(s), " +
                             "so the caller sets below are meaningless and this law would pass by seeing nothing. " +
                             "A caller-set assertion that cannot enumerate its universe proves nothing.";
                yield break;
            }

            foreach (var pair in new[]
                     {
                         new { Target = (MethodBase)update, Allowed = "BaseHourlyUpdate" },
                         new { Target = (MethodBase)baseHourly, Allowed = "UpdateBasesHourly" },
                         new { Target = (MethodBase)basesHourly, Allowed = HourlyCoroutine },
                     })
            {
                var callers = universe.Where(m => Program.Callees(m, game).Any(c => Same(c, pair.Target)))
                                      .Select(Describe).Distinct().ToList();
                // An iterator's body lives in a compiler-generated MoveNext, so the coroutine is matched by
                // NAME CONTAINMENT — <LevelHourlyUpdateCrt>d__NN.MoveNext is the real caller.
                var strays = callers.Where(n => n.IndexOf(pair.Allowed, StringComparison.Ordinal) < 0).ToList();
                if (callers.Count == 0)
                    yield return "L291 scan-empty: nothing in the geoscape assembly calls " + Describe(pair.Target) +
                                 " — the sweep cannot see the chain it is asserting about, so the green below it " +
                                 "is an artefact of a broken walk, not of a contained clock.";
                else if (strays.Count > 0)
                    yield return "L291 facility-clock-ungated: " + Describe(pair.Target) + " is reached from [" +
                                 string.Join(", ", strays) + "], outside the hourly chain " +
                                 "LevelHourlyUpdateCrt -> UpdateBasesHourly -> BaseHourlyUpdate -> UpdateFacility. " +
                                 "That chain is the ONLY reason a client cannot complete a facility off a mirrored " +
                                 "actor-clock stamp it has no epoch for (GeoPhoenixFacility.cs:366 vs " +
                                 "rail-baseline.txt:33/:418). A second entrance arms the same defect L290 fixed " +
                                 "for exploration — and it would mint a Functioning facility the host never built.";
            }

            // ── and the gate that contains that chain is still on it ─────────────────────────────────
            var patch = typeof(ClientSimGate).GetCustomAttributes(typeof(HarmonyPatch), false)
                                             .OfType<HarmonyPatch>()
                                             .Select(p => p.info)
                                             .FirstOrDefault(i => i != null && i.declaringType == typeof(GeoLevelController));
            if (patch == null || !string.Equals(patch.methodName, HourlyCoroutine, StringComparison.Ordinal))
                yield return "L291 hourly-gate-moved: ClientSimGate no longer prefixes " +
                             "GeoLevelController." + HourlyCoroutine + " (it names " +
                             (patch == null ? "no GeoLevelController method at all" : "'" + patch.methodName + "'") +
                             "). Every arm above is contingent on that one prefix: the moment the client runs the " +
                             "hourly tick, UpdateFacility:366 resolves a mirrored per-actor timestamp against a " +
                             "clock that never shared its zero, and the client completes constructions of its own.";
        }

        /// <summary>The game's own geoscape methods — the universe the caller sets quantify over. Scoped to
        /// the geoscape namespaces rather than the whole assembly because that is where every member of the
        /// family lives, and a whole-assembly IL walk buys nothing but minutes.</summary>
        private static List<MethodBase> GeoscapeMethods(Assembly game)
        {
            var all = new List<MethodBase>();
            Type[] types;
            try { types = game.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
            {
                var ns = t.Namespace;
                if (ns == null || ns.IndexOf("PhoenixPoint.Geoscape", StringComparison.Ordinal) != 0) continue;
                try
                {
                    foreach (var m in t.GetMethods(All)) if (m.DeclaringType == t) all.Add(m);
                    foreach (var c in t.GetConstructors(All)) all.Add(c);
                }
                catch { }
            }
            return all;
        }

        private static string Describe(MethodBase m) => (m.DeclaringType?.Name ?? "?") + "." + m.Name;

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
