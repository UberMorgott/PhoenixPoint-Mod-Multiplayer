using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.UI;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L253 — A GEOSCAPE PING LANDS ON THE GLOBE'S SURFACE, AND ONLY ON SOMETHING THAT IS THERE.
    ///
    /// THE INCIDENT. For its whole life the geoscape ping was AUDIBLE, ARROWED AND INVISIBLE: the cue
    /// played, the off-screen arrow appeared, and there was never a marker anywhere on the globe. L182
    /// read that as a missing <c>SetActive(true)</c>, shipped the activation, and nothing changed on
    /// screen — because the marker was switched on and BURIED AT THE CENTRE OF THE EARTH. Every globe
    /// object is a centre-pivot (<c>GeoActor.PivotTransform => transform</c>,
    /// <c>WorldPosition => Surface.position</c>, GeoActor.cs:23,25) whose only child at globe radius is
    /// <c>Surface = PivotTransform.Find("GlobeOffset")</c> (GeoVehicle.cs:315). An object ping parented
    /// its marker to <c>actor.transform</c> — the pivot, at (0,0,0) — and a point ping placed its marker by
    /// ROTATION ALONE, which leaves an object sitting at its parent's origin. Both work only if the prefab
    /// carries the ~6.4-unit offset itself, and the <c>Binds</c> prefab does not: the game never exercises
    /// that path, it re-parents POOLED scene instances that keep their authored local offset
    /// (GeoscapeGlobeMarkers.cs:38-52,:71,:99). The mod's OWN LOG had proved it before a line was changed —
    /// <c>ping arrow CLICKED — centring … on the pinged object at (0.0, 0.0, 0.0)</c>, against
    /// <c>(-6.0, 2.0, 1.0)</c> for a point ping actually on the surface. The same (0,0,0) is why the arrows
    /// "got stuck": <c>Faces</c> tests <c>Dot(world - centre, camera - centre)</c>, which for the centre
    /// itself is 0, never &gt; 0, so every object ping drew an arrow forever no matter where the camera was.
    ///
    /// AND THE OTHER HALF: A PING NEEDS SOMETHING TO POINT AT. The globe half used to fall back to a POINT
    /// ping wherever the cursor was, so a click on empty ocean cost a packet, a sound on every peer and a
    /// marker naming nothing. Open water is not a point of interest; the refusal has to be visible to the
    /// presser or he cannot tell it from the bug above.
    ///
    /// THE ARMS:
    ///   (a) <c>ping-sinks-into-the-globe</c> — <c>ShowGeo</c> reads <c>GeoActor.Surface</c>, never reaches
    ///       <c>Component.get_transform</c> (i.e. never parents to the pivot), and writes a
    ///       <c>Transform.position</c>. Rotation-alone placement is the defect, so a law that only asked
    ///       "does it set a rotation" would have been green throughout.
    ///   (b) <c>empty-space-still-pings</c> — <c>Capture</c> reaches <c>Refuse</c>, and <c>Refuse</c>
    ///       reaches <c>Post</c> (the native <c>AK.Wwise.Event</c> post). Both halves: a capture that never
    ///       refuses sends a ping for open ocean, a refusal that posts nothing is indistinguishable from a
    ///       key that did not register.
    ///   (c) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> places a marker by rotation alone and
    ///       captures without refusing anything. Arms (a) and (b) are both "must reach" shapes, so unless
    ///       the control comes back RED the scan resolved no call edges and would report the feature
    ///       present forever.
    ///
    /// Falsify: put <c>host = actor.transform</c> back in <c>ShowGeo</c> → (a); delete the
    /// <c>Refuse(view)</c> call from <c>Capture</c>, or the <c>cue.Post(host)</c> line from
    /// <c>Refuse</c> → (b); give <see cref="FakeSeam"/> a real body → (c).
    /// </summary>
    internal static class L253_APingLandsOnTheGlobeNotInsideIt
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(PingMarkers);
            var surface = typeof(GeoActor).GetProperty("Surface", AllMembers)?.GetGetMethod(true);
            var ring = typeof(GeoSiteVisualsDefs).GetField("HavenDefenseVisualsPrefab", AllMembers);

            if (surface == null || ring == null ||
                seam.GetMethod("ShowGeo", AllMembers) == null ||
                seam.GetMethod("Capture", AllMembers) == null ||
                seam.GetMethod("Refuse", AllMembers) == null)
            {
                yield return "L253 premise-changed: one of GeoActor.Surface (the globe-radius child every " +
                             "placement below hangs on), GeoSiteVisualsDefs.HavenDefenseVisualsPrefab (the " +
                             "native ring the ping borrows) or PingMarkers.ShowGeo/Capture/Refuse no longer " +
                             "resolves. Every arm is asserting about a shape the build no longer has.";
                yield break;
            }

            foreach (var v in Scan(seam, "PingMarkers")) yield return v;

            // ── arm (c): both arms above are "must reach", so they must be able to say NO.
            var control = Scan(typeof(FakeSeam), "FakeSeam").ToList();
            foreach (var want in new[] { "ping-sinks-into-the-globe", "empty-space-still-pings" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L253 control-not-red: FakeSeam places its marker by rotation alone and " +
                                 "captures a ping without ever refusing one, and the scan did not flag " +
                                 want + ". A must-reach arm whose scan resolves no call edges reports every " +
                                 "feature as present — which is exactly how this ping stayed green while " +
                                 "every marker it drew was inside the planet.";
        }

        /// <summary>Arms (a) and (b), over whichever type is handed in — the real seam, or the control.</summary>
        private static IEnumerable<string> Scan(Type seam, string label)
        {
            var showGeo = seam.GetMethod("ShowGeo", AllMembers);
            var capture = seam.GetMethod("Capture", AllMembers);
            var refuse = seam.GetMethod("Refuse", AllMembers);
            if (showGeo == null || capture == null || refuse == null)
            {
                yield return "L253 ping-sinks-into-the-globe: " + label + " has no ShowGeo/Capture/Refuse " +
                             "trio at all.";
                yield return "L253 empty-space-still-pings: " + label + " has no ShowGeo/Capture/Refuse " +
                             "trio at all.";
                yield break;
            }

            var readsSurface = Reaches(showGeo, "GeoActor", "get_Surface");
            var readsPivot = Reaches(showGeo, "Component", "get_transform");
            var writesPosition = Reaches(showGeo, "Transform", "set_position");
            if (!readsSurface || readsPivot || !writesPosition)
                yield return "L253 ping-sinks-into-the-globe: " + label + ".ShowGeo must hang the marker off " +
                             "GeoActor.Surface (the 'GlobeOffset' child at globe radius, GeoVehicle.cs:315 — " +
                             "and where the game itself parents world visuals, GeoVehicle.cs:454), must NOT " +
                             "reach Component.get_transform (the actor's own transform is the centre-pivot " +
                             "at (0,0,0)), and must WRITE a Transform.position for the point it is handed. " +
                             "Found Surface=" + readsSurface + ", pivot=" + readsPivot + ", position=" +
                             writesPosition + ". Rotation-alone placement leaves the marker at its parent's " +
                             "origin, which on the globe is the centre of the earth: invisible, and it also " +
                             "makes Faces()'s dot product exactly 0 so the off-screen arrow points at it " +
                             "forever.";

            var refuses = Reaches(capture, null, "Refuse");
            var sounds = Reaches(refuse, null, "Post");
            if (!refuses || !sounds)
                yield return "L253 empty-space-still-pings: " + label + ".Capture must call Refuse when the " +
                             "globe hit-test finds no point of interest, and Refuse must POST the game's own " +
                             "cue (InterceptionGameSoundDef.DisabledEquipmentClick via AK.Wwise.Event.Post, " +
                             "InterceptionGameSound.cs:34-43). Found refuse=" + refuses + ", post=" + sounds +
                             ". Without the first, a click on open ocean spends a packet and sounds on every " +
                             "peer to mark a place that names nothing; without the second the presser cannot " +
                             "tell a refusal from a key that never registered.";
        }

        /// <summary>ARM (c). Never instantiated, never registered — it exists only to be walked. Its ShowGeo
        /// is the old recipe (rotation alone, off the actor's own transform); its Capture pings whatever was
        /// under the cursor and never refuses; its Refuse makes no sound.</summary>
        private sealed class FakeSeam
        {
            internal static void ShowGeo(GeoActor actor)
            {
                var go = new GameObject("marker");
                go.transform.SetParent(actor.transform, false);        // the centre-pivot
                go.transform.rotation = Quaternion.identity;           // rotation alone — the whole defect
            }

            internal static void Capture()
            {
                Debug.Log("pinged whatever the cursor was over, ocean included");
            }

            internal static void Refuse()
            {
            }
        }

        // ─── IL helpers (same primitives as L153/L158/L160/L182/L250/L252; Program.cs is not partial) ────

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
