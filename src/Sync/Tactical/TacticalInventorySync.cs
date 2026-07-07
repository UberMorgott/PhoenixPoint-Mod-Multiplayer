using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;
using Codec = Multiplayer.Sync.Tactical.TacticalInventoryTransferCodec;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Mid-mission INVENTORY-TRANSFER relay (tactical loot UI re-enable). Closes the roadmap deferral "client
    /// mid-mission loot UI re-enable": the client loot/inventory view (crates, ground containers, dead-body drop
    /// containers, soldier↔soldier trades) is now allowed, and its committed mutations are host-authoritative.
    ///
    /// The native tactical inventory UI is DEFERRED-COMMIT: every drag commits at once through
    /// <c>UIStateInventory.ApplyInventoryActions → InventoryQuery.SyncItems</c>, which funnels each mutation into
    /// <c>InventoryComponent.RemoveItem</c>/<c>AddItem</c>. We intercept that ONE commit (spec-canon suppress+relay
    /// on the client; symmetric host→all mirror on the host):
    ///   • CLIENT (mirroring): capture the batch of cross-inventory MOVES, relay ONE <c>tac.intent.inventory</c>
    ///     (0x9A) to the host, and SUPPRESS the local commit (the host is authoritative; the outcome mirrors back).
    ///   • HOST intent handler (<see cref="HostOnInventoryIntent"/>): validate each move against its OWN state
    ///     (endpoints resolve, the item is present), apply the surviving moves natively, spend the inventory
    ///     ability's AP cost on the acting soldier, then broadcast the SURVIVING set as <c>tac.inventory</c> (0x9B).
    ///   • HOST own looting: native SyncItems runs, then the captured batch is broadcast on 0x9B — so a client
    ///     mirrors the HOST's looting the same way.
    ///   • ALL peers (<see cref="HandleInventoryApply"/>): re-run the SAME native moves under a
    ///     <see cref="SyncApplyScope"/> — this updates BOTH endpoints (soldier + container) with no despawn/respawn
    ///     churn. AP rides the 0x8F actor-state delta, never this surface.
    ///
    /// Endpoint = (actor netId, slot 0=backpack Inventory / 1=Equipments); a container is slot 0. Item identity =
    /// (ItemDef guid, index among that def in the SOURCE, pre-move) — both sides mirror the same source contents, so
    /// the Nth item of a def is the same logical item, and the def guid means the host matches by def (the DropItem
    /// FOLLOW-UP), never a blind index onto a drifted slot. Deterministic degrade: an unresolvable endpoint / a
    /// missing item / an empty batch → that move is skipped (never applied, never broadcast), no crash.
    ///
    /// All game types are reached by name via <see cref="AccessTools"/> (reflection boundary); the PURE wire codec
    /// is <see cref="TacticalInventoryTransferCodec"/> (unit-tested).
    /// </summary>
    public static class TacticalInventorySync
    {
        private static uint _nonce;
        private static uint NextNonce() => unchecked(++_nonce);

        // Host: the moves captured in the ApplyInventoryActions PREFIX (before native SyncItems mutates the
        // inventories), broadcast in the POSTFIX. Thread-static: the tactical UI commit runs on the main thread and
        // one host inventory view commits at a time.
        [ThreadStatic] private static List<Codec.Move> _hostPendingMoves;

        // ─── CLIENT/HOST capture at the inventory-view commit funnel ───────────────────────────────
        /// <summary>Called from the <c>ApplyInventoryActions</c> PREFIX (client + host co-op). Returns TRUE when the
        /// caller must SKIP the native SyncItems (client mirror suppress — the host applies + mirrors back). Host/SP
        /// → FALSE (native runs; the host also stashes the batch for the postfix broadcast). Fail-open: any capture
        /// error on the host runs native unchanged; on a mirroring client it still suppresses (a frozen client must
        /// never commit a local loot move — it would silently not exist post-mission).</summary>
        public static bool OnApplyInventoryActions(object uiStateInventory)
        {
            _hostPendingMoves = null;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return false;   // single-player: native commit, no capture

            bool mirroring = TacticalDeploySync.IsClientMirroring;
            List<Codec.Move> moves;
            int actingNetId, unsyncedDrops;
            try { moves = CaptureMoves(uiStateInventory, out actingNetId, out unsyncedDrops); }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] inventory capture failed: " + ex);
                // Client must not commit locally even on a capture error; host falls through to native.
                return mirroring;
            }

            if (mirroring)
            {
                if (unsyncedDrops > 0)
                    // Degrade-to-notify (sync canon): a bare-ground drag isn't relayed (the UI's fresh drop container
                    // is a local unregistered actor). The item stayed put on both sides — tell the player how to drop.
                    Debug.LogWarning("[Multiplayer][tac] client mirror: " + unsyncedDrops + " inventory drag(s) to a " +
                                     "fresh ground spot are NOT synced (co-op loot tracks moves between existing " +
                                     "soldiers/containers only) — item(s) stayed put; use the drop-item button to drop.");
                bool applyCost = moves.Count > 0;   // any cross-inventory move ⇒ the inventory ability's AP cost is due
                byte[] payload = Codec.EncodeIntent(actingNetId, applyCost, moves, NextNonce());
                TacticalMoveSync.SendToHost(engine, TacticalSurfaceIds.TacInventoryIntent, payload);
                Debug.Log("[Multiplayer][tac] CLIENT sent tac.intent.inventory acting=" + actingNetId +
                          " moves=" + moves.Count + " applyCost=" + applyCost);
                return true;   // SUPPRESS native SyncItems — the host is authoritative
            }
            if (engine.IsHost) _hostPendingMoves = moves;   // native runs; broadcast the same batch in the postfix
            return false;
        }

        /// <summary>Host POSTFIX after native SyncItems: broadcast the captured batch (host→all) so every client
        /// mirrors the HOST's own looting. No-op if nothing was captured / no moves / off-host.</summary>
        public static void OnApplyInventoryActionsPost()
        {
            var moves = _hostPendingMoves;
            _hostPendingMoves = null;
            if (moves == null || moves.Count == 0) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            BroadcastApply(engine, moves);
            Debug.Log("[Multiplayer][tac] HOST broadcast tac.inventory (own loot) moves=" + moves.Count);
        }

        // ─── HOST inbound: a client loot intent arrived ────────────────────────────────────────────
        /// <summary>HOST inbound (<c>tac.intent.inventory</c> 0x9A): peer-dedup → apply the surviving moves natively
        /// against the host's authoritative state → spend the inventory ability AP cost on the acting soldier →
        /// broadcast the SURVIVING set (0x9B) so every peer mirrors identically. No-op off-host / off-session.</summary>
        public static void HostOnInventoryIntent(ulong senderPeerId, byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (!Codec.TryDecodeIntent(payload, out var intent)) { Debug.LogError("[Multiplayer][tac] inventory intent decode failed"); return; }
            if (!TacticalDeploySync.IntentDedup.IsNew(senderPeerId, TacticalSurfaceIds.TacInventoryIntent, intent.Nonce)) return;

            try
            {
                var applied = ApplyMoves(intent.Moves, destroyEmptied: true);
                // AUTHORITY: the host recomputes whether the inventory ability's AP cost is due — it does NOT trust
                // the client's applyCost flag (kept on the wire for compat only, never for authority). The native UI
                // rule is "any cross-inventory move ⇒ cost due"; every APPLIED move is exactly that, so cost is due
                // iff ≥1 move applied. One intent == one inventory-view session, so the host charges AT MOST once here
                // — the native _alreadyPaidAbility within-session double-charge guard is inherently satisfied (there
                // is no second charge to make). A stale-client move the host rejected is excluded (uses applied, not requested).
                if (applied.Count > 0) HostApplyInventoryCost(intent.ActingNetId);

                // Broadcast ONLY the moves the host actually applied — a stale-client move the host rejected must
                // not be mirrored (the client already suppressed its local commit, so its item stays put → consistent).
                if (applied.Count > 0) BroadcastApply(engine, applied);
                Debug.Log("[Multiplayer][tac] HOST applied tac.intent.inventory acting=" + intent.ActingNetId +
                          " requested=" + intent.Moves.Count + " applied=" + applied.Count);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnInventoryIntent failed: " + ex); }
        }

        // ─── CLIENT inbound: apply the authoritative batch ─────────────────────────────────────────
        /// <summary>CLIENT inbound (<c>tac.inventory</c> 0x9B): seq-guard, then re-run the SAME native moves on the
        /// mirror under a <see cref="SyncApplyScope"/> (both endpoints update; container empties auto-destroy natively).
        /// No-op on host / off-session.</summary>
        public static void HandleInventoryApply(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!Codec.TryDecodeApply(payload, out var apply)) { Debug.LogError("[Multiplayer][tac] tac.inventory decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacInventoryApply, apply.Seq)) return;

            try
            {
                // Client mirror: DON'T destroy an emptied container here — the host's despawn sweep (0x93) is the
                // sole authority on container removal (idempotent on the client). Only suppress the mid-batch
                // inline auto-destroy so a rare put-back into a just-emptied container still lands.
                int applied;
                using (SyncApplyScope.Enter())
                    applied = ApplyMoves(apply.Moves, destroyEmptied: false).Count;
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacInventoryApply, apply.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.inventory seq=" + apply.Seq +
                          " requested=" + apply.Moves.Count + " applied=" + applied);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleInventoryApply failed: " + ex); }
        }

        private static void BroadcastApply(NetworkEngine engine, List<Codec.Move> moves)
        {
            uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacInventoryApply);
            byte[] payload = Codec.EncodeApply(moves, seq);
            TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacInventoryApply, payload);
        }

        // ─── CLIENT capture: compute the batch of cross-inventory MOVES from the view-state queries ──
        /// <summary>Read the inventory-view's scratch <c>InventoryQuery</c> set (private <c>GetInventoryQueries()</c>)
        /// and pair each REMOVED item (source query) with the query it was ADDED to (target) — the same closed set of
        /// moves the native <c>SyncItems</c> is about to commit. Each move carries stable identity: both endpoints as
        /// (netId, slot) and the item as (ItemDef guid, index among that def in the source's PRE-move contents). A
        /// removed item that does NOT become a relayed move — dragged to a fresh/untracked ground container (the UI
        /// spawns a LOCAL unregistered <c>ItemContainer</c> for a bare-ground drop, outside the query set), or an
        /// unreadable def/index — is COUNTED in <paramref name="unsyncedDrops"/> (never a guessed/half move). Since
        /// the client suppresses its local commit, such an item simply stays put on BOTH sides (consistent, no
        /// divergence) — but the player's drag silently didn't take, so the caller degrades-to-notify.</summary>
        private static List<Codec.Move> CaptureMoves(object ui, out int actingNetId, out int unsyncedDrops)
        {
            actingNetId = TacticalDeploySync.NetIdForLiveActor(GetProp(ui, "PrimaryActor"));
            unsyncedDrops = 0;
            var moves = new List<Codec.Move>();

            var getQueries = AccessTools.Method(ui.GetType(), "GetInventoryQueries");
            if (getQueries == null || !(getQueries.Invoke(ui, null) is IEnumerable queries)) return moves;

            // Per-query snapshot: endpoint identity + initial/removed/added item sets (reference identity).
            var recs = new List<QueryRec>();
            foreach (var q in queries)
            {
                if (q == null) continue;
                object linkedInv = GetField(q, "_linkedInventory");
                if (linkedInv == null) continue;
                ResolveEndpointId(linkedInv, out int netId, out byte slot);   // netId < 0 ⇒ unregistered (skipped below)

                var initial = ToObjectList(GetField(q, "_initialItems"));
                var current = ToObjectList(GetField(q, "_currentItems"));
                var initialSet = new HashSet<object>(initial, RefEq.Instance);
                var currentSet = new HashSet<object>(current, RefEq.Instance);

                var rec = new QueryRec { NetId = netId, Slot = slot, Initial = initial };
                foreach (var it in initial) if (!currentSet.Contains(it)) rec.Removed.Add(it);
                foreach (var it in current) if (!initialSet.Contains(it)) rec.Added.Add(it);
                recs.Add(rec);
            }

            // Map every ADDED item → its target endpoint (only registered endpoints can be a wire target).
            var addedTo = new Dictionary<object, QueryRec>(RefEq.Instance);
            foreach (var r in recs)
                if (r.NetId >= 0)
                    foreach (var it in r.Added)
                        addedTo[it] = r;

            // Pair each REMOVED item with the query it landed in → one MOVE (else count it as an unsynced drag).
            foreach (var src in recs)
            {
                if (src.NetId < 0) continue;
                foreach (var item in src.Removed)
                {
                    if (addedTo.TryGetValue(item, out var dst))
                    {
                        addedTo.Remove(item);
                        string guid = DefReflection.GetGuid(GetProp(item, "ItemDef"));
                        int srcDefIndex = string.IsNullOrEmpty(guid) ? -1 : IndexAmongDef(src.Initial, item, guid);
                        if (srcDefIndex >= 0)
                        {
                            moves.Add(new Codec.Move(src.NetId, src.Slot, dst.NetId, dst.Slot, guid, srcDefIndex));
                            continue;
                        }
                    }
                    // Removed but not relayable: dragged to a fresh/untracked ground container (unregistered, outside
                    // the query set) or an unreadable def/index. The client suppressed its commit, so the item stays
                    // put on both sides (consistent) — but the drag didn't take → surface it (degrade-to-notify).
                    unsyncedDrops++;
                }
            }
            return moves;
        }

        private sealed class QueryRec
        {
            public int NetId;
            public byte Slot;
            public List<object> Initial;
            public readonly List<object> Removed = new List<object>();
            public readonly List<object> Added = new List<object>();
        }

        // ─── Shared HOST + CLIENT apply: resolve-then-move (indices stable across the batch) ─────────
        /// <summary>Apply a batch of moves natively. PHASE 1 resolves every item instance against the CURRENT (==
        /// pre-move) source inventories, so multiple same-def moves keep stable indices; PHASE 2 performs
        /// remove-then-add per resolved move via <c>InventoryComponent.RemoveItem</c>/<c>AddItem</c> (the exact
        /// funnels native SyncItems uses — the moved INSTANCE is preserved, keeping ammo/charges). Container
        /// auto-destroy-when-empty is suppressed across the batch (like native SyncItems) then, when
        /// <paramref name="destroyEmptied"/> (HOST), emptied crates are destroyed once after all moves; a client
        /// leaves container removal to the host's despawn sweep. Returns the moves that actually applied (endpoints
        /// resolved + item present) — the caller broadcasts exactly that set.</summary>
        private static List<Codec.Move> ApplyMoves(IList<Codec.Move> moves, bool destroyEmptied)
        {
            var applied = new List<Codec.Move>();
            if (moves == null || moves.Count == 0) return applied;

            var resolved = new List<(object item, object src, object dst, Codec.Move move)>();
            var endpoints = new List<object>();   // unique inventory endpoints touched (for the destroy-empty guard)
            foreach (var m in moves)
            {
                object src = ResolveEndpoint(m.SrcNetId, m.SrcSlot);
                object dst = ResolveEndpoint(m.DstNetId, m.DstSlot);
                if (src == null || dst == null)
                {
                    Debug.LogWarning("[Multiplayer][tac] inventory move endpoint unresolved (src=" + m.SrcNetId +
                                     "/" + m.SrcSlot + " dst=" + m.DstNetId + "/" + m.DstSlot + ") — skip");
                    continue;
                }
                object item = NthItemOfDef(src, m.ItemDefGuid, m.SrcDefIndex);
                if (item == null)
                {
                    Debug.LogWarning("[Multiplayer][tac] inventory move item not present (def=" + m.ItemDefGuid +
                                     " idx=" + m.SrcDefIndex + " src=" + m.SrcNetId + "/" + m.SrcSlot + ") — skip");
                    continue;
                }
                resolved.Add((item, src, dst, m));
                if (!endpoints.Contains(src)) endpoints.Add(src);
                if (!endpoints.Contains(dst)) endpoints.Add(dst);
            }
            if (resolved.Count == 0) return applied;

            // Suppress container auto-destroy during the batch (faithful to native SyncItems), remember originals.
            var guarded = new List<(object actor, bool orig)>();
            foreach (var inv in endpoints)
            {
                object actor = GetProp(inv, "Actor");
                if (actor != null && TryGetDestroyWhenEmpty(actor, out bool orig))
                {
                    SetDestroyWhenEmpty(actor, false);
                    guarded.Add((actor, orig));
                }
            }

            foreach (var (item, src, dst, move) in resolved)
            {
                InvokeRemoveItem(src, item);
                InvokeAddItem(dst, item);
                applied.Add(move);
            }

            // Restore the flag; the HOST destroys any now-empty container (native ExitState behaviour) — a client
            // leaves that to the incoming despawn (0x93) so a container is removed by exactly one authority.
            foreach (var (actor, orig) in guarded)
            {
                SetDestroyWhenEmpty(actor, orig);
                if (destroyEmptied && orig && ContainerIsEmpty(actor)) DestroyActor(actor);
            }
            return applied;
        }

        private static void HostApplyInventoryCost(int actingNetId)
        {
            try
            {
                object soldier = TacticalDeploySync.ResolveLiveActor(actingNetId);
                if (soldier == null) return;
                var invAbilityType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.InventoryAbility");
                if (invAbilityType == null) return;
                var getAbility = AccessTools.Method(soldier.GetType(), "GetAbility");
                object ability = (getAbility != null && getAbility.IsGenericMethodDefinition)
                    ? getAbility.MakeGenericMethod(invAbilityType).Invoke(soldier, null) : null;
                if (ability == null) return;
                // public void ApplyCosts() — spends the inventory ability's AP cost (0 for a free ability). The AP
                // change mirrors to clients via the 0x8F actor-state delta.
                AccessTools.Method(invAbilityType, "ApplyCosts", Type.EmptyTypes)?.Invoke(ability, null);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostApplyInventoryCost failed: " + ex); }
        }

        // ─── Endpoint / item reflection helpers ─────────────────────────────────────────────────────
        /// <summary>CLIENT: the wire endpoint id of a live <c>InventoryComponent</c> — (its actor's netId, slot).
        /// slot = 1 when the component is the actor's <c>Equipments</c>, else 0 (backpack <c>Inventory</c> or a
        /// container's single inventory). netId &lt; 0 ⇒ the actor is not registered (endpoint not encodable).</summary>
        private static void ResolveEndpointId(object inv, out int netId, out byte slot)
        {
            netId = -1; slot = Codec.SlotInventory;
            object actor = GetProp(inv, "Actor");
            if (actor == null) return;
            netId = TacticalDeploySync.NetIdForLiveActor(actor);
            object equipments = GetProp(actor, "Equipments");
            if (equipments != null && ReferenceEquals(equipments, inv)) slot = Codec.SlotEquipments;
        }

        /// <summary>HOST/CLIENT: resolve a wire endpoint (netId, slot) to the live <c>InventoryComponent</c>.</summary>
        private static object ResolveEndpoint(int netId, byte slot)
        {
            object actor = TacticalDeploySync.ResolveLiveActor(netId);
            if (actor == null) return null;
            return slot == Codec.SlotEquipments ? GetProp(actor, "Equipments") : GetProp(actor, "Inventory");
        }

        /// <summary>The <paramref name="index"/>-th item of <paramref name="guid"/> in the inventory's CURRENT
        /// contents (== pre-move on both sides). Null when fewer than index+1 such items exist (stale client).</summary>
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

        /// <summary>Index of <paramref name="item"/> among items of <paramref name="guid"/> in the source's initial
        /// (pre-move) contents. -1 if not found (should not happen — a removed item is in initial).</summary>
        private static int IndexAmongDef(List<object> initial, object item, string guid)
        {
            int seen = 0;
            foreach (var it in initial)
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

        // InventoryComponent.RemoveItem(Item) / AddItem(Item, object) — the exact overloads (Item-typed, not the
        // ItemDef ones). Cached once; the type is stable across the session.
        private static MethodInfo _removeItem, _addItem;
        private static bool _invReflectionResolved;
        private static void EnsureInvReflection(object inv)
        {
            if (_invReflectionResolved) return;
            _invReflectionResolved = true;
            var invType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.InventoryComponent");
            var itemType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.Item");
            if (invType == null || itemType == null)
            {
                Debug.LogError("[Multiplayer][tac] inventory reflection: InventoryComponent/Item type not found");
                return;
            }
            _removeItem = AccessTools.Method(invType, "RemoveItem", new[] { itemType });
            _addItem = AccessTools.Method(invType, "AddItem", new[] { itemType, typeof(object) });
            if (_removeItem == null || _addItem == null)
                Debug.LogError("[Multiplayer][tac] inventory reflection: AddItem/RemoveItem(Item[,object]) not found");
        }

        private static void InvokeRemoveItem(object inv, object item)
        {
            EnsureInvReflection(inv);
            _removeItem?.Invoke(inv, new[] { item });
        }

        private static void InvokeAddItem(object inv, object item)
        {
            EnsureInvReflection(inv);
            _addItem?.Invoke(inv, new[] { item, null });   // source = null ⇒ native defaults it to the target inventory
        }

        private static bool TryGetDestroyWhenEmpty(object actor, out bool value)
        {
            value = false;
            var p = AccessTools.Property(actor.GetType(), "DestroyWhenEmpty");
            if (p == null || p.PropertyType != typeof(bool) || !p.CanRead || !p.CanWrite) return false;
            try { value = (bool)p.GetValue(actor, null); return true; }
            catch { return false; }
        }

        private static void SetDestroyWhenEmpty(object actor, bool value)
        {
            try { AccessTools.Property(actor.GetType(), "DestroyWhenEmpty")?.SetValue(actor, value, null); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] SetDestroyWhenEmpty failed: " + ex); }
        }

        private static bool ContainerIsEmpty(object containerActor)
        {
            object inv = GetProp(containerActor, "Inventory");
            return GetProp(inv, "Items") is ICollection c && c.Count == 0;
        }

        private static Type _actorComponentType;
        private static MethodInfo _destroyActor;
        private static void DestroyActor(object actor)
        {
            try
            {
                if (_destroyActor == null)
                {
                    var spawnerType = AccessTools.TypeByName("Base.Entities.ActorSpawner");
                    _actorComponentType = AccessTools.TypeByName("Base.Entities.ActorComponent");
                    _destroyActor = (spawnerType != null && _actorComponentType != null)
                        ? AccessTools.Method(spawnerType, "DestroyActor", new[] { _actorComponentType }) : null;
                }
                _destroyActor?.Invoke(null, new[] { actor });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] DestroyActor failed: " + ex); }
        }

        // ─── small reflection utils (mirror TacticalCombatSync) ─────────────────────────────────────
        private static List<object> ToObjectList(object enumerable)
        {
            var list = new List<object>();
            if (enumerable is IEnumerable e) foreach (var o in e) list.Add(o);
            return list;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        private static object GetField(object obj, string name)
        {
            if (obj == null) return null;
            var f = AccessTools.Field(obj.GetType(), name);
            if (f != null) return f.GetValue(obj);
            var p = AccessTools.Property(obj.GetType(), name);
            return p != null ? p.GetValue(obj, null) : null;
        }

        // Reference-identity set/map comparer (net472 has no built-in ReferenceEqualityComparer).
        private sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq Instance = new RefEq();
            bool IEqualityComparer<object>.Equals(object a, object b) => ReferenceEquals(a, b);
            int IEqualityComparer<object>.GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }
    }
}
