using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure wire codec + drift signature for the inventory state channel (#1) — the Phoenix faction
    /// global <c>ItemStorage</c> as per-def (guid, count, charges) entries. <c>charges</c> mirrors the
    /// storage stack's <c>CommonItemData.CurrentCharges</c> (the TOP unit's remaining charges; the other
    /// count-1 units are full by the stack invariant — <c>TotalAvailableCharges</c>,
    /// CommonItemData.cs:96-99). Charges ride the wire since the post-mission replenish desync fix: the
    /// native pipeline leaves PARTIAL stacks in faction storage (<c>GeoMission.TryReloadItem</c> →
    /// <c>ModifyCharges(-n)</c>, GeoMission.cs:1095-1104), so a count-only mirror silently refilled the
    /// client's stack to ChargesMax (GeoItem ctor charges=-1 → full) and drifted from the host.
    /// Free of any <c>IStateChannel</c>/<c>SyncEngine</c>/Unity dependency so it is directly
    /// unit-testable (mirrors <see cref="UnlockSnapshot"/>).
    ///
    /// Wire payload (inside StateSync): [u16 count]{[u16 guidLen][guid utf8][i32 count][i32 charges]}*.
    /// </summary>
    public static class InventorySnapshot
    {
        public static byte[] Encode(IReadOnlyList<(string guid, int count, int charges)> items)
        {
            if (items == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)items.Count);
                foreach (var (guid, count, charges) in items)
                {
                    var g = Encoding.UTF8.GetBytes(guid ?? "");
                    w.Write((ushort)g.Length);
                    w.Write(g);
                    w.Write(count);
                    w.Write(charges);
                }
                return ms.ToArray();
            }
        }

        public static List<(string guid, int count, int charges)> Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int n = r.ReadUInt16();
                    var list = new List<(string, int, int)>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int gl = r.ReadUInt16();
                        // BinaryReader.ReadBytes silently returns FEWER bytes at end-of-stream (no
                        // throw), so a truncated payload would decode to garbage rather than being
                        // rejected. Verify the full guid length was read; else throw → caught below.
                        var gbytes = r.ReadBytes(gl);
                        if (gbytes.Length != gl)
                            throw new EndOfStreamException("InventorySnapshot: truncated guid (wanted " + gl + ", got " + gbytes.Length + ")");
                        string guid = Encoding.UTF8.GetString(gbytes);
                        int count = r.ReadInt32();
                        int charges = r.ReadInt32();
                        list.Add((guid, count, charges));
                    }
                    return list;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null. Callers
            // (InventoryChannel.Apply) treat null as "no-op". No UnityEngine.Debug dependency here.
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Order-INSENSITIVE content signature backing the host drift poll
        /// (<c>InventoryChannel.PollHostDrift</c>): equal iff the (guid, count, charges) multiset is
        /// equal. Insensitive to enumeration order because the underlying storage is a
        /// <c>Dictionary</c> whose iteration order can shuffle on remove+re-add without any content
        /// change — an order-sensitive signature would false-fire a redundant flush there.
        /// </summary>
        public static string Signature(IReadOnlyList<(string guid, int count, int charges)> items)
        {
            if (items == null) return null;
            var lines = new List<string>(items.Count);
            foreach (var (guid, count, charges) in items)
                lines.Add(guid + ":" + count + ":" + charges);
            lines.Sort(StringComparer.Ordinal);
            return string.Join("|", lines);
        }
    }
}
