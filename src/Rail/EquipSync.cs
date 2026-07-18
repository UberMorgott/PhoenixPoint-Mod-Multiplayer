using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
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
    /// Soldier-loadout intent seam, GESTURE level (law 4a). The model chokepoint (GeoCharacter.SetItems)
    /// proved wrong in live 3-instance testing: UIStateEditSoldier.UpdateState re-flushes SetItems EVERY
    /// FRAME with identical content, so a model-level gate had to canonically encode the whole loadout
    /// per frame just to drop it (~60 encodes/s, sustained CPU burn) and its dedup swallowed genuine
    /// user gestures. A pure slot REPOSITION never changes the model at all (GeoItem has no position
    /// field; UIInventoryList packs first-fit from list order), so that seam cannot tell a gesture from
    /// per-frame noise. Quarry: old mod's EquipGesturePatches used the same UI chokepoints.
    ///
    /// EVERY PEER: the UI gesture seams below (AttemptSlotSwap / side-button / reload / loadout
    /// load-unload / scrap-off-doll) MARK a pending LOCAL gesture, and <c>ClientSimGate.EquipFlushGate</c>
    /// lets ONLY the flush that follows a mark reach the model — on host and client alike. Every OTHER
    /// edit-screen flush (idle UpdateState / ExitState / soldier-cycle) is blocked: stale widget content
    /// stomped mirrored deltas on the client within a frame, and on the host it REVERTED an applied
    /// remote intent one frame later, before the 0.5 s diff tick could see it, so the change never
    /// reached any peer (RCA 2026-07-18). The gate is bool checks only, never a per-frame encode (the
    /// old model-level gate's freeze).
    /// CLIENT additionally: the admitted flush fires the SetItems postfix, which sends ONE intent per
    /// gesture carrying the resulting three lists (RailMeta EntityList codec, no second DTO) — a local
    /// optimistic apply whose host echo via the normal 0xAC rail is the source of truth.
    /// HOST additionally: nothing is sent — the admitted flush IS the authoritative write and rides the
    /// normal diff. UpdateStorage is admitted on the HOST only (the host's own drag must move storage);
    /// on the client storage stays blocked even during a gesture — the host derives it from the intent.
    ///
    /// HOST (unchanged): dedup (shared ManufactureSync.Intents) → resolve character by the rail's stable
    /// key (IdentityResolver "U#&lt;charId&gt;") → decode lists → validate that every item the loadout
    /// GAINS is available in storage (stale client view → reject whole intent) → native
    /// GeoCharacter.SetItems → storage side-effects DERIVED from old-vs-new per-def counts (returned
    /// items AddItem'd back, gained items PopItem'd out). The result reaches everyone through the normal
    /// rail (GeoCharacter EntityList fields + storage GeoItemDict on DiffEngine 0xAC) — no bespoke echo.
    /// </summary>
    public static class EquipSync
    {
        private static readonly string[] ListFieldNames = { "_armourItems", "_equipmentItems", "_inventoryItems" };

        // ─── EVERY PEER: one pending flag, set per LOCAL user gesture ──────
        private static bool _gesturePending;
        private static int _flushFrame = -1;  // first frame EquipFlushGate admitted a flush for this gesture
        private static string _gestureSource; // for the per-gesture [MP][equip] log line

        // ─── HOST state: last committed loadout per character (the revert guard, see CheckLastApplyStuck) ─
        private static readonly Dictionary<int, byte[]> _appliedBefore = new Dictionary<int, byte[]>();
        private static readonly Dictionary<int, byte[]> _appliedAfter = new Dictionary<int, byte[]>();

        /// <summary>Read by <c>ClientSimGate.EquipFlushGate</c>: the native flush GROUP that follows a
        /// marked gesture passes the gate. The window is FRAME-scoped, not per-method, because the native
        /// flush always comes in a pair inside ONE frame and the order differs per call site —
        /// UpdateState :470 equip → :474 storage, but ExitState :225 storage → :226 equip, and
        /// ItemScrappedHandler :640 equip → :644 storage. Consuming on either method would starve the
        /// other half of the same gesture (on the HOST that means a silently lost item move). So: the
        /// flag stays pending — however many frames it takes the screen to flush — until the END of the
        /// first frame in which the gate actually admitted something.
        /// The CLIENT additionally consumes it earlier, in <see cref="SetItemsGestureSendPatch"/>, so a
        /// gesture yields exactly ONE intent.
        /// ponytail: a gesture that never produces a flush at all (screen torn down first) leaves the
        /// flag set, so ONE later flush group is admitted; bounded and self-expiring — add an explicit
        /// screen-exit clear only if that ever shows up in a log.</summary>
        internal static bool GesturePending
        {
            get
            {
                if (!_gesturePending) return false;
                if (_flushFrame >= 0 && Time.frameCount != _flushFrame) { _gesturePending = false; return false; }
                return true;
            }
        }

        /// <summary>Called by the gate when it admits a flush — starts the one-frame group window.</summary>
        internal static void NoteFlushAdmitted()
        {
            if (_flushFrame < 0) _flushFrame = Time.frameCount;
        }

        public static void Reset() => ResetForReloadBoundary();

        public static void ResetForReloadBoundary()
        {
            _gesturePending = false;
            _flushFrame = -1;
            _gestureSource = null;
            _appliedBefore.Clear();
            _appliedAfter.Clear();
        }

        private static GeoLevelController GeoLevel()
        {
            var level = Base.Core.GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        private static RailField ListField(int i) =>
            RailType.Get(typeof(GeoCharacter))?.FieldByName(ListFieldNames[i]);

        // ─── CLIENT: gesture seams (law 4a) — mark, then send on the next native flush ─

        /// <summary>A user equip gesture completed on THIS peer — host or client. The native flush that
        /// follows (per-frame on the open equip screens, so within a frame) is the gesture's commit, and
        /// is the ONLY flush <c>ClientSimGate.EquipFlushGate</c> lets through on either peer.
        /// CLIENT: that flush also carries the committed result into <see cref="SetItemsGestureSendPatch"/>,
        /// which turns it into ONE intent. Marking instead of sending here keeps one mechanism for BOTH
        /// equip screens (UIStateEditVehicle maps its lists into SetItems differently) and reads the exact
        /// model-committed lists, not a hand-rebuilt copy.
        /// HOST: nothing is sent — the flush IS the authoritative write, and the result rides the normal
        /// 0xAC diff. The mark exists purely so the gate can tell that commit from an idle stale flush.</summary>
        internal static void MarkGesture(string source)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return; // solo: gate is inert, nothing to mark
            if (SyncApplyScope.Active) return; // apply-driven UI churn is not a user gesture (law 8)
            if (GeoLevel() == null) return;    // tactical UIStateInventory shares these widgets — geoscape only
            _gesturePending = true;
            _flushFrame = -1;
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
        /// :303) bypasses AttemptSlotSwap — it moves items between lists directly.</summary>
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
        /// storage, UIStateEditVehicle:191/:196), so a mark placed after the body arrives too late and the
        /// gate would block the gesture's own commit. UIStateEditVehicle.ItemScrappedHandler is the case
        /// that bites: scrapping a STORAGE item never sets _uiRefreshNeeded (:193 is inside the equip-slot
        /// branch), so its inline UpdateStorage(:196) is the ONLY thing that ever commits the magazines
        /// unloaded into the widget list at :180. Marking first also matches the other seams' semantics —
        /// the flush belongs to the gesture whether it runs inline or one frame later.</summary>
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

        /// <summary>The send point: first native SetItems flush after a marked gesture (the equip screens
        /// flush per-frame, so this fires within a frame of the gesture). Cost on every other flush = one
        /// bool check. Inside SyncApplyScope the flag survives untouched — the next natural flush outside
        /// apply sends it (law 8: nothing is emitted from apply scope).</summary>
        [HarmonyPatch(typeof(GeoCharacter), nameof(GeoCharacter.SetItems))]
        internal static class SetItemsGestureSendPatch
        {
            private static void Postfix(GeoCharacter __instance, bool freeReload)
            {
                if (!_gesturePending || SyncApplyScope.Active) return;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) { _gesturePending = false; return; }
                // HOST: leave the flag alone — the SAME flush still has its storage half to run
                // (UpdateState :470 equip → :474 storage). The frame window in GesturePending expires it.
                SelfCheckGestureCommit();
                if (engine.IsHost) return;
                _gesturePending = false;
                try { SendLoadoutIntent(engine, __instance, freeReload); }
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
        /// admitted flush writes that truncated list into <c>GeoCharacter.SetItems</c> and the rail
        /// faithfully mirrors the loss: host screen right, host MODEL and every client wrong.
        ///
        /// The Changing half is pure duplication — same guard (<c>_isFiltering</c>), same argument, and
        /// nothing between the two callbacks reads <c>UnfilteredItems</c>; <c>ItemChangedHandler</c> does
        /// the authoritative remove, the <c>FilteredItems</c> upkeep and the re-insert. Skipping it makes
        /// every slot transition remove exactly ONE element, which is what the Changed half already
        /// assumes. Generic by construction: one patch on the shared list widget covers every list
        /// (armour / ready / backpack / storage), both equip screens, every gesture (drag, click-move,
        /// double-click, side-button, loadout preset, scrap) and every peer — no per-item special case.
        /// Inert outside a session, so solo keeps vanilla behaviour.</summary>
        [HarmonyPatch(typeof(UIInventoryList), "ItemChangingHandler")]
        internal static class InventoryListDoubleRemovePatch
        {
            private static bool Prefix()
            {
                var engine = NetworkEngine.Instance;
                return engine == null || !engine.IsActiveSession; // solo: run the original
            }
        }

        /// <summary>THE GUARD for the bug above, at the seam the codec self-check cannot see.
        /// <c>DiffEngine.SelfCheckEntityList</c> proves the BLOB survives reorder + value-duplicates; it
        /// cannot prove the list handed to it still holds what the player is looking at. This does, on the
        /// one flush that is authoritative — the gesture commit — by asserting the invariant the
        /// double-Remove breaks: every item a slot DISPLAYS must still be present BY REFERENCE in that
        /// list's <c>UnfilteredItems</c>, because <c>UpdateSoldierEquipment</c> (UIStateEditSoldier:548)
        /// writes exactly that list into the model and <c>UpdateStorage</c> (:564) Except-diffs the
        /// faction <c>ItemStorage</c> against it. A miss means the screen shows an item the model is about
        /// to lose — silently, on the host too. Keyed one-shot per list AND per duplicate-state so an
        /// early duplicate-free gesture can NOT retire the check for the case that actually breaks (the
        /// 2026-07-18 "16/16 OK on an empty list" lesson); failures always shout. Cost: user-gesture
        /// rate, one reference scan per list — never per tick, never per frame.</summary>
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

        /// <summary>ONE intent per gesture: [nonce][OpSetItems][charId][flags+3 EntityList blobs] on the
        /// shared 0xAE surface — decoded host-side by <see cref="HandleIntent"/> (unchanged).</summary>
        private static void SendLoadoutIntent(NetworkEngine engine, GeoCharacter character, bool freeReload)
        {
            int charId = (int)character.Id;
            var body = EncodeBody(freeReload,
                ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));
            if (body == null) return; // codec miss (pre-init) — logged inside
            uint nonce = ManufactureSync.NextNonce();
            byte[] inner;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(nonce);
                w.Write(ManufactureSync.OpSetItems);
                w.Write(charId);
                w.Write(body);
                inner = ms.ToArray();
            }
            try
            {
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoManufactureIntent, SyncKind.ActionRequest, inner);
                engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[MP][equip] CLIENT gesture intent sent char=U#" + charId + " gesture=" + _gestureSource +
                          " nonce=" + nonce + " bytes=" + inner.Length + " freeReload=" + freeReload);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.LogError("[MP][equip] intent exceeds envelope u16 — " + ex.Message);
            }
        }

        /// <summary>Equip/edit-screen scrap chokepoint: <c>UIStateEditSoldier.ItemScrappedHandler</c> (:632)
        /// and <c>UIStateEditVehicle.ItemScrappedHandler</c> (:183) call <c>GeoFaction.ScrapItem</c> directly —
        /// on a client that is an ungated wallet+storage write. CLIENT: block, forward to the EXISTING
        /// OpScrap intent (index slot = count); the host validates storage count and executes natively
        /// (ManufactureSync.HandleIntent), result rides wallet 0xA0 + storage 0xAC. UIModuleManufacturing's
        /// own ScrapItem call never reaches here on a client (ScrapAllItems is blocked earlier).
        /// ponytail: scrap is def-addressed — an item scrapped straight off an EQUIPPED slot is matched
        /// against host STORAGE (same-def storage copy scrapped if present, else host-reject no-op);
        /// carry a charId in the payload if per-instance equipped-slot scrap ever matters.</summary>
        [HarmonyPatch(typeof(GeoFaction), nameof(GeoFaction.ScrapItem))]
        internal static class ScrapItemCapturePatch
        {
            private static bool Prefix(GeoFaction __instance, GeoItem geoItem, int amount)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // native
                if (SyncApplyScope.Active) return false; // law 8: applying never echoes an intent
                var def = geoItem?.ItemDef;
                // Non-viewer faction (unreachable from UI) → block the local write, no intent.
                if (def != null && ReferenceEquals(__instance, GeoLevel()?.PhoenixFaction))
                {
                    ManufactureSync.SendIntent(ManufactureSync.OpScrap, def.Guid, amount);
                    Debug.Log("[MP][scrap] CLIENT equip-scrap intent def=" + def.Guid + " count=" + amount);
                }
                return false;
            }
        }

        /// <summary>Augment-install commit seam: <c>UIModuleMutate.OnAugmentApplied</c> /
        /// <c>UIModuleBionics.OnAugmentApplied</c> is the CONFIRM gesture — native writes the soldier's
        /// armour set, returns swapped-out parts to storage and charges Wallet.Take(ManufacturePrice),
        /// all local authoritative writes on a client. CLIENT: block the whole method, send OpAugment
        /// (defGuid + charId in the index slot); the host re-validates cost + fit and replays the native
        /// sequence (<see cref="HandleAugmentIntent"/>). The preview SetItems from OnAugmentClicked now
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
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // native
                if (SyncApplyScope.Active) return false; // law 8
                var character = (__instance as IAugmentationUIModule)?.CurrentCharacter;
                if (character == null || __0 == null) return false; // never write locally
                ManufactureSync.SendIntent(ManufactureSync.OpAugment, __0.Guid, (int)character.Id);
                Debug.Log("[MP][equip] CLIENT augment intent char=U#" + (int)character.Id + " def=" + __0.Guid);
                return false;
            }
        }

        private static List<GeoItem> ListValue(GeoCharacter c, int i)
        {
            var f = ListField(i);
            return f == null ? null : (f.GetValue(c) as IEnumerable<GeoItem>)?.ToList();
        }

        /// <summary>[flags:u8 bit0=freeReload bit1..3=list present][per present list: len:i32 + EntityList blob].
        /// One canonical byte body — doubles as the wire payload tail AND the dedup compare key.</summary>
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
                    var f = ListField(i);
                    if (f == null) { Debug.LogWarning("[MP][equip] no rail field " + ListFieldNames[i]); return null; }
                    var blob = RailMeta.EncodeEntityList(f, lists[i]);
                    w.Write(blob.Length);
                    w.Write(blob);
                }
                return ms.ToArray();
            }
        }

        // ─── HOST: intent apply (dedup already done by ManufactureSync.HandleIntent) ─

        internal static void HandleIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, BinaryReader r)
        {
            try
            {
                int charId = r.ReadInt32();
                byte flags = r.ReadByte();
                bool freeReload = (flags & 1) != 0;
                var newLists = new List<GeoItem>[3];
                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, charId, "no geoscape"); return; }
                for (int i = 0; i < 3; i++)
                {
                    if ((flags & (2 << i)) == 0) continue;
                    var blob = r.ReadBytes(r.ReadInt32());
                    var f = ListField(i);
                    if (f == null) { Reject(senderPeerId, charId, "no rail field " + ListFieldNames[i]); return; }
                    // Unresolvable elements (unknown def on drift) decode as non-GeoItem/null — dropped.
                    newLists[i] = (RailMeta.DecodeEntityList(blob, f, geo) ?? new List<object>())
                                  .OfType<GeoItem>().Where(g => g.ItemDef != null).ToList();
                }

                if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character))
                { Reject(senderPeerId, charId, "unresolved character"); return; }

                var storage = ResolveStorage(character);
                if (storage == null) { Reject(senderPeerId, charId, "no storage"); return; }

                // Old-vs-new per-def counts over the lists being REPLACED (null list = untouched → excluded).
                var oldItems = new List<GeoItem>();
                var delta = new Dictionary<ItemDef, int>();
                for (int i = 0; i < 3; i++)
                {
                    if (newLists[i] == null) continue;
                    var cur = ListValue(character, i);
                    if (cur != null) oldItems.AddRange(cur);
                    foreach (var g in newLists[i]) Bump(delta, g, +1);
                }
                foreach (var g in oldItems) Bump(delta, g, -1);

                // Validate BEFORE mutating: every gained def must be takeable from storage.
                foreach (var kv in delta)
                {
                    if (kv.Value <= 0) continue;
                    int have = storage.Items.TryGetValue(kv.Key, out var st) ? st.CommonItemData.Count : 0;
                    if (have < kv.Value)
                    { Reject(senderPeerId, charId, "storage lacks " + kv.Key.name + " need=" + kv.Value + " have=" + have); return; }
                }

                CheckLastApplyStuck(character, charId);

                // Apply natively; then the DERIVED storage side-effects.
                _appliedBefore[charId] = EncodeBody(false, ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));
                character.SetItems(newLists[0], newLists[1], newLists[2], freeReload);
                int took = 0, returned = 0;
                foreach (var kv in delta)
                {
                    if (kv.Value > 0)
                    {
                        for (int n = 0; n < kv.Value; n++) storage.PopItem(kv.Key); // soldier's copy came off the wire
                        took += kv.Value;
                    }
                    else if (kv.Value < 0)
                    {
                        // Return |delta| units by handing back the character's OLD instances (preserves
                        // per-instance ammo/charges; AddItem natively unloads magazines into storage).
                        // ponytail: loadout instances assumed count-1 (native UI grants that); a stacked
                        // instance would over-return — revisit only if counts ever exceed 1 in loadouts.
                        int need = -kv.Value;
                        foreach (var g in oldItems.Where(o => o.ItemDef == kv.Key).Take(need))
                        { storage.AddItem(g); returned++; }
                    }
                }
                try { (character.Faction as GeoPhoenixFaction)?.UpdatePreferredLoadout(character); }
                catch (Exception ex) { Debug.LogWarning("[MP][equip] UpdatePreferredLoadout: " + ex.Message); }
                Debug.Log("[MP][equip] HOST intent APPLIED char=U#" + charId + " nonce=" + nonce +
                          " peer=" + senderPeerId + " took=" + took + " returned=" + returned + " freeReload=" + freeReload);
                _appliedAfter[charId] = EncodeBody(false, ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));
                ReseedLocalScreenAfterRemoteMutation();
                // No push needed: GeoCharacter EntityList fields + storage GeoItemDict ride DiffEngine 0xAC.
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MP][equip] HOST intent REJECT (throw): " + ex);
            }
        }

        /// <summary>HOST replay of the native augment-install commit (OnAugmentClicked's list-building +
        /// OnAugmentApplied, model level): the augment is BOUGHT (Wallet.Take(ManufacturePrice)), not taken
        /// from storage — exactly why OpSetItems' "gained def must be in storage" validation would reject
        /// it. Validate cost + structural fit BEFORE mutating, then: swap the armour set natively, return
        /// non-permanent swapped-out parts to storage, drop 2-handed gear on a hand-losing augment, charge
        /// the wallet. Result rides GeoCharacter lists + storage GeoItemDict (0xAC) + wallet (0xA0).
        /// ponytail: the UI-side gates (2-augment cap, UnlockedAugmentations listing) are not re-checked —
        /// the client screen only offers slots built from host-mirrored state; add them if drift ever shows.</summary>
        internal static bool HandleAugmentIntent(ulong senderPeerId, uint nonce, string defGuid, int charId)
        {
            var geo = GeoLevel();
            if (geo == null) { Reject(senderPeerId, charId, "augment: no geoscape"); return false; }
            var def = Base.Core.GameUtl.GameComponent<Base.Defs.DefRepository>()?.GetDef(defGuid) as ItemDef;
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

        /// <summary>THE FIX (RCA 2026-07-18, host Player.log frames 12189→12190 / 12242→12243 / 12295→12296):
        /// a remote intent mutates the model behind the back of THIS peer's open equip screen, whose widget
        /// lists still hold the PRE-intent loadout. <c>UIStateEditSoldier.UpdateState</c> re-flushes those
        /// stale lists into the model on the NEXT FRAME (<c>UpdateSoldierEquipment</c> → SetItems,
        /// <c>UpdateStorage</c> → storage) — at the time gated by EquipFlushGate on the client only,
        /// UNGATED on the host. The revert landed ~16 ms after the apply, i.e. long before the rail's 0.5 s tick, so the
        /// DiffEngine correctly saw NO net change (changed=0, no 0xAC packet) and the removal never left
        /// the host. Reseeding model→UI synchronously here — same call as the mutation, before any
        /// UpdateState can run — makes that next native flush write the NEW content instead of the old.
        /// Reuses the EXISTING universal repaint seam (OpenUiRepaint, already host-safe: it routes
        /// UIStateEditSoldier to the read-direction reseed and defers on a local drag); this adds the
        /// missing CALLER (host intent-apply), not a second equip-specific repaint path.
        /// KEPT as belt-and-braces: EquipFlushGate now blocks the stale host flush outright, which makes
        /// this reseed redundant for CORRECTNESS but not for FRESHNESS — the gate stops the screen
        /// writing back, it does not make the screen show the new loadout. The reseed is what repaints
        /// it (law 11). ponytail: the remaining generalization is one shared post-apply reseed hook in
        /// ManufactureSync.HandleIntent so scrap/manufacture intents repaint too — out of scope here.</summary>
        private static void ReseedLocalScreenAfterRemoteMutation()
        {
            try { OpenUiRepaint.RepaintOpenGeoscapeScreen(); }
            catch (Exception ex) { Debug.LogWarning("[MP][equip] HOST post-apply reseed failed: " + ex.Message); }
        }

        /// <summary>THE GUARD for the bug above — a silent revert is what cost a whole test cycle (the only
        /// symptom was `returned` creeping 5→6→7 while the rail reported changed=0). Before applying the
        /// next intent for a character, re-encode its live loadout and compare against what this host
        /// committed last time. Only the UNAMBIGUOUS signature screams: the live loadout is byte-identical
        /// to the PRE-intent content, i.e. the apply was REVERTED wholesale by a stale view→model flush.
        /// A merely different loadout is NOT flagged — a host-local edit between two remote intents is
        /// legitimate co-op traffic, and a guard that cries wolf gets ignored.
        /// Cost: one canonical encode per intent (user-gesture rate) — never per tick, never per frame.</summary>
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

        private static void Bump(Dictionary<ItemDef, int> map, GeoItem g, int sign)
        {
            int units = Math.Max(1, g.CommonItemData?.Count ?? 1);
            map.TryGetValue(g.ItemDef, out var v);
            map[g.ItemDef] = v + sign * units;
        }

        private static void Reject(ulong peer, int charId, string why) =>
            Debug.LogWarning("[MP][equip] HOST intent REJECT char=U#" + charId + " peer=" + peer + " — " + why);

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
