using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Decoded research-UNLOCK snapshot: the three monotonic def-id sets that a research completion's
    /// reward cascade mutates on the host but which never reach the client today —
    ///   • <see cref="Facilities"/>: <c>GeoPhoenixFaction.AvailableFacilities</c> guids
    ///     (<c>FacilityResearchReward.GiveReward</c> adds a <c>PhoenixFacilityDef</c>: a facility TYPE
    ///     becomes buildable).
    ///   • <see cref="Manufacture"/>: <c>ItemManufacturing.ManufacturableItems[*].RelatedItemDef</c> guids
    ///     (<c>Manufacture.AddAvailableItem</c>: an item becomes manufacturable).
    ///   • <see cref="Augmentations"/>: <c>GeoFaction.UnlockedAugmentations</c> guids (mutation/bionic
    ///     <c>ItemDef</c>s).
    /// All three are monotonic UNLOCK lists (only ever grow during a campaign), so the client reconcile is
    /// purely additive/idempotent. This is the pure data + wire codec for the unlock state channel (#3) —
    /// free of any <c>IStateChannel</c>/<c>SyncEngine</c>/Unity dependency so it is directly unit-testable
    /// (mirrors <see cref="ResearchSnapshot"/>).
    ///
    /// Wire payload (inside StateSync):
    ///   [u16 facCount]{[u16 idLen][guid utf8]}*
    ///   [u16 manuCount]{[u16 idLen][guid utf8]}*
    ///   [u16 augCount]{[u16 idLen][guid utf8]}*
    /// </summary>
    public sealed class UnlockSnapshot
    {
        public readonly List<string> Facilities = new List<string>();
        public readonly List<string> Manufacture = new List<string>();
        public readonly List<string> Augmentations = new List<string>();

        public static byte[] Encode(UnlockSnapshot snap)
        {
            if (snap == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteList(w, snap.Facilities);
                WriteList(w, snap.Manufacture);
                WriteList(w, snap.Augmentations);
                return ms.ToArray();
            }
        }

        public static UnlockSnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new UnlockSnapshot();
                    ReadList(r, snap.Facilities);
                    ReadList(r, snap.Manufacture);
                    ReadList(r, snap.Augmentations);
                    return snap;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null. Callers
            // (UnlockChannel.Apply) treat null as "no-op". No UnityEngine.Debug dependency here.
            catch (Exception) { return null; }
        }

        private static void WriteList(BinaryWriter w, List<string> ids)
        {
            w.Write((ushort)ids.Count);
            foreach (var id in ids) WriteStr(w, id);
        }

        private static void ReadList(BinaryReader r, List<string> into)
        {
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++) into.Add(ReadStr(r));
        }

        private static void WriteStr(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        private static string ReadStr(BinaryReader r)
        {
            int len = r.ReadUInt16();
            // BinaryReader.ReadBytes silently returns FEWER bytes at end-of-stream (no throw), so a
            // truncated payload would decode to garbage rather than being rejected. Verify the full
            // length was read; otherwise throw → caught by Decode's try/catch → null (rejected).
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("UnlockSnapshot: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
