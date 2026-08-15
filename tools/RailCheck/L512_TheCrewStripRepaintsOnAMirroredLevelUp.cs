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
    ///   (c) <c>cross-not-in-the-key</c>, EXECUTED — the production <c>OpenUiRepaint.CrewSlotKey</c> must
    ///       return a DIFFERENT key when <c>LevelProgression.HasNewLevel</c> flips, with everything else
    ///       equal. This is the defect itself: a key that ignores the cross would leave the repaint gate
    ///       closed on exactly the change the player is waiting for.
    ///   (d) <c>strip-repaints-on-nothing</c>, POSITIVE CONTROL, EXECUTED — the same production gate
    ///       (<c>OpenUiRepaint.RepaintNeeded</c>) must return FALSE for an unchanged key. <c>SetCrew</c>
    ///       recreates its elements, and a rail flush lands ~10 times a second, so a repaint that always
    ///       fires is the L492 flicker at 10 Hz — strictly worse than the stale cross.
    ///   (e) <c>indicator-painted-elsewhere</c> — IL: <c>AircraftCrewController.SetCrew</c> still reads
    ///       <c>LevelProgression.HasNewLevel</c>. If the game stops painting the cross there, arms (a)-(d)
    ///       are re-deriving a strip that no longer draws the thing this law is about.
    ///
    /// WHAT IT DOES NOT PROVE: not that the cross is VISIBLE (no live Unity hierarchy here — the
    /// `activeInHierarchy` guard and the Invoke are not executed); not that `HasNewLevel` reaches the peer
    /// (that is the rail baseline's `covered=3/3`); and nothing about the OTHER surface that draws the same
    /// flag, <c>GeoRosterItem</c>:345, which belongs to <c>UIStateGeoRoster</c> and is re-derived by that
    /// state's own re-enter.
    ///
    /// NOT A QUORUM (P13): a repaint is a peer's own screen redrawing itself; nothing waits on anybody.
    ///
    /// Falsify:
    ///   • delete the <c>RefreshVehicleCrew</c> call from <c>RefreshPersistentHud</c> → (a)
    ///     [VERIFIED RED 2026-08-15, restored GREEN]
    ///   • drop <c>HasNewLevel</c> from <c>OpenUiRepaint.CrewSlotKey</c> → (c)
    ///     [VERIFIED RED 2026-08-15, restored GREEN]
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
            var off = FormatterServices.GetUninitializedObject(typeof(LevelProgression)) as LevelProgression;
            var on = FormatterServices.GetUninitializedObject(typeof(LevelProgression)) as LevelProgression;
            if (off == null || on == null)
            {
                yield return "L512 premise-changed: a bare LevelProgression could not be minted, so the executed " +
                             "arms below did not run. Re-ground the harness rather than trusting them.";
                yield break;
            }
            on.HasNewLevel = true;
            string keyOff = OpenUiRepaint.CrewSlotKey("Chen", 1, off, 0);
            string keyOn = OpenUiRepaint.CrewSlotKey("Chen", 1, on, 0);
            if (string.Equals(keyOff, keyOn, StringComparison.Ordinal))
                yield return "L512 cross-not-in-the-key: a soldier with HasNewLevel set produces the SAME crew " +
                             "key ('" + keyOn + "') as one without it, so the repaint gate stays closed on the " +
                             "exact change the player is waiting for. The green level-up cross is painted in one " +
                             "place only (AircraftCrewController.SetCrew:140) and nothing else re-runs it.";

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
