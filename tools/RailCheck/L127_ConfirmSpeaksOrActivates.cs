using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.View;
using PhoenixPoint.Tactical.View.ViewModules;
using UnityEngine;
using UnityEngine.UI;

namespace RailCheck
{
    /// <summary>
    /// L127 — A CONFIRM EITHER ACTIVATES OR SAYS WHY, AND NOTHING WE ADD TO THE HUD MAY EAT THE CLICK.
    ///
    /// THE NIGHT IT WAS WORTH WRITING (2026-08-05). On a client the player selected Overwatch and confirmed
    /// it for 32 seconds: no activation, no intent, the ability state never left. The engine's own
    /// "Ability … activated" line (<c>TacticalAbility.Activate</c>:1085) never appeared once, so
    /// <c>TacticalViewState.ActivateAbility</c>:270 never ran, so the click died ABOVE our seam — and the
    /// two candidate <c>if</c>s each account for the entire symptom while logging nothing:
    /// <c>UIStateOverwatchAbilitySelected.OnInputEvent</c>:215 (<c>!IsCursorOverGUI()</c>) and
    /// <c>AbilityConfirmed</c>:300-309 (<c>if (ability.IsEnabled() &amp;&amp; …)</c>, no else). Both sit
    /// outside every law we had: L78 covers the MIRROR side, L123 only starts once an intent REACHES the
    /// host, and neither can say a word about a confirm that never became an intent at all.
    ///
    /// This law does not, and cannot, make the engine speak — the guards are the game's. What it CAN assert,
    /// and does, is that our two answers to that night stay true:
    ///   (a) the diagnostic covers EVERY confirm entry point the game ships, derived from the ENGINE's own
    ///       wiring rather than from a list we maintain, so a fourth state cannot appear uncovered;
    ///   (b) every method the diagnostic covers is genuinely on the activation path, so "no line in the log"
    ///       keeps meaning "the handler was never entered" and not "we probed the wrong method";
    ///   (c) the derivation is RUN, not read, and answers a real, non-empty, patchable set;
    ///   (d) no tactical GUI element we add carries a raycast-enabled graphic beyond its own button.
    ///
    /// WHY (d) BELONGS IN THE SAME LAW, and is not a second subject. It is the OTHER half of the same
    /// symptom, reached from the opposite end. Unity answers
    /// <c>EventSystem.IsPointerOverGameObject()</c> — which is verbatim what
    /// <c>TacticalView.IsCursorOverGUI()</c> returns (<c>TacticalView.cs</c>:801-804) — YES for ANY
    /// <c>Graphic</c> with <c>raycastTarget</c> under the cursor, visible or not. So an invisible rect of
    /// ours parked over the battlefield makes the engine's <c>:215</c> guard refuse the player's map click,
    /// silently, forever, and the tell is exactly what was reported that night: GUI clicks kept working
    /// (the ability bar opened the state) while MAP clicks died. Our cloned Ready button is the one HUD
    /// element in the mod placed deliberately BELOW the row that hosts it, so it is the one that can reach
    /// the map — and a cloned prefab brings its whole raycast footprint with it, every frame, glow and
    /// backing plate included.
    ///
    /// SCOPED TO THE TACTICAL NAMESPACE ON PURPOSE. The lobby's clones
    /// (<c>NativeWidgetFactory</c>, <c>MainMenuPatches</c>) sit on menu screens where nothing consults
    /// <c>IsCursorOverGUI</c> and there is no map behind them to shadow; widening the arm there would buy a
    /// green tick and no safety. If a menu ever grows a world the cursor can click, this arm's scope is the
    /// one line to change.
    ///
    /// ponytail: (a) reads the game's event wiring with Program's IL walker; a body it cannot parse
    /// under-reports rather than inventing an edge, so the failure mode is a missed red, never a false one.
    /// </summary>
    internal static class L127_ConfirmSpeaksOrActivates
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var gameAsm = typeof(TacticalViewState).Assembly;
            var subscribe = typeof(UIModuleAbilityConfirmationButton).GetMethod("add_OnAbilityConfirmed", All);
            if (subscribe == null)
            {
                yield return "L127 premise-changed: UIModuleAbilityConfirmationButton.OnAbilityConfirmed is no " +
                             "longer an event, so the confirm button's subscribers can no longer be derived from " +
                             "the engine's own wiring. AbilityConfirmProbe's coverage is asserting nothing until " +
                             "this is re-grounded against whatever replaced it.";
                yield break;
            }

            // ── (c) the derivation RUNS, and answers something real ────────
            List<MethodBase> targets = null;
            string blewUp = null;
            try { targets = AbilityConfirmProbe.TargetMethods().ToList(); }
            catch (Exception ex) { blewUp = ex.GetBaseException().Message; }
            if (blewUp != null)
            {
                yield return "L127 probe-threw: AbilityConfirmProbe.TargetMethods() threw '" + blewUp + "'. " +
                             "Harmony calls this during PatchAll, and PatchAll is ONE unguarded loop — a throw " +
                             "here does not cost a log line, it abandons every patch class after this one and " +
                             "takes the whole mod down (the L125 disaster, 2026-08-05).";
                yield break;
            }
            if (targets.Count == 0)
            {
                yield return "L127 probe-empty: AbilityConfirmProbe.TargetMethods() derives NO confirm entry " +
                             "point, so it patches nothing and logs nothing. The probe would be silent in exactly " +
                             "the way it exists to end, and its silence would read as 'the handler was never " +
                             "entered' — the wrong half of the fork it was built to settle.";
                yield break;
            }
            foreach (var t in targets)
            {
                if (t.GetParameters().Length != 0 || (t as MethodInfo)?.ReturnType != typeof(void))
                    yield return "L127 probe-bad-shape: " + Describe(t) + " is not a zero-argument void method, " +
                                 "so the prefix's (__instance, __originalMethod) signature cannot bind to it and " +
                                 "Harmony throws at PatchAll time.";
                if (!L125_EveryPatchBinds.Emittable(t))
                    yield return "L127 probe-unemittable: " + Describe(t) + " is closed by a fault/filter " +
                                 "handler, and TargetMethods handed it to Harmony anyway. That is an " +
                                 "InvalidProgramException at bind time and every later patch class lost with it.";
            }

            // ── (a) every state the ENGINE wires to the confirm button ─────
            var wired = gameAsm.GetTypes()
                .Where(t => typeof(TacticalViewState).IsAssignableFrom(t))
                .Where(t => t.GetMethods(All).Any(m => Program.Callees(m, gameAsm)
                        .Any(c => c.MetadataToken == subscribe.MetadataToken && c.Module == subscribe.Module)))
                .ToList();
            if (wired.Count == 0)
                yield return "L127 no-wired-state: no TacticalViewState subclass subscribes to " +
                             "UIModuleAbilityConfirmationButton.OnAbilityConfirmed any more. The confirm button " +
                             "is driven some other way now and arm (a) is measuring against an empty set — " +
                             "re-read where a confirm enters before trusting the probe's coverage.";
            foreach (var state in wired)
                if (!targets.Any(t => t.DeclaringType == state))
                    yield return "L127 probe-misses-a-wired-state: " + state.Name + " subscribes to the confirm " +
                                 "button but AbilityConfirmProbe derives no entry point on it, so a confirm " +
                                 "through that state logs NOTHING — indistinguishable from a click the engine " +
                                 "swallowed before the handler, which is the one distinction the probe exists " +
                                 "to make.";

            // ── (b) each covered method really leads to an activation ──────
            foreach (var t in targets)
                if (!ReachesActivate(t, gameAsm, 4))
                    yield return "L127 probe-off-the-activation-path: " + Describe(t) + " never reaches " +
                                 "TacticalViewState.ActivateAbility, so it is a namesake and not a confirm. A " +
                                 "probe on the wrong method logs a line that proves nothing and, worse, makes " +
                                 "the ABSENCE of a line stop meaning anything.";

            // ── (d) our tactical HUD additions present one clickable face ──
            foreach (var v in RaycastArm()) yield return v;
        }

        /// <summary>Does this method lead to an ability actually being activated? By NAME on a
        /// <see cref="TacticalViewState"/>, because the call site is a <c>callvirt</c> against the most
        /// derived declaration (<c>UIStateShoot</c> overrides it) and matching one token would answer no for
        /// the override while the behaviour is identical.</summary>
        private static bool ReachesActivate(MethodBase m, Assembly asm, int depth)
        {
            if (depth <= 0 || m == null) return false;
            foreach (var c in Program.Callees(m, asm))
            {
                if (c.Name == "ActivateAbility" && c.DeclaringType != null &&
                    typeof(TacticalViewState).IsAssignableFrom(c.DeclaringType)) return true;
                if (c.DeclaringType != null && typeof(TacticalViewState).IsAssignableFrom(c.DeclaringType) &&
                    ReachesActivate(c, asm, depth - 1)) return true;
            }
            return false;
        }

        /// <summary>
        /// ARM (d). A mod method that CLONES a native tactical widget has taken on the whole cloned raycast
        /// footprint, so it must also hand it back down to one surface, and that surface must not be
        /// GUESSED. Both halves are required and they mean different things: writing <c>raycastTarget</c>
        /// without settling which face survives is a blanket sweep that leaves the button unclickable (or
        /// spares the wrong child), and settling the face without writing <c>raycastTarget</c> silences
        /// nothing at all.
        ///
        /// "NOT GUESSED" HAS TWO HONEST SPELLINGS, and this arm demanded only the first until 2026-08-08.
        /// Reading <c>Selectable.targetGraphic</c> asks Unity which graphic is the button's clickable face;
        /// WRITING it declares one the mod built itself. The read alone was written in on the assumption
        /// that a cloned prefab always names a face — and the live tactical log falsified that assumption
        /// on the one button this arm exists for: <c>ready button raycast NOT trimmed — its Button names no
        /// targetGraphic</c>. A law that can only be satisfied by asking a question with no answer stops
        /// being a guard: the code obeyed it by reading the field, finding null, and silencing NOTHING,
        /// which left a ~250x40 invisible click-eater over the tactical map. So the write counts too, and
        /// L182 arm (d) holds <c>TacticalReadyButton.TrimRaycast</c> to the stronger of the two.
        /// </summary>
        private static IEnumerable<string> RaycastArm()
        {
            var uiAsm = typeof(Graphic).Assembly;
            var setRaycast = typeof(Graphic).GetProperty("raycastTarget")?.GetSetMethod();
            var getTarget = typeof(Selectable).GetProperty("targetGraphic")?.GetGetMethod();
            var setTarget = typeof(Selectable).GetProperty("targetGraphic")?.GetSetMethod();
            var instantiate = typeof(UnityEngine.Object).Assembly;
            if (setRaycast == null || getTarget == null || setTarget == null)
            {
                yield return "L127 premise-changed: Graphic.raycastTarget or Selectable.targetGraphic no longer " +
                             "resolves in UnityEngine.UI, so 'one clickable surface' cannot be expressed against " +
                             "this Unity version and arm (d) is asserting nothing.";
                yield break;
            }

            var cloners = typeof(TacticalReadySync).Assembly.GetTypes()
                .Where(t => t.Namespace == "Multiplayer.Tactical")
                .SelectMany(t => t.GetMethods(All))
                .Where(m => Program.Callees(m, instantiate)
                        .Any(c => c.DeclaringType == typeof(UnityEngine.Object) && c.Name == "Instantiate"))
                .ToList();
            if (cloners.Count == 0)
                yield return "L127 no-tactical-clone: nothing under Multiplayer.Tactical clones a native widget " +
                             "any more. Either the co-op Ready button is gone (in which case delete this arm) or " +
                             "it is built some way this arm cannot see — and an arm that sees nothing cannot go " +
                             "red for the thing it was written for.";

            foreach (var m in cloners)
            {
                bool silences = Reaches(m, setRaycast, 4);
                bool namesTheFace = Reaches(m, getTarget, 4) || Reaches(m, setTarget, 4);
                if (silences && namesTheFace) continue;
                yield return "L127 mod-gui-raycast-unsilenced: " + Describe(m) + " clones a native tactical " +
                             "widget but " +
                             (!silences && !namesTheFace
                                 ? "never silences one graphic's raycastTarget"
                                 : !silences
                                     ? "settles Selectable.targetGraphic without ever writing raycastTarget"
                                     : "writes raycastTarget without ever settling Selectable.targetGraphic — " +
                                       "neither reading which face Unity says is clickable nor writing one it " +
                                       "built") +
                             ". Every extra raycast-enabled graphic it carries answers " +
                             "EventSystem.IsPointerOverGameObject() for the cursor, and TacticalView." +
                             "IsCursorOverGUI() gates the tactical MAP click on exactly that — so the clone " +
                             "silently eats the player's confirm and the symptom is 'the UI responds, the map " +
                             "does not'.";
            }
        }

        /// <summary>Transitive reachability from mod code to one specific external method, walking only
        /// through the mod's OWN assembly (the hop that matters here is Build -> TrimRaycast).</summary>
        private static bool Reaches(MethodBase m, MethodBase target, int depth)
        {
            if (depth <= 0 || m == null) return false;
            if (Program.Callees(m, target.Module.Assembly)
                       .Any(c => c.MetadataToken == target.MetadataToken && c.Module == target.Module)) return true;
            foreach (var c in Program.Callees(m, m.Module.Assembly))
                if (c != m && Reaches(c, target, depth - 1)) return true;
            return false;
        }

        private static string Describe(MethodBase m) =>
            (m.DeclaringType == null ? "?" : m.DeclaringType.Name) + "." + m.Name;
    }
}
