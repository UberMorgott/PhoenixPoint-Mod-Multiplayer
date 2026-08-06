using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Addons;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.DataObjects;

namespace RailCheck
{
    /// <summary>
    /// L148 — A MIRRORED IDENTITY CHANGE REPAINTS THE OPEN SQUAD/CUSTOMIZATION SCREEN. NOBODY LEAVES THE
    /// SCREEN AND COMES BACK TO SEE ANOTHER PLAYER'S RENAME OR RECOLOUR.
    ///
    /// THE REPORT (2026-08-06, two peers): "when I renamed my soldier it did NOT show up reactively for the
    /// other players — they had to leave the squad-management screen entirely to the geoscape and come back,
    /// only then did the nickname update. Same with customization — take off a helmet, repaint: at first it
    /// did not work at all, it only took effect when someone re-entered; later the helmet started working."
    ///
    /// THE STATE WAS NEVER MISSING AND THE REPAINT WAS NEVER MISSING. `369eaaf` landed the identity intent
    /// (0xAF op 12) and the client log proves the receiving half ran: `22:37:09.073 UiEventMap: no per-kind
    /// mapping for CharacterIdentity — universal open-screen repaint`. So the delta arrived, the screen was
    /// marked, the flush repainted it — and it painted the OLD values, because identity is the one kind whose
    /// entire presentation sits behind two caches the game invalidates ONLY from its own write path:
    ///
    ///   (1) THE NAME IS A PER-VISIT SNAPSHOT. `UnitDisplayData.Name` is assigned once, in the constructor,
    ///       from `character.GetName()` (UnitDisplayData.cs:41), and EVERY name label on these screens reads
    ///       that copy (`UIModuleActorCycle.RefreshSoldierInfo`:514 → `CurrentUnit.Name`). No refresh path
    ///       re-reads the character. The GAME KNOWS THIS: its own rename handler hand-patches the copy
    ///       (`RenameCharacter`:743) precisely because nothing else would — which is also the proof that this
    ///       is a cache and not an accident. The sibling fields are NOT snapshots (`GameTags` is the live
    ///       list reference, the item lists are deferred LINQ), so the name is the whole of hole (1).
    ///
    ///   (2) THE MODEL REBUILD IS SKIPPED FOR AN IDENTITY-ONLY CHANGE. `UIModuleActorCycle.DisplaySoldier`
    ///       — the funnel every one of these screens repaints through — pushes the fresh `GameTags` into the
    ///       addons manager with autorefresh OFF (:613) and then EARLY-RETURNS at :636-644 whenever the addon
    ///       set (armour + weapon) is unchanged, never reaching `CommonCharacterUtils.RebuildCharacter`.
    ///       A recolour, a new face, a haircut change no armour, so they take that return every time; and
    ///       turning autorefresh back on afterwards (:642) does not replay a change made while it was off
    ///       (`AddonsManager.SetAutorefreshOnTagsChanged`:197 only flips a flag). REMOVING A HELMET DOES
    ///       change the addon set — which is exactly, and only, why the user saw the helmet start working
    ///       while the recolour never did. That detail is written down here because it is the thing that
    ///       makes the bug look intermittent and sends the next reader hunting a race that does not exist.
    ///
    /// THE FIX IS TWO CACHE INVALIDATIONS, NOT A REPAINT OF OUR OWN. `UiEventMap.ReseedIdentityDisplay`
    /// re-reads every cached `UnitDisplayData.Name` from its own `BaseObject` (the game's own hand-patch,
    /// generalised to the whole cache because one batch can rename several), and EMPTIES
    /// `AddonsCharacterBuilder.Addons` — the very list the early-out compares against — so the game's own
    /// next `DisplaySoldier` takes the rebuild branch and refills it (`RebuildCharacter` opens with
    /// `addons.Clear()`, CommonCharacterUtils.cs:56-58). Nothing is torn off the model by clearing it: that
    /// list is the REQUEST for the next rebuild, never a description of what is currently worn.
    ///
    /// ARMS. (a)-(b) EXECUTE the two PREMISES against the real game assembly, because they are what the fix
    /// is worth: if a game update makes `Name` a live read, or stops comparing `Addons`, this workaround is
    /// dead weight and the law says so instead of rotting. (c)-(f) pin the shipped seam structurally — no
    /// console host can stand up a `UIModuleActorCycle`, so "the caches are actually invalidated" is asserted
    /// as the IL reaching the invalidations. (g) keeps the repaint itself wired.
    ///
    /// FALSIFY: delete the `CharacterIdentity` arm from `UiEventMap.Fire` → (c); delete the name loop →
    /// (d); delete the `Addons.Clear()` → (e); delete the `RefreshSoldierInfo` call → (f).
    ///
    /// STATED LIMIT: "the pixels changed" is not observable in a console harness. What is asserted is that
    /// both caches the repaint reads through are invalidated before the flush, and that the flush is still
    /// requested — the in-game gate is the other half.
    /// </summary>
    internal static class L148_MirroredIdentityRepaintsTheOpenScreen
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var modAsm = typeof(UiEventMap).Assembly;
            var fire = typeof(UiEventMap).GetMethod("Fire", All);
            var reseed = typeof(UiEventMap).GetMethod("ReseedIdentityDisplay", All);
            var markDirty = typeof(OpenUiRepaint).GetMethods(All)
                .FirstOrDefault(m => m.Name == "MarkDirty" && m.GetParameters().Length == 2);

            if (fire == null || reseed == null || markDirty == null)
            {
                yield return "L148 premise-changed: UiEventMap.{Fire,ReseedIdentityDisplay} / " +
                             "OpenUiRepaint.MarkDirty(Type,GeoLevelController) no longer resolve. The identity " +
                             "reactivity seam has moved and this law is asserting something about a shape the " +
                             "mod no longer has — re-read it before assuming a rename still crosses.";
                yield break;
            }

            // ── (a) PREMISE, EXECUTED: the display name really is a copy ─────────────────────────
            // Asked of the real type, not of a comment: a settable Name plus a GeoCharacter constructor that
            // CALLS GetName is exactly "snapshot". If either goes away the workaround can go with it.
            var nameProp = typeof(UnitDisplayData).GetProperty("Name", All);
            var fromCharacter = typeof(UnitDisplayData).GetConstructors(All)
                .FirstOrDefault(c => c.GetParameters().Length == 2 &&
                                     c.GetParameters()[0].ParameterType == typeof(GeoCharacter));
            if (nameProp == null || nameProp.GetSetMethod(nonPublic: true) == null)
                yield return "L148 name-is-no-longer-a-writable-copy: UnitDisplayData.Name is gone or read-only. " +
                             "The whole rename half of this fix is 'the game caches the name in a settable copy " +
                             "and hand-patches it on its own rename (RenameCharacter:743), so a mirrored rename " +
                             "must patch it too'. If the copy is not writable any more, that reseed is dead code " +
                             "and something else now owns the label — find it before trusting this law green.";
            else if (fromCharacter == null ||
                     !Program.Callees(fromCharacter, typeof(GeoCharacter).Assembly).Any(c => c.Name == "GetName"))
                yield return "L148 name-is-no-longer-a-snapshot: UnitDisplayData's GeoCharacter constructor no " +
                             "longer copies the name out of the character. If the name became a LIVE read the " +
                             "reseed loop is now pure overhead and should be deleted; if the constructor merely " +
                             "moved, this arm has to follow it. Either way the premise is not what it was.";

            // ── (b) PREMISE, EXECUTED: the early-out really reads the list we clear ──────────────
            var displaySoldier = typeof(UIModuleActorCycle).GetMethods(All).FirstOrDefault(m =>
                m.Name == "DisplaySoldier" && m.GetParameters().Length == 4 &&
                m.GetParameters()[0].ParameterType == typeof(UnitDisplayData));
            var addonsField = typeof(AddonsCharacterBuilder).GetField("Addons", All);
            if (displaySoldier == null || addonsField == null)
                yield return "L148 display-funnel-changed: UIModuleActorCycle.DisplaySoldier(UnitDisplayData,…) " +
                             "or AddonsCharacterBuilder.Addons no longer resolves. That method IS the funnel every " +
                             "one of these screens repaints through and that field IS what its early-out compares, " +
                             "so the appearance half of this fix has nothing to stand on.";
            else if (!Program.ReadsField(displaySoldier, addonsField))
                yield return "L148 early-out-no-longer-reads-addons: DisplaySoldier no longer reads " +
                             "AddonsCharacterBuilder.Addons. Clearing that list is a CACHE INVALIDATION whose only " +
                             "effect is to make the method's own unchanged-addon-set early-out (:636-644) miss; if " +
                             "the method stopped consulting it, the clear buys nothing and an identity-only change " +
                             "is silently back to painting the old model.";

            // ── (c) the arm exists: CharacterIdentity is no longer served by the default branch ──
            if (!Program.Callees(fire, modAsm).Any(c => c.MetadataToken == reseed.MetadataToken))
                yield return "L148 identity-has-no-arm: UiEventMap.Fire no longer reaches ReseedIdentityDisplay. " +
                             "Without it CharacterIdentity falls back to the blanket default arm, which marks the " +
                             "screen dirty and repaints it THROUGH the two stale caches — measured in the field as " +
                             "a rename that only appears after leaving to the geoscape and coming back.";

            // ── (d) the name cache is actually re-read ───────────────────────────────────────────
            var setName = nameProp?.GetSetMethod(nonPublic: true);
            var getName = typeof(GeoCharacter).GetMethod("GetName", All);
            var gameAsm = typeof(GeoCharacter).Assembly;
            var reseedCallees = Program.Callees(reseed, gameAsm).ToList();
            if (setName != null && getName != null &&
                (!reseedCallees.Any(c => c.MetadataToken == setName.MetadataToken) ||
                 !reseedCallees.Any(c => c.MetadataToken == getName.MetadataToken)))
                yield return "L148 name-cache-not-reseeded: ReseedIdentityDisplay no longer writes " +
                             "UnitDisplayData.Name from GeoCharacter.GetName. Every name label on these screens " +
                             "reads that copy (RefreshSoldierInfo:514) and nothing else re-reads it, so a mirrored " +
                             "rename becomes invisible until the visit is torn down — the exact reported symptom.";

            // ── (e) the model cache is actually invalidated ──────────────────────────────────────
            if (addonsField != null && !Program.ReadsField(reseed, addonsField))
                yield return "L148 model-cache-not-invalidated: ReseedIdentityDisplay no longer touches " +
                             "AddonsCharacterBuilder.Addons. Then DisplaySoldier takes its unchanged-addon-set " +
                             "early-out (:636-644) for every identity-only change — a recolour, a new face, a " +
                             "haircut — and the mirrored appearance never reaches the model. A HELMET change would " +
                             "still work, because that one alters the addon set, and that asymmetry is precisely " +
                             "what made this bug read as intermittent in the field.";

            // ── (f) the label the repaint path never touches ─────────────────────────────────────
            var refreshInfo = typeof(UIModuleActorCycle).GetMethod("RefreshSoldierInfo", All);
            if (refreshInfo != null && !reseedCallees.Any(c => c.MetadataToken == refreshInfo.MetadataToken))
                yield return "L148 label-never-refreshed: ReseedIdentityDisplay no longer calls " +
                             "UIModuleActorCycle.RefreshSoldierInfo. No repaint path calls it either — " +
                             "UIStateEditSoldier.DisplaySoldier:580-585 rebuilds the equip lists and the doll and " +
                             "never the header — so the freshly-reseeded copy would sit in memory with the old " +
                             "string still on screen.";

            // ── (g) a reseed without a repaint is half a fix ─────────────────────────────────────
            if (!Program.Callees(fire, modAsm).Any(c => c.MetadataToken == markDirty.MetadataToken))
                yield return "L148 identity-arm-skips-the-repaint: the CharacterIdentity path no longer reaches " +
                             "OpenUiRepaint.MarkDirty. Invalidating the caches only decides what the NEXT repaint " +
                             "will read; something still has to ask for one, or the screen keeps its pixels until " +
                             "an unrelated delta happens along.";
        }
    }
}
