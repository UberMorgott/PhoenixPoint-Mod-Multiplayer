using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.View.ViewControllers.Inventory;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L156 — AN IN-INVENTORY REORDER REPAINTS AN ALREADY-OPEN EQUIP SCREEN. NOBODY CLOSES THE SOLDIER AND
    /// RE-OPENS HIM TO SEE HOW ANOTHER PLAYER ARRANGED THE KIT.
    ///
    /// THE REPORT (HANDOFF §5d symptom 1): two peers have the SAME soldier open, one rearranges items INSIDE
    /// the inventory, the other "sees NOTHING until he closes and re-opens the screen". Requiring a re-open is
    /// a defect under the REACTIVITY mandate, not a cosmetic complaint.
    ///
    /// WHAT THIS LAW IS AND IS NOT. The RCA (2026-08-07) walked the whole path and found the SHIP half already
    /// correct and already law-covered: a permutation is byte-unequal to the canon (L19 `order-blind`), the
    /// EntityList blob is written in list order and decodes back into it (L10 `entitylist-order`), the host's
    /// intent apply rebuilds each list in WIRE order (EquipSync.HandleIntent:641-685), and the receiving
    /// applier reorders the LIVE instances rather than husking them (GenericApplier.cs:794-798). What NOTHING
    /// asserted was the other half — that the delta, once landed, actually becomes PIXELS on a screen that is
    /// already open. That half is four separate silent-swallow seams, and this law is each of them.
    ///
    /// THE RESIDUE THAT IS NOT A BUG, WRITTEN DOWN SO IT STOPS BEING RE-OPENED: a drag into EMPTY space right
    /// of the last item changes NO list order — `UIInventoryList.ItemChangedHandler:855-859` clamps with
    /// `Insert(Math.Min(Slots.IndexOf(slot), Count), item)` — so there is genuinely nothing to replicate, and
    /// the mover loses the hole himself on re-open. `GeoItem` carries no coordinate (GeoItem.cs:12-23); cell
    /// IS list index. Exact-cell placement is CLOSED as impossible. This law asserts the tractable half: every
    /// move that DOES permute the list must repaint every open equip screen, live.
    ///
    /// ARMS. (a) order stays STATE for the field itself — the one flag whose flip would silently stop
    /// replicating reorders while every codec law kept passing. (b) the relevance EXCLUSION must never grow to
    /// eat this repaint: `IgnoredKinds` exists because UIStateEditSoldier's rebuild costs 4-5 fps, and the
    /// cheapest future "fix" for that is to add the churning GeoCharacter/ItemStorage kinds to it — which
    /// would trade the frame rate back for exactly the stale screen reported here. (c) the mark is still
    /// asked for. (d) the screen has a native reseed entry at all, and (e) that reseed can actually RUN
    /// instead of declining to the fallback — `ReseedEquipScreen` is resolve-all-first, so ONE renamed member
    /// turns the whole entry into a silent `return false`. (f)-(g) EXECUTE the repaint seam against the real
    /// game assembly: `UIStateEditSoldier.DisplaySoldier:582` pushes `character.InventoryItems` into
    /// `UIModuleSoldierEquip.UpdateData`, which `Deinit()`s and `Init()`s the list (:632-633) so the slots
    /// re-pack first-fit from the NEW model order. That chain IS how order becomes pixels; if a game update
    /// breaks it the reseed still runs, still reports success, and still paints the old arrangement.
    ///
    /// FALSIFY: flip `_inventoryItems` to unordered → (a); add `typeof(GeoCharacter)` to `WorldLayerKinds` →
    /// (b); delete the `MarkDirty` from `UiEventMap.Fire` → (c); drop the `UIStateEditSoldier` row from
    /// `Table` → (d); rename `DisplaySoldier` → (e); make `DisplaySoldier` stop feeding `UpdateData` → (f).
    ///
    /// STATED LIMIT: a console harness cannot observe pixels. What is asserted is that the delta is shippable
    /// as order, is not declared irrelevant, is marked, is reseeded by a resolvable native entry, and that the
    /// entry's own read path still reaches the widget rebuild. The in-game gate is the other half.
    /// </summary>
    internal static class L156_InventoryReorderRepaintsTheOpenEquipScreen
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var modAsm = typeof(UiEventMap).Assembly;
            var gameAsm = typeof(GeoCharacter).Assembly;
            var fire = typeof(UiEventMap).GetMethod("Fire", All);
            var markDirty = typeof(OpenUiRepaint).GetMethods(All)
                .FirstOrDefault(m => m.Name == "MarkDirty" && m.GetParameters().Length == 2);
            var ignoredFi = typeof(UiNativeRepaint).GetField("IgnoredKinds", All);
            var tableFi = typeof(UiNativeRepaint).GetField("Table", All);
            var reseed = typeof(UiNativeRepaint).GetMethod("ReseedEquipScreen", All);

            if (fire == null || markDirty == null || ignoredFi == null || tableFi == null || reseed == null)
            {
                yield return "L156 premise-changed: UiEventMap.Fire / OpenUiRepaint.MarkDirty(Type,Geo) / " +
                             "UiNativeRepaint.{IgnoredKinds,Table,ReseedEquipScreen} no longer resolve. The equip " +
                             "reactivity seam has moved and this law is asserting something about a shape the mod " +
                             "no longer has — re-read it before assuming a rearranged inventory still repaints.";
                yield break;
            }

            // ── (a) ORDER IS STATE for the field that carries the arrangement ────────────────────
            // The arrangement IS the list order and nothing else (no coordinate exists), so `Unordered` is
            // the single bit standing between a reorder and silence. A codec law cannot catch this: L10
            // proves the blob round-trips order on a synthetic type, while the classifier decides per FIELD
            // whether that order is even signed. docs/rail-baseline.txt PRINTS the flag; only this asserts it.
            var rt = RailType.Get(typeof(GeoCharacter));
            var inv = rt?.Fields?.FirstOrDefault(f => f.Name == "_inventoryItems");
            if (inv == null)
                yield return "L156 premise-changed: GeoCharacter._inventoryItems is no longer a covered rail field. " +
                             "That list IS the soldier's inventory arrangement; if it stopped riding, a rearrangement " +
                             "reaches no peer at all and the rest of this law is asserting a repaint of nothing.";
            else if (inv.Class != FieldClass.EntityList || inv.Unordered)
                yield return "L156 inventory-order-not-state: GeoCharacter._inventoryItems rides as " + inv.Class +
                             " unordered=" + (inv.Unordered ? "yes" : "no") + ". A cell IS the list index here, so an " +
                             "unordered (canonically re-sorted) or non-EntityList arrangement means a pure " +
                             "rearrangement produces no delta — every peer keeps the old kit layout forever and " +
                             "nothing anywhere reports a failure.";

            // ── (b) THE PERF EXCLUSION MUST NOT EAT THIS REPAINT ─────────────────────────────────
            // IgnoredKinds is opt-in and conservative by construction, but it is also the declared home of
            // "this screen is too expensive to rebuild". GeoCharacter and ItemStorage churn in the same
            // batches as the world kinds already listed, so they are the obvious next entries for anyone
            // chasing the 4-5 fps — and they are precisely the two kinds an equip change arrives as.
            if (ignoredFi.GetValue(null) is Dictionary<Type, HashSet<Type>> ignored &&
                ignored.TryGetValue(typeof(UIStateEditSoldier), out var kinds) && kinds != null)
            {
                foreach (var kind in new[] { typeof(GeoCharacter), typeof(ItemStorage) })
                    if (kinds.Contains(kind))
                        yield return "L156 equip-repaint-declared-irrelevant: UIStateEditSoldier declares " + kind.Name +
                                     " irrelevant in UiNativeRepaint.IgnoredKinds. That is the kind a loadout or " +
                                     "inventory rearrangement ARRIVES as, so the open screen would decline its own " +
                                     "delta and keep painting the old arrangement — the reported 'nothing happens " +
                                     "until I re-open him'. The 4-5 fps this table exists for must be bought " +
                                     "somewhere else; EnterState reaches GetTotalAvailableStorage:596 and " +
                                     "GetEquippedItemHealthMap:495-497, so the L38 audit cannot license it either.";
            }

            // ── (c) the mark is still asked for ──────────────────────────────────────────────────
            if (!Program.Callees(fire, modAsm).Any(c => c.MetadataToken == markDirty.MetadataToken))
                yield return "L156 equip-delta-never-marks: UiEventMap.Fire no longer reaches " +
                             "OpenUiRepaint.MarkDirty(Type,GeoLevelController). The GeoCharacter arm can derive " +
                             "stats all it likes — if nothing requests a repaint, the mirrored arrangement sits in " +
                             "the model behind an open screen that never re-reads it.";

            // ── (d) the screen has a native reseed at all ────────────────────────────────────────
            if (!(tableFi.GetValue(null) is System.Collections.IDictionary table) ||
                !table.Contains(typeof(UIStateEditSoldier)))
                yield return "L156 equip-screen-has-no-reseed: UiNativeRepaint.Table has no UIStateEditSoldier row. " +
                             "The screen then repaints only through the Exit+Enter fallback, which N2/L38 keep off " +
                             "the expensive screens — so a rearrangement lands in the model with no path to the " +
                             "widgets that are already on screen.";

            // ── (e) the reseed can RUN, not just exist ───────────────────────────────────────────
            // ReseedEquipScreen is resolve-all-first: any one null MethodInfo makes it `return false` and hand
            // the screen to a fallback the perf design keeps away from it. A silent decline is the failure
            // mode this repo fights, so the resolution itself is the assertion.
            foreach (var member in new[] { "EsRefreshFlag", "EsGetData", "EsDisplay", "EsRefreshStorage" })
            {
                var fi = typeof(UiNativeRepaint).GetField(member, All);
                if (fi == null || fi.GetValue(null) == null)
                    yield return "L156 equip-reseed-declines: UiNativeRepaint." + member + " did not resolve against " +
                                 "the game assembly. ReseedEquipScreen checks every member BEFORE invoking any of " +
                                 "them, so one renamed native member turns the whole UIStateEditSoldier entry into a " +
                                 "silent `return false` — the repaint is then never attempted and the screen keeps " +
                                 "the arrangement it was opened with.";
            }

            // ── (f)-(g) THE REPAINT SEAM, EXECUTED AGAINST THE REAL GAME ASSEMBLY ────────────────
            // This is the only part that says the reseed re-reads the MODEL's order rather than merely
            // refreshing something. DisplaySoldier:582 hands character.InventoryItems to UpdateData, which
            // Deinit+Init the inventory list (:632-633) so the slots re-pack first-fit from the new order.
            var displaySoldier = typeof(UIStateEditSoldier).GetMethods(All).FirstOrDefault(m =>
                m.Name == "DisplaySoldier" && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(GeoCharacter));
            var updateData = typeof(UIModuleSoldierEquip).GetMethod("UpdateData", All);
            var listInit = typeof(UIInventoryList).GetMethods(All).FirstOrDefault(m => m.Name == "Init");

            if (displaySoldier == null || updateData == null || listInit == null)
                yield return "L156 premise-changed: UIStateEditSoldier.DisplaySoldier(GeoCharacter) / " +
                             "UIModuleSoldierEquip.UpdateData / UIInventoryList.Init no longer resolve. That chain " +
                             "IS how a mirrored list order becomes visible slots; if it moved, find where before " +
                             "trusting this law green.";
            else
            {
                if (!Program.Callees(displaySoldier, gameAsm).Any(c => c.MetadataToken == updateData.MetadataToken))
                    yield return "L156 reseed-does-not-re-read-the-model: UIStateEditSoldier.DisplaySoldier no longer " +
                                 "reaches UIModuleSoldierEquip.UpdateData. UpdateData is what receives " +
                                 "character.InventoryItems; without it the reseed refreshes weights, storage and the " +
                                 "perk tree while the item widgets keep the order they were built with — a repaint " +
                                 "that runs, reports success, and shows the stale arrangement.";
                if (!Program.Callees(updateData, gameAsm).Any(c => c.MetadataToken == listInit.MetadataToken))
                    yield return "L156 model-order-never-rebuilds-the-slots: UIModuleSoldierEquip.UpdateData no longer " +
                                 "reaches UIInventoryList.Init. Init is the REBUILD (Deinit clears the slots, Init " +
                                 "re-packs them first-fit from the passed list) and it is the only step that turns " +
                                 "list order into cell positions. If UpdateData starts diffing instead, an " +
                                 "order-ONLY change looks like 'same set, nothing to do' and is dropped in silence.";
            }
        }
    }
}
