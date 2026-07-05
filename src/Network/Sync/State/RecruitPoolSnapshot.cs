using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE record of one haven's recruit slot (PS3 spec §2.4): keyed by the vanilla <c>GeoSite.SiteId</c>
    /// — TFTV-safe, because TFTV's Haven Recruits overlay reads the vanilla <c>GeoHaven.AvailableRecruit</c>
    /// as its source of truth (taxonomy §6) and never replaces the store. <see cref="Blob"/> is the
    /// <c>GeoUnitDescriptor</c> game-Serializer graph; null = the slot is CLEARED (hired / expired) —
    /// an honest tombstone the client stamps as null, never a skip.
    /// </summary>
    public sealed class RecruitHavenRecord
    {
        public readonly int SiteId;
        public readonly byte[] Blob;   // GeoUnitDescriptor graph; null = no recruit at this haven

        public RecruitHavenRecord(int siteId, byte[] blob)
        {
            SiteId = siteId;
            Blob = blob;
        }
    }

    /// <summary>One resource line of a naked recruit's price (ResourcePack → ResourceUnit mirror).
    /// Generic (type, value) pairs — decompile ground truth: the vanilla cost is
    /// <c>ResourceType.Supplies</c> as a difficulty-scaled FLOAT (GeoPhoenixFaction.GenerateNakedRecruitsCost)
    /// and <c>ResourceType</c> is a flags enum up to 0x800, so the spec sketch's fixed
    /// [costMaterials][costTech] i32 pair cannot carry it faithfully.</summary>
    public sealed class RecruitCostEntry
    {
        public readonly int ResourceType;   // PhoenixPoint.Common.Core.ResourceType numeric value
        public readonly float Value;

        public RecruitCostEntry(int resourceType, float value)
        {
            ResourceType = resourceType;
            Value = value;
        }
    }

    /// <summary>PURE record of one base ("naked") recruit: descriptor blob + its price
    /// (<c>GeoPhoenixFaction._nakedRecruits</c> Dictionary&lt;GeoUnitDescriptor, ResourcePack&gt; entry).</summary>
    public sealed class RecruitNakedRecord
    {
        public readonly byte[] Blob;               // GeoUnitDescriptor graph (never null on emit)
        public readonly RecruitCostEntry[] Cost;   // ResourcePack lines (may be empty, never null)

        public RecruitNakedRecord(byte[] blob, RecruitCostEntry[] cost)
        {
            Blob = blob;
            Cost = cost ?? new RecruitCostEntry[0];
        }
    }

    /// <summary>
    /// Decoded off-roster recruit-pool snapshot for state channel #10 — pure data + wire codec, free of
    /// any <c>IStateChannel</c>/<c>SyncEngine</c>/Unity dependency (mirrors <see cref="PersonnelSnapshot"/>).
    /// Three pools (taxonomy §6): per-haven <c>AvailableRecruit</c> (diffed by SiteId), the base
    /// naked-recruit roster and the containment (<c>_capturedUnits</c>) — the latter two are FULL-SET
    /// blocks (when present they carry the WHOLE pool; the client clear+refills, so one delivered flush
    /// fully heals any earlier drop).
    ///
    /// Wire payload (inside EncodeStateSync(channelId=10, ver, payload)):
    ///   [u8 blockFlags]                          // bit0 Havens, bit1 Naked, bit2 Captured; bits 3-7 RESERVED
    ///   per SET bit in ascending order: [i32 blockLen][block bytes]
    ///     Haven block   = [u16 n]{ [i32 siteId][u8 hasRecruit] (=1 → [i32 blobLen][blob]) }*
    ///     Naked block   = [u16 n]{ [i32 blobLen][blob] [u8 nCost]{ [i32 resourceType][f32 value] }* }*
    ///     Captured block= [u16 n]{ [i32 blobLen][blob] }*
    ///
    /// Block ABSENT (flag clear) = "unchanged, hash-skipped" — the client leaves that pool alone; block
    /// PRESENT with n=0 = "pool is EMPTY" (honest clear). This present/absent split is WHY the payload
    /// leads with flags instead of the spec sketch's three always-present blocks: the PS2-style hash-skip
    /// needs an unambiguous "no change" that is distinct from "empty". Unknown higher flag bits are
    /// length-skipped (parse-known-then-skip, the PersonnelSnapshot recLen contract); a truncated KNOWN
    /// block throws → whole payload rejected (all-or-nothing).
    /// </summary>
    public sealed class RecruitPoolSnapshot
    {
        public const byte BlockHavens = 0x01;
        public const byte BlockNaked = 0x02;
        public const byte BlockCaptured = 0x04;

        public readonly List<RecruitHavenRecord> Havens = new List<RecruitHavenRecord>();
        /// <summary>True = the naked block is carried (its FULL set, possibly empty). False = unchanged.</summary>
        public bool HasNaked;
        public readonly List<RecruitNakedRecord> Naked = new List<RecruitNakedRecord>();
        /// <summary>True = the captured block is carried (its FULL set, possibly empty). False = unchanged.</summary>
        public bool HasCaptured;
        public readonly List<byte[]> Captured = new List<byte[]>();

        public static byte[] Encode(RecruitPoolSnapshot snap)
        {
            if (snap == null) return null;
            byte[] havens = snap.Havens.Count > 0 ? EncodeHavenBlock(snap.Havens) : null;
            byte[] naked = snap.HasNaked ? EncodeNakedBlock(snap.Naked) : null;
            byte[] captured = snap.HasCaptured ? EncodeCapturedBlock(snap.Captured) : null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                byte flags = 0;
                if (havens != null) flags |= BlockHavens;
                if (naked != null) flags |= BlockNaked;
                if (captured != null) flags |= BlockCaptured;
                w.Write(flags);
                // Ascending bit order — the decoder walks bits 0..7 and length-skips unknown ones.
                if (havens != null) { w.Write(havens.Length); w.Write(havens); }
                if (naked != null) { w.Write(naked.Length); w.Write(naked); }
                if (captured != null) { w.Write(captured.Length); w.Write(captured); }
                return ms.ToArray();
            }
        }

        /// <summary>Public: the channel hashes these exact block bytes for the PS2-style skip
        /// (unchanged full-set → block omitted, zero bytes on the wire).</summary>
        public static byte[] EncodeHavenBlock(List<RecruitHavenRecord> records)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)records.Count);
                foreach (var rec in records)
                {
                    w.Write(rec.SiteId);
                    if (rec.Blob == null) { w.Write((byte)0); continue; }   // cleared slot: no blob fields
                    w.Write((byte)1);
                    w.Write(rec.Blob.Length);
                    w.Write(rec.Blob);
                }
                return ms.ToArray();
            }
        }

        public static byte[] EncodeNakedBlock(List<RecruitNakedRecord> records)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)records.Count);
                foreach (var rec in records)
                {
                    w.Write(rec.Blob.Length);
                    w.Write(rec.Blob);
                    int n = rec.Cost.Length > byte.MaxValue ? byte.MaxValue : rec.Cost.Length;
                    w.Write((byte)n);
                    for (int i = 0; i < n; i++)
                    {
                        w.Write(rec.Cost[i].ResourceType);
                        w.Write(rec.Cost[i].Value);
                    }
                }
                return ms.ToArray();
            }
        }

        public static byte[] EncodeCapturedBlock(List<byte[]> blobs)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)blobs.Count);
                foreach (var blob in blobs)
                {
                    w.Write(blob.Length);
                    w.Write(blob);
                }
                return ms.ToArray();
            }
        }

        public static RecruitPoolSnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new RecruitPoolSnapshot();
                    byte flags = r.ReadByte();
                    for (int bit = 0; bit < 8; bit++)
                    {
                        byte mask = (byte)(1 << bit);
                        if ((flags & mask) == 0) continue;
                        int len = r.ReadInt32();
                        if (len < 0 || len > ms.Length - ms.Position)
                            throw new EndOfStreamException("RecruitPoolSnapshot: block len " + len + " exceeds payload");
                        byte[] block = r.ReadBytes(len);
                        switch (mask)
                        {
                            case BlockHavens: DecodeHavenBlock(block, snap); break;
                            case BlockNaked: snap.HasNaked = true; DecodeNakedBlock(block, snap); break;
                            case BlockCaptured: snap.HasCaptured = true; DecodeCapturedBlock(block, snap); break;
                            // Unknown future block: length-skipped above (forward tolerance).
                        }
                    }
                    return snap;
                }
            }
            // Pure/Unity-free: malformed payload → null; the caller (channel Apply) treats null as no-op.
            catch (Exception) { return null; }
        }

        private static void DecodeHavenBlock(byte[] block, RecruitPoolSnapshot snap)
        {
            using (var ms = new MemoryStream(block))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                int n = r.ReadUInt16();
                for (int i = 0; i < n; i++)
                {
                    int siteId = r.ReadInt32();
                    byte has = r.ReadByte();
                    byte[] blob = null;
                    if (has != 0)
                    {
                        blob = ReadBlob(r, ms);
                        if (blob == null) throw new EndOfStreamException("RecruitPoolSnapshot: truncated haven blob");
                    }
                    snap.Havens.Add(new RecruitHavenRecord(siteId, blob));
                }
                if (ms.Position != ms.Length) throw new EndOfStreamException("RecruitPoolSnapshot: haven block trailing bytes");
            }
        }

        private static void DecodeNakedBlock(byte[] block, RecruitPoolSnapshot snap)
        {
            using (var ms = new MemoryStream(block))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                int n = r.ReadUInt16();
                for (int i = 0; i < n; i++)
                {
                    byte[] blob = ReadBlob(r, ms);
                    if (blob == null) throw new EndOfStreamException("RecruitPoolSnapshot: truncated naked blob");
                    int nCost = r.ReadByte();
                    var cost = new RecruitCostEntry[nCost];
                    for (int c = 0; c < nCost; c++)
                        cost[c] = new RecruitCostEntry(r.ReadInt32(), r.ReadSingle());
                    snap.Naked.Add(new RecruitNakedRecord(blob, cost));
                }
                if (ms.Position != ms.Length) throw new EndOfStreamException("RecruitPoolSnapshot: naked block trailing bytes");
            }
        }

        private static void DecodeCapturedBlock(byte[] block, RecruitPoolSnapshot snap)
        {
            using (var ms = new MemoryStream(block))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                int n = r.ReadUInt16();
                for (int i = 0; i < n; i++)
                {
                    byte[] blob = ReadBlob(r, ms);
                    if (blob == null) throw new EndOfStreamException("RecruitPoolSnapshot: truncated captured blob");
                    snap.Captured.Add(blob);
                }
                if (ms.Position != ms.Length) throw new EndOfStreamException("RecruitPoolSnapshot: captured block trailing bytes");
            }
        }

        /// <summary>[i32 len][bytes] with bounds check against the remaining stream; null = malformed.</summary>
        private static byte[] ReadBlob(BinaryReader r, MemoryStream ms)
        {
            int len = r.ReadInt32();
            if (len < 0 || len > ms.Length - ms.Position) return null;
            var blob = r.ReadBytes(len);
            return blob.Length == len ? blob : null;
        }
    }

    /// <summary>
    /// PURE host-side PS3 haven-flush core: which dirty havens actually SHIP this flush (mirrors
    /// <see cref="PersonnelStateFlush"/>). Per distinct SiteId it reads the slot (injected delegate),
    /// FNV-1a-hash-compares the slot CONTENT (cleared vs blob bytes) against the last-EMITTED one and
    /// emits only changed havens, bounded by a per-flush byte budget. Outcomes per id: EMIT / Skipped
    /// (hash equal) / Deferred (budget hit — stays dirty, NOT hash-stamped) / Failed (haven unresolved or
    /// blob serialize failed — dropped, not deferred, so a dead id can't spin the drain loop).
    /// Unity-free → directly unit-testable with fake readers.
    /// </summary>
    public static class RecruitPoolFlush
    {
        /// <summary>Record frame overhead: i32 siteId + u8 hasRecruit + i32 blobLen.</summary>
        public const int RecordOverheadBytes = 9;

        public sealed class HavenSource
        {
            public bool Resolved;   // false = haven/serializer miss → Failed
            public byte[] Blob;     // null (with Resolved) = slot cleared (no recruit)
        }

        public sealed class Result
        {
            public readonly List<RecruitHavenRecord> Emit = new List<RecruitHavenRecord>();
            public readonly List<int> Deferred = new List<int>();   // budget overflow — stays dirty
            public int SkippedUnchanged;
            public int Failed;
        }

        /// <summary>Slot content hash: leading marker byte keeps "cleared" distinct from any blob, then
        /// the blob bytes (FNV-1a 64 via <see cref="PersonnelStateFlush.Hash"/> semantics).</summary>
        public static ulong HashSlot(byte[] blobOrNull)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                h ^= blobOrNull == null ? (byte)0 : (byte)1; h *= 1099511628211UL;
                if (blobOrNull != null) foreach (byte b in blobOrNull) { h ^= b; h *= 1099511628211UL; }
                return h;
            }
        }

        public static Result Run(IEnumerable<int> dirtySiteIds, Func<int, HavenSource> read,
                                 IDictionary<int, ulong> lastSent, int byteBudget)
        {
            var res = new Result();
            if (dirtySiteIds == null) return res;
            var seen = new HashSet<int>();
            int used = 0;
            bool budgetClosed = false;   // once the budget rejects one record, defer the rest UNREAD
            foreach (var id in dirtySiteIds)
            {
                if (!seen.Add(id)) continue;
                if (budgetClosed) { res.Deferred.Add(id); continue; }
                var src = read != null ? read(id) : null;
                if (src == null || !src.Resolved) { res.Failed++; continue; }
                ulong h = HashSlot(src.Blob);
                if (lastSent != null && lastSent.TryGetValue(id, out var prev) && prev == h)
                {
                    res.SkippedUnchanged++;
                    continue;
                }
                int recBytes = RecordOverheadBytes + (src.Blob?.Length ?? 0);
                // Emit-at-least-one: the first changed record always ships, else one slot larger than
                // the budget would starve forever (the PersonnelStateFlush contract).
                if (used + recBytes > byteBudget && res.Emit.Count > 0)
                {
                    res.Deferred.Add(id);
                    budgetClosed = true;
                    continue;
                }
                res.Emit.Add(new RecruitHavenRecord(id, src.Blob));
                if (lastSent != null) lastSent[id] = h;
                used += recBytes;
            }
            return res;
        }
    }

    /// <summary>
    /// PURE value-only pool reconcile (PS3 §4): clear + refill the LIVE collection instances (never a
    /// wholesale swap — any captured UI reference keeps seeing the same object), no native mutator, no
    /// cascade. Full-set semantics: the incoming records ARE the pool; a hire/kill on the host simply
    /// arrives as a smaller set. Operates on non-generic <see cref="IDictionary"/>/<see cref="IList"/>
    /// so it is directly unit-testable on fakes (mirrors <see cref="RosterReconcile"/>).
    /// </summary>
    public static class RecruitPoolReconcile
    {
        /// <summary>Refill the naked-recruit dict (descriptor → cost). Returns applied count; -1 = no target.</summary>
        public static int ApplyNaked(IDictionary target, IList<KeyValuePair<object, object>> entries)
        {
            if (target == null || entries == null) return -1;
            target.Clear();
            foreach (var kv in entries)
                if (kv.Key != null) target[kv.Key] = kv.Value;
            return target.Count;
        }

        /// <summary>Refill the captured-unit list. Returns applied count; -1 = no target.</summary>
        public static int ApplyCaptured(IList target, IList<object> descriptors)
        {
            if (target == null || descriptors == null) return -1;
            target.Clear();
            foreach (var d in descriptors)
                if (d != null) target.Add(d);
            return target.Count;
        }
    }
}
