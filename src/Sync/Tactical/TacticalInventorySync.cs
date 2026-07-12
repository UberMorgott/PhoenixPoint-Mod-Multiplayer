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
    /// REACTIVE per-gesture rail (canon 2026-07-13: no waiting for screen-close — two players in one container
    /// must see each other's moves live; the host serializes, a conflicting second move is rejected and the origin
    /// reconciles): every completed gesture on the tactical inventory screen (<c>AttemptSlotSwap</c> / side-button /
    /// Undo — see <c>Multiplayer.Harmony.Tactical.InventoryGesturePatches</c>) diffs the UI lists against a
    /// per-gesture baseline (<see cref="OnTacticalGesture"/>) and rides the SAME 0x9A/0x9B surfaces immediately:
    /// the client keeps its optimistic UI move (the model stays mirror-only), the host applies natively +
    /// broadcasts. The host broadcasts even a fully-rejected intent (empty apply) so every open inventory view —
    /// including the origin's optimistic one — repaints from authoritative truth
    /// (<see cref="RefreshOpenInventoryView"/>). The close-commit funnel below stays as defense-in-depth for any
    /// gesture seam the per-gesture rail misses (its diff is empty otherwise, because each relayed gesture
    /// re-baselines the UI lists).
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

            _costChargedForState = null;   // screen session ends at ExitState → re-arm the per-session AP-cost latch

            if (mirroring)
            {
                if (unsyncedDrops > 0)
                    // Degrade-to-notify (sync canon): a bare-ground drag isn't relayed (the UI's fresh drop container
                    // is a local unregistered actor). The item stayed put on both sides — tell the player how to drop.
                    Debug.LogWarning("[Multiplayer][tac] client mirror: " + unsyncedDrops + " inventory drag(s) to a " +
                                     "fresh ground spot are NOT synced (co-op loot tracks moves between existing " +
                                     "soldiers/containers only) — item(s) stayed put; use the drop-item button to drop.");
                if (moves.Count == 0)
                {
                    // Normal reactive-rail close: every gesture already relayed + re-baselined, so the close diff is
                    // empty — nothing to send, just keep the client commit suppressed (model stays mirror-only).
                    Debug.Log("[Multiplayer][tac] close-commit: empty diff (per-gesture rail already synced) — suppress only");
                    return true;
                }
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
                // AP: cost is due iff ≥1 move APPLIED (native rule: any cross-inventory move ⇒ cost due; a rejected
                // move is excluded) AND the origin flags this intent as its screen-session's first (per-gesture rail
                // sends one intent per gesture; native charges the ability once per inventory-view session, so the
                // origin's session latch is the only place that knows "first"). A stale/hostile flag can only
                // UNDER-charge the origin's own soldier — co-op, not adversarial.
                if (applied.Count > 0 && intent.ApplyCost) HostApplyInventoryCost(intent.ActingNetId);

                // Broadcast the APPLIED subset — even when empty: a rejected/conflicting move never mirrors (the
                // origin suppressed its local commit, so its model is consistent), but the empty apply still reaches
                // every peer so an OPEN inventory view — the origin's optimistic UI above all — repaints from
                // authoritative truth (the reject-revert mechanism).
                if (intent.Moves.Count > 0) BroadcastApply(engine, applied);
                Debug.Log("[Multiplayer][tac] HOST applied tac.intent.inventory acting=" + intent.ActingNetId +
                          " requested=" + intent.Moves.Count + " applied=" + applied.Count +
                          " rejected=" + (intent.Moves.Count - applied.Count));
                if (applied.Count > 0) RefreshOpenInventoryView("client-intent");   // host may watch the same crate
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
                // Reactive rail: an open inventory view repaints from the just-updated truth. For the origin this is
                // the echo (repaint == its optimistic move) or the reject-revert (empty apply → item snaps back);
                // for a bystander watching the same container it's the live mirror of the other player's move.
                RefreshOpenInventoryView("mirror-apply");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleInventoryApply failed: " + ex); }
        }

        private static void BroadcastApply(NetworkEngine engine, List<Codec.Move> moves)
        {
            uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacInventoryApply);
            byte[] payload = Codec.EncodeApply(moves, seq);
            TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacInventoryApply, payload);
        }

        // ─── REACTIVE per-gesture rail (immediate relay, no screen-close wait) ──────────────────────
        // One AP-cost per screen session (native charges the inventory ability once per view session). The latch is
        // the state instance of the session that already charged/flagged; ExitState (OnApplyInventoryActions above)
        // re-arms it. ponytail: a fully-rejected first intent leaves the flag armed → that session under-charges;
        // acceptable in co-op, upgrade to a host-side per-session ack if AP drift ever matters.
        private static object _costChargedForState;

        /// <summary>A tactical-inventory gesture completed (slot swap / side-button / Undo). Diff the UI lists
        /// against the per-gesture baseline — the SAME membership truth the deferred <c>AttemptMoveItems</c> would
        /// commit at close — and relay it NOW: client sends one 0x9A intent (optimistic UI kept, model mirror-only);
        /// host applies natively + broadcasts 0x9B. After a relay the lists are RE-BASELINED so the next gesture
        /// (and the close-commit) diffs only itself. Undo needs no special case: the native restore makes the diff
        /// == the reverse moves. No-op outside co-op / outside the tactical inventory view. Never throws into the
        /// native gesture.</summary>
        public static void OnTacticalGesture()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive) return;   // single-player: pure native
                bool host = engine.IsHost;
                if (!host && !TacticalDeploySync.IsClientMirroring) return;
                object state = ActiveTacticalInventoryState();
                if (state == null) return;                        // geoscape equip / no inventory view → not ours

                var moves = BuildGestureMoves(state, out object module, out int actingNetId, out int unsynced);
                if (module == null) return;
                if (moves.Count > 0)
                {
                    bool firstOfSession = !ReferenceEquals(_costChargedForState, state);
                    if (host)
                    {
                        var applied = ApplyMoves(moves, destroyEmptied: true);
                        if (applied.Count > 0)
                        {
                            if (firstOfSession) { HostApplyInventoryCost(actingNetId); _costChargedForState = state; }
                            BroadcastApply(engine, applied);
                        }
                        Debug.Log("[Multiplayer][tac] GESTURE host applied moves=" + applied.Count + "/" + moves.Count +
                                  " acting=" + actingNetId + " unsynced=" + unsynced);
                    }
                    else
                    {
                        byte[] payload = Codec.EncodeIntent(actingNetId, firstOfSession, moves, NextNonce());
                        TacticalMoveSync.SendToHost(engine, TacticalSurfaceIds.TacInventoryIntent, payload);
                        _costChargedForState = state;
                        Debug.Log("[Multiplayer][tac] GESTURE client sent tac.intent.inventory moves=" + moves.Count +
                                  " acting=" + actingNetId + " applyCost=" + firstOfSession + " unsynced=" + unsynced);
                    }
                }
                ReBaseline(module);
                if (unsynced > 0)
                {
                    // Same degrade as the close-commit rail (fresh local ground container / unreadable item), but
                    // reactive: repaint from truth NOW so the player sees the drag didn't take.
                    Debug.LogWarning("[Multiplayer][tac] GESTURE " + unsynced + " drag(s) not syncable (fresh ground " +
                                     "container or unreadable item) — reverting view; use the drop-item button to drop.");
                    RefreshOpenInventoryView("unsynced-gesture");
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] OnTacticalGesture failed: " + ex); }
        }

        /// <summary>The gesture's cross-inventory moves: per UI list in <c>CurrentInventoryLists</c>, diff
        /// <c>GetRemovedItems()</c>/<c>GetAddedItems()</c> (UnfilteredItems vs the list's <c>_initialItems</c>
        /// baseline — kept per-gesture by <see cref="ReBaseline"/>), map each list to its real
        /// <c>InventoryComponent</c> through the state's own <c>OnListRemoveItems</c>/<c>OnListAddItems</c> funcs
        /// (the exact mapping the deferred <c>AttemptMoveItems</c> uses), then pair removed↔added by reference.
        /// Item identity = (def guid, index among that def in the source's REAL pre-move items) — identical on both
        /// sides (client model untouched until mirror; host computes before applying). Unpairable/unresolvable →
        /// counted in <paramref name="unsynced"/>, never guessed.</summary>
        private static List<Codec.Move> BuildGestureMoves(object state, out object module, out int actingNetId, out int unsynced)
        {
            actingNetId = TacticalDeploySync.NetIdForLiveActor(GetProp(state, "PrimaryActor"));
            unsynced = 0;
            var moves = new List<Codec.Move>();
            module = GetProp(state, "_soldierEquipModule");   // expression-bodied property (GetProp is property-first)
            if (module == null) return moves;
            if (!(GetProp(module, "CurrentInventoryLists") is IEnumerable lists)) return moves;
            var removeMap = GetField(state, "OnListRemoveItems") as IDictionary;   // UIInventoryList → Func<Item, InventoryComponent>
            var addMap = GetField(state, "OnListAddItems") as IDictionary;
            if (removeMap == null || addMap == null) return moves;

            var removedFrom = new List<KeyValuePair<object, object>>();            // (item, source InventoryComponent)
            var addedTo = new Dictionary<object, object>(RefEq.Instance);           // item → target InventoryComponent
            foreach (var list in lists)
            {
                if (list == null) continue;
                foreach (var it in InvokeEnum(list, "GetRemovedItems"))
                    removedFrom.Add(new KeyValuePair<object, object>(it, InvokeListMap(removeMap, list, it)));
                foreach (var it in InvokeEnum(list, "GetAddedItems"))
                    addedTo[it] = InvokeListMap(addMap, list, it);
            }

            foreach (var rem in removedFrom)
            {
                object item = rem.Key, srcInv = rem.Value;
                if (item == null || srcInv == null || !addedTo.TryGetValue(item, out object dstInv) || dstInv == null)
                { unsynced++; continue; }
                addedTo.Remove(item);
                ResolveEndpointId(srcInv, out int srcNet, out byte srcSlot);
                ResolveEndpointId(dstInv, out int dstNet, out byte dstSlot);
                string guid = DefReflection.GetGuid(GetProp(item, "ItemDef"));
                int defIdx = (srcNet >= 0 && dstNet >= 0 && !string.IsNullOrEmpty(guid))
                    ? IndexAmongDefInInventory(srcInv, item, guid) : -1;
                if (defIdx < 0) { unsynced++; continue; }
                if (srcNet == dstNet && srcSlot == dstSlot) continue;   // same-inventory shuffle: no membership change
                moves.Add(new Codec.Move(srcNet, srcSlot, dstNet, dstSlot, guid, defIdx));
            }
            return moves;
        }

        /// <summary>Reset every current UI list's <c>_initialItems</c> baseline to its live contents, so the next
        /// gesture (and the deferred close-commit) diffs only what happens AFTER this relay. In-place clear+refill
        /// of the existing List&lt;ICommonItem&gt; — no generic construction across the reflection boundary.</summary>
        private static FieldInfo _uiListInitialItemsField;
        private static void ReBaseline(object module)
        {
            if (!(GetProp(module, "CurrentInventoryLists") is IEnumerable lists)) return;
            foreach (var list in lists)
            {
                if (list == null) continue;
                if (_uiListInitialItemsField == null)
                    _uiListInitialItemsField = AccessTools.Field(list.GetType(), "_initialItems");
                if (!(_uiListInitialItemsField?.GetValue(list) is IList baseline)) continue;
                var snapshot = ToObjectList(GetProp(list, "UnfilteredItems"));
                baseline.Clear();
                foreach (var it in snapshot) baseline.Add(it);
            }
        }

        /// <summary>Repaint an OPEN tactical inventory view from the real (just-mirrored) inventories via the
        /// state's own <c>InitInventory</c>/<c>InitVehicleInventory</c>, then re-baseline so the repaint itself
        /// never diffs as a phantom gesture. Skipped mid-drag (a repaint would clobber the dangling drag icon —
        /// truth lands on the next mirror/close instead). ponytail: refreshes the primary+storage lists only; an
        /// open secondary trade panel stays stale until reselect/close — upgrade if live soldier↔soldier trading
        /// across players becomes a real flow.</summary>
        internal static void RefreshOpenInventoryView(string reason)
        {
            try
            {
                object state = ActiveTacticalInventoryState();
                if (state == null) return;
                object module = GetProp(state, "_soldierEquipModule");
                if (module == null) return;
                object dragIcon = GetProp(module, "ItemDragIcon");
                if (dragIcon != null &&
                    AccessTools.Method(dragIcon.GetType(), "IsBeingDragged")?.Invoke(dragIcon, null) as bool? == true)
                {
                    Debug.Log("[Multiplayer][tac] GESTURE refresh deferred — drag in progress (" + reason + ")");
                    return;
                }
                bool vehicle = GetField(state, "_isVehicleInventory") as bool? == true;
                AccessTools.Method(state.GetType(), vehicle ? "InitVehicleInventory" : "InitInventory")
                    ?.Invoke(state, null);
                ReBaseline(module);
                Debug.Log("[Multiplayer][tac] GESTURE refreshed open inventory view (" + reason + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] RefreshOpenInventoryView failed: " + ex); }
        }

        /// <summary>The live <c>UIStateInventory</c> when it is the CURRENT tactical view state, else null (covers
        /// "screen not open", geoscape, and no live mission — <c>LiveTlc</c> is null outside a mission).</summary>
        private static Type _uiStateInventoryType;
        private static bool _uiStateInventoryResolved;
        private static object ActiveTacticalInventoryState()
        {
            if (!_uiStateInventoryResolved)
            {
                _uiStateInventoryResolved = true;
                _uiStateInventoryType = AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewStates.UIStateInventory");
            }
            if (_uiStateInventoryType == null) return null;
            object view = GetProp(TacticalDeploySync.LiveTlc, "View");
            object current = GetProp(view, "CurrentState");
            return current != null && _uiStateInventoryType.IsInstanceOfType(current) ? current : null;
        }

        /// <summary>Index of <paramref name="item"/> among items of the same def in the REAL inventory's current
        /// (pre-move) contents — the wire identity <see cref="NthItemOfDef"/> resolves on the other side. -1 when
        /// the item is not in that inventory (e.g. it lives in a fresh local ground container).</summary>
        private static int IndexAmongDefInInventory(object inv, object item, string guid)
        {
            if (!(GetProp(inv, "Items") is IEnumerable items)) return -1;
            int seen = 0;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (!string.Equals(DefReflection.GetGuid(GetProp(it, "ItemDef")), guid, StringComparison.Ordinal)) continue;
                if (ReferenceEquals(it, item)) return seen;
                seen++;
            }
            return -1;
        }

        private static IEnumerable InvokeEnum(object obj, string method)
            => (AccessTools.Method(obj.GetType(), method)?.Invoke(obj, null) as IEnumerable) ?? new object[0];

        /// <summary>Invoke the state's list→inventory mapping func (<c>Func&lt;Item, InventoryComponent&gt;</c>)
        /// for one item; null on any failure (missing list key, closure NRE, cast) — the caller degrades.</summary>
        private static object InvokeListMap(IDictionary map, object list, object item)
        {
            try { return (map[list] as Delegate)?.DynamicInvoke(item); }
            catch { return null; }
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
            // TEMP diagnostic — remove after inventory-sync RCA: one throttle-free line per capture disambiguating
            // "UI diff never populated" (every query rem/add == 0) vs "pairing drops moves" (rem > 0 but moves < rem).
            var diag = new System.Text.StringBuilder();
            foreach (var r in recs)
                diag.Append(" q[net=").Append(r.NetId).Append(",slot=").Append(r.Slot)
                    .Append(" rem=").Append(r.Removed.Count).Append(" add=").Append(r.Added.Count).Append(']');
            Debug.Log("[Multiplayer][tac] CaptureMoves actingNetId=" + actingNetId + " queries=" + recs.Count +
                      " moves=" + moves.Count + " unsyncedDrops=" + unsyncedDrops + " |" + diag);
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
