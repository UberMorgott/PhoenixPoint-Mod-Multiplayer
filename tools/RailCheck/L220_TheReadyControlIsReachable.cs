using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using UnityEngine;
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
    ///
    /// NOT A GATE, STILL. Nothing here asserts that anything WAITS on the tally — it must not, and L119
    /// keeps it that way. This law is only about the control being hit-testable and the press being
    /// visible; a press still changes one label and gates nothing on any peer.
    ///
    /// Falsify: drop either <c>layer</c> assignment in <c>BuildGreenOverlay</c>/<c>TrimRaycast</c> → (a);
    /// delete the <c>Debug.Log</c> at the end of <c>Toggle</c> → (b); drop the <c>g.depth</c> read from
    /// <c>ReportFootprint</c> → (c); empty <see cref="FakeSeam"/> → (d).
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
            };

            if (toggle == null || report == null || premises.Any(m => m == null))
            {
                yield return "L220 premise-changed: one of TacticalReadySync.Toggle, " +
                             "TacticalReadyRowFollower.ReportFootprint, GameObject.set_layer, " +
                             "Graphic.get_depth or Debug.Log(object) no longer resolves. The seams this law " +
                             "is written over, or the members it names, have moved — every arm below is " +
                             "asserting about a shape the build no longer has.";
                yield break;
            }

            foreach (var v in ScanLayers(typeof(TacticalReadyButton), "TacticalReadyButton")) yield return v;
            foreach (var v in ScanPress(toggle, "TacticalReadySync.Toggle")) yield return v;
            foreach (var v in ScanReport(report, "TacticalReadyRowFollower.ReportFootprint")) yield return v;

            // ── arm (d): the scan must be able to SEE each violation.
            var control = ScanLayers(typeof(FakeSeam), "FakeSeam")
                .Concat(ScanPress(typeof(FakeSeam).GetMethod("Press", AllMembers), "FakeSeam.Press"))
                .Concat(ScanReport(typeof(FakeSeam).GetMethod("Report", AllMembers), "FakeSeam.Report"))
                .ToList();
            foreach (var want in new[] { "ready-widget-off-the-drawn-layer", "ready-press-is-silent",
                                         "ready-reachability-unmeasured" })
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

        /// <summary>Arm (c), over one method.</summary>
        private static IEnumerable<string> ScanReport(MethodBase report, string label)
        {
            if (!Reaches(report, "Graphic", "get_depth"))
                yield return "L220 ready-reachability-unmeasured: " + label + " must read Graphic.depth for " +
                             "the surfaces it counts. Counting raycastTarget flags answers 'how many surfaces " +
                             "could eat a map click' and says NOTHING about whether any of them can be hit — " +
                             "the shipped diagnostic reported a healthy 'raycast-enabled graphics remaining: " +
                             "1' over a button no pointer could reach. depth == -1 is uGUI's own marker for " +
                             "'the canvas never drew this', and it is the one fact that separates the two.";
        }

        /// <summary>ARM (d). Never instantiated, never registered — it exists only to be walked. One
        /// violation per arm: a child built without a layer, a press that only warns, and a report that
        /// counts surfaces without asking whether any is drawn.</summary>
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

            internal static int Report(Graphic[] graphics)
            {
                int live = 0;
                foreach (var g in graphics) if (g.raycastTarget) live++;   // (c): never reads depth
                return live;
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
