using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.View.ViewControllers.Inventory;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Interception.Equipments;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    // ponytail: ManufactureSync + ResearchSync are now TWO order-channel seams sharing the same
    // primitives (SurfaceSeq/IntentDedup/SyncApplyScope/queue-snapshot+intent shape). Do NOT extract a
    // generic OrderChannel yet — it would touch the in-game-verified ResearchSync. Extract once a 3rd
    // queue appears, or after both are in-game-verified.

    /// <summary>
    /// Migration step 3 — MANUFACTURING QUEUE on the rail. The timed manufacture queue
    /// (<see cref="ItemManufacturing"/>._queue) is an inherently UN-KEYABLE list: duplicate item defs are
    /// allowed, so its elements have no stable id and the generic value rail (DiffEngine 0xAC) EXCLUDES the
    /// whole collection at walk time ("element has no stable key"). This is the sanctioned catalog seam the
    /// mandate permits for such a list: a host→all ORDER snapshot carrying the queue as an explicit ordered
    /// list of (RelatedItemDef.Guid, AccumulatedPoints), plus client→host action intents. Scope = STEP 1:
    /// the queue + client intents only. STEP 2 (a made/finished item landing in the client item locker) is
    /// a separate cross-cutting inventory structural gap and is NOT addressed here.
    ///
    /// HOST → ALL (surface GeoManufacture 0xAD): the full PhoenixFaction queue ORDER + per-element
    /// AccumulatedPoints, pushed on every structural change (native OnItemAdded/OnItemRemoved/OnItemCompleted/
    /// OnQueueReordered event = an apply point → immediate push) and by the ≤2 Hz poll as the drift backstop
    /// (which also carries progress-only AccumulatedPoints changes — those fire no native event). The whole
    /// queue is the delta; there is no separate value channel (the generic rail cannot address un-keyable
    /// elements). _lastSentQueue coalesces an immediate push and the following poll tick into one send.
    ///
    /// CLIENT (projector, law 3): rebuilds ItemManufacturing.Queue IN PLACE (same List instance) from the
    /// snapshot — resolve each def guid to the live ManufacturableItem, new ManufactureQueueItem, restore
    /// AccumulatedPoints — inside SyncApplyScope (law 8), then repaints the open manufacturing screen
    /// (SetupQueue, law 11). No game logic runs on the client (no ManufactureItem/FinishManufactureItem).
    ///
    /// CLIENT → HOST (surface GeoManufactureIntent 0xAE): the manufacturing screen's entry points
    /// (ItemManufacturing.ManufactureItem / Cancel(int) / Cancel(ManufactureQueueItem) / PutInFromOfQueue /
    /// PutUpInQueue / PutDownInQueue — all reachable from UIModuleManufacturing) are intent-capture Harmony
    /// prefixes (law 4a): on the client they BLOCK the native call and send an intent addressed by def guid
    /// (add) or by queue INDEX + a def-at-index guard (cancel/reorder — the un-keyable list is addressed by
    /// position); on the host (and inside SyncApplyScope) native code runs untouched. Transport + nonce +
    /// dedup + dispatch + reject discipline ride <see cref="IntentRail"/> (surface 0xAE registered in
    /// RegisterIntents); the op handlers here validate against HOST state (def-at-index still matches,
    /// else the client's view was stale → reject) → execute the SAME native method → the observe seams
    /// broadcast the outcome. Invalid intents reject through IntentRail (log + forced queue resend).
    ///
    /// SIM GATING (law 4b): NOT added here — ItemManufacturing.Update() is driven only by
    /// GeoLevelController.LevelHourlyUpdateCrt, which <see cref="ClientSimGate"/> already freezes on clients.
    /// </summary>
    public static class ManufactureSync
    {
        // Intent ops (GeoManufactureIntent inner payload) — each maps 1:1 onto a native ItemManufacturing method.
        private const byte OpQueue = 1;   // ManufactureItem(def)  — add/start (also the instant path when cost==0)
        private const byte OpCancel = 2;  // Cancel(index)
        private const byte OpFront = 3;   // PutInFromOfQueue(element)
        private const byte OpUp = 4;      // PutUpInQueue(element)
        private const byte OpDown = 5;    // PutDownInQueue(element)
        // Instant scrap (UIModuleManufacturing.ScrapAllItems, no queue). index slot is reused as the item COUNT.
        // internal: EquipSync's GeoFaction.ScrapItem capture (equip-screen scrap dialog) reuses this op.
        internal const byte OpScrap = 6;       // GeoFaction.ScrapItem(item, count)   — item-storage scrap
        private const byte OpScrapVehicle = 7; // GeoFaction.ScrapVehicleEquipment(v) — vehicle-equipment scrap (count unused)
        // Soldier-loadout intent (per-GESTURE, sent by EquipSync's UI seams) — rides THIS surface (0xAE) but
        // its payload after [nonce][op] is EquipSync's own ([charId][flags][slot triples]); HandleIntent
        // branches early, before the fixed [defGuid][index] header the other ops share.
        internal const byte OpSetItems = 8;
        // Augment install (UIModuleMutate/UIModuleBionics.OnAugmentApplied confirm). The augment def is
        // BOUGHT on the spot (Wallet.Take(ManufacturePrice)), never taken from storage, so it cannot ride
        // OpSetItems' storage validation. defGuid slot = augment def, index slot = charId. EquipSync owns
        // validation + native replay. ponytail: id 9 deliberately left free — never reuse ids.
        internal const byte OpAugment = 10;
        // Equip-screen QUICK PRODUCE (the side button on an empty slot): the native handler
        // UIStateEditSoldier.ItemManufactureHandler:615 / UIStateEditVehicle:166 does Wallet.Take +
        // `new GeoItem(def)` INLINE and never touches ItemManufacturing — so none of the intent seams above
        // ever saw it, and a client bought items that existed only on that client, forever (RCA 2026-07-18).
        // defGuid slot = the item def; index unused.
        private const byte OpQuickProduce = 11;
        // Equip-screen scrap of an EQUIPPED item — per-INSTANCE, unlike def-addressed OpScrap. Payload
        // after [nonce][op] is EquipSync's own ([charId][list][slot triple][amount]); HandleIntent branches
        // early like OpSetItems. EquipSync owns capture, validation and the native replay.
        internal const byte OpScrapEquipped = 12;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        // ─── Host observe state ────────────────────────────────────────────
        private static ItemManufacturing _hooked;   // faction Manufacture we subscribed to (unhook on level change)
        private static float _nextTickAt;            // realtime throttle (0.5 s → max 2 Hz)
        private static string _lastSentQueue;        // "guid:intPts|"… joined (order + rounded-points sensitive)

        private static readonly MethodInfo SetupQueueMethod =
            AccessTools.Method(typeof(UIModuleManufacturing), "SetupQueue");

        // DoFilter(Predicate<ItemDef>, Func<ItemDef,IComparable>) rebuilds the available-item LIST + owned
        // counts (RefreshFilters+GetItems+RefreshItemList) — the part SetupQueue misses. null,null = keep the
        // active tab (GetFilter(_useClassFilter)) + default order (verified null-safe in the decompile).
        private static readonly MethodInfo DoFilterMethod =
            AccessTools.Method(typeof(UIModuleManufacturing), "DoFilter",
                new[] { typeof(Predicate<ItemDef>), typeof(Func<ItemDef, IComparable>) });

        // Client scrap carts (UI-local, not game state) — read to build scrap intents, then cleared optimistically.
        private static readonly AccessTools.FieldRef<UIModuleManufacturing, ItemStorage> ScrapItemsField =
            AccessTools.FieldRefAccess<UIModuleManufacturing, ItemStorage>("_scrapItems");
        private static readonly AccessTools.FieldRef<UIModuleManufacturing, VehicleEquipmentStorage> VehicleScrapItemsField =
            AccessTools.FieldRefAccess<UIModuleManufacturing, VehicleEquipmentStorage>("_vehicleEquipmentScrapItems");

        // Scrap-mode AVAILABLE-list feeds: SNAPSHOT copies of faction storage taken once on mode entry
        // (UIModuleManufacturing.Init:363-371) — see ResyncScrapSnapshot.
        private static readonly AccessTools.FieldRef<UIModuleManufacturing, ItemStorage> ScrapStorageField =
            AccessTools.FieldRefAccess<UIModuleManufacturing, ItemStorage>("_scrapStorage");
        private static readonly AccessTools.FieldRef<UIModuleManufacturing, VehicleEquipmentStorage> VehicleScrapStorageField =
            AccessTools.FieldRefAccess<UIModuleManufacturing, VehicleEquipmentStorage>("_vehicleEquipmentScrapStorage");
        private static readonly MethodInfo StripPartialMagsMethod =
            AccessTools.Method(typeof(UIModuleManufacturing), "StripPartialMagsFromScrapStorage");

        // ─── Lifecycle (driven by SyncEngine) ──────────────────────────────

        public static void Reset()
        {
            ResetForReloadBoundary();
            Seq.Reset();
        }

        /// <summary>Arm the 0xAE surface on the generic intent engine. Ops with the shared [defGuid][index]
        /// body ride <see cref="HandleDefIndexIntent"/>; the EquipSync-owned payloads (loadout / equipped-
        /// scrap) get their own table entries — the old hand-rolled early-branching inside one switch is
        /// now just registration. Reconverge on reject = forced queue-order resend (the un-keyable queue
        /// rides the 0xAD order channel, out of ForceReemit's reach; content-identical resends are an
        /// in-place rebuild + repaint on clients, harmless at reject rate).</summary>
        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>();
            foreach (byte op in new[] { OpQueue, OpCancel, OpFront, OpUp, OpDown,
                                        OpScrap, OpScrapVehicle, OpAugment, OpQuickProduce })
                ops[op] = HandleDefIndexIntent;
            ops[OpSetItems] = (engine, peer, nonce, op, r) => EquipSync.HandleIntent(engine, peer, nonce, r);
            ops[OpScrapEquipped] = (engine, peer, nonce, op, r) => EquipSync.HandleScrapEquippedIntent(peer, nonce, r);
            IntentRail.Register(SurfaceIds.GeoManufactureIntent, "manufacture", ops, ForceQueueResend);
        }

        private static void ForceQueueResend()
        {
            _lastSentQueue = null;
            PushQueueSnapshot(NetworkEngine.Instance); // self-guards non-host / no session / no geoscape
        }

        /// <summary>Mid-session reload boundary (rca-3 contract): drop the dying-geoscape hook + the last-sent
        /// mark (post-reload state re-emitted), KEEP the seq streams + intent nonce (persist symmetrically).</summary>
        public static void ResetForReloadBoundary()
        {
            Unhook();
            _lastSentQueue = null;
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        private static ItemManufacturing PhoenixManufacture() => GeoLevel()?.PhoenixFaction?.Manufacture;

        // ─── HOST: hook management + ≤2 Hz queue snapshot poll ─────────────

        public static void HostTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsHost || !engine.IsActiveSession) return;
            if (Time.realtimeSinceStartup < _nextTickAt) return;
            _nextTickAt = Time.realtimeSinceStartup + 0.5f;

            var manufacture = PhoenixManufacture();
            if (manufacture == null) { Unhook(); return; }
            if (!ReferenceEquals(manufacture, _hooked))
            {
                Unhook();
                manufacture.OnItemAdded += HostOnOrderChanged;
                manufacture.OnItemRemoved += HostOnOrderChanged;
                manufacture.OnItemCompleted += HostOnItemChanged;
                manufacture.OnQueueReordered += HostOnReordered;
                _hooked = manufacture;
                Debug.Log("[Multiplayer][rail] ManufactureSync: hooked queue events (host)");
            }

            // Drift backstop (≤2 Hz): the known apply points push immediately (HandleIntent success + the
            // structural events above), so a change no longer waits up to 500 ms for this tick. This tick is
            // the sole carrier of progress-only AccumulatedPoints changes (no native event fires for those).
            PushQueueSnapshot(engine);
        }

        private static void HostOnOrderChanged(ItemManufacturing.ManufactureQueueItem item, int index) => HostPush();
        private static void HostOnItemChanged(ItemManufacturing.ManufactureQueueItem item) => HostPush();
        private static void HostOnReordered() => HostPush();

        private static void HostPush()
        {
            var engine = NetworkEngine.Instance;
            if (engine != null && engine.IsHost && engine.IsActiveSession) PushQueueSnapshot(engine);
        }

        /// <summary>Send the full queue snapshot NOW if it changed since the last send (order + rounded
        /// points). Shared behind the poll and the immediate pushes; the _lastSentQueue compare doubles as
        /// the double-send guard, so an immediate push then a poll tick landing right after is a no-op.</summary>
        private static void PushQueueSnapshot(NetworkEngine engine)
        {
            if (engine == null || !engine.IsHost || !engine.IsActiveSession) return;
            var manufacture = PhoenixManufacture();
            if (manufacture == null) return;

            var entries = new List<KeyValuePair<string, float>>(manufacture.Queue.Count);
            var sb = new StringBuilder();
            foreach (var qi in manufacture.Queue)
            {
                var def = qi?.ManufacturableItem?.RelatedItemDef;
                if (def == null) continue;
                entries.Add(new KeyValuePair<string, float>(def.Guid, qi.AccumulatedPoints));
                // int points in the key (production accrues in integer steps → no float jitter → no churn).
                sb.Append(def.Guid).Append(':').Append((int)qi.AccumulatedPoints).Append('|');
            }
            string queueKey = sb.ToString();
            if (queueKey == _lastSentQueue) return;
            _lastSentQueue = queueKey;
            Send(engine, EncodeSnapshot(Seq.Next(SurfaceIds.GeoManufacture), entries));
        }

        private static void Unhook()
        {
            if (_hooked == null) return;
            try
            {
                _hooked.OnItemAdded -= HostOnOrderChanged;
                _hooked.OnItemRemoved -= HostOnOrderChanged;
                _hooked.OnItemCompleted -= HostOnItemChanged;
                _hooked.OnQueueReordered -= HostOnReordered;
            }
            catch { }
            _hooked = null;
        }

        private static void Send(NetworkEngine engine, byte[] inner)
        {
            try
            {
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoManufacture, SyncKind.StateDelta, inner);
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.LogError("[Multiplayer][rail] ManufactureSync: payload exceeds envelope u16 — " + ex.Message);
            }
        }

        // ─── Inbound (armed as part of SurfaceRouter.GeoscapeInbound) ──────

        /// <summary>Returns true when the surface was consumed (GeoManufacture; the 0xAE intent surface
        /// rides <see cref="IntentRail.HandleInbound"/>).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoManufacture) return false;
            if (engine == null || engine.IsHost) return true; // host never applies its own surface
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    if (!Seq.ShouldApply(SurfaceIds.GeoManufacture, seq)) return true; // stale re-send
                    int n = r.ReadUInt16();
                    var entries = new List<KeyValuePair<string, float>>(n);
                    for (int i = 0; i < n; i++)
                    {
                        string guid = r.ReadString();
                        float pts = r.ReadSingle();
                        entries.Add(new KeyValuePair<string, float>(guid, pts));
                    }
                    ApplySnapshot(entries, seq);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] ManufactureSync inbound failed: " + ex); }
            return true;
        }

        // ─── HOST: intent apply (validate → execute NATIVELY; dedup/decode/reject = IntentRail) ──────

        /// <summary>Every 0xAE op with the shared [defGuid][index] body (the EquipSync-owned payloads
        /// have their own table entries — see <see cref="RegisterIntents"/>).</summary>
        private static void HandleDefIndexIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string defGuid = r.ReadString();
            int index = r.ReadInt32();

            var manufacture = PhoenixManufacture();
            if (manufacture == null)
            {
                IntentRail.Reject(SurfaceIds.GeoManufactureIntent, senderPeerId, "no manufacture op=" + op);
                return;
            }

            bool ok = false;
            if (op == OpQueue)
            {
                var item = manufacture.ManufacturableItems.FirstOrDefault(
                    i => i.RelatedItemDef != null && i.RelatedItemDef.Guid == defGuid);
                ok = item != null && manufacture.CanManufacture(item) == ItemManufacturing.ManufactureFailureReason.None;
                if (ok)
                {
                    manufacture.ManufactureItem(item);
                    // [mfgdiag] boundary: resulting host storage count for this def (real-loss vs visual proof; remove after diag).
                    // The storage LOOKUP sits inside the guard too — it is diag-only work, not just a log.
                    if (MpDiag.On)
                    {
                        var st = GeoLevel()?.PhoenixFaction?.ItemStorage;
                        int made = (st != null && item.RelatedItemDef != null && st.Items.TryGetValue(item.RelatedItemDef, out var gi))
                            ? gi.CommonItemData.Count : -1;
                        Debug.Log("[Multiplayer][mfgdiag] HOST made def=" + defGuid + " nonce=" + nonce + " -> hostStorageCount=" + made);
                    }
                }
                else IntentRail.Reject(SurfaceIds.GeoManufactureIntent, senderPeerId, "cannot manufacture " + defGuid);
            }
            else if (op == OpScrap)
            {
                // index carries the scrap COUNT — < 1 rejected (GeoFaction.ScrapItem's loop would silently
                // no-op, :1219, and the handler must not report ok on it). ScrapItem refunds+subtracts in
                // place but does NOT remove the emptied GeoItem (UIModuleManufacturing.ScrapAllItems does) — so we do.
                var def = GameUtl.GameComponent<DefRepository>()?.GetDef(defGuid) as ItemDef;
                var storage = GeoLevel()?.PhoenixFaction?.ItemStorage;
                if (index >= 1 && def != null && storage != null && storage.Items.TryGetValue(def, out var gi) &&
                    gi.CommonItemData.Count >= index)
                {
                    GeoLevel().PhoenixFaction.ScrapItem(gi, index);
                    if (gi.CommonItemData.IsEmpty()) storage.RemoveItem(gi);
                    ok = true;
                }
                else IntentRail.Reject(SurfaceIds.GeoManufactureIntent, senderPeerId,
                                       "scrap missing/insufficient " + defGuid + " need=" + index);
                if (MpDiag.On) Debug.Log("[MP][scrap] HOST scrapped " + defGuid + " x" + index + " ok=" + ok);
            }
            else if (op == OpScrapVehicle)
            {
                var def = GameUtl.GameComponent<DefRepository>()?.GetDef(defGuid) as GeoVehicleEquipmentDef;
                var air = GeoLevel()?.PhoenixFaction?.AircraftItemStorage;
                if (def != null && air != null && air.HasItem(def))
                {
                    var veh = air.PopItem(def);   // removes from storage
                    if (veh != null) { GeoLevel().PhoenixFaction.ScrapVehicleEquipment(veh); ok = true; }
                }
                else IntentRail.Reject(SurfaceIds.GeoManufactureIntent, senderPeerId, "scrapVehicle missing " + defGuid);
                if (MpDiag.On) Debug.Log("[MP][scrap] HOST scrapped " + defGuid + " x1 ok=" + ok);
            }
            else if (op == OpAugment)
            {
                // Augment install: index slot = charId. Bought with resources, not storage — EquipSync
                // owns the cost/fit validation + the native replay (and its own scoped rejects).
                ok = EquipSync.HandleAugmentIntent(senderPeerId, nonce, defGuid, index);
            }
            else if (op == OpQuickProduce)
            {
                // Host replay of the equip-screen buy, with the affordability check the native handler
                // never does: Wallet.Take(ResourcePack) calls HasResources and DISCARDS the result
                // (Wallet.cs:63-65), so without this gate a stale client could overdraw the wallet.
                // The item lands in the shared faction storage — the one place whose contents already
                // mirror to every peer (GeoItemDict on 0xAC); the buyer then equips it with a normal
                // loadout gesture. We do NOT try to reproduce which UI slot the click targeted:
                // that is view state, and guessing identity placement is exactly what law 3 forbids.
                var def = GameUtl.GameComponent<DefRepository>()?.GetDef(defGuid) as ItemDef;
                var faction = GeoLevel()?.PhoenixFaction;
                if (def != null && faction?.Wallet != null && faction.ItemStorage != null &&
                    manufacture.ManufacturableItems.Any(i => i.RelatedItemDef == def) &&
                    faction.Wallet.HasResources(def.ManufacturePrice))
                {
                    faction.Wallet.Take(def.ManufacturePrice, OperationReason.Purchase);
                    faction.ItemStorage.AddItem(new GeoItem(def));
                    ok = true;
                }
                else IntentRail.Reject(SurfaceIds.GeoManufactureIntent, senderPeerId,
                                       "quick-produce " + (def == null ? "unknown def" : "not manufacturable or unaffordable") +
                                       " " + defGuid);
                if (MpDiag.On) Debug.Log("[MP][quickproduce] HOST bought " + defGuid + " ok=" + ok);
            }
            else
            {
                var queue = manufacture.Queue;
                // Address the un-keyable list by position, but guard against an index race: the def at that
                // index must still be the one the client acted on (else its view was stale → reject; the
                // next snapshot re-syncs it).
                if (index < 0 || index >= queue.Count ||
                    queue[index]?.ManufacturableItem?.RelatedItemDef?.Guid != defGuid)
                {
                    IntentRail.Reject(SurfaceIds.GeoManufactureIntent, senderPeerId,
                                      "stale index " + index + " def=" + defGuid + " op=" + op);
                    return;
                }
                var element = queue[index];
                switch (op)
                {
                    case OpCancel: manufacture.Cancel(index); ok = true; break;
                    case OpFront: manufacture.PutInFromOfQueue(element); ok = true; break;   // native self-guards element!=Current
                    case OpUp: manufacture.PutUpInQueue(element); ok = true; break;          // native self-guards element!=Current
                    case OpDown: manufacture.PutDownInQueue(element); ok = true; break;      // native self-guards element!=Last
                }
            }

            if (ok)
            {
                // The native call fires the observe events (→ HostPush → snapshot) already; the explicit
                // push is the belt (coalesced by _lastSentQueue). The host executed the native method
                // DIRECTLY (not via a UIModuleManufacturing button), so its own open screen is pull-model
                // stale — repaint it like the client-apply path does.
                RepaintManufacturingUi();
                PushQueueSnapshot(engine);
                Debug.Log("[Multiplayer][rail] ManufactureSync HOST intent APPLIED op=" + op + " def=" + defGuid +
                          " idx=" + index + " peer=" + senderPeerId);
            }
        }

        // ─── CLIENT: snapshot apply (inside SyncApplyScope, law 8) ─────────

        private static void ApplySnapshot(List<KeyValuePair<string, float>> entries, uint seq)
        {
            var manufacture = PhoenixManufacture();
            if (manufacture == null) return;
            using (SyncApplyScope.Enter())
            {
                // Rebuild the live queue IN PLACE (same List instance — Queue exposes _queue by reference) to
                // the host's exact order. No native game logic runs: we build ManufactureQueueItems directly.
                var queue = manufacture.Queue;
                queue.Clear();
                foreach (var e in entries)
                {
                    // Fast path: reuse the client's local ManufacturableItem (carries any host-applied cost
                    // multiplier) — populated for STARTING items only. Fall back to the def repo by GUID for
                    // RESEARCH-unlocked items: the unlock chain is host-only, so those are ABSENT from the
                    // client's ManufacturableItems and the local lookup misses (most real items are researched).
                    var item = manufacture.ManufacturableItems.FirstOrDefault(
                        i => i.RelatedItemDef != null && i.RelatedItemDef.Guid == e.Key);
                    if (item == null)
                    {
                        var def = GameUtl.GameComponent<DefRepository>()?.GetDef(e.Key) as ItemDef;
                        if (def == null)
                        {
                            Debug.LogWarning("[Multiplayer][rail] ManufactureSync: unknown manufacturable def " + e.Key + " — skipped");
                            continue;
                        }
                        // ponytail: def-rebuilt costs ignore live SetCostMultiplier tweaks (ManufacturableItem
                        // .cs:50-74) so the queue % / price display can drift on such items — display-only on a
                        // projector client (intents are def-addressed, the host recosts natively); ship costs
                        // in the snapshot entries if it ever matters.
                        item = new ManufacturableItem(def);
                    }
                    queue.Add(new ItemManufacturing.ManufactureQueueItem(item) { AccumulatedPoints = e.Value });
                }
                RepaintManufacturingUi();
                Seq.Mark(SurfaceIds.GeoManufacture, seq);
                Debug.Log("[Multiplayer][rail] ManufactureSync CLIENT applied queue count=" + queue.Count);
            }
        }

        // Law 11: UIModuleManufacturing is pull-model (it subscribes to no ItemManufacturing / ItemStorage
        // model events). A snapshot/intent/storage delta landing while the screen is OPEN must rebuild it in
        // place. SetupQueue rebuilds the current-item + queue sub-panel; DoFilter rebuilds the available-item
        // list + owned counts. Both are idempotent (pure re-read of the live model), reproducing exactly the
        // native queue-add + tab-switch refresh. Screen closed → no-op (next open re-Inits natively).
        internal static void RepaintManufacturingUi()
        {
            try
            {
                var view = GeoLevel()?.View;
                if (view == null || !(view.CurrentViewState is UIStateManufacturing)) return;
                var module = view.GeoscapeModules?.ManufacturingModule;
                if (module == null) return;
                if (module.Mode == UIModuleManufacturing.UIMode.Scrap) ResyncScrapSnapshot(module);
                SetupQueueMethod?.Invoke(module, null);
                DoFilterMethod?.Invoke(module, new object[] { null, null });
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer][rail] ManufactureSync: screen rebuild failed: " + ex.Message); }
        }

        /// <summary>Scrap mode feeds its available-item list from SNAPSHOT copies of faction storage taken
        /// once on mode entry (UIModuleManufacturing.Init:363-371 — Clear + AddItems + StripPartialMags),
        /// never re-read: natively safe because the screen pauses the game so storage cannot move, but in
        /// co-op stale the instant a remote delta lands — SetupQueue/DoFilter alone rebuild FROM the stale
        /// snapshot, which is why the open scrap screen never changed. Replay the native snapshot block
        /// against live storage, preserving the user's staged cart (native re-snapshot dumps it — that is
        /// mid-gesture state, same category as the _filter this screen's Exit+Enter opt-out protects):
        /// clamp each staged count to what live storage still holds (a remote peer may have scrapped the
        /// same items; also keeps the host's native ScrapAllItems from dereferencing a vanished def at
        /// UIModuleManufacturing.cs:858-859), then subtract the cart so staged items don't double-show.</summary>
        private static void ResyncScrapSnapshot(UIModuleManufacturing module)
        {
            var faction = GeoLevel()?.PhoenixFaction;
            if (faction?.ItemStorage == null || faction.AircraftItemStorage == null) return;

            var cart = ScrapItemsField(module);
            foreach (var kv in cart.Items.ToList())
            {
                int live = faction.ItemStorage.Items.TryGetValue(kv.Key, out var gi) ? gi.CommonItemData.Count : 0;
                int extra = kv.Value.CommonItemData.Count - live;
                // Same GeoItem shape the native cart-return path uses (RemoveFromScrapQueue :846).
                if (extra > 0) cart.RemoveItem(new GeoItem(kv.Key, extra, kv.Key.ChargesMax));
            }
            var vehCart = VehicleScrapItemsField(module);
            foreach (var def in vehCart.Items.Select(v => v.EquipmentDef).Distinct().ToList())
            {
                int extra = vehCart.CountItem(def) - faction.AircraftItemStorage.CountItem(def);
                for (int i = 0; i < extra; i++) vehCart.RemoveItem(def);
            }

            var snapshot = ScrapStorageField(module);
            snapshot.Clear();
            snapshot.AddItems(faction.ItemStorage);
            StripPartialMagsMethod?.Invoke(module, null);
            snapshot.RemoveItems(cart);
            var vehSnapshot = VehicleScrapStorageField(module);
            vehSnapshot.Clear();
            vehSnapshot.AddItems(faction.AircraftItemStorage);
            vehSnapshot.RemoveItems(vehCart);
        }

        // ─── CLIENT: intent capture (fed by the Harmony prefixes below) ────

        /// <summary>TRUE = run the native method (host, no session, apply scope, or a non-Phoenix manufacture);
        /// FALSE = client blocks it (caller sends the intent instead).</summary>
        private static bool ShouldRunNative(ItemManufacturing instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            if (SyncApplyScope.Active) return true;                       // law 8: applying never echoes an intent
            return !ReferenceEquals(instance, PhoenixManufacture());      // NPC/foreign manufacture stays native (un-synced)
        }

        /// <summary>The shared [defGuid][index] intent body on 0xAE (EquipSync's scrap/augment captures
        /// reuse it too). Nonce + envelope + send ride <see cref="IntentRail.Send"/>.</summary>
        internal static void SendIntent(byte op, string defGuid, int index)
            => IntentRail.Send(SurfaceIds.GeoManufactureIntent, op, "op=" + op + " def=" + defGuid + " idx=" + index,
                               w => { w.Write(defGuid ?? ""); w.Write(index); });

        private static bool CaptureAdd(ItemManufacturing instance, ManufacturableItem item)
        {
            if (ShouldRunNative(instance)) return true;
            var def = item?.RelatedItemDef;
            if (def == null) return false;
            SendIntent(OpQueue, def.Guid, 0);
            return false;
        }

        private static bool CaptureIndex(ItemManufacturing instance, int index, byte op)
        {
            if (ShouldRunNative(instance)) return true;
            var queue = instance.Queue;
            if (index < 0 || index >= queue.Count) return false;
            var def = queue[index]?.ManufacturableItem?.RelatedItemDef;
            if (def == null) return false;
            SendIntent(op, def.Guid, index);
            return false;
        }

        private static bool CaptureElement(ItemManufacturing instance, ItemManufacturing.ManufactureQueueItem element, byte op)
        {
            if (ShouldRunNative(instance)) return true;
            int index = instance.Queue.IndexOf(element);
            var def = element?.ManufacturableItem?.RelatedItemDef;
            if (index < 0 || def == null) return false;
            SendIntent(op, def.Guid, index);
            return false;
        }

        // ─── Harmony seams (the ONLY patches this subsystem owns, law 4a) ──

        /// <summary>Intent capture (law 4a): every manufacturing-screen entry point on ItemManufacturing.</summary>
        [HarmonyPatch(typeof(ItemManufacturing))]
        internal static class IntentCapturePatches
        {
            [HarmonyPrefix, HarmonyPatch(nameof(ItemManufacturing.ManufactureItem))]
            private static bool ManufacturePrefix(ItemManufacturing __instance, ManufacturableItem item)
                => CaptureAdd(__instance, item);

            [HarmonyPrefix, HarmonyPatch("Cancel", new[] { typeof(int) })]
            private static bool CancelIndexPrefix(ItemManufacturing __instance, int index)
                => CaptureIndex(__instance, index, OpCancel);

            [HarmonyPrefix, HarmonyPatch("Cancel", new[] { typeof(ItemManufacturing.ManufactureQueueItem) })]
            private static bool CancelElementPrefix(ItemManufacturing __instance, ItemManufacturing.ManufactureQueueItem element)
                => CaptureElement(__instance, element, OpCancel);

            [HarmonyPrefix, HarmonyPatch(nameof(ItemManufacturing.PutInFromOfQueue))]
            private static bool FrontPrefix(ItemManufacturing __instance, ItemManufacturing.ManufactureQueueItem element)
                => CaptureElement(__instance, element, OpFront);

            [HarmonyPrefix, HarmonyPatch(nameof(ItemManufacturing.PutUpInQueue))]
            private static bool UpPrefix(ItemManufacturing __instance, ItemManufacturing.ManufactureQueueItem element)
                => CaptureElement(__instance, element, OpUp);

            [HarmonyPrefix, HarmonyPatch(nameof(ItemManufacturing.PutDownInQueue))]
            private static bool DownPrefix(ItemManufacturing __instance, ItemManufacturing.ManufactureQueueItem element)
                => CaptureElement(__instance, element, OpDown);
        }

        /// <summary>Intent capture for INSTANT scrap (UIModuleManufacturing.ScrapAllItems). On the client:
        /// send one scrap intent per staged cart entry, optimistically empty the local carts, BLOCK the native
        /// call (no local storage/wallet mutation → no revert-flicker). Host result rides the storage +
        /// wallet 0xAC value rail; the universal open-UI repaint refreshes every client screen.</summary>
        [HarmonyPatch(typeof(UIModuleManufacturing), nameof(UIModuleManufacturing.ScrapAllItems))]
        internal static class ScrapCapturePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(UIModuleManufacturing __instance)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost || SyncApplyScope.Active)
                    return true; // host / no session / apply-scope → run native authoritatively

                var scrapCart = ScrapItemsField(__instance);
                foreach (var kv in scrapCart.Items)
                {
                    int count = kv.Value?.CommonItemData?.Count ?? 0;
                    if (kv.Key == null || count <= 0) continue;
                    SendIntent(OpScrap, kv.Key.Guid, count);
                    if (MpDiag.On) Debug.Log("[MP][scrap] sent Op=" + OpScrap + " def=" + kv.Key.Guid + " count=" + count);
                }
                var vehCart = VehicleScrapItemsField(__instance);
                foreach (var ve in vehCart.Items)
                {
                    var def = ve?.EquipmentDef;
                    if (def == null) continue;
                    SendIntent(OpScrapVehicle, def.Guid, 0);
                    if (MpDiag.On) Debug.Log("[MP][scrap] sent Op=" + OpScrapVehicle + " def=" + def.Guid + " count=1");
                }

                // Optimistic UI: empty the carts + disable the button + rebuild the scrap panel (mirrors native's
                // own post-scrap UI; the real storage/wallet change arrives via the host rails).
                scrapCart.Clear();
                vehCart.Clear();
                __instance.ScrapAllButton.SetInteractable(isInteractable: false);
                SetupQueueMethod?.Invoke(__instance, null);
                return false;
            }
        }

        /// <summary>Intent capture (law 4a) for the equip-screen QUICK-PRODUCE button — the hole that made
        /// the mirror diverge. The button's own handler buys the item with an inline
        /// <c>Wallet.Take</c> + <c>new GeoItem(def)</c> (UIStateEditSoldier:617-619, UIStateEditVehicle:168-170)
        /// and never calls <c>ItemManufacturing</c>, so every seam this class owns missed it.
        ///
        /// Patched at the BUTTON, not at the two handlers: one seam covers the soldier AND vehicle equip
        /// screens (both route through this MonoBehaviour), and blocking here means the client never even
        /// creates the local <c>GeoItem</c> — returning null from the handler instead would NRE in the
        /// caller, which dereferences the result unconditionally (UIInventorySlotSideButton:332-341).
        /// Only the BUY branch is intercepted: ActionNeededFree merely moves an item that already exists
        /// (EquipSync's loadout intent carries that) and Repair is its own confirm flow.</summary>
        [HarmonyPatch(typeof(UIInventorySlotSideButton), "OnSideButtonPressed")]
        internal static class QuickProduceCapturePatch
        {
            private static readonly AccessTools.FieldRef<UIInventorySlotSideButton, UIInventorySlotSideButton.GeneralState> StateField =
                AccessTools.FieldRefAccess<UIInventorySlotSideButton, UIInventorySlotSideButton.GeneralState>("_currentState");
            private static readonly AccessTools.FieldRef<UIInventorySlotSideButton, ItemDef> ItemToProduceField =
                AccessTools.FieldRefAccess<UIInventorySlotSideButton, ItemDef>("_itemToProduce");

            [HarmonyPrefix]
            private static bool Prefix(UIInventorySlotSideButton __instance)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost || SyncApplyScope.Active)
                    return true; // host / solo / apply scope → the native buy IS the authoritative one

                var state = StateField(__instance);
                if (state.State != UIInventorySlotSideButton.SideButtonState.ActionNeededAffordable ||
                    state.Action == UIInventorySlotSideButton.SideButtonAction.Repair)
                    return true;

                var def = ItemToProduceField(__instance);
                if (def == null) return true;
                SendIntent(OpQuickProduce, def.Guid, 0);
                if (MpDiag.On) Debug.Log("[MP][quickproduce] CLIENT blocked local buy, sent intent def=" + def.Guid);
                // No local Wallet.Take, no local GeoItem: the host's wallet + storage deltas on 0xAC are
                // the only source. (The client wallet IS mirrored, but a diff rail only resends on a
                // HOST-side change — a client-local Take would sit wrong until the host's wallet happened
                // to move. Blocking is the fix; the mirror is not a corrector.)
                return false;
            }
        }

        // ─── Wire codecs (inner payloads) ───────────────────────────────────

        private static byte[] EncodeSnapshot(uint seq, List<KeyValuePair<string, float>> entries)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write((ushort)Math.Min(entries.Count, ushort.MaxValue));
                for (int i = 0; i < entries.Count && i < ushort.MaxValue; i++)
                {
                    w.Write(entries[i].Key ?? "");
                    w.Write(entries[i].Value);
                }
                return ms.ToArray();
            }
        }

    }
}
