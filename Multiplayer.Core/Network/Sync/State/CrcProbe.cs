using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multiplayer.Util;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Inc5 part 1 — rolling CRC divergence probe, PURE side (no engine/Unity/game types). The host
    /// broadcasts, once per in-game hour, the CRC-32 of each hand-picked DETERMINISTIC state subset on the
    /// <c>GeoCrcProbe</c> (0xA9) envelope surface; the client recomputes the same subsets over its own
    /// mirrored state and compares (<see cref="DivergenceMonitor"/>). Detection ONLY — no auto-resync
    /// (that is the later reconnect/self-heal increment).
    ///
    /// DESIGN: whole-blob serialization is nondeterministic (float formatting, dict ordering), so each
    /// subset is a canonical byte image of a few stable fields: every collection is SORTED on a total
    /// order, floats are QUANTIZED to whole units, strings are raw UTF-8 with a length prefix. Both sides
    /// run this exact code over their own live reads, so equal state ⇒ equal bytes ⇒ equal CRC.
    /// </summary>
    public static class CrcSubsetIds
    {
        // Never reuse a retired id (SurfaceIds tombstone convention). New subsets take new ids.
        public const byte Wallet = 1;     // player wallet: (resourceType, whole-unit amount) sorted by type
        public const byte Sites = 2;      // per-site {SiteId, ownerFactionDefGuid, state} sorted by SiteId
        public const byte Roster = 3;     // Phoenix site-roster GeoUnitId SET (distinct, sorted)
        public const byte Research = 4;   // completed research id SET (distinct, ordinal-sorted)

        public static string Name(byte id)
        {
            switch (id)
            {
                case Wallet: return "wallet";
                case Sites: return "sites";
                case Roster: return "roster";
                case Research: return "research";
                default: return "subset-" + id;   // forward-compat: an unknown host subset stays nameable
            }
        }
    }

    /// <summary>Canonical (deterministic) byte image + CRC-32 per subset. See <see cref="CrcSubsetIds"/>.</summary>
    public static class CrcSubsetCrc
    {
        /// <summary>Wallet slots sorted by resource type; amounts quantized to whole units
        /// (<c>Math.Round</c>) so sub-unit float noise from the diff-apply path never false-flags.</summary>
        public static uint Wallet(IEnumerable<(int type, float value)> slots)
        {
            var list = new List<(int type, float value)>(slots ?? Array.Empty<(int, float)>());
            list.Sort((a, b) => a.type.CompareTo(b.type));
            return CrcOf(w =>
            {
                w.Write(list.Count);
                foreach (var (type, value) in list)
                {
                    w.Write(type);
                    w.Write((long)Math.Round(value));
                }
            });
        }

        /// <summary>Per-site identity triple sorted by SiteId (ties broken by owner guid then state, so the
        /// image is total-ordered even against a pathological duplicate id).</summary>
        public static uint Sites(IEnumerable<(int siteId, string ownerGuid, byte state)> sites)
        {
            var list = new List<(int siteId, string ownerGuid, byte state)>(
                sites ?? Array.Empty<(int, string, byte)>());
            list.Sort((a, b) =>
            {
                int c = a.siteId.CompareTo(b.siteId);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.ownerGuid ?? "", b.ownerGuid ?? "");
                return c != 0 ? c : a.state.CompareTo(b.state);
            });
            return CrcOf(w =>
            {
                w.Write(list.Count);
                foreach (var (siteId, ownerGuid, state) in list)
                {
                    w.Write(siteId);
                    WriteStr(w, ownerGuid);
                    w.Write(state);
                }
            });
        }

        /// <summary>Roster GeoUnitId SET: distinct + sorted (membership divergence detector — container
        /// placement is deliberately NOT hashed, ordering rides the personnel channel's own reconcile).</summary>
        public static uint Roster(IEnumerable<long> unitIds)
        {
            var set = new SortedSet<long>(unitIds ?? Array.Empty<long>());
            return CrcOf(w =>
            {
                w.Write(set.Count);
                foreach (var id in set) w.Write(id);
            });
        }

        /// <summary>Completed research id SET: distinct + ordinal-sorted UTF-8.</summary>
        public static uint Research(IEnumerable<string> completedIds)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (completedIds != null)
                foreach (var id in completedIds)
                    if (!string.IsNullOrEmpty(id)) set.Add(id);
            return CrcOf(w =>
            {
                w.Write(set.Count);
                foreach (var id in set) WriteStr(w, id);
            });
        }

        private static uint CrcOf(Action<BinaryWriter> write)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                write(w);
                w.Flush();
                return Crc32.Compute(ms.ToArray());
            }
        }

        private static void WriteStr(BinaryWriter w, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            w.Write(bytes.Length);
            w.Write(bytes);
        }
    }

    /// <summary>
    /// Wire codec for one probe ROUND (the inner bytes of a <c>GeoCrcProbe</c> 0xA9 envelope):
    ///   [u8 version=1][u32 round][u8 count]{[u8 subsetId][u32 crc]}*
    /// Versioned + tolerant: an unknown version or malformed payload decodes to false (forward-compat drop,
    /// the SurfaceRouter discipline). The round rides the shared <c>SurfaceSeq</c> stream (dup/stale drop).
    /// </summary>
    public static class CrcProbeCodec
    {
        public const byte Version = 1;

        public static byte[] Encode(uint round, IReadOnlyList<(byte subsetId, uint crc)> entries)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(Version);
                w.Write(round);
                int count = entries?.Count ?? 0;
                w.Write((byte)count);
                for (int i = 0; i < count; i++)
                {
                    w.Write(entries[i].subsetId);
                    w.Write(entries[i].crc);
                }
                return ms.ToArray();
            }
        }

        public static bool TryDecode(byte[] data, out uint round, out List<(byte subsetId, uint crc)> entries)
        {
            round = 0;
            entries = null;
            if (data == null || data.Length < 6) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    if (r.ReadByte() != Version) return false;   // unknown future version → drop whole round
                    round = r.ReadUInt32();
                    int count = r.ReadByte();
                    var list = new List<(byte, uint)>(count);
                    for (int i = 0; i < count; i++)
                        list.Add((r.ReadByte(), r.ReadUInt32()));
                    entries = list;
                    return true;
                }
            }
            catch
            {
                round = 0;
                entries = null;
                return false;
            }
        }
    }
}
