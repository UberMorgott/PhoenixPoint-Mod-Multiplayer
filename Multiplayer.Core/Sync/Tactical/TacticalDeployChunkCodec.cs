using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE chunking layer for the <c>tac.deploy</c> snapshot (FIX 1). The full <see cref="TacticalDeployCodec"/>
    /// payload is hundreds of KB, but <c>SyncProtocol.EncodeEnvelope</c> caps an envelope payload at a u16
    /// (65535 B) and THROWS on overflow. We must NOT touch that hot file, so we split the codec payload into
    /// fragments that each fit comfortably under the cap, send each as its OWN <c>tac.deployChunk</c> envelope
    /// over the SAME tactical rail, and REASSEMBLE on the receive side before handing the whole payload to
    /// <see cref="TacticalDeployCodec.TryDecode"/>.
    ///
    /// Fragment wire (engine-free, BinaryWriter/Reader only → unit-testable):
    ///   [siteId:i32][deployGeneration:i32][chunkIndex:i32][chunkCount:i32][totalLen:i32][fragLen:i32][frag:N]
    ///
    /// Reassembly is ORDER-INDEPENDENT (Stun transport is unordered) and IDEMPOTENT (a duplicate chunk is
    /// ignored; a (siteId,deployGeneration) set reassembles exactly once). <see cref="ChunkReassembler"/> is a
    /// pure buffer keyed by (siteId,deployGeneration) so it unit-tests with no engine types.
    /// </summary>
    public static class TacticalDeployChunkCodec
    {
        /// <summary>Fragment payload size (bytes of the inner deploy-codec blob per chunk). Chosen well under
        /// the u16 envelope cap (65535): one fragment envelope payload = this + the 24-byte chunk header +
        /// the 4-byte envelope header = 49180 B &lt; 65535. A round 48 KiB keeps a comfortable margin.</summary>
        public const int FragmentSize = 48 * 1024;   // 49152

        /// <summary>The fixed chunk-header size in bytes: 6 × i32.</summary>
        public const int HeaderSize = 6 * 4;

        /// <summary>Hard sanity caps on the wire-declared chunk header (FA-0016). count/totalLen are read raw
        /// and later drive <c>new byte[count][]</c>, <c>new bool[count]</c> and <c>new byte[totalLen]</c> in
        /// <see cref="ChunkReassembler.Accept"/>; a single crafted chunk with count~2e9 or totalLen~2e9 is an
        /// alloc bomb (host→client). A full deploy payload is hundreds of KB, so 64 MB / 4096 chunks clears any
        /// legitimate payload (64 MB / 48 KiB ≈ 1365 chunks) with wide margin.</summary>
        public const int MaxTotalLen = 64 * 1024 * 1024;
        public const int MaxChunkCount = 4096;

        /// <summary>True when the whole codec payload fits in a SINGLE envelope (no chunking needed). The
        /// threshold leaves headroom under the 65535 cap for the 4-byte envelope header + slack.</summary>
        public const int SingleEnvelopeMax = 60000;

        /// <summary>The decoded header + fragment of one chunk.</summary>
        public sealed class Fragment
        {
            public int SiteId;
            public int DeployGeneration;
            public int ChunkIndex;
            public int ChunkCount;
            public int TotalLen;
            public byte[] Data;
        }

        /// <summary>Split a full deploy-codec payload into ordered chunk-envelope payloads (each ready to hand
        /// to <c>EncodeEnvelope(TacDeployChunk, …)</c>). Always produces ≥1 chunk (an empty/small payload is a
        /// single chunk). The caller decides whether to chunk at all via <see cref="SingleEnvelopeMax"/>.</summary>
        public static List<byte[]> Split(int siteId, int deployGeneration, byte[] full)
        {
            full = full ?? new byte[0];
            int frag = FragmentSize;
            int count = full.Length == 0 ? 1 : (full.Length + frag - 1) / frag;
            var chunks = new List<byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                int off = i * frag;
                int len = System.Math.Min(frag, full.Length - off);
                if (len < 0) len = 0;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(siteId);
                    w.Write(deployGeneration);
                    w.Write(i);
                    w.Write(count);
                    w.Write(full.Length);
                    w.Write(len);
                    if (len > 0) w.Write(full, off, len);
                    chunks.Add(ms.ToArray());
                }
            }
            return chunks;
        }

        /// <summary>Decode one chunk-envelope payload. Returns false (no partial accept) on truncation.</summary>
        public static bool TryDecode(byte[] data, out Fragment fragment)
        {
            fragment = null;
            if (data == null || data.Length < HeaderSize) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int siteId = r.ReadInt32();
                    int gen = r.ReadInt32();
                    int idx = r.ReadInt32();
                    int count = r.ReadInt32();
                    int totalLen = r.ReadInt32();
                    int fragLen = r.ReadInt32();
                    // FA-0016: bound count + totalLen BEFORE returning a Fragment, so the reassembler never
                    // allocates arrays sized by an attacker's raw wire value. fragLen is already bounded by the
                    // remaining-bytes check below (and by the u16 envelope cap upstream).
                    if (count <= 0 || count > MaxChunkCount || idx < 0 || idx >= count
                        || totalLen < 0 || totalLen > MaxTotalLen || fragLen < 0) return false;
                    if (ms.Length - ms.Position < fragLen) return false;
                    var buf = fragLen > 0 ? r.ReadBytes(fragLen) : new byte[0];
                    if (buf.Length != fragLen) return false;
                    fragment = new Fragment
                    {
                        SiteId = siteId, DeployGeneration = gen, ChunkIndex = idx,
                        ChunkCount = count, TotalLen = totalLen, Data = buf
                    };
                    return true;
                }
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// PURE order-independent, idempotent reassembler for chunked <c>tac.deploy</c> payloads (FIX 1). Feed it
    /// decoded <see cref="TacticalDeployChunkCodec.Fragment"/>s in any order, with duplicates; when the last
    /// missing fragment of a (siteId,deployGeneration) set arrives it returns the fully concatenated payload
    /// ONCE (subsequent or duplicate chunks for that key return null). A higher deployGeneration for the same
    /// site discards a stale partial set (re-deploy / resync). Engine-free → unit-testable.
    /// </summary>
    public sealed class ChunkReassembler
    {
        private sealed class Pending
        {
            public int Generation;
            public int Count;
            public int TotalLen;
            public byte[][] Frags;
            public bool[] Seen;
            public int SeenCount;
            public bool Completed;   // guards exactly-once completion (idempotent dup handling)
        }

        // Keyed by siteId. One in-flight assembly per site (a newer generation replaces an older partial).
        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        // Last fully-assembled generation per site — so re-fed chunks of an ALREADY-completed set (reliable
        // double-send) are dropped instead of re-completing. (The pending buffer is freed on completion to
        // save memory; this tiny per-site int is the exactly-once guard that survives.)
        private readonly Dictionary<int, int> _completedGeneration = new Dictionary<int, int>();

        /// <summary>Accept one fragment. Returns the fully reassembled payload exactly once (when the set
        /// becomes complete), else null. Idempotent: duplicate / post-completion chunks return null.</summary>
        public byte[] Accept(TacticalDeployChunkCodec.Fragment f)
        {
            if (f == null) return null;

            // Already assembled this (or a newer) generation for this site → drop any straggler/duplicate
            // chunk of a completed set (reliable transport double-send) without re-completing.
            if (_completedGeneration.TryGetValue(f.SiteId, out var doneGen) && f.DeployGeneration <= doneGen)
                return null;

            if (!_pending.TryGetValue(f.SiteId, out var p) || p.Generation != f.DeployGeneration)
            {
                // New site, or a newer generation for this site → start (replace) the assembly. Ignore a
                // stale OLDER generation entirely.
                if (p != null && f.DeployGeneration < p.Generation) return null;
                p = new Pending
                {
                    Generation = f.DeployGeneration,
                    Count = f.ChunkCount,
                    TotalLen = f.TotalLen,
                    Frags = new byte[f.ChunkCount][],
                    Seen = new bool[f.ChunkCount],
                    SeenCount = 0,
                    Completed = false
                };
                _pending[f.SiteId] = p;
            }

            if (p.Completed) return null;                       // already hydrated this set → idempotent drop
            // FA-0016: every fragment of a (site,generation) set must agree on count + totalLen. A straggler
            // that disagrees is hostile/corrupt (or a stale re-fragmentation) — drop it, never resize the set.
            if (f.ChunkCount != p.Count || f.TotalLen != p.TotalLen) return null;
            if (f.ChunkIndex < 0 || f.ChunkIndex >= p.Count) return null;
            if (p.Seen[f.ChunkIndex]) return null;              // duplicate chunk → idempotent drop

            p.Seen[f.ChunkIndex] = true;
            p.Frags[f.ChunkIndex] = f.Data ?? new byte[0];
            p.SeenCount++;
            if (p.SeenCount < p.Count) return null;             // not complete yet

            // Complete: concat in index order into a TotalLen buffer.
            p.Completed = true;
            var outBuf = new byte[p.TotalLen];
            int off = 0;
            for (int i = 0; i < p.Count; i++)
            {
                var d = p.Frags[i] ?? new byte[0];
                int n = System.Math.Min(d.Length, p.TotalLen - off);
                if (n > 0) System.Array.Copy(d, 0, outBuf, off, n);
                off += d.Length;
            }
            _pending.Remove(f.SiteId);                          // free the buffer
            _completedGeneration[f.SiteId] = f.DeployGeneration; // remember exactly-once for re-fed chunks
            return outBuf;
        }
    }
}
