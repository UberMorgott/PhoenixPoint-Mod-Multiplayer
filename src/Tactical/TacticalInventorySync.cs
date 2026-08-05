using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Defs;
using Base.Entities;
using Base.Utils.Maths;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.View.ViewControllers.Inventory;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// TACTICAL ARC A6, PART 1 — INVENTORY AND LOOT: rearranging a soldier in battle, handing a magazine to
    /// the man next to him, picking a rifle off the ground, emptying a crate or a corpse.
    ///
    /// THE COMMIT IS THE WHOLE BATCH, BECAUSE THE GAME'S IS. Dragging an item in the tactical inventory
    /// screen does NOT touch the model: <c>UIStateInventory.AttemptMoveItems</c>:739-751 pushes every gesture
    /// into a per-inventory <c>InventoryQuery</c>, which is pure staging — <c>InventoryQuery.AddItem</c>:26-33
    /// only edits a private list. The model moves in exactly one place,
    /// <c>InventoryQuery.SyncItems</c>:44-67, which drains every query into the real
    /// <c>InventoryComponent.AddItem/RemoveItem</c>. That static has ONE caller in the whole assembly —
    /// <c>UIStateInventory.ApplyInventoryActions</c>:898-903, reached only from <c>ExitState</c>:443. So the
    /// question the architect asked is answered by the game itself: there is no per-slot model funnel to ride,
    /// the per-slot calls are staging, and the batch is the transaction. This matches the geoscape precedents
    /// <c>GeoEquipIntent</c> op <c>setItems=8</c> and <c>GeoVehicleIntent setEquipment</c> for the same
    /// structural reason rather than by analogy.
    ///
    /// AP IS CHARGED ONCE, BY THE GAME, WHERE THE GAME CHARGES IT (law L69a). v1 (`6617846`) deducted AP
    /// eagerly on the first gesture; the native per-drag gate is
    /// <c>UIStateInventory.CanPayForTransfer</c>:560-571 → <c>ActionPointRequirementSatisfied</c>, so the
    /// eager deduction made the game refuse every FURTHER drag and the screen locked up. The real charge is a
    /// single <c>ApplyCostsCommand</c> (<c>UIStateInventory</c>:222-242 → <c>InventoryAbility.ApplyCosts</c>)
    /// run at most once per session — <c>ShouldApplyCosts</c>:573-591 returns false once
    /// <c>_alreadyPaidAbility</c> is set, which is what makes the secondary-soldier switch
    /// (<c>OnSelectedSecondarySoldier</c>:758-764) free. THIS ARC NEVER CALLS ApplyCosts ON AN ACTING PEER AT
    /// ALL: it only OBSERVES the native charge in a postfix and carries the resulting number. The one place
    /// it does charge is the HOST applying a client's committed batch, where the client's own screen has
    /// already closed — still exactly once, still the game's own method, still at the moment the session
    /// ended.
    ///
    /// NO NEW SURFACE. Host→all is op <see cref="TacticalDamageSync.OpInventory"/> on 0x84 (the discrete
    /// event stream: a hand-off must not be overtaken by the shot fired with the handed weapon) and
    /// client→host is op <see cref="OpIntentInventory"/> on the 0x83 intent family that already carries
    /// commands and resnapshot requests. 0x85 stays free.
    ///
    /// v1's <c>TacCrateOpen</c> AND <c>TacItemDestroy</c> ARE BOTH GONE, folded rather than ported. Opening a
    /// crate is <c>OpenCrateAbility</c>, an ordinary <c>TacticalAbility.Activate</c> that A3a already mirrors,
    /// so the lid animation is native on every peer; the crate's CONTENTS are just containers in this batch.
    /// A consumed grenade or medkit leaves its owner's inventory, and "left every container in the batch" is
    /// how this payload says destroyed — there is nothing left to name with a surface of its own.
    ///
    /// LOOT IS THE HOST'S ROLL AND IS NOT RE-ROLLED (law L69c). A corpse's contents were already decided by
    /// A4: <c>DieAbility.ShouldDestroyItem</c> is answered from <see cref="LootMirror"/>'s host manifest, and
    /// <c>TFTVEconomyExploitsFixes</c>:130 overrides that same roll — a host-only method under A4's gate, so
    /// TFTV's version runs exactly once, on the host, and rides out in the manifest like vanilla's. This arc
    /// only MOVES what is already in the corpse; it never asks what should be in it.
    ///
    /// WHAT THE PAYLOAD IS. Per container: <c>(actorKey, kind)</c> where kind names the actor's own
    /// <c>Inventory</c> or <c>Equipments</c>, or <see cref="KindGround"/> — a pile of dropped items, addressed
    /// by the GAME'S OWN container identity <c>(ComponentSetDef guid, Pos)</c> because such a pile has no actor
    /// key to carry and never could have one (see the constant) — plus the ordered items in it, each as a DEF
    /// GUID and its <see cref="ChargeOf"/>. Items carry no shared
    /// identity (A4 had to solve the same problem for the corpse manifest and reached the same answer), and
    /// they do not need one here: a committed batch only ever MOVES items between the containers it names, so
    /// the multiset of defs across those containers is invariant, and the host CHECKS that invariant before
    /// accepting a client's batch. That check is the trust boundary — it is what stops a client conjuring a
    /// second Hel Cannon by editing a layout.
    ///
    /// KNOWN CEILING: order WITHIN a container is not forced, only membership. The native lists are re-sorted
    /// by the UI's own filters on display, and forcing order would mean removing and re-adding items that
    /// never moved — which for <c>EquipmentComponent</c> means unmounting and remounting a weapon that was
    /// never touched. WHAT THAT USED TO COST, AND NO LONGER DOES: because only membership crossed, two
    /// magazines of one def were interchangeable to this batch and it moved whichever THIS peer's list held
    /// first — a full clip on the host, a spent one here. The per-item CHARGE now rides beside each def guid
    /// and <see cref="ChargeOf"/>/<see cref="OrdinalOf"/>/<see cref="ResolveIn"/> are the ONE address rule
    /// this file and A7's target codec both use, so same-def items resolve apart WITHOUT forcing an order on
    /// anything. The charge deliberately stays out of <see cref="ContentsDiff"/> — that multiset is the trust
    /// boundary against a client conjuring items, and a charge that ticked mid-batch is not that.
    /// </summary>
    public static class TacticalInventorySync
    {
        /// <summary>Op 3 of the 0x83 intent family (1 = command, 2 = resnapshot request).</summary>
        internal const byte OpIntentInventory = 3;

        private const byte KindInventory = 0;
        private const byte KindEquipments = 1;
        /// <summary>A6's CLOSED CEILING — a pile of items lying on the ground. It gets a kind of its own rather
        /// than an actor key because the container has no shared identity to key: it is created LOCALLY, by
        /// whichever peer's screen dropped into it (<c>UIStateInventory.CreateGroundInventory</c>:513-522, run on
        /// EVERY inventory open) or by a death every peer mirrors separately (<c>DieAbility.DropItems</c>:181 →
        /// <c>TacticalItem.Drop</c>:506-537). No host-assigned key could name the one a CLIENT made, and A4's
        /// spawn record could not either — the client's screen needs the pile the instant it drops, long before
        /// any host round trip. The address is instead THE GAME'S OWN:
        /// <c>TacticalItem.GetOrCreateItemContainer</c>:668-690 finds a dropped-item container by
        /// <c>Utl.Equals(actor.Pos, pos) &amp;&amp; actor.ActorDef == def</c> and spawns one with exactly that
        /// recipe when there is none. So a pile is named by where it lies and what it is — stable, symmetric,
        /// nothing to assign and nothing to adopt.</summary>
        private const byte KindGround = 2;

        /// <summary>The acting peer's observed native charge, waiting for the batch it belongs to. Set by the
        /// <see cref="InventoryCostSeam"/> postfix, consumed by the commit. Never a deduction of our own.</summary>
        private static int _payerKey;
        private static bool _charged;
        private static readonly HashSet<string> _said = new HashSet<string>(StringComparer.Ordinal);
        private static FieldInfo _linked;

        internal static void Reset()
        {
            _payerKey = 0;
            _charged = false;
            _said.Clear();
        }

        private static void SayOnce(string key, string message)
        {
            if (_said.Add(key)) Debug.LogError(message);
        }

        // ─── THE ADDRESS ───────────────────────────────────────────────────

        /// <summary>One container on the wire: whose it is, which of that actor's two it is, and what is in
        /// it. A plain class rather than a tuple so the codec and the validator can be read side by side.</summary>
        internal sealed class Slot
        {
            internal int ActorKey;
            internal byte Kind;
            /// <summary><see cref="KindGround"/> only: the container's own <c>ComponentSetDef</c> guid and the
            /// position it lies at — the game's own container identity, in place of an actor key.</summary>
            internal string SetGuid;
            internal Vector3 Pos;
            internal List<string> ItemDefs = new List<string>();
            /// <summary>Parallel to <see cref="ItemDefs"/>, one <see cref="ChargeOf"/> per item. It rides so
            /// that a batch moving ONE of two same-def magazines moves the RIGHT one; it deliberately stays
            /// OUT of <see cref="ContentsDiff"/>, which keeps checking def guids alone — the multiset check
            /// is the trust boundary against a client conjuring items, and a charge that ticked between
            /// capture and apply is not that.</summary>
            internal List<int> ItemCharges = new List<int>();
        }

        /// <summary>The <c>InventoryComponent</c> behind a query. <c>_linkedInventory</c> is private and there
        /// is no accessor, so this is the arc's ONE reflection site; a failure to resolve it is loud, because
        /// silently shipping an empty batch is exactly the swallow class this repo keeps paying for.</summary>
        private static InventoryComponent LinkedOf(InventoryQuery query)
        {
            if (query == null) return null;
            if (_linked == null)
            {
                _linked = AccessTools.Field(typeof(InventoryQuery), "_linkedInventory");
                if (_linked == null)
                {
                    SayOnce("linked-field",
                        "[Multiplayer][tac] InventoryQuery._linkedInventory did not resolve — no inventory change " +
                        "can be named on the wire, so every item any peer moves in battle stays on that peer's " +
                        "screen alone.");
                    return null;
                }
            }
            return _linked.GetValue(query) as InventoryComponent;
        }

        /// <summary>Which of the owning actor's containers this is. Compared BY REFERENCE against the actor's
        /// own two rather than by component type: <c>EquipmentComponent</c> derives from
        /// <c>InventoryComponent</c>, so a type test would answer "inventory" for a weapon slot.</summary>
        internal static bool AddressOf(InventoryComponent component, out int actorKey, out byte kind)
        {
            actorKey = 0;
            kind = KindInventory;
            if (component == null) return false;
            var actor = component.Actor as TacticalActorBase;
            if (actor == null) return false;
            actorKey = TacticalActorKey.Of(actor);
            if (actorKey == 0) return false;
            if (ReferenceEquals(component, actor.Inventory)) { kind = KindInventory; return true; }
            var tac = actor as TacticalActor;
            if (tac != null && ReferenceEquals(component, tac.Equipments)) { kind = KindEquipments; return true; }
            return false;
        }

        internal static InventoryComponent ContainerOf(TacticalActorBase actor, byte kind)
        {
            if (actor == null) return null;
            if (kind == KindInventory) return actor.Inventory;
            var tac = actor as TacticalActor;
            return tac == null ? null : tac.Equipments;
        }

        /// <summary>EVERY pile at this address, because <c>(ComponentSetDef guid, Pos)</c> names a SET and not a
        /// singleton — the game itself puts more than one pile on a tile. <c>UIStateInventory</c>:513-522 spawns a
        /// fresh container under the primary soldier on EVERY inventory open WITHOUT the find-or-create
        /// <c>TacticalItem.GetOrCreateItemContainer</c>:676-684 does, so a soldier who opens his kit while standing
        /// on the pile he dropped last time leaves two; and that same find is skipped outright for a
        /// <c>TacticalItemDef.IndividualContainerWhenDropped</c> item. Returning only the first was the 2026-08-01
        /// failure: two slots resolved onto ONE container, its contents were counted twice, and the multiset check
        /// then refused (or misapplied) every batch made after the first ground drop.
        ///
        /// Empty when there is no such pile and <paramref name="create"/> is false — a slot that ends EMPTY names a
        /// pile only so its items can be moved OUT of it, and spawning one for that would litter every peer's map
        /// with the container <c>UIStateInventory</c> makes and unmakes on every single inventory open.</summary>
        private static List<InventoryComponent> GroundPiles(TacticalLevelController tlc, string setGuid, Vector3 pos,
                                                            bool create)
        {
            var found = new List<InventoryComponent>();
            if (tlc == null || tlc.Map == null) return found;
            var repo = GameUtl.GameComponent<DefRepository>();
            var setDef = string.IsNullOrEmpty(setGuid) || repo == null ? null : repo.GetDef(setGuid) as ComponentSetDef;
            var containerDef = setDef == null ? null : setDef.Components.OfType<ItemContainerDef>().FirstOrDefault();
            if (containerDef == null)
            {
                SayOnce("ground-def-" + setGuid,
                    "[Multiplayer][tac] the container def '" + setGuid + "' a dropped-item pile is made of does not " +
                    "resolve on this peer, so every item dropped onto bare ground stays in its owner's pack here.");
                return found;
            }
            // The game's own lookup, verbatim (TacticalItem.GetOrCreateItemContainer:676-684) — but exhaustive.
            foreach (var c in tlc.Map.GetActors<ItemContainer>())
                if (Utl.Equals(c.Pos, pos) && c.ActorDef == containerDef) found.Add(c.Inventory);
            if (found.Count > 0 || !create) return found;
            // ...and the game's own spawn recipe when there is none (:685-690, UIStateInventory:516-521).
            var data = containerDef.CreateInstanceData();
            data.OverrideTransform = true;
            data.Pos = pos;
            data.Rot = Quaternion.identity;
            data.Source = setDef;
            var spawned = ActorSpawner.SpawnActor<ItemContainer>(setDef, data);
            if (spawned == null)
            {
                SayOnce("ground-spawn-" + setGuid,
                    "[Multiplayer][tac] the game's own spawner returned nothing for a dropped-item pile at " + pos +
                    " — the items the host dropped there stay in their owner's pack on this screen.");
                return found;
            }
            Debug.Log("[Multiplayer][tac] mirrored a pile of dropped items at " + pos + ".");
            found.Add(spawned.Inventory);
            return found;
        }

        /// <summary>The acting peer's half of the same address: an <c>ItemContainer</c> with no shared key is a
        /// pile somebody dropped, and it is named by its def and its position instead of being dropped from the
        /// batch (which is what made a ground drop invisible everywhere but the screen that made it).</summary>
        private static bool GroundSlot(InventoryComponent component, out Slot slot)
        {
            slot = null;
            var container = component == null ? null : component.Actor as ItemContainer;
            if (container == null || !ReferenceEquals(component, container.Inventory)) return false;
            var setDef = container.Source as ComponentSetDef;
            if (setDef == null)
            {
                var set = container.GetComponent<ComponentSet>();
                setDef = set == null ? null : set.SetDef;
            }
            if (setDef == null) return false;
            slot = new Slot { ActorKey = 0, Kind = KindGround, SetGuid = setDef.Guid, Pos = container.Pos };
            return true;
        }

        // ─── CODEC ─────────────────────────────────────────────────────────

        internal static void WriteLayout(BinaryWriter w, List<Slot> slots, int payerKey, float payerAp, bool charged,
                                         bool partial)
        {
            w.Write(payerKey);
            w.Write(payerAp);
            w.Write((byte)(charged ? 1 : 0));
            w.Write((byte)(partial ? 1 : 0));
            w.Write(slots.Count);
            foreach (var s in slots)
            {
                w.Write(s.ActorKey);
                w.Write(s.Kind);
                if (s.Kind == KindGround)
                {
                    w.Write(s.SetGuid ?? "");
                    w.Write(s.Pos.x); w.Write(s.Pos.y); w.Write(s.Pos.z);
                }
                w.Write(s.ItemDefs.Count);
                for (int i = 0; i < s.ItemDefs.Count; i++)
                {
                    w.Write(s.ItemDefs[i] ?? "");
                    w.Write(i < s.ItemCharges.Count ? s.ItemCharges[i] : -1);
                }
            }
        }

        internal static List<Slot> ReadLayout(BinaryReader r, out int payerKey, out float payerAp, out bool charged,
                                              out bool partial)
        {
            payerKey = r.ReadInt32();
            payerAp = r.ReadSingle();
            charged = r.ReadByte() != 0;
            partial = r.ReadByte() != 0;
            int n = r.ReadInt32();
            var slots = new List<Slot>(n);
            for (int i = 0; i < n; i++)
            {
                var s = new Slot { ActorKey = r.ReadInt32(), Kind = r.ReadByte() };
                if (s.Kind == KindGround)
                {
                    s.SetGuid = r.ReadString();
                    s.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }
                int items = r.ReadInt32();
                for (int j = 0; j < items; j++) { s.ItemDefs.Add(r.ReadString()); s.ItemCharges.Add(r.ReadInt32()); }
                slots.Add(s);
            }
            return slots;
        }

        // ─── THE VALIDATOR ─────────────────────────────────────────────────

        /// <summary>The whole acceptance decision as a PURE function — null = accept, otherwise the human
        /// reason. Same shape and same reason as <see cref="TacticalCommandSync.Validate"/>: the race and the
        /// trust boundary are then testable headless (RailCheck L69).
        ///
        /// <paramref name="contentsPreserved"/> is the trust boundary and not a sanity check. A committed
        /// batch only MOVES items between the containers it names, so the multiset of item defs across them
        /// cannot change; a batch where it did is either a client inventing an item or two peers editing the
        /// same crate at once, and both must be refused rather than applied.</summary>
        internal static string Validate(bool everyContainerResolved, string firstUnresolved,
                                        bool contentsPreserved, string contentsDiff,
                                        bool payerFound, bool payerCanAfford, bool charged)
        {
            if (!everyContainerResolved)
                return "the host cannot find " + (firstUnresolved ?? "one of the named containers");
            if (!contentsPreserved)
                return "the batch does not hold the same items it started with (" +
                       (contentsDiff ?? "contents differ") + ") — a commit only MOVES items between the " +
                       "containers it names, so this one either invents an item or races another peer's";
            if (charged && !payerFound)
                return "the batch charges an actor the host cannot find";
            if (charged && !payerCanAfford)
                return "that soldier cannot afford the inventory action on the host — another peer spent the " +
                       "action points first (first-to-act-wins)";
            return null;
        }

        /// <summary>The multiset comparison behind <c>contentsPreserved</c>, kept separate and pure so the
        /// harness can falsify it directly. Returns null when the two lists hold the same defs in any order,
        /// otherwise a human description of the first discrepancy.</summary>
        internal static string ContentsDiff(List<string> before, List<string> after)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var g in before)
            {
                int c;
                counts[g ?? ""] = counts.TryGetValue(g ?? "", out c) ? c + 1 : 1;
            }
            foreach (var g in after)
            {
                int c;
                string k = g ?? "";
                if (!counts.TryGetValue(k, out c) || c == 0) return "item def '" + k + "' appears from nowhere";
                counts[k] = c - 1;
            }
            foreach (var kv in counts)
                if (kv.Value > 0) return kv.Value + " × item def '" + kv.Key + "' vanished";
            return null;
        }

        // ─── THE APPLY, shared by the host's intent path and every peer's delta ───

        /// <summary>Move the named containers' contents to the layout, through the game's own
        /// <c>InventoryComponent.AddItem/RemoveItem</c> (the same virtuals <c>InventoryQuery.SyncItems</c>
        /// drains into, so an <c>EquipmentComponent</c> still mounts and unmounts natively). Items already in
        /// the right container are LEFT ALONE — re-adding a weapon that never moved would unmount and remount
        /// it. Returns null on success, or the refusal reason.</summary>
        internal static string ApplyLayout(List<Slot> slots, bool validateContents)
        {
            var tlc = TacticalDamageSync.Tlc();
            var targets = new List<InventoryComponent>(slots.Count);
            // Every container a slot POOLS from. It is not `targets`: a ground slot pools from all the piles at its
            // address and adds to the first of them (see GroundPiles).
            var pooled = new List<InventoryComponent>(slots.Count);
            string firstUnresolved = null;

            foreach (var s in slots)
            {
                if (s.Kind == KindGround)
                {
                    // An empty ground slot is "take everything OUT of that pile" — it must FIND one, never make one.
                    var piles = GroundPiles(tlc, s.SetGuid, s.Pos, create: s.ItemDefs.Count > 0);
                    if (piles.Count == 0 && firstUnresolved == null && s.ItemDefs.Count > 0)
                        firstUnresolved = "the pile of dropped items at " + s.Pos +
                                          " (this peer can neither find nor build one)";
                    targets.Add(piles.Count == 0 ? null : piles[0]);
                    pooled.AddRange(piles);
                    continue;
                }
                string why;
                var actor = TacticalActorKey.Resolve(tlc, s.ActorKey, out why);
                var component = ContainerOf(actor, s.Kind);
                if (component == null && firstUnresolved == null)
                    firstUnresolved = (s.Kind == KindEquipments ? "the equipment slots" : "the backpack") +
                                      " of actor " + s.ActorKey + " (" + (actor == null ? why : "that actor has no such container") + ")";
                targets.Add(component);
                if (component != null) pooled.Add(component);
            }

            // The pool is every item currently sitting in the named containers, with where it sits now.
            var pool = new List<KeyValuePair<Item, InventoryComponent>>();
            var before = new List<string>();
            foreach (var c in pooled)
            {
                foreach (var item in c.Items)
                {
                    pool.Add(new KeyValuePair<Item, InventoryComponent>(item, c));
                    before.Add(DefGuid(item));
                }
            }
            var after = new List<string>();
            foreach (var s in slots) after.AddRange(s.ItemDefs);

            string diff = ContentsDiff(before, after);
            string refusal = Validate(firstUnresolved == null, firstUnresolved,
                                      !validateContents || diff == null, diff,
                                      true, true, false);
            if (refusal != null) return refusal;

            // A pile emptied by these moves destroys ITSELF — ItemContainer.OnItemRemovedFromContainer:93-96 is
            // the game's own DestroyWhenEmpty rule, and letting it run is what makes a picked-up pile disappear
            // on every peer with nothing of ours doing the destroying (law L67). An emptied pile is only ever a
            // SOURCE in this loop, so it is never dereferenced after it goes.
            int moved = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var target = targets[i];
                if (target == null) continue;
                for (int k = 0; k < slots[i].ItemDefs.Count; k++)
                {
                    string guid = slots[i].ItemDefs[k];
                    int charge = k < slots[i].ItemCharges.Count ? slots[i].ItemCharges[k] : -1;
                    // THE SAME ADDRESS RULE AS ItemAddress, applied to a whole batch: the def alone made two
                    // magazines of one type interchangeable, so a batch moving ONE of them moved whichever
                    // this peer's list happened to hold first — a full clip on the host, a spent one here.
                    // The charge is preferred; a def-only match is still taken (a charge that ticked between
                    // the sender's capture and this apply must not strand an item) but only after every
                    // exact one is spoken for.
                    int found = -1, inPlace = -1, foundLoose = -1, inPlaceLoose = -1;
                    for (int p = 0; p < pool.Count; p++)
                    {
                        if (pool[p].Key == null || DefGuid(pool[p].Key) != guid) continue;
                        bool here = ReferenceEquals(pool[p].Value, target);
                        bool exact = ChargeOf(pool[p].Key) == charge;
                        if (exact && here) { inPlace = p; break; }
                        if (exact) { if (found < 0) found = p; }
                        else if (here) { if (inPlaceLoose < 0) inPlaceLoose = p; }
                        else if (foundLoose < 0) foundLoose = p;
                    }
                    // Only when no EXACT item exists anywhere does the def-only match get used at all.
                    if (inPlace < 0 && found < 0) { inPlace = inPlaceLoose; found = foundLoose; }
                    // Prefer an item that is ALREADY here: nothing to do, and nothing to unmount.
                    if (inPlace >= 0) { pool.RemoveAt(inPlace); continue; }
                    if (found < 0)
                    {
                        SayOnce("noitem-" + guid,
                            "[Multiplayer][tac] the host's inventory batch places item def '" + guid + "' that this " +
                            "peer cannot find in any of the named containers — that soldier's kit now differs from " +
                            "the host's for the rest of the mission.");
                        continue;
                    }
                    var entry = pool[found];
                    pool.RemoveAt(found);
                    entry.Value.RemoveItem(entry.Key);
                    target.AddItem(entry.Key);
                    moved++;
                }
            }
            foreach (var leftover in pool)
                SayOnce("orphan-" + DefGuid(leftover.Key),
                    "[Multiplayer][tac] item def '" + DefGuid(leftover.Key) + "' is in this peer's containers but " +
                    "in none of the host's — it is left where it is rather than guessed at, so this soldier " +
                    "carries something the host's does not.");
            if (moved > 0)
                Debug.Log("[Multiplayer][tac] applied an inventory batch: " + moved + " item(s) moved across " +
                          slots.Count + " container(s).");
            // LAW 11, and it is the whole reason the 2026-08-01 run looked dead (RCA below). This is the ONE
            // funnel every mirrored batch passes through — the host validating a client's intent and every peer
            // applying the settle — and it is reached with NO ability executed, so
            // TacticalUiRepaint's only other dirty source (its AbilityExecuted postfix, TacticalUiRepaint:175)
            // never fires for an inventory move. Model fresh, view stale: the observer's open
            // UIStateCharacterSelected kept painting the pre-drop kit and the pre-swap weapon, which is
            // indistinguishable from "nothing crossed". Unconditional on `moved`: a charge-only closing batch
            // moves nothing and still changes the AP the ability bar's affordability is baked from.
            TacticalUiRepaint.MarkDirty();
            return null;
        }

        private static string DefGuid(Item item)
        {
            var def = item == null ? null : item.ItemDef;
            // ReferenceEquals, not ==: BaseDef is a ScriptableObject, so `def == null` is Unity's overloaded
            // comparison and answers true for a def whose native side is gone as well as for a genuinely
            // absent one. Reading .Guid is a managed field read either way, and an item silently reported as
            // def-less is an item every address in this file then confuses with every other def-less item.
            return ReferenceEquals(def, null) ? "" : def.Guid;
        }

        // ─── THE ITEM ADDRESS RULE — declared ONCE, used by both riders ────

        /// <summary>THE per-item state an address can see, and the reason two identical clips are not
        /// actually identical: the CHARGE. <c>CommonItemData.CurrentCharges</c>:33 is
        /// <c>Ammo?.CurrentCharges ?? _charges</c> — for a magazine its own rounds, for a weapon the rounds
        /// in the magazine loaded into it — so it is exactly the field that makes "the half-empty clip" a
        /// different item from "the full one" while both share a def guid.
        ///
        /// -1 means "this kind of item has no charge at all" (a plain <c>Item</c> that is not a
        /// <c>TacticalItem</c>). It is deliberately not 0: an empty clip really is charge 0, and collapsing
        /// the two would put chargeless items in the same equivalence class as spent ones.</summary>
        internal static int ChargeOf(Item item)
        {
            var t = item as TacticalItem;
            var data = t == null ? null : t.CommonItemData;
            return data == null ? -1 : data.CurrentCharges;
        }

        /// <summary>How many items BEFORE this one, in its own container, share its (def, charge).
        ///
        /// This is what turns A7's ceiling — "the far side resolves the FIRST same-def item" — into a
        /// bounded one. The ordinal orders WITHIN an equivalence class whose members are identical in every
        /// field the address can see, so a peer whose container happens to be sorted differently picks a
        /// different member of THAT CLASS and never a different clip. The old address had no class at all:
        /// a full magazine and a spent one were one key, and "the first" was a coin toss between them.
        ///
        /// ponytail: it does NOT make the ordinal itself peer-stable, and it must not pretend to — forcing
        /// container order would mean unmounting a weapon nobody touched (the A6 ceiling above, still
        /// standing). What it makes peer-stable is the ANSWER, which is the only thing a caller reads.</summary>
        internal static int OrdinalOf(Item item)
        {
            var c = item == null ? null : item.InventoryComponent;
            // ReferenceEquals, not ==: InventoryComponent is a DefineableBehavior (a MonoBehaviour), whose
            // overloaded == also answers true for a DESTROYED one. The guard here is against a genuinely
            // absent container, and walking a managed item list needs no live native object.
            if (ReferenceEquals(c, null)) return 0;
            string guid = DefGuid(item);
            int charge = ChargeOf(item);
            int n = 0;
            foreach (var other in c.Items)
            {
                if (ReferenceEquals(other, item)) return n;
                if (other != null && DefGuid(other) == guid && ChargeOf(other) == charge) n++;
            }
            return n;
        }

        /// <summary>The inverse of <see cref="OrdinalOf"/>: the item a written address names, on THIS peer.
        /// Null when the container holds no such item at all — never a "closest match", because a reload
        /// aimed at the wrong weapon is the divergence this address exists to stop.</summary>
        internal static Item ResolveIn(InventoryComponent container, string guid, int charge, int ordinal)
        {
            if (ReferenceEquals(container, null)) return null;   // see OrdinalOf
            int n = 0;
            foreach (var it in container.Items)
                if (it != null && DefGuid(it) == guid && ChargeOf(it) == charge && n++ == ordinal) return it;
            // The charge DRIFTED — a reload or a shot landed on the sender and has not been applied here
            // yet. Fall back to the ordinal-th item of the DEF, which is still a DISTINCT item per ordinal:
            // the one thing that must never come back is "the first one" for every address at once.
            n = 0;
            foreach (var it in container.Items)
                if (it != null && DefGuid(it) == guid && n++ == ordinal) return it;
            return null;
        }

        // ─── THE CAPTURE ───────────────────────────────────────────────────

        /// <summary>Observe the native AP charge. A POSTFIX, and nothing else: the deduction has already
        /// happened, by the game, at the game's moment. Recording it is all this arc does with it.</summary>
        internal static void OnCostsApplied(InventoryAbility ability)
        {
            if (TacticalDamageSync.LiveEngine() == null) return;
            if (SyncApplyScope.Active) return;            // the host charging a client's committed batch
            var actor = ability == null ? null : ability.TacticalActorBase;
            int key = TacticalActorKey.Of(actor);
            if (key == 0)
            {
                SayOnce("payer-unkeyed",
                    "[Multiplayer][tac] an inventory action was charged to an actor with no shared key, so no " +
                    "other peer can be told — that soldier's AP is now higher everywhere else.");
                return;
            }
            _payerKey = key;
            _charged = true;
        }

        /// <summary>True once the game has charged the inventory action but no batch has carried that charge yet.
        /// The commit seam needs it because per-gesture flushing leaves the CLOSING commit with nothing left to
        /// move: every item already went, and the only thing the last batch has to say is "this cost a point".</summary>
        internal static bool HasPendingCharge => _charged;

        private static MethodInfo _attemptMove, _applyActions;
        private static PropertyInfo _equipModule;
        private static FieldInfo _listInitial;

        /// <summary>THE SECOND HALF OF A MID-SESSION DRAIN, and leaving it out is what walled the screen after one
        /// gesture (2026-08-01). The inventory screen keeps TWO baselines and the game takes them at the SAME
        /// instant, one line apart in <c>EnterState</c>: <c>ResetInventoryQueries</c>:312 baselines every
        /// <c>InventoryQuery</c> against the model, and <c>InitInventory</c>:330 →
        /// <c>UIInventoryList.Init</c>:443 baselines every UI list's <c>_initialItems</c> against the same
        /// arrangement. <c>AttemptMoveItems</c>:741-751 then stages the DIFFERENCE between them —
        /// <c>GetAddedItems</c>/<c>GetRemovedItems</c>:462-470 are <c>UnfilteredItems.Except(_initialItems)</c>,
        /// measured against the SESSION START.
        ///
        /// Our per-gesture flush advances only ONE of the two: <c>SyncItems</c>:61 re-<c>Reset</c>s every query to
        /// the freshly committed model, while <c>_initialItems</c> still describes the session start. The
        /// baselines drift apart by one gesture at every flush, and the moment a gesture RESTORES a session-start
        /// membership — putting a dropped item back in the pack, the second half of every drop/pick-up test — the
        /// UI delta is EMPTY. Nothing is staged, <c>WillModifyInventory</c> answers false on every query, the
        /// commit seam ships nothing (measured: <c>willModify=0</c> at 9798 s / 10374 s, no intent emitted, no
        /// host refusal), and the model keeps the item on the ground while that screen paints it in the pack —
        /// a divergence that survives the screen's own <c>ExitState</c> and is only cleared by REOPENING, which
        /// re-runs <c>Init</c>. Exactly the reported wall.
        ///
        /// So re-baseline the lists at the instant we commit, which is the invariant the game itself keeps: its
        /// OWN mid-session drain is a TWO-step sequence, <c>OnSelectedSecondarySoldier</c>:765-772 =
        /// <c>AttemptMoveItems()</c> followed by <c>InitInventory()</c>. We ran the drain and skipped the
        /// re-baseline. This writes <c>_initialItems</c> only (<c>Init</c> would also <c>SetItems</c>:447 and
        /// rebuild every slot mid-drag); it walks <c>CurrentInventoryLists</c>, the SAME collection
        /// <c>AttemptMoveItems</c> iterates, so the next gesture's delta is exactly that gesture. A COPY, never
        /// the live list — aliasing <c>UnfilteredItems</c> would make every future delta empty.</summary>
        private static void Rebaseline(UIStateInventory state)
        {
            if (_equipModule == null)
            {
                _equipModule = AccessTools.Property(typeof(UIStateInventory), "_soldierEquipModule");
                _listInitial = AccessTools.Field(typeof(UIInventoryList), "_initialItems");
                if (_equipModule == null || _listInitial == null)
                {
                    SayOnce("rebaseline-members",
                        "[Multiplayer][tac] UIStateInventory._soldierEquipModule / UIInventoryList._initialItems did " +
                        "not resolve, so the inventory screen's two baselines cannot be kept together — only the " +
                        "FIRST gesture of each inventory session will reach the other peers.");
                    return;
                }
            }
            var module = _equipModule.GetValue(state, null) as UIModuleSoldierEquip;
            if (module == null) return;
            foreach (var list in module.CurrentInventoryLists)
                if (list != null && list.UnfilteredItems != null)
                    _listInitial.SetValue(list, new List<ICommonItem>(list.UnfilteredItems));
        }

        /// <summary>PER-GESTURE COMMIT (law L69, 2026-08-01 — the batch is still the batch, it is just drained
        /// every gesture instead of once at close). The game stages a drag in the UI LISTS, not even in the query:
        /// <c>UIStateInventory.AttemptMoveItems</c>:739-751 (UI lists → queries) and
        /// <c>ApplyInventoryActions</c>:898-903 (queries → model) both run only from <c>ExitState</c>:442-443, so
        /// until the screen closes NOTHING has moved and there is nothing to replicate — which is exactly what
        /// every other peer saw. This runs the game's OWN two steps after every swap, so the model moves when the
        /// hand moves and the existing commit seam ships the batch it already knows how to ship. It is not a new
        /// mechanism: <c>OnSelectedSecondarySoldier</c>:758-773 already drains mid-session for the same reason.
        ///
        /// AP IS NOT TOUCHED — deliberately, and it is the whole reason this is safe. v1 (`6617846`) charged per
        /// gesture, so <c>AllowItemSwapHandler</c>:815 → <c>ActionPointRequirementSatisfied</c> then refused every
        /// further drag and the screen locked. Here <c>ApplyCostsCommand</c> is left exactly where the game puts
        /// it (<c>ExitState</c>:440, at most once per session via <c>_alreadyPaidAbility</c>): AP is CONSTANT for
        /// the whole session, so the swap gate answers the same thing on the last gesture as on the first, and
        /// the charge crosses the wire in the closing batch.
        ///
        /// THE DRAIN IS TWO STEPS, NOT THREE-MINUS-ONE. Draining without re-baselining the UI lists leaves the
        /// screen measuring every later gesture against the SESSION START while the queries already sit on the
        /// committed model — see <see cref="Rebaseline"/> for why that walls the screen after one gesture, and
        /// why it also retires the earlier "replay every past gesture" behaviour rather than compensating for
        /// it: with the baselines together again, <c>AttemptMoveItems</c> stages THIS gesture and nothing
        /// else.</summary>
        internal static void FlushGesture(UIStateInventory state)
        {
            if (state == null || TacticalDamageSync.LiveEngine() == null) return;
            if (_attemptMove == null)
            {
                _attemptMove = AccessTools.Method(typeof(UIStateInventory), "AttemptMoveItems");
                _applyActions = AccessTools.Method(typeof(UIStateInventory), "ApplyInventoryActions");
                if (_attemptMove == null || _applyActions == null)
                {
                    SayOnce("flush-methods",
                        "[Multiplayer][tac] UIStateInventory.AttemptMoveItems/ApplyInventoryActions did not resolve — " +
                        "inventory changes fall back to crossing only when the screen is closed.");
                    return;
                }
            }
            _attemptMove.Invoke(state, null);
            _applyActions.Invoke(state, null);
            Rebaseline(state);
        }

        /// <summary>The batch is ABOUT to be committed to the model on THIS peer, and the staged queries
        /// already hold exactly what it will commit (<c>InventoryQuery.Items</c> IS <c>_currentItems</c>, the
        /// staged list <c>SyncItems</c> is about to drain). Captured BEFORE the native write and never after:
        /// a postfix would make this family a result-ship (law 19), and the acting peer's own play is
        /// speculative in exactly A3a's sense — it announces first and the host settles.</summary>
        internal static void OnBatchCommitting(IEnumerable<InventoryQuery> queries)
        {
            var engine = TacticalDamageSync.LiveEngine();
            if (engine == null) return;
            if (SyncApplyScope.Active) return;            // law 8: this commit IS a mirror being applied

            int payerKey = _payerKey;
            bool charged = _charged;
            _payerKey = 0;
            _charged = false;

            var slots = new List<Slot>();
            bool partial = false;
            foreach (var q in queries)
            {
                var component = LinkedOf(q);
                if (component == null)
                {
                    // WAS A SILENT `continue`, and it is one of the two ways the 2026-07-31 run committed
                    // inventory sessions with not one line on any peer's log. A query whose linked component
                    // cannot be read is a container whose contents will never cross.
                    SayOnce("query-unlinked",
                        "[Multiplayer][tac] an InventoryQuery in a committing batch has NO linked " +
                        "InventoryComponent (LinkedOf returned null), so whatever it moves stays on this peer.");
                    continue;
                }
                int key;
                byte kind;
                Slot slot;
                if (AddressOf(component, out key, out kind))
                {
                    slot = new Slot { ActorKey = key, Kind = kind };
                }
                else if (GroundSlot(component, out slot))
                {
                    // An untouched EMPTY pile is the one UIStateInventory:513-522 spawns on every inventory open
                    // and destroys again on close. Naming it would make every other peer build the same litter.
                    if (q.Items.Count == 0 && !q.WillModifyInventory()) continue;
                }
                else
                {
                    // What is left here is a container that is neither an actor's own two nor a pile on the
                    // ground — a mount, a mod's, something this arc has not met. It ships PARTIAL, which is what
                    // stops the host refusing the whole batch for the one item that "vanished" out of it.
                    partial = true;
                    SayOnce("addr-" + TacticalActorLifecycle.SafeName(component),
                        "[Multiplayer][tac] an inventory container in this batch has NO shared address and is not a " +
                        "pile on the ground either, so whatever moved through it stays on this peer's screen. The " +
                        "rest of the batch still crosses.");
                    continue;
                }
                // The staged list VERBATIM. dc8944f deduped it here instead, because the flush used to replay every
                // earlier gesture (`AddItem` for an item the query already held, so `_currentItems` carried it
                // twice and the host's multiset check refused the batch). That replay is gone at its source —
                // Rebaseline puts the UI lists back on the model at the instant we commit, over the very
                // collection AttemptMoveItems iterates, so a query can no longer receive the same item twice. A
                // dedup kept past that point would only hide a REAL double, which is exactly what the multiset
                // check exists to refuse out loud (law 12).
                foreach (var item in q.Items) { slot.ItemDefs.Add(DefGuid(item)); slot.ItemCharges.Add(ChargeOf(item)); }
                slots.Add(slot);
            }
            // A ground address names EVERY pile at (def, pos), so two queries CAN share one: UIStateInventory:513-522
            // spawns a second pile under a soldier who opens his kit standing on the one he dropped last time. Two
            // slots with one address made the receiver resolve both onto its single pile and count its contents
            // twice, which refused (or silently mis-applied) every batch after the first ground drop — so the
            // sender merges them into the one container the receiver will see.
            for (int i = slots.Count - 1; i > 0; i--)
            {
                if (slots[i].Kind != KindGround) continue;
                for (int j = 0; j < i; j++)
                {
                    if (slots[j].Kind != KindGround || !string.Equals(slots[j].SetGuid, slots[i].SetGuid, StringComparison.Ordinal) ||
                        !Utl.Equals(slots[j].Pos, slots[i].Pos)) continue;
                    slots[j].ItemDefs.AddRange(slots[i].ItemDefs);
                    slots[j].ItemCharges.AddRange(slots[i].ItemCharges);
                    slots.RemoveAt(i);
                    break;
                }
            }
            if (slots.Count == 0)
            {
                // The other silent `return`. Reaching here means the game told us a batch WAS about to be
                // committed (WillModifyInventory) and not one of its containers could be addressed — never a
                // no-op, always a batch that vanished.
                SayOnce("batch-unaddressable",
                    "[Multiplayer][tac] a committing inventory batch named NO addressable container, so " +
                    "nothing crosses at all — every item this session moved stays on this peer's screen.");
                return;
            }

            float ap = PayerAp(payerKey);
            string what = "inventory " + slots.Count + " container(s)" + (charged ? " charged" : "") +
                          (partial ? " PARTIAL" : "");
            if (engine.IsHost)
                TacticalDamageSync.Send(TacticalDamageSync.OpInventory, what,
                    w => WriteLayout(w, slots, payerKey, ap, charged, partial));
            else
                IntentRail.Send(SurfaceIds.TacCommandIntent, OpIntentInventory, what,
                                w => WriteLayout(w, slots, payerKey, ap, charged, partial));
        }

        private static float PayerAp(int key)
        {
            if (key == 0) return float.NaN;
            string why;
            var actor = TacticalActorKey.Resolve(TacticalDamageSync.Tlc(), key, out why) as TacticalActor;
            var stats = actor == null ? null : actor.CharacterStats;
            return stats == null ? float.NaN : (float)stats.ActionPoints;
        }

        // ─── HOST: the intent ──────────────────────────────────────────────

        internal static void HandleInventoryIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int payerKey;
            float payerAp;
            bool charged, partial;
            var slots = ReadLayout(r, out payerKey, out payerAp, out charged, out partial);
            if (partial)
                Debug.LogWarning("[Multiplayer][tac] peer=" + senderPeerId + " sent a PARTIAL inventory batch — one of " +
                                 "its containers is neither an actor's own two nor a pile on the ground, so it has no " +
                                 "shared identity at all. The contents check is skipped, so the items it swallowed " +
                                 "stay where they are on this peer instead of the whole batch being refused.");

            string why;
            var payer = TacticalActorKey.Resolve(TacticalDamageSync.Tlc(), payerKey, out why) as TacticalActor;
            var ability = payer == null ? null : payer.GetAbility<InventoryAbility>();
            if (charged)
            {
                string refusal = Validate(true, null, true, null,
                                          ability != null, ability != null && ability.ActionPointRequirementSatisfied,
                                          true);
                if (refusal != null)
                {
                    IntentRail.Reject(SurfaceIds.TacCommandIntent, senderPeerId, "inventory batch: " + refusal);
                    return;
                }
            }

            string applied;
            using (SyncApplyScope.Enter()) applied = ApplyLayout(slots, validateContents: !partial);
            if (applied != null)
            {
                IntentRail.Reject(SurfaceIds.TacCommandIntent, senderPeerId, "inventory batch: " + applied);
                return;
            }

            // THE ONE CHARGE ON THE HOST, and it is the game's own method at the moment the acting peer's
            // session ended — never eager, never per gesture (law L69a). The scope is what keeps the seam
            // postfix from reading it back as a second local charge.
            if (charged && ability != null)
                using (SyncApplyScope.Enter()) ability.ApplyCosts();

            var settled = new List<Slot>(slots);
            float ap = PayerAp(payerKey);
            TacticalDamageSync.Send(TacticalDamageSync.OpInventory,
                "inventory settle from peer=" + senderPeerId + " (" + settled.Count + " container(s))",
                w => WriteLayout(w, settled, payerKey, ap, charged, partial));
            Debug.Log("[Multiplayer][tac] HOST inventory batch from peer=" + senderPeerId + " ACCEPTED — " +
                      settled.Count + " container(s)" + (charged ? ", action points charged" : "") +
                      " nonce=" + nonce);
        }

        // ─── PEERS: apply ──────────────────────────────────────────────────

        internal static void ApplyInventory(BinaryReader r)
        {
            int payerKey;
            float payerAp;
            bool charged, partial;
            var slots = ReadLayout(r, out payerKey, out payerAp, out charged, out partial);

            // The host has ALREADY validated this batch; a peer applies it verbatim, so the contents check
            // that guards the trust boundary is not repeated here (it would refuse a legitimate host batch
            // whenever this peer had drifted, exactly when it most needs to be reconciled).
            string applied;
            using (SyncApplyScope.Enter()) applied = ApplyLayout(slots, validateContents: false);
            if (applied != null)
            {
                Debug.LogError("[Multiplayer][tac] the host's inventory batch CANNOT be applied — " + applied +
                               ". Somebody's kit is now wrong on this screen.");
                return;
            }
            if (payerKey == 0 || float.IsNaN(payerAp)) return;
            string why;
            var payer = TacticalActorKey.Resolve(TacticalDamageSync.Tlc(), payerKey, out why) as TacticalActor;
            var stats = payer == null ? null : payer.CharacterStats;
            if (stats == null) return;
            TacticalDamageSync.Correct(stats.ActionPoints, payerAp, payer.name + " AP (inventory)");
        }
    }

    /// <summary>
    /// THE ONE COMMIT SEAM. <c>InventoryQuery.SyncItems</c> is the only place a staged tactical inventory
    /// batch becomes real, and it has exactly one caller in the assembly
    /// (<c>UIStateInventory.ApplyInventoryActions</c>:898-903). A PREFIX, and it never blocks: the staged
    /// queries already hold what the native body is about to write, so capturing here is a CAPTURE and not a
    /// result-ship (law 19), and it is the same posture A3a's <c>Activate</c> prefix has. It asks the game's
    /// own question first — <c>WillModifyInventory</c>:95-106, which <c>SyncItems</c> is about to answer
    /// false forever by resetting every query — so merely LOOKING at a soldier's backpack puts nothing on
    /// the wire.
    /// </summary>
    [HarmonyPatch(typeof(InventoryQuery), nameof(InventoryQuery.SyncItems))]
    internal static class InventoryCommitSeam
    {
        private static void Prefix(IEnumerable<InventoryQuery> queries)
        {
            // ALWAYS AUDIBLE (2026-07-31 RCA): the whole seam produced not one log line across three
            // instances, and "the screen was opened and closed without moving anything" is indistinguishable
            // from "the patch never bound" unless the seam says it ran. Rate is one line per inventory
            // session close, so it is not gated behind MpDiag.
            int total = 0, willModify = 0;
            if (queries != null)
                foreach (var q in queries)
                {
                    total++;
                    if (q != null && q.WillModifyInventory()) willModify++;
                }
            // `|| HasPendingCharge`: with the per-gesture flush the CLOSING commit has nothing left to move —
            // every item went at the gesture that moved it — but it is the first commit that runs after
            // ExitState:440 charged the action, and that charge still has to reach the other peers.
            bool charge = TacticalInventorySync.HasPendingCharge;
            Debug.Log("[Multiplayer][tac] inventory commit seam fired, queries=" + total +
                      " willModify=" + willModify + (charge ? " charge-pending" : "") +
                      (willModify == 0 && !charge ? " (nothing was moved — nothing to ship)" : ""));
            if (willModify > 0 || charge) TacticalInventorySync.OnBatchCommitting(queries);
        }
    }

    /// <summary>
    /// THE PER-GESTURE SEAM — presentation side (law 4c), and it adds no wire surface at all: it makes the model
    /// move when the hand moves, so the commit seam above fires per gesture instead of once at close.
    /// <c>UIStateInventory.RefreshUI</c> is the game's own per-swap callback (subscribed to
    /// <c>UIModuleSoldierEquip.OnItemsSwapped</c> at <c>EnterState</c>:332, raised at
    /// <c>UIModuleSoldierEquip</c>:1103/1122 for both the drag and the double-click path) and it has exactly one
    /// other caller, the tail of <c>UndoInventoryActions</c>:917 — where flushing is equally right, because an
    /// undo has to reach the peers that already saw the move.
    /// </summary>
    [HarmonyPatch]
    internal static class InventoryGestureSeam
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(UIStateInventory), "RefreshUI");

        private static void Postfix(UIStateInventory __instance) => TacticalInventorySync.FlushGesture(__instance);
    }

    /// <summary>
    /// THE COST OBSERVER (law L69a). A POSTFIX on the game's own single charge —
    /// <c>InventoryAbility.ApplyCosts()</c>, the public parameterless overload <c>ApplyCostsCommand</c> calls
    /// — so this arc reads the number the game charged and never decides one. Patching the protected
    /// <c>TacticalAbility.ApplyCosts(object)</c> instead would catch every ability's costs, and patching
    /// anything earlier would be v1's eager charge again.
    /// </summary>
    [HarmonyPatch]
    internal static class InventoryCostSeam
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(InventoryAbility), nameof(InventoryAbility.ApplyCosts), new Type[0]);

        private static void Postfix(InventoryAbility __instance) => TacticalInventorySync.OnCostsApplied(__instance);
    }
}
