using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge to <c>PhoenixPoint.Geoscape.Entities.ItemStorage</c> (the Phoenix faction's
    /// global item storage). The mod has NO compile-time game references, so every member is resolved
    /// by name and cached.
    ///
    /// Verified against the decompile (2026-06-16):
    ///   • storage owner: <c>GeoFaction.ItemStorage</c> is a public FIELD (GeoFaction.cs:75), type
    ///     <c>ItemStorage</c>. (Design said "GeoFaction.ItemStorage (GeoFaction.cs:75)"; confirmed it
    ///     is a field, not a property — bound via AccessTools.Field.)
    ///   • model: <c>ItemStorage.Items : IReadOnlyDictionary&lt;ItemDef,GeoItem&gt;</c> (ItemStorage.cs:22).
    ///   • count: <c>GeoItem.CommonItemData.Count</c> (CommonItemData.cs:39).
    ///   • charges: <c>CommonItemData.CurrentCharges</c> (CommonItemData.cs:33) — the stack's TOP-unit
    ///     charges (all other units are full: <c>TotalAvailableCharges</c>, CommonItemData.cs:96-99).
    ///   • def: <c>GeoItem.ItemDef</c> (GeoItem.cs:21); guid via <see cref="DefReflection.GetGuid"/>.
    ///   • change event: <c>ItemStorage.StorageChanged</c> — a plain <c>System.Action</c> FIELD
    ///     (ItemStorage.cs:17), so a direct delegate subscription works (no DynamicMethod needed).
    ///   • reconcile (client apply): mirror the game's own load path
    ///     <c>GeoFaction.LevelStartLoadedGame</c> (GeoFaction.cs:600-601): <c>Clear()</c> then add a
    ///     fresh <c>new GeoItem(def, count, charges)</c> per def. <c>AddItem</c> clones the GeoItem and
    ///     preserves its preset <c>CommonItemData</c> when the def is absent (ItemStorage.cs:58-65). The
    ///     faction-storage ammo auto-unload (ItemStorage.cs:48-57) is harmless: a fresh GeoItem made
    ///     via the count ctor has an empty AmmoManager (no loaded magazines) → nothing to unload.
    /// </summary>
    public static class ItemStorageReflection
    {
        private static bool _ready;
        private static Type _itemStorageType;   // PhoenixPoint.Geoscape.Entities.ItemStorage
        private static Type _geoItemType;        // PhoenixPoint.Geoscape.Entities.GeoItem
        private static Type _itemDefType;        // PhoenixPoint.Common.Entities.Items.ItemDef
        private static FieldInfo _factionStorageField; // GeoFaction.ItemStorage (field)
        private static PropertyInfo _itemsProp;        // ItemStorage.Items
        private static FieldInfo _storageChangedField; // ItemStorage.StorageChanged (Action field)
        private static MethodInfo _clearMethod;        // ItemStorage.Clear()
        private static MethodInfo _addItemMethod;      // ItemStorage.AddItem(GeoItem)
        private static PropertyInfo _geoItemDefProp;   // GeoItem.ItemDef
        private static PropertyInfo _geoItemCommonProp;// GeoItem.CommonItemData
        private static PropertyInfo _commonCountProp;  // CommonItemData.Count
        private static PropertyInfo _commonChargesProp;// CommonItemData.CurrentCharges
        private static ConstructorInfo _geoItemCtor;   // GeoItem(ItemDef, int count, int charges, AmmoManager, int)

        private static void Ensure()
        {
            if (_ready) return;
            _itemStorageType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.ItemStorage");
            _geoItemType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoItem");
            _itemDefType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemDef");
            if (_itemStorageType == null || _geoItemType == null || _itemDefType == null) return;

            _itemsProp = AccessTools.Property(_itemStorageType, "Items");
            _storageChangedField = AccessTools.Field(_itemStorageType, "StorageChanged");
            _clearMethod = AccessTools.Method(_itemStorageType, "Clear");
            _addItemMethod = AccessTools.Method(_itemStorageType, "AddItem", new[] { _geoItemType });
            _geoItemDefProp = AccessTools.Property(_geoItemType, "ItemDef");
            _geoItemCommonProp = AccessTools.Property(_geoItemType, "CommonItemData");
            if (_geoItemCommonProp != null)
            {
                _commonCountProp = AccessTools.Property(_geoItemCommonProp.PropertyType, "Count");
                _commonChargesProp = AccessTools.Property(_geoItemCommonProp.PropertyType, "CurrentCharges");
            }
            // GeoItem(ItemDef def, int count = 1, int charges = -1, AmmoManager ammo = null, int malfunctionPercent = -100)
            _geoItemCtor = AccessTools.GetDeclaredConstructors(_geoItemType)?.Find(c =>
            {
                var ps = c.GetParameters();
                return ps.Length >= 2 && ps[0].ParameterType == _itemDefType && ps[1].ParameterType == typeof(int);
            });

            _ready = _itemsProp != null && _clearMethod != null && _addItemMethod != null
                     && _geoItemDefProp != null && _commonCountProp != null && _commonChargesProp != null
                     && _geoItemCtor != null;
        }

        /// <summary>The Phoenix faction's global <c>ItemStorage</c> (field on <c>GeoFaction</c>), or null.</summary>
        public static object GetStorage(GeoRuntime rt)
        {
            var fac = rt?.PhoenixFaction();
            if (fac == null) return null;
            try
            {
                if (_factionStorageField == null || _factionStorageField.DeclaringType == null
                    || !_factionStorageField.DeclaringType.IsInstanceOfType(fac))
                    _factionStorageField = AccessTools.Field(fac.GetType(), "ItemStorage");
                return _factionStorageField?.GetValue(fac);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ItemStorageReflection.GetStorage failed: " + ex.Message); return null; }
        }

        /// <summary>Host: enumerate the storage as (def guid, count, charges) entries, one per def stack.
        /// Charges = the stack's <c>CurrentCharges</c> (top unit; see class doc). Null if unavailable.</summary>
        public static List<(string guid, int count, int charges)> Snapshot(GeoRuntime rt)
        {
            try
            {
                Ensure();
                if (!_ready) return null;
                var storage = GetStorage(rt);
                if (storage == null) return null;
                var dict = _itemsProp.GetValue(storage, null) as IEnumerable;
                if (dict == null) return null;

                var list = new List<(string, int, int)>();
                foreach (var entry in dict) // KeyValuePair<ItemDef, GeoItem>
                {
                    var geoItem = GetKvpValue(entry);
                    if (geoItem == null) continue;
                    var def = _geoItemDefProp.GetValue(geoItem, null);
                    string guid = DefReflection.GetGuid(def);
                    if (string.IsNullOrEmpty(guid)) continue;
                    int count = GetCount(geoItem);
                    if (count > 0) list.Add((guid, count, GetCharges(geoItem)));
                }
                return list;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ItemStorageReflection.Snapshot failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Client: reconcile the live storage to EXACTLY match <paramref name="target"/>. Mirrors the
        /// game's own load reconcile (<c>Clear()</c> + fresh <c>AddItem(new GeoItem(def, count, charges))</c>
        /// per def). Defs present on the client but absent from the snapshot are dropped by the Clear.
        /// </summary>
        public static void Apply(GeoRuntime rt, List<(string guid, int count, int charges)> target)
        {
            if (target == null) return;
            try
            {
                Ensure();
                if (!_ready) return;
                var storage = GetStorage(rt);
                if (storage == null) return;

                // NOTE — reconcile: Clear()+rebuild with a fresh `new GeoItem(def, count, charges)` per
                // def, exactly mirroring the game's own load path (GeoFaction.LevelStartLoadedGame,
                // GeoFaction.cs:600-601). The snapshot carries per-stack CHARGES because the native
                // post-mission replenish leaves PARTIAL stacks in faction storage
                // (GeoMission.TryReloadItem → ModifyCharges(-n), GeoMission.cs:1095-1104) — a count-only
                // rebuild silently refilled them to ChargesMax (ctor charges=-1 → full) and drifted from
                // the host. The ctor stores the exact value: charges>=0 → _charges = charges
                // (CommonItemData.cs:51-63), so the mirrored stack is bit-identical (GeoItem.Equals
                // compares def+count+CurrentCharges). Remaining per-GeoItem state (AmmoManager / loaded
                // magazines, malfunction%) still never lives in faction storage: AddItem auto-unloads
                // magazines into their own def stacks (ItemStorage.cs:52-63), and a count-ctor GeoItem
                // has an empty AmmoManager so that unload is a no-op here. Stateful per-instance items
                // (a loaded weapon, a partially-used medkit) live on soldiers / in equipment and are
                // reconciled by their own channels — they never round-trip through here. Do NOT switch
                // to a diff-against-existing reconcile: there is no per-instance state to preserve.
                _clearMethod.Invoke(storage, null);
                foreach (var (guid, count, charges) in target)
                {
                    if (count <= 0) continue;
                    var def = DefReflection.GetDefByGuid(guid);
                    if (def == null) continue;
                    var geoItem = NewGeoItem(def, count, charges);
                    if (geoItem == null) continue;
                    _addItemMethod.Invoke(storage, new[] { geoItem });
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ItemStorageReflection.Apply failed: " + ex.Message); }
        }

        /// <summary>Subscribe a callback to <c>ItemStorage.StorageChanged</c> (plain Action field).
        /// Returns the bound delegate (pass back to <see cref="Unsubscribe"/>), or null.</summary>
        public static Delegate SubscribeStorageChanged(object storage, Action onChanged)
        {
            if (storage == null || onChanged == null) return null;
            try
            {
                Ensure();
                if (_storageChangedField == null) return null;
                var existing = _storageChangedField.GetValue(storage) as Delegate;
                var combined = Delegate.Combine(existing, onChanged);
                _storageChangedField.SetValue(storage, combined);
                return onChanged;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ItemStorageReflection.SubscribeStorageChanged failed: " + ex.Message); return null; }
        }

        public static void Unsubscribe(object storage, Delegate handler)
        {
            if (storage == null || handler == null || _storageChangedField == null) return;
            try
            {
                var existing = _storageChangedField.GetValue(storage) as Delegate;
                var reduced = Delegate.Remove(existing, handler);
                _storageChangedField.SetValue(storage, reduced);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ItemStorageReflection.Unsubscribe failed: " + ex.Message); }
        }

        // ─── helpers ──────────────────────────────────────────────────────

        private static object NewGeoItem(object def, int count, int charges)
        {
            // GeoItem(ItemDef def, int count, int charges = -1, AmmoManager ammo = null, int malf = -100)
            var ps = _geoItemCtor.GetParameters();
            var args = new object[ps.Length];
            args[0] = def;
            args[1] = count;
            for (int i = 2; i < ps.Length; i++) args[i] = ps[i].DefaultValue;
            // Per-stack charges ride param #2; a negative value keeps the ctor's "full charges"
            // default (charges<0 → _charges = ChargesMax, CommonItemData.cs:56-63).
            if (ps.Length >= 3 && ps[2].ParameterType == typeof(int)) args[2] = charges;
            return _geoItemCtor.Invoke(args);
        }

        private static int GetCount(object geoItem)
        {
            var common = _geoItemCommonProp.GetValue(geoItem, null);
            if (common == null) return 0;
            return (int)_commonCountProp.GetValue(common, null);
        }

        // Stack top-unit charges; -1 (= ctor "full" default) when unreadable so a broken read can
        // never zero a client's stack.
        private static int GetCharges(object geoItem)
        {
            var common = _geoItemCommonProp.GetValue(geoItem, null);
            if (common == null) return -1;
            return (int)_commonChargesProp.GetValue(common, null);
        }

        // KeyValuePair<ItemDef,GeoItem>.Value via reflection (struct, boxed during foreach).
        private static PropertyInfo _kvpValueProp;
        private static object GetKvpValue(object kvp)
        {
            if (kvp == null) return null;
            if (_kvpValueProp == null || _kvpValueProp.DeclaringType != kvp.GetType())
                _kvpValueProp = kvp.GetType().GetProperty("Value");
            return _kvpValueProp?.GetValue(kvp, null);
        }
    }
}
