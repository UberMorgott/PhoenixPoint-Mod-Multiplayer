using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Common.View.ViewControllers.Inventory;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View.ViewControllers.AugmentationScreen;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Soldier-loadout intent seam, GESTURE level (law 4a) — the client half of equip. Without it a client's
    /// inventory edits are purely local: the rail is host→client only, so a client drag mirrors NOWHERE.
    ///
    /// SEAM CHOICE: the model chokepoint (GeoCharacter.SetItems) alone is not one — UIStateEditSoldier
    /// .UpdateState re-flushes SetItems EVERY FRAME with identical content, and a pure slot REPOSITION never
    /// changes the model at all (GeoItem has no position field; UIInventoryList packs first-fit from list
    /// order). So the UI gestures MARK (AttemptSlotSwap / side-button / reload / loadout load-unload /
    /// scrap-off-doll) and the next native SetItems flush turns one mark into exactly ONE intent. The mark is
    /// a bool; nothing is ever encoded per frame.
    ///
    /// HOST: dedup (IntentRail, shared 0xAE surface) → resolve the character by the rail's stable key
    /// (IdentityResolver "U#&lt;charId&gt;") → match every wire slot against the character's OWN live GeoItems
    /// → PopItem from storage only for genuine GAINS, AddItem back whatever the client dropped → native
    /// GeoCharacter.SetItems. The result reaches every peer through the normal rail (GeoCharacter EntityList
    /// fields + storage GeoItemDict on DiffEngine 0xAC) — no bespoke echo.
    /// </summary>
    public static class EquipSync
    {
        // ─── EVERY PEER: one pending flag, set per LOCAL user gesture ──────
        private static bool _gesturePending;
        private static string _gestureSource; // for the per-gesture [MP][equip] log line
        private static int _gestureCharId = -1; // char the gesture belongs to (-1 = unknown → any flush)

        // ─── HOST state: last committed loadout per character (the revert guard, see CheckLastApplyStuck) ─
        private static readonly Dictionary<int, byte[]> _appliedBefore = new Dictionary<int, byte[]>();
        private static readonly Dictionary<int, byte[]> _appliedAfter = new Dictionary<int, byte[]>();

        /// <summary>Hard cap on a wire list length — the only untrusted count in this payload, and the one
        /// value a malformed/hostile peer could turn into an allocation. Loadout lists are single digits.</summary>
        private const int MaxSlotsPerList = 64;

        public static void Reset() => ResetForReloadBoundary();

        public static void ResetForReloadBoundary()
        {
            _gesturePending = false;
            _gestureSource = null;
            _gestureCharId = -1;
            _appliedBefore.Clear();
            _appliedAfter.Clear();
        }

        private static GeoLevelController GeoLevel()
        {
            var level = Base.Core.GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        // ─── EVERY PEER: gesture seams (law 4a) — mark, then send on the next native flush ─

        /// <summary>A user equip gesture completed on THIS peer. The native flush that follows (the equip
        /// screens flush per-frame, so within a frame) is the gesture's commit, and on a CLIENT it carries the
        /// committed result into <see cref="SetItemsGestureSendPatch"/>, which turns it into ONE intent.
        /// Marking instead of sending here keeps one mechanism for BOTH equip screens (UIStateEditVehicle maps
        /// its lists into SetItems differently) and reads the exact model-committed lists, not a hand-rebuilt
        /// copy. On the HOST the mark only arms the self-check — the flush IS the authoritative write and the
        /// result rides the normal 0xAC diff.</summary>
        internal static void MarkGesture(string source)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return; // solo: nothing to mark
            if (SyncApplyScope.Active) return; // apply-driven UI churn is not a user gesture (law 8)
            var geo = GeoLevel();
            if (geo == null) return;           // tactical UIStateInventory shares these widgets — geoscape only
            // Bind the mark to the soldier being edited: SetItems flushes for EVERY character pass the
            // send patch, and an unbound mark was consumed by the NEXT flush of ANY of them — a fast
            // soldier-switch handed the intent to the wrong one. Both edit screens drive the same
            // ActorCycle module (UIStateEditSoldier/-Vehicle → _geoscapeModules.ActorCycleModule), so
            // its CurrentCharacter IS the gesture's soldier.
            var cur = geo.View?.GeoscapeModules?.ActorCycleModule?.CurrentCharacter;
            _gestureCharId = cur == null ? -1 : (int)cur.Id;
            _gesturePending = true;
            _gestureSource = source;
        }

        /// <summary>Every drag-drop / click-move / double-click equip-unequip funnels through
        /// <c>UIModuleSoldierEquip.AttemptSlotSwap</c> (:1128); __result true = the swap happened.</summary>
        [HarmonyPatch(typeof(UIModuleSoldierEquip), "AttemptSlotSwap")]
        internal static class SlotSwapGesturePatch
        {
            private static void Postfix(bool __result) { if (__result) MarkGesture("slot-swap"); }
        }

        /// <summary>Side-button quick-add / ammo-load (<c>UIInventorySlotSideButton.OnSideButtonPressed</c>
        /// :303) bypasses AttemptSlotSwap — it moves items between lists directly. (The BUY branch of the same
        /// button is blocked client-side by ManufactureSync.QuickProduceCapturePatch; a mark left over from a
        /// blocked buy costs one no-op intent, which the host resolves to zero changes.)</summary>
        [HarmonyPatch(typeof(UIInventorySlotSideButton), "OnSideButtonPressed")]
        internal static class SideButtonGesturePatch
        {
            private static void Postfix() => MarkGesture("side-button");
        }

        /// <summary>Reload button (<c>UIModuleSoldierEquip.ReloadItemHandler</c> — TryLoadAmmo changes ammo
        /// bytes on equipped items without any slot move).</summary>
        [HarmonyPatch(typeof(UIModuleSoldierEquip), "ReloadItemHandler")]
        internal static class ReloadGesturePatch
        {
            private static void Postfix() => MarkGesture("reload");
        }

        /// <summary>Loadout-preset load (both button routes call LoadLoadout(GeoCharacter)), unequip-all
        /// (UnloadLoadout() button entry), and scrap-off-the-doll (ItemScrappedHandler removes the item from
        /// the soldier/vehicle lists; its wallet+storage half already rides OpScrap via ScrapItemCapturePatch).
        /// PREFIX, not postfix: these four run their OWN flushes INLINE (UIStateEditSoldier:640 equip /:644
        /// storage, UIStateEditVehicle:191/:196), so a mark placed after the body arrives too late to belong to
        /// the gesture's own commit.</summary>
        [HarmonyPatch]
        internal static class LoadoutButtonsGesturePatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(UIStateEditSoldier), "LoadLoadout", new[] { typeof(GeoCharacter) });
                yield return AccessTools.Method(typeof(UIStateEditSoldier), "UnloadLoadout", Type.EmptyTypes);
                yield return AccessTools.Method(typeof(UIStateEditSoldier), "ItemScrappedHandler");
                yield return AccessTools.Method(typeof(UIStateEditVehicle), "ItemScrappedHandler");
            }

            private static void Prefix() => MarkGesture("loadout-button");
        }

        /// <summary>The send point: first native SetItems flush after a marked gesture (the equip screens flush
        /// per-frame, so this fires within a frame of the gesture). Cost on every other flush = one bool check.
        /// Inside SyncApplyScope the flag survives untouched — the next natural flush outside apply sends it
        /// (law 8: nothing is emitted from apply scope).</summary>
        [HarmonyPatch(typeof(GeoCharacter), nameof(GeoCharacter.SetItems))]
        internal static class SetItemsGestureSendPatch
        {
            private static void Postfix(GeoCharacter __instance, bool freeReload)
            {
                if (!_gesturePending || SyncApplyScope.Active) return;
                if (_gestureCharId >= 0 && (int)__instance.Id != _gestureCharId) return; // another char's per-frame flush must not consume the mark
                _gesturePending = false;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return;
                SelfCheckGestureCommit();
                DiffEngine.FlushOnHostGesture(); // host loadout write must not wait out the 0.5 s poll
                if (engine.IsHost) return; // the flush IS the authoritative write; it rides the 0xAC diff
                try { SendLoadoutIntent(__instance, freeReload); }
                catch (Exception ex) { Debug.LogWarning("[MP][equip] CLIENT gesture send failed: " + ex.Message); }
            }
        }

        /// <summary>THE ROOT CAUSE of "move one of two identical items between slots and the other one
        /// vanishes for everyone but the mover" (RCA 2026-07-18). GAME bug, not ours — we only made it
        /// visible: <c>UIInventorySlot.SetItem</c> (:369-413) fires BOTH <c>OnItemChangingHandlers</c> and
        /// <c>OnItemChangedHandlers</c> for the SAME transition, and <c>UIInventoryList</c> subscribes one
        /// handler to each (:888-889) that BOTH run <c>UnfilteredItems.Remove(oldItem)</c> (:835 / :847).
        /// <c>List&lt;T&gt;.Remove</c> matches through <c>EqualityComparer&lt;ICommonItem&gt;.Default</c> →
        /// <c>GeoItem.Equals</c> (GeoItem.cs:124) = VALUE equality (def + count + charges), so with a
        /// value-duplicate in the list — two identical magazines, the everyday case — the FIRST Remove
        /// deletes the OTHER copy and the second deletes the dragged one, while only one is re-Inserted
        /// (:853-864). The list loses an element; the SLOTS still hold both, so the screen looks right.
        /// Solo hides it (nothing reads the model until the screen is re-entered). In co-op the very next
        /// flush writes that truncated list into <c>GeoCharacter.SetItems</c> and the rail faithfully mirrors
        /// the loss: host screen right, host MODEL and every client wrong.
        ///
        /// The Changing half is pure duplication — same guard (<c>_isFiltering</c>), same argument, and
        /// nothing between the two callbacks reads <c>UnfilteredItems</c>; <c>ItemChangedHandler</c> does the
        /// authoritative remove, the <c>FilteredItems</c> upkeep and the re-insert. Skipping it makes every
        /// slot transition remove exactly ONE element, which is what the Changed half already assumes.
        /// Generic by construction: one patch on the shared list widget covers every list (armour / ready /
        /// backpack / storage), both equip screens, every gesture and every peer. Inert outside a session.
        /// See <see cref="InventoryListIdentityRemovePatch"/> for the other half — both are needed.</summary>
        [HarmonyPatch(typeof(UIInventoryList), "ItemChangingHandler")]
        internal static class InventoryListDoubleRemovePatch
        {
            private static bool Prepare() => RequireInventoryListMethod("ItemChangingHandler");

            private static bool Prefix()
            {
                var engine = NetworkEngine.Instance;
                return engine == null || !engine.IsActiveSession; // solo: run the original
            }
        }

        /// <summary>THE OTHER HALF: make the surviving remove IDENTITY-based. <c>ItemChangedHandler</c>
        /// (:839-880) removes <c>oldItem</c> from <c>UnfilteredItems</c> (:847) and <c>FilteredItems</c>
        /// (:848-851) with <c>List&lt;T&gt;.Remove</c>/<c>Contains</c>, which match through
        /// <c>GeoItem.Equals</c> (def+count+charges), NOT reference. With a value-duplicate present that
        /// deletes the WRONG instance: the list keeps the moved item twice and drops the twin a slot is still
        /// displaying, so the next <c>SetItems</c> flush hands the model one GeoItem bound to two slots.
        ///
        /// Fix without reimplementing the handler: before it runs, SWAP the first value-equal element with the
        /// actual <c>oldItem</c> instance, so the very same value-based <c>Remove</c> now lands on the identity
        /// match. The two swapped elements are by definition value-equal, so list CONTENT (what the model and
        /// the canonical diff see) is untouched — only which reference survives changes, which is the point.
        /// ponytail: only this handler is realigned. UIInventoryList removes items value-based elsewhere too
        /// (RemoveItem :341/:356-370, StackItems' Except :583) — untouched because no live failure points
        /// there; if a self-check FAIL ever reports list &lt; shown, that is where to look.</summary>
        [HarmonyPatch(typeof(UIInventoryList), "ItemChangedHandler")]
        internal static class InventoryListIdentityRemovePatch
        {
            private static bool Prepare() => RequireInventoryListMethod("ItemChangedHandler");

            private static void Prefix(UIInventoryList __instance, ICommonItem oldItem)
            {
                if (oldItem == null || __instance == null || __instance.IsFiltering) return;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return; // solo: vanilla
                AlignFirstValueMatchToReference(__instance.UnfilteredItems, oldItem);
                AlignFirstValueMatchToReference(__instance.FilteredItems, oldItem);
            }
        }

        /// <summary>Make <c>list.IndexOf(target)</c> — what Remove/Contains use — resolve to the actual
        /// <paramref name="target"/> INSTANCE, by swapping it with whatever value-equal element currently
        /// sits at that index. No-op when the first match already IS the instance (the normal case) or when
        /// the instance is not in this list at all.</summary>
        private static void AlignFirstValueMatchToReference(List<ICommonItem> list, ICommonItem target)
        {
            if (list == null) return;
            int byValue = list.IndexOf(target);
            if (byValue < 0 || ReferenceEquals(list[byValue], target)) return;
            // Anything before byValue is not value-equal to target, so the instance can only be after it.
            for (int i = byValue + 1; i < list.Count; i++)
            {
                if (!ReferenceEquals(list[i], target)) continue;
                list[i] = list[byValue];
                list[byValue] = target;
                return;
            }
        }

        /// <summary>Loud bind check for the two <see cref="UIInventoryList"/> handler patches. A rename or a
        /// signature drift makes AccessTools return null SILENTLY, and attribute-based PatchAll then aborts
        /// with one generic message that takes every remaining patch down with it. Returning false here
        /// instead keeps the rest of the mod patched and says exactly what broke.</summary>
        private static bool RequireInventoryListMethod(string name)
        {
            if (AccessTools.DeclaredMethod(typeof(UIInventoryList), name) == null)
            {
                Debug.LogError("[MP][equip] PATCH BIND FAILED: UIInventoryList." + name + " did not resolve —" +
                               " value-equal inventory items WILL be corrupted on every equip gesture. Patch skipped.");
                return false;
            }
            Debug.Log("[MP][equip] patch bound: UIInventoryList." + name);
            return true;
        }

        /// <summary>THE FALSIFIER for the two patches above, at the seam no codec self-check can see: it
        /// cannot prove the list handed to the model still holds what the player is looking at. This does, on
        /// the one flush that is authoritative — the gesture commit — by asserting the invariant the
        /// double-Remove breaks: every item a slot DISPLAYS must still be present BY REFERENCE in that list's
        /// <c>UnfilteredItems</c>, because <c>UpdateSoldierEquipment</c> writes exactly that list into the
        /// model and <c>UpdateStorage</c> Except-diffs the faction <c>ItemStorage</c> against it. Keyed
        /// one-shot per list AND per duplicate-state so an early duplicate-free gesture can NOT retire the
        /// check for the case that actually breaks; failures always shout. Cost: user-gesture rate, one
        /// reference scan per list — never per tick, never per frame.</summary>
        private static readonly HashSet<string> _slotCheckSeen = new HashSet<string>(StringComparer.Ordinal);

        private static void SelfCheckGestureCommit()
        {
            var view = GeoLevel()?.View;
            if (!(view?.CurrentViewState is UIStateEditSoldier)) return; // vehicle screen uses other lists
            var equip = view.GeoscapeModules?.SoldierEquipModule;
            if (equip == null) return;
            CheckSlotsAgainstList("armour", equip.ArmorList);
            CheckSlotsAgainstList("ready", equip.ReadyList);
            CheckSlotsAgainstList("inventory", equip.InventoryList);
            CheckSlotsAgainstList("storage", equip.StorageList);
        }

        private static void CheckSlotsAgainstList(string name, UIInventoryList list)
        {
            var items = list?.UnfilteredItems;
            if (items == null || list.Slots == null) return;
            int shown = 0, missing = 0;
            foreach (var slot in list.Slots)
            {
                if (slot == null || slot.Empty) continue;
                shown++;
                bool found = false;
                foreach (var item in items) if (ReferenceEquals(item, slot.Item)) { found = true; break; }
                if (!found) missing++;
            }
            // GeoItem.Equals/GetHashCode are value-based, so Distinct() collapses exactly the duplicates
            // that arm the double-Remove — this is the "was the dangerous case actually exercised?" key.
            bool dup = items.Count != items.Distinct().Count();
            string key = name + (dup ? " dup" : " nodup");
            if (missing > 0)
                Debug.LogError("[MP][equip] SELF-CHECK FAIL " + key + ": " + missing + " of " + shown +
                               " displayed items are NOT in UnfilteredItems (list=" + items.Count + ") — the screen " +
                               "shows them but GeoCharacter.SetItems/UpdateStorage is about to drop them. " +
                               "UIInventoryList double-Remove vs GeoItem value equality is back.");
            else if (_slotCheckSeen.Add(key))
                Debug.Log("[MP][equip] self-check OK " + key + " shown=" + shown + " list=" + items.Count);
        }

        /// <summary>ONE intent per gesture: [nonce][OpSetItems][charId][body] on the shared 0xAE surface —
        /// decoded host-side by <see cref="HandleIntent"/>. Nonce/envelope/send ride <see cref="IntentRail"/>
        /// (its shared allocator replaced the old borrowed ManufactureSync.NextNonce counter).</summary>
        private static void SendLoadoutIntent(GeoCharacter character, bool freeReload)
        {
            int charId = (int)character.Id;
            var body = EncodeBody(freeReload, ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));
            IntentRail.Send(SurfaceIds.GeoManufactureIntent, ManufactureSync.OpSetItems,
                "loadout char=U#" + charId + " gesture=" + _gestureSource + " bytes=" + body.Length +
                " freeReload=" + freeReload,
                w => { w.Write(charId); w.Write(body); });
        }

        /// <summary>Equip/edit-screen scrap chokepoint: <c>UIStateEditSoldier.ItemScrappedHandler</c> (:622)
        /// and <c>UIStateEditVehicle.ItemScrappedHandler</c> (:173) call <c>GeoFaction.ScrapItem</c> directly —
        /// on a client that is an ungated wallet+storage write. CLIENT: block the local write and forward.
        /// The scrapped instance is addressed two ways:
        ///   • found (by REFERENCE) in a soldier's armour/equipment/inventory list → OpScrapEquipped
        ///     (charId + list + slot triple): the host must subtract THAT instance and drop it off the doll.
        ///     The def-addressed OpScrap would instead destroy an unrelated same-def STORAGE copy while every
        ///     peer keeps the item equipped (the doll item never empties, UIStateEditSoldier:633-636).
        ///   • otherwise it IS a storage instance → the EXISTING def-addressed OpScrap (index slot = count).
        /// UIModuleManufacturing's own ScrapItem call never reaches here on a client (ScrapAllItems is
        /// blocked earlier).</summary>
        [HarmonyPatch(typeof(GeoFaction), nameof(GeoFaction.ScrapItem))]
        internal static class ScrapItemCapturePatch
        {
            private static bool Prefix(GeoFaction __instance, GeoItem geoItem, int amount)
            {
                DiffEngine.FlushOnHostGesture();
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // native
                if (SyncApplyScope.Active) return false; // law 8: applying never echoes an intent
                var def = geoItem?.ItemDef;
                // Non-viewer faction (unreachable from UI) → block the local write, no intent.
                if (def == null || !ReferenceEquals(__instance, GeoLevel()?.PhoenixFaction)) return false;
                if (FindEquippedInstance(__instance, geoItem, out int charId, out byte listIdx))
                    SendScrapEquippedIntent(charId, listIdx, geoItem, amount);
                else
                {
                    ManufactureSync.SendIntent(ManufactureSync.OpScrap, def.Guid, amount);
                    Debug.Log("[MP][scrap] CLIENT equip-scrap intent def=" + def.Guid + " count=" + amount);
                }
                // EITHER branch: this gesture also armed the loadout mark (LoadoutButtonsGesturePatch
                // prefixes ItemScrappedHandler) and the local model stays untouched, so the follow-up
                // SetItems flush would ship the PRE-scrap lists (equipped: asks the host to re-equip the
                // item it just destroyed; storage: a pointless loadout echo). Drop the mark; the rail +
                // open-UI repaint deliver the post-scrap truth.
                _gesturePending = false;
                return false;
            }
        }

        /// <summary>Which Phoenix soldier's loadout holds this exact GeoItem INSTANCE (reference scan —
        /// the equip widgets hold the live model items). False = not equipped, i.e. a storage instance.</summary>
        private static bool FindEquippedInstance(GeoFaction faction, GeoItem item, out int charId, out byte listIdx)
        {
            foreach (var c in faction.Characters)
            {
                if (c == null) continue;
                for (byte i = 0; i < 3; i++)
                {
                    IReadOnlyList<GeoItem> list = i == 0 ? c.ArmourItems : i == 1 ? c.EquipmentItems : c.InventoryItems;
                    if (list == null) continue;
                    for (int k = 0; k < list.Count; k++)
                        if (ReferenceEquals(list[k], item)) { charId = (int)c.Id; listIdx = i; return true; }
                }
            }
            charId = -1; listIdx = 0;
            return false;
        }

        /// <summary>[nonce][OpScrapEquipped][charId][list][defGuid][count][charges][amount] on the shared
        /// 0xAE surface — decoded host-side by <see cref="HandleScrapEquippedIntent"/>. The (def, count,
        /// charges) triple is the same slot ADDRESS OpSetItems uses: count/charges only disambiguate
        /// same-def siblings, the host answers with its OWN live instance.</summary>
        private static void SendScrapEquippedIntent(int charId, byte listIdx, GeoItem item, int amount)
            => IntentRail.Send(SurfaceIds.GeoManufactureIntent, ManufactureSync.OpScrapEquipped,
                "equipped-scrap char=U#" + charId + " list=" + listIdx + " def=" + item.ItemDef.Guid +
                " amount=" + amount,
                w =>
                {
                    w.Write(charId);
                    w.Write(listIdx);
                    w.Write(item.ItemDef.Guid);
                    w.Write(item.CommonItemData.Count);
                    w.Write(item.CommonItemData.CurrentCharges);
                    w.Write(amount);
                });

        /// <summary>Augment-install commit seam: <c>UIModuleMutate.OnAugmentApplied</c> /
        /// <c>UIModuleBionics.OnAugmentApplied</c> is the CONFIRM gesture — native writes the soldier's
        /// armour set, returns swapped-out parts to storage and charges Wallet.Take(ManufacturePrice),
        /// all local authoritative writes on a client. CLIENT: block the whole method, send OpAugment
        /// (defGuid + charId in the index slot); the host re-validates cost + fit and replays the native
        /// sequence (<see cref="HandleAugmentIntent"/>). The preview SetItems from OnAugmentClicked still
        /// runs natively on the client (local optimistic paperdoll preview, no gesture mark → no intent);
        /// the confirmed install arrives via the rail + open-UI repaint.</summary>
        [HarmonyPatch]
        internal static class AugmentApplyCapturePatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(UIModuleMutate), nameof(UIModuleMutate.OnAugmentApplied));
                yield return AccessTools.Method(typeof(UIModuleBionics), nameof(UIModuleBionics.OnAugmentApplied));
            }

            // __0 = the augment ItemDef (positional: the two targets name the parameter differently).
            private static bool Prefix(object __instance, ItemDef __0)
            {
                DiffEngine.FlushOnHostGesture();
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // native
                if (SyncApplyScope.Active) return false; // law 8
                var character = (__instance as IAugmentationUIModule)?.CurrentCharacter;
                if (character == null || __0 == null) return false; // never write locally
                ManufactureSync.SendIntent(ManufactureSync.OpAugment, __0.Guid, (int)character.Id);
                Debug.Log("[MP][equip] CLIENT augment intent char=U#" + (int)character.Id + " def=" + __0.Guid);
                // Mirror the native tail's VIEW-MODEL half (UIModuleBionics.cs:255-256 —
                // CharacterOriginalItems := CharacterCurrentItems): the blocked body was the only thing
                // that advanced the module's snapshot past the staged trial, and without it the screen's
                // NEXT exit revert (Deinit/PopState → RevertUnconfirmedChanges → SetItems(snapshot), a
                // user gesture OUTSIDE any apply scope) rolls the confirmed augment back off the mirror.
                // Same obligation as PersonnelSync's ClearBoughtAbility mirror — view bookkeeping only,
                // no model write, no wallet.
                if (__instance is UIModuleBionics b && b.CharacterCurrentItems.Count > 0)
                { b.CharacterOriginalItems.Clear(); b.CharacterOriginalItems.AddRange(b.CharacterCurrentItems); }
                else if (__instance is UIModuleMutate m && m.CharacterCurrentItems.Count > 0)
                { m.CharacterOriginalItems.Clear(); m.CharacterOriginalItems.AddRange(m.CharacterCurrentItems); }
                return false;
            }
        }

        // ─── Wire codec: per-slot (defGuid, count, charges) triples ─────────
        //
        // WHY NOT the RailMeta EntityList blob this seam originally shipped: AmmoManager is UNCOVERED in the
        // rail classifier (docs/rail-baseline.txt:54-56, covered=0/2), so RailMeta.HasBlobContent:713 is false
        // for it and RailMeta.cs:757-762 writes TagNull instead. CommonItemData.Ammo is `Descend AmmoManager`,
        // so every blob shipped a loaded weapon with a NULLED magazine stack, and the host's SetItems then
        // wrote that loss into the authoritative model.
        //
        // The triple carries no item CONTENT — it is an ADDRESS. `count`/`charges` exist only to DISAMBIGUATE
        // same-def siblings (GeoItemCodec keys the storage dict on ItemDef.Guid alone, so two magazines of one
        // def differing only in charges are otherwise indistinguishable and would swap slots). The host answers
        // an address with its OWN live GeoItem, whose AmmoManager is never read, written or reconstructed.

        private struct SlotRef
        {
            public string Guid;
            public int Count;
            public int Charges;
        }

        /// <summary>The character's three loadout lists, in wire order (0 armour, 1 equipment, 2 inventory).</summary>
        private static List<GeoItem> ListValue(GeoCharacter c, int i)
        {
            IReadOnlyList<GeoItem> src = i == 0 ? c.ArmourItems : i == 1 ? c.EquipmentItems : c.InventoryItems;
            return src == null ? null : src.Where(g => g?.ItemDef != null).ToList();
        }

        /// <summary>[flags:u8 bit0=freeReload bit1..3=list present][per present list: n:i32 + n×(defGuid:string,
        /// count:i32, charges:i32)]. One canonical byte body — doubles as the wire payload tail AND the
        /// revert-guard compare key.</summary>
        private static byte[] EncodeBody(bool freeReload, List<GeoItem> armour, List<GeoItem> equipment, List<GeoItem> inventory)
        {
            var lists = new[] { armour, equipment, inventory };
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                byte flags = (byte)(freeReload ? 1 : 0);
                for (int i = 0; i < 3; i++) if (lists[i] != null) flags |= (byte)(2 << i);
                w.Write(flags);
                for (int i = 0; i < 3; i++)
                {
                    if (lists[i] == null) continue;
                    w.Write(lists[i].Count);
                    foreach (var g in lists[i])
                    {
                        var d = g.CommonItemData;
                        w.Write(g.ItemDef.Guid);
                        w.Write(d.Count);
                        w.Write(d.CurrentCharges);
                    }
                }
                return ms.ToArray();
            }
        }

        private static List<SlotRef>[] DecodeBody(BinaryReader r, out bool freeReload)
        {
            byte flags = r.ReadByte();
            freeReload = (flags & 1) != 0;
            var lists = new List<SlotRef>[3];
            for (int i = 0; i < 3; i++)
            {
                if ((flags & (2 << i)) == 0) continue;
                int n = r.ReadInt32();
                if (n < 0 || n > MaxSlotsPerList) throw new InvalidDataException("slot count " + n + " out of range");
                var list = new List<SlotRef>(n);
                for (int k = 0; k < n; k++)
                    list.Add(new SlotRef { Guid = r.ReadString(), Count = r.ReadInt32(), Charges = r.ReadInt32() });
                lists[i] = list;
            }
            return lists;
        }

        // ─── HOST: intent apply (dedup already done by IntentRail's dispatch) ─

        internal static void HandleIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, BinaryReader r)
        {
            int charId = -1;
            try
            {
                charId = r.ReadInt32();
                var wire = DecodeBody(r, out bool freeReload);

                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, charId, "no geoscape"); return; }
                if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character))
                { Reject(senderPeerId, charId, "unresolved character"); return; }
                var storage = ResolveStorage(character);
                if (storage == null) { Reject(senderPeerId, charId, "no storage"); return; }

                // The character's OWN live items from the lists being REPLACED (a null wire list = untouched,
                // so its items stay out of the pool). Everything the client still wants is served from HERE —
                // same object, same AmmoManager, nothing rebuilt.
                var pool = new List<GeoItem>();
                for (int i = 0; i < 3; i++) if (wire[i] != null) pool.AddRange(ListValue(character, i));

                // Plan first, mutate second: a GeoItem entry = keep this live instance, an ItemDef entry = one
                // genuine GAIN to pop out of storage.
                var plan = new List<object>[3];
                var need = new Dictionary<ItemDef, int>();
                for (int i = 0; i < 3; i++)
                {
                    if (wire[i] == null) continue;
                    plan[i] = new List<object>(wire[i].Count);
                    foreach (var s in wire[i])
                    {
                        var def = GeoItemCodec.ResolveDef(s.Guid);
                        if (def == null) { Reject(senderPeerId, charId, "unknown def " + s.Guid); return; }
                        var live = TakeBestFit(pool, def, s.Count, s.Charges);
                        if (live != null) { plan[i].Add(live); continue; }
                        plan[i].Add(def);
                        need.TryGetValue(def, out int have);
                        need[def] = have + 1;
                    }
                }

                // Validate BEFORE mutating: every gain must be takeable from storage (stale client view →
                // reject the WHOLE intent; Reject's forced re-emit re-syncs that client — with nothing
                // changed host-side the rail would otherwise emit no delta at all).
                foreach (var kv in need)
                {
                    int have = storage.Items.TryGetValue(kv.Key, out var st) ? st.CommonItemData.Count : 0;
                    if (have < kv.Value)
                    { Reject(senderPeerId, charId, "storage lacks " + kv.Key.name + " need=" + kv.Value + " have=" + have); return; }
                }

                CheckLastApplyStuck(character, charId);
                _appliedBefore[charId] = EncodeBody(false, ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));

                var newLists = new List<GeoItem>[3];
                int took = 0;
                for (int i = 0; i < 3; i++)
                {
                    if (plan[i] == null) continue;
                    newLists[i] = new List<GeoItem>(plan[i].Count);
                    foreach (var entry in plan[i])
                    {
                        if (entry is GeoItem live) { newLists[i].Add(live); continue; }
                        // ponytail: a gain always pops exactly ONE unit — the wire `count` is a match hint, not
                        // a quantity. Native loadout items are count-1 by construction (GeoCharacter
                        // .CreateCharacter / UIInventoryList both build them singly), so a stack split inside a
                        // loadout resolves at whole-instance granularity. Revisit only if counts >1 ever appear.
                        var got = storage.PopItem((ItemDef)entry);
                        if (got != null) { newLists[i].Add(got); took++; }
                    }
                }

                character.SetItems(newLists[0], newLists[1], newLists[2], freeReload);
                // Whatever the client did NOT ask for is what the loadout LOST: hand back the LIVE instance so
                // per-instance charges survive and AddItem natively unloads its magazines into storage.
                foreach (var dropped in pool) storage.AddItem(dropped);
                SelfCheckApplied(character, wire, charId);

                try { (character.Faction as GeoPhoenixFaction)?.UpdatePreferredLoadout(character); }
                catch (Exception ex) { Debug.LogWarning("[MP][equip] UpdatePreferredLoadout: " + ex.Message); }
                Debug.Log("[MP][equip] HOST intent APPLIED char=U#" + charId + " nonce=" + nonce +
                          " peer=" + senderPeerId + " took=" + took + " returned=" + pool.Count + " freeReload=" + freeReload);
                _appliedAfter[charId] = EncodeBody(false, ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));
                ReseedLocalScreenAfterRemoteMutation();
                // No push needed: GeoCharacter EntityList fields + storage GeoItemDict ride DiffEngine 0xAC.
            }
            catch (Exception ex)
            {
                Reject(senderPeerId, charId, "(throw) " + ex);
            }
        }

        /// <summary>HOST replay of the edit-screen scrap of an EQUIPPED item at model level
        /// (UIStateEditSoldier.ItemScrappedHandler:622-646 / UIStateEditVehicle:173-198): unload the item's
        /// magazines into storage, <c>GeoFaction.ScrapItem</c> (wallet refund + per-unit subtract,
        /// GeoFaction.cs:1217-1230) on THAT instance, and when the stack empties drop it from the soldier's
        /// list — native's widget RemoveItem + the follow-up UpdateSoldierEquipment flush collapse into one
        /// <c>SetItems</c> here (untouched lists ride null, GeoCharacter.cs:831). Result reaches every peer
        /// through the normal rail: character lists (EntityList) + storage (GeoItemDict) + wallet.</summary>
        internal static void HandleScrapEquippedIntent(ulong senderPeerId, uint nonce, BinaryReader r)
        {
            int charId = -1;
            try
            {
                charId = r.ReadInt32();
                byte listIdx = r.ReadByte();
                string guid = r.ReadString();
                int count = r.ReadInt32();
                int charges = r.ReadInt32();
                int amount = r.ReadInt32();

                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, charId, "scrap: no geoscape"); return; }
                if (listIdx > 2) { Reject(senderPeerId, charId, "scrap: bad list " + listIdx); return; }
                if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character))
                { Reject(senderPeerId, charId, "scrap: unresolved character"); return; }
                // The refund lands on the character's faction — never let a client intent scrap an
                // NPC-faction soldier's gear (law 3).
                if (!ReferenceEquals(character.Faction, geo.PhoenixFaction))
                { Reject(senderPeerId, charId, "scrap: not a Phoenix soldier"); return; }
                var def = GeoItemCodec.ResolveDef(guid);
                if (def == null) { Reject(senderPeerId, charId, "scrap: unknown def " + guid); return; }

                var pool = ListValue(character, listIdx);
                var live = TakeBestFit(pool, def, count, charges); // pool is now the list WITHOUT live
                if (live == null) { Reject(senderPeerId, charId, "scrap: not equipped " + def.name); return; }
                if (amount < 1 || live.CommonItemData.Count < amount)
                { Reject(senderPeerId, charId, "scrap: amount " + amount + " > held " + live.CommonItemData.Count); return; }

                var storage = ResolveStorage(character);
                if (live.CommonItemData.Ammo != null && storage != null)
                    foreach (var mag in live.CommonItemData.Ammo.UnloadMagazines())
                        if (mag is GeoItem geoMag) storage.AddItem(geoMag); // native :627-630 via the storage widget
                character.Faction.ScrapItem(live, amount);
                if (live.CommonItemData.IsEmpty())
                    character.SetItems(listIdx == 0 ? pool : null, listIdx == 1 ? pool : null, listIdx == 2 ? pool : null);

                try { (character.Faction as GeoPhoenixFaction)?.UpdatePreferredLoadout(character); }
                catch (Exception ex) { Debug.LogWarning("[MP][equip] UpdatePreferredLoadout: " + ex.Message); }
                _appliedAfter[charId] = EncodeBody(false, ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));
                ReseedLocalScreenAfterRemoteMutation();
                Debug.Log("[MP][scrap] HOST equipped-scrap APPLIED char=U#" + charId + " def=" + guid +
                          " x" + amount + " nonce=" + nonce + " peer=" + senderPeerId);
            }
            catch (Exception ex)
            {
                Reject(senderPeerId, charId, "scrap (throw) " + ex);
            }
        }

        /// <summary>THE FALSIFIER for the resolution above, and the one runnable check behind it: whatever the
        /// host ended up holding must carry the DEF LAYOUT the client asked for, list by list. Counts and
        /// charges are deliberately NOT compared — the host's own instances are the truth there, that is the
        /// whole point of resolving addresses instead of shipping content — but a def appearing or going
        /// missing means TakeBestFit, the storage pop or the return path lost an item, which is exactly the
        /// "it vanished for everyone but the mover" symptom. Cost: user-gesture rate, two sorted def lists.</summary>
        private static void SelfCheckApplied(GeoCharacter character, List<SlotRef>[] wire, int charId)
        {
            for (int i = 0; i < 3; i++)
            {
                if (wire[i] == null) continue;
                var want = wire[i].Select(s => s.Guid).ToList();
                var got = ListValue(character, i).Select(g => g.ItemDef.Guid).ToList();
                if (want.OrderBy(g => g, StringComparer.Ordinal)
                        .SequenceEqual(got.OrderBy(g => g, StringComparer.Ordinal))) continue;
                Debug.LogError("[MP][equip] SELF-CHECK FAIL apply char=U#" + charId + " list=" + i +
                               " — the host's loadout is not the def layout the client asked for. requested=[" +
                               string.Join(",", want) + "] got=[" + string.Join(",", got) + "]");
            }
        }

        /// <summary>Match one wire slot to one of the character's OWN live items. Ranked so an exact
        /// (count, charges) match wins over a same-def-only one — that is what keeps two same-def siblings with
        /// different charges in their own slots. Returns null = the loadout genuinely GAINS this def.</summary>
        private static GeoItem TakeBestFit(List<GeoItem> pool, ItemDef def, int count, int charges)
        {
            int best = -1, bestScore = -1;
            for (int i = 0; i < pool.Count; i++)
            {
                if (!ReferenceEquals(pool[i].ItemDef, def)) continue;
                var d = pool[i].CommonItemData;
                int score = (d.Count == count ? 2 : 0) + (d.CurrentCharges == charges ? 1 : 0);
                if (score <= bestScore) continue;
                best = i;
                bestScore = score;
                if (score == 3) break;
            }
            if (best < 0) return null;
            var pick = pool[best];
            pool.RemoveAt(best);
            return pick;
        }

        /// <summary>HOST replay of the native augment-install commit (OnAugmentClicked's list-building +
        /// OnAugmentApplied, model level): the augment is BOUGHT (Wallet.Take(ManufacturePrice)), not taken
        /// from storage — exactly why OpSetItems' "gained def must be in storage" validation would reject it.
        /// Validate cost + structural fit BEFORE mutating, then: swap the armour set natively, return
        /// non-permanent swapped-out parts to storage, drop 2-handed gear on a hand-losing augment, charge the
        /// wallet. Result rides GeoCharacter lists + storage GeoItemDict + wallet (all 0xAC value rail).
        /// ponytail: the UI-side gates (2-augment cap, UnlockedAugmentations listing) are not re-checked —
        /// the client screen only offers slots built from host-mirrored state; add them if drift ever shows.</summary>
        internal static bool HandleAugmentIntent(ulong senderPeerId, uint nonce, string defGuid, int charId)
        {
            var geo = GeoLevel();
            if (geo == null) { Reject(senderPeerId, charId, "augment: no geoscape"); return false; }
            var def = GeoItemCodec.ResolveDef(defGuid);
            if (def == null) { Reject(senderPeerId, charId, "augment: unknown def " + defGuid); return false; }
            var tags = Base.Core.GameUtl.GameComponent<SharedData>().SharedGameTags;
            if (def.Tags == null || !(def.Tags.Contains(tags.AnuMutationTag) || def.Tags.Contains(tags.BionicalTag)))
            { Reject(senderPeerId, charId, "not an augment: " + def.name); return false; }
            if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character))
            { Reject(senderPeerId, charId, "augment: unresolved character"); return false; }
            var faction = character.Faction;
            if (faction?.Wallet == null || !faction.Wallet.HasResources(def.ManufacturePrice))
            { Reject(senderPeerId, charId, "augment: cannot afford " + def.name); return false; }

            var originals = character.ArmourItems.ToList();
            var removedDefs = CommonCharacterUtils.CanSwapItem(
                character.TemplateDef.GetAddonsMangerDef(), def,
                originals.Select(i => i.ItemDef).ToList(), null, character);
            if (removedDefs == null)
            { Reject(senderPeerId, charId, "augment does not fit: " + def.name); return false; }

            var current = originals.Where(i => !removedDefs.Contains(i.ItemDef)).ToList();
            current.Add(new GeoItem(def));
            var storage = ResolveStorage(character);
            character.SetItems(current);
            foreach (var item in originals.Except(current))
                if (storage != null && !item.ItemDef.IsPermanentAugment) storage.AddItem(item);
            if (CommonCharacterUtils.LoseHandOnEquip(def))
            {
                var oneHand = new List<GeoItem>();
                var twoHand = new List<GeoItem>();
                foreach (var eq in character.EquipmentItems)
                    (eq.ItemDef.HandsToUse > 1 ? twoHand : oneHand).Add(eq);
                character.SetItems(current, oneHand);
                foreach (var eq in twoHand)
                    if (storage != null && !(eq.ItemDef.Tags?.Contains(tags.AnuMutationTag) ?? false)) storage.AddItem(eq);
            }
            faction.Wallet.Take(def.ManufacturePrice, OperationReason.Purchase);
            try { (faction as GeoPhoenixFaction)?.UpdatePreferredLoadout(character); }
            catch (Exception ex) { Debug.LogWarning("[MP][equip] UpdatePreferredLoadout: " + ex.Message); }
            Debug.Log("[MP][equip] HOST augment APPLIED char=U#" + charId + " def=" + defGuid +
                      " nonce=" + nonce + " peer=" + senderPeerId);
            _appliedAfter[charId] = EncodeBody(false, ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));
            ReseedLocalScreenAfterRemoteMutation(); // same stale-flush hazard as OpSetItems
            return true;
        }

        /// <summary>A remote intent mutates the model behind the back of THIS peer's open equip screen, whose
        /// widget lists still hold the PRE-intent loadout (RCA 2026-07-18, host frames 12189→12190):
        /// <c>UIStateEditSoldier.UpdateState</c> re-flushed those stale lists back over the applied intent one
        /// frame later, ~16 ms before the rail's 0.5 s tick, so the DiffEngine correctly saw NO net change and
        /// the removal never left the host. <c>EquipFlushGate</c> now blocks that stale flush outright, which
        /// makes this reseed redundant for CORRECTNESS but not for FRESHNESS — the gate stops the screen
        /// writing back, it does not make the screen show the new loadout. The reseed is what repaints it
        /// (law 11), through the EXISTING universal seam (OpenUiRepaint), not a second equip-specific path.
        /// Dirty-mark only — the flush (SyncEngine.Tick, host too) owns drag/typing defer + coalescing.</summary>
        private static void ReseedLocalScreenAfterRemoteMutation() => OpenUiRepaint.MarkDirty();

        /// <summary>THE GUARD for the bug above — a silent revert is what cost a whole test cycle (the only
        /// symptom was `returned` creeping 5→6→7 while the rail reported changed=0). Before applying the next
        /// intent for a character, re-encode its live loadout and compare against what this host committed last
        /// time. Only the UNAMBIGUOUS signature screams: the live loadout is byte-identical to the PRE-intent
        /// content, i.e. the apply was REVERTED wholesale by a stale view→model flush. A merely different
        /// loadout is NOT flagged — a host-local edit between two remote intents is legitimate co-op traffic,
        /// and a guard that cries wolf gets ignored. Cost: one encode per intent (user-gesture rate).</summary>
        private static void CheckLastApplyStuck(GeoCharacter character, int charId)
        {
            if (!_appliedAfter.TryGetValue(charId, out var after) || after == null) return;
            if (!_appliedBefore.TryGetValue(charId, out var before) || before == null) return;
            if (RailMeta.BytesEqual(before, after)) return; // that intent was a no-op — nothing to revert
            var now = EncodeBody(false, ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));
            if (now == null || !RailMeta.BytesEqual(now, before)) return;
            Debug.LogError("[MP][equip] HOST previous apply was REVERTED for char=U#" + charId +
                           " — the live loadout is back to its PRE-intent content, so a stale view→model flush " +
                           "(open-screen UpdateState) undid it. The rail can only emit what the host still holds: " +
                           "removals will silently never reach any peer.");
        }

        /// <summary>Law-7 convergence for a REFUSED equip intent — the origin of the IntentRail reject
        /// pattern. Unlike the block-first intent seams, equip gestures mutate the CLIENT's local mirror
        /// natively before the host answers (SetItems + UpdateStorage are real model writes — the seam
        /// ships the RESULT; the augment preview writes the armour set too). On reject the host state did
        /// not change, so the diff rail emits nothing and that client would stay diverged forever.
        /// <see cref="IntentRail.Reject"/> re-emits current host values for everything the gesture could
        /// have touched — the character subtree + the faction storage subtree — and the client's ordinary
        /// Apply converges it (no new wire concept).</summary>
        private static void Reject(ulong peer, int charId, string why)
        {
            var phoenix = GeoLevel()?.PhoenixFaction;
            IntentRail.Reject(SurfaceIds.GeoManufactureIntent, peer, "equip char=U#" + charId + " — " + why,
                charId >= 0 ? "U#" + charId : null,
                phoenix?.Def != null ? "F#" + phoenix.Def.Guid + ".ItemStorage" : null);
        }

        /// <summary>The storage the equip screens trade against (UIStateEditSoldier.StorageItems()).
        /// ponytail: non-global storage (def-flag flip by a mod) = linear scan for the soldier's site;
        /// vanilla+TFTV use global faction storage, so the scan is dead code in practice.</summary>
        private static ItemStorage ResolveStorage(GeoCharacter character)
        {
            var faction = character.Faction;
            if (faction == null) return null;
            if (faction.UseGlobalStorage) return faction.ItemStorage;
            foreach (var s in faction.Sites)
                if (s != null && s.GetAllCharacters().Contains(character)) return s.ItemStorage;
            foreach (var v in faction.Vehicles)
                if (v != null && v.GetAllCharacters().Contains(character)) return v.CurrentSite?.ItemStorage;
            return null;
        }
    }
}
