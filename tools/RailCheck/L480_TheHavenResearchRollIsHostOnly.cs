using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L480 — THE HAVEN-RESEARCH ROLL IS HOST-ONLY, SO A LANDED AssignedResearchId IS NEVER RE-ROLLED AWAY.
    ///
    /// Live session 2026-08-14: the host's CRC backstop reported roots <c>S#75</c>/<c>S#76</c> STILL
    /// diverged after a forced re-emit, and the client named
    /// <c>SerializationData.HavenData.AssignedResearchId</c> as the candidate. A forced re-emit provably
    /// cannot win that argument, because the client OVERWRITES the leaf locally after it lands:
    /// <c>GeoHavenResearchManager.UpdateFactionHavensResearch</c> (GeoHavenResearchManager.cs:66-99) first
    /// nulls <c>AssignedResearch</c> on every haven of the faction (:73) and then re-pairs havens with
    /// research using an UNSEEDED local RNG — two <c>GetRandomElement()</c> draws per pairing (:91/:94). The
    /// diff is host-now vs host-before, so once the client has re-rolled there is nothing left on the host
    /// to re-state. And the client reaches it constantly: level start (GeoPhoenixFaction.cs:341, right after
    /// the save transfer), every completed research (:1069) and every first haven visit (:1168) — all three
    /// run on a client because its sim clock is deliberately not frozen (ClientSimGate).
    ///
    /// So this law asserts the three things that make the overwrite impossible, and asserts the last one by
    /// RUNNING the gate rather than by reading it:
    ///   • the PREMISE still holds — the funnel resolves and still draws from the unseeded RNG, i.e. the
    ///     reason this must be host-only has not been engineered away underneath us;
    ///   • the FUNNEL is COMPLETE — nothing outside <c>GeoHavenResearchManager</c> (the roll) and
    ///     <c>GeoHaven</c> (its own save-restore) writes <c>GeoHaven.AssignedResearch</c>, so one gate on
    ///     the roll is the whole door. One gate, never one per caller: two gates on one assignment can
    ///     disagree, and the disagreement is the bug (the argument ResearchSync already records for
    ///     ClientResearchGate);
    ///   • the GATE is BOUND and CLOSED — <c>HavenResearchGate</c> patches exactly that method, refuses on
    ///     a peer that is not the host, and admits on the host.
    ///
    /// Falsify: make the prefix return true for a client → <c>gate-opens</c>; make it refuse on the host
    /// → <c>host-gated</c>; point <c>TargetMethod</c> elsewhere or let the name drift → <c>gate-unbound</c>;
    /// let a second type outside the manager write <c>AssignedResearch</c> → <c>funnel-leak</c>.
    /// </summary>
    internal static class L480_TheHavenResearchRollIsHostOnly
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var funnel = typeof(GeoHavenResearchManager).GetMethod(
                "UpdateFactionHavensResearch", All, null,
                new[] { typeof(GeoFaction), typeof(IEnumerable<GeoHaven>) }, null);
            var assigned = typeof(GeoHaven).GetField("AssignedResearch", All);
            if (funnel == null || assigned == null)
            {
                yield return "L480 premise-changed: GeoHavenResearchManager.UpdateFactionHavensResearch" +
                             "(GeoFaction, IEnumerable<GeoHaven>) or GeoHaven.AssignedResearch no longer " +
                             "resolves. The haven-research assignment has changed shape — re-derive the funnel " +
                             "from the decompile before assuming a client still cannot re-roll it.";
                yield break;
            }

            if (!Program.Callees(funnel, game).Any(
                    c => string.Equals(c?.Name, "GetRandomElement", StringComparison.Ordinal)))
                yield return "L480 premise-changed: UpdateFactionHavensResearch no longer draws from " +
                             "GetRandomElement. The whole reason this must be host-only is that the pairing is " +
                             "an UNSEEDED local roll no re-emit can survive (GeoHavenResearchManager.cs:91/:94). " +
                             "If the roll became deterministic, re-argue the gate instead of keeping it by " +
                             "inertia.";

            // ── the funnel is COMPLETE: one door into the assignment ────────────────────────────────────
            var universe = GeoscapeMethods(game);
            if (universe.Count < 200)
            {
                yield return "L480 scan-empty: the geoscape IL sweep found only " + universe.Count +
                             " method(s), so the writer set below is meaningless and this law would pass by " +
                             "seeing nothing.";
                yield break;
            }

            var writers = universe.Where(m => Program.FieldRefs(m, OpCodes.Stfld).Any(f => f == assigned))
                                  .Select(m => m.DeclaringType)
                                  .Where(t => t != null).Distinct().ToList();
            if (writers.Count == 0)
                yield return "L480 scan-empty: nothing in the geoscape assembly writes GeoHaven." +
                             "AssignedResearch — the sweep cannot see the field it is asserting about, so the " +
                             "green below it is an artefact of a broken walk.";
            else
            {
                var strays = writers.Where(t => !Owned(t)).Select(t => t.FullName).ToList();
                if (strays.Count > 0)
                    yield return "L480 funnel-leak: GeoHaven.AssignedResearch is written from [" +
                                 string.Join(", ", strays) + "], outside GeoHavenResearchManager (the roll) and " +
                                 "GeoHaven (its own save-restore). HavenResearchGate sits on the roll, so a " +
                                 "second door lets a client mint an assignment of its own again and the host's " +
                                 "AssignedResearchId is overwritten the moment it lands.";
            }

            // ── the gate is BOUND to that exact method ──────────────────────────────────────────────────
            MethodBase target = null;
            try { target = HavenResearchGate.TargetMethod(); } catch { }
            if (target == null || !Same(target, funnel))
                yield return "L480 gate-unbound: HavenResearchGate.TargetMethod resolves to " +
                             (target == null ? "null" : target.DeclaringType?.Name + "." + target.Name) +
                             ", not GeoHavenResearchManager.UpdateFactionHavensResearch. A null handle is not a " +
                             "gate — PatchAll turns it into one swallowed warning (L23) and the client re-rolls " +
                             "haven research in silence.";

            // ── CLOSED BY DEFAULT: run the prefix, do not read it ───────────────────────────────────────
            foreach (var v in GateBehaviour()) yield return v;
        }

        /// <summary>Invokes the production prefix with a synthetic engine. Uninitialized instances only —
        /// no ctor, no transport, no session, so <c>IsActiveSession</c> reads false off <c>IsActive</c>: the
        /// gate is driven in exactly the unknown window a mid-load client sits in.</summary>
        private static IEnumerable<string> GateBehaviour()
        {
            var prefix = typeof(HavenResearchGate).GetMethod("Prefix", All);
            var instance = typeof(NetworkEngine).GetProperty("Instance", All);
            var isHost = typeof(NetworkEngine).GetProperty("IsHost", All);
            var isActive = typeof(NetworkEngine).GetProperty("IsActive", All);
            if (prefix == null || instance?.GetSetMethod(true) == null || isHost?.GetSetMethod(true) == null ||
                isActive?.GetSetMethod(true) == null)
            {
                yield return "L480 premise-changed: HavenResearchGate.Prefix, NetworkEngine.Instance, " +
                             "NetworkEngine.IsActive or NetworkEngine.IsHost cannot be driven reflectively any " +
                             "more, so the behavioural arm below proves nothing.";
                yield break;
            }

            var saved = instance.GetValue(null);
            string clientVerdict = null, hostVerdict = null;
            try
            {
                var engine = (NetworkEngine)FormatterServices.GetUninitializedObject(typeof(NetworkEngine));
                instance.SetValue(null, engine);
                // LIVE engine, no Session: IsActiveSession still reads false, i.e. the unknown window right
                // after the save transfer — but the engine is UP, which is what separates a real client from
                // a Shutdown() husk in front of a solo campaign (ClientAuthority's IsActive term).
                isActive.SetValue(engine, true);

                isHost.SetValue(engine, false);
                if ((bool)prefix.Invoke(null, new object[] { null }))
                    clientVerdict = "L480 gate-opens: HavenResearchGate.Prefix ADMITTED the haven-research roll " +
                                    "on a peer that is not the host while IsActiveSession read false — the " +
                                    "UNKNOWN window right after a save transfer, which is the client's very " +
                                    "first reach into this funnel (GeoPhoenixFaction.cs:341). That roll nulls " +
                                    "every haven's " +
                                    "AssignedResearch and re-pairs from an unseeded RNG " +
                                    "(GeoHavenResearchManager.cs:73/:91/:94), so the host's mirrored " +
                                    "AssignedResearchId is overwritten the moment it lands and no re-emit can " +
                                    "ever heal it — the S#75/S#76 CRC backstop, verbatim.";

                isHost.SetValue(engine, true);
                if (!(bool)prefix.Invoke(null, new object[] { null }))
                    hostVerdict = "L480 host-gated: HavenResearchGate.Prefix REFUSED the haven-research roll on " +
                                  "the host. The host is the only peer that may run it — a closed gate there " +
                                  "means no haven is ever assigned research at all, on any peer, since every " +
                                  "peer mirrors the host's AssignedResearchId.";
            }
            finally { instance.SetValue(null, saved); }

            if (clientVerdict != null) yield return clientVerdict;
            if (hostVerdict != null) yield return hostVerdict;
        }

        /// <summary>The two types allowed to write the field: the manager that rolls it (its compiler-
        /// generated closures included — the null pass at :73 is a lambda) and GeoHaven's own save-restore
        /// (SetAssignedResearchFromInstanceData:1400-1407, which replays recorded instance data, not a roll).
        /// </summary>
        private static bool Owned(Type t)
        {
            for (var cur = t; cur != null; cur = cur.DeclaringType)
                if (cur == typeof(GeoHavenResearchManager) || cur == typeof(GeoHaven)) return true;
            return false;
        }

        /// <summary>The game's own geoscape methods — the universe the writer set quantifies over.</summary>
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

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
