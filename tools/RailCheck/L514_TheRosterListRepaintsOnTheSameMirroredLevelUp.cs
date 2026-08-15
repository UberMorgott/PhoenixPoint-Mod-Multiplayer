using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewControllers.Roster;

namespace RailCheck
{
    /// <summary>
    /// L514 — THE GREEN LEVEL-UP CROSS HAS TWO PAINT SITES, AND BOTH ARE OWNED. The list twin of L512.
    ///
    /// L512 closed the crew strip (<c>AircraftCrewController.SetCrew</c>:140). It closed ONE of the two
    /// places the game switches a level-up indicator on: the other is <c>GeoRosterItem</c>:345, which reads
    /// the SAME flag (<c>Character.Progression.LevelProgression.HasNewLevel</c>) and belongs to a DIFFERENT
    /// owner — <c>UIModuleGeoRoster</c>, held by <c>GeoscapeModulesData</c>:30. A module, again: no
    /// <see cref="UiNativeRepaint.Table"/> key can reach it, it had ZERO repaint references in src/, and
    /// re-entering the roster VIEW STATE only re-derives it while that state is the CURRENT one — not while
    /// a queued window is up in front of it, and not while the module rides another state.
    ///
    /// So this is not "L512 again": it is the second half of the same defect, and a peer could still watch a
    /// stale cross on the list after L512 fixed the strip.
    ///
    /// THE ARMS:
    ///   (a) <c>list-not-in-the-hud-repaint</c> — IL: <c>OpenUiRepaint.RefreshPersistentHud</c> reaches
    ///       <c>RefreshRosterSlots</c>. The whole claim that this module HAS a repaint owner.
    ///   (b) <c>list-repaint-unbound</c> — the native member it drives resolves BY SIGNATURE:
    ///       <c>GeoRosterItem.UpdateCharacterData()</c> (:239, no args) — the game's own per-slot paint,
    ///       exactly what <c>GeoRosterItem.Init</c>:211 calls. Native UI reused, never hand-drawn, and the
    ///       notification GameObject is never poked directly — so a renamed native member must be loud
    ///       rather than silently repaint nothing.
    ///   (c) <c>cross-not-in-the-list-key</c>, EXECUTED — a touched <c>GeoCharacter</c> leaf (the cross is
    ///       one: <c>HasNewLevel</c>, rooted at "U#") must MOVE this list's <c>OpenUiRepaint.ScopeKey</c>.
    ///       Since §B.8/L543 the list is keyed on its DECLARED prefixes, not on a hand-rolled
    ///       <c>RosterSignature</c>/<c>CrewSlotKey</c>. Shared with L512 on purpose: ONE mechanism for both
    ///       surfaces of one flag means the two can never disagree about whether the cross moved.
    ///   (d) <c>list-repaints-on-nothing</c>, POSITIVE CONTROL, EXECUTED — the same production gate
    ///       (<c>OpenUiRepaint.RepaintNeeded</c>) must refuse an UNCHANGED key. The whole roster is dozens
    ///       of slots and the flush lands ~10 times a second.
    ///   (e) <c>indicator-painted-elsewhere</c> — IL: <c>GeoRosterItem.UpdateLocations</c> still reads
    ///       <c>LevelProgression.HasNewLevel</c>, and <c>UpdateCharacterData</c> still reaches it. If either
    ///       stops being true, this law re-derives a list that no longer draws the thing it is about.
    ///
    /// WHAT IT DOES NOT PROVE: not that the cross is VISIBLE (no live Unity hierarchy here — the
    /// `activeInHierarchy` guards and the Invoke are not executed); not that <c>HasNewLevel</c> REACHES the
    /// peer (that is the rail baseline's `covered=3/3` on <c>LevelProgression</c>); not that these are the
    /// only two paint sites in the game — only that the two known ones both have an owner; and nothing about
    /// the crew strip, which is L512's.
    ///
    /// NOT A QUORUM (P13): a repaint is one peer's own screen redrawing itself; nothing waits on anybody.
    ///
    /// Falsify:
    ///   • delete the <c>RefreshRosterSlots</c> call from <c>RefreshPersistentHud</c> → (a)
    ///     [VERIFIED RED 2026-08-15, restored GREEN]
    /// </summary>
    internal static class L514_TheRosterListRepaintsOnTheSameMirroredLevelUp
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(OpenUiRepaint).Assembly;
            var hud = typeof(OpenUiRepaint).GetMethod("RefreshPersistentHud", All);
            var slots = typeof(OpenUiRepaint).GetMethod("RefreshRosterSlots", All);
            if (hud == null || slots == null)
            {
                yield return "L514 premise-changed: OpenUiRepaint.RefreshPersistentHud / .RefreshRosterSlots " +
                             "did not resolve, so nothing checks that the roster list — the SECOND surface that " +
                             "paints the green level-up cross, and a MODULE no UiNativeRepaint.Table key can " +
                             "reach — has any repaint owner at all. Re-point this law.";
                yield break;
            }

            // ── (a) the module has an owner in the one repaint that belongs to no view state ────────────
            if (!Program.Callees(hud, mod).Any(c => c.MetadataToken == slots.MetadataToken &&
                                                    c.Module == slots.Module))
                yield return "L514 list-not-in-the-hud-repaint: RefreshPersistentHud no longer re-derives the " +
                             "roster list. UIModuleGeoRoster is held by GeoscapeModulesData, not by a view " +
                             "state, so the roster SCREEN's re-enter is the only thing that ever touched it — " +
                             "and that only runs while that screen is the CURRENT state.";

            // ── (b) the native per-slot paint is bound by SIGNATURE ─────────────────────────────────────
            var update = typeof(GeoRosterItem).GetMethod("UpdateCharacterData", All, null, Type.EmptyTypes, null);
            var character = typeof(GeoRosterItem).GetProperty("Character", All);
            if (update == null || character == null)
                yield return "L514 list-repaint-unbound: GeoRosterItem.UpdateCharacterData() or .Character no " +
                             "longer resolves, so the repaint above binds nothing and returns silently. The " +
                             "game's own per-slot paint is REUSED here — the notification GameObject is never " +
                             "toggled by hand — which is why a renamed native member has to be loud.";

            // ── (c) EXECUTED: the cross is in the key this list gates on ────────────────────────────────
            // The hand-rolled CrewSlotKey/RosterSignature are GONE (§B.8, L543); the list is keyed on
            // ScopeKey, a generation that advances exactly when a path under one of the prefixes this list
            // DECLARED was touched. Same claim, one layer up: a GeoCharacter leaf (HasNewLevel is one,
            // rooted at "U#" by IdentityResolver.cs:145) must MOVE the list's key. Shared with L512 on
            // purpose — one mechanism for both surfaces of one flag.
            const string rosterSurface = "UIModuleGeoRoster";
            OpenUiRepaint.Reset();
            string keyOff = OpenUiRepaint.ScopeKey(rosterSurface);
            OpenUiRepaint.BumpScopeGenerations(
                new[] { "U#7.Progression.LevelProgression.HasNewLevel" });
            string keyOn = OpenUiRepaint.ScopeKey(rosterSurface);
            if (string.Equals(keyOff, keyOn, StringComparison.Ordinal))
                yield return "L514 cross-not-in-the-list-key: a touched GeoCharacter leaf produced the SAME " +
                             "slot key ('" + keyOn + "') as an untouched one, so the roster repaint gate stays " +
                             "closed on exactly the change the player is waiting for. Most likely cause: " +
                             "\"U#\" was dropped from this surface's row in UiNativeRepaint.DeclaredPrefixes.";

            // ── (d) POSITIVE CONTROL, EXECUTED: an unchanged key does NOT repaint ───────────────────────
            const string probe = "L514-probe";
            OpenUiRepaint.RepaintNeeded(probe, keyOff);                 // first call always repaints
            if (OpenUiRepaint.RepaintNeeded(probe, keyOff))
                yield return "L514 list-repaints-on-nothing: the repaint gate fires again for an UNCHANGED " +
                             "roster key, so arm (c) would pass with the key ignored entirely — every slot in " +
                             "the roster repainted ~10 times a second for nothing.";
            if (!OpenUiRepaint.RepaintNeeded(probe, keyOn))
                yield return "L514 list-does-not-repaint-on-a-change: the repaint gate refused a CHANGED roster " +
                             "key. Bounding the repaint must never cost the repaint itself — REACTIVITY is the " +
                             "hard mandate and the flush bound is the concession, not the other way round.";
            OpenUiRepaint.RepaintNeeded(probe, null);                   // leave no probe key behind

            // ── (e) the game still paints the cross where this law says it does ─────────────────────────
            var locations = typeof(GeoRosterItem).GetMethod("UpdateLocations", All, null, Type.EmptyTypes, null);
            var hasNewLevel = typeof(PhoenixPoint.Common.Entities.Characters.LevelProgression)
                .GetField("HasNewLevel", All);
            if (locations == null || hasNewLevel == null || !Program.ReadsField(locations, hasNewLevel))
                yield return "L514 indicator-painted-elsewhere: GeoRosterItem.UpdateLocations no longer reads " +
                             "LevelProgression.HasNewLevel (or one of them stopped resolving), so the list this " +
                             "law re-derives is not the thing that draws the cross any more. Find the new paint " +
                             "site before believing any arm above.";
            if (update != null && locations != null &&
                !Program.Callees(update, typeof(GeoRosterItem).Assembly)
                        .Any(c => c.MetadataToken == locations.MetadataToken))
                yield return "L514 list-paint-does-not-reach-the-cross: GeoRosterItem.UpdateCharacterData no " +
                             "longer reaches UpdateLocations, which is where the level-up notification is " +
                             "switched on — so the per-slot paint this law drives would refresh everything " +
                             "EXCEPT the indicator it exists for.";
        }
    }
}
