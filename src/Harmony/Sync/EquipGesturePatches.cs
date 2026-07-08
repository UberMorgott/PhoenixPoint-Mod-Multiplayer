using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Soldier-equip co-op sync v2 — GESTURE-AT-SOURCE adapters + client per-frame FLUSH SUPPRESSION (rebuild
    /// 2026-07-08; design: docs/superpowers/specs/2026-07-08-coop-edit-session-engine-design.md; native map:
    /// docs/research/pp-equip-screen-native-map.md). Replaces the deleted flush-diff relay
    /// (SetItemsEditRelayPatch + LoadoutRelayDedup) — the FPS-collapse layer that inferred edits by diffing the
    /// per-frame SetItems flush and stormed one intent ~60x/s.
    ///
    /// CLIENT (gate on, active session, not applying):
    ///   • FLUSH SUPPRESSED — <c>UIStateEditSoldier.UpdateSoldierEquipment</c> (:546, the per-frame SetItems
    ///     re-push) and <c>UpdateStorage</c> (:564) are no-oped, so the client model is written ONLY by #9/#1
    ///     mirror applies (client never simulates).
    ///   • GESTURE ADAPTERS — a postfix on each real user-action seam relays ONE <see cref="EquipSoldierAction"/>
    ///     built from the POST-gesture UI list truth (module ArmorList/ReadyList/InventoryList.UnfilteredItems),
    ///     never from the flush. Seams: AttemptSlotSwap (the universal doll/storage swap choke — drag-drop,
    ///     click-pick, double-click quick-equip all funnel here), side-button quick-add, reload, drop-area,
    ///     scrap (returnRemovedToStorage=false: destroyed, not returned), loadout unload/load.
    ///   • DRAG LIFECYCLE — BeginDrag/EndDrag drive <see cref="EquipMirrorRepaint"/>'s EditSession so a mirror
    ///     repaint landing mid-drag defers (never clobbers the dangling drag), capped ~2s.
    ///
    /// HOST / single-player: every patch is a pass-through (<see cref="PersonnelEditRelay.ShouldRelay"/> false).
    /// The host is fully native; #9/#1 equip dirty rides the once-per-edit SetItems seam
    /// (GeoCharacterSetItemsStateDirtyPatch), NOT these gestures and NOT any per-frame call. GATE OFF → pure
    /// vanilla. Every game type resolves via AccessTools (Prepare false → PatchAll skips silently); best-effort
    /// try/catch — never breaks the native gesture.
    /// </summary>
    internal static class EquipGestureRelay
    {
        private static bool _ensured;
        private static FieldInfo _viewField;               // GeoLevelController.View
        private static PropertyInfo _currentViewStateProp; // GeoscapeView.CurrentViewState
        private static Type _editSoldierType;              // UIStateEditSoldier
        private static FieldInfo _currentCharacterField;   // UIStateEditSoldier._currentCharacter
        private static FieldInfo _soldierEquipModuleField; // UIStateEditSoldier._soldierEquipModule
        private static FieldInfo _armorListField;          // UIModuleSoldierEquip.ArmorList (public UIInventoryList)
        private static FieldInfo _readyListField;          // UIModuleSoldierEquip.ReadyList
        private static FieldInfo _inventoryListField;      // UIModuleSoldierEquip.InventoryList
        private static PropertyInfo _unfilteredItemsProp;  // UIInventoryList.UnfilteredItems (List<ICommonItem>)

        private static void Ensure()
        {
            if (_ensured) return;
            _ensured = true;
            try
            {
                var geoT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
                var viewT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
                _editSoldierType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier");
                var moduleT = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleSoldierEquip");
                var listT = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewControllers.Inventory.UIInventoryList");
                if (geoT != null) _viewField = AccessTools.Field(geoT, "View");
                if (viewT != null) _currentViewStateProp = AccessTools.Property(viewT, "CurrentViewState");
                if (_editSoldierType != null)
                {
                    _currentCharacterField = AccessTools.Field(_editSoldierType, "_currentCharacter");
                    _soldierEquipModuleField = AccessTools.Field(_editSoldierType, "_soldierEquipModule");
                }
                if (moduleT != null)
                {
                    _armorListField = AccessTools.Field(moduleT, "ArmorList");
                    _readyListField = AccessTools.Field(moduleT, "ReadyList");
                    _inventoryListField = AccessTools.Field(moduleT, "InventoryList");
                }
                if (listT != null) _unfilteredItemsProp = AccessTools.Property(listT, "UnfilteredItems");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EquipGestureRelay.Ensure failed: " + ex.Message); }
        }

        /// <summary>The active soldier-equip state, or null (also null on the vehicle-equip screen — it is a
        /// different state type — so vehicle gestures are ignored, staying vanilla per E1 scope).</summary>
        private static object ActiveState()
        {
            Ensure();
            if (_viewField == null || _currentViewStateProp == null || _editSoldierType == null) return null;
            var geo = GeoRuntime.Instance?.GeoLevel();
            if (geo == null) return null;
            var view = _viewField.GetValue(geo);
            if (view == null) return null;
            var state = _currentViewStateProp.GetValue(view, null);
            return state != null && _editSoldierType.IsInstanceOfType(state) ? state : null;
        }

        /// <summary>The currently-edited soldier GeoCharacter, or null when the equip screen is not current.</summary>
        internal static object ActiveCharacter()
        {
            var state = ActiveState();
            return state != null ? _currentCharacterField?.GetValue(state) : null;
        }

        /// <summary>An equip gesture completed. CLIENT → relay ONE full-loadout intent from the post-gesture UI
        /// list truth (ownership/permission gated inside <see cref="PersonnelEditRelay.Relay"/>). Host/SP/apply →
        /// no-op (the native flush + SetItems dirty seam own the host result). Never throws into the native gesture.</summary>
        internal static void OnGesture(bool returnRemovedToStorage)
        {
            try
            {
                if (!EquipSyncV2Gate.Enabled) return;
                if (!PersonnelEditRelay.ShouldRelay()) return;   // client-only; host + single-player run native
                var state = ActiveState();
                if (state == null) return;
                object character = _currentCharacterField?.GetValue(state);
                object module = _soldierEquipModuleField?.GetValue(state);
                if (character == null || module == null) return;
                long unitId = PersonnelReflection.ReadUnitId(character);
                string[] armour = ReadList(module, _armorListField);
                string[] equip = ReadList(module, _readyListField);
                string[] inv = ReadList(module, _inventoryListField);
                PersonnelEditRelay.Relay(ActionCategory.Equip, unitId, true,
                    () => new EquipSoldierAction(unitId, armour, equip, inv, returnRemovedToStorage));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EquipGestureRelay.OnGesture failed: " + ex.Message); }
        }

        private static string[] ReadList(object module, FieldInfo listField)
        {
            if (listField == null || _unfilteredItemsProp == null) return Array.Empty<string>();
            object list = listField.GetValue(module);
            if (list == null) return Array.Empty<string>();
            object items = _unfilteredItemsProp.GetValue(list, null);
            return PersonnelEditReflection.ReadItemGuids(items).ToArray();
        }

        /// <summary>True → SUPPRESS the client's per-frame equip/storage flush (gate on, active co-op client, not
        /// an engine apply). The client model is then mirror-only; host + single-player keep the native flush.</summary>
        internal static bool SuppressClientFlush() => EquipSyncV2Gate.Enabled && PersonnelEditRelay.ShouldRelay();
    }

    // ─── gesture relays (one intent per completed user action) ────────────────────────────────────────────

    [HarmonyPatch]
    public static class EquipAttemptSlotSwapGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleSoldierEquip");
            _target = t != null ? AccessTools.Method(t, "AttemptSlotSwap") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIModuleSoldierEquip.AttemptSlotSwap " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        // __result true = a completed swap (the universal doll/storage move choke). One intent per drop.
        public static void Postfix(bool __result) { if (__result) EquipGestureRelay.OnGesture(true); }
    }

    [HarmonyPatch]
    public static class EquipReloadGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleSoldierEquip");
            _target = t != null ? AccessTools.Method(t, "ReloadItemHandler") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIModuleSoldierEquip.ReloadItemHandler " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => EquipGestureRelay.OnGesture(true);
    }

    [HarmonyPatch]
    public static class EquipDropAreaGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleSoldierEquip");
            _target = t != null ? AccessTools.Method(t, "AreaEndDragHandler") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIModuleSoldierEquip.AreaEndDragHandler " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => EquipGestureRelay.OnGesture(true);
    }

    [HarmonyPatch]
    public static class EquipSideButtonGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewControllers.Inventory.UIInventorySlotSideButton");
            _target = t != null ? AccessTools.Method(t, "OnSideButtonPressed") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIInventorySlotSideButton.OnSideButtonPressed " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        // Quick-add / ammo / repair from storage — bypasses AttemptSlotSwap; relay the post-press loadout.
        public static void Postfix() => EquipGestureRelay.OnGesture(true);
    }

    [HarmonyPatch]
    public static class EquipScrapGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier");
            _target = t != null ? AccessTools.Method(t, "ItemScrappedHandler") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIStateEditSoldier.ItemScrappedHandler " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        // Scrap DESTROYS the item — returnRemovedToStorage:false so the storage delta never dupes it back in.
        public static void Postfix() => EquipGestureRelay.OnGesture(false);
    }

    [HarmonyPatch]
    public static class EquipUnloadLoadoutGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier");
            // The parameterless button entry (:285) → moves all equipped to storage, then RequestRefreshCharacterData.
            _target = t != null ? AccessTools.Method(t, "UnloadLoadout", Type.EmptyTypes) : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIStateEditSoldier.UnloadLoadout() " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => EquipGestureRelay.OnGesture(true);
    }

    [HarmonyPatch]
    public static class EquipLoadLoadoutGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier");
            _target = t != null ? AccessTools.Method(t, "LoadLoadoutAndManufactureIfNeeded") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIStateEditSoldier.LoadLoadoutAndManufactureIfNeeded " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => EquipGestureRelay.OnGesture(true);
    }

    // ─── drag lifecycle → EditSession gesture-in-flight (repaint defer) ────────────────────────────────────

    [HarmonyPatch]
    public static class EquipDragBeginPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewControllers.Inventory.UIInventoryItemDragIcon");
            _target = t != null ? AccessTools.Method(t, "BeginDrag") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIInventoryItemDragIcon.BeginDrag " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => EquipMirrorRepaint.OnGestureBegin(GeoRuntime.Instance);
    }

    [HarmonyPatch]
    public static class EquipDragEndPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewControllers.Inventory.UIInventoryItemDragIcon");
            _target = t != null ? AccessTools.Method(t, "EndDrag") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIInventoryItemDragIcon.EndDrag " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => EquipMirrorRepaint.OnGestureEnd();
    }

    // ─── client per-frame flush suppression (model written only by #9/#1 mirror applies) ───────────────────

    [HarmonyPatch]
    public static class EquipClientFlushSuppressPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier");
            _target = t != null ? AccessTools.Method(t, "UpdateSoldierEquipment") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIStateEditSoldier.UpdateSoldierEquipment suppress " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        // false = skip the native SetItems re-push on a co-op client (model is mirror-only).
        public static bool Prefix() => !EquipGestureRelay.SuppressClientFlush();
    }

    [HarmonyPatch]
    public static class EquipClientStorageFlushSuppressPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier");
            _target = t != null ? AccessTools.Method(t, "UpdateStorage") : null;
            Debug.Log("[Multiplayer] EquipGesturePatches: UIStateEditSoldier.UpdateStorage suppress " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static bool Prefix() => !EquipGestureRelay.SuppressClientFlush();
    }
}
