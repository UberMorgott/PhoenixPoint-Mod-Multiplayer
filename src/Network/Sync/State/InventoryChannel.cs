using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// State channel #1 — Phoenix faction global <c>ItemStorage</c> (fixes 8v7 manufacture desync).
    /// Host snapshots (def guid, count) pairs on <c>ItemStorage.StorageChanged</c>; client reconciles
    /// its storage to match exactly (Clear + rebuild). Mirrors the wallet echo shape.
    ///
    /// Wire payload (inside StateSync): [u16 count]{[u16 guidLen][guid utf8][i32 count]}*.
    /// </summary>
    public sealed class InventoryChannel : IStateChannel
    {
        public byte ChannelId => 1;

        // ─── host subscription state (mirrors WalletWatcher) ───────────────
        private Delegate _handler;
        private object _storage;
        private bool _bound;

        public byte[] Snapshot(GeoRuntime rt)
        {
            var items = ItemStorageReflection.Snapshot(rt);
            if (items == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)items.Count);
                foreach (var (guid, count) in items)
                {
                    var g = Encoding.UTF8.GetBytes(guid ?? "");
                    w.Write((ushort)g.Length);
                    w.Write(g);
                    w.Write(count);
                }
                return ms.ToArray();
            }
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var target = Decode(data);
            if (target == null) return;
            ItemStorageReflection.Apply(rt, target);
        }

        public void AttachHost(SyncEngine eng)
        {
            if (_bound) return;                                  // bound; skip the per-frame reflection
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
            _bound = true;
        }

        public void DetachHost()
        {
            if (_storage != null && _handler != null)
                ItemStorageReflection.Unsubscribe(_storage, _handler);
            _storage = null;
            _handler = null;
            _bound = false;
        }

        private static List<(string guid, int count)> Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int n = r.ReadUInt16();
                    var list = new List<(string, int)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int gl = r.ReadUInt16();
                        // BinaryReader.ReadBytes silently returns fewer bytes on truncation (no throw);
                        // verify the full guid length was read, else bail (caught below → null = no-op).
                        var gbytes = r.ReadBytes(gl);
                        if (gbytes.Length != gl)
                            throw new EndOfStreamException("InventoryChannel: truncated guid (wanted " + gl + ", got " + gbytes.Length + ")");
                        string guid = Encoding.UTF8.GetString(gbytes);
                        int count = r.ReadInt32();
                        list.Add((guid, count));
                    }
                    return list;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] InventoryChannel.Decode failed: " + ex.Message); return null; }
        }
    }
}
