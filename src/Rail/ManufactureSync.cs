using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Base.Core;
using Base.Defs;
using Base.Serialization.General;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.View.ViewControllers.Inventory;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Interception.Equipments;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewControllers.Manufacturing;
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
        // Ops 8 (setItems), 10 (augment), 12 (scrapEquipped) moved to the 0xB3 GeoEquipIntent surface
        // 2026-07-26 (EquipSync owns them end-to-end; the surface byte IS the family). Id 9 was never
        // used. Do NOT reuse 8-10/12 on THIS surface — a stale peer would misroute them.
        // Equip-screen QUICK PRODUCE (the side button on an empty slot): the native handler
        // UIStateEditSoldier.ItemManufactureHandler:615 / UIStateEditVehicle:166 does Wallet.Take +
        // `new GeoItem(def)` INLINE and never touches ItemManufacturing — so none of the intent seams above
        // ever saw it, and a client bought items that existed only on that client, forever (RCA 2026-07-18).
        // Stays a MANUFACTURE op (host replay = wallet buy into faction storage, this family's validation).
        // defGuid slot = the item def; index unused.
        private const byte OpQuickProduce = 11;
        // Shared scrap cart: the module's cart is per-screen UI state the game clears on every
        // reopen/mode switch (UIModuleManufacturing.Init:363-371), so the shared truth is the
        // host-authoritative MOD store (ScrapCartState), mirrored to all by the generic value rail
        // as mod root "M#cart". Gesture ops ride this surface.
        private const byte OpCartAdd = 13;    // [defGuid][isVehicle:i32] — queue ONE unit for scrapping
        private const byte OpCartRemove = 14; // [defGuid][isVehicle:i32] — unqueue ONE unit
        private const byte OpCartScrap = 15;  // (body unused) — scrap the WHOLE shared cart; any peer may confirm

        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        // ─── Host observe state ────────────────────────────────────────────
        private static ItemManufacturing _hooked;   // faction Manufacture we subscribed to (unhook on level change)
        private static float _nextTickAt;            // realtime throttle (0.5 s → max 2 Hz)
        private static string _lastSentQueue;        // "guid:intPts|"… joined (order + rounded-points sensitive)

        // Shared scrap cart store — MOD state riding the generic rail as mod root "M#cart"
        // (IdentityResolver.RegisterModRoot): the host walks/diffs it like any game root; on a client
        // it is a mirror written ONLY by the generic applier. Host mutations ship via the normal flush
        // seams (IntentRail dispatch FlushNow; ShouldRunNative's FlushOnHostGesture for host gestures).
        internal const string CartRootKey = "M#cart";
        internal static readonly ScrapCartState Cart = new ScrapCartState();

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
        /// body ride <see cref="HandleDefIndexIntent"/>; the cart ops get their own table entries.
        /// (The EquipSync-owned ops moved to the 0xB3 GeoEquipIntent surface 2026-07-26.) Reconverge on
        /// reject = forced queue-order resend (the un-keyable queue rides the 0xAD order channel, out of
        /// ForceReemit's reach; content-identical resends are an in-place rebuild + repaint on clients,
        /// harmless at reject rate).</summary>
        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>();
            foreach (byte op in new[] { OpQueue, OpCancel, OpFront, OpUp, OpDown,
                                        OpScrap, OpScrapVehicle, OpQuickProduce })
                ops[op] = HandleDefIndexIntent;
            ops[OpCartAdd] = HandleCartMutateIntent;
            ops[OpCartRemove] = HandleCartMutateIntent;
            ops[OpCartScrap] = HandleCartScrapIntent;
            IntentRail.Register(SurfaceIds.GeoManufactureIntent, "manufacture", ops, ForceQueueResend);
            // The shared cart rides the value rail as a mod-state root: same registration on BOTH peers
            // (this runs in SyncEngine's ctor host and client alike); the client's instance is the apply target.
            IdentityResolver.RegisterModRoot(CartRootKey, Cart);
        }

        private static void ForceQueueResend()
        {
            _lastSentQueue = null;
            PushQueueSnapshot(NetworkEngine.Instance); // self-guards non-host / no session / no geoscape
            // Cart needs no reject reconverge: clients never write the mirror locally (block-first),
            // so a reject leaves nothing to heal — the rail is the only writer.
        }

        /// <summary>Mid-session reload boundary (rca-3 contract): drop the dying-geoscape hook + the last-sent
        /// mark (post-reload state re-emitted), KEEP the seq streams + intent nonce (persist symmetrically).</summary>
        public static void ResetForReloadBoundary()
        {
            Unhook();
            _lastSentQueue = null;
            // Cart is transient UX state; a reload re-shapes storage under it — start clean on BOTH
            // peers (mod-root contract: mod state is EMPTY at every boundary — baselines don't emit).
            Cart.Items.Clear();
            Cart.Vehicles.Clear();
        }

        private static ItemManufacturing PhoenixManufacture() => GenericApplier.GeoLevel()?.PhoenixFaction?.Manufacture;

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
                        string guid = WireString.ReadKey(r);
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

        /// <summary>Every 0xAE op with the shared [defGuid][index] body (the cart ops have their own
        /// table entries — see <see cref="RegisterIntents"/>).</summary>
        private static void HandleDefIndexIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string defGuid = WireString.ReadKey(r);
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
                        var st = GenericApplier.GeoLevel()?.PhoenixFaction?.ItemStorage;
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
                var storage = GenericApplier.GeoLevel()?.PhoenixFaction?.ItemStorage;
                if (index >= 1 && def != null && storage != null && storage.Items.TryGetValue(def, out var gi) &&
                    gi.CommonItemData.Count >= index)
                {
                    GenericApplier.GeoLevel().PhoenixFaction.ScrapItem(gi, index);
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
                var air = GenericApplier.GeoLevel()?.PhoenixFaction?.AircraftItemStorage;
                if (def != null && air != null && air.HasItem(def))
                {
                    var veh = air.PopItem(def);   // removes from storage
                    if (veh != null) { GenericApplier.GeoLevel().PhoenixFaction.ScrapVehicleEquipment(veh); ok = true; }
                }
                else IntentRail.Reject(SurfaceIds.GeoManufactureIntent, senderPeerId, "scrapVehicle missing " + defGuid);
                if (MpDiag.On) Debug.Log("[MP][scrap] HOST scrapped " + defGuid + " x1 ok=" + ok);
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
                var faction = GenericApplier.GeoLevel()?.PhoenixFaction;
                if (def != null && faction?.Wallet != null && faction.ItemStorage != null &&
                    manufacture.ManufacturableItems.Any(i => ReferenceEquals(i.RelatedItemDef, def)) &&   // L113: def identity
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
                var view = GenericApplier.GeoLevel()?.View;
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
        /// against live storage FIRST, then reconcile the cart against the result, preserving the user's
        /// staged cart (native re-snapshot dumps it — that is mid-gesture state, same category as the
        /// _filter this screen's Exit+Enter opt-out protects): clamp each staged count to what the rebuilt
        /// POOL still offers (a remote peer may have scrapped the same items; also keeps the host's native
        /// ScrapAllItems from dereferencing a vanished def at UIModuleManufacturing.cs:858-859), then
        /// subtract the cart so staged items don't double-show. Pool, not raw faction storage — see the
        /// partial-magazine note in the body.</summary>
        private static void ResyncScrapSnapshot(UIModuleManufacturing module)
        {
            var faction = GenericApplier.GeoLevel()?.PhoenixFaction;
            if (faction?.ItemStorage == null || faction.AircraftItemStorage == null) return;

            // THE POOL FIRST, and everything below measured against it. The native snapshot block ends with
            // StripPartialMagsFromScrapStorage (UIModuleManufacturing.cs:1075-1092), which WITHHOLDS one
            // partial magazine per def — a 5-stack whose last clip is half-empty offers 4. Measuring the
            // cart against the RAW faction stack instead (what this did until 2026-08-07) stages a
            // magazine the screen never held, and the subtract at the bottom underflows: the engine says
            // "Trying to remove 5 items with 36 charges but only 4 items with 32 charges available in
            // PX_SniperRifle_AmmoClip_ItemDef" (x31 in one client session) and then RemoveItem drops the
            // whole entry (ItemStorage.cs:87-89) — the item disappears off the scrap screen.
            var snapshot = ScrapStorageField(module);
            snapshot.Clear();
            snapshot.AddItems(faction.ItemStorage);
            StripPartialMagsMethod?.Invoke(module, null);

            var cart = ScrapItemsField(module);
            foreach (var kv in cart.Items.ToList())
            {
                int live = snapshot.Items.TryGetValue(kv.Key, out var gi) ? gi.CommonItemData.Count : 0;
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

            // Shared-cart reconcile (in-session only): counts-to-target against the host-authoritative
            // store — foreign peers' queued items materialize, remotely unqueued ones return, and a def
            // absent from the store leaves the cart (the store is the ONLY truth in co-op). Idempotent:
            // equal counts move nothing, so re-applying the same echo is a no-op. Add clones use the
            // native add shape (charges/ammo/malfunction read off the POOL entry, exactly as
            // AddToScrapQueue:1116 reads them off _scrapStorage); removals use the native return shape
            // (RemoveFromScrapQueue:846). Targets clamp to the pool; the subtract below takes the final
            // cart out of it, so the partition stays exact.
            var engine = NetworkEngine.Instance;
            if (engine != null && engine.IsActiveSession)
            {
                var repo = GameUtl.GameComponent<DefRepository>();
                var targets = new Dictionary<ItemDef, int>();
                foreach (var kv in Cart.Items)
                    if (repo?.GetDef(kv.Key) is ItemDef d && !(d is GeoVehicleEquipmentDef)) targets[d] = kv.Value;
                foreach (var kv in cart.Items.ToList())
                {
                    targets.TryGetValue(kv.Key, out int want);
                    int cur = kv.Value.CommonItemData.Count;
                    if (cur > want) cart.RemoveItem(new GeoItem(kv.Key, cur - want, kv.Key.ChargesMax));
                }
                foreach (var t in targets)
                {
                    // Count AND charges off the POOL, never the raw stack: the pool's entry is full-charged
                    // exactly because the partial was withheld, so each add moves the cart count by one
                    // (CommonItemData.AddItem:205-213 merges partial charges instead of counting them).
                    if (!snapshot.Items.TryGetValue(t.Key, out var live)) continue;
                    int cur = cart.Items.TryGetValue(t.Key, out var cgi) ? cgi.CommonItemData.Count : 0;
                    int want = Math.Min(t.Value, live.CommonItemData.Count);
                    for (int i = cur; i < want; i++)
                        cart.AddItem(new GeoItem(t.Key, 1, live.CommonItemData.CurrentCharges,
                                                 live.CommonItemData.Ammo, live.CommonItemData.Malfunction));
                }

                var vehTargets = new Dictionary<GeoVehicleEquipmentDef, int>();
                foreach (var kv in Cart.Vehicles)
                    if (repo?.GetDef(kv.Key) is GeoVehicleEquipmentDef vd) vehTargets[vd] = kv.Value;
                foreach (var def in vehCart.Items.Select(v => v.EquipmentDef).Distinct().ToList())
                {
                    vehTargets.TryGetValue(def, out int want);
                    for (int cur = vehCart.CountItem(def); cur > want; cur--) vehCart.RemoveItem(def);
                }
                foreach (var t in vehTargets)
                {
                    int want = Math.Min(t.Value, faction.AircraftItemStorage.CountItem(t.Key));
                    for (int cur = vehCart.CountItem(t.Key); cur < want; cur++)
                    {
                        // an instance not already staged — the cart and snapshot partition instance refs
                        var inst = faction.AircraftItemStorage.Items.FirstOrDefault(
                            v => ReferenceEquals(v.EquipmentDef, t.Key) && !vehCart.Items.Contains(v));   // L113: def identity
                        if (inst == null) break;
                        vehCart.AddItem(inst);
                    }
                }
            }

            // The subtract that partitions pool into "available" and "staged". It is the ONE place a
            // shortfall shows, and the engine reports it with a bare Debug.LogError naming only a def —
            // nothing that says a PEER's cart is the thing that overdrew. Say it ourselves, first.
            foreach (var kv in cart.Items)
            {
                var staged = kv.Value.CommonItemData;
                var have = snapshot.Items.TryGetValue(kv.Key, out var pool) ? pool.CommonItemData : null;
                if (have != null && have.Count >= staged.Count && have.TotalCharges >= staged.TotalCharges) continue;
                Debug.LogError("[MP][scrapcart] SHORTFALL " + kv.Key?.name + ": cart holds " + staged.Count +
                               " item(s)/" + staged.TotalCharges + " charges but the scrap pool offers only " +
                               (have?.Count ?? 0) + "/" + (have?.TotalCharges ?? 0) +
                               " — the subtract underflows and the def drops off the screen. Shared store says " +
                               (Cart.Items.TryGetValue(kv.Key.Guid, out int shared) ? shared : 0) + ".");
            }
            snapshot.RemoveItems(cart);
            var vehSnapshot = VehicleScrapStorageField(module);
            vehSnapshot.Clear();
            vehSnapshot.AddItems(faction.AircraftItemStorage);
            vehSnapshot.RemoveItems(vehCart);
        }

        // ─── CLIENT: intent capture (fed by the Harmony prefixes below) ────

        /// <summary>TRUE = run the native method (host, no session, apply scope, or a non-Phoenix manufacture);
        /// FALSE = client blocks it (caller sends the intent instead). The base decision is THE shared law
        /// (<see cref="IntentRail.ShouldRunNative"/>); the one family arm on top: NPC/foreign manufacture
        /// stays native (un-synced).</summary>
        private static bool ShouldRunNative(ItemManufacturing instance)
            => IntentRail.ShouldRunNative() || !ReferenceEquals(instance, PhoenixManufacture());

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
                if (IntentRail.ShouldRunNative())
                {
                    // HOST confirm runs the native method, which never re-validates the cart against live
                    // storage: a queued item a CLIENT meanwhile equipped/moved (mirrored into host storage)
                    // → TryGetValue null → ScrapItem(null,…) NRE (UIModuleManufacturing.cs:856-863); a
                    // shrunk stack → GeoFaction.ScrapItem refunds the full stale count with no clamp
                    // (GeoFaction.cs:1217-1228) = ghost resources. Solo cannot race — prune only in-session.
                    if (CartHostRecording())
                        try { PruneCartsToLiveStorage(__instance); }
                        catch (Exception ex) { Debug.LogWarning("[MP][scrap] cart prune failed: " + ex.Message); }
                    return true; // host / no session / apply-scope → run native authoritatively
                }

                // Shared cart: ONE intent scraps the whole host-authoritative store (any peer may
                // confirm — the user's ask; a racing second confirm finds an empty store host-side and
                // no-ops, so nothing can double-scrap or refund twice). Block-first (client-posture
                // law): NO local writes, not even the mirror — the host's storage/wallet deltas plus
                // the cart root's dict tombstones repaint the screen (ResyncScrapSnapshot empties the
                // staged carts; DoFilter re-derives ScrapAllButton natively, RefreshItemList:1176).
                SendIntent(OpCartScrap, "", 0);
                return false;
            }

            /// <summary>HOST confirm scrapped the module carts (≡ the spliced shared store, post-prune):
            /// retire the store — the rail's dict tombstones empty every peer's mirror (the prefix
            /// already armed the host-gesture flush). A client OpCartScrap that raced this finds the
            /// store empty and no-ops — no double-scrap, no double-refund.</summary>
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!CartHostRecording()) return;
                Cart.Items.Clear();
                Cart.Vehicles.Clear();
            }

            /// <summary>Scrap-what-remains: drop/clamp cart entries to the LIVE faction storage — the
            /// same storage-count law the client intent path already enforces host-side (OpScrap
            /// validation above). RemoveItem drops the whole dict entry once its count hits 0
            /// (ItemStorage.cs:87-89), so a fully vanished item leaves the cart instead of NREing
            /// the native loop.</summary>
            private static void PruneCartsToLiveStorage(UIModuleManufacturing module)
            {
                var faction = GenericApplier.GeoLevel()?.PhoenixFaction;
                if (faction == null) return;
                var cart = ScrapItemsField(module);
                foreach (var def in cart.Items.Keys.ToList())
                {
                    faction.ItemStorage.Items.TryGetValue(def, out var live);
                    int have = ScrappableCount(live);   // the pool's ceiling, not the raw stack's
                    int queued = cart.Items[def].CommonItemData.Count;
                    if (queued > have)
                        cart.RemoveItem(new GeoItem(def, queued - have, def.ChargesMax));
                }
                var vehCart = VehicleScrapItemsField(module);
                foreach (var def in vehCart.Items.Select(v => v.EquipmentDef).Distinct().ToList())
                    while (vehCart.CountItem(def) > faction.AircraftItemStorage.CountItem(def))
                        vehCart.RemoveItem(vehCart.GetItem(def));
            }
        }

        // ─── Shared scrap cart (host-authoritative MOD store on rail root "M#cart" + gesture seams) ─────

        /// <summary>TRUE = we are the in-session host outside an apply scope — record the accepted
        /// native gesture into the store (the postfix gate).</summary>
        private static bool CartHostRecording()
        {
            var engine = NetworkEngine.Instance;
            return engine != null && engine.IsActiveSession && engine.IsHost && !SyncApplyScope.Active;
        }

        private static void CartHostMutate(string defGuid, bool veh, int delta)
        {
            var store = veh ? Cart.Vehicles : Cart.Items;
            store.TryGetValue(defGuid, out int cur);
            int next = cur + delta;
            if (next <= 0) store.Remove(defGuid); else store[defGuid] = next;
            // Ships on the rail: the capture prefix's ShouldRunNative already armed the host-gesture flush.
        }

        /// <summary>How many of a def the SCRAP SCREEN actually offers — the stack MINUS the one partial
        /// magazine <c>StripPartialMagsFromScrapStorage</c> withholds (UIModuleManufacturing.cs:1075-1092),
        /// mirroring its predicate exactly. This, not the raw stack, is the ceiling a cart may be staged to:
        /// the pool is what a staged item is finally subtracted from, so validating against the raw stack
        /// lets a peer stage a magazine that pool never held and the subtract underflows. A host running the
        /// screen natively can never overdraw (AddToScrapQueue:1109-1116 moves items OUT of the pool, and
        /// throws once it is empty) — only the client, whose gesture is validated here instead.</summary>
        private static int ScrappableCount(GeoItem gi)
        {
            var def = gi?.ItemDef;
            if (def == null) return 0;
            var cid = gi.CommonItemData;
            bool withheld = cid.CurrentCharges != def.ChargesMax && !(def.CompatibleAmmunition?.Any() ?? false);
            return cid.Count - (withheld ? 1 : 0);
        }

        private static int CartLiveCount(string defGuid, bool veh)
        {
            var faction = GenericApplier.GeoLevel()?.PhoenixFaction;
            var repo = GameUtl.GameComponent<DefRepository>();
            if (faction == null || repo == null) return 0;
            if (veh)
                return repo.GetDef(defGuid) is GeoVehicleEquipmentDef vd ? faction.AircraftItemStorage.CountItem(vd) : 0;
            return repo.GetDef(defGuid) is ItemDef d && faction.ItemStorage.Items.TryGetValue(d, out var gi)
                ? ScrappableCount(gi) : 0;
        }

        /// <summary>Host: apply a client queue/unqueue gesture to the store. Add validates against live
        /// storage (store count may never exceed it); a stale unqueue is a deliberate no-op. The change
        /// rides the rail — IntentRail's dispatch FlushNow ships it this frame.</summary>
        private static void HandleCartMutateIntent(NetworkEngine engine, ulong peer, uint nonce, byte op, BinaryReader r)
        {
            string defGuid = WireString.ReadKey(r);
            bool veh = r.ReadInt32() != 0;
            if (string.IsNullOrEmpty(defGuid)) return;
            var store = veh ? Cart.Vehicles : Cart.Items;
            store.TryGetValue(defGuid, out int cur);
            if (op == OpCartAdd)
            {
                if (cur >= CartLiveCount(defGuid, veh))
                { IntentRail.Reject(SurfaceIds.GeoManufactureIntent, peer, "cart add over storage def=" + defGuid); return; }
                store[defGuid] = cur + 1;
            }
            else
            {
                if (cur <= 0) return;
                if (cur == 1) store.Remove(defGuid); else store[defGuid] = cur - 1;
            }
            if (MpDiag.On) Debug.Log("[MP][scrapcart] HOST op=" + op + " def=" + defGuid + " veh=" + veh + " peer=" + peer);
        }

        /// <summary>Host: scrap the WHOLE shared store — any peer's confirm. Every entry is clamped to
        /// live storage at execution (no ghost refunds, same law as the a1b30a2 prune); an empty store
        /// (double-confirm race) is a silent no-op. Storage/wallet outcomes ride the 0xAC value rail
        /// (FlushNow + MarkDirty ride the IntentRail dispatch).</summary>
        private static void HandleCartScrapIntent(NetworkEngine engine, ulong peer, uint nonce, byte op, BinaryReader r)
        {
            var faction = GenericApplier.GeoLevel()?.PhoenixFaction;
            if (faction == null) return;
            if (Cart.Items.Count == 0 && Cart.Vehicles.Count == 0) return;
            var repo = GameUtl.GameComponent<DefRepository>();
            foreach (var kv in Cart.Items.ToList())
            {
                if (!(repo?.GetDef(kv.Key) is ItemDef def)) continue;
                if (!faction.ItemStorage.Items.TryGetValue(def, out var gi)) continue;
                int n = Math.Min(kv.Value, gi.CommonItemData.Count);
                if (n <= 0) continue;
                faction.ScrapItem(gi, n);
                if (gi.CommonItemData.IsEmpty()) faction.ItemStorage.RemoveItem(gi);
            }
            foreach (var kv in Cart.Vehicles.ToList())
            {
                if (!(repo?.GetDef(kv.Key) is GeoVehicleEquipmentDef vd)) continue;
                for (int i = 0; i < kv.Value; i++)
                {
                    var ve = faction.AircraftItemStorage.PopItem(vd);
                    if (ve == null) break;
                    faction.ScrapVehicleEquipment(ve);
                }
            }
            Cart.Items.Clear();
            Cart.Vehicles.Clear();
            Debug.Log("[MP][scrapcart] HOST scrapped shared cart (confirm by peer=" + peer + " nonce=" + nonce + ")");
        }

        /// <summary>Queue-for-scrap gesture (UIModuleManufacturing.AddToScrapQueue:1094). CLIENT: block
        /// native + send the intent — the module never shows un-acked state, so the click cannot be
        /// lost, only confirmed by the mirrored delta (~1 frame on LAN). HOST: native runs; the postfix
        /// records the ACCEPTED gesture (a throwing add never reaches it) and the rail ships it.</summary>
        [HarmonyPatch(typeof(UIModuleManufacturing), "AddToScrapQueue")]
        internal static class CartAddCapturePatch
        {
            private static bool Prefix(GeoManufactureItem item, bool addSecondary, ref bool __result)
            {
                if (IntentRail.ShouldRunNative()) return true;
                var def = addSecondary ? item?.SecondaryItemDef : item?.ItemDef;
                if (def == null) return false;
                SendIntent(OpCartAdd, def.Guid, def is GeoVehicleEquipmentDef ? 1 : 0);
                __result = true; // caller only gates its click-sound/refresh on it
                return false;
            }

            private static void Postfix(GeoManufactureItem item, bool addSecondary, bool __result)
            {
                if (!__result || !CartHostRecording()) return;
                var def = addSecondary ? item?.SecondaryItemDef : item?.ItemDef;
                if (def != null) CartHostMutate(def.Guid, def is GeoVehicleEquipmentDef, +1);
            }
        }

        /// <summary>Unqueue gesture (UIModuleManufacturing.RemoveFromScrapQueue:827) — same split.</summary>
        [HarmonyPatch(typeof(UIModuleManufacturing), "RemoveFromScrapQueue")]
        internal static class CartRemoveCapturePatch
        {
            private static bool Prefix(IManufacturableUIElement item)
            {
                if (IntentRail.ShouldRunNative()) return true;
                var def = item?.ItemDef;
                if (def == null) return false;
                SendIntent(OpCartRemove, def.Guid, def is GeoVehicleEquipmentDef ? 1 : 0);
                return false;
            }

            private static void Postfix(IManufacturableUIElement item)
            {
                if (!CartHostRecording()) return;
                var def = item?.ItemDef;
                if (def != null) CartHostMutate(def.Guid, def is GeoVehicleEquipmentDef, -1);
            }
        }

        /// <summary>Native Init CLEARS the module's cart copies on every open/mode switch
        /// (UIModuleManufacturing.Init:363-371) — replay the shared store into the fresh screen so a
        /// reopened scrap page shows everyone's pending queue. Degrades to per-player behaviour on any
        /// splice failure (logged) — a cart that fails to share beats a crash on the scrap screen.</summary>
        [HarmonyPatch(typeof(UIModuleManufacturing), "Init")]
        internal static class CartReopenSplicePatch
        {
            private static void Postfix(UIModuleManufacturing __instance)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return;
                if (__instance.Mode != UIModuleManufacturing.UIMode.Scrap) return;
                if (Cart.Items.Count == 0 && Cart.Vehicles.Count == 0) return;
                try
                {
                    using (SyncApplyScope.Enter())
                    {
                        ResyncScrapSnapshot(__instance);
                        SetupQueueMethod?.Invoke(__instance, null);
                        DoFilterMethod?.Invoke(__instance, new object[] { null, null });
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[MP][scrapcart] reopen splice failed — cart stays local this open: " + ex.Message); }
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
                if (IntentRail.ShouldRunNative())
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

    /// <summary>
    /// The shared scrap cart — the first MOD-STATE rail root ("M#cart"): a plain serializer-visible
    /// class the generic engine walks/diffs/mirrors exactly like a game root (contract at
    /// <see cref="IdentityResolver.RegisterModRoot"/>). Host authoritative; client instance is the
    /// generic applier's write target. Values are def-guid → queued-for-scrap count.
    /// </summary>
    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public sealed class ScrapCartState
    {
        public Dictionary<string, int> Items = new Dictionary<string, int>();     // ItemDef guid → count
        public Dictionary<string, int> Vehicles = new Dictionary<string, int>();  // GeoVehicleEquipmentDef guid → count
    }
}
