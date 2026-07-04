using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the host-authoritative research-UNLOCK state channel (#3). The mod has NO
    /// compile-time game references, so every member is resolved by name and cached. Mirrors
    /// <see cref="ItemStorageReflection"/> / <see cref="ResearchStateReflection"/>.
    ///
    /// Verified against the decompile (2026-06-17):
    ///   • player faction: <see cref="GeoRuntime.PhoenixFaction"/> → <c>GeoPhoenixFaction</c>.
    ///   • facilities: <c>GeoPhoenixFaction.AvailableFacilities : List&lt;PhoenixFacilityDef&gt;</c>
    ///     (public auto-prop, GeoPhoenixFaction.cs:224). Added by
    ///     <c>FacilityResearchReward.GiveReward</c> (FacilityResearchReward.cs:16, dedup via Contains).
    ///   • manufacture: <c>GeoFaction.Manufacture : ItemManufacturing</c> (public prop, GeoFaction.cs:153)
    ///     → <c>ItemManufacturing.ManufacturableItems : List&lt;ManufacturableItem&gt;</c>
    ///     (ItemManufacturing.cs:66); each element's <c>RelatedItemDef : ItemDef</c>. Added by
    ///     <c>ManufactureResearchReward.GiveReward</c> via
    ///     <c>ItemManufacturing.AddAvailableItem(ItemDef)</c> (ItemManufacturing.cs:254 — internally
    ///     idempotent: it no-ops if already present and requires the ManufacturableTag, so a client
    ///     reconcile is safe to call for every snapshot id).
    ///   • augmentations: <c>GeoFaction.UnlockedAugmentations : HashSet&lt;ItemDef&gt;</c> (public prop,
    ///     GeoFaction.cs:155). Added by <c>ManufactureResearchReward.GiveReward</c> (set Add → idempotent).
    ///
    /// All three are MONOTONIC unlock lists, so the client reconcile is purely additive (never removes) —
    /// the client is a pure mirror; the host is the only thing that ever unlocks.
    /// </summary>
    public static class UnlockReflection
    {
        private static bool _ready;
        private static Type _itemManufacturingType; // PhoenixPoint.Common.Entities.Items.ItemManufacturing
        private static Type _itemDefType;            // PhoenixPoint.Common.Entities.Items.ItemDef
        private static FieldInfo _availFacilitiesField; // GeoPhoenixFaction.AvailableFacilities (List<PhoenixFacilityDef>) backing field
        private static PropertyInfo _manufactureProp;   // GeoFaction.Manufacture (ItemManufacturing)
        private static PropertyInfo _manufacturableItemsProp; // ItemManufacturing.ManufacturableItems (List<ManufacturableItem>)
        private static MethodInfo _addAvailableItem;    // ItemManufacturing.AddAvailableItem(ItemDef)
        private static PropertyInfo _relatedItemDefProp; // ManufacturableItem.RelatedItemDef (ItemDef)
        private static PropertyInfo _unlockedAugProp;    // GeoFaction.UnlockedAugmentations (HashSet<ItemDef>)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var fac = rt?.PhoenixFaction();
            if (fac == null) return;
            var facType = fac.GetType();

            _itemManufacturingType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemManufacturing");
            _itemDefType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemDef");

            // AvailableFacilities is an auto-property on GeoPhoenixFaction; AccessTools.Field walks the
            // type hierarchy and finds the auto-prop backing field on the concrete faction type.
            _availFacilitiesField = AccessTools.Field(facType, "AvailableFacilities")
                                    ?? AccessTools.Field(facType, "<AvailableFacilities>k__BackingField");
            _manufactureProp = AccessTools.Property(facType, "Manufacture");
            _unlockedAugProp = AccessTools.Property(facType, "UnlockedAugmentations");

            if (_itemManufacturingType != null)
            {
                _manufacturableItemsProp = AccessTools.Property(_itemManufacturingType, "ManufacturableItems");
                if (_itemDefType != null)
                    _addAvailableItem = AccessTools.Method(_itemManufacturingType, "AddAvailableItem", new[] { _itemDefType });
            }

            _ready = _availFacilitiesField != null && _manufactureProp != null
                     && _manufacturableItemsProp != null && _addAvailableItem != null
                     && _unlockedAugProp != null;
        }

        /// <summary>
        /// Host: snapshot the three monotonic unlock sets as def guids. Null if unavailable.
        /// </summary>
        public static UnlockSnapshot Snapshot(GeoRuntime rt)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return null;
                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;

                var snap = new UnlockSnapshot();

                // Facilities: List<PhoenixFacilityDef>.
                if (_availFacilitiesField.GetValue(fac) is IEnumerable facilities)
                    foreach (var def in facilities)
                    {
                        string guid = DefReflection.GetGuid(def);
                        if (!string.IsNullOrEmpty(guid)) snap.Facilities.Add(guid);
                    }

                // Manufacture: ManufacturableItems[*].RelatedItemDef.
                var manufacture = _manufactureProp.GetValue(fac, null);
                if (manufacture != null)
                {
                    if (_relatedItemDefProp == null)
                    {
                        // Resolve ManufacturableItem.RelatedItemDef off the live element type lazily.
                        var items0 = _manufacturableItemsProp.GetValue(manufacture, null) as IEnumerable;
                        if (items0 != null)
                            foreach (var mi in items0) { if (mi != null) { _relatedItemDefProp = AccessTools.Property(mi.GetType(), "RelatedItemDef"); break; } }
                    }
                    if (_manufacturableItemsProp.GetValue(manufacture, null) is IEnumerable items && _relatedItemDefProp != null)
                        foreach (var mi in items)
                        {
                            if (mi == null) continue;
                            var def = _relatedItemDefProp.GetValue(mi, null);
                            string guid = DefReflection.GetGuid(def);
                            if (!string.IsNullOrEmpty(guid)) snap.Manufacture.Add(guid);
                        }
                }

                // Augmentations: HashSet<ItemDef>.
                if (_unlockedAugProp.GetValue(fac, null) is IEnumerable augs)
                    foreach (var def in augs)
                    {
                        string guid = DefReflection.GetGuid(def);
                        if (!string.IsNullOrEmpty(guid)) snap.Augmentations.Add(guid);
                    }

                return snap;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] UnlockReflection.Snapshot failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Client: ADD any host-unlocked def the client is missing (monotonic, idempotent). Facilities and
        /// augmentations are dedup-on-add (Contains/HashSet); manufacture goes through the game's own
        /// idempotent <c>AddAvailableItem</c>. Never removes — the client is a pure mirror of the host's
        /// growing unlock set.
        /// </summary>
        public static void Apply(GeoRuntime rt, UnlockSnapshot target)
        {
            if (target == null) return;
            try
            {
                Ensure(rt);
                if (!_ready) return;
                var fac = rt?.PhoenixFaction();
                if (fac == null) return;

                // Facilities: add missing PhoenixFacilityDef to the List.
                if (_availFacilitiesField.GetValue(fac) is IList facList)
                {
                    foreach (var guid in target.Facilities)
                    {
                        var def = DefReflection.GetDefByGuid(guid);
                        if (def == null) continue;
                        if (!facList.Contains(def)) facList.Add(def);
                    }
                }

                // Manufacture: the game's AddAvailableItem(ItemDef) is internally idempotent + tag-gated.
                var manufacture = _manufactureProp.GetValue(fac, null);
                if (manufacture != null)
                {
                    foreach (var guid in target.Manufacture)
                    {
                        var def = DefReflection.GetDefByGuid(guid);
                        if (def == null) continue;
                        try { _addAvailableItem.Invoke(manufacture, new[] { def }); }
                        catch (Exception ex) { Debug.LogError("[Multiplayer] UnlockReflection.Apply AddAvailableItem '" + guid + "' failed (skipped): " + ex.Message); }
                    }
                }

                // Augmentations: HashSet<ItemDef>.Add (idempotent).
                if (_unlockedAugProp.GetValue(fac, null) is object augSet)
                {
                    var addMethod = AccessTools.Method(augSet.GetType(), "Add", new[] { _itemDefType });
                    if (addMethod != null)
                        foreach (var guid in target.Augmentations)
                        {
                            var def = DefReflection.GetDefByGuid(guid);
                            if (def == null) continue;
                            try { addMethod.Invoke(augSet, new[] { def }); }
                            catch (Exception ex) { Debug.LogError("[Multiplayer] UnlockReflection.Apply aug Add '" + guid + "' failed (skipped): " + ex.Message); }
                        }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] UnlockReflection.Apply failed: " + ex.Message); }
        }
    }
}
