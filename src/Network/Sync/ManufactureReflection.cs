using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Reflection bridge to <c>PhoenixPoint.Common.Entities.Items.ItemManufacturing</c>.
    ///
    /// Verified against the decompile (2026-06-15):
    ///   • start:    <c>ItemManufacturing.ManufactureItem(ManufacturableItem item)</c> (:169, public).
    ///   • complete: <c>ItemManufacturing.FinishManufactureItem(ManufactureQueueItem element)</c>
    ///               (:479, PRIVATE — AccessTools.Method finds it; patch via TargetMethod).
    ///   • def-resolver: <c>ItemManufacturing.GetManufacturableItemByDef(ItemDef def)</c> (:506).
    ///   • item def: <c>ManufacturableItem.RelatedItemDef</c> (public readonly ItemDef, :22).
    ///   • queue:    <c>ItemManufacturing.Queue</c> (List&lt;ManufactureQueueItem&gt;, :64);
    ///               <c>ManufactureQueueItem.ManufacturableItem</c> (public field, :40).
    ///   • the ItemManufacturing instance is reached via <c>GeoFaction.Manufacture</c> (GeoFaction.cs:153).
    ///   • id = <c>BaseDef.Guid</c> (string), resolved via <c>DefRepository.GetDef(guid)</c>.
    /// </summary>
    public static class ManufactureReflection
    {
        private static bool _ready;
        private static Type _manufacturingType;        // ItemManufacturing
        private static Type _manufacturableItemType;   // ManufacturableItem
        private static Type _queueItemType;            // ItemManufacturing+ManufactureQueueItem
        private static Type _itemDefType;              // ItemDef
        private static MethodInfo _manufactureItem;    // ManufactureItem(ManufacturableItem)
        private static MethodInfo _finishItem;         // FinishManufactureItem(ManufactureQueueItem)
        private static MethodInfo _getByDef;           // GetManufacturableItemByDef(ItemDef)
        private static PropertyInfo _queueProp;        // ItemManufacturing.Queue
        private static FieldInfo _relatedItemDefField; // ManufacturableItem.RelatedItemDef
        private static FieldInfo _queueItemMItemField; // ManufactureQueueItem.ManufacturableItem
        private static PropertyInfo _factionManufactureProp;

        private static void Ensure()
        {
            if (_ready) return;
            _manufacturingType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemManufacturing");
            _manufacturableItemType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ManufacturableItem");
            _itemDefType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemDef");
            if (_manufacturingType == null || _manufacturableItemType == null) return;
            _queueItemType = AccessTools.Inner(_manufacturingType, "ManufactureQueueItem");
            // RelatedItemDef is the canonical ItemDef type; use its field type as a robust fallback.
            if (_itemDefType == null)
                _itemDefType = _relatedItemDefFieldType();

            _manufactureItem = AccessTools.Method(_manufacturingType, "ManufactureItem", new[] { _manufacturableItemType });
            if (_queueItemType != null)
                _finishItem = AccessTools.Method(_manufacturingType, "FinishManufactureItem", new[] { _queueItemType });
            if (_itemDefType != null)
                _getByDef = AccessTools.Method(_manufacturingType, "GetManufacturableItemByDef", new[] { _itemDefType });
            _queueProp = AccessTools.Property(_manufacturingType, "Queue");
            _relatedItemDefField = AccessTools.Field(_manufacturableItemType, "RelatedItemDef");
            if (_queueItemType != null)
                _queueItemMItemField = AccessTools.Field(_queueItemType, "ManufacturableItem");

            _ready = _manufactureItem != null && _finishItem != null && _getByDef != null
                     && _relatedItemDefField != null && _queueItemMItemField != null;
        }

        private static Type _relatedItemDefFieldType()
        {
            var f = AccessTools.Field(_manufacturableItemType, "RelatedItemDef");
            return f?.FieldType;
        }

        private static object GetFactionManufacture(GeoRuntime rt)
        {
            var fac = rt?.PhoenixFaction();
            if (fac == null) return null;
            try
            {
                if (_factionManufactureProp == null || _factionManufactureProp.DeclaringType == null
                    || !_factionManufactureProp.DeclaringType.IsInstanceOfType(fac))
                    _factionManufactureProp = AccessTools.Property(fac.GetType(), "Manufacture");
                return _factionManufactureProp?.GetValue(fac, null);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ManufactureReflection.GetFactionManufacture failed: " + ex.Message); return null; }
        }

        /// <summary>Read the item def GUID off a <c>ManufacturableItem</c> (interceptor side).</summary>
        public static string GetItemId(object manufacturableItem)
        {
            if (manufacturableItem == null) return null;
            try
            {
                Ensure();
                var def = _relatedItemDefField?.GetValue(manufacturableItem);
                return DefReflection.GetGuid(def);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ManufactureReflection.GetItemId failed: " + ex.Message); return null; }
        }

        /// <summary>Read the item def GUID off a <c>ManufactureQueueItem</c> (completion interceptor side).</summary>
        public static string GetQueueItemId(object queueItem)
        {
            if (queueItem == null) return null;
            try
            {
                Ensure();
                var mItem = _queueItemMItemField?.GetValue(queueItem);
                return GetItemId(mItem);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ManufactureReflection.GetQueueItemId failed: " + ex.Message); return null; }
        }

        /// <summary>Index of a queue item within the live <c>Queue</c> (deterministic completion fallback key).</summary>
        public static int GetQueueIndex(GeoRuntime rt, object queueItem)
        {
            try
            {
                Ensure();
                var manufacture = GetFactionManufacture(rt);
                var queue = _queueProp?.GetValue(manufacture, null) as IList;
                if (queue == null) return -1;
                return queue.IndexOf(queueItem);
            }
            catch { return -1; }
        }

        private static object ResolveManufacturable(object manufacture, string itemDefId)
        {
            if (manufacture == null || string.IsNullOrEmpty(itemDefId)) return null;
            var def = DefReflection.GetDefByGuid(itemDefId);
            if (def == null) return null;
            return _getByDef.Invoke(manufacture, new[] { def });
        }

        /// <summary>Resolve <paramref name="itemDefId"/> → ManufacturableItem and queue it (Apply side).</summary>
        public static void Queue(GeoRuntime rt, string itemDefId)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var manufacture = GetFactionManufacture(rt);
                var item = ResolveManufacturable(manufacture, itemDefId);
                if (item == null) return;
                _manufactureItem.Invoke(manufacture, new[] { item });
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ManufactureReflection.Queue failed: " + ex.Message); }
        }

        /// <summary>
        /// Complete a queued item (Apply side). Primary key = matching item def GUID; if multiple
        /// queued items share the def, prefer <paramref name="queueIndex"/> when valid.
        /// </summary>
        public static void Complete(GeoRuntime rt, string itemDefId, int queueIndex)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var manufacture = GetFactionManufacture(rt);
                var queue = _queueProp?.GetValue(manufacture, null) as IList;
                if (queue == null || queue.Count == 0) return;

                object target = null;
                // Prefer the explicit index if it points at a queue item whose def matches.
                if (queueIndex >= 0 && queueIndex < queue.Count)
                {
                    var atIndex = queue[queueIndex];
                    if (GetQueueItemId(atIndex) == itemDefId) target = atIndex;
                }
                // Fallback: first queued item with the matching def GUID.
                if (target == null)
                {
                    foreach (var qi in queue)
                        if (GetQueueItemId(qi) == itemDefId) { target = qi; break; }
                }
                if (target == null) return;
                _finishItem.Invoke(manufacture, new[] { target });
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ManufactureReflection.Complete failed: " + ex.Message); }
        }
    }
}
