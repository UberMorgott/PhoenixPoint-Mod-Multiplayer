using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Tactical;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L116 — A REPAINT REBUILDS EVERY PANEL THE OPEN CONTAINER VIEW SHOWS, NOT THE ONE THE CHANGE NAMED.
    ///
    /// THE REPORT (2026-08-05). Two soldiers standing next to each other, both inventories on screen at
    /// once, an item moved between them: it appears on ONE side and the other side needs a full close and
    /// reopen. The user also reported the personnel bar FLICKERING, which was the decisive clue — a repaint
    /// really was firing, so this was never "the change did not cross".
    ///
    /// WHAT WAS ACTUALLY RUNNING. <c>TacticalInventorySync.ApplyLayout</c> moves the items in the model and
    /// calls <c>TacticalUiRepaint.MarkDirty</c>, correctly. The flush then takes its ELSE branch, because
    /// <c>UIStateInventory</c> is deliberately NOT in <c>AbilityBarStates</c> — and that exclusion is RIGHT:
    /// its <c>ExitState</c>:428-453 COMMITS the staged batch (<c>ApplyInventoryActions</c>:443) and destroys
    /// containers, so an Exit+Enter repaint would confirm a drag the player never confirmed. But the else
    /// branch was <c>RepaintModules</c> alone, which refreshes <c>UIModuleAbilities.SetAbilities</c> and
    /// <c>UIModuleActionBar.SetActionBar</c> — the two BOTTOM-BAR modules. No item slot on either panel was
    /// ever rebuilt. The flicker was <c>PaintSquadBar</c>, a third path that touches no view state at all.
    ///
    /// SO THE GAP IS THE FAMILY, NOT THE SIBLING. <c>UIStateInventory</c> is the only screen in the assembly
    /// that shows TWO actors at once (<c>_secondaryActor</c>:249, set in <c>EnterState</c>:289 from
    /// <c>GetTacticalActors</c>:679-693, which filters by <c>TacUtil.CanTradeWith</c> — the adjacency the
    /// user described). A repaint that rebuilt "the actor the delta named" would still leave the other panel
    /// stale, which is why the fix rebuilds every panel the view is showing and the law asserts exactly that.
    /// The rebuild pair is the game's own: <c>UndoInventoryActions</c>:905-918 already redraws both sides
    /// without transitioning the state (<c>UpdateData</c>:626 + <c>UpdateSecondaryData</c>:767 +
    /// <c>RefreshUI</c>:802).
    ///
    /// ARM (e) IS THE ONE THAT EARNS ITS KEEP, and it guards this repo's dominant bug class rather than the
    /// reported symptom. The rebuild reads the game's private getters by reflection and hands the results
    /// to <c>UpdateData</c> as <c>IEnumerable&lt;ICommonItem&gt;</c>. If any handle failed to resolve, or if
    /// <c>TacticalItem</c> ever stopped implementing <c>ICommonItem</c>, every <c>as</c> would yield NULL and
    /// the repaint would hand the module four empty lists — i.e. it would CLEAR both panels instead of
    /// rebuilding them, silently, and look exactly like the bug it replaced. So the handles and the
    /// assignability are asserted, not assumed.
    ///
    /// Falsify: drop the <c>UpdateSecondaryData</c> call → <c>sibling-panel-unrebuilt</c>; drop the
    /// <c>UpdateData</c> call → <c>primary-panel-unrebuilt</c>; stop calling <c>RepaintContainerView</c> from
    /// the flush → <c>repaint-unreached</c>; add <c>UIStateInventory</c> to <c>AbilityBarStates</c> →
    /// <c>commit-by-repaint</c>; rename any private game member the rebuild reflects on → <c>handle-unresolved</c>;
    /// make <c>RepaintContainerView</c> return void, or delete the <c>IsBeingDragged</c> test →
    /// <c>drag-not-deferred</c>; let the game collapse the two-actor screen to one panel →
    /// <c>premise-two-panels</c>.
    /// </summary>
    internal static class L116_EveryOpenContainerPanel
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var repaint = typeof(TacticalUiRepaint);
            var mod = repaint.Assembly;

            var container = repaint.GetMethod("RepaintContainerView", All);
            var flush = repaint.GetNestedType("ViewStateUpdatePatch", All)?.GetMethod("Postfix", All);
            var barStates = repaint.GetField("AbilityBarStates", All)?.GetValue(null) as HashSet<string>;

            var invState = AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewStates.UIStateInventory");
            var module = typeof(UIModuleSoldierEquip);
            var updateData = module.GetMethod("UpdateData", All);
            var updateSecondary = module.GetMethod("UpdateSecondaryData", All);
            var dragIcon = module.GetField("ItemDragIcon", All);

            if (container == null || flush == null || barStates == null || invState == null ||
                updateData == null || updateSecondary == null || dragIcon == null)
            {
                yield return "L116 premise-changed: TacticalUiRepaint.RepaintContainerView / " +
                             "ViewStateUpdatePatch.Postfix / AbilityBarStates, or UIStateInventory, or " +
                             "UIModuleSoldierEquip.{UpdateData,UpdateSecondaryData,ItemDragIcon}, no longer " +
                             "resolves. The tactical container screen has moved and this law is asserting " +
                             "something about a shape it no longer has — re-read it before trusting the repaint.";
                yield break;
            }

            // ── (a) premise: the screen really does show two actors ────────
            var secondary = invState.GetField("_secondaryActor", All);
            if (secondary == null)
                yield return "L116 premise-two-panels: UIStateInventory no longer has _secondaryActor, so the " +
                             "screen that showed two adjacent soldiers at once is gone. This law asserts a " +
                             "both-panel rebuild for a second panel that no longer exists — re-read the screen " +
                             "rather than keeping a rebuild alive for it.";

            // ── (b) THE OUTCOME: both panels, every time ───────────────────
            var calls = Program.Callees(container, game).ToList();
            if (!calls.Any(c => c.MetadataToken == updateSecondary.MetadataToken && c.Module == updateSecondary.Module))
                yield return "L116 sibling-panel-unrebuilt: RepaintContainerView never calls " +
                             "UIModuleSoldierEquip.UpdateSecondaryData:767, so the SECOND soldier's panel is not " +
                             "rebuilt. That is the reported bug exactly — the item lands in the model, the primary " +
                             "side redraws, and the other side keeps the pre-move kit until the whole screen is " +
                             "closed and reopened.";
            if (!calls.Any(c => c.MetadataToken == updateData.MetadataToken && c.Module == updateData.Module))
                yield return "L116 primary-panel-unrebuilt: RepaintContainerView never calls " +
                             "UIModuleSoldierEquip.UpdateData:626, so the PRIMARY panel is not rebuilt either — " +
                             "the same staleness with the panels swapped. Both halves, or the screen is not " +
                             "repainted at all.";

            // ── (c) it is REACHED, and RepaintModules is not the whole answer ──
            if (!Program.Callees(flush, mod).Any(c => c.MetadataToken == container.MetadataToken && c.Module == container.Module))
                yield return "L116 repaint-unreached: the tactical flush (ViewStateUpdatePatch.Postfix) does not " +
                             "call RepaintContainerView, so the both-panel rebuild is correct and never runs. That " +
                             "is the state this bug was found in: the else branch was RepaintModules alone, which " +
                             "refreshes the two bottom-bar modules and no item slot on any panel.";

            // ── (d) the commit hazard stays closed ─────────────────────────
            if (barStates.Contains("UIStateInventory"))
                yield return "L116 commit-by-repaint: UIStateInventory is in AbilityBarStates, so a repaint would " +
                             "Exit+Enter it — and UIStateInventory.ExitState:428-453 is not a teardown, it COMMITS " +
                             "the staged batch (ApplyInventoryActions:443) and calls ActorSpawner.DestroyActor:451. " +
                             "A repaint would confirm a drag the player never confirmed. Rebuild in place instead.";

            // ── (e) the handles resolve, and the lists cannot silently be null ──
            foreach (var name in new[] { "_secondaryActor", "_isVehicleInventory", "_isSecondaryVehicleInventory" })
                if (invState.GetField(name, All) == null)
                    yield return "L116 handle-unresolved: UIStateInventory." + name + " does not resolve, so the " +
                                 "rebuild reads null and declines (or paints the wrong list kind) on every batch — " +
                                 "silently, which is this repo's dominant bug shape, not a crash anyone would notice.";
            if (invState.GetProperty("PrimaryActor", All) == null)
                yield return "L116 handle-unresolved: UIStateInventory.PrimaryActor does not resolve — the rebuild " +
                             "cannot name the soldier whose panel it is redrawing and declines on every batch.";
            foreach (var name in new[] { "GetSoldierBackpackItems", "GetSoldierReadyItems", "GetVehicleReadyItems",
                                         "GetGroundItems", "GetItemContainers", "RefreshUI" })
            {
                var m = invState.GetMethod(name, All);
                if (m == null)
                {
                    yield return "L116 handle-unresolved: UIStateInventory." + name + " does not resolve, so the " +
                                 "rebuild has no live item source and would hand the module empty lists — CLEARING " +
                                 "the panel it was asked to refresh.";
                    continue;
                }
                // The cast the rebuild performs is `as IEnumerable<ICommonItem>`. A getter whose return type is
                // not assignable to it yields NULL at runtime, and UpdateData(null,…) empties the list — the
                // repaint would then look identical to the staleness it replaced.
                if (name == "GetItemContainers" || name == "RefreshUI") continue;
                if (!typeof(IEnumerable<ICommonItem>).IsAssignableFrom(m.ReturnType))
                    yield return "L116 handle-unresolved: UIStateInventory." + name + " returns " +
                                 m.ReturnType.Name + ", which is NOT an IEnumerable<ICommonItem>. The rebuild's " +
                                 "`as` cast yields null and the panel is CLEARED rather than refreshed — a silent " +
                                 "swallow that reads to the player as the item vanishing.";
            }
            if (!typeof(ICommonItem).IsAssignableFrom(typeof(TacticalItem)))
                yield return "L116 handle-unresolved: TacticalItem no longer implements ICommonItem, so every item " +
                             "list the rebuild casts becomes null at once and both panels would be emptied by the " +
                             "very repaint meant to fill them.";

            // ── (f) a held drag is deferred, never dropped ─────────────────
            if (container.ReturnType != typeof(bool))
                yield return "L116 drag-not-deferred: RepaintContainerView does not return bool, so it cannot tell " +
                             "the flush it declined. A repaint skipped for a live drag would then be skipped " +
                             "FOREVER rather than retried — the difference between 'later' and 'never'.";
            if (!Program.Callees(container, game).Any(c => c.Name == "IsBeingDragged"))
                yield return "L116 drag-not-deferred: RepaintContainerView never asks ItemDragIcon.IsBeingDragged, " +
                             "so it rebuilds the lists under a held item and yanks it out of the player's hand — " +
                             "the same hazard OpenUiRepaint.LocalInputInFlight exists to avoid on the geoscape.";
        }
    }
}
