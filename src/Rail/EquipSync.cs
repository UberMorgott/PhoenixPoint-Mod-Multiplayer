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
    /// CLIENT now: the UI gesture seams below (AttemptSlotSwap / side-button / reload / loadout
    /// load-unload / scrap-off-doll) MARK a pending gesture; the native flush that follows (SetItems runs
    /// natively on the client again — local optimistic apply, cheap) fires the postfix which sends ONE
    /// intent per gesture carrying the resulting three lists (RailMeta EntityList codec, no second DTO).
    /// The host echo via the normal 0xAC rail is the source of truth and overwrites the optimistic
    /// state; the ONLY client write-gate left is apply-scope-only (ClientApplyScopeEquipFlushGate) so
    /// the OpenUiRepaint re-enter can't stomp just-applied deltas with a stale view-model flush.
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

        // ─── CLIENT state: one pending flag, set per user gesture ──────────
        private static bool _gesturePending;
        private static string _gestureSource; // for the per-gesture [MP][equip] log line

        public static void Reset() => ResetForReloadBoundary();

        public static void ResetForReloadBoundary()
        {
            _gesturePending = false;
            _gestureSource = null;
        }

        private static GeoLevelController GeoLevel()
        {
            var level = Base.Core.GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        private static RailField ListField(int i) =>
            RailType.Get(typeof(GeoCharacter))?.FieldByName(ListFieldNames[i]);

        // ─── CLIENT: gesture seams (law 4a) — mark, then send on the next native flush ─

        /// <summary>A user equip gesture completed on this client. The next native GeoCharacter.SetItems
        /// flush (per-frame on the open equip screens, so within a frame) carries the committed result and
        /// <see cref="SetItemsGestureSendPatch"/> turns it into ONE intent. Marking instead of sending here
        /// keeps one mechanism for BOTH equip screens (UIStateEditVehicle maps its lists into SetItems
        /// differently) and reads the exact model-committed lists, not a hand-rebuilt copy.</summary>
        internal static void MarkGesture(string source)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return;
            if (SyncApplyScope.Active) return; // apply-driven UI churn is not a user gesture (law 8)
            if (GeoLevel() == null) return;    // tactical UIStateInventory shares these widgets — geoscape only
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
        /// the soldier/vehicle lists; its wallet+storage half already rides OpScrap via ScrapItemCapturePatch).</summary>
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

            private static void Postfix() => MarkGesture("loadout-button");
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
                if (engine == null || !engine.IsActiveSession || engine.IsHost) { _gesturePending = false; return; }
                _gesturePending = false;
                try { SendLoadoutIntent(engine, __instance, freeReload); }
                catch (Exception ex) { Debug.LogWarning("[MP][equip] CLIENT gesture send failed: " + ex.Message); }
            }
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

                // Apply natively; then the DERIVED storage side-effects.
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
            return true;
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
