using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L270 — A PLAYER ABILITY MAY NOT WRITE A RAIL-COVERED ROOT ON A CLIENT, WHICHEVER ROOT IT IS.
    ///
    /// THE REPORT (2026-08-08). On a client the owner flew to a haven, pressed STEAL AN AIRCRAFT, and the
    /// squad screen said the aircraft was "busy with something else". The host, 200 ms earlier:
    /// <c>S#79 — launch: that site has no runnable mission any more</c> → <c>HOST mission REJECT peer=1</c>.
    /// Then the client repainted <c>[MP][site] repaint S#79 … activeMission=StealAircraftAN_CustomMissionTypeDef</c>
    /// — a mission the host did not have — and the backstop sealed it: <c>CRC backstop: root 'S#79' DIVERGED
    /// on peer 1 (host A3B2BD63 != client 2B848FC9)</c>.
    ///
    /// <c>StealAircraftAbility.ActivateInternal</c>:92 → <c>GeoHaven.PrepareHavenMission</c> →
    /// <c>Site.SetActiveMission</c> (GeoHaven.cs:1091). A structural create on the CLIENT'S OWN graph, which
    /// the host-now-vs-host-before diff can never mention again.
    ///
    /// WHY L42 WAS GREEN THROUGH ALL OF IT, AND WHY THAT IS THIS LAW'S SUBJECT. L42 sweeps exactly this class
    /// of defect — an ability callee that writes rail-covered state with no seam — and it had been green for
    /// weeks. Its receiver filter read <c>c.DeclaringType == vehicle</c>. Its own comment said the gesture set
    /// was "DISCOVERED, never declared". Both were true and the law was still blind, because A FILTER THAT
    /// NAMES ONE TYPE IS THE DECLARATION: it declared the haven, the site and the faction out of existence.
    /// L42's filter is now <see cref="Program.RailCoveredRoots"/> and this law is what keeps it that wide —
    /// the arm that would otherwise be silently narrowed back by the next person who wants a green run.
    ///
    ///   (a) <c>receiver-set-narrowed</c> — <see cref="Program.RailCoveredRoots"/> still names all four roots.
    ///       Drop one and every ability gesture on it becomes invisible again, exactly as the haven was.
    ///   (b) <c>funnel-undiscovered</c> — the sweep still REACHES <c>GeoHaven.PrepareHavenMission</c> from a
    ///       shipped ability. If the discovery stops finding the very call that cost this session, nothing
    ///       below it means anything.
    ///   (c) <c>mutator-ungated</c> — every callee the sweep reaches is covered, judged by
    ///       <see cref="Program.CoveredGesture"/>: our own prefix, or every root method it funnels into
    ///       carrying one. Reads are not gestures — <c>GeoHaven.GetAvailableStealAircraftMission</c> returns
    ///       and writes nothing, and gating it would empty the modal the player picks the mission from.
    ///   (d) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam.MintsAMission"/> calls
    ///       <c>GeoSite.SetActiveMission</c> with no prefix anywhere near it; arm (c)'s own judge is run over
    ///       it and MUST come back uncovered.
    ///
    /// Falsify (each verified RED, then restored): drop <c>GeoHaven</c> from <c>RailCoveredRoots</c> → (a) and
    /// (b); delete the <c>HavenMissionGate</c> prefix → (c), twice over (<c>PrepareHavenMission</c> AND the
    /// <c>PrepareDummyMissions</c> wrapper that funnels into it, which is the point of the funnel clause);
    /// make <see cref="FakeSeam.MintsAMission"/> an empty body → (d).
    /// </summary>
    internal static class L270_AbilityMutatorSweep
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var game = typeof(GeoHaven).Assembly;
            var roots = Program.RailCoveredRoots();
            var prepare = typeof(GeoHaven).GetMethod("PrepareHavenMission", All);
            var activations = Program.AbilityActivations(game);

            if (prepare == null || activations.Count == 0)
            {
                yield return "L270 premise-changed: " +
                             (prepare == null
                                 ? "GeoHaven.PrepareHavenMission no longer resolves"
                                 : "no concrete GeoAbility declares ActivateInternal") +
                             " — the discovery rule no longer matches the game, so this law is asleep and every " +
                             "ability gesture on every rail-covered root is unchecked. That is precisely the " +
                             "state L42 was in on 2026-08-08, and it cost a session.";
                yield break;
            }

            // ── (a) the receiver set is still the whole covered graph ───────────────────────────────
            foreach (var t in new[] { typeof(GeoVehicle), typeof(GeoSite), typeof(GeoHaven), typeof(GeoFaction) })
                if (Array.IndexOf(roots, t) < 0)
                    yield return "L270 receiver-set-narrowed: Program.RailCoveredRoots no longer names " +
                                 t.Name + ". The sweep's receiver filter IS its declaration of what counts as " +
                                 "shared state — narrow it and every player gesture on that root goes invisible " +
                                 "while the law reports green, which is exactly how a client came to mint its own " +
                                 "StealAircraft mission and then ask the host to launch it.";

            // ── the sweep itself: THE SAME discovery L42 runs, not a second copy of it ──────────────
            var reached = Program.AbilityGestures(game, roots);

            // ── (b) it still finds the call the report was about ────────────────────────────────────
            if (!reached.Values.Any(m => m.MetadataToken == prepare.MetadataToken))
                yield return "L270 funnel-undiscovered: the ability sweep no longer reaches " +
                             "GeoHaven.PrepareHavenMission, the single call that writes Site.SetActiveMission " +
                             "(GeoHaven.cs:1091) for steal-aircraft AND for every haven infiltration " +
                             "(HavenFacilityController:110, HavenInteractionController:219). It is reachable ONLY " +
                             "through StealAircraftAbility's modal CALLBACK (:86-95), so a discovery that stops " +
                             "following an ability into its own closures loses it entirely — which is half of why " +
                             "L42 was green while a client minted its own mission.";

            // ── (c) every reached mutator is covered ────────────────────────────────────────────────
            var covered = Program.OurPrefixTargets();
            foreach (var kv in reached)
                if (!Program.CoveredGesture(kv.Value, game, roots, covered))
                    yield return "L270 mutator-ungated: " + kv.Key + " is reached from a GeoAbility." +
                                 "ActivateInternal and it writes a rail-covered root, but nothing of ours " +
                                 "prefixes it and it funnels into nothing of ours either. On a client that runs " +
                                 "LOCALLY and the host-now-vs-host-before diff can never correct it — the peer " +
                                 "ends up holding state the host has never heard of, which is a CRC divergence " +
                                 "wearing a feature's clothes. Give it a gate or an intent.";

            // ── (d) POSITIVE CONTROL, executed: the same judge over a seam that really is uncovered ──
            if (Program.CoveredGesture(typeof(FakeSeam).GetMethod("MintsAMission", All), game, roots, covered))
                yield return "L270 control-not-red: FakeSeam.MintsAMission calls GeoSite.SetActiveMission with no " +
                             "prefix of ours anywhere, and CoveredGesture called it covered. Arm (c) is " +
                             "decorative — it would stay green over the exact write that started this.";
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the shape of the defect — an ungated call that wires a mission
            /// onto a site. Never executed; only its IL is read. The judge MUST call this uncovered.</summary>
            internal static void MintsAMission(GeoSite site, GeoMission mission) => site.SetActiveMission(mission);
        }
    }
}
