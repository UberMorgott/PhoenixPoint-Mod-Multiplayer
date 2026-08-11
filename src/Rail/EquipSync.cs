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
    /// Soldier-loadout intent seam (law 4a) — the client half of equip. Without it a client's inventory
    /// edits are purely local: the rail is host→client only, so a client drag mirrors NOWHERE.
    ///
    /// SEAM CHOICE: the model chokepoint <c>GeoCharacter.SetItems</c>, BLOCK-FIRST like every other family
    /// (IntentRail.ShouldRunNative). This family used to be the one exception — UI gestures set a bool and a
    /// POSTFIX on a later flush turned the mark into an intent — and that second, unguaranteed phase is
    /// exactly how a whole session of storage↔soldier drags emitted NOTHING and nobody but the mover saw a
    /// thing (RCA 2026-07-29: 4 `patch bound` lines, zero `self-check OK`, host `changed=0`).
    /// What the marks were really compensating for was that the equip screens RE-flush content that did not
    /// change (UIStateEditSoldier.cs:459-474, event-driven off _uiRefreshNeeded), most loudly right after a
    /// host echo repaints them, and that a slot move which leaves the LIST order alone is nothing to sync at
    /// all. One byte compare against the character's own canon (<see cref="ChangedBody"/>) covers both, with
    /// no state to keep and no second phase to miss — while a move that DOES reorder the list still ships,
    /// because order is state (RailCheck L10). Bonus: every OTHER caller of the funnel (loadout presets,
    /// replenish, deployment) is captured for free instead of needing its own mark.
    ///
    /// ITEM POSITION IS SETTLED — DO NOT RE-OPEN IT (HANDOFF §5d, closed 2026-08-07 after a full source
    /// walk; the owner's decision, recorded here because the question keeps coming back).
    ///   • THERE IS NO COORDINATE. `GeoItem` carries def + count + charges and nothing spatial
    ///     (GeoItem.cs:12-23), and neither does an `ItemStorage` entry. A CELL IS A LIST INDEX.
    ///   • THE GRID PACKS FIRST-FIT FROM LIST ORDER. `UIInventoryList.ItemChangedHandler` re-inserts with
    ///     `Insert(Math.Min(Slots.IndexOf(slot), Count), item)` (:855-859) — a drag into empty space right
    ///     of the last item CLAMPS BACK to the end, so it changes no order, produces no delta, and the
    ///     MOVER HIMSELF loses the hole the moment he re-opens the screen. Nothing to replicate exists.
    ///     Do not add a position field and do not ship cell hints: there is no such state to mirror, and
    ///     v1 "keeping" a cell was v1 re-showing the mover his own un-reconciled widgets.
    ///   • ORDER ALREADY REPLICATES, END TO END, VERIFIED LINK BY LINK: a permutation is byte-unequal to
    ///     the canon (<see cref="EncodeBody"/> writes guids in list order → L19 `order-blind`); the host
    ///     rebuilds each list in WIRE order (<see cref="HandleIntent"/> :641-685); `_inventoryItems` rides
    ///     as an ORDERED EntityList (docs/rail-baseline.txt, asserted by L156); the receiving applier
    ///     reorders the LIVE instances (GenericApplier.cs:794-798) and its unchanged-check is byte-wise
    ///     (:1375). So a rearrangement that permutes the list DOES reach every peer.
    ///   • THE REACTIVITY HALF IS THE ONE THAT WAS NEVER ASSERTED, and is now: the delta arrives as kind
    ///     GeoCharacter → `UiEventMap.Fire` → `OpenUiRepaint.MarkDirty` → `UiNativeRepaint.Table`'s
    ///     UIStateEditSoldier entry → `ReseedEquipScreen` → `UIStateEditSoldier.DisplaySoldier`:582 hands
    ///     `character.InventoryItems` to `UIModuleSoldierEquip.UpdateData`, which `Deinit()`+`Init()`s the
    ///     list (:632-633) so the slots re-pack from the NEW order. RailCheck L156 holds every seam of that
    ///     chain — including the standing temptation to silence the equip screen's repaint by adding
    ///     GeoCharacter to `UiNativeRepaint.IgnoredKinds` to buy back its 4-5 fps.
    ///
    /// HOST: dedup (IntentRail, own 0xB3 surface — split off 0xAE 2026-07-26, the surface byte IS the
    /// family) → resolve the character by the rail's stable key (IdentityResolver "U#&lt;charId&gt;") →
    /// match every wire slot against the character's OWN live GeoItems → PopItem from storage only for
    /// genuine GAINS, AddItem back whatever the client dropped → native GeoCharacter.SetItems. The result
    /// reaches every peer through the normal rail (GeoCharacter EntityList fields + storage GeoItemDict on
    /// DiffEngine 0xAC) — no bespoke echo.
    /// </summary>
    public static class EquipSync
    {
        [ThreadStatic] private static bool _applyingPreparationIntent;
        [ThreadStatic] private static bool _hostLocalPreparationIntent;
        // Intent ops (GeoEquipIntent 0xB3 inner payload). Byte values kept from their 0xAE tenure —
        // never renumber a live op (no version negotiation; both peers must agree byte-for-byte).
        internal const byte OpSetItems = 8;       // per-gesture loadout commit [charId][flags][slot triples]
        internal const byte OpAugment = 10;       // augment install confirm [defGuid][charId] — bought (Wallet.Take), never from storage
        internal const byte OpScrapEquipped = 12; // per-INSTANCE scrap of an equipped item [charId][list][slot triple][amount]

        /// <summary>Arm the 0xB3 surface on the generic intent engine. No family reconverge — every
        /// reject passes its scoped subtree prefixes explicitly (<see cref="Reject"/>).</summary>
        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpSetItems] = (engine, peer, nonce, op, r) => HandleIntent(engine, peer, nonce, r),
                [OpScrapEquipped] = (engine, peer, nonce, op, r) => HandleScrapEquippedIntent(peer, nonce, r),
                [OpAugment] = (engine, peer, nonce, op, r) => HandleAugmentIntent(peer, nonce, WireString.ReadKey(r), r.ReadInt32()),
            };
            IntentRail.Register(SurfaceIds.GeoEquipIntent, "equip", ops);
        }

        // ─── HOST state: last committed loadout per character (the revert guard, see CheckLastApplyStuck) ─
        private static readonly Dictionary<int, byte[]> _appliedBefore = new Dictionary<int, byte[]>();
        private static readonly Dictionary<int, byte[]> _appliedAfter = new Dictionary<int, byte[]>();
        /// <summary>Who asked for the loadout the host is now holding — the ONE peer that has to be told if
        /// that apply is ever undone behind its back (<see cref="CheckLastApplyStuck"/>). Written beside
        /// <see cref="_appliedAfter"/> by every remote apply, so the pair can never name different intents.</summary>
        private static readonly Dictionary<int, ulong> _appliedPeer = new Dictionary<int, ulong>();

        /// <summary>Hard cap on a wire list length — the only untrusted count in this payload, and the one
        /// value a malformed/hostile peer could turn into an allocation. Loadout lists are single digits.</summary>
        private const int MaxSlotsPerList = 64;

        public static void Reset() => ResetForReloadBoundary();

        public static void ResetForReloadBoundary()
        {
            _appliedBefore.Clear();
            _appliedAfter.Clear();
            _appliedPeer.Clear();
            _sent.Clear();
        }

        // ─── EVERY PEER: the ONE capture seam (law 4a), block-first ─────────

        /// <summary>THE seam. <c>GeoCharacter.SetItems</c> (GeoCharacter.cs:831, no overloads) is the model
        /// funnel every loadout write bottoms out in — both equip screens' flushes (UIStateEditSoldier:548,
        /// UIStateEditVehicle:427), loadout presets (CharacterLoadoutsViewService:55/:59), replenish
        /// (UIModuleReplenish:278/:418), deployment (UIStateRosterDeployment:555), post-mission
        /// (PostmissionReplenishManager:179). HOST/solo: native, and <c>ShouldRunNative</c>'s own
        /// FlushOnHostGesture arm ships the result this frame. CLIENT: never writes the model (law 3) —
        /// the call is blocked and, when it would have CHANGED something, converted into exactly one
        /// OpSetItems. The screen keeps showing the client's staged widgets until the host echo repaints it
        /// (UiEventMap[UIStateEditSoldier/-Vehicle] → ReseedEquipScreen), the same posture as Personnel.</summary>
        [HarmonyPatch(typeof(GeoCharacter), nameof(GeoCharacter.SetItems))]
        internal static class SetItemsCapturePatch
        {
            /// <summary>SECOND, behind <c>SetItemsApplyGate</c> (<c>Priority.First</c>) — see the reason
            /// there. Every side effect on this seam lives here, so it runs only once the pure gate has
            /// agreed this flush is a real gesture and not a mirror apply.</summary>
            [HarmonyPriority(Priority.Normal)]
            private static bool Prefix(GeoCharacter __instance, IEnumerable<GeoItem> armour,
                                       IEnumerable<GeoItem> equipment, IEnumerable<GeoItem> inventory, bool freeReload)
            {
                SelfCheckGestureCommit(); // in-session only; the flush about to happen is the commit
                if (BlockStaleHostRevert(__instance, armour, equipment, inventory, freeReload)) return false;
                var network = NetworkEngine.Instance; DurablePreparationEditContext hostPreparation;
                if (!_applyingPreparationIntent && network != null && network.IsActiveSession && network.IsHost &&
                    !SyncApplyScope.Active && DeploymentWindowClose.TryCapturePreparationEditContext(out hostPreparation))
                {
                    var body = ChangedBody(freeReload,
                        new[] { ToSlots(armour), ToSlots(equipment), ToSlots(inventory) }, Slots(__instance));
                    if (body == null) return false;
                    using (var ms = new MemoryStream()) using (var writer = new BinaryWriter(ms, Encoding.UTF8, true))
                    {
                        writer.Write((int)__instance.Id); writer.Write(body);
                        DurablePreparationEditContext.Write(writer, hostPreparation); writer.Flush(); ms.Position = 0;
                        using (var reader = new BinaryReader(ms, Encoding.UTF8, true))
                        { _hostLocalPreparationIntent = true; try { HandleIntent(network, 0, 0, reader); }
                          finally { _hostLocalPreparationIntent = false; } }
                    }
                    return false;
                }
                if (IntentRail.ShouldRunNative() || IsAugmentStaging())
                    return true;
                try
                {
                    var canon = Slots(__instance);
                    var body = ChangedBody(freeReload,
                        new[] { ToSlots(armour), ToSlots(equipment), ToSlots(inventory) }, canon);
                    if (body == null) return false; // this flush changes nothing — block silently, zero traffic
                    int charId = (int)__instance.Id;
                    if (AlreadySent(charId, body, EncodeBody(false, canon)))
                    {
                        if (MpDiag.On)
                            Debug.Log("[MP][equip] duplicate flush suppressed char=U#" + charId +
                                      " bytes=" + body.Length + " — same body, model unmoved, host echo still in flight");
                        return false;
                    }
                    IntentRail.Send(SurfaceIds.GeoEquipIntent, OpSetItems,
                        "loadout char=U#" + charId + " bytes=" + body.Length + " freeReload=" + freeReload,
                        w =>
                        {
                            w.Write(charId); w.Write(body);
                            DurablePreparationEditContext preparation;
                            if (DeploymentWindowClose.TryCapturePreparationEditContext(out preparation))
                                DurablePreparationEditContext.Write(w, preparation);
                        });
                }
                catch (Exception ex)
                {
                    // Nothing was written and nothing was sent: no delta will ever repaint the staged
                    // widgets, so reconverge them from the (un-mutated) local model like the reject path.
                    Debug.LogError("[MP][equip] CLIENT loadout capture failed — reconverging local UI: " + ex);
                    OpenUiRepaint.MarkDirty();
                }
                return false;
            }

        }

        // ─── CLIENT state: the intent already in flight per character (the repeat guard) ───
        private struct Sent { internal byte[] Body, Canon; }

        private static readonly Dictionary<int, Sent> _sent = new Dictionary<int, Sent>();

        /// <summary>THE REPEAT GUARD. Blocking the client's native write leaves the canon UNTOUCHED, so the
        /// equip screen's next content flush — it re-flushes on every UI event, 6-7 frames in a row after one
        /// drag (log 2026-07-29: nonce=1..7, bytes=510, frames 21503-21509) — recomputes the same difference
        /// and would ship the same intent again. IntentDedup cannot see it: its (peer, surface, nonce) key is
        /// the transport's redelivery guard, and these ARE distinct intents by that key.
        /// Suppress only while NOTHING has moved: same body AND same canon it was computed against. The host
        /// echo (or a reject reconverge) mutates the model, which retires the memo by itself — so the same
        /// gesture repeated after the echo ships again, with no event wiring, no per-screen knowledge and
        /// nothing to reset by hand. First sight of a (body, canon) pair records it and returns false = send.</summary>
        internal static bool AlreadySent(int charId, byte[] body, byte[] canon)
        {
            if (_sent.TryGetValue(charId, out var s) &&
                RailMeta.BytesEqual(s.Body, body) && RailMeta.BytesEqual(s.Canon, canon)) return true;
            _sent[charId] = new Sent { Body = body, Canon = canon };
            return false;
        }

        /// <summary>The family's ONE native-passthrough arm (the IntentRail law allows arms on top of the base
        /// decision, never a restatement of it). The augment screens use the LIVE GeoCharacter as their preview
        /// buffer: <c>UIModuleBionics.OnAugmentClicked</c>:198 SetItems a staged armour set containing a
        /// <c>new GeoItem(mutation)</c> that sits on no storage shelf, and :209/:129 (Mutate :184/:195/:120)
        /// SetItems it back. Capturing those would ship one guaranteed-reject intent per CLICK and leave the
        /// paperdoll frozen. The CONFIRM is captured properly one level up (<see cref="AugmentApplyCapturePatch"/>
        /// → OpAugment), and UiEventMap.RepaintAugmentScreen (:442-461) already treats "live == staged trial"
        /// as an expected client state. Nothing here reaches the rail: the staged set is presentation.</summary>
        private static bool IsAugmentStaging()
        {
            var vs = GenericApplier.GeoLevel()?.View?.CurrentViewState;
            return vs is UIStateBionics || vs is UIStateMutate;
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
            // ONE pass for both indices. The old shape ran list.IndexOf (a full value-equality scan) and
            // THEN a second forward scan for the reference — twice the comparisons, on a handler that fires
            // once per item per list per rebuild, i.e. O(items²) on a repaint of the equip screen. The
            // instance is value-equal to itself, so the first value match can never sit AFTER it: reaching
            // the reference with no earlier value match means the list is already aligned and there is
            // nothing to do — which is the normal case and now exits at the item's own index.
            int firstValue = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], target))
                {
                    if (firstValue < 0) return;         // already the first match — no-op
                    list[i] = list[firstValue];
                    list[firstValue] = target;
                    return;
                }
                if (firstValue < 0 && Equals(list[i], target)) firstValue = i;
            }
            // target is not in this list at all → no-op, same as before.
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
            var engine = NetworkEngine.Instance;
            // Solo: the two UIInventoryList patches this falsifies are deliberately inert, so the game's own
            // double-Remove is live and the check would shout at a player we are not protecting.
            if (engine == null || !engine.IsActiveSession) return;
            var view = GenericApplier.GeoLevel()?.View;
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
                if (IntentRail.ShouldRunNative()) return true;
                var def = geoItem?.ItemDef;
                // Non-viewer faction (unreachable from UI) → block the local write, no intent.
                if (def == null || !ReferenceEquals(__instance, GenericApplier.GeoLevel()?.PhoenixFaction)) return false;
                if (FindEquippedInstance(__instance, geoItem, out int charId, out byte listIdx))
                    SendScrapEquippedIntent(charId, listIdx, geoItem, amount);
                else
                {
                    ManufactureSync.SendIntent(ManufactureSync.OpScrap, def.Guid, amount);
                    Debug.Log("[MP][scrap] CLIENT equip-scrap intent def=" + def.Guid + " count=" + amount);
                }
                // The follow-up flush needs no special handling any more: blocking this call leaves the
                // count un-decremented, so the native handler's `IsEmpty()` branch never strips the widget
                // (UIStateEditSoldier.cs:633-636) and its UpdateSoldierEquipment:640 re-flushes PRE-scrap
                // lists that are byte-identical to the still-PRE-scrap model → ChangedBody blocks them
                // silently. The post-scrap truth arrives as the host's delta + repaint.
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

        /// <summary>[nonce][OpScrapEquipped][charId][list][defGuid][count][charges][amount] on the 0xB3
        /// equip surface — decoded host-side by <see cref="HandleScrapEquippedIntent"/>. The (def, count,
        /// charges) triple is the same slot ADDRESS OpSetItems uses: count/charges only disambiguate
        /// same-def siblings, the host answers with its OWN live instance.</summary>
        private static void SendScrapEquippedIntent(int charId, byte listIdx, GeoItem item, int amount)
            => IntentRail.Send(SurfaceIds.GeoEquipIntent, OpScrapEquipped,
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
                if (IntentRail.ShouldRunNative()) return true;
                var character = (__instance as IAugmentationUIModule)?.CurrentCharacter;
                if (character == null || __0 == null) return false; // never write locally
                string defGuid = __0.Guid;
                int charId = (int)character.Id;
                IntentRail.Send(SurfaceIds.GeoEquipIntent, OpAugment,
                    "op=" + OpAugment + " def=" + defGuid + " char=U#" + charId,
                    w => { w.Write(defGuid); w.Write(charId); });
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

        /// <summary>CLICK PARITY on the augment screens — the v1 fix (quarry commit cbb9b2c,
        /// <c>AugmentSlotSwitchUnlockPatch</c>) ported as its PRINCIPLE, not its code.
        ///
        /// The behaviour, grounded in the decompile: <c>UIModuleMutationSection.SelectMutation</c>
        /// (:173-259) is a TOGGLE on the single field <c>_selectedMutationSlot</c> (:78), and while a slot
        /// IS selected its own loop disables every sibling — :219 <c>slot2.SetEnable(enabled: false)</c> —
        /// while <c>PhoenixGeneralButton</c> drops clicks on a disabled button. So a click on part B while
        /// part A is selected is silently swallowed; the player must click A a SECOND time to release it
        /// first. A section left in that state is precisely the "the armour is blocked and can no longer be
        /// selected or cleared" report, and it is where a repaint that nulls the selection without re-running
        /// this loop strands the screen.
        ///
        /// Postfix, PRESENTATION ONLY (law 4c): whenever a selection is armed, restore every slot's enabled
        /// state to the game's OWN rule, <c>CanApplyAugumentation</c> (:160-171) — the identical expression
        /// native writes in <c>RefreshContainerSlots</c> (:384) and in this method's own deselect branch
        /// (:210). Clicking B then runs <c>SelectMutation(B)</c> natively and A releases by itself, which is
        /// the requested behaviour. No model write, no wire traffic, nothing staged, no new UI.
        /// IN-SESSION ONLY, so single-player stays vanilla.</summary>
        [HarmonyPatch(typeof(UIModuleMutationSection), "SelectMutation")]
        internal static class AugmentSlotSwitchUnlockPatch
        {
            private static readonly FieldInfo SelectedSlot =
                AccessTools.Field(typeof(UIModuleMutationSection), "_selectedMutationSlot");
            private static readonly MethodInfo CanApply =
                AccessTools.Method(typeof(UIModuleMutationSection), "CanApplyAugumentation");

            /// <summary>Loud, never silent: if the game renamed either member the patch would be a no-op that
            /// looks installed, and the double-click bug would quietly come back.</summary>
            private static bool Prepare()
            {
                if (SelectedSlot != null && CanApply != null) return true;
                Debug.LogError("[Multiplayer][equip] AugmentSlotSwitchUnlockPatch NOT bound (" +
                               (SelectedSlot == null ? "_selectedMutationSlot " : "") +
                               (CanApply == null ? "CanApplyAugumentation " : "") +
                               "missing) — augment body-part switching stays vanilla double-click");
                return false;
            }

            private static void Postfix(UIModuleMutationSection __instance)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return; // solo: vanilla
                // Unity's overloaded != also answers "destroyed", which a plain null check would miss.
                if (!(SelectedSlot.GetValue(__instance) is UnityEngine.Object sel) || sel == null) return;
                var container = __instance.CurrentContainer;
                if (container == null || container.Slots == null) return;
                foreach (var slot in container.Slots)
                    if (slot != null)
                        slot.SetEnable((bool)CanApply.Invoke(__instance, new object[] { slot.Mutation }));
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

        internal struct SlotRef
        {
            internal string Guid;
            internal int Count;
            internal int Charges;
        }

        private sealed class EquipRollbackSnapshot
        {
            private readonly GeoCharacter _character; private readonly ItemStorage _storage;
            private readonly List<GeoItem>[] _lists; private readonly List<GeoItem> _stored;
            internal EquipRollbackSnapshot(GeoCharacter character, ItemStorage storage)
            {
                _character=character;_storage=storage;
                _lists=Enumerable.Range(0,3).Select(i=>ListValue(character,i)
                    .Select(x=>(GeoItem)x.Clone()).ToList()).ToArray();
                _stored=storage.ToList().Select(x=>(GeoItem)x.Clone()).ToList();
            }
            internal void Restore()
            {
                _storage.Clear(); _storage.AddItems(ClonePreparationSnapshot(_stored,x=>(GeoItem)x.Clone()));
                _applyingPreparationIntent=true;
                try{_character.SetItems(ClonePreparationSnapshot(_lists[0],x=>(GeoItem)x.Clone()),
                    ClonePreparationSnapshot(_lists[1],x=>(GeoItem)x.Clone()),
                    ClonePreparationSnapshot(_lists[2],x=>(GeoItem)x.Clone()),false);}
                finally{_applyingPreparationIntent=false;}
            }
        }

        internal static IReadOnlyList<T> ClonePreparationSnapshot<T>(IEnumerable<T> snapshot,Func<T,T> clone)
        {if(snapshot==null)throw new ArgumentNullException(nameof(snapshot));if(clone==null)throw new ArgumentNullException(nameof(clone));
            return snapshot.Select(clone).ToArray();}

        /// <summary>The character's three loadout lists, in wire order (0 armour, 1 equipment, 2 inventory).</summary>
        private static List<GeoItem> ListValue(GeoCharacter c, int i)
        {
            IReadOnlyList<GeoItem> src = i == 0 ? c.ArmourItems : i == 1 ? c.EquipmentItems : c.InventoryItems;
            return src == null ? null : src.Where(g => g?.ItemDef != null).ToList();
        }

        /// <summary>Live items → wire addresses. Null in, null out: that is how "this call does not touch
        /// that list" survives all the way to <see cref="ChangedBody"/> and the wire flags.</summary>
        private static List<SlotRef> ToSlots(IEnumerable<GeoItem> src)
            => src?.Where(g => g?.ItemDef != null)
                  .Select(g => new SlotRef
                  {
                      Guid = g.ItemDef.Guid,
                      Count = g.CommonItemData.Count,
                      Charges = g.CommonItemData.CurrentCharges,
                  }).ToList();

        /// <summary>The character's own current loadout as wire addresses — the canon everything compares to.</summary>
        private static List<SlotRef>[] Slots(GeoCharacter c)
            => new[] { ToSlots(c.ArmourItems), ToSlots(c.EquipmentItems), ToSlots(c.InventoryItems) };

        /// <summary>THE CLIENT DECISION, pure and headless so the harness can falsify it (RailCheck L19).
        /// A null incoming list means "untouched", so it is filled from <paramref name="current"/> before
        /// encoding — that one substitution is what replaces the deleted gesture marks, and it makes the
        /// compare key and the wire body THE SAME bytes (a list resolved back to its own content costs the
        /// host nothing: TakeBestFit matches every entry out of the pool, zero gains, zero drops).
        /// Byte-equal to the character's canon = this call changes nothing (the screen's re-flush after a
        /// repaint, an order-preserving slot move, a blocked scrap's pre-scrap re-flush) → null → block in
        /// silence.
        /// Otherwise = exactly ONE OpSetItems body. freeReload rides the incoming side only: a free reload is
        /// a real mutation even when the lists match, so it can never compare equal.</summary>
        internal static byte[] ChangedBody(bool freeReload, List<SlotRef>[] incoming, List<SlotRef>[] current)
        {
            var merged = new List<SlotRef>[3];
            for (int i = 0; i < 3; i++) merged[i] = incoming[i] ?? current[i];
            var body = EncodeBody(freeReload, merged);
            return RailMeta.BytesEqual(body, EncodeBody(false, current)) ? null : body;
        }

        /// <summary>[flags:u8 bit0=freeReload bit1..3=list present][per present list: n:i32 + n×(defGuid:string,
        /// count:i32, charges:i32)]. One canonical byte body — doubles as the wire payload tail AND the
        /// compare key for <see cref="ChangedBody"/> and <see cref="CheckLastApplyStuck"/>.</summary>
        internal static byte[] EncodeBody(bool freeReload, List<SlotRef>[] lists)
        {
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
                    foreach (var s in lists[i])
                    {
                        w.Write(s.Guid);
                        w.Write(s.Count);
                        w.Write(s.Charges);
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
                    list.Add(new SlotRef { Guid = WireString.ReadKey(r), Count = r.ReadInt32(), Charges = r.ReadInt32() });
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
                DurablePreparationEditContext preparation;
                bool hasPreparation = DurablePreparationEditContext.TryReadTrailing(r, out preparation);

                var geo = GenericApplier.GeoLevel();
                if (geo == null) { Reject(senderPeerId, charId, "no geoscape"); return; }
                if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character))
                { Reject(senderPeerId, charId, "unresolved character"); return; }
                var storage = ResolveStorage(character);
                if (storage == null) { Reject(senderPeerId, charId, "no storage"); return; }
                if (!hasPreparation && DeploymentWindowClose.RequiresPreparationContextForSender(engine, senderPeerId))
                { Reject(senderPeerId, charId, "open deployment preparation requires revision context"); return; }
                DurablePreparationEditEngine preparationEngine = null;
                if (hasPreparation)
                {
                    preparationEngine = new DurablePreparationEditEngine(DurableInboxSession.ActiveStore,
                        delta =>
                        {
                            var touched = new HashSet<object> { character, storage };
                            for (int list = 0; list < 3; list++)
                                foreach (var item in ListValue(character, list)) if (item != null) touched.Add(item);
                            try { MissionSync.BroadcastPreparationEdit(delta); }
                            catch (Exception ex) { Debug.LogError("[MP][inbox] preparation-edit broadcast failed after durable commit: " + ex); }
                            UiEventMap.FirePreparationEdit(touched, geo, delta.Occurrence, delta.PreparationRevision);
                        });
                }

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

                bool appliedForMemo = false;
                Func<bool> apply = () =>
                {
                CheckLastApplyStuck(character, charId);
                _appliedBefore[charId] = EncodeBody(false, Slots(character));

                var newLists = new List<GeoItem>[3];
                var gained = new List<GeoItem>(); // popped out of storage = the half the widget also ammo-loads
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
                        if (got != null) { newLists[i].Add(got); gained.Add(got); took++; }
                    }
                }

                _applyingPreparationIntent = true;
                try { character.SetItems(newLists[0], newLists[1], newLists[2], freeReload); }
                finally { _applyingPreparationIntent = false; }
                // Whatever the client did NOT ask for is what the loadout LOST: hand back the LIVE instance so
                // per-instance charges survive and AddItem natively unloads its magazines into storage.
                foreach (var dropped in pool) storage.AddItem(dropped);
                // …and the OTHER half of the widget gesture the block intercepted (see AutoLoadAmmo).
                // AFTER the returns: storage is then in the state the screen's own storage commit leaves it
                // (UIStateEditSoldier.UpdateStorage:564-578), so a swap can reload out of the magazines the
                // swapped-out weapon just unloaded — the same pool the client's widget list was showing.
                foreach (var got in gained) AutoLoadAmmo(got, storage);
                SelfCheckApplied(character, wire, charId);

                try { (character.Faction as GeoPhoenixFaction)?.UpdatePreferredLoadout(character); }
                catch (Exception ex) { Debug.LogWarning("[MP][equip] UpdatePreferredLoadout: " + ex.Message); }
                Debug.Log("[MP][equip] HOST intent APPLIED char=U#" + charId + " nonce=" + nonce +
                          " peer=" + senderPeerId + " took=" + took + " returned=" + pool.Count + " freeReload=" + freeReload);
                appliedForMemo = true;
                ReseedLocalScreenAfterRemoteMutation();
                return true;
                };
                if (hasPreparation)
                {
                    string refusal;
                    var touchedIds = new[] { IdentityResolver.RootRef(character), IdentityResolver.RootRef(storage) };
                    var rollback = new EquipRollbackSnapshot(character, storage);
                    bool accepted = preparationEngine.TryApply(preparation,
                        () => DeploymentWindowClose.AuthorizePreparationContext(engine, senderPeerId,
                            preparation, _hostLocalPreparationIntent), apply, rollback.Restore, touchedIds, out refusal);
                    if (appliedForMemo && !_hostLocalPreparationIntent) RecordApplied(character, charId, senderPeerId);
                    if (!accepted) { Reject(senderPeerId, charId, refusal); return; }
                }
                else { apply(); if (appliedForMemo && !_hostLocalPreparationIntent) RecordApplied(character, charId, senderPeerId); }
                // No push needed: GeoCharacter EntityList fields + storage GeoItemDict ride DiffEngine 0xAC.
            }
            catch (Exception ex)
            {
                Reject(senderPeerId, charId, "(throw) " + ex);
            }
        }

        /// <summary>HOST replay of the ammo half of the intercepted gesture. Vanilla auto-load is WIDGET
        /// work, not model work: UIInventoryList.AddItem:154-163 → TryLoadAmmo:256-293 →
        /// TryLoadItemWithItem:295-313 pulls magazines out of the storage list and LoadMagazine's them into
        /// the weapon, and the storage half is committed later by UIStateEditSoldier.UpdateStorage:564-578.
        /// The intent carries an ADDRESS, so the host answers it with <c>storage.PopItem</c> — and a faction
        /// ItemStorage UNLOADS every magazine on the way IN (ItemStorage.cs:52-63), so what it hands back is
        /// always empty while the client's screen shows a loaded gun. Replaying only half the funnel is what
        /// made the gun arrive empty on the host.
        ///
        /// Driven entirely by def METADATA (<c>ItemDef.CompatibleAmmunition</c>/<c>ChargesMax</c>,
        /// ItemDef.cs:44/47) — every weapon, every ammo type, both equip screens, no per-item knowledge.
        /// Same licensed move as <see cref="HandleScrapEquippedIntent"/>'s UnloadMagazines replay.
        /// The REVERSE direction needs nothing: <c>storage.AddItem</c> unloads natively.</summary>
        private static void AutoLoadAmmo(GeoItem item, ItemStorage storage)
        {
            var ammo = item.CommonItemData.Ammo; // non-null exactly for ammo-compatible defs (CommonItemData.cs:76-83)
            if (ammo == null || storage == null) return;
            // Vehicle gear reloads FOR FREE and never out of storage (TryLoadAmmo:256-259) — the wire's
            // freeReload bit covers the flushes that carry one, this covers the ones that do not.
            if (IsVehicleEquipment(item)) { item.ReloadForFree(); return; }
            int max = item.ItemDef.ChargesMax;
            var compat = item.ItemDef.CompatibleAmmunition;
            if (compat == null) return;
            foreach (var magDef in compat)
            {
                if (magDef == null) continue; // ItemDef.cs:178-183 logs these exist
                while (ammo.CurrentCharges < max)
                {
                    if (!storage.Items.TryGetValue(magDef, out var stack)) break; // out of stock: native stops too
                    // What PopItem is about to hand back: a stack >1 splits off a FULL fresh GeoItem, a
                    // single one is the instance itself with its own charges (ItemStorage.cs:95-111).
                    int magCharges = stack.CommonItemData.Count > 1 ? magDef.ChargesMax
                                                                    : stack.CommonItemData.CurrentCharges;
                    // AmmoManager.LoadMagazine:32-35 EVICTS LoadedMagazines[0] when the load overflows —
                    // a magazine destroyed for nothing, in a loop that can then never fill. Stop instead.
                    if (magCharges <= 0 || ammo.CurrentCharges + magCharges > max) break;
                    var mag = storage.PopItem(magDef);
                    if (mag == null) break;
                    ammo.LoadMagazine(mag);
                }
                if (ammo.CurrentCharges >= max) return;
            }
            // Cheap falsifier for the replay above: a gun that stayed EMPTY while its own ammo sits in
            // storage means the pop/load path lost the gesture — the exact symptom this replay exists for.
            if (ammo.CurrentCharges == 0 &&
                compat.Any(d => d != null && storage.Items.TryGetValue(d, out var s) && s.CommonItemData.CurrentCharges > 0))
                Debug.LogError("[MP][equip] SELF-CHECK FAIL ammo — " + item.ItemDef.name +
                               " stayed empty while storage still holds compatible ammunition");
        }

        /// <summary>UIInventoryList.IsVehicleEquipment:250-253, model side.</summary>
        private static bool IsVehicleEquipment(GeoItem item)
        {
            var tags = Base.Core.GameUtl.GameComponent<SharedData>()?.SharedGameTags;
            return tags != null && item.ItemDef.Tags != null && item.ItemDef.Tags.Contains(tags.VehicleClassTag);
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
                string guid = WireString.ReadKey(r);
                int count = r.ReadInt32();
                int charges = r.ReadInt32();
                int amount = r.ReadInt32();

                var geo = GenericApplier.GeoLevel();
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

                // Same before/after pair every other remote apply records — a scrap that lands on the host
                // while a peer holds the equip screen open is undone by the same stale flush.
                CheckLastApplyStuck(character, charId);
                _appliedBefore[charId] = EncodeBody(false, Slots(character));

                var storage = ResolveStorage(character);
                if (live.CommonItemData.Ammo != null && storage != null)
                    foreach (var mag in live.CommonItemData.Ammo.UnloadMagazines())
                        if (mag is GeoItem geoMag) storage.AddItem(geoMag); // native :627-630 via the storage widget
                character.Faction.ScrapItem(live, amount);
                if (live.CommonItemData.IsEmpty())
                    character.SetItems(listIdx == 0 ? pool : null, listIdx == 1 ? pool : null, listIdx == 2 ? pool : null);

                try { (character.Faction as GeoPhoenixFaction)?.UpdatePreferredLoadout(character); }
                catch (Exception ex) { Debug.LogWarning("[MP][equip] UpdatePreferredLoadout: " + ex.Message); }
                RecordApplied(character, charId, senderPeerId);
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
            var geo = GenericApplier.GeoLevel();
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

            // Same before/after pair every other remote apply records — see BlockStaleHostRevert.
            CheckLastApplyStuck(character, charId);
            _appliedBefore[charId] = EncodeBody(false, Slots(character));

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
            RecordApplied(character, charId, senderPeerId);
            ReseedLocalScreenAfterRemoteMutation(); // same stale-flush hazard as OpSetItems
            return true;
        }

        /// <summary>Close one remote apply: what the host now holds, and WHO asked for it. One call, so the
        /// two can never name different intents — <see cref="CheckLastApplyStuck"/> hands the second half to
        /// <see cref="Reject"/> when an apply turns out to have been undone.</summary>
        private static void RecordApplied(GeoCharacter character, int charId, ulong peer)
        {
            _appliedAfter[charId] = EncodeBody(false, Slots(character));
            _appliedPeer[charId] = peer;
        }

        /// <summary>A remote intent mutates the model behind the back of THIS peer's open equip screen, whose
        /// widget lists still hold the PRE-intent loadout (RCA 2026-07-18, host frames 12189→12190):
        /// <c>UIStateEditSoldier.UpdateState</c> re-flushed those stale lists back over the applied intent one
        /// frame later, ~16 ms before the rail's 0.5 s tick, so the DiffEngine correctly saw NO net change and
        /// the removal never left the host. <c>SetItemsApplyGate</c> now blocks that stale flush outright, which
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
            var now = EncodeBody(false, Slots(character));
            if (now == null || !RailMeta.BytesEqual(now, before)) return;
            Debug.LogError("[MP][equip] HOST previous apply was REVERTED for char=U#" + charId +
                           " — the live loadout is back to its PRE-intent content, so a stale view→model flush " +
                           "(open-screen UpdateState) undid it. The rail can only emit what the host still holds: " +
                           "removals will silently never reach any peer.");
            // AND TELL THE ONE PEER IT HAPPENED TO. The host used to log this to its own console and stop
            // there: seven applies were undone in eleven seconds (log 2026-08-07 23:12:44→23:12:55, nonces
            // 43-49) while the gesturing client's log carried no revert, reject or correction line at all —
            // it believed all ten gestures had landed. A revert is a refusal that arrives late, so it takes
            // the refusal path: Reject re-emits the character + storage subtrees, which converges that
            // client's mirror onto what the host actually holds and repaints its open screen. `notify` is on
            // because this is the exact case IntentRail reserves it for — the player is looking at a loadout
            // that is not the one the host has, and nothing else on his screen would ever say so.
            if (_appliedPeer.TryGetValue(charId, out var peer) && peer != 0)
                RejectAndNotify(peer, charId, "a previous loadout change was undone on the host by a stale screen flush");
        }

        /// <summary>THE ROOT-CAUSE HALF of the revert above — the guard that stops it instead of reporting it.
        /// <see cref="SetItemsApplyGate"/> only blocks a view→model flush taken INSIDE an apply
        /// (<c>SyncApplyScope</c>); a HOST standing in its own equip screen while a remote intent lands is
        /// outside every scope — <see cref="IntentRail.ShouldRunNative"/> returns true for a host — so
        /// <c>UIStateEditSoldier.UpdateState</c>'s next flush wrote its still-PRE-intent widget lists straight
        /// back over the applied loadout, ~150-1800 ms later, and the DiffEngine correctly emitted nothing.
        /// That is the ordinary two-players-in-loadout case, not an edge.
        ///
        /// The signature is the same UNAMBIGUOUS one <see cref="CheckLastApplyStuck"/> already trusts, asked
        /// one flush EARLIER: the model still holds exactly what the last remote apply committed, and this
        /// flush wants to put back exactly what stood there before it. No enumeration of screens, no timer.
        /// The three outcomes are what makes it self-limiting rather than a lock:
        ///   • body == the PRE-intent content → stale view, block it, and mark the screen for repaint (the
        ///     block stops the write, the repaint is what makes the screen show the new loadout — law 11);
        ///   • body == what the host holds → an ordinary idle re-flush, nothing to decide;
        ///   • anything else → a real host-local edit, so the memo RETIRES and a host that later drags the
        ///     loadout back to its old layout by hand is never fought.</summary>
        private static bool BlockStaleHostRevert(GeoCharacter character, IEnumerable<GeoItem> armour,
                                                 IEnumerable<GeoItem> equipment, IEnumerable<GeoItem> inventory,
                                                 bool freeReload)
        {
            if (freeReload || _appliedBefore.Count == 0) return false; // a free reload is a real mutation
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return false;
            int charId = (int)character.Id;
            if (!_appliedBefore.TryGetValue(charId, out var before)) return false;
            if (!_appliedAfter.TryGetValue(charId, out var after)) return false;

            var canon = Slots(character);
            var incoming = new[] { ToSlots(armour), ToSlots(equipment), ToSlots(inventory) };
            var merged = new List<SlotRef>[3];
            for (int i = 0; i < 3; i++) merged[i] = incoming[i] ?? canon[i];

            switch (JudgeHostFlush(before, after, EncodeBody(false, canon), EncodeBody(false, merged)))
            {
                case FlushVerdict.Native:
                    return false;
                case FlushVerdict.Retire:
                    _appliedBefore.Remove(charId);   // a genuine host edit — never fight the next one
                    return false;
                default:
                    Debug.LogWarning("[MP][equip] HOST stale screen flush BLOCKED for char=U#" + charId +
                                     " — an open equip screen tried to write back the loadout as it stood BEFORE peer=" +
                                     (_appliedPeer.TryGetValue(charId, out var p) ? p.ToString() : "?") +
                                     "'s change. The write is dropped and this screen is repainted from the model.");
                    ReseedLocalScreenAfterRemoteMutation();
                    return true;
            }
        }

        /// <summary>What a host-side loadout flush IS, decided on four byte bodies and nothing else — pure and
        /// headless so the harness can falsify it (RailCheck L187), exactly like <see cref="ChangedBody"/>.
        /// <paramref name="before"/>/<paramref name="after"/> are the last remote apply's pair,
        /// <paramref name="canon"/> is what the host holds right now and <paramref name="incoming"/> is what
        /// this flush wants to write (untouched lists already merged from canon, so all four are comparable).</summary>
        internal enum FlushVerdict
        {
            /// <summary>Nothing to decide — let the native write run.</summary>
            Native,
            /// <summary>A wholesale write-back of the PRE-intent loadout over an apply still standing.</summary>
            StaleRevert,
            /// <summary>A real host-local edit — the memo no longer describes anything and must go.</summary>
            Retire,
        }

        internal static FlushVerdict JudgeHostFlush(byte[] before, byte[] after, byte[] canon, byte[] incoming)
        {
            if (before == null || after == null || canon == null || incoming == null) return FlushVerdict.Native;
            if (RailMeta.BytesEqual(before, after)) return FlushVerdict.Native;   // that intent was a no-op
            if (!RailMeta.BytesEqual(canon, after)) return FlushVerdict.Native;   // the model already moved on
            if (RailMeta.BytesEqual(incoming, after)) return FlushVerdict.Native; // idle re-flush of what is there
            return RailMeta.BytesEqual(incoming, before) ? FlushVerdict.StaleRevert : FlushVerdict.Retire;
        }

        /// <summary>Law-7 convergence for a REFUSED equip intent — the origin of the IntentRail reject
        /// pattern. Unlike the block-first intent seams, equip gestures mutate the CLIENT's local mirror
        /// natively before the host answers (SetItems + UpdateStorage are real model writes — the seam
        /// ships the RESULT; the augment preview writes the armour set too). On reject the host state did
        /// not change, so the diff rail emits nothing and that client would stay diverged forever.
        /// <see cref="IntentRail.Reject"/> re-emits current host values for everything the gesture could
        /// have touched — the character subtree + the faction storage subtree — and the client's ordinary
        /// Apply converges it (no new wire concept).</summary>
        private static void Reject(ulong peer, int charId, string why) =>
            IntentRail.Reject(SurfaceIds.GeoEquipIntent, peer, "equip char=U#" + charId + " — " + why,
                              ReemitPrefixes(charId));

        /// <summary>The family's ONE notifying refusal, kept as its own call site so RailCheck L123's
        /// allowlist names exactly this case and every ORDINARY equip refusal above stays quiet. Reserved for
        /// a refusal that arrives AFTER the player was told yes — see <see cref="CheckLastApplyStuck"/>.</summary>
        private static void RejectAndNotify(ulong peer, int charId, string why) =>
            IntentRail.Reject(SurfaceIds.GeoEquipIntent, peer, "equip char=U#" + charId + " — " + why, true,
                              ReemitPrefixes(charId));

        /// <summary>Everything an equip gesture could have touched: the character subtree + faction storage.</summary>
        private static string[] ReemitPrefixes(int charId)
        {
            var phoenix = GenericApplier.GeoLevel()?.PhoenixFaction;
            return new[]
            {
                charId >= 0 ? "U#" + charId : null,
                phoenix?.Def != null ? "F#" + phoenix.Def.Guid + ".ItemStorage" : null,
            };
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
