using System;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #1 — Phoenix faction global <c>ItemStorage</c> (fixes 8v7 manufacture desync).
    /// Host snapshots (def guid, count, charges) entries on <c>ItemStorage.StorageChanged</c>; client
    /// reconciles its storage to match exactly (Clear + rebuild). Mirrors the wallet echo shape.
    /// Wire codec + drift signature live in the pure <see cref="InventorySnapshot"/> (unit-tested).
    ///
    /// The change EVENT alone is not enough: native code consumes storage WITHOUT raising
    /// <c>StorageChanged</c> — partial <c>PopItem</c> (ItemStorage.cs:102-106) and partial
    /// <c>RemoveItem</c> (:86-91) fire it only when a def stack is fully removed,
    /// <c>CommonItemData.ModifyCharges</c> raises only <c>OnItemModified</c> (CommonItemData.cs:101-141),
    /// and <c>Clear()</c> raises nothing. The post-mission replenish (GeoMission.cs:1095-1104) and
    /// <c>UIModuleReplenish</c> (:250) write through exactly those silent paths, so the host-side
    /// <see cref="PollHostDrift"/> backstop re-derives dirtiness from absolute truth.
    /// </summary>
    public sealed class InventoryChannel : IStateChannel
    {
        public byte ChannelId => 1;

        // ─── host subscription state (mirrors WalletWatcher) ───────────────
        private Delegate _handler;
        private object _storage;
        // Host: content signature of the last snapshot actually BROADCAST (poll baseline). Snapshot()
        // is the sole payload builder and runs only at broadcast time (SyncEngine.FlushChannel /
        // BroadcastAllChannels), so what it returns is exactly what clients received — the drift poll
        // never re-fires what was just sent (same baseline discipline as _lastWalletBroadcast).
        private string _lastBroadcastSig;

        public byte[] Snapshot(GeoRuntime rt)
        {
            var items = ItemStorageReflection.Snapshot(rt);
            if (items == null) return null;
            _lastBroadcastSig = InventorySnapshot.Signature(items);
            return InventorySnapshot.Encode(items);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var target = InventorySnapshot.Decode(data);
            if (target == null)
            {
                if (data != null)
                    Debug.LogError("[Multiplayer] InventoryChannel: undecodable payload (" + data.Length + " bytes) — apply skipped");
                return;
            }
            ItemStorageReflection.Apply(rt, target);
        }

        /// <summary>
        /// Host poll backstop (throttled by <see cref="SyncEngine.Tick"/>): signature-compare the live
        /// storage against the last broadcast and mark the channel dirty on drift — catching ALL silent
        /// writers (see class doc), current and future. Marks only; the one existing per-channel flush
        /// path stays the sole sender. No-op off-geoscape (null snapshot).
        /// </summary>
        public void PollHostDrift(GeoRuntime rt, SyncEngine eng)
        {
            if (eng == null) return;
            var items = ItemStorageReflection.Snapshot(rt);
            if (items == null) return;                        // not in geoscape yet / mid-load
            var sig = InventorySnapshot.Signature(items);
            if (sig == _lastBroadcastSig) return;             // clients already hold exactly this
            // DIAG: fires only on real drift the StorageChanged event path missed (or the pre-first-
            // flush seed). The flush itself logs nothing here — tripwire in FlushChannel covers storms.
            if (_lastBroadcastSig != null)
                Debug.Log("[Multiplayer] Inventory poll drift detected (silent storage write; event path missed it) — arming channel #1 flush");
            eng.MarkChannelDirty(ChannelId);
        }

        public void AttachHost(SyncEngine eng)
        {
            // NO hard "already bound" gate — rebind when the LIVE storage instance changes (geoscape reload builds
            // a fresh one; the old `if (_bound) return;` left this channel on the dead instance forever — the
            // WalletWatcher lesson, WalletWatcher.cs:20-28). Instance check keeps the per-frame cost tiny.
            if (eng == null) return;
            var storage = ItemStorageReflection.GetStorage(GeoRuntime.Instance);
            if (storage == null) return;                         // not in geoscape yet / mid-load
            if (ReferenceEquals(storage, _storage)) return;      // already bound to this instance

            DetachHost();                                        // drop any stale binding
            _storage = storage;
            byte id = ChannelId;
            _handler = ItemStorageReflection.SubscribeStorageChanged(
                storage, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            // Seed clients with the authoritative inventory the moment we bind.
            eng.MarkChannelDirty(id);
        }

        public void DetachHost()
        {
            if (_storage != null && _handler != null)
                ItemStorageReflection.Unsubscribe(_storage, _handler);
            _storage = null;
            _handler = null;
        }
    }
}
