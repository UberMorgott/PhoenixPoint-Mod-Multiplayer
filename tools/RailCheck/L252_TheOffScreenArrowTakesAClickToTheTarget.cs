using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Cameras;
using HarmonyLib;
using Multiplayer.UI;
using PhoenixPoint.Geoscape.Cameras;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L252 — THE OFF-SCREEN ARROW IS BIG ENOUGH TO HIT, TAKES A CLICK, TAKES THE CLICKER TO THE TARGET, AND
    /// DOES NOT ALSO ORDER A SOLDIER THERE.
    ///
    /// WHAT THE ARROW IS FOR. A ping outside the camera's view has no marker to see, so the only thing the
    /// watcher gets is the arrow at the screen edge. It shipped at <c>max(12, height/50)</c> px half-extent —
    /// ~21px at 1080p, a glyph you have to already know about to notice — and it was not clickable, so the
    /// answer to "who is pointing at what" was still "go and find it yourself". This law states the fixed
    /// version as an outcome, because every part of it is the kind that can silently regress: a size is one
    /// number, a click handler is a branch nobody exercises in a harness, and a camera move is a call that
    /// can be swapped for one that does not move anything.
    ///
    /// THE FOURTH ARM IS THE ONE NOBODY WOULD THINK OF. An IMGUI rect is not a GameObject, so
    /// <c>EventSystem.IsPointerOverGameObject</c> cannot see it — which means the click that follows a ping
    /// ALSO falls through to the world. The tactical states take their world clicks behind exactly one gate,
    /// <c>if (!Context.View.IsCursorOverGUI())</c> (<c>TacticalViewState.cs:47-70</c>), and the globe behind
    /// its own spelling of the same method (<c>GeoscapeView.IsCursorOverGui</c>, <c>:905</c>, also read by
    /// <c>GeoscapeCamera.cs:187,268-286</c>). So clicking an arrow near the screen edge would follow the ping
    /// AND walk the selected soldier to wherever it was drawn. <c>Event.current.Use()</c> does not help — the
    /// game reads the mouse through Rewired, which never sees an IMGUI event as consumed. The fix is to tell
    /// the game's OWN gate that the arrow is UI, and arm (d) is what keeps that gate wired.
    ///
    /// THE ARMS:
    ///   (a) <c>arrow-too-small-to-find</c> — <c>ArrowHalfMin</c> and <c>ArrowHalfFraction</c> are at least
    ///       twice what shipped (12f / 0.02f). Read as VALUES, so shrinking the arrow back has to be a
    ///       deliberate edit to a number a law is watching.
    ///   (b) <c>arrow-takes-no-click</c> — <c>OnGUI</c> hit-tests a rect (<c>Rect.Contains</c>) and reaches
    ///       <c>Focus</c>. A drawn-only arrow is the state this law was written to end.
    ///   (c) <c>click-moves-no-camera</c> — the OUTCOME arm: <c>Focus</c> reaches
    ///       <c>CameraDirector.Hint</c> AND builds both native param objects — <c>CameraChaseParams</c> for
    ///       the battle (the recipe <c>TacticalActorViewBase.DoCameraChaseParam</c>:491-501 uses, i.e. what a
    ///       portrait click does) and <c>GeoCamDirectorParams</c> for the globe (verbatim
    ///       <c>GeoscapeView.ChaseTarget</c>:1109-1113). Both, because a fix that only ever moved the
    ///       tactical camera would look complete on the screen it was tested on. This is the arm that makes
    ///       L160's narrowed camera ban honest: L160 says a packet may not move a camera, this says the click
    ///       must.
    ///   (d) <c>arrow-click-leaks-to-the-world</c> — both gate patches exist, target the two real game
    ///       methods, and their bodies read <c>PingMarkers.CursorOverArrow</c>. A patch class that stops
    ///       reading the flag is a patch that returns the game's own answer and blocks nothing.
    ///   (e) POSITIVE CONTROL, EXECUTED — arms (b) and (c) are "must reach" arms, so the control is a seam
    ///       that does NOT: <see cref="FakeSeam"/> has an <c>OnGUI</c> that draws without hit-testing and a
    ///       <c>Focus</c> that moves nothing. Both arms must flag it, or a scan that resolved no call edges
    ///       would report the feature working forever.
    ///
    /// Falsify: set <c>ArrowHalfMin</c> back to 12f → (a); delete the <c>Focus(p)</c> call from <c>OnGUI</c>
    /// → (b); replace either <c>Hint</c> call in <c>Focus</c> with a log line → (c); empty either patch
    /// class's Postfix → (d); give <see cref="FakeSeam"/> a real body → (e).
    /// </summary>
    internal static class L252_TheOffScreenArrowTakesAClickToTheTarget
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>What the arrow shipped at, and what arm (a) measures against. The owner asked for 2-3x.</summary>
        private const float ShippedHalfMin = 12f;
        private const float ShippedHalfFraction = 0.02f;

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(PingMarkers);
            var halfMin = seam.GetField("ArrowHalfMin", AllMembers);
            var fraction = seam.GetField("ArrowHalfFraction", AllMembers);
            var overArrow = seam.GetProperty("CursorOverArrow", AllMembers)?.GetGetMethod(true);
            var tacGate = typeof(TacticalView).GetMethod("IsCursorOverGUI", AllMembers);
            var geoGate = typeof(GeoscapeView).GetMethod("IsCursorOverGui", AllMembers);
            var hint = typeof(CameraDirector).GetMethod("Hint", AllMembers, null,
                                                        new[] { typeof(CameraHint), typeof(object) }, null);

            if (halfMin == null || fraction == null || overArrow == null || tacGate == null ||
                geoGate == null || hint == null || !halfMin.IsLiteral || !fraction.IsLiteral)
            {
                yield return "L252 premise-changed: one of PingMarkers.ArrowHalfMin / ArrowHalfFraction (both " +
                             "must stay compile-time constants this law can read) / CursorOverArrow, " +
                             "TacticalView.IsCursorOverGUI, GeoscapeView.IsCursorOverGui or " +
                             "CameraDirector.Hint(CameraHint, object) no longer resolves. Those are the " +
                             "arrow's size, the flag the game's cursor gate reads, the two gates themselves " +
                             "and the camera mover the click rides — every arm below is asserting about a " +
                             "shape the build no longer has.";
                yield break;
            }

            // ── arm (a): the size, as a value.
            var min = (float)halfMin.GetRawConstantValue();
            var frac = (float)fraction.GetRawConstantValue();
            if (min < ShippedHalfMin * 2f || frac < ShippedHalfFraction * 2f)
                yield return "L252 arrow-too-small-to-find: the arrow's half-extent is max(" + min + ", " +
                             "height*" + frac + "), which is not at least twice the max(" + ShippedHalfMin +
                             ", height*" + ShippedHalfFraction + ") it shipped at. That original was ~21px on " +
                             "a 1080p screen — for the ONE widget whose entire job is 'somebody is pointing " +
                             "at something you cannot see', too small to notice is the whole feature missing. " +
                             "It is also now the click target, so shrinking it makes the click harder to land " +
                             "as well as the arrow harder to see.";

            foreach (var v in Scan(seam, "PingMarkers")) yield return v;

            // ── arm (d): the game's own cursor gate is told the arrow is UI, on both screens.
            foreach (var patch in new[] { typeof(PingArrowIsGuiInBattle), typeof(PingArrowIsGuiOnTheGlobe) })
            {
                var target = patch == typeof(PingArrowIsGuiInBattle) ? tacGate : geoGate;
                var attr = patch.GetCustomAttributes(typeof(HarmonyPatch), false).Length;
                var post = patch.GetMethod("Postfix", AllMembers);
                if (attr == 0 || post == null || !Reaches(post, null, overArrow.Name))
                    yield return "L252 arrow-click-leaks-to-the-world: " + patch.Name + " must be a " +
                                 "[HarmonyPatch] whose Postfix reads PingMarkers.CursorOverArrow, over " +
                                 target.DeclaringType.Name + "." + target.Name + ". An IMGUI rect is not a " +
                                 "GameObject, so EventSystem.IsPointerOverGameObject cannot see the arrow and " +
                                 "the game's own gate answers false over it. Without this patch a click on " +
                                 "the arrow follows the ping AND reaches the world under it — on the " +
                                 "battlefield that is an order to walk the selected soldier to the screen " +
                                 "edge, on the globe it is a drag of the camera. Losing a move because you " +
                                 "looked at a ping is worse than the problem the arrow solves.";
            }

            // ── arm (e): the "must reach" arms must be able to say NO.
            var control = Scan(typeof(FakeSeam), "FakeSeam").ToList();
            foreach (var want in new[] { "arrow-takes-no-click", "click-moves-no-camera" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L252 control-not-red: FakeSeam draws an arrow that hit-tests nothing and " +
                                 "'focuses' without touching a camera, and the scan did not flag " + want +
                                 ". A must-reach arm whose scan resolves no call edges reports every feature " +
                                 "as present — which is exactly how a law ships green over a dead button.";
        }

        /// <summary>Arms (b) and (c), over whichever type is handed in — the real seam, or the control.</summary>
        private static IEnumerable<string> Scan(Type seam, string label)
        {
            var onGui = seam.GetMethod("OnGUI", AllMembers);
            var focus = seam.GetMethod("Focus", AllMembers);
            if (onGui == null || focus == null)
            {
                yield return "L252 arrow-takes-no-click: " + label + " has no OnGUI/Focus pair at all.";
                yield break;
            }

            if (!Reaches(onGui, "Rect", "Contains") || !Reaches(onGui, null, "Focus"))
                yield return "L252 arrow-takes-no-click: " + label + ".OnGUI must hit-test the arrow's rect " +
                             "(Rect.Contains) and call Focus. An arrow that is only drawn answers 'somebody " +
                             "pinged something off screen' and then leaves the watcher to go and find it — " +
                             "which is the state this arrow was in for its whole first life.";

            var movesCamera = Reaches(focus, "CameraDirector", "Hint");
            var buildsChase = Creates(focus, typeof(CameraChaseParams));
            var buildsGeo = Creates(focus, typeof(GeoCamDirectorParams));
            if (!movesCamera || !buildsChase || !buildsGeo)
                yield return "L252 click-moves-no-camera: " + label + ".Focus must call CameraDirector.Hint " +
                             "and build BOTH native param objects — CameraChaseParams for the battlefield " +
                             "(the recipe TacticalActorViewBase.DoCameraChaseParam:491-501 uses, i.e. what a " +
                             "portrait click already does) and GeoCamDirectorParams for the globe (verbatim " +
                             "GeoscapeView.ChaseTarget:1109-1113). Found Hint=" + movesCamera + ", chase=" +
                             buildsChase + ", geo=" + buildsGeo + ". A click that centres nothing is the " +
                             "same dead control as an arrow that takes no click, one layer further in — and " +
                             "a fix that only moved the tactical camera would look complete on whichever " +
                             "screen it was tested on.";
        }

        /// <summary>ARM (e). Never instantiated, never registered — it exists only to be walked. Its OnGUI
        /// draws without asking where the mouse is; its Focus logs instead of moving anything.</summary>
        private sealed class FakeSeam
        {
            internal static void OnGUI()
            {
                Debug.Log("drew an arrow at " + new Rect(0f, 0f, 1f, 1f));   // never asks where the mouse is
            }

            internal static void Focus()
            {
                Debug.Log("went nowhere");
            }
        }

        // ─── IL helpers (same primitives as L153/L158/L160/L182/L250; Program.cs is not partial) ────────

        private static bool Reaches(MethodBase caller, string declaringType, string calleeName)
            => CalleesOf(caller).Any(c => c.Name == calleeName &&
                                          (declaringType == null || c.DeclaringType?.Name == declaringType));

        /// <summary>Does the method construct that type — newobj (0x73) resolving to one of its ctors.</summary>
        private static bool Creates(MethodBase caller, Type type)
        {
            foreach (var tok in TokensAfter(caller, 0x73))
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null && c.DeclaringType == type) return true;
            }
            return false;
        }

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
