using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
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
    /// <c>Inventory</c> or <c>Equipments</c> — plus the ordered item DEF GUIDS in it. Items carry no shared
    /// identity (A4 had to solve the same problem for the corpse manifest and reached the same answer), and
    /// they do not need one here: a committed batch only ever MOVES items between the containers it names, so
    /// the multiset of defs across those containers is invariant, and the host CHECKS that invariant before
    /// accepting a client's batch. That check is the trust boundary — it is what stops a client conjuring a
    /// second Hel Cannon by editing a layout.
    ///
    /// KNOWN CEILING: order WITHIN a container is not forced, only membership. The native lists are re-sorted
    /// by the UI's own filters on display, and forcing order would mean removing and re-adding items that
    /// never moved — which for <c>EquipmentComponent</c> means unmounting and remounting a weapon that was
    /// never touched.
    /// </summary>
    public static class TacticalInventorySync
    {
        /// <summary>Op 3 of the 0x83 intent family (1 = command, 2 = resnapshot request).</summary>
        internal const byte OpIntentInventory = 3;

        private const byte KindInventory = 0;
        private const byte KindEquipments = 1;

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
            internal List<string> ItemDefs = new List<string>();
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
                w.Write(s.ItemDefs.Count);
                foreach (var g in s.ItemDefs) w.Write(g ?? "");
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
                int items = r.ReadInt32();
                for (int j = 0; j < items; j++) s.ItemDefs.Add(r.ReadString());
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
            string firstUnresolved = null;

            foreach (var s in slots)
            {
                string why;
                var actor = TacticalActorKey.Resolve(tlc, s.ActorKey, out why);
                var component = ContainerOf(actor, s.Kind);
                if (component == null && firstUnresolved == null)
                    firstUnresolved = (s.Kind == KindEquipments ? "the equipment slots" : "the backpack") +
                                      " of actor " + s.ActorKey + " (" + (actor == null ? why : "that actor has no such container") + ")";
                targets.Add(component);
            }

            // The pool is every item currently sitting in the named containers, with where it sits now.
            var pool = new List<KeyValuePair<Item, InventoryComponent>>();
            var before = new List<string>();
            foreach (var c in targets)
            {
                if (c == null) continue;
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

            int moved = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var target = targets[i];
                if (target == null) continue;
                foreach (var guid in slots[i].ItemDefs)
                {
                    int found = -1, inPlace = -1;
                    for (int p = 0; p < pool.Count; p++)
                    {
                        if (pool[p].Key == null || DefGuid(pool[p].Key) != guid) continue;
                        if (ReferenceEquals(pool[p].Value, target)) { inPlace = p; break; }
                        if (found < 0) found = p;
                    }
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
            return null;
        }

        private static string DefGuid(Item item)
        {
            var def = item == null ? null : item.ItemDef;
            return def == null ? "" : def.Guid;
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
                if (!AddressOf(component, out key, out kind))
                {
                    // THE ARC'S ONE NAMED CEILING. Pre-existing crates and corpses are keyed — ItemContainer IS a
                    // TacticalActorBase, so TacticalActorKey.BuildBattleKeys gives every one on the map a
                    // battle-start ordinal. What is NOT keyed is the container the game spawns THE MOMENT you drop
                    // something onto bare ground (UIStateInventory.CreateGroundInventory:512-521 calls
                    // ActorSpawner.SpawnActor<ItemContainer>), because A4 replicates mid-battle spawns only for
                    // TacticalActor. So the batch ships WITHOUT that container and says so — marked PARTIAL, which
                    // is what stops the host refusing the whole batch for the one item that "vanished" out of it.
                    partial = true;
                    SayOnce("addr-" + TacticalActorLifecycle.SafeName(component),
                        "[Multiplayer][tac] an inventory container in this batch has NO shared address — almost " +
                        "always the pile the game just spawned under a soldier who dropped something onto bare " +
                        "ground, which exists on this peer alone (A6 replicates container CONTENTS, not container " +
                        "spawns). The rest of the batch still crosses; the dropped item stays in that soldier's " +
                        "pack on every other screen.");
                    continue;
                }
                var slot = new Slot { ActorKey = key, Kind = kind };
                foreach (var item in q.Items) slot.ItemDefs.Add(DefGuid(item));
                slots.Add(slot);
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
                                 "its containers has no shared identity (a pile dropped onto bare ground). The " +
                                 "contents check is skipped for it, so the items it swallowed stay where they are " +
                                 "on this peer instead of the whole batch being refused.");

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
            Debug.Log("[Multiplayer][tac] inventory commit seam fired, queries=" + total +
                      " willModify=" + willModify + (willModify == 0 ? " (nothing was moved — nothing to ship)" : ""));
            if (willModify > 0) TacticalInventorySync.OnBatchCommitting(queries);
        }
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
