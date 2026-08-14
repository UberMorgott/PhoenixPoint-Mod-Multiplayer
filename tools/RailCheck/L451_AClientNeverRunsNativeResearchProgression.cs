using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;

namespace RailCheck
{
    /// <summary>
    /// L451 — A CLIENT NEVER RUNS NATIVE RESEARCH PROGRESSION, INCLUDING WHEN THE SESSION IS UNKNOWN.
    ///
    /// RCA 2026-08-14: on a client, the first research to complete completed the ENTIRE queue. There is
    /// exactly one mechanism in the game that turns one completion into all of them, and it is native:
    /// <c>Research.Update</c>:654-660 hands the whole research wallet to
    /// <c>AddProgressToCurrentResearch(..., propagate: true)</c>, which walks <c>_researchQueue</c> passing
    /// leftover points down the line (Research.cs:736-748). On a client that method must never run — the
    /// element state and progress leaves are a pure mirror (rail-baseline.txt:460-466).
    ///
    /// The gate used to be a prefix on <c>Research.Update</c> itself, and it opened on
    /// <c>engine == null || !engine.IsActiveSession</c>. That is not a gate, it is a door with a timer: the
    /// funnel above it, <c>GeoFaction.UpdateResearch</c>, also restamps <c>NextResearchUpdate</c>
    /// (GeoFaction.cs:1416), which the rail deliberately does NOT mirror (rail-baseline.txt:544), so on a
    /// client that stamp is permanently stale and <c>CanUpdateResearch</c> (:1396-1407) is permanently
    /// satisfied. One tick in the unknown window — mid-load, mission boundary, a momentarily unset
    /// <c>Session.HostPeerId</c> — is all the cascade ever needed.
    ///
    /// So this law asserts the two things that make that impossible, and asserts the second one by RUNNING
    /// the gate rather than by reading it:
    ///   • the funnel is COMPLETE — <c>Research.Update</c> has exactly one caller in the game assembly,
    ///     <c>GeoFaction.UpdateResearch</c>, and the gate covers that method AND the
    ///     <c>GeoAlienFaction</c> override that calls it (GeoAlienFaction.cs:831-844);
    ///   • the gate is CLOSED BY DEFAULT — with a client engine whose session predicate reads false, the
    ///     prefix still refuses; with a host engine it still admits.
    ///
    /// Falsify: let the prefix open on <c>!IsActiveSession</c> → <c>gate-opens-on-unknown</c>; make it
    /// refuse on the host too → <c>host-gated</c>; drop either target → <c>gate-narrowed</c>; add a second
    /// caller of <c>Research.Update</c> → <c>funnel-leak</c>; re-add a mod prefix on <c>Research.Update</c>
    /// → <c>second-gate</c>.
    /// </summary>
    internal static class L451_AClientNeverRunsNativeResearchProgression
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var funnel = typeof(GeoFaction).GetMethod("UpdateResearch", All);
            var alienFunnel = typeof(GeoAlienFaction).GetMethod("UpdateResearch", All);
            var tick = typeof(Research).GetMethod("Update", All);
            var canUpdate = typeof(GeoFaction).GetMethod("CanUpdateResearch", All);
            var nextUpdate = typeof(GeoFaction).GetField("NextResearchUpdate", All);
            if (funnel == null || alienFunnel == null || tick == null || canUpdate == null || nextUpdate == null)
            {
                yield return "L451 premise-changed: one of GeoFaction.{UpdateResearch,CanUpdateResearch," +
                             "NextResearchUpdate}, GeoAlienFaction.UpdateResearch or Research.Update no longer " +
                             "resolves. The research progression funnel has changed shape — re-derive it from " +
                             "the decompile before assuming a client still cannot reach the cascade.";
                yield break;
            }

            if (!Program.Callees(funnel, game).Any(c => Same(c, tick)))
                yield return "L451 premise-changed: GeoFaction.UpdateResearch no longer calls Research.Update, " +
                             "so gating the funnel no longer gates the tick. Find where the tick moved before " +
                             "trusting the gate below it.";

            // ── the funnel is COMPLETE: exactly one door into the cascade ───────────────────────────────
            var universe = GeoscapeMethods(game);
            if (universe.Count < 200)
            {
                yield return "L451 scan-empty: the geoscape IL sweep found only " + universe.Count +
                             " method(s), so the caller set below is meaningless and this law would pass by " +
                             "seeing nothing.";
                yield break;
            }

            var callers = universe.Where(m => Program.Callees(m, game).Any(c => Same(c, tick)))
                                  .Select(Describe).Distinct().ToList();
            if (callers.Count == 0)
                yield return "L451 scan-empty: nothing in the geoscape assembly calls Research.Update — the " +
                             "sweep cannot see the funnel it is asserting about, so the green below it is an " +
                             "artefact of a broken walk.";
            else
            {
                var strays = callers.Where(n => n.IndexOf("UpdateResearch", StringComparison.Ordinal) < 0).ToList();
                if (strays.Count > 0)
                    yield return "L451 funnel-leak: Research.Update is reached from [" + string.Join(", ", strays) +
                                 "], outside GeoFaction.UpdateResearch. The client gate sits on the funnel, so a " +
                                 "second door lets the queue cascade run locally — one completed research would " +
                                 "complete every queued one (Research.cs:736-748).";
            }

            // ── the gate covers BOTH declarations ───────────────────────────────────────────────────────
            var targets = GateTargets();
            if (targets == null)
            {
                yield return "L451 gate-narrowed: ClientResearchGate.TargetMethods could not be invoked, so the " +
                             "set of methods the client gate actually patches is unknown. A gate whose targets " +
                             "cannot be read is not a gate.";
                yield break;
            }
            foreach (var required in new[] { funnel, alienFunnel })
                if (!targets.Any(t => Same(t, required)))
                    yield return "L451 gate-narrowed: ClientResearchGate no longer patches " + Describe(required) +
                                 ". GeoAlienFaction overrides UpdateResearch and completes SmallFlyerResearchDef " +
                                 "BEFORE delegating to base (GeoAlienFaction.cs:831-844), so both declarations " +
                                 "have to be covered or a client keeps writing research state of its own.";

            // ── and no SECOND gate on the same tick, which is what could disagree ───────────────────────
            var duplicates = typeof(ClientResearchGate).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).OfType<HarmonyPatch>()
                             .Any(p => p.info != null && p.info.declaringType == typeof(Research) &&
                                       string.Equals(p.info.methodName, "Update", StringComparison.Ordinal)))
                .Select(t => t.FullName).ToList();
            if (duplicates.Count > 0)
                yield return "L451 second-gate: [" + string.Join(", ", duplicates) + "] prefix Research.Update " +
                             "again. The whole point of moving the gate to GeoFaction.UpdateResearch was ONE " +
                             "verdict: two gates on the same tick can disagree, and the narrower one opening " +
                             "for a single frame is the original defect.";

            // ── CLOSED BY DEFAULT: run the prefix, do not read it ───────────────────────────────────────
            foreach (var v in GateBehaviour()) yield return v;
        }

        /// <summary>Invokes the production prefix with a synthetic engine. Uninitialized instances only —
        /// no ctor, no transport, no session: <c>IsActiveSession</c> reads false off <c>IsActive</c>, which is
        /// exactly the "unknown window" the RCA named.</summary>
        private static IEnumerable<string> GateBehaviour()
        {
            var prefix = typeof(ClientResearchGate).GetMethod("Prefix", All);
            var instance = typeof(NetworkEngine).GetProperty("Instance", All);
            var isHost = typeof(NetworkEngine).GetProperty("IsHost", All);
            if (prefix == null || instance?.GetSetMethod(true) == null || isHost?.GetSetMethod(true) == null)
            {
                yield return "L451 premise-changed: ClientResearchGate.Prefix, NetworkEngine.Instance or " +
                             "NetworkEngine.IsHost cannot be driven reflectively any more, so the behavioural " +
                             "arm below proves nothing.";
                yield break;
            }

            var saved = instance.GetValue(null);
            string clientVerdict = null, hostVerdict = null;
            try
            {
                var engine = (NetworkEngine)FormatterServices.GetUninitializedObject(typeof(NetworkEngine));
                instance.SetValue(null, engine);

                isHost.SetValue(engine, false);
                if ((bool)prefix.Invoke(null, new object[] { null }))
                    clientVerdict = "L451 gate-opens-on-unknown: ClientResearchGate.Prefix ADMITTED native " +
                                    "research progression for a peer that is not the host while IsActiveSession " +
                                    "read false. That window — mid-load, mission boundary, a momentarily unset " +
                                    "HostPeerId — is the entire bug: NextResearchUpdate is not mirrored " +
                                    "(rail-baseline.txt:544) so the stale stamp always passes CanUpdateResearch, " +
                                    "and one tick dumps the whole wallet through the queue (Research.cs:654-660, " +
                                    ":736-748). The gate must fail CLOSED.";

                isHost.SetValue(engine, true);
                if (!(bool)prefix.Invoke(null, new object[] { null }))
                    hostVerdict = "L451 host-gated: ClientResearchGate.Prefix REFUSED native research " +
                                  "progression on the host. The host is the only peer that may run it — a " +
                                  "closed gate there stops research for everyone, since every peer mirrors the " +
                                  "host's element state.";
            }
            finally { instance.SetValue(null, saved); }

            if (clientVerdict != null) yield return clientVerdict;
            if (hostVerdict != null) yield return hostVerdict;
        }

        private static List<MethodBase> GateTargets()
        {
            var m = typeof(ClientResearchGate).GetMethod("TargetMethods", All);
            if (m == null) return null;
            try { return ((IEnumerable<MethodBase>)m.Invoke(null, null)).Where(x => x != null).ToList(); }
            catch { return null; }
        }

        /// <summary>The game's own geoscape methods — the universe the caller set quantifies over.</summary>
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
