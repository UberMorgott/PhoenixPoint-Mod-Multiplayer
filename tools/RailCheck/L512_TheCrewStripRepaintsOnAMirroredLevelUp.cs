using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewModules;

namespace RailCheck
{
    /// <summary>
    /// L512 — THE CREW STRIP IS RE-DERIVED FROM THE PERSISTENT-HUD REPAINT, AND A MIRRORED LEVEL-UP IS WHAT
    /// MOVES IT. The module twin of L497.
    ///
    /// THE REPORT (owner, 2026-08-15): a soldier levels up, the green cross appears on the peer whose
    /// gesture it was, and the ally's already-open aircraft strip does not show it.
    ///
    /// NOT A REPLICATION DEFECT, and that is why L497 stayed green through it. <c>LevelProgression</c> is a
    /// covered rail type (docs/rail-baseline.txt: `covered=3/3`, `Leaf HasNewLevel (Boolean)`) and an
    /// audited <see cref="UiEventMap"/> kind that marks dirty. L497 asserts the STAT route end to end —
    /// <c>EchoStatChange</c> → <c>StatChangeEvent</c> → <c>RefreshCrewBars</c> — and never mentions
    /// <c>HasNewLevel</c> or <c>LevelUpIndicator</c>, because the cross is on the OTHER route: it is painted
    /// only by <c>AircraftCrewController.SetCrew</c>:140, which no stat echo ever re-runs.
    ///
    /// THE GAP IS THE MODULE. <c>UIModuleVehicleSelection</c> is held by <c>GeoscapeModulesData</c>, not by
    /// a view state, so <see cref="UiNativeRepaint.Table"/> has no key that can reach it, and it had ZERO
    /// references in src/. The view-state re-enter DOES re-run <c>SetVehicleInfo</c> → <c>SetCrew</c> while
    /// <c>UIStateVehicleSelected</c> is the CURRENT state — but a queued window (a modal, an event dialog)
    /// on top of the map is the current state instead, takes its own <c>Table</c> arm or the
    /// queued-window SKIP, and the strip visible behind it is then repainted by nothing at all.
    ///
    /// THE ARMS:
    ///   (a) <c>strip-not-in-the-hud-repaint</c> — IL: <c>OpenUiRepaint.RefreshPersistentHud</c> reaches
    ///       <c>RefreshVehicleCrew</c>. This is the whole claim that the module HAS a repaint owner.
    ///   (b) <c>strip-repaint-unbound</c> — the two native members it drives resolve BY SIGNATURE:
    ///       <c>UIModuleVehicleSelection.SetCrew(IEnumerable&lt;GeoCharacter&gt;, int)</c> (:401) and
    ///       <c>_currentVehicle</c> (:153). Native UI reused, never hand-drawn — so a renamed or
    ///       re-signatured native member must report itself rather than silently repaint nothing.
    ///   (c) <c>cross-not-in-the-key</c>, EXECUTED — a touched <c>GeoCharacter</c> leaf (the cross is one:
    ///       <c>LevelProgression.HasNewLevel</c>, rooted at "U#") must MOVE the strip's
    ///       <c>OpenUiRepaint.ScopeKey</c>. Since §B.8/L543 the strip is keyed on its DECLARED prefixes and
    ///       not on a hand-rolled <c>CrewSlotKey</c>, so the claim is executed one layer up — but it is the
    ///       same defect: a key that ignores the cross leaves the repaint gate closed on exactly the change
    ///       the player is waiting for.
    ///   (d) <c>strip-repaints-on-nothing</c>, POSITIVE CONTROL, EXECUTED — the same production gate
    ///       (<c>OpenUiRepaint.RepaintNeeded</c>) must return FALSE for an unchanged key. <c>SetCrew</c>
    ///       recreates its elements, and a rail flush lands ~10 times a second, so a repaint that always
    ///       fires is the L492 flicker at 10 Hz — strictly worse than the stale cross.
    ///   (a2) <c>hud-refresh-behind-the-queued-window-skip</c> — the reason arm (a) is worth anything while a
    ///       modal or an event dialog is up. <c>OpenUiRepaint.RepaintOpenGeoscapeScreen</c> is the caller that
    ///       runs <c>RefreshPersistentHud</c>; <c>Repaint</c> is the callee that holds the
    ///       <c>PauseHold.IsCurrentQueuedWindow</c> SKIP and the <c>UiNativeRepaint.TryRepaint</c> early
    ///       return. The HUD refresh must live in the FORMER and never in the latter, because a queued
    ///       window takes its own <see cref="UiNativeRepaint.Table"/> arm or the skip, while the crew strip
    ///       is still VISIBLE behind it. Move the refresh one frame down into <c>Repaint</c> and the strip
    ///       behind every open window silently stops updating — with the skip line in the log claiming, quite
    ///       correctly, that it only declined a re-enter.
    ///   (e) <c>indicator-painted-elsewhere</c> — IL: <c>AircraftCrewController.SetCrew</c> still reads
    ///       <c>LevelProgression.HasNewLevel</c>. If the game stops painting the cross there, arms (a)-(d)
    ///       are re-deriving a strip that no longer draws the thing this law is about.
    ///
    /// WHAT IT DOES NOT PROVE: not that the cross is VISIBLE (no live Unity hierarchy here — the
    /// `activeInHierarchy` guard and the Invoke are not executed); not that `HasNewLevel` reaches the peer
    /// (that is the rail baseline's `covered=3/3`); nothing about the OTHER surface that draws the same flag,
    /// <c>GeoRosterItem</c>:345 — that is L514's; and arm (a2) proves only that the HUD refresh is
    /// STRUCTURALLY above the skip, never that any particular window is currently up.
    ///
    /// NOT A QUORUM (P13): a repaint is a peer's own screen redrawing itself; nothing waits on anybody.
    ///
    /// Falsify:
    ///   • delete the <c>RefreshVehicleCrew</c> call from <c>RefreshPersistentHud</c> → (a)
    ///     [VERIFIED RED 2026-08-15, restored GREEN]
    ///   • drop "U#" from this surface's row in <c>UiNativeRepaint.DeclaredPrefixes</c> → (c)
    ///   • move the <c>RefreshPersistentHud</c> call out of <c>RepaintOpenGeoscapeScreen</c> and into
    ///     <c>Repaint</c> → (a2) [VERIFIED RED 2026-08-15, restored GREEN]
    ///   • make <c>RepaintNeeded</c> always return true → (d)
    /// </summary>
    internal static class L512_TheCrewStripRepaintsOnAMirroredLevelUp
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(OpenUiRepaint).Assembly;
            var hud = typeof(OpenUiRepaint).GetMethod("RefreshPersistentHud", All);
            var crew = typeof(OpenUiRepaint).GetMethod("RefreshVehicleCrew", All);
            if (hud == null || crew == null)
            {
                yield return "L512 premise-changed: OpenUiRepaint.RefreshPersistentHud / .RefreshVehicleCrew " +
                             "did not resolve, so nothing checks that the aircraft crew strip — a MODULE, which " +
                             "no UiNativeRepaint.Table key can reach — has any repaint owner at all. Re-point " +
                             "this law; the failure it guards is a strip that silently stops updating.";
                yield break;
            }

            // ── (a) the module has an owner in the one repaint that belongs to no view state ────────────
            if (!Program.Callees(hud, mod).Any(c => c.MetadataToken == crew.MetadataToken &&
                                                    c.Module == crew.Module))
                yield return "L512 strip-not-in-the-hud-repaint: RefreshPersistentHud no longer re-derives the " +
                             "aircraft crew strip. UIModuleVehicleSelection is held by GeoscapeModulesData, not " +
                             "by a view state, so UiNativeRepaint.Table has no key that reaches it — and while a " +
                             "queued window is the current state, the strip behind it is repainted by nothing.";

            // ── (a2) …and it runs ABOVE the queued-window skip, so a visible strip behind a modal or an
            //         event dialog is still re-derived. Structural, not ordering-by-eye: the refresh is in
            //         the CALLER, the skip is in the CALLEE, so no arrangement of statements can put the
            //         refresh behind it. ─────────────────────────────────────────────────────────────────
            var outer = typeof(OpenUiRepaint).GetMethod("RepaintOpenGeoscapeScreen", All);
            var inner = typeof(OpenUiRepaint).GetMethod("Repaint", All);
            var skip = typeof(PauseHold).GetMethod("IsCurrentQueuedWindow", All);
            if (outer == null || inner == null || skip == null)
            {
                yield return "L512 premise-changed: OpenUiRepaint.RepaintOpenGeoscapeScreen / .Repaint / " +
                             "PauseHold.IsCurrentQueuedWindow did not resolve, so nothing checks that the HUD " +
                             "refresh stays ABOVE the queued-window skip. Re-point this arm.";
            }
            else
            {
                bool skipInInner = Program.Callees(inner, mod)
                    .Any(c => c.MetadataToken == skip.MetadataToken && c.Module == skip.Module);
                bool hudInOuter = Program.Callees(outer, mod)
                    .Any(c => c.MetadataToken == hud.MetadataToken && c.Module == hud.Module);
                bool hudInInner = Program.Callees(inner, mod)
                    .Any(c => c.MetadataToken == hud.MetadataToken && c.Module == hud.Module);
                if (!skipInInner)
                    yield return "L512 premise-changed: OpenUiRepaint.Repaint no longer holds the " +
                                 "PauseHold.IsCurrentQueuedWindow skip, so this arm is asserting the HUD " +
                                 "refresh sits above a gate that moved. Find where the skip lives now.";
                else if (!hudInOuter || hudInInner)
                    yield return "L512 hud-refresh-behind-the-queued-window-skip: RefreshPersistentHud is no " +
                                 "longer run by RepaintOpenGeoscapeScreen ABOVE the skip (or has moved down " +
                                 "into Repaint, which holds it). With a modal or an event dialog current, " +
                                 "Repaint takes its own UiNativeRepaint.Table arm or returns at the queued- " +
                                 "window SKIP — and the crew strip is still VISIBLE behind that window. The " +
                                 "strip would then be repainted by nothing at all, while the log line " +
                                 "truthfully reports only that a re-enter was declined.";
            }

            // ── (b) the native members it drives are bound by SIGNATURE ─────────────────────────────────
            var setCrew = typeof(UIModuleVehicleSelection).GetMethod("SetCrew", All, null,
                new[] { typeof(IEnumerable<GeoCharacter>), typeof(int) }, null);
            var current = typeof(UIModuleVehicleSelection).GetField("_currentVehicle", All);
            if (setCrew == null || current == null)
                yield return "L512 strip-repaint-unbound: UIModuleVehicleSelection.SetCrew(IEnumerable<" +
                             "GeoCharacter>, int) or ._currentVehicle no longer resolves, so the repaint above " +
                             "binds nothing and returns silently. Native UI is REUSED here, never hand-drawn — " +
                             "which is the right call and is exactly why a renamed native member has to be loud.";

            // ── (c) EXECUTED: the green cross is in the repaint key ─────────────────────────────────────
            // The hand-rolled CrewSlotKey is GONE (§B.8, L543): the strip is now keyed on ScopeKey, a
            // generation that advances exactly when a path under one of the prefixes the strip DECLARED was
            // touched. So the same claim is executed one layer up — a GeoCharacter leaf (LevelProgression
            // .HasNewLevel is one: docs/rail-baseline.txt LevelProgression covered=3/3, rooted at "U#" by
            // IdentityResolver.cs:145) must MOVE this strip's key.
            const string crewSurface = "UIModuleVehicleSelection";
            OpenUiRepaint.Reset();
            string keyOff = OpenUiRepaint.ScopeKey(crewSurface);
            OpenUiRepaint.BumpScopeGenerations(
                new[] { "U#7.Progression.LevelProgression.HasNewLevel" });
            string keyOn = OpenUiRepaint.ScopeKey(crewSurface);
            if (string.Equals(keyOff, keyOn, StringComparison.Ordinal))
                yield return "L512 cross-not-in-the-key: a touched GeoCharacter leaf produced the SAME crew " +
                             "key ('" + keyOn + "') as an untouched one, so the repaint gate stays closed on " +
                             "the exact change the player is waiting for. The green level-up cross is painted " +
                             "in one place only (AircraftCrewController.SetCrew:140) and nothing else re-runs " +
                             "it. Most likely cause: \"U#\" was dropped from this surface's row in " +
                             "UiNativeRepaint.DeclaredPrefixes.";

            // ── (d) POSITIVE CONTROL, EXECUTED: an unchanged key does NOT repaint ───────────────────────
            const string strip = "L512-probe";
            OpenUiRepaint.RepaintNeeded(strip, keyOff);                 // first call always repaints
            if (OpenUiRepaint.RepaintNeeded(strip, keyOff))
                yield return "L512 strip-repaints-on-nothing: the repaint gate fires again for an UNCHANGED " +
                             "crew key, so arm (c) would pass with the key ignored entirely. SetCrew recreates " +
                             "every slot element and a rail flush lands ~10 times a second: that is the L492 " +
                             "flicker back, at 10 Hz, on the strip the player stares at all game.";
            if (!OpenUiRepaint.RepaintNeeded(strip, keyOn))
                yield return "L512 strip-does-not-repaint-on-a-change: the repaint gate refused a CHANGED crew " +
                             "key. Bounding the repaint must never cost the repaint itself — REACTIVITY is the " +
                             "hard mandate and the flicker bound is the concession, not the other way round.";
            OpenUiRepaint.RepaintNeeded(strip, null);                   // leave no probe key behind

            // ── (e) the game still paints the cross where this law says it does ─────────────────────────
            var nativeSetCrew = typeof(AircraftCrewController).GetMethod("SetCrew", All);
            var hasNewLevel = typeof(LevelProgression).GetField("HasNewLevel", All);
            if (nativeSetCrew == null || hasNewLevel == null || !Reads(nativeSetCrew, hasNewLevel.MetadataToken))
                yield return "L512 indicator-painted-elsewhere: AircraftCrewController.SetCrew no longer reads " +
                             "LevelProgression.HasNewLevel (or one of them stopped resolving), so the strip this " +
                             "law re-derives is not the thing that draws the green cross any more. Find the new " +
                             "paint site before believing any arm above.";
        }

        /// <summary>Raw 4-byte metadata-token scan of <paramref name="m"/>'s IL (the L107/L475 pattern).</summary>
        private static bool Reads(MethodBase m, int token)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) return false;
            for (int i = 0; i + 4 <= il.Length; i++)
                if (BitConverter.ToInt32(il, i) == token) return true;
            return false;
        }
    }
}
