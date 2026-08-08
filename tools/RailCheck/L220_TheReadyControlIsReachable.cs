using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RailCheck
{
    /// <summary>
    /// L220 — THE READY CONTROL IS REACHABLE, AND A PRESS LEAVES A MARK.
    ///
    /// L182 arm (d) already stood over this exact seam and was GREEN through the whole defect, because it
    /// asserts that <c>TrimRaycast</c> CALLS <c>Graphic.set_raycastTarget</c> and
    /// <c>Selectable.set_targetGraphic</c>. It did call both. Every wire on the button was correct and the
    /// button still took neither hover nor click on any of three peers — the classic shape this repo keeps
    /// paying for: a law that asserts the CALL while the OUTCOME is dead.
    ///
    /// WHAT ACTUALLY HAPPENED (2026-08-08). The clone itself comes from <c>Object.Instantiate</c>, which
    /// carried the prefab's UI layer across. Its two hand-built children did not: <c>new GameObject(...)</c>
    /// starts on layer 0 (Default), which the HUD camera does not render. An undrawn graphic keeps
    /// <c>CanvasRenderer.absoluteDepth == -1</c>, and uGUI's raycaster opens with
    /// <c>if (graphic.depth == -1 || !graphic.raycastTarget || graphic.canvasRenderer.cull) continue;</c>
    /// — "-1 means it hasn't been processed by the canvas, which means it isn't actually drawn"
    /// (GraphicRaycaster.Raycast, uGUI 2019.4). Since <c>TrimRaycast</c> had by then silenced all twelve
    /// NATIVE surfaces in favour of that one built face, the button's entire hit footprint was a graphic
    /// nothing could hit. No <c>OnPointerEnter</c> (so no hover frame — the highlight is driven from
    /// <c>PhoenixGeneralButton.OnPointerEnter</c>:214-227, not from a colour tint) and no
    /// <c>OnPointerClick</c> (so no <c>Button.onClick</c>). Unity's own runtime UI factory takes the one
    /// line that prevents it: <c>DefaultControls.CreateUIObject</c> is <c>go.layer = parent.layer</c>.
    ///
    /// THE ARMS:
    ///   (a) <c>ready-widget-off-the-drawn-layer</c> — the OUTCOME arm, and the one that would have been red
    ///       through the defect. Every method of <see cref="TacticalReadyButton"/> calls
    ///       <c>GameObject.set_layer</c> at least as often as it constructs a <c>GameObject</c>. COUNTED,
    ///       not merely present, for the same reason L182 arm (b) counts: this class builds TWO children
    ///       (the tint and the hit face) and adopting the layer on one of them leaves the other exactly as
    ///       dead as before, with nothing else red.
    ///   (b) <c>ready-press-is-silent</c> — <c>TacticalReadySync.Toggle</c> reaches <c>Debug.Log</c>. The
    ///       method already reached <c>Debug.LogWarning</c> on its no-session bail, which is why "nobody
    ///       clicked" and "the click reached nothing" were indistinguishable in six logs across three
    ///       instances. The arm names <c>Log</c> specifically so restoring the bail-only shape is red.
    ///   (c) <c>ready-reachability-unmeasured</c> — <c>TacticalReadyRowFollower.ReportFootprint</c> reaches
    ///       <c>Graphic.get_depth</c>. A headless harness cannot hit-test a canvas, so the mechanical
    ///       guarantee this law can actually give is that the RUNTIME measures it and says so: counting
    ///       raycast surfaces without asking whether any of them is drawn is precisely the diagnostic that
    ///       printed a clean line over a completely dead button.
    ///   (d) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> below builds a child without adopting a
    ///       layer, presses with a warning only, and reports without reading a depth. (a), (b) and (c) must
    ///       all go red on it, or their green above is a scan that resolved nothing and passes forever.
    ///   (f) <c>ready-widget-off-screen</c> — THE ONLY ARM HERE THAT ASSERTS A GEOMETRIC OUTCOME, AND THE
    ///       ONLY ONE THAT INSPECTS NOTHING WE SET. Every other arm reads back a field this mod wrote, which
    ///       is exactly how (a), (c) and L182 (d) stayed green over a button that took neither hover nor
    ///       click. This one EXECUTES <c>TacticalReadyRowFollower.ShiftOntoScreen</c> and asserts the RESULT:
    ///       the placed rect ends up inside the screen rect, and a rect already inside is not moved. A widget
    ///       outside the canvas cannot be hovered or clicked no matter how correct every wire on it is — no
    ///       pointer position maps to it — and that is the shape the placement had: three successive fixes
    ///       each pushed this clone FURTHER below the End Turn button (a height, then the row's spacing, then
    ///       the row's drawn overhang, live total 110 parent-local units off a button sitting near the bottom
    ///       of the HUD) and not one of them checked that the place it landed still existed.
    ///   (g) <c>ready-placement-skips-the-screen</c> — the wiring half, and named as such: a pure function
    ///       nobody calls is decorative, so the method that writes <c>anchoredPosition</c> must be the one
    ///       that asks. This arm asserts a CALL and is therefore the weak one; (f) is the arm that would have
    ///       been red through the defect.
    ///
    /// NOT A GATE, STILL. Nothing here asserts that anything WAITS on the tally — it must not, and L119
    /// keeps it that way. This law is only about the control being hit-testable and the press being
    /// visible; a press still changes one label and gates nothing on any peer.
    ///
    /// Falsify: drop either <c>layer</c> assignment in <c>BuildGreenOverlay</c>/<c>TrimRaycast</c> → (a);
    /// delete the <c>Debug.Log</c> at the end of <c>Toggle</c> → (b); drop the <c>g.depth</c> read from
    /// <c>ReportFootprint</c> → (c); empty <see cref="FakeSeam"/> → (d); make
    /// <c>ShiftOntoScreen</c> return 0 → (f); drop the <c>ClampOntoScreen</c> call from
    /// <c>TacticalReadyRowFollower.Apply</c> → (g).
    /// </summary>
    internal static class L220_TheReadyControlIsReachable
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var toggle = typeof(TacticalReadySync).GetMethod("Toggle", AllMembers);
            var report = typeof(TacticalReadyRowFollower).GetMethod("ReportFootprint", AllMembers);

            // The GAME/ENGINE members the arms name must resolve, or every arm below is an assertion about a
            // shape this build does not have. get_depth in particular: arm (c) requires a call to it by name,
            // and a uGUI that renamed it would turn the requirement red for the wrong reason — better to say
            // "premise changed" than to send someone chasing a repair that is already in place.
            var premises = new MemberInfo[]
            {
                typeof(GameObject).GetProperty("layer", AllMembers)?.GetSetMethod(true),
                typeof(Graphic).GetProperty("depth", AllMembers)?.GetGetMethod(true),
                typeof(Debug).GetMethod("Log", BindingFlags.Public | BindingFlags.Static, null,
                                        new[] { typeof(object) }, null),
                typeof(EventSystem).GetMethod("RaycastAll", AllMembers),
                typeof(Graphic).GetProperty("raycastTarget", AllMembers)?.GetSetMethod(true),
            };

            if (toggle == null || report == null || premises.Any(m => m == null))
            {
                yield return "L220 premise-changed: one of TacticalReadySync.Toggle, " +
                             "TacticalReadyRowFollower.ReportFootprint, GameObject.set_layer, " +
                             "Graphic.get_depth, Graphic.set_raycastTarget, EventSystem.RaycastAll or " +
                             "Debug.Log(object) no longer resolves. The seams this law is written over, or " +
                             "the members it names, have moved — every arm below is asserting about a shape " +
                             "the build no longer has.";
                yield break;
            }

            foreach (var v in ScanLayers(typeof(TacticalReadyButton), "TacticalReadyButton")) yield return v;
            foreach (var v in ScanPress(toggle, "TacticalReadySync.Toggle")) yield return v;
            foreach (var v in ScanProbe(typeof(TacticalReadyRowFollower), "TacticalReadyRowFollower")) yield return v;
            foreach (var v in ScanFace(typeof(TacticalReadyButton), "TacticalReadyButton")) yield return v;
            foreach (var v in ScanScreen(TacticalReadyRowFollower.ShiftOntoScreen,
                                         "TacticalReadyRowFollower.ShiftOntoScreen")) yield return v;
            foreach (var v in ScanPlacement(typeof(TacticalReadyRowFollower), "TacticalReadyRowFollower"))
                yield return v;

            // ── arm (d): the scan must be able to SEE each violation.
            var control = ScanLayers(typeof(FakeSeam), "FakeSeam")
                .Concat(ScanPress(typeof(FakeSeam).GetMethod("Press", AllMembers), "FakeSeam.Press"))
                .Concat(ScanProbe(typeof(FakeSeam), "FakeSeam"))
                .Concat(ScanFace(typeof(FakeSeam), "FakeSeam"))
                .Concat(ScanScreen(FakeSeam.NeverShift, "FakeSeam.NeverShift"))
                .Concat(ScanPlacement(typeof(FakeSeam), "FakeSeam"))
                .ToList();
            foreach (var want in new[] { "ready-widget-off-the-drawn-layer", "ready-press-is-silent",
                                         "ready-reachability-unasked", "ready-face-disarmed",
                                         "ready-widget-off-screen", "ready-placement-skips-the-screen" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L220 control-not-red: FakeSeam commits " + want + " and the scan did not " +
                                 "flag it. The arm cannot tell a reachable control from a dead one, so its " +
                                 "green above means nothing — which is exactly how L182 arm (d) stayed green " +
                                 "over a button that took neither hover nor click.";
        }

        /// <summary>Arm (a), over a type and every nest under it.</summary>
        private static IEnumerable<string> ScanLayers(Type seam, string label)
        {
            foreach (var t in AllTypes(seam))
                foreach (var m in AllMethodsOf(t))
                {
                    int made = 0, placed = 0;
                    foreach (var callee in CalleesOf(m))
                    {
                        if (callee.IsConstructor && callee.DeclaringType == typeof(GameObject)) made++;
                        if (callee.Name == "set_layer" && callee.DeclaringType == typeof(GameObject)) placed++;
                    }
                    if (made > placed)
                        yield return "L220 ready-widget-off-the-drawn-layer: " + label + "." + m.Name +
                                     " builds " + made + " GameObject(s) and adopts a layer " + placed +
                                     " time(s). `new GameObject` starts on layer 0 (Default), which the HUD " +
                                     "camera does not render, so the graphic is never batched, keeps " +
                                     "depth == -1, and GraphicRaycaster skips it — the control renders as " +
                                     "part of the button and answers no pointer at all. Instantiate carried " +
                                     "the clone's layer over; a hand-built child has to be given one, which " +
                                     "is the single line Unity's own DefaultControls.CreateUIObject takes.";
                }
        }

        /// <summary>Arm (b), over one method.</summary>
        private static IEnumerable<string> ScanPress(MethodBase press, string label)
        {
            if (!Reaches(press, "Debug", "Log"))
                yield return "L220 ready-press-is-silent: " + label + " must reach Debug.Log on the path that " +
                             "actually flips the flag. A LogWarning on the no-session bail is not this: with " +
                             "only that, a build where the button is unreachable produces logs BYTE-IDENTICAL " +
                             "to one where nobody pressed it, which is how a dead control survived a full " +
                             "test round across three instances and six logs. Silent swallow is this repo's " +
                             "dominant bug class and a press is where it costs the most.";
        }

        /// <summary>
        /// ARM (c). THE OUTCOME IS "SOMETHING CAN HIT IT", AND ONLY THE RAYCASTER CAN SAY SO.
        ///
        /// This arm used to require that the diagnostic read <c>Graphic.depth</c>. It did, and it was GREEN
        /// across a full three-instance test round over a button that took neither hover nor click — the
        /// fifth assert-the-call law in this repo in one night. Every field it inspected was ours and every
        /// one read correct (<c>MP_ReadyHit(layer=5 depth=258)</c>, exactly one live surface). Reading back
        /// what we ourselves set can never establish reachability, because the parts that refuse a raycast
        /// are the parts we do NOT own: the ancestor ICanvasRaycastFilter chain, the canvas a graphic is
        /// registered against, and whatever is drawn on top.
        ///
        /// So the requirement is now the measurement that can come back NO: the follower must put the
        /// question to <c>EventSystem.RaycastAll</c>, and the method that asks must be able to reach
        /// <c>Debug.LogError</c> — a probe whose negative answer is silent restores exactly the condition
        /// this law exists to end, where a dead control and a working one produce identical logs.
        /// </summary>
        private static IEnumerable<string> ScanProbe(Type seam, string label)
        {
            MethodBase asker = null;
            foreach (var t in AllTypes(seam))
                foreach (var m in AllMethodsOf(t))
                    if (Reaches(m, "EventSystem", "RaycastAll")) { asker = m; break; }

            if (asker == null)
                yield return "L220 ready-reachability-unasked: " + label + " never calls " +
                             "EventSystem.RaycastAll. Nothing in the clone's own fields can answer whether a " +
                             "pointer can reach this button — raycastTarget, layer and depth all read healthy " +
                             "on the dead one. The only component that knows is the raycaster the game itself " +
                             "uses, so it has to be asked at the button's own centre.";
            else if (!Reaches(asker, "Debug", "LogError"))
                yield return "L220 ready-reachability-unasked: " + label + "." + asker.Name + " asks " +
                             "EventSystem.RaycastAll and cannot reach Debug.LogError, so a NO comes back " +
                             "silent. A probe that only speaks when the answer is good is the same green-over-" +
                             "dead shape this arm replaced.";
        }

        /// <summary>
        /// ARM (e). THE BUTTON'S FACE IS THE CLONE'S OWN NATIVE GRAPHICS.
        ///
        /// The regression this law was written after is `30a64e3`: a TrimRaycast that swept the clone with
        /// GetComponentsInChildren&lt;Graphic&gt; and set raycastTarget=false on all twelve, substituting one
        /// hand-built transparent Image. Before it the button worked (it was hit-testable enough to eat map
        /// clicks, reported twice); after it, dead on every peer, and a layer repair on the built face did
        /// not revive it. The clone is an Instantiate of the native End Turn button and its face is already
        /// correct; disarming it and re-deriving one is the defect.
        ///
        /// The shape flagged is the sweep specifically — a method that both enumerates children and clears
        /// raycastTarget. Clearing it on a single graphic this class BUILT (the tint overlay, which must not
        /// eat clicks) enumerates nothing and is untouched.
        /// </summary>
        private static IEnumerable<string> ScanFace(Type seam, string label)
        {
            foreach (var t in AllTypes(seam))
                foreach (var m in AllMethodsOf(t))
                    if (Reaches(m, null, "GetComponentsInChildren") &&
                        Reaches(m, "Graphic", "set_raycastTarget"))
                        yield return "L220 ready-face-disarmed: " + label + "." + m.Name + " sweeps the " +
                                     "clone's children and clears Graphic.raycastTarget. That is the exact " +
                                     "edit that killed this button in 30a64e3 — the clone is a copy of the " +
                                     "native End Turn button and its own graphics ARE its clickable face, so " +
                                     "silencing them leaves a widget nothing can hit no matter how carefully " +
                                     "a replacement surface is built. A button answering the pointer over its " +
                                     "own rect is not a bug; End Turn does the same. If a click must survive " +
                                     "under this widget, move the widget.";
        }

        /// <summary>The shape of the placement's screen clamp, so arm (f) can run the real one and a
        /// deliberately broken one through the same assertions.</summary>
        private delegate float Shift(float myBottom, float myTop, float screenBottom, float screenTop);

        /// <summary>
        /// ARM (f). EXECUTED, ON THE OUTCOME.
        ///
        /// The clone's rect in the first column is LIVE — world y=[596.6661..616.6664], reported identically
        /// by all three peers on 2026-08-08, 36.67 world units below the End Turn container it hangs from.
        /// The screen edge is the fact the diagnostic never captured, which is the whole reason this arm
        /// exists: whatever it is, the placement has to land inside it. So the cases put that edge on both
        /// sides of the live rect and demand the same outcome each time — after the shift, the rect is
        /// INSIDE the screen — plus the case that a rect already inside is not moved, because a clamp that
        /// nudges a widget that was fine is a widget that drifts a little further every frame.
        ///
        /// Note what is NOT asserted: no field of ours, no call, no count. Only "does the rect this
        /// arithmetic produces exist on the screen".
        /// </summary>
        private static IEnumerable<string> ScanScreen(Shift shift, string label)
        {
            var cases = new[]
            {
                // why, myBottom, myTop, screenBottom, screenTop
                new Case("the live placement, on a canvas whose bottom edge falls in the 36.67-unit gap the " +
                         "clone was pushed into", 596.6661f, 616.6664f, 600f, 960f),
                new Case("the live placement, wholly below the canvas", 596.6661f, 616.6664f, 620f, 980f),
                new Case("pushed off the TOP instead", 950f, 970f, 600f, 960f),
                new Case("already inside — must not move", 700f, 720f, 600f, 960f),
            };

            foreach (var c in cases)
            {
                float s = shift(c.MyBottom, c.MyTop, c.ScreenBottom, c.ScreenTop);
                bool inside = c.MyBottom + s >= c.ScreenBottom - 0.01f && c.MyTop + s <= c.ScreenTop + 0.01f;
                bool wasInside = c.MyBottom >= c.ScreenBottom && c.MyTop <= c.ScreenTop;
                if (!inside)
                    yield return "L220 ready-widget-off-screen: " + label + " leaves the ready button at y=[" +
                                 (c.MyBottom + s) + ".." + (c.MyTop + s) + "] on a screen spanning [" +
                                 c.ScreenBottom + ".." + c.ScreenTop + "] (" + c.Why + "). A rect outside the " +
                                 "canvas takes no hover and no click on any peer, ever, with every wire on the " +
                                 "button correct — no pointer position maps to it, so the raycaster is never " +
                                 "even asked. That is the symptom this button showed for four rounds while " +
                                 "raycastTarget, layer and depth all read healthy.";
                else if (wasInside && s != 0f)
                    yield return "L220 ready-widget-off-screen: " + label + " moved a button that was already " +
                                 "inside the screen by " + s + " (" + c.Why + "). The clamp is a last resort, " +
                                 "not a placement: applied to a rect that was fine it fights the row follower " +
                                 "and the button crawls up the HUD.";
            }
        }

        private sealed class Case
        {
            internal Case(string why, float b, float t, float sb, float st)
            { Why = why; MyBottom = b; MyTop = t; ScreenBottom = sb; ScreenTop = st; }
            internal readonly string Why;
            internal readonly float MyBottom, MyTop, ScreenBottom, ScreenTop;
        }

        /// <summary>ARM (g), and honestly labelled: this one asserts a CALL. It exists only so arm (f) cannot
        /// pass over a pure function nothing invokes — the method that writes the clone's
        /// <c>anchoredPosition</c> must also be the one that asks whether the result is on the screen.</summary>
        private static IEnumerable<string> ScanPlacement(Type seam, string label)
        {
            foreach (var t in AllTypes(seam))
                foreach (var m in AllMethodsOf(t))
                    if (Reaches(m, "RectTransform", "set_anchoredPosition") &&
                        !Reaches(m, null, "ClampOntoScreen") && !Reaches(m, null, "ShiftOntoScreen"))
                        yield return "L220 ready-placement-skips-the-screen: " + label + "." + m.Name +
                                     " writes RectTransform.anchoredPosition and never asks whether the " +
                                     "result is on the screen. Every placement fix this button has had pushed " +
                                     "it further down — a height, a spacing, then the row's drawn overhang — " +
                                     "and the clamp is the only thing standing between the next one and a " +
                                     "widget nobody can point at.";
        }

        /// <summary>ARM (d). Never instantiated, never registered — it exists only to be walked. One
        /// violation per arm: a child built without a layer, a press that only warns, a type that never asks
        /// the raycaster anything (arm (c) needs no member here — its violation is the ABSENCE of a
        /// RaycastAll call anywhere on this type), and a sweep that disarms the clone's own face.</summary>
        private sealed class FakeSeam
        {
            internal static void Build(Transform parent)
            {
                var go = new GameObject("Fake", typeof(Image));      // (a): never given a layer
                go.transform.SetParent(parent, false);
            }

            internal static void Press()
            {
                Debug.LogWarning("no session");                     // (b): warns, never logs the press
            }

            internal static void Trim(GameObject clone)
            {
                foreach (var g in clone.GetComponentsInChildren<Graphic>(true))
                    g.raycastTarget = false;                        // (e): silences the clone's own face
            }

            /// <summary>(f): the placement that was live on 2026-08-08 — it drops the clone and never looks
            /// at where it landed.</summary>
            internal static float NeverShift(float myBottom, float myTop, float screenBottom, float screenTop)
                => 0f;

            /// <summary>(g): writes a position, never asks whether it is on the screen.</summary>
            internal static void Place(RectTransform rt)
            {
                rt.anchoredPosition = new Vector2(0f, -110f);
            }
        }

        // ─── IL helpers (same primitives as L182; Program.cs is not partial) ─────────────────────

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

        /// <summary>call / callvirt / NEWOBJ. The newobj opcode is what arm (a) turns on — a constructed
        /// GameObject is not reachable through call/callvirt alone.</summary>
        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F, 0x73))
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
