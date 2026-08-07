using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using Multiplayer.UI;
using PhoenixPoint.Geoscape.View;
using UnityEngine;
using UnityEngine.UI;

namespace RailCheck
{
    /// <summary>
    /// L182 — A MARKER THAT WAS "SHOWN" IS ACTUALLY SWITCHED ON, AND A BUTTON THAT IS NOT A BUTTON DOES NOT
    /// EAT THE MAP.
    ///
    /// TWO SURFACES, ONE BUG SHAPE: presentation code that RETURNS NORMALLY having drawn nothing, or having
    /// left an invisible rect behind. Neither is reachable by L158 — no native call was blocked, no
    /// <c>[SerializeMember]</c> leaf was written, nothing was thrown — and neither is reachable by L160,
    /// which only says a ping does not DRIVE. Both were green through the whole of the reported defect.
    ///
    /// WHAT ACTUALLY HAPPENED (2026-08-07/08). Pings were audible and invisible. The cue is DOWNSTREAM of
    /// the marker (<c>PingMarkers.Track</c> ends in <c>Cue()</c> and is only reached after
    /// <c>AddMarker</c> returned), so the sound PROVED the draw call had run and produced nothing.
    /// <c>GeoscapeGlobeMarkers.AddMarker</c> has two branches and only the POOLED one calls
    /// <c>gameObject.SetActive(true)</c> (GeoscapeGlobeMarkers.cs:72 point / :101 actor); the
    /// FRESH-INSTANTIATE branch (:62-66) never does. <c>SitePointOfInterest</c> has no pooled instance
    /// waiting when a ping lands, so every ping got a marker that was never switched on — and
    /// <c>AddMarker</c> returned it happily. The game itself never falls in: it is that method's only other
    /// caller and it activates by hand at every one of its own instantiate sites
    /// (<c>AncientSiteProbeAbilityView.cs:88</c>, <c>UIStateVehicleSelected.cs:728</c>,
    /// <c>TacticalActorViewBase.cs:449</c>).
    ///
    /// THE ARMS:
    ///   (a) <c>ping-borrows-a-pooled-marker</c> — <see cref="PingMarkers"/> reaches
    ///       <c>GeoscapeGlobeMarkers.AddMarker</c> nowhere. Not style: a pooled marker is handed back to the
    ///       game for re-use, so its lifetime is not ours to end and its colour is not ours to write. The
    ///       ban is what keeps arm (b) meaningful — you cannot activate what you did not instantiate.
    ///   (b) <c>ping-marker-not-switched-on</c> — the OUTCOME arm, and the one that would have been red
    ///       through the defect. Every method of <see cref="PingMarkers"/> calls <c>SetActive</c> at least
    ///       as many times as it calls <c>Object.Instantiate</c>. Counted, not merely present, because
    ///       <c>ShowTac</c> instantiates on two branches and one activation would leave the other silent.
    ///   (c) <c>ping-has-no-owner-colour</c> — the local door and the network door are still distinct
    ///       (<c>PingMarkers.Update</c> reaches <c>ShowLocal</c> and not <c>Show</c>), and BOTH renderers
    ///       reach <see cref="PingMarkers"/>'s tint. Nothing on the wire says who sent a ping, so if
    ///       <c>Update</c> is ever pointed back at <c>Show</c> every ping silently becomes somebody else's
    ///       colour and no other arm notices.
    ///   (d) <c>ready-button-face-not-built</c> — <c>TacticalReadyButton.TrimRaycast</c> reaches BOTH
    ///       <c>Graphic.set_raycastTarget</c> and <c>Selectable.set_targetGraphic</c>. The version that
    ///       shipped the defect reached neither on the path it actually took: it read
    ///       <c>targetGraphic</c>, found the prefab had named none, logged, and returned with the whole
    ///       cloned footprint still raycastable over the tactical map — where
    ///       <c>TacticalView.IsCursorOverGUI()</c> gates the confirm click, so it ate them. Building the
    ///       face instead of hoping the asset named one is what makes the give-up branch unreachable.
    ///   (e) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> below borrows a pooled marker, instantiates
    ///       without activating, and trims nothing. (a), (b) and (d) must all go red on it, or their green
    ///       above is a scan that resolved no call edges and would pass forever.
    ///
    /// Falsify: put <c>markers.AddMarker(...)</c> back into <c>ShowGeo</c> → (a); drop the
    /// <c>go.SetActive(true)</c> after either <c>Instantiate</c> → (b); point <c>Update</c> at <c>Show</c>,
    /// or stop calling <c>Tint</c> from either shower → (c); restore <c>TrimRaycast</c>'s
    /// "no targetGraphic, leave it alone" early return → (d); empty <see cref="FakeSeam"/> → (e).
    /// </summary>
    internal static class L182_ShownMeansSwitchedOn
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(PingMarkers);
            var update = seam.GetMethod("Update", AllMembers);
            var showLocal = seam.GetMethod("ShowLocal", AllMembers);
            var show = seam.GetMethod("Show", AllMembers);
            var showGeo = seam.GetMethod("ShowGeo", AllMembers);
            var showTac = seam.GetMethod("ShowTac", AllMembers);
            var tint = seam.GetMethod("Tint", AllMembers);
            var trim = typeof(TacticalReadyButton).GetMethod("TrimRaycast", AllMembers);

            // The banned/required GAME members must resolve, or the arms below are assertions about a shape
            // this build does not have. AddMarker in particular: arm (a) bans a method by name, and a game
            // patch that renamed it would turn the ban green while the hole it names is wide open.
            var globe = typeof(GeoscapeGlobeMarkers);
            var premises = new MemberInfo[]
            {
                globe.GetMethod("AddMarker", AllMembers, null, new[] { typeof(Vector3), typeof(GlobeMarkerType), typeof(float) }, null),
                globe.GetMethod("AddMarker", AllMembers, null, new[] { typeof(PhoenixPoint.Geoscape.Entities.GeoActor), typeof(GlobeMarkerType), typeof(float) }, null),
                globe.GetMethod("GetMarkerPrefab", AllMembers),
                typeof(Graphic).GetProperty("raycastTarget", AllMembers)?.GetSetMethod(true),
                typeof(Selectable).GetProperty("targetGraphic", AllMembers)?.GetSetMethod(true),
            };

            if (update == null || showLocal == null || show == null || showGeo == null || showTac == null ||
                tint == null || trim == null || premises.Any(m => m == null))
            {
                yield return "L182 premise-changed: one of PingMarkers.Update/Show/ShowLocal/ShowGeo/ShowTac/" +
                             "Tint, TacticalReadyButton.TrimRaycast, GeoscapeGlobeMarkers.AddMarker (either " +
                             "overload) / GetMarkerPrefab, Graphic.set_raycastTarget or " +
                             "Selectable.set_targetGraphic no longer resolves. The seams this law is written " +
                             "over, or the members it names, have moved — every arm below is asserting about " +
                             "a shape the build no longer has.";
                yield break;
            }

            foreach (var v in ScanPing(seam, "PingMarkers")) yield return v;
            foreach (var v in ScanTrim(trim, "TacticalReadyButton.TrimRaycast")) yield return v;

            // ── arm (c): the two doors, and the colour they exist for.
            if (!Reaches(update, null, "ShowLocal") || Reaches(update, null, "Show"))
                yield return "L182 ping-has-no-owner-colour: PingMarkers.Update must send its own ping through " +
                             "ShowLocal and never through Show. The packet carries no sender identity — by " +
                             "design, so it stays byte-identical across builds — so WHICH ENTRY POINT the " +
                             "payload came through is the only thing that answers 'is this mine'. Point Update " +
                             "back at Show and every peer sees every ping in the other-peer colour, with " +
                             "nothing red and nothing logged.";
            foreach (var shower in new[] { showGeo, showTac })
                if (!Reaches(shower, null, "Tint"))
                    yield return "L182 ping-has-no-owner-colour: PingMarkers." + shower.Name + " never calls " +
                                 "Tint, so the marker it draws keeps the prefab's stock colour and the own/peer " +
                                 "distinction silently applies to one screen only. The colour is per-INSTANCE " +
                                 "and is only possible because this class stopped borrowing pooled markers " +
                                 "(arm (a)) — losing it here loses the only thing that ownership bought.";

            // ── arm (e): the scan must be able to SEE each violation.
            var control = ScanPing(typeof(FakeSeam), "FakeSeam")
                .Concat(ScanTrim(typeof(FakeSeam).GetMethod("Trim", AllMembers), "FakeSeam.Trim"))
                .ToList();
            foreach (var want in new[] { "ping-borrows-a-pooled-marker", "ping-marker-not-switched-on",
                                         "ready-button-face-not-built" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L182 control-not-red: FakeSeam commits " + want + " and the scan did not flag " +
                                 "it. The arm cannot tell a marker that was switched on from one that was not, " +
                                 "so its green above means nothing — that is exactly how this defect survived " +
                                 "two laws that both looked at the same seam.";
        }

        /// <summary>Arms (a) and (b), over a type and every nest under it.</summary>
        private static IEnumerable<string> ScanPing(Type seam, string label)
        {
            foreach (var t in AllTypes(seam))
                foreach (var m in AllMethodsOf(t))
                {
                    int made = 0, woke = 0;
                    foreach (var callee in CalleesOf(m))
                    {
                        if (callee.Name == "AddMarker" && callee.DeclaringType == typeof(GeoscapeGlobeMarkers))
                            yield return "L182 ping-borrows-a-pooled-marker: " + label + "." + m.Name +
                                         " calls GeoscapeGlobeMarkers.AddMarker. That method hands back a " +
                                         "POOLED marker the game re-uses, and its fresh-instantiate branch " +
                                         "(GeoscapeGlobeMarkers.cs:62-66) never switches the marker on — which " +
                                         "is why pings were audible and invisible. A borrowed marker is also " +
                                         "not ours to expire or to colour. Take the prefab from " +
                                         "GetMarkerPrefab and instantiate it.";
                        if (callee.Name == "Instantiate") made++;
                        if (callee.Name == "SetActive") woke++;
                    }
                    if (made > woke)
                        yield return "L182 ping-marker-not-switched-on: " + label + "." + m.Name + " instantiates " +
                                     made + " native prefab(s) and calls SetActive " + woke + " time(s). A prefab " +
                                     "handed to Instantiate is not guaranteed to arrive active — the game writes " +
                                     "SetActive(true) by hand at every one of its own instantiate sites " +
                                     "(AncientSiteProbeAbilityView.cs:88, UIStateVehicleSelected.cs:728, " +
                                     "TacticalActorViewBase.cs:449) — and an inactive marker draws nothing while " +
                                     "every line around it succeeds, cue included.";
                }
        }

        /// <summary>Arm (d), over one method.</summary>
        private static IEnumerable<string> ScanTrim(MethodBase trim, string label)
        {
            var callees = CalleesOf(trim).Select(c => c.Name).ToList();
            if (!callees.Contains("set_raycastTarget") || !callees.Contains("set_targetGraphic"))
                yield return "L182 ready-button-face-not-built: " + label + " must both silence graphics " +
                             "(Graphic.set_raycastTarget) and NAME the one it keeps " +
                             "(Selectable.set_targetGraphic). Reading targetGraphic and giving up when the " +
                             "prefab named none is the shipped defect: the clone sits a full row BELOW its HUD " +
                             "container, over the tactical map, and every graphic it carries answers " +
                             "EventSystem.IsPointerOverGameObject() — which is what TacticalView.IsCursorOverGUI() " +
                             "gates the confirm click on. The face has to be built, not looked up.";
        }

        /// <summary>ARM (e). Never instantiated, never registered — it exists only to be walked. One
        /// violation per arm: a pooled marker, an Instantiate with no SetActive, and a trim that trims
        /// nothing.</summary>
        private sealed class FakeSeam
        {
            internal static void Draw(GeoscapeGlobeMarkers markers, GameObject prefab)
            {
                markers.AddMarker(Vector3.zero, GlobeMarkerType.SitePointOfInterest, 5f);   // (a)
                UnityEngine.Object.Instantiate(prefab);                                     // (b): never woken
            }

            internal static void Trim(Button btn)                                           // (d): trims nothing
            {
                if (btn != null) btn.enabled = true;
            }
        }

        // ─── IL helpers (same primitives as L153/L158/L160; Program.cs is not partial) ────────────

        private static IEnumerable<Type> AllTypes(Type root)
        {
            yield return root;
            foreach (var n in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var d in AllTypes(n)) yield return d;
        }

        private static IEnumerable<MethodBase> AllMethodsOf(Type t)
            => t.GetMethods(AllMembers).Cast<MethodBase>().Concat(t.GetConstructors(AllMembers));

        private static bool Reaches(MethodBase caller, string declaringType, string calleeName)
            => CalleesOf(caller).Any(c => c.Name == calleeName &&
                                          (declaringType == null || c.DeclaringType?.Name == declaringType));

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
