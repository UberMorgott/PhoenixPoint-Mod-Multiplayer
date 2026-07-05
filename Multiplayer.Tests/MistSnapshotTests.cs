using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync.State;
using Xunit;

// WA-1 mist state channel (#8) — pure codec/chunker/reassembler tests. The engine glue (MistChannel pump +
// MistReflection RecordInstanceData/ProcessInstanceData bridge) is game-bound and in-game verified; these lock
// the wire round-trip, the chunk split/reassembly identity, and the client-side last-wins + idempotence
// contract (a re-shipped or duplicate emission must never re-apply; a newer emission supersedes a partial
// older one).
public class MistSnapshotTests
{
    private static byte[] Bytes(int len, int seed = 1)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = (byte)((i * 31 + seed) & 0xFF);
        return b;
    }

    // ─── blob codec ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Blob_RoundTrip_PreservesHoursAndBothPayloads()
    {
        var mist = Bytes(1000, 3);
        var rep = Bytes(517, 9);
        var blob = MistBlob.Encode(4321, mist, rep);
        Assert.True(MistBlob.TryDecode(blob, out int hours, out var m, out var r));
        Assert.Equal(4321, hours);
        Assert.Equal(mist, m);
        Assert.Equal(rep, r);
    }

    [Fact]
    public void Blob_NullPayloads_EncodeAsEmpty()
    {
        var blob = MistBlob.Encode(7, null, null);
        Assert.True(MistBlob.TryDecode(blob, out int hours, out var m, out var r));
        Assert.Equal(7, hours);
        Assert.Empty(m);
        Assert.Empty(r);
    }

    [Fact]
    public void Blob_Truncated_IsCleanReject()
    {
        var blob = MistBlob.Encode(1, Bytes(100), Bytes(50));
        var cut = new byte[blob.Length - 20];
        Array.Copy(blob, cut, cut.Length);
        Assert.False(MistBlob.TryDecode(cut, out _, out _, out _));
        Assert.False(MistBlob.TryDecode(null, out _, out _, out _));
    }

    [Fact]
    public void ContentHash_ChangesOnContent_NotOnEquality_AndIgnoresNothingElse()
    {
        var mist = Bytes(64, 1);
        var rep = Bytes(64, 2);
        ulong h1 = MistBlob.ContentHash(mist, rep);
        ulong h2 = MistBlob.ContentHash(Bytes(64, 1), Bytes(64, 2));
        Assert.Equal(h1, h2);                                   // deterministic
        var mist2 = Bytes(64, 1); mist2[10] ^= 0x40;
        Assert.NotEqual(h1, MistBlob.ContentHash(mist2, rep));  // content-sensitive
    }

    [Fact]
    public void ContentHash_DomainSeparated_BetweenMistAndRepeller()
    {
        // Moving a byte across the mist/repeller boundary must change the hash.
        ulong a = MistBlob.ContentHash(new byte[] { 1, 2 }, new byte[] { 3 });
        ulong b = MistBlob.ContentHash(new byte[] { 1 }, new byte[] { 2, 3 });
        Assert.NotEqual(a, b);
    }

    // ─── chunk codec + splitter ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Chunk_RoundTrip_PreservesHeaderAndSlice()
    {
        var slice = Bytes(300);
        var data = MistChunkCodec.Encode(42u, 7, 3, slice);
        Assert.True(MistChunkCodec.TryDecode(data, out uint seq, out int count, out int idx, out var s));
        Assert.Equal(42u, seq);
        Assert.Equal(7, count);
        Assert.Equal(3, idx);
        Assert.Equal(slice, s);
    }

    [Fact]
    public void Chunk_TruncatedOrWrongFormat_IsCleanReject()
    {
        var data = MistChunkCodec.Encode(1u, 1, 0, Bytes(64));
        var cut = new byte[data.Length - 10];
        Array.Copy(data, cut, cut.Length);
        Assert.False(MistChunkCodec.TryDecode(cut, out _, out _, out _, out _));
        data[0] = 99;   // unknown format byte
        Assert.False(MistChunkCodec.TryDecode(data, out _, out _, out _, out _));
        Assert.False(MistChunkCodec.TryDecode(null, out _, out _, out _, out _));
    }

    [Fact]
    public void EncodeAll_SplitsAndReassembles_ToTheIdenticalBlob()
    {
        var blob = Bytes(10_000);
        var chunks = MistChunkCodec.EncodeAll(5u, blob, 3000);   // → 4 chunks (3000*3 + 1000)
        Assert.Equal(4, chunks.Count);
        var ra = new MistReassembler();
        byte[] outBlob = null;
        foreach (var c in chunks)
        {
            Assert.True(MistChunkCodec.TryDecode(c, out uint seq, out int count, out int idx, out var slice));
            Assert.Equal(5u, seq);
            Assert.Equal(4, count);
            if (ra.Push(seq, count, idx, slice, out var b)) outBlob = b;
        }
        Assert.Equal(blob, outBlob);
    }

    [Fact]
    public void EncodeAll_SmallBlob_IsSingleChunk_AndEmptyIsNull()
    {
        Assert.Single(MistChunkCodec.EncodeAll(1u, Bytes(10), 3000));
        Assert.Null(MistChunkCodec.EncodeAll(1u, new byte[0], 3000));
        Assert.Null(MistChunkCodec.EncodeAll(1u, null, 3000));
    }

    // ─── reassembler: last-wins + idempotence ──────────────────────────────────────────────────────────

    private static List<(uint seq, int count, int idx, byte[] slice)> Decoded(uint seq, byte[] blob, int chunkBytes)
    {
        var list = new List<(uint, int, int, byte[])>();
        foreach (var c in MistChunkCodec.EncodeAll(seq, blob, chunkBytes))
        {
            MistChunkCodec.TryDecode(c, out uint s, out int n, out int i, out var sl);
            list.Add((s, n, i, sl));
        }
        return list;
    }

    [Fact]
    public void Reassembler_OutOfOrderChunks_StillComplete()
    {
        var blob = Bytes(5000);
        var parts = Decoded(1u, blob, 2000);   // 3 chunks
        var ra = new MistReassembler();
        Assert.False(ra.Push(parts[2].seq, parts[2].count, parts[2].idx, parts[2].slice, out _));
        Assert.False(ra.Push(parts[0].seq, parts[0].count, parts[0].idx, parts[0].slice, out _));
        Assert.True(ra.Push(parts[1].seq, parts[1].count, parts[1].idx, parts[1].slice, out var outBlob));
        Assert.Equal(blob, outBlob);
    }

    [Fact]
    public void Reassembler_DuplicateChunk_IsNoOp_AndAppliedSeqNeverReapplies()
    {
        var blob = Bytes(4000);
        var parts = Decoded(3u, blob, 2000);   // 2 chunks
        var ra = new MistReassembler();
        Assert.False(ra.Push(parts[0].seq, parts[0].count, parts[0].idx, parts[0].slice, out _));
        Assert.False(ra.Push(parts[0].seq, parts[0].count, parts[0].idx, parts[0].slice, out _));   // dup → no-op
        Assert.True(ra.Push(parts[1].seq, parts[1].count, parts[1].idx, parts[1].slice, out _));
        // Full re-delivery of the SAME emission (join-time re-ship) → every chunk dropped, never re-applied.
        foreach (var p in parts)
            Assert.False(ra.Push(p.seq, p.count, p.idx, p.slice, out _));
    }

    [Fact]
    public void Reassembler_NewerEmission_SupersedesPartialOlder_LastWins()
    {
        var oldBlob = Bytes(4000, 1);
        var newBlob = Bytes(4000, 2);
        var oldParts = Decoded(1u, oldBlob, 2000);
        var newParts = Decoded(2u, newBlob, 2000);
        var ra = new MistReassembler();
        Assert.False(ra.Push(oldParts[0].seq, oldParts[0].count, oldParts[0].idx, oldParts[0].slice, out _));
        // Newer emission arrives before the older completes → reset to the newer.
        Assert.False(ra.Push(newParts[0].seq, newParts[0].count, newParts[0].idx, newParts[0].slice, out _));
        // Older emission's remaining chunk is now stale → dropped.
        Assert.False(ra.Push(oldParts[1].seq, oldParts[1].count, oldParts[1].idx, oldParts[1].slice, out _));
        Assert.True(ra.Push(newParts[1].seq, newParts[1].count, newParts[1].idx, newParts[1].slice, out var outBlob));
        Assert.Equal(newBlob, outBlob);
    }

    [Fact]
    public void Reassembler_StaleSeqAfterApply_IsDropped()
    {
        var ra = new MistReassembler();
        var parts2 = Decoded(2u, Bytes(1000), 2000);
        Assert.True(ra.Push(parts2[0].seq, parts2[0].count, parts2[0].idx, parts2[0].slice, out _));
        var parts1 = Decoded(1u, Bytes(1000), 2000);
        Assert.False(ra.Push(parts1[0].seq, parts1[0].count, parts1[0].idx, parts1[0].slice, out _));
    }

    [Fact]
    public void Reassembler_InvalidHeader_IsRejected()
    {
        var ra = new MistReassembler();
        Assert.False(ra.Push(1u, 0, 0, Bytes(10), out _));                              // count < 1
        Assert.False(ra.Push(1u, MistChunkCodec.MaxChunks + 1, 0, Bytes(10), out _));   // over cap
        Assert.False(ra.Push(1u, 2, 2, Bytes(10), out _));                              // idx out of range
        Assert.False(ra.Push(1u, 2, 0, null, out _));                                   // null slice
    }
}
