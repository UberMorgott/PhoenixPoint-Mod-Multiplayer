using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Inventory-channel (#1) storage dirty seams (wave-2 R2, 2026-07-08 RCA). The channel's only dirty
    /// source was the native <c>ItemStorage.StorageChanged</c> event (InventoryChannel.AttachHost), but the
    /// native mutators fire it ASYMMETRICALLY (decompile ItemStorage.cs): <c>AddItem</c> always (:72);
    /// <c>RemoveItem</c> ONLY when a def's stack hits zero (:87-90) — a partial-stack decrement (equipping
    /// one of N rifles) never flushed, so a host equip-from-storage updated the client's soldier doll (#9)
    /// but not its storage list (the observed asymmetry). <c>PopItem</c> has the same gap (:102-106 partial
    /// pop returns without the event; only the last-item pop fires :109).
    ///
    /// Fix: HOST-only Postfixes on the three mutators mark #1 dirty UNCONDITIONALLY on any actual change
    /// (the PersonnelStatePatches seam doctrine: IsHost-gated, NO SyncApplyScope.IsApplying skip — a host
    /// mutation inside a relayed apply must mirror). Marks COALESCE: MarkChannelDirty is a HashSet add
    /// (SyncEngine.cs:619 `_channelDirty.Add`) drained ONCE per Tick (SyncEngine.cs:2123-2132 — one
    /// FlushChannel per dirty channel, then Clear), so a bulk host op (mission-return auto-storage, N
    /// AddItems) costs N set-adds → ONE #1 snapshot/broadcast; temp-storage churn (UIStateEditSoldier.
    /// UpdateStorage scratch ItemStorages, :564-577) rides that same single flush. Reflective targets
    /// (Prepare false → PatchAll skips silently); best-effort — never breaks the native mutator.
    /// </summary>
    internal static class StorageDirty
    {
        internal static MethodBase Resolve(string methodName, Type[] args)
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.ItemStorage");
            var m = t != null ? AccessTools.Method(t, methodName, args) : null;
            Debug.Log("[Multiplayer] StorageDirtyPatches: ItemStorage." + methodName + " "
                      + (m != null ? "bound" : "NOT FOUND — storage dirty seam disabled"));
            return m;
        }

        internal static Type GeoItemType() => AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoItem");

        internal static void Mark()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                engine.Sync?.MarkChannelDirty(SurfaceIds.InventoryChannel);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] StorageDirtyPatches.Mark failed: " + ex.Message); }
        }
    }

    [HarmonyPatch]
    public static class ItemStorageAddItemDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var geoItem = StorageDirty.GeoItemType();
            _target = geoItem != null ? StorageDirty.Resolve("AddItem", new[] { geoItem }) : null;
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        // AddItem always mutates (stack merge or new entry, :64-71) → always mark.
        public static void Postfix() => StorageDirty.Mark();
    }

    [HarmonyPatch]
    public static class ItemStorageRemoveItemDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var geoItem = StorageDirty.GeoItemType();
            _target = geoItem != null ? StorageDirty.Resolve("RemoveItem", new[] { geoItem }) : null;
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        // __result false = nothing removed (permanent augment / def not stored, :78-84) → no change, no mark.
        public static void Postfix(bool __result) { if (__result) StorageDirty.Mark(); }
    }

    [HarmonyPatch]
    public static class ItemStoragePopItemDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var itemDef = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemDef");
            _target = itemDef != null ? StorageDirty.Resolve("PopItem", new[] { itemDef }) : null;
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        // null = def not stored (:97-100) → no change; a partial pop (:102-106) mutates WITHOUT the event.
        public static void Postfix(object __result) { if (__result != null) StorageDirty.Mark(); }
    }
}
