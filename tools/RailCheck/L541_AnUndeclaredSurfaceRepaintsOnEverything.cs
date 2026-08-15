using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L541 — AN UNDECLARED SURFACE REPAINTS ON EVERYTHING. ASSERTED, NEVER ASSUMED.
    ///
    /// This is the safe-degradation property and it is the reason scoping can be added to a live game at
    /// all: declaration is opt-in to SCOPE, never opt-in to reactivity, so a surface nobody has declared —
    /// which is every surface but the declared few, INCLUDING another mod's own panels — keeps today's
    /// behaviour and can never go stale. A stale screen is a defect in this repo, not a cosmetic issue,
    /// and the whole of §B is only safe while this arm is green.
    ///
    /// R3 is the risk this law also covers from the other side: a declaration that stops at the root
    /// silently reverts a surface to repaint-on-everything. That is SAFE but INVISIBLE, so arm (e)
    /// executes a declared surface in both directions — a useless declaration shows up as a surface that
    /// repaints on a path it declared nothing about.
    ///
    /// ARMS, all EXECUTED against the real OpenUiRepaint.SurfaceRepaints with no live screen:
    ///   (a) undeclared-surface-scoped — a surface with no row in DeclaredPrefixes repaints.
    ///   (b) unknown-surface-scoped — a null surface name repaints.
    ///   (c) null-path-scoped — a null path inside the touched set repaints, because an unknown path
    ///       cannot be proven irrelevant.
    ///   (d) empty-declaration-scoped — a row present but empty repaints; an empty array must not read as
    ///       "reads nothing", because there is no way to declare that.
    ///   (e) declared-surface-never-scopes / declared-surface-always-repaints — the declared surface must
    ///       refuse an untouched prefix AND accept a touched one. Without both, SurfaceRepaints could be
    ///       a constant and every other arm would be vacuous.
    ///   (f) nothing-touched-repaints — an EMPTY touched set must NOT repaint, or the scoping buys
    ///       nothing at all.
    ///
    /// ROLES SEPARATED (§C.3): SurfaceRepaints is a pure function of (surface name, path set) with no
    /// peer, no session and no role — nothing role-dependent exists for one role to mask.
    ///
    /// Falsify (compile-valid src mutations, each named): make SurfaceRepaints return false for an
    /// undeclared surface → (a); drop the `path == null` guard → (c); treat `prefixes.Length == 0` as a
    /// declaration that matches nothing → (d); `=> true;` → (f); `=> false;` → (e).
    /// </summary>
    internal static class L541_AnUndeclaredSurfaceRepaintsOnEverything
    {
        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(OpenUiRepaint);
            var decide = repaint.GetMethod("SurfaceRepaints", BindingFlags.Static | BindingFlags.NonPublic |
                                                              BindingFlags.Public);
            if (decide == null || UiNativeRepaint.DeclaredPrefixes == null)
            {
                yield return "L541 premise-changed: OpenUiRepaint.SurfaceRepaints or " +
                             "UiNativeRepaint.DeclaredPrefixes did not resolve. Re-point this law before " +
                             "believing the verdict — while it cannot see the decision, every surface in " +
                             "the game is unprotected.";
                yield break;
            }

            var anyPath = new List<string> { "S#76.SerializationData.HavenData.AssignedResearchId" };

            if (!OpenUiRepaint.SurfaceRepaints("UIStateNoSuchScreenHasEverExisted", anyPath))
                yield return "L541 undeclared-surface-scoped: a surface with no declaration refused a " +
                             "repaint. NO DECLARATION MUST MEAN REPAINT ON EVERYTHING — declaration is " +
                             "opt-in to SCOPE, never opt-in to reactivity, and a forgotten surface must " +
                             "degrade to today's behaviour, never to stale data.";

            if (!OpenUiRepaint.SurfaceRepaints(null, anyPath))
                yield return "L541 unknown-surface-scoped: a NULL surface name refused a repaint. The " +
                             "current view state is legitimately null for a single frame, and that frame " +
                             "must not be able to swallow a change.";

            if (!OpenUiRepaint.SurfaceRepaints("UIStateEditSoldier", new List<string> { null }))
                yield return "L541 null-path-scoped: a null path inside the touched set was scoped away. " +
                             "An unknown path cannot be proven irrelevant to anything.";

            // (d) an EMPTY declaration must behave like NO declaration.
            var savedEmpty = AddTemporaryRow("UIStateL541EmptyProbe", new string[0]);
            if (!OpenUiRepaint.SurfaceRepaints("UIStateL541EmptyProbe", anyPath))
                yield return "L541 empty-declaration-scoped: an EMPTY prefix array read as 'this surface " +
                             "reads nothing'. There is no way to declare that, by design — an empty row " +
                             "is a row somebody has not finished writing.";
            RemoveTemporaryRow("UIStateL541EmptyProbe", savedEmpty);

            // (e) BOTH DIRECTIONS on a real declared surface.
            var saved = AddTemporaryRow("UIStateL541Probe", new[] { "U#" });
            if (OpenUiRepaint.SurfaceRepaints("UIStateL541Probe",
                    new List<string> { "S#76.SerializationData.HavenData.AssignedResearchId" }))
                yield return "L541 declared-surface-never-scopes: a surface declaring only 'U#' repainted " +
                             "for a path under 'S#'. This is the whole point of the work — an unrelated " +
                             "peer's site tick must stop repainting a screen that provably cannot show it.";
            if (!OpenUiRepaint.SurfaceRepaints("UIStateL541Probe",
                    new List<string> { "U#4.SerializationData.Progression" }))
                yield return "L541 declared-surface-always-repaints: a surface declaring 'U#' refused a " +
                             "repaint for a path under 'U#'. Without this direction SurfaceRepaints could " +
                             "simply return false and every arm above would be vacuous — and every " +
                             "declared screen would go stale.";
            RemoveTemporaryRow("UIStateL541Probe", saved);

            // (f) POSITIVE CONTROL, the other way: nothing touched, nothing repainted.
            if (OpenUiRepaint.SurfaceRepaints("UIStateEditSoldier", new List<string>()))
                yield return "L541 positive-control: an EMPTY touched set still demanded a repaint, so the " +
                             "scoping buys nothing and every arm above is measuring a function that " +
                             "always says yes.";
        }

        /// <summary>Insert a probe row so the declared-surface arms do not depend on which real screens
        /// happen to be declared today. Returns the previous value so it can be put back exactly.</summary>
        private static string[] AddTemporaryRow(string name, string[] prefixes)
        {
            string[] previous;
            UiNativeRepaint.DeclaredPrefixes.TryGetValue(name, out previous);
            UiNativeRepaint.DeclaredPrefixes[name] = prefixes;
            return previous;
        }

        private static void RemoveTemporaryRow(string name, string[] previous)
        {
            if (previous == null) UiNativeRepaint.DeclaredPrefixes.Remove(name);
            else UiNativeRepaint.DeclaredPrefixes[name] = previous;
        }
    }
}
