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
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View.ViewControllers.AugmentationScreen;
using PhoenixPoint.Geoscape.View.ViewModules;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Soldier-loadout intent seam on the ONE model-level chokepoint <c>GeoCharacter.SetItems</c>
    /// (every equip path funnels there: drag/drop, side-button quick-add, reload, loadout load/unload,
    /// replenish, edit-vehicle crew, roster deployment — UI-gesture hooks would miss some).
    ///
    /// CLIENT (law 3 projector + law 4a capture, Gap C+D): SetItems is ALWAYS blocked — the mirrored
    /// model is written only by the rail's field appliers, never by UI view-model flushes (the open
    /// equip screen re-flushes its stale view-model from UpdateState/ExitState, incl. inside the
    /// OpenUiRepaint re-enter, which would stomp every just-applied delta). Outside SyncApplyScope the
    /// blocked write becomes an intent on the shared manufacturing intent surface (0xAE, OpSetItems),
    /// but ONLY on real content change: the proposed loadout is canonically encoded (the same
    /// RailMeta.EncodeEntityList codec the DiffEngine uses for these very fields) and compared against
    /// BOTH the character's current mirrored content (drops the no-op re-flush storm) and the last-sent
    /// payload (drops resends while the host echo is in flight).
    /// ponytail: that content-compare IS the send-rate cap — the old mod relayed this per-frame path
    /// unconditionally and produced ~60 intents/sec + FPS collapse; here a no-change frame sends
    /// NOTHING, so the ceiling is one intent per actual user gesture.
    ///
    /// HOST: dedup (shared ManufactureSync.Intents) → resolve character by the rail's stable key
    /// (IdentityResolver "U#&lt;tacUnitId&gt;") → decode lists → validate that every item the loadout
    /// GAINS is available in storage (stale client view → reject whole intent) → native
    /// GeoCharacter.SetItems → storage side-effects DERIVED from old-vs-new per-def counts (returned
    /// items AddItem'd back, gained items PopItem'd out — the client never writes storage). The result
    /// reaches everyone through the normal rail (GeoCharacter EntityList fields + storage GeoItemDict
    /// on DiffEngine 0xAC) — no bespoke echo channel.
    /// </summary>
    public static class EquipSync
    {
        private static readonly string[] ListFieldNames = { "_armourItems", "_equipmentItems", "_inventoryItems" };

        // ─── CLIENT state ──────────────────────────────────────────────────
        private static readonly Dictionary<int, byte[]> _lastSent = new Dictionary<int, byte[]>();
        private static int _suppressed; // dedup-killed SetItems flushes since last send (storm telemetry)

        public static void Reset() => ResetForReloadBoundary();

        public static void ResetForReloadBoundary()
        {
            _lastSent.Clear();
            _suppressed = 0;
        }

        private static GeoLevelController GeoLevel()
        {
            var level = Base.Core.GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        private static RailField ListField(int i) =>
            RailType.Get(typeof(GeoCharacter))?.FieldByName(ListFieldNames[i]);

        // ─── CLIENT: the write-gate + intent capture (Harmony seam, law 4a/4b) ─

        /// <summary>Gap C+D. FALSE = client: never mutate the mirror locally; emit intent when warranted.</summary>
        [HarmonyPatch(typeof(GeoCharacter), nameof(GeoCharacter.SetItems))]
        internal static class SetItemsCapturePatch
        {
            private static bool Prefix(GeoCharacter __instance,
                IEnumerable<GeoItem> armour, IEnumerable<GeoItem> equipment, IEnumerable<GeoItem> inventory,
                bool freeReload)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // native
                // CLIENT: always block — even inside SyncApplyScope (the OpenUiRepaint re-enter runs the
                // screen's ExitState in apply scope, and its stale view-model flush is exactly the stomp).
                if (SyncApplyScope.Active) return false; // blocked silently, never an intent (law 8)
                try { CaptureIntent(engine, __instance, armour, equipment, inventory, freeReload); }
                catch (Exception ex) { Debug.LogWarning("[MP][equip] CLIENT capture failed: " + ex.Message); }
                return false;
            }
        }

        private static void CaptureIntent(NetworkEngine engine, GeoCharacter character,
            IEnumerable<GeoItem> armour, IEnumerable<GeoItem> equipment, IEnumerable<GeoItem> inventory, bool freeReload)
        {
            int charId = (int)character.Id;
            var proposed = EncodeBody(freeReload,
                armour == null ? null : armour.ToList(),
                equipment == null ? null : equipment.ToList(),
                inventory == null ? null : inventory.ToList());
            if (proposed == null) return; // codec miss (pre-init) — logged inside

            // Dedup line 1: no-op flush (UpdateState/ExitState re-writing what the mirror already holds).
            // NOTE: compares with the SAME freeReload flag so a genuine free-reload of depleted items
            // (different ammo bytes) still goes through, while the per-frame identical flush dies here.
            var current = EncodeBody(freeReload,
                ListValue(character, 0), ListValue(character, 1), ListValue(character, 2));
            if (current != null && RailMeta.BytesEqual(proposed, current)) { Suppress(); return; }
            // Dedup line 2: identical to the payload already in flight (host echo not applied yet).
            if (_lastSent.TryGetValue(charId, out var last) && RailMeta.BytesEqual(proposed, last)) { Suppress(); return; }

            _lastSent[charId] = proposed;
            uint nonce = ManufactureSync.NextNonce();
            byte[] inner;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(nonce);
                w.Write(ManufactureSync.OpSetItems);
                w.Write(charId);
                w.Write(proposed);
                inner = ms.ToArray();
            }
            try
            {
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoManufactureIntent, SyncKind.ActionRequest, inner);
                engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[MP][equip] CLIENT intent sent char=U#" + charId + " nonce=" + nonce +
                          " bytes=" + inner.Length + " freeReload=" + freeReload + " suppressedSinceLast=" + _suppressed);
                _suppressed = 0;
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
        /// sequence (<see cref="HandleAugmentIntent"/>). The preview SetItems from OnAugmentClicked is
        /// already blocked by SetItemsCapturePatch and its stray intent host-rejected (augment def not in
        /// storage) — the paperdoll preview simply doesn't render on a client; the confirmed install
        /// arrives via the rail + open-UI repaint.</summary>
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

        private static void Suppress()
        {
            _suppressed++;
            if (_suppressed % 300 == 0)
                Debug.Log("[MP][equip] CLIENT dedup suppressed=" + _suppressed + " SetItems flushes (storm guard alive)");
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
