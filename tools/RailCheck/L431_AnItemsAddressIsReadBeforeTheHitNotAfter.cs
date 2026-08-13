using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L431 — A CARRIED ITEM'S ADDRESS IS READ BEFORE THE HIT, NEVER AFTER IT.
    ///
    /// THE MEASURED FAILURE (live 3-instance co-op, 2026-08-13). The host's post-mission panel listed NOTHING
    /// recovered while both clients listed an <c>NJ_Gauss_AssaultRifle_WeaponDef</c>. The host was RIGHT and the
    /// clients were wrong, and the whole divergence is one dropped record:
    ///   • host Player.log frame 19675 — "Item NJ_Gauss_AssaultRifle_WeaponDef destroyed on Soldier_8": the
    ///     rifle's own hit points reached zero IN COMBAT, 0.11 s before that enemy died (same frame 21:24:02.284).
    ///   • host multiplayer.log, same instant — "[tac] damage to an ownerless Weapon
    ///     'NJ_Gauss_AssaultRifle_WeaponDef' is NOT relayed — it has no shared key". The killing hit on an item
    ///     is the ONE hit whose address is gone by the time a postfix asks for it:
    ///     <c>TacticalItem.ApplyDamage</c> → zero health → <c>OnHealthReachedZero</c>:662-665 →
    ///     <c>Destroy</c>:540-570, whose :560 is <c>InventoryComponent?.RemoveItem(this)</c>. After that
    ///     <c>Item.Actor</c> is null and <c>ParentItemSlot</c> is null, so <c>GetActor()</c> answers null and
    ///     <c>TacticalActorKey.SlotOf</c> answers "" — for an item that was mounted on a keyed actor a
    ///     microsecond earlier.
    ///   • both clients, at the enemy's death — "the host's corpse manifest for actor -826923094 has no answer
    ///     left for item def '6b391a34-…'" then "this peer KEEPS it". Correct on their side: the host's manifest
    ///     never named the rifle because the host had already destroyed it, and the client still had one to ask
    ///     about only because the destroying hit never arrived. Same shape again at 21:26:52 for Soldier_9.
    ///   • end of mission — "[MP][outcome] HOST mission reward seq=1 res=0 items=0", against two rifles sitting
    ///     on the clients' ground. <c>GeoMission.ManageGear</c>:868-873 recovers <c>GetItemsOnTheGround</c>, so
    ///     the loot panel is a direct readout of this divergence.
    ///
    /// THE RULE: the seam that captures item damage must read the item's address in its PREFIX — while the item
    /// is still mounted — and the postfix must use THAT, never re-derive it from an item the game may already
    /// have detached. This is not the ownerless-receiver refusal L359 protects: that refusal stays, and stays
    /// loud, for a receiver that genuinely never had an address. This law is about the address that EXISTED and
    /// was thrown away by reading it one call too late.
    ///
    /// WHY A LAW: the refusal was loud, printed once by <c>SayOnce</c>, and read as "a known unaddressable
    /// case" for five days — while it was silently deciding what every peer recovers from every battle. A
    /// postfix-only address is a one-line regression with no failure of its own.
    ///
    /// NOT A QUORUM (P13), and no peer waits for anything: this only changes which bytes the HOST emits.
    ///
    /// Falsify: revert <c>ItemDamageSeam.Postfix</c> to <c>var slot = __instance.ParentItemSlot</c> and pass
    /// nothing on → (a) address-read-after-the-hit + (b) postfix-rereads-the-erased-address; drop the
    /// <c>ItemSlot</c> parameter from <c>OnDamageApplied</c> → (c) capture-cannot-be-told-the-address; stop
    /// reading the captured slot inside it → (c) captured-address-unused.
    /// </summary>
    internal static class L431_AnItemsAddressIsReadBeforeTheHitNotAfter
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalDamageSync);
            var seam = sync.Assembly.GetType("Multiplayer.Tactical.ItemDamageSeam");
            var prefix = seam == null ? null : seam.GetMethod("Prefix", All);
            var postfix = seam == null ? null : seam.GetMethod("Postfix", All);
            var shipped = sync.GetMethod("OnDamageApplied", All);
            if (seam == null || prefix == null || postfix == null || shipped == null)
            {
                // GUARD. Every arm is a statement about these four members. Without them the law asks nothing
                // at all, and the thing it stops asking about is which items a battle leaves on the ground.
                yield return "L431 premise-changed: ItemDamageSeam.Prefix / ItemDamageSeam.Postfix / " +
                             "TacticalDamageSync.OnDamageApplied no longer resolves. Whether a carried item's " +
                             "address survives the hit that destroys it is then unchecked — and that address " +
                             "decides whether every peer recovers the same loot from a battle.";
                yield break;
            }

            // ── (a) THE PREFIX CAPTURES THE ADDRESS ─────────────────────────────────
            var held = prefix.GetParameters().FirstOrDefault(p => p.Name == "__state");
            if (held == null || !held.IsOut || held.ParameterType != typeof(ItemSlot).MakeByRefType())
                yield return "L431 address-read-after-the-hit: ItemDamageSeam.Prefix does not hand an " +
                             "`out ItemSlot __state` to its postfix, so the only address the capture can have is " +
                             "one read AFTER the damage landed. For the hit that takes an item to zero the game " +
                             "has already run Destroy → InventoryComponent.RemoveItem by then, and the record is " +
                             "refused as ownerless — the 2026-08-13 loot inversion exactly.";

            var carried = postfix.GetParameters().FirstOrDefault(p => p.Name == "__state");
            if (carried == null || carried.ParameterType != typeof(ItemSlot))
                yield return "L431 postfix-drops-the-address: ItemDamageSeam.Postfix takes no `ItemSlot __state`, " +
                             "so whatever the prefix captured is discarded before anything can be shipped with it.";

            // ── (b) AND THE POSTFIX DOES NOT GO BACK TO THE ERASED ONE ──────────────
            // The routing decision AND the address both have to come from the captured slot: TacticalItem
            // .ApplyDamage:301-313 routes on the parent slot, and a destroyed item has none, so a re-read
            // answers "no parent slot" for an item that had one.
            if (Program.CalleeSequence(postfix).Any(c => c != null && c.Name == "get_ParentItemSlot"))
                yield return "L431 postfix-rereads-the-erased-address: ItemDamageSeam.Postfix reads " +
                             "TacticalItem.ParentItemSlot itself. That property is null for exactly the hit this " +
                             "law is about — the one that destroyed the item — so the capture is back to " +
                             "answering '' for a slot that existed when the damage was computed.";

            // ── (c) THE CAPTURE CAN BE TOLD THE ADDRESS, AND READS IT ───────────────
            var takes = shipped.GetParameters();
            if (!takes.Any(p => p.ParameterType == typeof(ItemSlot)))
                yield return "L431 capture-cannot-be-told-the-address: TacticalDamageSync.OnDamageApplied takes " +
                             "no ItemSlot, so a prefix-captured address has nowhere to go and the ship path is " +
                             "left deriving one from a receiver the game may already have detached.";
            else
            {
                var reads = Program.CalleeSequence(shipped);
                bool actor = reads.Any(c => c != null && c.Name == "GetActor" && c.DeclaringType == typeof(ItemSlot));
                bool name = reads.Any(c => c != null && c.Name == "GetSlotName" && c.DeclaringType == typeof(ItemSlot));
                if (!actor || !name)
                    yield return "L431 captured-address-unused: OnDamageApplied accepts the captured ItemSlot but " +
                                 "never asks it for " + (actor ? "GetSlotName" : "GetActor") + ". BOTH halves are " +
                                 "erased by the destroy — the actor key AND the slot name — and a record missing " +
                                 "either one is refused just as completely as one missing both.";
            }

            // ── POSITIVE CONTROL: the detector CAN find get_ParentItemSlot ──────────
            // It is the prefix that legitimately reads it. If the walker cannot see it there, arm (b) would be
            // green over a postfix that re-reads the erased address on every line.
            if (!Program.CalleeSequence(prefix).Any(c => c != null && c.Name == "get_ParentItemSlot"))
                yield return "L431 control-failed: the IL walker cannot find TacticalItem.ParentItemSlot in " +
                             "ItemDamageSeam.Prefix, which is the one place that must read it. Arm (b) therefore " +
                             "cannot tell a postfix that re-derives the erased address from one that does not.";
        }
    }
}
