using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure wire codecs for the MIST state channel (#8, WA-1) — the host echoes the native
    /// <c>MistRendererSystem.RecordInstanceData()</c> gift (deflate-compressed mist + repeller textures,
    /// already the exact payload the game's own SAVE writes) and the client redraws via the native
    /// <c>ProcessInstanceData</c> path. Free of any SyncEngine/Unity dependency so round-trips are directly
    /// unit-testable (mirrors <see cref="GeoVehicleSnapshot"/> / <see cref="GeoSiteSnapshot"/>).
    ///
    /// Because the full field can exceed a single safe message (the raw texture is 2048×1024×4 = 8 MB;
    /// deflate shrinks it heavily but late-game mist can still be hundreds of KB, while the 32 KB
    /// save-transfer chunk bound is the proven safe packet size across all three transports), one mist
    /// EMISSION is split into fixed-size CHUNKS that each ride one ordinary channel flush on the GeoState
    /// (0xA1) rail — no new packet family. The client reassembles by emission sequence
    /// (<see cref="MistReassembler"/>, last-wins + idempotent) and applies only a COMPLETE set.
    ///
    /// Wire, per chunk (= one ch#8 payload inside EncodeStateSync(8, ver, payload)):
    ///   [u8 fmt=1][u32 emitSeq][u16 chunkCount][u16 chunkIdx][i32 len][len bytes]
    /// Assembled blob:
    ///   [i32 hoursPassed][i32 mistLen][mist deflate bytes][i32 repLen][repeller deflate bytes]
    /// The mist/repeller bytes are the RAW deflate output (the native base64 strings decoded) — 25% smaller
    /// on the wire; the client re-encodes to base64 only to feed the native instance-data fields.
    /// </summary>
    public static class MistBlob
    {
        /// <summary>Hard sanity bound for one assembled emission (raw texture is 8 MB; deflate output is
        /// always smaller, so 16 MB rejects only corrupt/hostile counts).</summary>
        public const int MaxBlobBytes = 16 * 1024 * 1024;

        public static byte[] Encode(int hoursPassed, byte[] mist, byte[] repeller)
        {
            mist = mist ?? new byte[0];
            repeller = repeller ?? new byte[0];
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(hoursPassed);
                w.Write(mist.Length);
                w.Write(mist);
                w.Write(repeller.Length);
                w.Write(repeller);
                return ms.ToArray();
            }
        }

        public static bool TryDecode(byte[] blob, out int hoursPassed, out byte[] mist, out byte[] repeller)
        {
            hoursPassed = 0; mist = null; repeller = null;
            if (blob == null) return false;
            try
            {
                using (var ms = new MemoryStream(blob))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    hoursPassed = r.ReadInt32();
                    mist = ReadBlock(r, ms);
                    repeller = ReadBlock(r, ms);
                    return mist != null && repeller != null;
                }
            }
            catch (Exception) { return false; }
        }

        private static byte[] ReadBlock(BinaryReader r, MemoryStream ms)
        {
            int len = r.ReadInt32();
            if (len < 0 || len > ms.Length - ms.Position) return null;   // truncated/corrupt → clean reject
            var b = r.ReadBytes(len);
            return b.Length == len ? b : null;
        }

        /// <summary>Deterministic FNV-1a 64 content hash over the mist + repeller bytes ONLY (HoursPassed is
        /// EXCLUDED — it increments every in-game hour even when the field itself is unchanged, and hashing it
        /// would defeat the host's send-dedup). Used host-side: emit only when the field really changed.</summary>
        public static ulong ContentHash(byte[] mist, byte[] repeller)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                if (mist != null) foreach (byte b in mist) { h ^= b; h *= 1099511628211UL; }
                h ^= 0xFF; h *= 1099511628211UL;   // domain separator so (A+ε, B) ≠ (A, ε+B)
                if (repeller != null) foreach (byte b in repeller) { h ^= b; h *= 1099511628211UL; }
                return h;
            }
        }
    }

    /// <summary>Chunk-level codec + splitter for one mist emission (see <see cref="MistBlob"/> wire doc).</summary>
    public static class MistChunkCodec
    {
        public const byte Format = 1;
        /// <summary>Max chunks per emission (16 MB blob bound / 24 KB chunks ≈ 700; 1024 is a sanity cap).</summary>
        public const int MaxChunks = 1024;

        public static byte[] Encode(uint emitSeq, ushort chunkCount, ushort chunkIdx, byte[] slice)
        {
            slice = slice ?? new byte[0];
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(Format);
                w.Write(emitSeq);
                w.Write(chunkCount);
                w.Write(chunkIdx);
                w.Write(slice.Length);
                w.Write(slice);
                return ms.ToArray();
            }
        }

        public static bool TryDecode(byte[] data, out uint emitSeq, out int chunkCount, out int chunkIdx, out byte[] slice)
        {
            emitSeq = 0; chunkCount = 0; chunkIdx = 0; slice = null;
            if (data == null) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    if (r.ReadByte() != Format) return false;
                    emitSeq = r.ReadUInt32();
                    chunkCount = r.ReadUInt16();
                    chunkIdx = r.ReadUInt16();
                    int len = r.ReadInt32();
                    if (len < 0 || len > ms.Length - ms.Position) return false;
                    var b = r.ReadBytes(len);
                    if (b.Length != len) return false;
                    slice = b;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>Split one assembled blob into encoded chunk payloads (each ≤ header + chunkBytes; a blob
        /// smaller than one chunk yields exactly one). Null/empty blob or a blob needing more than
        /// <see cref="MaxChunks"/> chunks → null (caller skips the emission).</summary>
        public static List<byte[]> EncodeAll(uint emitSeq, byte[] blob, int chunkBytes)
        {
            if (blob == null || blob.Length == 0 || chunkBytes <= 0) return null;
            int count = (blob.Length + chunkBytes - 1) / chunkBytes;
            if (count > MaxChunks) return null;
            var chunks = new List<byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                int offset = i * chunkBytes;
                int len = Math.Min(chunkBytes, blob.Length - offset);
                var slice = new byte[len];
                Buffer.BlockCopy(blob, offset, slice, 0, len);
                chunks.Add(Encode(emitSeq, (ushort)count, (ushort)i, slice));
            }
            return chunks;
        }
    }

    /// <summary>
    /// Pure client-side chunk reassembler for the mist channel. Semantics:
    ///   • LAST-WINS: a chunk of a NEWER emission seq discards any partial older assembly; chunks of an
    ///     emission older than the one being assembled (or already applied) are dropped.
    ///   • IDEMPOTENT: a duplicate chunk (transport double-send) is a no-op; a chunk of an
    ///     already-applied seq is dropped, so one emission can never apply twice.
    ///   • An incomplete set (lost chunk on the best-effort transport) is simply superseded by the next
    ///     hourly emission — mist heals; nothing blocks.
    /// </summary>
    public sealed class MistReassembler
    {
        private bool _hasApplied;
        private uint _appliedSeq;
        private uint _seq;
        private byte[][] _slices;
        private int _received;

        /// <summary>Diagnostic: chunks buffered for the emission currently being assembled.</summary>
        public int BufferedCount => _received;

        /// <summary>Feed one decoded chunk. Returns true with the fully-assembled blob exactly ONCE per
        /// emission seq (on its final missing chunk); false otherwise (buffered / duplicate / stale / invalid).</summary>
        public bool Push(uint emitSeq, int chunkCount, int chunkIdx, byte[] slice, out byte[] blob)
        {
            blob = null;
            if (slice == null || chunkCount < 1 || chunkCount > MistChunkCodec.MaxChunks) return false;
            if (chunkIdx < 0 || chunkIdx >= chunkCount) return false;
            if (_hasApplied && emitSeq <= _appliedSeq) return false;          // already applied (or older) → drop
            if (_slices != null && emitSeq < _seq) return false;              // older than the set being assembled → drop

            if (_slices == null || emitSeq != _seq || _slices.Length != chunkCount)
            {
                // New emission (or count mismatch = corrupt/mixed set) → reset to this seq. LAST-WINS.
                _seq = emitSeq;
                _slices = new byte[chunkCount][];
                _received = 0;
            }
            if (_slices[chunkIdx] != null) return false;                      // duplicate chunk → no-op
            _slices[chunkIdx] = slice;
            _received++;
            if (_received < chunkCount) return false;

            long total = 0;
            foreach (var s in _slices) total += s.Length;
            if (total <= 0 || total > MistBlob.MaxBlobBytes) { Reset(); return false; }
            var assembled = new byte[total];
            int offset = 0;
            foreach (var s in _slices)
            {
                Buffer.BlockCopy(s, 0, assembled, 0 + offset, s.Length);
                offset += s.Length;
            }
            _hasApplied = true;
            _appliedSeq = emitSeq;
            Reset();
            blob = assembled;
            return true;
        }

        private void Reset()
        {
            _slices = null;
            _received = 0;
        }
    }
}
