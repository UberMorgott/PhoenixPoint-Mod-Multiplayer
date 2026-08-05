using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// THE CONFIRM EITHER ACTIVATES OR SAYS WHY — DIAGNOSTIC ONLY, IT CHANGES NOTHING.
    ///
    /// THE NIGHT IT WAS WORTH WRITING (2026-08-05). On a client the player selected Overwatch, the ability
    /// state opened, and confirming did NOTHING for 32 seconds of clicking: no local activation, no intent,
    /// the UI state never left. <c>TacticalAbility.Activate</c>:1085 — the game's own
    /// "Ability … activated" line — never appeared once, which proves
    /// <c>TacticalViewState.ActivateAbility</c>:270 never ran. Our only seam there
    /// (<see cref="TacticalCommandSync"/>'s <c>AbilityActivateCapture</c>) is a void prefix that cannot
    /// block, so the swallow was ABOVE us, in the engine, and two candidate <c>if</c>s could each account
    /// for the whole symptom with no log line between them:
    ///   · <c>UIStateOverwatchAbilitySelected.OnInputEvent</c>:215 —
    ///     <c>ev.Name == "Select" &amp;&amp; !Context.View.IsCursorOverGUI()</c>. A cursor the engine thinks
    ///     is over a GUI element never reaches <c>AbilityConfirmed()</c> at all.
    ///   · <c>UIStateOverwatchAbilitySelected.AbilityConfirmed</c>:300-309 —
    ///     <c>if (_overwatchAbility.IsEnabled() &amp;&amp; …)</c>, with NO else. A disabled ability is
    ///     dropped in total silence.
    /// Both are the project's signature bug shape (a silent swallow), and neither is reachable by reasoning
    /// from a log that does not exist. So the fork is settled by ONE line, at the ONE place that
    /// distinguishes them:
    ///   · line present, <c>NotDisabled</c>, activation follows → the confirm path is fine, look elsewhere;
    ///   · line present, a named disabled state → the <c>:302</c> fork, WITH the reason;
    ///   · no line at all while the player clicks → the <c>:215</c> fork, because the handler was never
    ///     entered — and that absence is only evidence BECAUSE the line is unconditional on entry.
    /// That last case is why this logs EVERY entry rather than only refusals, which is more than was asked
    /// for and is the whole diagnostic value: a probe that stays quiet on the good path cannot tell
    /// "refused" from "never reached" from "never bound".
    ///
    /// TARGETS ARE DERIVED, NOT LISTED. Every confirm entry point is a zero-argument void instance method
    /// named <c>AbilityConfirmed</c> or <c>ConfirmShoot</c> declared on a <see cref="TacticalViewState"/>
    /// subclass; the walk finds the three the game ships today
    /// (<c>UIStateOverwatchAbilitySelected.AbilityConfirmed</c>,
    /// <c>UIStateAbilitySelected.AbilityConfirmed</c>, <c>UIStateShoot.ConfirmShoot</c> — inherited
    /// unoverridden by <c>UIStateFreeCam</c>) and would find a fourth the day the game grows one. A hand
    /// list would go stale in exactly the silence this exists to end. RailCheck L127 arm (a) asserts the
    /// derivation still covers the shipped set.
    ///
    /// PRIVATE IS NOT A PROBLEM; A FAULT HANDLER WOULD BE. <c>AbilityConfirmed</c> is private on both
    /// states — irrelevant to Harmony, which resolves by metadata and never by C# accessibility. What DOES
    /// decide patchability is whether the body is closed by a <c>fault</c>/<c>filter</c> clause: Harmony
    /// cannot rebuild those and the attempt throws <c>InvalidProgramException</c> at bind time, taking
    /// every later patch class in <c>PatchAll</c> with it (the L125 disaster, 2026-08-05). All three
    /// bodies are plain — no <c>try</c> at all in any of them — and the filter below re-checks that per
    /// target at bind time rather than trusting today's reading, so a future body that grows a
    /// <c>using</c> is skipped instead of killing the mod.
    /// </summary>
    [HarmonyPatch]
    internal static class AbilityConfirmProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.DeclaredOnly;

        /// <summary>The confirm entry points, by SHAPE. RailCheck <c>L125</c>'s emittability rule is applied
        /// here too — a target Harmony cannot rebuild is dropped, never patched.</summary>
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            var found = typeof(TacticalViewState).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(TacticalViewState).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(All))
                .Where(m => (m.Name == "AbilityConfirmed" || m.Name == "ConfirmShoot") &&
                            m.ReturnType == typeof(void) && m.GetParameters().Length == 0)
                .Where(Emittable)
                .Cast<MethodBase>()
                .ToList();
            Debug.Log("[Multiplayer][tac] confirm probe covers " + found.Count + " entry point(s): " +
                      string.Join(", ", found.Select(m => m.DeclaringType.Name + "." + m.Name).ToArray()));
            return found;
        }

        /// <summary>The one metadata fact that decides whether Harmony can rebuild a body at all — the same
        /// rule RailCheck L125 asserts, applied at bind time so a body that grows a fault clause costs us a
        /// missing log line and not the whole mod.</summary>
        private static bool Emittable(MethodBase m)
        {
            var body = m.GetMethodBody();
            if (body == null) return false;
            foreach (ExceptionHandlingClause c in body.ExceptionHandlingClauses)
                if (c.Flags == ExceptionHandlingClauseOptions.Fault ||
                    c.Flags == ExceptionHandlingClauseOptions.Filter) return false;
            return true;
        }

        /// <summary>Reads only. Wrapped whole because a diagnostic that can throw is worse than no
        /// diagnostic: it would turn a silent confirm into a broken one.</summary>
        private static void Prefix(TacticalViewState __instance, MethodBase __originalMethod)
        {
            try
            {
                var ability = FirstAbility(__instance);
                var disabled = ability == null ? (AbilityDisabledState?)null : ability.GetDisabledState();
                Debug.Log("[Multiplayer][tac] confirm " + __instance.GetType().Name + "." +
                          __originalMethod.Name +
                          " actor=" + (ability == null || ability.TacticalActorBase == null
                              ? "?" : ability.TacticalActorBase.DisplayName) +
                          " ability=" + (ability == null ? "?" : ability.GetType().Name) +
                          " disabledState=" + (disabled == null ? "?" : disabled.ToString()) +
                          " cursorOverGUI=" + CursorOverGui(__instance) +
                          (disabled != null && disabled != AbilityDisabledState.NotDisabled
                              ? " — WILL REFUSE SILENTLY (the engine's guard drops this confirm with no else)"
                              : string.Empty));
            }
            catch { }
        }

        /// <summary>The ability the state is confirming, by shape rather than by field name: the three
        /// states call it <c>_overwatchAbility</c>, <c>_selectedAbility</c> and <c>_ability</c>, and the
        /// only thing they agree on is the TYPE.</summary>
        private static TacticalAbility FirstAbility(TacticalViewState state)
        {
            for (var t = state.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(All))
                    if (typeof(TacticalAbility).IsAssignableFrom(f.FieldType))
                    {
                        var v = f.GetValue(state) as TacticalAbility;
                        if (v != null) return v;
                    }
            return null;
        }

        /// <summary>The very flag <c>OnInputEvent</c>:215 gates the map click on, read through the state's
        /// own protected <c>Context</c> so it is the same answer the engine got and not a second reading.</summary>
        private static string CursorOverGui(TacticalViewState state)
        {
            var ctx = Traverse.Create(state).Property("Context").GetValue<TacticalViewContext>();
            if (ctx == null || ctx.View == null) return "?";
            return ctx.View.IsCursorOverGUI().ToString();
        }
    }
}
