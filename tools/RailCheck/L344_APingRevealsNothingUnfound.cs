using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.UI;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L344 — A GEOSCAPE PING POINTS AT WHAT THE VIEWER HAS ALREADY FOUND, AND NEVER AT ANYTHING ELSE.
    ///
    /// THE OUTCOME: no site the LOCAL viewer's faction cannot see is ever named by a ping he sends, and no
    /// site the LOCAL viewer's faction cannot see is ever ringed by a ping that arrives. Both halves, and
    /// the second is the one that carries the leak: one player has scanned a region another has not, and if
    /// only the send side asks, a host who has explored ahead reveals every site he points at.
    ///
    /// THE DEFECT IT STANDS AGAINST, and it was a MAP SCANNER. The geoscape ping picks with the game's own
    /// hit test, <c>GeoscapeView.SelectAtCursor</c> — whose picking branch is <c>GeoMap.PickObjectOnPosition</c>
    /// (GeoMap.cs:806-815): a bare <c>Physics.Raycast</c> over <c>UnityLayers.Picking</c> returning
    /// <c>hitInfo.transform.GetComponentInParent&lt;GeoActor&gt;()</c>, WITH NO VISIBILITY FILTER OF ANY KIND.
    /// An unrevealed site answers that raycast exactly like a revealed one. So a player could hold the ping
    /// key and sweep the cursor over black ocean, and every press that produced a marker instead of the
    /// refusal click told him a point of interest was there — and told every other peer too, drawing a
    /// bright native ring around a site they had not earned. Nothing threw, nothing logged, and the ring is
    /// indistinguishable from a legitimate one. That the SIBLING branch of the very same method does filter
    /// (<c>selectWithProximation</c> → <c>where s.GetVisible(_context.ViewerFaction)</c>,
    /// GeoscapeView.cs:983-986) is what says the missing filter is an omission and not a design.
    ///
    /// WHY THE EXISTING PING LAWS ARE ALL GREEN THROUGH IT. L158 asks whether the seam alters STATE — a ping
    /// on an unrevealed site alters none; it only shows. L160 asks whether an arrival drives the camera,
    /// enters a state or moves a selection — it does none of those. L253 asks whether the marker lands on
    /// the globe's SURFACE rather than inside it, and a leaked one lands perfectly. L182 asks whether it was
    /// switched on, and it was. Every one of them is satisfied by a ping that works exactly as designed on
    /// a site the viewer was never supposed to know exists. Revelation is a fifth question and needed a
    /// fifth law.
    ///
    /// THE PREDICATE IS THE GAME'S OWN, WHICH IS ARM (c)'s WHOLE POINT. A site answers
    /// <c>GeoSite.GetVisible(GeoFaction)</c> (GeoSite.cs:387-390) — the per-faction flag the game's own
    /// <c>GeoSite.Selectable</c> gates on with this exact viewer (GeoSite.cs:179-186) — asked about
    /// <c>GeoLevelController.ViewerFaction</c> (:209), which is what makes the answer LOCAL on each peer. A
    /// craft answers <c>GeoVehicle.IsVisible</c> (:174-187, written per viewer at :606-613). A mod-side
    /// cache of "sites I have seen" would satisfy arms (a) and (b) and be wrong the first time the game
    /// revealed one without telling us.
    ///
    /// ARMS
    ///   (a) <c>send-side-scans-the-map</c> — <c>Capture</c> must consult the predicate. Without it the
    ///       presser's own screen is the scanner, which is the half a player can farm at will.
    ///   (b) <c>arrival-reveals-the-unfound</c> — <c>ShowGeo</c> must consult it too, about ITS OWN viewer.
    ///       The load-bearing arm: peers legitimately disagree about what is revealed, so the sender's
    ///       answer is not the receiver's and may not be spent as it.
    ///   (c) <c>predicate-is-not-the-games</c> — the predicate must reach <c>GeoSite.GetVisible</c>,
    ///       <c>GeoLevelController.get_ViewerFaction</c> and <c>GeoVehicle.get_IsVisible</c>, i.e. ask the
    ///       game per viewer rather than remember an answer.
    ///   (d) <c>every-actor-is-a-secret</c> — EXECUTED, and the arm that keeps this law from being satisfied
    ///       by refusing everything. <c>Known(null, null)</c> must be TRUE: a scanner, an ancient-site probe
    ///       and a behemoth carry no per-faction visibility flag to ask about (a submerged behemoth switches
    ///       its whole <c>VisualsRoot</c> off, GeoBehemothActor.cs:584/:609, taking its picking collider with
    ///       it), so they pass. A law with only arms (a)-(c) is greenest when the predicate is
    ///       <c>return false;</c> and the ping feature is dead.
    ///   (e) <c>control-not-red</c> — POSITIVE CONTROL. A seam that pings whatever the raycast found and
    ///       draws whatever arrives must raise (a) and (b). Reachability arms whose scan resolves no call
    ///       edges report every gate as present; that is exactly how the ping in L253 stayed green while
    ///       every marker it drew was inside the planet.
    ///
    /// Falsify. The first three were RUN on 2026-08-09, each verified RED against a real build and then
    /// restored; the last two are stated, not executed, and are marked so rather than implied:
    ///   • VERIFIED RED — <c>var seen = pick.Actor != null;</c> (drop the <c>Known</c> call from
    ///     <c>Capture</c>) → <c>send-side-scans-the-map</c>
    ///   • VERIFIED RED — <c>if (false)</c> in place of the <c>ShowGeo</c> gate, i.e. the leak reintroduced
    ///     exactly as it shipped → <c>arrival-reveals-the-unfound</c>
    ///   • VERIFIED RED — <c>return vehicle != null &amp;&amp; vehicle.IsVisible;</c> (the default flipped to
    ///     deny) → <c>every-actor-is-a-secret</c>. Note the near-miss that does NOT falsify it: an early
    ///     <c>if (actor != null || geo != null) return false;</c> leaves the null/null path untouched and
    ///     the arm stays green. The arm measures the DEFAULT branch, nothing else.
    ///   • not run — <c>Known =&gt; _seenBefore.Contains(actor)</c> (a mod-side cache) →
    ///     <c>predicate-is-not-the-games</c>
    ///   • not run — rename/move <c>Known</c>, <c>GeoSite.GetVisible</c> or <c>GeoVehicle.IsVisible</c> →
    ///     <c>premise-changed</c>
    /// </summary>
    internal static class L344_APingRevealsNothingUnfound
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(PingMarkers);
            var known = seam.GetMethod("Known", AllMembers);
            var getVisible = typeof(GeoSite).GetMethod("GetVisible", AllMembers);
            var isVisible = typeof(GeoVehicle).GetProperty("IsVisible", AllMembers)?.GetGetMethod(true);
            var viewer = typeof(GeoLevelController).GetProperty("ViewerFaction", AllMembers)?.GetGetMethod(true);

            if (known == null || getVisible == null || isVisible == null || viewer == null ||
                seam.GetMethod("Capture", AllMembers) == null ||
                seam.GetMethod("ShowGeo", AllMembers) == null)
            {
                yield return "L344 premise-changed: one of PingMarkers.Known/Capture/ShowGeo (the gate and " +
                             "the two doors it gates), GeoSite.GetVisible (the game's per-faction reveal " +
                             "flag, GeoSite.cs:387), GeoVehicle.IsVisible (:174) or " +
                             "GeoLevelController.ViewerFaction (:209) no longer resolves. Every arm below is " +
                             "asserting about a shape the build no longer has.";
                yield break;
            }

            foreach (var v in Scan(seam, "PingMarkers")) yield return v;

            // ── arm (c): the gate must ask the GAME, per viewer, and not a memory of its own.
            // ONE HOP THROUGH THE SEAM'S OWN HELPERS, and the hop is not a loophole — it is forced by arm (d).
            // GeoVehicle.IsVisible is `VisualsRoot.activeInHierarchy`, a one-line wrapper over an ECALL, and
            // under -c Release the JIT inlines it into whatever calls it — which makes THAT method impossible
            // to compile outside the player (L113's landmine). So the gate physically cannot both call it
            // directly and be Invoke-able by arm (d); the engine question has to sit behind a NoInlining
            // one-liner. A one-level IL walk read that containment as "the gate stopped asking the game" and
            // turned red on the fix for its own sibling arm. The hop is scoped to methods DECLARED ON THE SEAM
            // ITSELF, so the question still cannot be laundered through a cache, the rail, or the sender.
            var asksSite = ReachesVia(known, seam, "GeoSite", "GetVisible");
            var asksViewer = ReachesVia(known, seam, "GeoLevelController", "get_ViewerFaction");
            var asksCraft = ReachesVia(known, seam, "GeoVehicle", "get_IsVisible");
            if (!asksSite || !asksViewer || !asksCraft)
                yield return "L344 predicate-is-not-the-games: PingMarkers.Known must answer out of the " +
                             "game's own per-faction state — GeoSite.GetVisible (GeoSite.cs:387-390) asked " +
                             "about GeoLevelController.ViewerFaction (:209), and GeoVehicle.IsVisible " +
                             "(:174-187) for a craft. Found site=" + asksSite + ", viewer=" + asksViewer +
                             ", craft=" + asksCraft + ". A gate that consults anything else — a set of ids " +
                             "the mod has seen, a flag the rail carries, the SENDER's answer — is wrong the " +
                             "first time the game reveals a site without telling us, and wrong silently: it " +
                             "draws a ring around a site this peer has not found and logs nothing.";

            // ── arm (d): EXECUTED. Neither branch dereferences its arguments on this path, so it runs
            // outside a Unity runtime — which is the whole reason the gate was written with two `as` casts
            // and a default rather than a type switch that would have needed a live level to answer.
            var passes = false;
            string threw = null;
            try { passes = (bool)known.Invoke(null, new object[] { null, null }); }
            catch (Exception e) { threw = (e.InnerException ?? e).GetType().Name; }

            if (threw != null)
            {
                yield return "L344 every-actor-is-a-secret: PingMarkers.Known(null, null) threw " + threw +
                             " — the gate now dereferences something on the path an actor that is neither a " +
                             "GeoSite nor a GeoVehicle takes, so a GeoScanner, a GeoAncientSiteProbe or a " +
                             "GeoBehemothActor ping throws into the packet drain instead of being drawn.";
                yield break;
            }
            if (!passes)
                yield return "L344 every-actor-is-a-secret: PingMarkers.Known refuses an actor that is " +
                             "neither a GeoSite nor a GeoVehicle, and those three (GeoScanner, " +
                             "GeoAncientSiteProbe, GeoBehemothActor) carry no per-faction visibility flag to " +
                             "refuse them on — a submerged behemoth already switches its VisualsRoot off " +
                             "(GeoBehemothActor.cs:584/:609) and takes its picking collider with it. This " +
                             "arm exists because arms (a)-(c) are all satisfied by `Known => false`, which " +
                             "closes the leak by deleting the feature.";

            // ── arm (e): the reachability arms above must be able to say NO.
            var control = Scan(typeof(FakeSeam), "FakeSeam").ToList();
            foreach (var want in new[] { "send-side-scans-the-map", "arrival-reveals-the-unfound" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L344 control-not-red: FakeSeam pings whatever the raycast returned and " +
                                 "rings whatever arrives, consulting no visibility gate at all, and the scan " +
                                 "did not flag " + want + ". A must-reach arm whose IL walk resolves no call " +
                                 "edges reports every gate as present.";
        }

        /// <summary>Arms (a) and (b), over whichever type is handed in — the real seam, or the control.</summary>
        private static IEnumerable<string> Scan(Type seam, string label)
        {
            var capture = seam.GetMethod("Capture", AllMembers);
            var showGeo = seam.GetMethod("ShowGeo", AllMembers);
            if (capture == null || showGeo == null)
            {
                yield return "L344 send-side-scans-the-map: " + label + " has no Capture/ShowGeo pair at all.";
                yield return "L344 arrival-reveals-the-unfound: " + label + " has no Capture/ShowGeo pair at all.";
                yield break;
            }

            if (!Reaches(capture, null, "Known"))
                yield return "L344 send-side-scans-the-map: " + label + ".Capture reaches no visibility " +
                             "gate, so the geoscape hit test it picks with — GeoMap.PickObjectOnPosition " +
                             "(GeoMap.cs:806-815), a raw Physics.Raycast on UnityLayers.Picking with no " +
                             "filter — hands it unrevealed sites exactly like revealed ones. The ping key " +
                             "then works as a map scanner: every press that draws a marker instead of " +
                             "playing the refusal click says a point of interest is under the cursor.";

            if (!Reaches(showGeo, null, "Known"))
                yield return "L344 arrival-reveals-the-unfound: " + label + ".ShowGeo reaches no visibility " +
                             "gate, so it rings whatever the sender named regardless of whether THIS peer's " +
                             "faction has found it. The send-side gate cannot stand in for this one: the " +
                             "whole point of a shared campaign is that one player has scanned a region " +
                             "another has not, so a host who has explored ahead reveals every site he " +
                             "points at, in a native ring indistinguishable from a legitimate one.";
        }

        /// <summary>ARM (e). Never instantiated, never registered — it exists only to be walked. This is the
        /// shipped code as it stood before 2026-08-09: pick, encode, resolve, ring, no question asked.</summary>
        private sealed class FakeSeam
        {
            internal static string Capture(GeoActor picked)
            {
                return picked == null ? null : picked.name;
            }

            internal static void ShowGeo(GeoActor actor)
            {
                Debug.Log("ringed " + actor.name + " without asking whether this peer has found it");
            }
        }

        // ─── IL helpers (same primitives as L153/L158/L160/L182/L250/L252/L253; Program.cs is not partial) ──

        private static bool Reaches(MethodBase caller, string declaringType, string calleeName)
            => CalleesOf(caller).Any(c => c.Name == calleeName &&
                                          (declaringType == null || c.DeclaringType?.Name == declaringType));

        /// <summary><see cref="Reaches"/> plus ONE hop through a callee declared on <paramref name="seam"/>
        /// itself. Deliberately not a full transitive closure: the arm asserts "this gate asks the game", and
        /// a walk that wandered off the seam would let any helper anywhere satisfy it.</summary>
        private static bool ReachesVia(MethodBase caller, Type seam, string declaringType, string calleeName)
            => Reaches(caller, declaringType, calleeName) ||
               CalleesOf(caller).Any(c => c.DeclaringType == seam && Reaches(c, declaringType, calleeName));

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null) yield return c;
            }
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
