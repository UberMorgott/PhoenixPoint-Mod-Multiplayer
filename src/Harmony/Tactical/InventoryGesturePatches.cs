using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// REACTIVE tactical-inventory gesture seams (canon 2026-07-13: every item move relays IMMEDIATELY — no
    /// screen-close wait, so two players in one container see each other's moves live and the host serializes
    /// conflicts). Thin Harmony glue over <see cref="TacticalInventorySync.OnTacticalGesture"/>, which no-ops
    /// outside co-op or when the tactical <c>UIStateInventory</c> isn't the current view state — so these patches
    /// coexist with the GEOSCAPE equip rail (<c>EquipGesturePatches</c> bails in tactical, this rail bails in
    /// geoscape) even though both hook the shared <c>UIModuleSoldierEquip</c>.
    ///
    /// Seams (all postfix, diff-based — an empty UI-list diff relays nothing, so over-hooking is safe):
    ///   • <c>UIModuleSoldierEquip.AttemptSlotSwap</c> (result true) — THE universal move choke: drag-drop,
    ///     click-move, double-click quick-equip all funnel here.
    ///   • <c>UIInventorySlotSideButton.OnSideButtonPressed</c> — quick-add bypasses AttemptSlotSwap.
    ///   • <c>UIStateInventory.UndoInventoryActions</c> — the native restore makes the UI diff == the REVERSE
    ///     moves, so undo relays as a normal gesture (already-applied host moves are moved back, not unsent).
    ///     ponytail: the inventory ability's AP already spent this session is NOT refunded on undo (native would
    ///     refund at close) — accept the drift, AP reconciles via 0x8F; upgrade if players actually undo-loot.
    /// NOT covered (deliberate): reload/ammo (charges ride the 0x8F actor-state delta, membership unchanged),
    /// manufacture/scrap drop areas (geoscape-only UI), drops onto the fresh LOCAL ground container (unregistered
    /// actor — degrade-to-notify, same as the close-commit rail; the synced path is DropItemAbility).
    /// </summary>
    [HarmonyPatch]
    public static class TacInventorySlotSwapGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleSoldierEquip");
            _target = t != null ? AccessTools.Method(t, "AttemptSlotSwap") : null;
            Debug.Log("[Multiplayer] InventoryGesturePatches: UIModuleSoldierEquip.AttemptSlotSwap " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        // __result true = a completed swap (may be TWO moves when the destination item swapped back).
        public static void Postfix(bool __result) { if (__result) TacticalInventorySync.OnTacticalGesture(); }
    }

    [HarmonyPatch]
    public static class TacInventorySideButtonGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewControllers.Inventory.UIInventorySlotSideButton");
            _target = t != null ? AccessTools.Method(t, "OnSideButtonPressed") : null;
            Debug.Log("[Multiplayer] InventoryGesturePatches: UIInventorySlotSideButton.OnSideButtonPressed " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => TacticalInventorySync.OnTacticalGesture();
    }

    [HarmonyPatch]
    public static class TacInventoryUndoGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewStates.UIStateInventory");
            _target = t != null ? AccessTools.Method(t, "UndoInventoryActions") : null;
            Debug.Log("[Multiplayer] InventoryGesturePatches: UIStateInventory.UndoInventoryActions " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => TacticalInventorySync.OnTacticalGesture();
    }
}
