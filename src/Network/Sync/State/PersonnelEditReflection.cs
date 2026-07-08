using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Validation;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PS4 host-apply reflection bridge: runs the AUTHORITATIVE native personnel mutation for a relayed
    /// client-edit intent (equip / augment / hire / transfer / dismiss / rename). The mod has NO compile-time
    /// game references, so every member resolves by name and is cached (mirrors <see cref="PersonnelReflection"/>).
    /// SELF-CONTAINED by design (only <see cref="DefReflection"/>, <see cref="PermissionManager"/>,
    /// <see cref="GeoRuntime"/> + AccessTools) so it links standalone into the unit-test build — it does NOT
    /// call the heavier PersonnelReflection/GeoSiteReflection bridges (it re-derives the small pieces it needs:
    /// GeoUnitId, container scan, site-by-id). Each entry point runs the SAME native the game itself uses
    /// (decompile-verified 2026-07-06):
    ///   • equip / augment → <c>GeoCharacter.SetItems(armour, equipment, inventory, freeReload)</c>
    ///     (GeoCharacter.cs:831 — a null list is left unchanged; augment sets only the armour/bodypart list);
    ///   • hire → <c>GeoPhoenixFaction.HireNakedRecruit(GeoUnitDescriptor, IGeoCharacterContainer)</c>
    ///     (:662 — the exact recruit-screen call, UIStateRosterRecruits.cs:301; havens + naked pool both use it);
    ///   • transfer → native <c>RemoveCharacter</c>(current)+<c>AddCharacter</c>(dest) (no dedicated method);
    ///   • dismiss → <c>GeoFaction.KillCharacter(unit, Dismissed)</c> (:1593, UIStateEditSoldier.cs:425);
    ///   • rename → <c>GeoCharacter.Rename(string)</c> (:826);
    ///   • containment kill / harvest → <c>GeoPhoenixFaction.KillCapturedUnit</c> (:771, kill button
    ///     UIStateRosterAliens.cs:256) / <c>HarvestCapturedUnit(unit, ResourceType)</c> (:881, dismantle
    ///     buttons :275/:296 — funnels through KillCapturedUnit :893, yield via Wallet.Give → 0xA0);
    ///   • level-up ability / stat spend → <c>CharacterProgression.LearnAbility</c> /
    ///     <c>ModifyBaseStat</c> with native cost re-derivation (<c>GetAbilitySlotCost</c> /
    ///     <c>GetBaseStatCost</c> + <c>CanLearnAbility</c>/<c>CanModifyBaseStat</c> gates,
    ///     CharacterProgression.cs:162/201/274/296) and the soldier-first/faction-spill SP split
    ///     (<see cref="ProgressionSpend"/> — the UIModuleCharacterProgression.ConsumeAbilityCost mirror).
    /// Every native call runs on the HOST inside <c>SyncApplyScope</c> (the OnActionRequest apply path), so the
    /// client-suppress interceptors pass through AND the existing PS1/PS2/PS3 dirty Postfix hooks fire
    /// (authoritative write → mirror back on #6/#9/#10). All reflection is null-safe best-effort: a miss logs
    /// and no-ops. In a unit-test process (no game assembly) every resolve returns null → every Apply is inert,
    /// exactly like <see cref="Actions.MoveVehicleAction"/> (Apply is in-game verified, not unit-tested).
    /// </summary>
    public static class PersonnelEditReflection
    {
        // ─── Ownership (PURE — unit-testable, no game types) ──────────────────

        /// <summary>PS4 ownership gate for a soldier-targeted intent: the actor may edit THIS soldier when it
        /// is FullCommander (default co-op grant) or holds ControlSoldiers AND owns the GeoUnitId (the
        /// SoldierAssignment 0x41 registry). Wraps <see cref="PermissionManager.CanControlSoldier"/> — the SAME
        /// predicate tactical soldier-control uses, keyed by the SAME GeoUnitId.</summary>
        public static bool OwnsSoldier(Guid actor, long unitId)
            => actor != Guid.Empty && PermissionManager.CanControlSoldier(actor, unchecked((int)unitId));

        // ─── Cached bindings ──────────────────────────────────────────────────

        private static Type _geoItemType;         // PhoenixPoint.Geoscape.Entities.GeoItem
        private static Type _charType;            // PhoenixPoint.Geoscape.Entities.GeoCharacter
        private static Type _deathReasonType;     // PhoenixPoint.Geoscape.Levels.CharacterDeathReason
        private static Type _geoHavenType;        // PhoenixPoint.Geoscape.Entities.GeoHaven
        private static PropertyInfo _itemDefProp; // GeoItem.ItemDef
        private static PropertyInfo _charIdProp;  // GeoCharacter.Id (GeoTacUnitId)
        private static FieldInfo _tacIdField;     // GeoTacUnitId._id
        private static FieldInfo _siteIdField;    // GeoSite.SiteId
        private static FieldInfo _vehicleIdField; // GeoVehicle.VehicleID
        private static MethodInfo _setItems;      // GeoCharacter.SetItems
        private static MethodInfo _rename;        // GeoCharacter.Rename(string)
        private static MethodInfo _killCharacter; // GeoPhoenixFaction.KillCharacter
        private static MethodInfo _hireNaked;     // GeoPhoenixFaction.HireNakedRecruit
        private static MethodInfo _killCaptured;  // GeoPhoenixFaction.KillCapturedUnit
        private static MethodInfo _harvestCaptured; // GeoPhoenixFaction.HarvestCapturedUnit
        private static Type _resourceTypeType;    // PhoenixPoint.Common.Core.ResourceType
        private static PropertyInfo _havenAvailRecruit; // GeoHaven.AvailableRecruit
        // Progression intents (level-up / stat spend) — CharacterProgression members are single-overload
        private static Type _charBaseAttrType;          // PhoenixPoint.Common.Entities.Characters.CharacterBaseAttribute
        private static Type _abilityTrackSourceType;    // PhoenixPoint.Common.Entities.Characters.AbilityTrackSource
        private static PropertyInfo _progressionProp;   // GeoCharacter.Progression
        private static MethodInfo _getAbilityTrack;     // CharacterProgression.GetAbilityTrack(AbilityTrackSource)
        private static MethodInfo _canLearnAbility;     // CharacterProgression.CanLearnAbility(slot, str, will, speed)
        private static MethodInfo _getAbilitySlotCost;  // CharacterProgression.GetAbilitySlotCost(slot)
        private static MethodInfo _learnAbility;        // CharacterProgression.LearnAbility(slot)
        private static MethodInfo _getBaseStat;         // CharacterProgression.GetBaseStat(stat)
        private static MethodInfo _canModifyBaseStat;   // CharacterProgression.CanModifyBaseStat(stat, toValue)
        private static MethodInfo _getBaseStatCost;     // CharacterProgression.GetBaseStatCost(stat, forValue)
        private static MethodInfo _modifyBaseStat;      // CharacterProgression.ModifyBaseStat(stat, amount)
        private static FieldInfo _skillPointsField;     // CharacterProgression.SkillPoints (public field)
        private static FieldInfo _factionSkillpointsField; // GeoPhoenixFaction.Skillpoints (public field)
        private static FieldInfo _trackSlotsField;      // AbilityTrack.AbilitiesByLevel (AbilityTrackSlot[])
        private static FieldInfo _slotAbilityField;     // AbilityTrackSlot.Ability (public field)
        private static PropertyInfo _sharedDataProp;    // GeoLevelController.SharedData
        private static FieldInfo _sharedGameTagsField;  // SharedData.SharedGameTags (SharedGameTagsDataDef)
        private static FieldInfo _mutoidClassTagField;  // SharedGameTagsDataDef.MutoidClassTag (ClassTagDef)
        private static PropertyInfo _gameTagsProp;      // GeoCharacter.GameTags (GameTagsList)
        private static PropertyInfo _sitesProp;   // GeoFaction.Sites
        private static PropertyInfo _vehiclesProp;// GeoFaction.Vehicles
        private static FieldInfo _mapField;       // GeoLevelController.Map
        private static PropertyInfo _allSitesProp;// GeoMap.AllSites
        // v2 storage-delta reconcile (equip): faction global store + its single-item add/remove.
        private static PropertyInfo _useGlobalStorageProp; // GeoPhoenixFaction.UseGlobalStorage
        private static MethodInfo _storeAddItem;    // ItemStorage.AddItem(GeoItem)
        private static MethodInfo _storeRemoveItem; // ItemStorage.RemoveItem(GeoItem)

        private static Type GeoItemT() => _geoItemType ?? (_geoItemType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoItem"));
        private static Type CharT() => _charType ?? (_charType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter"));

        // ─── CLIENT-side serialize: GeoItem list → def guids (for the equip/augment intent) ──

        /// <summary>Read the stable <c>BaseDef.Guid</c> of each item in a native <c>IEnumerable&lt;GeoItem&gt;</c>
        /// (the SetItems arg the client is trying to apply). Null in → empty list (a cleared slot is honest, not
        /// skipped). An unreadable item is dropped (logged) rather than aborting the relay.</summary>
        public static List<string> ReadItemGuids(object geoItemEnumerable)
        {
            var guids = new List<string>();
            if (geoItemEnumerable is IEnumerable items)
            {
                if (_itemDefProp == null && GeoItemT() != null) _itemDefProp = AccessTools.Property(GeoItemT(), "ItemDef");
                foreach (var it in items)
                {
                    if (it == null) continue;
                    try
                    {
                        object def = _itemDefProp?.GetValue(it, null);
                        string guid = DefReflection.GetGuid(def);
                        if (!string.IsNullOrEmpty(guid)) guids.Add(guid);
                    }
                    catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.ReadItemGuids item skipped: " + ex.Message); }
                }
            }
            return guids;
        }

        /// <summary>CLIENT: the def guids of a soldier's CURRENT loadout list (private field
        /// <c>_armourItems</c>/<c>_equipmentItems</c>/<c>_inventoryItems</c>). Fills a null SetItems arg (null =
        /// "leave this slot unchanged") so the relayed <see cref="Actions.EquipSoldierAction"/> always carries
        /// the COMPLETE intended final loadout (the host SetItems replaces all three lists).</summary>
        public static string[] ReadCurrentItemGuids(object soldier, string fieldName)
        {
            try
            {
                if (soldier == null) return Array.Empty<string>();
                var f = CachedField(soldier.GetType(), fieldName);
                return ReadItemGuids(f?.GetValue(soldier)).ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>CLIENT: map a hire descriptor to a host-resolvable source key. Naked pool → (1, ordinal)
        /// (the #10 full-set re-emit preserves order); haven → (0, havenSiteId) (one recruit per haven, #10 keys
        /// by SiteId). (-1,-1) = unresolved (the interceptor then suppresses + logs without relaying).</summary>
        public static bool ResolveRecruitSource(GeoRuntime rt, object descriptor, out int kind, out int id)
        {
            kind = -1; id = -1;
            if (descriptor == null) return false;
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac != null)
                {
                    var nf = AccessTools.Field(fac.GetType(), "_nakedRecruits");
                    if (nf?.GetValue(fac) is IEnumerable pool)
                    {
                        int i = 0;
                        foreach (var entry in pool)
                        {
                            object k = entry;
                            var kp = entry?.GetType().GetProperty("Key");
                            if (kp != null) k = kp.GetValue(entry, null);
                            if (ReferenceEquals(k, descriptor)) { kind = 1; id = i; return true; }
                            i++;
                        }
                    }
                }
                foreach (var site in AllSites(rt))
                {
                    object haven = HavenComponent(site);
                    if (haven == null) continue;
                    if (_havenAvailRecruit == null) _havenAvailRecruit = AccessTools.Property(haven.GetType(), "AvailableRecruit");
                    if (ReferenceEquals(_havenAvailRecruit?.GetValue(haven, null), descriptor))
                    {
                        kind = 0; id = GetSiteId(site); return id >= 0;
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.ResolveRecruitSource failed: " + ex.Message); }
            return false;
        }

        /// <summary>CLIENT: map a containment-screen captive (<c>GeoUnitDescriptor</c>) to a host-resolvable
        /// key: its ORDINAL in <c>_capturedUnits</c> (the #10 full-set mirror preserves host order — the
        /// naked-pool precedent) + the TemplateDef-guid FINGERPRINT <see cref="ContainmentTarget"/> validates
        /// against drift. False = unresolved (the interceptor suppresses + logs without relaying).</summary>
        public static bool ResolveCapturedSource(GeoRuntime rt, object descriptor, out int ordinal, out string templateGuid)
        {
            ordinal = -1; templateGuid = null;
            if (descriptor == null) return false;
            try
            {
                var list = CapturedList(rt);
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!ReferenceEquals(list[i], descriptor)) continue;
                    ordinal = i;
                    templateGuid = CapturedTemplateGuid(descriptor);
                    return !string.IsNullOrEmpty(templateGuid);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.ResolveCapturedSource failed: " + ex.Message); }
            return false;
        }

        // ─── HOST-side apply ──────────────────────────────────────────────────

        /// <summary>Full-loadout replace (equip): rebuild each of the three GeoItem lists from its def guids and
        /// run the native SetItems (freeReload:true — a fresh def-built item carries no ammo state to preserve;
        /// exact charges converge via the authoritative #9 blob), then reconcile faction storage by the
        /// authoritative loadout DELTA (v2 rebuild — <see cref="ReconcileStorageDelta"/>).</summary>
        public static void Equip(GeoRuntime rt, long unitId, string[] armourGuids, string[] equipGuids,
                                 string[] invGuids, bool returnRemovedToStorage = true)
        {
            var soldier = ResolveSoldierById(rt, unitId);
            if (soldier == null) { LogUnresolved("Equip", unitId); return; }
            // Snapshot the OLD loadout BEFORE SetItems so the storage delta is computed against authoritative
            // host state, never a client storage view (client never simulates; host is the one storage writer).
            var oldLoadout = new List<string>();
            oldLoadout.AddRange(ReadCurrentItemGuids(soldier, "_armourItems"));
            oldLoadout.AddRange(ReadCurrentItemGuids(soldier, "_equipmentItems"));
            oldLoadout.AddRange(ReadCurrentItemGuids(soldier, "_inventoryItems"));
            InvokeSetItems(soldier, BuildItems(armourGuids), BuildItems(equipGuids), BuildItems(invGuids));
            var newLoadout = new List<string>();
            if (armourGuids != null) newLoadout.AddRange(armourGuids);
            if (equipGuids != null) newLoadout.AddRange(equipGuids);
            if (invGuids != null) newLoadout.AddRange(invGuids);
            ReconcileStorageDelta(rt, oldLoadout, newLoadout, returnRemovedToStorage);
        }

        /// <summary>Mirror native <c>UIStateEditSoldier.UpdateStorage</c>, but from the AUTHORITATIVE loadout
        /// delta instead of a client storage snapshot: items the soldier GAINED (new \ old, multiset) are removed
        /// from faction storage (they came from it); items it LOST (old \ new) are added back — gated by
        /// <paramref name="returnRemoved"/> (false for scrap: destroyed, not returned). Relative delta ⇒ two peers
        /// pulling from shared storage never clobber each other (the absolute-snapshot dupe). E1: reconciles the
        /// FACTION global store (<c>UseGlobalStorage</c>); a site-local store is a noted follow-up. The #1 dirty
        /// mark rides the SetItems seam (host mirror), so no explicit mark here.</summary>
        private static void ReconcileStorageDelta(GeoRuntime rt, List<string> oldLoadout, List<string> newLoadout, bool returnRemoved)
        {
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return;
                if (_useGlobalStorageProp == null) _useGlobalStorageProp = AccessTools.Property(fac.GetType(), "UseGlobalStorage");
                bool global = _useGlobalStorageProp?.GetValue(fac, null) is bool g && g;
                if (!global)
                {
                    Debug.Log("[Multiplayer] PersonnelEditReflection.ReconcileStorageDelta: faction uses site-local storage — storage delta deferred (E1 reconciles global store only)");
                    return;
                }
                object store = ItemStorageReflection.GetStorage(rt);
                if (store == null) return;
                MultisetDiff(oldLoadout, newLoadout, out var added, out var removed);
                if (_storeAddItem == null) _storeAddItem = AccessTools.Method(store.GetType(), "AddItem");
                if (_storeRemoveItem == null) _storeRemoveItem = AccessTools.Method(store.GetType(), "RemoveItem");
                var gainedItems = added.Count > 0 ? BuildItems(added.ToArray()) : null;
                var lostItems = returnRemoved && removed.Count > 0 ? BuildItems(removed.ToArray()) : null;
                if (gainedItems != null) foreach (var it in gainedItems) _storeRemoveItem?.Invoke(store, new[] { it });
                if (lostItems != null) foreach (var it in lostItems) _storeAddItem?.Invoke(store, new[] { it });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.ReconcileStorageDelta failed: " + ex.Message); }
        }

        /// <summary>Multiset difference of two guid lists: <paramref name="added"/> = new minus old,
        /// <paramref name="removed"/> = old minus new (honouring per-guid counts, so 2→1 of a def yields one
        /// removed, not a full clear).</summary>
        private static void MultisetDiff(List<string> oldList, List<string> newList, out List<string> added, out List<string> removed)
        {
            added = new List<string>();
            removed = new List<string>();
            var oldCounts = new Dictionary<string, int>();
            var newCounts = new Dictionary<string, int>();
            foreach (var g in oldList) { if (g == null) continue; oldCounts.TryGetValue(g, out int c); oldCounts[g] = c + 1; }
            foreach (var g in newList) { if (g == null) continue; newCounts.TryGetValue(g, out int c); newCounts[g] = c + 1; }
            foreach (var kv in newCounts)
            {
                oldCounts.TryGetValue(kv.Key, out int o);
                for (int i = 0; i < kv.Value - o; i++) added.Add(kv.Key);
            }
            foreach (var kv in oldCounts)
            {
                newCounts.TryGetValue(kv.Key, out int n);
                for (int i = 0; i < kv.Value - n; i++) removed.Add(kv.Key);
            }
        }

        /// <summary>Augment = body-part swap through the SAME SetItems, armour(bodypart) list only; equipment/
        /// inventory pass null → left unchanged (GeoCharacter.cs:848/860 null-skip). Also deducts the
        /// augment's <c>ManufacturePrice</c> from the faction wallet (the native UI calls
        /// <c>Wallet.Take(augment.ManufacturePrice)</c> inside <c>OnAugmentApplied</c>, which the relay
        /// bypasses — without this the client-relayed augment is free). The added item is derived from the
        /// (old → new) bodypart delta.</summary>
        public static void Augment(GeoRuntime rt, long unitId, string[] bodypartGuids)
        {
            var soldier = ResolveSoldierById(rt, unitId);
            if (soldier == null) { LogUnresolved("Augment", unitId); return; }
            // Snapshot old bodyparts BEFORE SetItems (same pattern as Equip's storage-delta snapshot).
            var oldBodyparts = new List<string>(ReadCurrentItemGuids(soldier, "_armourItems"));
            InvokeSetItems(soldier, BuildItems(bodypartGuids), null, null);
            // Wallet deduction: the ADDED item (new \ old delta) is the augment whose ManufacturePrice
            // the native flow would have deducted via Wallet.Take. Typically exactly one item added.
            DeductAugmentCost(rt, oldBodyparts, bodypartGuids != null ? new List<string>(bodypartGuids) : new List<string>());
        }

        /// <summary>Deduct the <c>ManufacturePrice</c> of each ADDED bodypart (the augment) from the
        /// faction wallet. Reads the individual manufacture cost fields from the ItemDef (public floats:
        /// ManufactureTech, ManufactureMaterials, ManufactureMutagen, etc., decompile ItemDef.cs:60-70)
        /// and applies negative diffs via <see cref="WalletReflection.ApplyDiff"/>. Best-effort: a miss
        /// logs and no-ops (the bodypart swap itself already succeeded).</summary>
        private static void DeductAugmentCost(GeoRuntime rt, List<string> oldBodyparts, List<string> newBodyparts)
        {
            try
            {
                MultisetDiff(oldBodyparts, newBodyparts, out var added, out _);
                if (added.Count == 0) return;
                var wallet = rt?.Wallet();
                if (wallet == null) { Debug.Log("[Multiplayer] PersonnelEditReflection.DeductAugmentCost: wallet unresolved — cost not deducted"); return; }
                // ResourceType enum values (decompile ResourceType.cs): Materials=2, Tech=4, Mutagen=0x100,
                // LivingCrystals=0x200, Orichalcum=0x400, ProteanMutane=0x800.
                var costFields = new (string field, int resType)[]
                {
                    ("ManufactureMaterials", 2),
                    ("ManufactureTech",      4),
                    ("ManufactureMutagen",   0x100),
                    ("ManufactureLivingCrystals", 0x200),
                    ("ManufactureOricalcum", 0x400),
                    ("ManufactureProteanMutane", 0x800),
                };
                foreach (var guid in added)
                {
                    object def = DefReflection.GetDefByGuid(guid);
                    if (def == null) continue;
                    foreach (var (field, resType) in costFields)
                    {
                        var fi = AccessTools.Field(def.GetType(), field);
                        if (fi == null) continue;
                        float cost = fi.GetValue(def) is float f ? f : 0f;
                        if (cost > 0f) WalletReflection.ApplyDiff(wallet, resType, -cost);
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.DeductAugmentCost failed: " + ex.Message); }
        }

        /// <summary>Hire a recruit (haven or naked pool) into a base: resolve the source GeoUnitDescriptor + the
        /// destination base Site, then run the native HireNakedRecruit.</summary>
        public static void Hire(GeoRuntime rt, int sourceKind, int sourceId, int destBaseSiteId)
        {
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) { LogUnresolved("Hire", destBaseSiteId); return; }
                object destSite = ResolveSiteById(rt, destBaseSiteId);
                if (destSite == null) { Debug.Log("[Multiplayer] PersonnelEditReflection.Hire: dest base site " + destBaseSiteId + " unresolved — skipped"); return; }
                object descriptor = sourceKind == 0 ? ResolveHavenRecruit(rt, sourceId) : ResolveNakedRecruit(fac, sourceId);
                if (descriptor == null) { Debug.Log("[Multiplayer] PersonnelEditReflection.Hire: recruit (kind=" + sourceKind + ",id=" + sourceId + ") unresolved — skipped"); return; }
                if (_hireNaked == null) _hireNaked = AccessTools.Method(fac.GetType(), "HireNakedRecruit");
                _hireNaked?.Invoke(fac, new[] { descriptor, destSite });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.Hire failed: " + ex.Message); }
        }

        /// <summary>Transfer a soldier between Phoenix containers: native RemoveCharacter(current) then
        /// AddCharacter(dest) — remove-before-add so the soldier is never in two containers.</summary>
        public static void Transfer(GeoRuntime rt, long unitId, int destKind, int destId)
        {
            try
            {
                var soldier = ResolveSoldierById(rt, unitId, out object current);
                if (soldier == null) { LogUnresolved("Transfer", unitId); return; }
                object dest = destKind == 1 ? ResolveVehicleById(rt, destId) : ResolveSiteById(rt, destId);
                if (dest == null) { Debug.Log("[Multiplayer] PersonnelEditReflection.Transfer: dest (kind=" + destKind + ",id=" + destId + ") unresolved — skipped"); return; }
                var charT = CharT();
                if (charT == null) return;
                if (current != null && !ReferenceEquals(current, dest))
                    AccessTools.Method(current.GetType(), "RemoveCharacter", new[] { charT })?.Invoke(current, new[] { soldier });
                AccessTools.Method(dest.GetType(), "AddCharacter", new[] { charT })?.Invoke(dest, new[] { soldier });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.Transfer failed: " + ex.Message); }
        }

        /// <summary>Dismiss a soldier: native KillCharacter(soldier, CharacterDeathReason.Dismissed).</summary>
        public static void Dismiss(GeoRuntime rt, long unitId)
        {
            try
            {
                var fac = rt?.PhoenixFaction();
                var soldier = ResolveSoldierById(rt, unitId);
                if (fac == null || soldier == null) { LogUnresolved("Dismiss", unitId); return; }
                if (_killCharacter == null) _killCharacter = AccessTools.Method(fac.GetType(), "KillCharacter");
                object dismissed = DismissedReason();
                if (_killCharacter == null || dismissed == null) return;
                _killCharacter.Invoke(fac, new[] { soldier, dismissed });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.Dismiss failed: " + ex.Message); }
        }

        /// <summary>Kill a captured Pandoran: native <c>GeoPhoenixFaction.KillCapturedUnit(unit)</c> (the exact
        /// containment kill-button call, UIStateRosterAliens.cs:256). Unknown captive (host trimmed/killed it
        /// since the client's last #10 mirror) → logged no-op (reject precedent).</summary>
        public static void KillCaptured(GeoRuntime rt, int ordinal, string templateGuid)
        {
            try
            {
                var fac = rt?.PhoenixFaction();
                var unit = ResolveCapturedUnit(rt, ordinal, templateGuid);
                if (fac == null || unit == null) { LogUnresolved("KillCaptured", ordinal); return; }
                if (_killCaptured == null) _killCaptured = AccessTools.Method(fac.GetType(), "KillCapturedUnit");
                _killCaptured?.Invoke(fac, new[] { unit });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.KillCaptured failed: " + ex.Message); }
        }

        /// <summary>Harvest (dismantle) a captured Pandoran for food/mutagens: native
        /// <c>GeoPhoenixFaction.HarvestCapturedUnit(unit, ResourceType)</c> (the exact dismantle-button call,
        /// UIStateRosterAliens.cs:275/296). It funnels through KillCapturedUnit + Wallet.Give, so the result
        /// mirrors via the existing #10 dirty seam + the 0xA0 wallet snapshot. Unknown captive → logged no-op.</summary>
        public static void HarvestCaptured(GeoRuntime rt, int ordinal, string templateGuid, int resourceType)
        {
            try
            {
                var fac = rt?.PhoenixFaction();
                var unit = ResolveCapturedUnit(rt, ordinal, templateGuid);
                if (fac == null || unit == null) { LogUnresolved("HarvestCaptured", ordinal); return; }
                if (_harvestCaptured == null) _harvestCaptured = AccessTools.Method(fac.GetType(), "HarvestCapturedUnit");
                if (_resourceTypeType == null) _resourceTypeType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceType");
                if (_harvestCaptured == null || _resourceTypeType == null) { LogUnresolved("HarvestCaptured", ordinal); return; }
                _harvestCaptured.Invoke(fac, new[] { unit, Enum.ToObject(_resourceTypeType, resourceType) });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.HarvestCaptured failed: " + ex.Message); }
        }

        /// <summary>Rename a soldier: native GeoCharacter.Rename(newName).</summary>
        public static void RenameSoldier(GeoRuntime rt, long unitId, string newName)
        {
            try
            {
                var soldier = ResolveSoldierById(rt, unitId);
                if (soldier == null) { LogUnresolved("Rename", unitId); return; }
                if (_rename == null && CharT() != null) _rename = AccessTools.Method(CharT(), "Rename", new[] { typeof(string) });
                _rename?.Invoke(soldier, new object[] { newName ?? string.Empty });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.RenameSoldier failed: " + ex.Message); }
        }

        // ─── HOST-side apply: progression (level-up ability / stat spend) ─────

        /// <summary>Buy an ability on a progression track with skill points: resolve the
        /// (trackSource, slotIndex) slot, fingerprint-check the relayed ability-def guid (an EMPTY slot —
        /// the personal/mutoid pick — takes the relayed def, mirroring BuyAbility's null-slot assign,
        /// UIModuleCharacterProgression.cs:393-396), gate on the NATIVE <c>CanLearnAbility</c>
        /// (level/prereq/duplicate) + the combined SP pool, deduct via <see cref="ProgressionSpend"/>
        /// (soldier SkillPoints first, spill into GeoPhoenixFaction.Skillpoints — ConsumeAbilityCost
        /// :428-442), then run the native <c>LearnAbility</c>. Any unresolved/failed step logs + no-ops;
        /// the learned ability + spent SP mirror back on the #9 blob (AddAbility dirty seam).</summary>
        public static void LevelUpAbility(GeoRuntime rt, long unitId, int trackSource, int slotIndex, string abilityGuid)
        {
            object slot = null;
            bool assignedHere = false;   // pre-gate personal-pick assign, rolled back on any reject
            try
            {
                var soldier = ResolveSoldierById(rt, unitId);
                if (soldier == null) { LogUnresolved("LevelUpAbility", unitId); return; }
                if (HasPandoranProgression(rt, soldier))
                { Debug.Log("[Multiplayer] PersonnelEditReflection.LevelUpAbility: unit " + unitId + " has mutoid (mutagen-cost) progression — out of the SP intent family, intent skipped"); return; }
                object prog = ReadProgression(soldier);
                if (prog == null) { LogUnresolved("LevelUpAbility(progression)", unitId); return; }
                EnsureProgressionMembers(prog);
                if (_abilityTrackSourceType == null)
                    _abilityTrackSourceType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Characters.AbilityTrackSource");
                if (_getAbilityTrack == null || _canLearnAbility == null || _getAbilitySlotCost == null
                    || _learnAbility == null || _abilityTrackSourceType == null || _charBaseAttrType == null) return;
                object track = _getAbilityTrack.Invoke(prog, new[] { Enum.ToObject(_abilityTrackSourceType, trackSource) });
                if (track == null) { Debug.Log("[Multiplayer] PersonnelEditReflection.LevelUpAbility: unit " + unitId + " has no track source=" + trackSource + " — intent skipped"); return; }
                if (_trackSlotsField == null) _trackSlotsField = AccessTools.Field(track.GetType(), "AbilitiesByLevel");
                var slots = _trackSlotsField?.GetValue(track) as Array;
                if (slots == null || slotIndex < 0 || slotIndex >= slots.Length)
                { Debug.Log("[Multiplayer] PersonnelEditReflection.LevelUpAbility: slot " + slotIndex + " out of range for unit " + unitId + " — intent skipped"); return; }
                slot = slots.GetValue(slotIndex);
                if (slot == null) { Debug.Log("[Multiplayer] PersonnelEditReflection.LevelUpAbility: null slot " + slotIndex + " — intent skipped"); return; }
                if (_slotAbilityField == null) _slotAbilityField = AccessTools.Field(slot.GetType(), "Ability");
                object slotAbility = _slotAbilityField?.GetValue(slot);
                if (slotAbility == null)
                {
                    object def = DefReflection.GetDefByGuid(abilityGuid);
                    if (def == null) { Debug.Log("[Multiplayer] PersonnelEditReflection.LevelUpAbility: ability def " + abilityGuid + " unresolved — intent skipped"); return; }
                    // Native BuyAbility pre-assigns the personal pick (:393-396) and the gates below READ
                    // slot.Ability (CanLearnAbility level/prereq + GetAbilitySlotCost) — so assign FIRST,
                    // but roll back on any reject: a dangling slot.Ability lives in _abilityTracks
                    // ([SerializeMember]), would replicate on #9 and permanently lock the slot's pick.
                    _slotAbilityField.SetValue(slot, def);
                    assignedHere = true;
                }
                else if (DefReflection.GetGuid(slotAbility) != abilityGuid)
                { Debug.Log("[Multiplayer] PersonnelEditReflection.LevelUpAbility: slot ability drifted (expected " + abilityGuid + ") — intent skipped"); return; }
                // Native gate: level requirement + prerequisite slot + not-already-learned (the stat args
                // are part of the native signature; current values keep the call honest).
                object[] canArgs = { slot, GetBaseStatInt(prog, 0), GetBaseStatInt(prog, 1), GetBaseStatInt(prog, 2) };
                if (!(bool)_canLearnAbility.Invoke(prog, canArgs))
                {
                    Debug.Log("[Multiplayer] PersonnelEditReflection.LevelUpAbility: CanLearnAbility=false for unit " + unitId + " slot " + slotIndex + " — intent skipped");
                    RollbackSlotAssign(slot, assignedHere);
                    return;
                }
                int cost = (int)_getAbilitySlotCost.Invoke(prog, new[] { slot });
                if (!TrySpendSkillPoints(rt, prog, cost, "LevelUpAbility", unitId)) { RollbackSlotAssign(slot, assignedHere); return; }
                _learnAbility.Invoke(prog, new[] { slot });
                Debug.Log("[Multiplayer] PersonnelEditReflection.LevelUpAbility: unit " + unitId + " learned " + abilityGuid + " (cost " + cost + ")");
            }
            catch (Exception ex)
            {
                RollbackSlotAssign(slot, assignedHere);
                Debug.LogError("[Multiplayer] PersonnelEditReflection.LevelUpAbility failed: " + ex.Message);
            }
        }

        /// <summary>Undo the pre-gate personal-pick slot assign when a later gate rejects (all-or-nothing:
        /// nothing learned + no SP spent must also mean no slot.Ability write survives).</summary>
        private static void RollbackSlotAssign(object slot, bool assignedHere)
        {
            if (!assignedHere || slot == null || _slotAbilityField == null) return;
            try { _slotAbilityField.SetValue(slot, null); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.RollbackSlotAssign failed: " + ex.Message); }
        }

        /// <summary>Spend skill points on ONE base stat, <paramref name="delta"/> single points applied
        /// stepwise the way the native +1 button does: per point, native <c>CanModifyBaseStat</c> (max-cap)
        /// + <c>GetBaseStatCost(stat, cur+1)</c> re-derived on the HOST (the client's SP arithmetic is
        /// never trusted), <see cref="ProgressionSpend"/> pool split, then native <c>ModifyBaseStat(+1)</c>.
        /// A mid-loop failure keeps the points already applied (each was individually affordable/legal —
        /// partial converge beats all-or-nothing rollback of native writes) and logs the shortfall.</summary>
        public static void SpendStatPoints(GeoRuntime rt, long unitId, int statId, int delta)
        {
            try
            {
                if (statId < 0 || statId > 2 || delta <= 0) return;   // action Validate mirrors this
                var soldier = ResolveSoldierById(rt, unitId);
                if (soldier == null) { LogUnresolved("SpendStatPoints", unitId); return; }
                if (HasPandoranProgression(rt, soldier))
                { Debug.Log("[Multiplayer] PersonnelEditReflection.SpendStatPoints: unit " + unitId + " has mutoid (mutagen-cost) progression — out of the SP intent family, intent skipped"); return; }
                object prog = ReadProgression(soldier);
                if (prog == null) { LogUnresolved("SpendStatPoints(progression)", unitId); return; }
                EnsureProgressionMembers(prog);
                if (_charBaseAttrType == null || _getBaseStat == null || _canModifyBaseStat == null
                    || _getBaseStatCost == null || _modifyBaseStat == null) return;
                object stat = Enum.ToObject(_charBaseAttrType, statId);
                int applied = 0;
                for (int i = 0; i < delta; i++)
                {
                    int cur = (int)_getBaseStat.Invoke(prog, new[] { stat });
                    if (!(bool)_canModifyBaseStat.Invoke(prog, new object[] { stat, cur + 1 })) break;   // native max cap
                    int cost = (int)_getBaseStatCost.Invoke(prog, new object[] { stat, cur + 1 });
                    if (!TrySpendSkillPoints(rt, prog, cost, "SpendStatPoints", unitId)) break;
                    _modifyBaseStat.Invoke(prog, new object[] { stat, 1 });
                    applied++;
                }
                Debug.Log("[Multiplayer] PersonnelEditReflection.SpendStatPoints: unit " + unitId + " stat " + statId
                          + " +" + applied + (applied == delta ? "" : " (requested +" + delta + " — cap/pool stop)"));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.SpendStatPoints failed: " + ex.Message); }
        }

        /// <summary>Deduct <paramref name="cost"/> SP: soldier <c>CharacterProgression.SkillPoints</c>
        /// first, shortfall spills into <c>GeoPhoenixFaction.Skillpoints</c> (both public fields).
        /// False (logged) when the combined pool cannot cover it — nothing is written.</summary>
        private static bool TrySpendSkillPoints(GeoRuntime rt, object prog, int cost, string op, long unitId)
        {
            if (_skillPointsField == null) _skillPointsField = AccessTools.Field(prog.GetType(), "SkillPoints");
            if (_skillPointsField == null) return false;
            var fac = rt?.PhoenixFaction();
            if (_factionSkillpointsField == null && fac != null) _factionSkillpointsField = AccessTools.Field(fac.GetType(), "Skillpoints");
            int sp = (int)_skillPointsField.GetValue(prog);
            int fsp = fac != null && _factionSkillpointsField != null ? (int)_factionSkillpointsField.GetValue(fac) : 0;
            if (!ProgressionSpend.TrySplit(cost, sp, fsp, out int newSp, out int newFsp))
            {
                Debug.Log("[Multiplayer] PersonnelEditReflection." + op + ": unit " + unitId + " cannot afford cost "
                          + cost + " (sp=" + sp + " faction=" + fsp + ") — intent skipped");
                return false;
            }
            _skillPointsField.SetValue(prog, newSp);
            if (newFsp != fsp && fac != null && _factionSkillpointsField != null) _factionSkillpointsField.SetValue(fac, newFsp);
            return true;
        }

        /// <summary>HOST re-derivation of the UI's <c>_hasPandoranProgression</c> init
        /// (UIModuleCharacterProgression.cs:467): <c>GeoCharacter.GameTags</c> contains
        /// <c>GeoLevelController.SharedData.SharedGameTags.MutoidClassTag</c>. Mutoid progression is
        /// mutagen-funded (Wallet.Take, ConsumeAbilityCost :430-434) — out of the SP intent family, so
        /// the client suppresses it without relay; a stale/forged intent must not charge SP here either.
        /// Unresolvable → false (the SP path is correct for every non-mutoid soldier).</summary>
        private static bool HasPandoranProgression(GeoRuntime rt, object soldier)
        {
            try
            {
                var geo = rt?.GeoLevel();
                if (geo == null || soldier == null) return false;
                if (_sharedDataProp == null) _sharedDataProp = AccessTools.Property(geo.GetType(), "SharedData");
                object shared = _sharedDataProp?.GetValue(geo, null);
                if (shared == null) return false;
                if (_sharedGameTagsField == null) _sharedGameTagsField = AccessTools.Field(shared.GetType(), "SharedGameTags");
                object sharedTags = _sharedGameTagsField?.GetValue(shared);
                if (sharedTags == null) return false;
                if (_mutoidClassTagField == null) _mutoidClassTagField = AccessTools.Field(sharedTags.GetType(), "MutoidClassTag");
                object mutoidTag = _mutoidClassTagField?.GetValue(sharedTags);
                if (mutoidTag == null) return false;
                if (_gameTagsProp == null) _gameTagsProp = AccessTools.Property(soldier.GetType(), "GameTags");
                if (!(_gameTagsProp?.GetValue(soldier, null) is IEnumerable tags)) return false;
                foreach (var tag in tags)
                    if (ReferenceEquals(tag, mutoidTag)) return true;   // defs are singletons
                return false;
            }
            catch { return false; }
        }

        /// <summary>The soldier's live <c>GeoCharacter.Progression</c> (CharacterProgression), or null.</summary>
        private static object ReadProgression(object soldier)
        {
            try
            {
                if (_progressionProp == null) _progressionProp = AccessTools.Property(soldier.GetType(), "Progression");
                return _progressionProp?.GetValue(soldier, null);
            }
            catch { return null; }
        }

        private static int GetBaseStatInt(object prog, int statId)
            => (int)_getBaseStat.Invoke(prog, new[] { Enum.ToObject(_charBaseAttrType, statId) });

        /// <summary>Bind the CharacterProgression members once (all single-overload, decompile-verified
        /// 2026-07-07: GetAbilityTrack/CanLearnAbility/GetAbilitySlotCost/LearnAbility/GetBaseStat/
        /// CanModifyBaseStat/GetBaseStatCost/ModifyBaseStat + the public SkillPoints field).</summary>
        private static void EnsureProgressionMembers(object prog)
        {
            var t = prog.GetType();
            if (_charBaseAttrType == null) _charBaseAttrType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Characters.CharacterBaseAttribute");
            if (_getAbilityTrack == null) _getAbilityTrack = AccessTools.Method(t, "GetAbilityTrack");
            if (_canLearnAbility == null) _canLearnAbility = AccessTools.Method(t, "CanLearnAbility");
            if (_getAbilitySlotCost == null) _getAbilitySlotCost = AccessTools.Method(t, "GetAbilitySlotCost");
            if (_learnAbility == null) _learnAbility = AccessTools.Method(t, "LearnAbility");
            if (_getBaseStat == null) _getBaseStat = AccessTools.Method(t, "GetBaseStat");
            if (_canModifyBaseStat == null) _canModifyBaseStat = AccessTools.Method(t, "CanModifyBaseStat");
            if (_getBaseStatCost == null) _getBaseStatCost = AccessTools.Method(t, "GetBaseStatCost");
            if (_modifyBaseStat == null) _modifyBaseStat = AccessTools.Method(t, "ModifyBaseStat");
            if (_skillPointsField == null) _skillPointsField = AccessTools.Field(t, "SkillPoints");
        }

        // ─── self-contained resolution ────────────────────────────────────────

        /// <summary>A soldier's shared GeoUnitId (GeoCharacter.Id → GeoTacUnitId._id), 0 = unresolved.</summary>
        private static long ReadUnitId(object soldier)
        {
            if (soldier == null) return 0;
            try
            {
                if (_charIdProp == null) _charIdProp = AccessTools.Property(soldier.GetType(), "Id");
                object gid = _charIdProp?.GetValue(soldier, null);
                if (gid == null) return 0;
                if (_tacIdField == null) _tacIdField = AccessTools.Field(gid.GetType(), "_id");
                if (_tacIdField != null) return (int)_tacIdField.GetValue(gid);
                return Convert.ToInt32(gid);
            }
            catch { return 0; }
        }

        private static object ResolveSoldierById(GeoRuntime rt, long unitId) => ResolveSoldierById(rt, unitId, out _);

        /// <summary>Scan every Phoenix container (vehicles + sites) for the soldier with this GeoUnitId; also
        /// returns the container currently holding it (for transfer's remove-before-add).</summary>
        private static object ResolveSoldierById(GeoRuntime rt, long unitId, out object container)
        {
            container = null;
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;
                EnsureFactionProps(fac);
                var hit = ScanForSoldier(_vehiclesProp?.GetValue(fac, null) as IEnumerable, unitId, ref container)
                          ?? ScanForSoldier(_sitesProp?.GetValue(fac, null) as IEnumerable, unitId, ref container);
                return hit;
            }
            catch { return null; }
        }

        private static object ScanForSoldier(IEnumerable containers, long unitId, ref object container)
        {
            if (containers == null) return null;
            foreach (var c in containers)
            {
                var list = TacUnits(c);
                if (list == null) continue;
                foreach (var ch in list)
                    if (ReadUnitId(ch) == unitId) { container = c; return ch; }
            }
            return null;
        }

        private static IList TacUnits(object container)
        {
            try { return CachedField(container.GetType(), "_tacUnits")?.GetValue(container) as IList; }
            catch { return null; }
        }

        private static object ResolveVehicleById(GeoRuntime rt, int vehicleId)
        {
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;
                EnsureFactionProps(fac);
                if (!(_vehiclesProp?.GetValue(fac, null) is IEnumerable vehicles)) return null;
                if (_vehicleIdField == null)
                {
                    var vt = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
                    if (vt != null) _vehicleIdField = AccessTools.Field(vt, "VehicleID");
                }
                foreach (var v in vehicles)
                    if (v != null && _vehicleIdField != null && Convert.ToInt32(_vehicleIdField.GetValue(v)) == vehicleId) return v;
            }
            catch { }
            return null;
        }

        private static object ResolveSiteById(GeoRuntime rt, int siteId)
        {
            if (siteId < 0) return null;
            foreach (var s in AllSites(rt))
                if (GetSiteId(s) == siteId) return s;
            return null;
        }

        private static int GetSiteId(object site)
        {
            try
            {
                if (site == null) return -1;
                if (_siteIdField == null)
                {
                    var st = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
                    if (st != null) _siteIdField = AccessTools.Field(st, "SiteId");
                }
                object v = _siteIdField?.GetValue(site);
                return v is int i ? i : -1;
            }
            catch { return -1; }
        }

        private static object ResolveHavenRecruit(GeoRuntime rt, int havenSiteId)
        {
            try
            {
                object haven = HavenComponent(ResolveSiteById(rt, havenSiteId));
                if (haven == null) return null;
                if (_havenAvailRecruit == null) _havenAvailRecruit = AccessTools.Property(haven.GetType(), "AvailableRecruit");
                return _havenAvailRecruit?.GetValue(haven, null);
            }
            catch { return null; }
        }

        private static object ResolveNakedRecruit(object fac, int ordinal)
        {
            try
            {
                var f = AccessTools.Field(fac.GetType(), "_nakedRecruits");
                if (!(f?.GetValue(fac) is IEnumerable pool)) return null;
                int i = 0;
                foreach (var entry in pool)
                {
                    object key = entry;
                    var kvpKey = entry?.GetType().GetProperty("Key");
                    if (kvpKey != null) key = kvpKey.GetValue(entry, null);
                    if (i++ == ordinal) return key;
                }
            }
            catch { }
            return null;
        }

        /// <summary>The live <c>GeoPhoenixFaction._capturedUnits</c> list (containment pool), or null.</summary>
        private static IList CapturedList(GeoRuntime rt)
        {
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;
                return CachedField(fac.GetType(), "_capturedUnits")?.GetValue(fac) as IList;
            }
            catch { return null; }
        }

        /// <summary>A captive's stable fingerprint: <c>GeoUnitDescriptor.UnitType.TemplateDef</c> BaseDef guid
        /// (both readonly fields, decompile GeoUnitDescriptor.cs:176/36), or null.</summary>
        private static string CapturedTemplateGuid(object descriptor)
        {
            try
            {
                if (descriptor == null) return null;
                var unitType = CachedField(descriptor.GetType(), "UnitType")?.GetValue(descriptor);
                var def = unitType != null ? CachedField(unitType.GetType(), "TemplateDef")?.GetValue(unitType) : null;
                return DefReflection.GetGuid(def);
            }
            catch { return null; }
        }

        /// <summary>HOST: resolve a relayed (ordinal, fingerprint) captive key against the live containment
        /// list via the pure <see cref="ContainmentTarget"/> (ordinal hit → drift fallback → null = reject).</summary>
        private static object ResolveCapturedUnit(GeoRuntime rt, int ordinal, string templateGuid)
        {
            try
            {
                var list = CapturedList(rt);
                if (list == null) return null;
                int idx = ContainmentTarget.Resolve(list.Count, i => CapturedTemplateGuid(list[i]), ordinal, templateGuid);
                return idx >= 0 ? list[idx] : null;
            }
            catch { return null; }
        }

        private static IEnumerable AllSites(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null) yield break;
            if (_mapField == null) _mapField = AccessTools.Field(geo.GetType(), "Map");
            object map = _mapField?.GetValue(geo);
            if (map == null) yield break;
            if (_allSitesProp == null) _allSitesProp = AccessTools.Property(map.GetType(), "AllSites");
            if (!(_allSitesProp?.GetValue(map, null) is IEnumerable sites)) yield break;
            foreach (var s in sites) if (s != null) yield return s;
        }

        private static object HavenComponent(object site)
        {
            try
            {
                if (_geoHavenType == null) _geoHavenType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoHaven");
                if (_geoHavenType == null || !(site is Component c)) return null;
                return c.GetComponent(_geoHavenType);
            }
            catch { return null; }
        }

        private static void EnsureFactionProps(object fac)
        {
            if (_sitesProp == null) _sitesProp = AccessTools.Property(fac.GetType(), "Sites");
            if (_vehiclesProp == null) _vehiclesProp = AccessTools.Property(fac.GetType(), "Vehicles");
        }

        // ─── item build + invoke helpers ──────────────────────────────────────

        private static readonly Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();
        private static FieldInfo CachedField(Type t, string name)
        {
            string key = t.FullName + "." + name;
            if (!_fieldCache.TryGetValue(key, out var f))
            {
                f = AccessTools.Field(t, name);
                _fieldCache[key] = f;
            }
            return f;
        }

        /// <summary>Build a native <c>List&lt;GeoItem&gt;</c> from def guids (resolve def → new GeoItem(def)).
        /// Null in → null out (SetItems leaves that slot unchanged); non-null → a (possibly empty) list SetItems
        /// clears+refills. Mirrors the game's own reconstruction (GeoCharacter.cs:1620).</summary>
        private static IList BuildItems(string[] guids)
        {
            if (guids == null) return null;
            var geoItemT = GeoItemT();
            if (geoItemT == null) return null;
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(geoItemT));
            foreach (var g in guids)
            {
                object def = DefReflection.GetDefByGuid(g);
                object item = def != null ? NewGeoItem(geoItemT, def) : null;
                if (item != null) list.Add(item);
            }
            return list;
        }

        private static object NewGeoItem(Type geoItemT, object itemDef)
        {
            foreach (var ctor in geoItemT.GetConstructors())
            {
                var pars = ctor.GetParameters();
                if (pars.Length == 0 || !pars[0].ParameterType.IsInstanceOfType(itemDef)) continue;
                var args = new object[pars.Length];
                args[0] = itemDef;
                bool ok = true;
                for (int i = 1; i < pars.Length; i++)
                {
                    if (!pars[i].HasDefaultValue) { ok = false; break; }
                    args[i] = pars[i].DefaultValue;
                }
                if (!ok) continue;
                try { return ctor.Invoke(args); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.NewGeoItem failed: " + ex.Message); return null; }
            }
            return null;
        }

        private static void InvokeSetItems(object soldier, IList armour, IList equipment, IList inventory)
        {
            try
            {
                if (_setItems == null && CharT() != null) _setItems = AccessTools.Method(CharT(), "SetItems");
                _setItems?.Invoke(soldier, new object[] { armour, equipment, inventory, true });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.InvokeSetItems failed: " + ex.Message); }
        }

        private static object DismissedReason()
        {
            try
            {
                if (_deathReasonType == null) _deathReasonType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.CharacterDeathReason");
                return _deathReasonType != null ? Enum.Parse(_deathReasonType, "Dismissed") : null;
            }
            catch { return null; }
        }

        private static void LogUnresolved(string op, long id)
            => Debug.Log("[Multiplayer] PersonnelEditReflection." + op + ": target " + id + " unresolved on host — intent skipped");
    }
}
