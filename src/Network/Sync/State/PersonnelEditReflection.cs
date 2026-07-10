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
    ///   • equip → <c>GeoCharacter.SetItems(armour, equipment, inventory, freeReload)</c>
    ///     (GeoCharacter.cs:831 — a null list is left unchanged) + authoritative storage-delta reconcile;
    ///   • augment → the FULL native <c>OnAugmentApplied</c> commit chain, headless (gates + CanSwapItem +
    ///     SetItems + displaced-to-storage + Wallet.Take + statistics + SaveLoadout + TFTV parity — see
    ///     <see cref="Augment"/>);
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
        // Native stat-edit FRAME (the value ChangeCharacterStat/SetStatButtonInteractabilty price+cap against —
        // effective displayed stat, NOT the raw GetBaseStat allocation): (int)(GetProgressionBaseStats().<attr>
        // + Bonus<attr>), RefreshStats:516-518 + GeoCharacter.cs:1167.
        private static MethodInfo _getProgressionBaseStats; // GeoCharacter.GetProgressionBaseStats() → BaseCharacterStats
        private static FieldInfo _bcsEndurance;         // BaseCharacterStats.Endurance (Strength frame)
        private static FieldInfo _bcsWillpower;         // BaseCharacterStats.Willpower
        private static FieldInfo _bcsSpeed;             // BaseCharacterStats.Speed
        private static PropertyInfo _bonusStrength;     // GeoCharacter.BonusStrength (float)
        private static PropertyInfo _bonusWillpower;    // GeoCharacter.BonusWillpower (float)
        private static PropertyInfo _bonusSpeed;        // GeoCharacter.BonusSpeed (float)
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
        // Augment intent (native OnAugmentApplied chain, headless) — decompile-verified 2026-07-09:
        // CommonCharacterUtils.cs:98/120, GeoCharacter.cs:302/831/1388/1476, ItemDef.cs:40/89,
        // TacticalItemDef.cs:41, GeoFaction.cs:75/155, GeoPhoenixFaction.cs:1233, Wallet.cs:40/63,
        // PhoenixStatisticsManager.cs:407/417, SharedGameTagsDataDef.cs:53/59, AddonDef.cs:24/69.
        private static Type _itemDefType;             // PhoenixPoint.Common.Entities.Items.ItemDef
        private static Type _tacItemDefType;          // PhoenixPoint.Tactical.Entities.Equipments.TacticalItemDef
        private static Type _operationReasonType;     // PhoenixPoint.Common.Core.OperationReason
        private static Type _statsManagerType;        // PhoenixPoint.Common.Core.PhoenixStatisticsManager
        private static FieldInfo _isPermanentAugmentField; // TacticalItemDef.IsPermanentAugment (public bool, hides base)
        private static FieldInfo _anuMutationTagField;     // SharedGameTagsDataDef.AnuMutationTag (GameTagDef)
        private static FieldInfo _bionicalTagField;        // SharedGameTagsDataDef.BionicalTag (GameTagDef)
        private static FieldInfo _handsToUseField;          // ItemDef.HandsToUse (public int)
        private static PropertyInfo _templateDefProp;       // GeoCharacter.TemplateDef (TacCharacterDef)
        private static PropertyInfo _manufacturePriceProp;  // ItemDef.ManufacturePrice (ResourcePack, lazy)
        private static PropertyInfo _unlockedAugsProp;      // GeoFaction.UnlockedAugmentations (HashSet<ItemDef>)
        private static MethodInfo _getAddonsMangerDef;      // TacCharacterDef.GetAddonsMangerDef() (game's typo)
        private static MethodInfo _canSwapItem;             // CommonCharacterUtils.CanSwapItem (static)
        private static MethodInfo _loseHandOnEquip;         // CommonCharacterUtils.LoseHandOnEquip (static)
        private static MethodInfo _walletHasResources;      // Wallet.HasResources(ResourcePack)
        private static MethodInfo _walletTake;              // Wallet.Take(ResourcePack, OperationReason)
        private static MethodInfo _updatePreferredLoadout;  // GeoPhoenixFaction.UpdatePreferredLoadout(GeoCharacter)
        private static MethodInfo _saveLoadout;             // GeoCharacter.SaveLoadout()
        private static MethodInfo _getEquippedItemHealth;   // GeoCharacter.GetEquippedItemHealth(GeoItem)
        private static MethodInfo _repairItem;              // GeoCharacter.RepairItem(GeoItem, bool)
        private static MethodInfo _onApplyMutation;         // PhoenixStatisticsManager.OnApplyMutation(ItemDef)
        private static MethodInfo _onApplyBionic;           // PhoenixStatisticsManager.OnApplyBionic(ItemDef)
        private static MethodInfo _statsComponent;          // GameUtl.GameComponent<PhoenixStatisticsManager>() (closed)

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

        // ─── HOST-side apply: augment (native OnAugmentApplied chain, headless) ─

        private const int MaxAugmentations = 2;   // UIModuleMutate/UIModuleBionics MAX_AUGMENTATIONS

        /// <summary>Augment = the FULL native commit chain, headless — the faithful replica of
        /// <c>UIModuleMutate/UIModuleBionics.OnAugmentApplied</c> + the <c>UIStateMutate/UIStateBionics</c>
        /// AugmentApplied handlers, which the relayed intent bypasses (decompile-verified 2026-07-09:
        /// UIModuleMutate.cs:160/199, UIModuleBionics.cs:174/213, UIStateMutate.cs:144, UIStateBionics.cs:144):
        ///   1. native gates — family tag (AnuMutationTag/BionicalTag), faction UnlockedAugmentations,
        ///      other-family slot lock + the 2-augment limit (InitCharacterInfo slot states + the
        ///      ApplyMutation guard), affordability via <c>Wallet.HasResources(ManufacturePrice)</c> (the
        ///      pack aggregates all six cost fields INCLUDING Mutagen, ItemDef.cs:89 — one native check
        ///      covers CanAffordMutation's mutagen gate and UIModuleBionics.CanAugment's wallet gate);
        ///   2. new bodypart list from the game's OWN swap resolver (<c>CommonCharacterUtils.CanSwapItem</c>
        ///      — the exact OnAugmentClicked call) keeping the LIVE displaced-complement GeoItems + a fresh
        ///      <c>new GeoItem(augment)</c>;
        ///   3. <c>GeoCharacter.SetItems</c> (freeReload:false like the native calls; on
        ///      <c>LoseHandOnEquip</c> also replaces equipment with the 1-handed-only survivors);
        ///   4. displaced non-permanent-augment bodyparts → faction ItemStorage (bionics also free-repair a
        ///      damaged displaced part, UIModuleBionics.cs:222-227); dropped 2-handed equipment → storage
        ///      unless family-tagged;
        ///   5. <c>Wallet.Take(ManufacturePrice, Purchase)</c>, <c>UpdatePreferredLoadout</c>,
        ///      <c>PhoenixStatisticsManager.OnApplyMutation/OnApplyBionic</c>, <c>SaveLoadout</c>;
        ///   6. TFTV parity via <see cref="TftvAugmentCompat"/> (its augment side-effects ride UI postfixes
        ///      this headless path never trips; no-op without TFTV).
        /// A failed gate logs an intent-DENIED + no-ops (host stays authoritative; the client mirror simply
        /// never changes). The result mirrors back on the #9 blob (_armourItems/_equipmentItems are
        /// [SerializeMember]) + the 0xA0 wallet snapshot.</summary>
        public static void Augment(GeoRuntime rt, long unitId, string augmentGuid)
        {
            try
            {
                var soldier = ResolveSoldierById(rt, unitId);
                if (soldier == null) { LogUnresolved("Augment", unitId); return; }
                object augment = DefReflection.GetDefByGuid(augmentGuid);
                var fac = rt?.PhoenixFaction();
                if (augment == null || fac == null)
                { Deny(unitId, augment == null ? "augment def " + augmentGuid + " unresolved" : "faction unresolved"); return; }
                EnsureAugmentMembers(soldier, fac);

                // Family tag (mutation vs bionic) — drives the slot-lock/limit gates, the storage tag
                // exclusion, the statistics hook and the TFTV side-effects.
                object sharedTags = SharedGameTags(rt);
                if (_anuMutationTagField == null && sharedTags != null) _anuMutationTagField = AccessTools.Field(sharedTags.GetType(), "AnuMutationTag");
                if (_bionicalTagField == null && sharedTags != null) _bionicalTagField = AccessTools.Field(sharedTags.GetType(), "BionicalTag");
                object mutationTag = sharedTags != null ? _anuMutationTagField?.GetValue(sharedTags) : null;
                object bionicTag = sharedTags != null ? _bionicalTagField?.GetValue(sharedTags) : null;
                bool isMutation = HasTag(augment, mutationTag);
                bool isBionic = !isMutation && HasTag(augment, bionicTag);
                if (!isMutation && !isBionic) { Deny(unitId, "def carries no mutation/bionic family tag"); return; }
                object familyTag = isMutation ? mutationTag : bionicTag;
                object otherTag = isMutation ? bionicTag : mutationTag;

                // Native gate — CanApplyAugumentation: the augment must be unlocked for the faction.
                if (!UnlockedAugmentationsContains(fac, augment)) { Deny(unitId, "augment not unlocked"); return; }

                // Native gate — slot lock + 2-augment limit (InitCharacterInfo slot states + the
                // ApplyMutation "SlotState == Available || MutationUsed != null" guard): the other family
                // at the slot blocks it; same family at the slot allows replacement past the limit.
                object slot = FirstRequiredSlot(augment);
                object atSlot = AugmentDefAtSlot(soldier, slot);
                if (atSlot != null && HasTag(atSlot, otherTag)) { Deny(unitId, "slot blocked by other-family augment"); return; }
                if (ReferenceEquals(atSlot, augment)) { Deny(unitId, "augment already applied at slot"); return; }
                bool sameFamilyAtSlot = atSlot != null && HasTag(atSlot, familyTag);
                if (!sameFamilyAtSlot && CountAugments(soldier, mutationTag, bionicTag) >= MaxAugmentations)
                { Deny(unitId, "augmentation limit reached"); return; }

                // Native gate — affordability (see the summary: one HasResources covers both screens).
                object wallet = rt?.Wallet();
                object price = _manufacturePriceProp?.GetValue(augment, null);
                if (wallet == null || price == null) { LogUnresolved("Augment(wallet)", unitId); return; }
                if (_walletHasResources == null) _walletHasResources = AccessTools.Method(wallet.GetType(), "HasResources", new[] { _manufacturePriceProp.PropertyType });
                if (_walletTake == null && _operationReasonType != null) _walletTake = AccessTools.Method(wallet.GetType(), "Take", new[] { _manufacturePriceProp.PropertyType, _operationReasonType });
                if (_walletHasResources == null || !(bool)_walletHasResources.Invoke(wallet, new[] { price }))
                { Deny(unitId, "cannot afford ManufacturePrice"); return; }

                // Native swap resolve — the exact OnAugmentClicked call: which current bodyparts must leave
                // for the augment to attach. Null = incompatible with the character's addon slots.
                var armourItems = CachedField(soldier.GetType(), "_armourItems")?.GetValue(soldier) as IList;
                object addonsMgr = _getAddonsMangerDef?.Invoke(_templateDefProp?.GetValue(soldier, null), null);
                var oldDefs = NewTypedList(_itemDefType);
                if (armourItems == null || addonsMgr == null || oldDefs == null || _canSwapItem == null)
                { LogUnresolved("Augment(swap-members)", unitId); return; }
                foreach (var it in armourItems)
                { object d = _itemDefProp?.GetValue(it, null); if (d != null) oldDefs.Add(d); }
                var toRemove = _canSwapItem.Invoke(null, new object[] { addonsMgr, augment, oldDefs, null, soldier, false }) as IList;
                if (toRemove == null) { Deny(unitId, "augment incompatible with current bodyparts (CanSwapItem)"); return; }

                // New bodypart list: keep the LIVE GeoItem instances that stay (they carry item state,
                // exactly like the native CharacterCurrentItems rebuild) + a fresh GeoItem for the augment.
                var newArmour = NewGeoItemList();
                var displaced = new List<object>();
                foreach (var it in armourItems)
                {
                    object d = _itemDefProp?.GetValue(it, null);
                    if (d != null && toRemove.Contains(d)) displaced.Add(it); else newArmour.Add(it);
                }
                object augmentItem = NewGeoItem(GeoItemT(), augment);
                if (newArmour == null || augmentItem == null) { LogUnresolved("Augment(item)", unitId); return; }
                newArmour.Add(augmentItem);

                // Hand-loss (native OnAugmentApplied step 2): an augment carrying an UnusableHand status
                // unequips every >1-handed equipment item; the 1-handed survivors are re-set.
                bool loseHand = _loseHandOnEquip != null && (bool)_loseHandOnEquip.Invoke(null, new[] { augment });
                IList keepEquipment = null;
                var dropped2H = new List<object>();
                if (loseHand)
                {
                    keepEquipment = NewGeoItemList();
                    if (CachedField(soldier.GetType(), "_equipmentItems")?.GetValue(soldier) is IList equipmentItems && keepEquipment != null)
                        foreach (var it in equipmentItems)
                        {
                            object d = _itemDefProp?.GetValue(it, null);
                            int hands = d != null && _handsToUseField != null ? (int)_handsToUseField.GetValue(d) : 1;
                            if (hands > 1) dropped2H.Add(it); else keepEquipment.Add(it);
                        }
                }

                // Wallet — native Wallet.Take(ManufacturePrice, OperationReason.Purchase). Native order is
                // SetItems-then-Take, but affordability is already gated above (HasResources) and every
                // Deny/return is behind us, so charging BEFORE the commit is value-identical on the happy
                // path — and a throw in any post-commit step can no longer leave the augment applied unpaid.
                if (_walletTake != null && _operationReasonType != null)
                    _walletTake.Invoke(wallet, new[] { price, Enum.Parse(_operationReasonType, "Purchase") });

                // Commit — the native SetItems (freeReload:false, matching OnAugmentClicked/OnAugmentApplied).
                InvokeSetItems(soldier, newArmour, keepEquipment, null, false);

                // Post-commit steps: each group isolated so one failure logs loudly and never silently
                // skips the rest (augment + charge are already committed; the #9 blob mirrors the
                // authoritative result regardless).
                try
                {
                    // Displaced bodyparts → faction storage unless permanent augment (OnAugmentApplied step 1);
                    // bionics additionally free-repair a damaged displaced part (UIModuleBionics.cs:222-227).
                    object store = ItemStorageReflection.GetStorage(rt);
                    if (_storeAddItem == null && store != null) _storeAddItem = AccessTools.Method(store.GetType(), "AddItem");
                    foreach (var it in displaced)
                    {
                        if (store != null && !IsPermanentAugment(_itemDefProp?.GetValue(it, null)))
                            _storeAddItem?.Invoke(store, new[] { it });
                        if (isBionic && _getEquippedItemHealth != null && _repairItem != null
                            && (float)_getEquippedItemHealth.Invoke(soldier, new[] { it }) < 1f)
                            _repairItem.Invoke(soldier, new object[] { it, false });
                    }
                    // Dropped 2-handed equipment → storage unless family-tagged (the native tag exclusion).
                    foreach (var it in dropped2H)
                    {
                        if (store != null && !HasTag(_itemDefProp?.GetValue(it, null), familyTag))
                            _storeAddItem?.Invoke(store, new[] { it });
                    }
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.Augment: displaced-to-storage failed — " + ex.Message); }

                try
                {
                    // Preferred loadout + statistics + loadout save (OnAugmentApplied step 5 + UIState handler).
                    _updatePreferredLoadout?.Invoke(fac, new[] { soldier });
                    object statsMgr = _statsComponent?.Invoke(null, null);
                    if (statsMgr != null) (isBionic ? _onApplyBionic : _onApplyMutation)?.Invoke(statsMgr, new[] { augment });
                    _saveLoadout?.Invoke(soldier, null);
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.Augment: loadout/statistics failed — " + ex.Message); }

                // TFTV parity — its augment side-effects ride UI postfixes this headless path never trips
                // (already failure-isolated per effect inside).
                TftvAugmentCompat.OnHostAugmentApplied(rt, soldier, augment, isBionic);

                Debug.Log("[Multiplayer] PersonnelEditReflection.Augment: unit " + unitId + " applied "
                          + (isBionic ? "bionic " : "mutation ") + augmentGuid
                          + (displaced.Count > 0 ? " (displaced " + displaced.Count + ")" : "")
                          + (loseHand ? " (hand-loss: dropped " + dropped2H.Count + " two-handed)" : ""));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.Augment failed: " + ex.Message); }
        }

        private static void Deny(long unitId, string reason)
            => Debug.Log("[Multiplayer] PersonnelEditReflection.Augment: unit " + unitId + " intent DENIED — " + reason);

        /// <summary>Bind the augment-chain members once (types by full name, members single-overload except
        /// where the exact param list is passed — the AccessTools exact-match trap).</summary>
        private static void EnsureAugmentMembers(object soldier, object fac)
        {
            if (_itemDefType == null) _itemDefType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemDef");
            if (_tacItemDefType == null) _tacItemDefType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.TacticalItemDef");
            if (_operationReasonType == null) _operationReasonType = AccessTools.TypeByName("PhoenixPoint.Common.Core.OperationReason");
            if (_statsManagerType == null) _statsManagerType = AccessTools.TypeByName("PhoenixPoint.Common.Core.PhoenixStatisticsManager");
            if (_itemDefProp == null && GeoItemT() != null) _itemDefProp = AccessTools.Property(GeoItemT(), "ItemDef");
            if (_isPermanentAugmentField == null && _tacItemDefType != null) _isPermanentAugmentField = AccessTools.Field(_tacItemDefType, "IsPermanentAugment");
            if (_handsToUseField == null && _itemDefType != null) _handsToUseField = AccessTools.Field(_itemDefType, "HandsToUse");
            if (_manufacturePriceProp == null && _itemDefType != null) _manufacturePriceProp = AccessTools.Property(_itemDefType, "ManufacturePrice");
            if (_templateDefProp == null) _templateDefProp = AccessTools.Property(soldier.GetType(), "TemplateDef");
            if (_getAddonsMangerDef == null && _templateDefProp != null) _getAddonsMangerDef = AccessTools.Method(_templateDefProp.PropertyType, "GetAddonsMangerDef");
            var utilsT = AccessTools.TypeByName("PhoenixPoint.Common.Utils.CommonCharacterUtils");
            if (_canSwapItem == null && utilsT != null) _canSwapItem = AccessTools.Method(utilsT, "CanSwapItem");
            if (_loseHandOnEquip == null && utilsT != null) _loseHandOnEquip = AccessTools.Method(utilsT, "LoseHandOnEquip");
            if (_updatePreferredLoadout == null) _updatePreferredLoadout = AccessTools.Method(fac.GetType(), "UpdatePreferredLoadout");
            var charT = CharT();
            if (_saveLoadout == null && charT != null) _saveLoadout = AccessTools.Method(charT, "SaveLoadout");
            if (_getEquippedItemHealth == null && charT != null && GeoItemT() != null) _getEquippedItemHealth = AccessTools.Method(charT, "GetEquippedItemHealth", new[] { GeoItemT() });
            if (_repairItem == null && charT != null && GeoItemT() != null) _repairItem = AccessTools.Method(charT, "RepairItem", new[] { GeoItemT(), typeof(bool) });
            if (_onApplyMutation == null && _statsManagerType != null) _onApplyMutation = AccessTools.Method(_statsManagerType, "OnApplyMutation");
            if (_onApplyBionic == null && _statsManagerType != null) _onApplyBionic = AccessTools.Method(_statsManagerType, "OnApplyBionic");
            if (_statsComponent == null && _statsManagerType != null)
            {
                var gameUtl = AccessTools.TypeByName("Base.Core.GameUtl") ?? AccessTools.TypeByName("GameUtl");
                var generic = gameUtl != null ? AccessTools.Method(gameUtl, "GameComponent", new Type[0]) : null;
                if (generic != null && generic.IsGenericMethodDefinition) _statsComponent = generic.MakeGenericMethod(_statsManagerType);
            }
        }

        /// <summary>GeoLevelController.SharedData.SharedGameTags (SharedGameTagsDataDef), or null. Shares the
        /// cached property/field handles with <see cref="HasPandoranProgression"/>.</summary>
        private static object SharedGameTags(GeoRuntime rt)
        {
            try
            {
                var geo = rt?.GeoLevel();
                if (geo == null) return null;
                if (_sharedDataProp == null) _sharedDataProp = AccessTools.Property(geo.GetType(), "SharedData");
                object shared = _sharedDataProp?.GetValue(geo, null);
                if (shared == null) return null;
                if (_sharedGameTagsField == null) _sharedGameTagsField = AccessTools.Field(shared.GetType(), "SharedGameTags");
                return _sharedGameTagsField?.GetValue(shared);
            }
            catch { return null; }
        }

        /// <summary>The def's <c>Tags</c> list (public GameTagsList field, AddonDef.cs:83) contains this tag
        /// def (defs are singletons → reference compare). Null-safe false.</summary>
        private static bool HasTag(object def, object tag)
        {
            try
            {
                if (def == null || tag == null) return false;
                if (!(CachedField(def.GetType(), "Tags")?.GetValue(def) is IEnumerable tags)) return false;
                foreach (var t in tags) if (ReferenceEquals(t, tag)) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>Native <c>AugmentScreenUtilities.IsPermanentAugment</c>: TacticalItemDef.IsPermanentAugment
        /// (the derived `new` field, TacticalItemDef.cs:41); a non-tactical def is never permanent.</summary>
        private static bool IsPermanentAugment(object def)
        {
            try
            {
                if (def == null || _tacItemDefType == null || !_tacItemDefType.IsInstanceOfType(def)) return false;
                return _isPermanentAugmentField != null && (bool)_isPermanentAugmentField.GetValue(def);
            }
            catch { return false; }
        }

        /// <summary>ItemDef.RequiredSlotBinds[0].RequiredSlot (AddonSlotDef) — the augment's target body slot
        /// (the same [0] the native section mapping and TFTV use; augments bind exactly one slot), or null.</summary>
        private static object FirstRequiredSlot(object def)
        {
            try
            {
                if (!(CachedField(def.GetType(), "RequiredSlotBinds")?.GetValue(def) is Array binds) || binds.Length == 0) return null;
                object bind = binds.GetValue(0);
                return bind == null ? null : CachedField(bind.GetType(), "RequiredSlot")?.GetValue(bind);
            }
            catch { return null; }
        }

        /// <summary>Native <c>AugmentScreenUtilities.GetAugmentAtSlot</c>: the ItemDef of the armour item
        /// whose RequiredSlotBinds bind this AddonSlotDef, or null.</summary>
        private static object AugmentDefAtSlot(object soldier, object slot)
        {
            try
            {
                if (slot == null) return null;
                if (!(CachedField(soldier.GetType(), "_armourItems")?.GetValue(soldier) is IList items)) return null;
                foreach (var it in items)
                {
                    object d = _itemDefProp?.GetValue(it, null);
                    if (d == null || !(CachedField(d.GetType(), "RequiredSlotBinds")?.GetValue(d) is Array binds)) continue;
                    foreach (var bind in binds)
                        if (bind != null && ReferenceEquals(CachedField(bind.GetType(), "RequiredSlot")?.GetValue(bind), slot))
                            return d;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Native <c>AugmentScreenUtilities.GetNumberOfAugments</c>: armour items tagged with either
        /// augment family tag.</summary>
        private static int CountAugments(object soldier, object mutationTag, object bionicTag)
        {
            int n = 0;
            try
            {
                if (!(CachedField(soldier.GetType(), "_armourItems")?.GetValue(soldier) is IList items)) return 0;
                foreach (var it in items)
                {
                    object d = _itemDefProp?.GetValue(it, null);
                    if (HasTag(d, mutationTag) || HasTag(d, bionicTag)) n++;
                }
            }
            catch { }
            return n;
        }

        /// <summary>Native CanApplyAugumentation's unlock gate: <c>GeoFaction.UnlockedAugmentations</c>
        /// (HashSet&lt;ItemDef&gt;) contains this def (reference compare — defs are singletons).</summary>
        private static bool UnlockedAugmentationsContains(object fac, object def)
        {
            try
            {
                if (_unlockedAugsProp == null) _unlockedAugsProp = AccessTools.Property(fac.GetType(), "UnlockedAugmentations");
                if (!(_unlockedAugsProp?.GetValue(fac, null) is IEnumerable set)) return false;
                foreach (var d in set) if (ReferenceEquals(d, def)) return true;
                return false;
            }
            catch { return false; }
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
                StatRefundTracker.ResetUnit(unitId);   // a reused GeoUnitId must not inherit this soldier's stat-refund net
                StatEditAffordance.ResetUnit(unitId);  // ...nor the local minus-button optimistic click counter
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

        /// <summary>Apply ONE base-stat click authoritatively at the model (thin-client: the +/- button is a pure
        /// input, the host computes everything). <paramref name="delta"/> is SIGNED — +N = spend, −N = refund —
        /// applied one point at a time the way the native button does. Pricing parity is decompile-verified: the
        /// spend spill (<see cref="ProgressionSpend.TrySplit"/> — soldier SP first, remainder into faction, gated
        /// on the combined pool) is identical to native <c>ChangeCharacterStat</c>'s increase branch
        /// (UIModuleCharacterProgression.cs:892-903) and both charge <c>GetBaseStatCost(stat, cur+1)</c>.
        ///   • SPEND (+): per point native <c>CanModifyBaseStat</c> (max cap) + re-derived cost + pool split, then
        ///     native <c>ModifyBaseStat(+1)</c>; the applied count is recorded in <see cref="StatRefundTracker"/>
        ///     so a later refund is ledger-bounded. A mid-loop shortfall keeps the points already applied.
        ///   • REFUND (−): bounded by the per-(unit,stat) session net-applied ledger (anti-farm — never banks free
        ///     SP nor drops below the session-start value), credited at the SYMMETRIC per-point price the spend
        ///     charged (<c>GetBaseStatCost(stat, cur)</c>, mirroring native decrement :909); credit lands on the
        ///     soldier SP pool. The result mirrors on the #9 blob (progression _baseStats + SkillPoints).</summary>
        public static void SpendStatPoints(GeoRuntime rt, long unitId, int statId, int delta)
        {
            try
            {
                if (statId < 0 || statId > 2 || delta == 0) return;   // action Validate mirrors this (signed, non-zero)
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
                if (delta > 0)
                {
                    // SPEND (plus button): each point re-priced + pool-gated on the LIVE state, stepwise as the
                    // native +1 does. A mid-loop shortfall keeps the points already applied (each was individually
                    // affordable/legal) and logs. Record the applied count so a later refund is ledger-bounded.
                    int applied = 0;
                    for (int i = 0; i < delta; i++)
                    {
                        int cur = EffectiveStat(soldier, statId);   // native frame: effective displayed stat, NOT raw _baseStats
                        if (!(bool)_canModifyBaseStat.Invoke(prog, new object[] { stat, cur + 1 })) break;   // native max cap
                        int cost = (int)_getBaseStatCost.Invoke(prog, new object[] { stat, cur + 1 });
                        if (!TrySpendSkillPoints(rt, prog, cost, "SpendStatPoints", unitId)) break;
                        _modifyBaseStat.Invoke(prog, new object[] { stat, 1 });
                        applied++;
                    }
                    StatRefundTracker.RecordSpend(unitId, statId, applied);
                    Debug.Log("[Multiplayer] PersonnelEditReflection.SpendStatPoints: unit " + unitId + " stat " + statId
                              + " +" + applied + (applied == delta ? "" : " (requested +" + delta + " — cap/pool stop)"));
                }
                else
                {
                    // REFUND (minus button): bounded by the per-session net-applied ledger so a refund never banks
                    // free SP nor drops the stat below its session-start value (anti-farm). Symmetric price — credit
                    // the SAME per-point cost the spend charged (native decrement refunds GetBaseStatCost(stat, cur),
                    // UIModuleCharacterProgression.cs:909). Credit lands on the soldier SkillPoints pool.
                    int cap = StatRefundTracker.RefundableCap(unitId, statId);
                    int want = StatRefundTracker.ClampRefund(cap, -delta);
                    if (want <= 0)
                    { Debug.Log("[Multiplayer] PersonnelEditReflection.SpendStatPoints: unit " + unitId + " stat " + statId + " refund DENIED (requested -" + (-delta) + ", session-cap " + cap + ") — no state change"); return; }
                    int refunded = 0;
                    for (int i = 0; i < want; i++)
                    {
                        int cur = EffectiveStat(soldier, statId);   // native frame: effective displayed stat, NOT raw _baseStats
                        if (cur <= 0) break;                                                          // never below the floor
                        int price = (int)_getBaseStatCost.Invoke(prog, new object[] { stat, cur });   // symmetric per-point refund price
                        _modifyBaseStat.Invoke(prog, new object[] { stat, -1 });
                        RefundSkillPoints(prog, price);                                                // credit soldier SP (never negative)
                        refunded++;
                    }
                    StatRefundTracker.RecordRefund(unitId, statId, refunded);
                    Debug.Log("[Multiplayer] PersonnelEditReflection.SpendStatPoints: unit " + unitId + " stat " + statId
                              + " -" + refunded + " [refund]" + (refunded == -delta ? "" : " (requested -" + (-delta) + ", session-cap " + cap + ")"));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.SpendStatPoints failed: " + ex.Message); }
        }

        /// <summary>Credit <paramref name="price"/> SP back to the soldier's own <c>CharacterProgression.SkillPoints</c>
        /// (refund lands on the per-soldier pool; the ledger already bounds the count so this can never bank free SP).
        /// ponytail: soldier-pool credit, not native's faction-first reverse-spill (:915) — the anti-farm ledger caps
        /// the refund count so at worst a spend-from-faction then refund shifts that SP into the soldier's private pool;
        /// upgrade to reverse-spill only if that shared→private drift is ever observed to matter.</summary>
        private static void RefundSkillPoints(object prog, int price)
        {
            if (price <= 0) return;
            if (_skillPointsField == null) _skillPointsField = AccessTools.Field(prog.GetType(), "SkillPoints");
            if (_skillPointsField == null) return;
            int sp = (int)_skillPointsField.GetValue(prog);
            _skillPointsField.SetValue(prog, sp + price);
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

        /// <summary>The soldier's EFFECTIVE base stat for a CharacterBaseAttribute (0=Str,1=Will,2=Speed) — the
        /// value the NATIVE stat editor gates + prices against (UIModuleCharacterProgression.RefreshStats:516-518:
        /// <c>(int)(GetProgressionBaseStats().&lt;attr&gt; + Bonus&lt;attr&gt;)</c> = bodypart contributions +
        /// allocated points + item/mutation bonus, GeoCharacter.cs:1167). This is NOT
        /// <c>CharacterProgression.GetBaseStat</c> (the raw <c>_baseStats</c> allocation, which omits bodyparts +
        /// bonus and is far smaller) — pricing/capping on that undercharged (near-zero SP cost) and never reached
        /// the cap (infinite upgrade). Unresolvable → 0 (inert in a test process, like every reflection here).</summary>
        internal static int EffectiveStat(object soldier, int statId)
        {
            try
            {
                if (soldier == null || statId < 0 || statId > 2) return 0;
                EnsureEffectiveStatMembers(soldier);
                if (_getProgressionBaseStats == null) return 0;
                object bcs = _getProgressionBaseStats.Invoke(soldier, null);
                FieldInfo bf = statId == 0 ? _bcsEndurance : (statId == 1 ? _bcsWillpower : _bcsSpeed);
                PropertyInfo bp = statId == 0 ? _bonusStrength : (statId == 1 ? _bonusWillpower : _bonusSpeed);
                float baseAttr = bcs != null && bf != null ? Convert.ToSingle(bf.GetValue(bcs)) : 0f;
                float bonus = bp != null ? Convert.ToSingle(bp.GetValue(soldier, null)) : 0f;
                return StatEditGate.EffectiveStat(baseAttr, bonus);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditReflection.EffectiveStat failed: " + ex.Message); return 0; }
        }

        /// <summary>Bind the effective-stat frame members once: GeoCharacter.GetProgressionBaseStats() + the
        /// returned BaseCharacterStats value fields + the GeoCharacter.Bonus* float properties.</summary>
        private static void EnsureEffectiveStatMembers(object soldier)
        {
            var st = soldier.GetType();
            if (_getProgressionBaseStats == null) _getProgressionBaseStats = AccessTools.Method(st, "GetProgressionBaseStats");
            if (_bonusStrength == null) _bonusStrength = AccessTools.Property(st, "BonusStrength");
            if (_bonusWillpower == null) _bonusWillpower = AccessTools.Property(st, "BonusWillpower");
            if (_bonusSpeed == null) _bonusSpeed = AccessTools.Property(st, "BonusSpeed");
            if (_bcsEndurance == null && _getProgressionBaseStats != null)
            {
                var bcsT = _getProgressionBaseStats.ReturnType;
                _bcsEndurance = AccessTools.Field(bcsT, "Endurance");
                _bcsWillpower = AccessTools.Field(bcsT, "Willpower");
                _bcsSpeed = AccessTools.Field(bcsT, "Speed");
            }
        }

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

        /// <summary>A fresh native <c>List&lt;T&gt;</c> for a game element type, or null.</summary>
        private static IList NewTypedList(Type elementType)
            => elementType == null ? null : (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));

        private static IList NewGeoItemList() => NewTypedList(GeoItemT());

        /// <summary>Build a native <c>List&lt;GeoItem&gt;</c> from def guids (resolve def → new GeoItem(def)).
        /// Null in → null out (SetItems leaves that slot unchanged); non-null → a (possibly empty) list SetItems
        /// clears+refills. Mirrors the game's own reconstruction (GeoCharacter.cs:1620).</summary>
        private static IList BuildItems(string[] guids)
        {
            if (guids == null) return null;
            var list = NewGeoItemList();
            if (list == null) return null;
            foreach (var g in guids)
            {
                object def = DefReflection.GetDefByGuid(g);
                object item = def != null ? NewGeoItem(GeoItemT(), def) : null;
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

        // freeReload:true for equip (a def-rebuilt item carries no ammo state to preserve); the augment
        // chain passes false, matching the native OnAugmentClicked/OnAugmentApplied SetItems calls.
        private static void InvokeSetItems(object soldier, IList armour, IList equipment, IList inventory, bool freeReload = true)
        {
            try
            {
                if (_setItems == null && CharT() != null) _setItems = AccessTools.Method(CharT(), "SetItems");
                _setItems?.Invoke(soldier, new object[] { armour, equipment, inventory, freeReload });
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
