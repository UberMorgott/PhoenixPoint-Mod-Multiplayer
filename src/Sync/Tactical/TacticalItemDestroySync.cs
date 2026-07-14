using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// THROWABLE/CONSUMABLE item-DESTROY mirror (tac.item.destroy 0x9F) — rca-inventory part 3.
    ///
    /// Symptom: the host throws a grenade ("Item PX_HandGrenade_WeaponDef destroyed on Soldier_8"), but the item
    /// REMOVAL never reaches clients — the equip mirror (0x8B) carries only the SELECTED slot, never a removal — so
    /// every client keeps a PHANTOM grenade in its mirror inventory. When that phantom's owner re-throws it, the
    /// client relays a tac.intent.ability whose ability guid no longer resolves on the host
    /// (TacticalCombatSync.HostOnAbilityIntent → ResolveAbilityByGuid → null) → throw animation but NO projectile.
    ///
    /// RCA: item destruction funnels through the ONE native chokepoint <c>TacticalItem.Destroy()</c> — for a thrown
    /// throwable (FireWeaponAtTargetCrt's <c>if (weapon.IsThrowable) weapon.Destroy()</c>) AND for a consumable that
    /// hits 0 charges (<c>TacticalItem.OnChargesChanged → Destroy()</c>, TacticalItem.cs:696). It runs only on the
    /// HOST (the client's own throw is suppressed/relayed, and the fire-anim/origin-native replay SKIPS the local
    /// Destroy — ThrowableDestroyGuardPatch — precisely to keep the host authoritative), so nothing removed the item
    /// on peers.
    ///
    /// Fix: HOST-prefix that single chokepoint (<see cref="TacticalItemDestroyBroadcastPatch"/>) and, for a
    /// throwable/consumable attached to a REGISTERED actor, broadcast (actorNetId, slot, itemDefGuid, defIndex). Every
    /// peer removes the SAME item from its mirror inventory via the native <c>Destroy()</c>. Item identity mirrors the
    /// loot surface (0x9A/0x9B): (ItemDef guid, index among that def in the slot inventory, PRE-removal). Host-gated so
    /// a client's own local Destroy (incl. the peer's apply below re-entering Destroy) never re-broadcasts.
    /// </summary>
    public static class TacticalItemDestroySync
    {
        // Slot discriminator — same meaning as TacticalInventoryTransferCodec.Slot*.
        private const byte SlotInventory = 0;    // backpack (TacticalActor.Inventory)
        private const byte SlotEquipments = 1;   // ready slots (TacticalActor.Equipments)

        // ─── HOST: a throwable/consumable item is being DESTROYED → broadcast the removal ─────────────
        /// <summary>HOST prefix hook (before the native <c>TacticalItem.Destroy()</c> removes the item): for a
        /// throwable OR consumable attached to a REGISTERED actor, read {actorNetId, slot, itemDefGuid, defIndex}
        /// from the still-intact inventory and broadcast <c>tac.item.destroy</c>. No-op off-host / off-session, for a
        /// dummy/detached item, for a non-throwable/non-consumable (weapons/armor destroyed by the death cascade are
        /// removed with their actor via the despawn mirror), or for an unregistered actor. Fail-open (logs+swallows)
        /// so the native destroy always proceeds.</summary>
        public static void HostBroadcastItemDestroy(object item)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || !engine.IsHost) return;
                if (item == null) return;

                // Scope to the phantom-prone set: thrown throwables + charge-consumables. Everything else routes
                // through Destroy() too (death-drop, teardown) but must NOT mirror as an inventory removal here.
                bool throwable = GetProp(item, "IsThrowable") as bool? ?? false;
                bool consumable = GetProp(item, "IsConsumable") as bool? ?? false;
                if (!(throwable || consumable)) return;

                object actor = GetProp(item, "Actor") ?? GetProp(item, "TacticalActor");
                if (actor == null) return;
                int actorNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (actorNetId < 0) return;   // unregistered actor → its items don't sync anyway

                object inv = GetProp(item, "InventoryComponent");
                if (inv == null) return;
                byte slot = SlotFor(actor, inv);

                string guid = DefReflection.GetGuid(GetProp(item, "ItemDef"));
                if (string.IsNullOrEmpty(guid)) return;

                int defIndex = IndexAmongDef(inv, item, guid);
                if (defIndex < 0) return;   // item not found in its own inventory (already detached) → nothing to mirror

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacItemDestroy);
                byte[] payload = TacticalLiveCodec.EncodeItemDestroy(seq, actorNetId, slot, guid, defIndex);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacItemDestroy, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.item.destroy seq=" + seq + " actorNetId=" + actorNetId +
                          " slot=" + slot + " def=" + guid + " defIndex=" + defIndex + " (throwable=" + throwable + " consumable=" + consumable + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastItemDestroy failed: " + ex); }
        }

        // ─── CLIENT: apply the host item-destroy → remove the phantom from the mirror inventory ───────
        /// <summary>CLIENT inbound (<c>tac.item.destroy</c>): resolve actor → slot inventory → the Nth item of the def,
        /// then call the native <c>TacticalItem.Destroy()</c> to remove it from the mirror inventory (and tear down its
        /// visual) exactly as the host did. Re-entering Destroy() here is safe: the broadcast hook is host-gated, so the
        /// client apply never re-broadcasts. No-op on host / stale seq / unresolvable actor / item already gone.</summary>
        public static void ClientOnItemDestroy(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeItemDestroy(payload, out var o)) { Debug.LogError("[Multiplayer][tac] tac.item.destroy decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacItemDestroy, o.Seq)) return;

            try
            {
                object actor = TacticalDeploySync.ResolveLiveActor(o.ActorNetId);
                if (actor == null) { Debug.LogWarning("[Multiplayer][tac] tac.item.destroy: no actor for netId " + o.ActorNetId); TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacItemDestroy, o.Seq); return; }

                object inv = o.Slot == SlotEquipments ? GetProp(actor, "Equipments") : GetProp(actor, "Inventory");
                object item = NthItemOfDef(inv, o.ItemDefGuid, o.DefIndex);
                if (item == null)
                {
                    // Already removed on this side (e.g. the origin's own throw path already cleaned it, or a prior
                    // reconcile) — nothing to do, still advance the seq so a later stale copy is rejected.
                    Debug.Log("[Multiplayer][tac] tac.item.destroy: item already gone (def=" + o.ItemDefGuid + " idx=" + o.DefIndex +
                              " actor=" + o.ActorNetId + "/" + o.Slot + ") — skip");
                    TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacItemDestroy, o.Seq);
                    return;
                }

                // Native Destroy() removes it from the inventory (base.InventoryComponent.RemoveItem) and tears down the
                // holstered visual — the faithful mirror of the host's destroy.
                AccessTools.Method(item.GetType(), "Destroy", Type.EmptyTypes)?.Invoke(item, null);
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacItemDestroy, o.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.item.destroy seq=" + o.Seq + " actorNetId=" + o.ActorNetId +
                          " slot=" + o.Slot + " def=" + o.ItemDefGuid + " defIndex=" + o.DefIndex);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientOnItemDestroy failed: " + ex); }
        }

        // ─── reflection helpers ───────────────────────────────────────────────────────────────────────
        /// <summary>Which of the actor's two inventories holds the item — 1 when the item's InventoryComponent IS the
        /// actor's Equipments (ready slots), else 0 (backpack). Identical shape to
        /// <c>TacticalInventorySync.ResolveEndpointId</c>.</summary>
        private static byte SlotFor(object actor, object inv)
        {
            object equipments = GetProp(actor, "Equipments");
            return equipments != null && ReferenceEquals(equipments, inv) ? SlotEquipments : SlotInventory;
        }

        /// <summary>Index of <paramref name="item"/> among items of <paramref name="guid"/> in the inventory's CURRENT
        /// (pre-removal) contents, or -1 if not found. Mirrors <c>TacticalInventorySync.IndexAmongDef</c>.</summary>
        private static int IndexAmongDef(object inv, object item, string guid)
        {
            if (!(GetProp(inv, "Items") is IEnumerable items)) return -1;
            int seen = 0;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (string.Equals(DefReflection.GetGuid(GetProp(it, "ItemDef")), guid, StringComparison.Ordinal))
                {
                    if (ReferenceEquals(it, item)) return seen;
                    seen++;
                }
            }
            return -1;
        }

        /// <summary>The <paramref name="index"/>-th item of <paramref name="guid"/> in the inventory's CURRENT
        /// contents, or null. Mirrors <c>TacticalInventorySync.NthItemOfDef</c>.</summary>
        private static object NthItemOfDef(object inv, string guid, int index)
        {
            if (!(GetProp(inv, "Items") is IEnumerable items)) return null;
            int seen = 0;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (string.Equals(DefReflection.GetGuid(GetProp(it, "ItemDef")), guid, StringComparison.Ordinal))
                {
                    if (seen == index) return it;
                    seen++;
                }
            }
            return null;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }
    }

    /// <summary>Thin Harmony glue: HOST prefix on <c>TacticalItem.Destroy()</c> broadcasts the throwable/consumable
    /// removal so peers drop the phantom (<see cref="TacticalItemDestroySync"/>). A void prefix — it never skips the
    /// native destroy and never affects the sibling <see cref="Multiplayer.Harmony.Tactical.ThrowableDestroyGuardPatch"/>
    /// (that guard runs only on the client-replay side; on the host nothing skips, so this always fires). Reads the
    /// inventory BEFORE the native RemoveItem so the pre-removal def-index is stable. Auto-register via PatchAll;
    /// reflection target so a method-rename can't hard-crash bootstrap.</summary>
    [HarmonyPatch]
    public static class TacticalItemDestroyBroadcastPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.TacticalItem");
            if (t == null) return false;
            // public override List<Addon> Destroy() — single zero-arg override (the same chokepoint the throwable
            // guard patches), the ONE funnel every throwable/consumable destruction passes through.
            _target = AccessTools.Method(t, "Destroy", new Type[0]);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Prefix(object __instance)
        {
            try { TacticalItemDestroySync.HostBroadcastItemDestroy(__instance); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] TacticalItemDestroyBroadcastPatch failed: " + ex); }
        }
    }
}
