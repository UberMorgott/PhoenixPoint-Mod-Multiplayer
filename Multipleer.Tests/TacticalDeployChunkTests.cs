using System;
using System.Collections.Generic;
using Multipleer.Sync.Tactical;
using Xunit;

/// <summary>
/// FIX 1 unit tests: the tac.deploy chunk split → reassemble path. The deploy snapshot is hundreds of KB but
/// SyncProtocol.EncodeEnvelope caps an envelope payload at u16 (65535 B) and throws on overflow; we split the
/// codec payload into fragments under the cap and reassemble on receive. Reassembly must be order-independent
/// (Stun is unordered) and idempotent (duplicates ignored, completion fires exactly once).
/// </summary>
public class TacticalDeployChunkTests
{
    private static byte[] MakePayload(int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = (byte)((i * 31 + 7) % 251);
        return b;
    }

    private static List<TacticalDeployChunkCodec.Fragment> DecodeAll(List<byte[]> chunkPayloads)
    {
        var frags = new List<TacticalDeployChunkCodec.Fragment>();
        foreach (var cp in chunkPayloads)
        {
            Assert.True(TacticalDeployChunkCodec.TryDecode(cp, out var f));
            frags.Add(f);
        }
        return frags;
    }

    [Fact]
    public void Split_ProducesMultipleChunks_ForOverCapPayload()
    {
        // ~300 KB → several 48 KiB fragments.
        var full = MakePayload(300_000);
        var chunks = TacticalDeployChunkCodec.Split(siteId: 5, deployGeneration: 1, full);
        Assert.True(chunks.Count > 1);

        // Every chunk-envelope payload must fit safely under the u16 envelope cap.
        foreach (var c in chunks)
            Assert.True(c.Length <= ushort.MaxValue, "chunk payload " + c.Length + " exceeds u16 cap");
    }

    [Fact]
    public void Reassemble_InOrder_ReturnsOriginalExactlyOnce()
    {
        var full = MakePayload(250_000);
        var chunks = TacticalDeployChunkCodec.Split(7, 3, full);
        var frags = DecodeAll(chunks);

        var asm = new ChunkReassembler();
        byte[] result = null;
        int completions = 0;
        foreach (var f in frags)
        {
            var r = asm.Accept(f);
            if (r != null) { result = r; completions++; }
        }
        Assert.Equal(1, completions);
        Assert.Equal(full, result);
    }

    [Fact]
    public void Reassemble_OutOfOrder_ReturnsOriginal()
    {
        var full = MakePayload(220_000);
        var frags = DecodeAll(TacticalDeployChunkCodec.Split(9, 2, full));

        // Deterministic shuffle (reverse + interleave) — exercise arbitrary arrival order.
        frags.Reverse();
        var asm = new ChunkReassembler();
        byte[] result = null;
        foreach (var f in frags)
        {
            var r = asm.Accept(f);
            if (r != null) result = r;
        }
        Assert.Equal(full, result);
    }

    [Fact]
    public void Reassemble_WithDuplicateChunks_IsIdempotent()
    {
        var full = MakePayload(180_000);
        var frags = DecodeAll(TacticalDeployChunkCodec.Split(11, 4, full));

        // Feed every fragment TWICE, interleaved, plus the whole set again after completion.
        var asm = new ChunkReassembler();
        int completions = 0;
        byte[] result = null;

        foreach (var f in frags) { var r = asm.Accept(f); if (r != null) { result = r; completions++; } }
        // Re-feed duplicates of the now-complete set → must NOT complete again, must NOT corrupt.
        foreach (var f in frags) { var r = asm.Accept(f); if (r != null) { result = r; completions++; } }

        Assert.Equal(1, completions);
        Assert.Equal(full, result);
    }

    [Fact]
    public void Reassemble_DuplicatesBeforeCompletion_StillCompletesOnce()
    {
        var full = MakePayload(150_000);
        var frags = DecodeAll(TacticalDeployChunkCodec.Split(13, 5, full));

        var asm = new ChunkReassembler();
        int completions = 0;
        byte[] result = null;
        foreach (var f in frags)
        {
            // Duplicate each fragment immediately, before the set is complete.
            var r1 = asm.Accept(f);
            if (r1 != null) { result = r1; completions++; }
            var r2 = asm.Accept(f);   // duplicate
            if (r2 != null) { result = r2; completions++; }
        }
        Assert.Equal(1, completions);
        Assert.Equal(full, result);
    }

    [Fact]
    public void SmallPayload_SingleChunk_RoundTrips()
    {
        // A payload at/under the single-envelope threshold still splits into exactly one chunk if chunked.
        var full = MakePayload(1234);
        var chunks = TacticalDeployChunkCodec.Split(1, 1, full);
        Assert.Single(chunks);

        Assert.True(TacticalDeployChunkCodec.TryDecode(chunks[0], out var f));
        Assert.Equal(1, f.ChunkCount);
        Assert.Equal(0, f.ChunkIndex);
        Assert.Equal(full.Length, f.TotalLen);

        var asm = new ChunkReassembler();
        var result = asm.Accept(f);
        Assert.Equal(full, result);
    }

    [Fact]
    public void EmptyPayload_SingleChunk_RoundTrips()
    {
        var chunks = TacticalDeployChunkCodec.Split(1, 1, new byte[0]);
        Assert.Single(chunks);
        var frags = DecodeAll(chunks);
        var asm = new ChunkReassembler();
        var result = asm.Accept(frags[0]);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void NewerGeneration_ReplacesStalePartialSet()
    {
        var fullA = MakePayload(120_000);
        var fullB = MakePayload(120_000);
        // Make B distinguishable from A.
        for (int i = 0; i < fullB.Length; i++) fullB[i] ^= 0xFF;

        var fragsA = DecodeAll(TacticalDeployChunkCodec.Split(20, 1, fullA));
        var fragsB = DecodeAll(TacticalDeployChunkCodec.Split(20, 2, fullB));   // same site, newer gen

        var asm = new ChunkReassembler();
        // Deliver only the FIRST fragment of generation 1 (partial), then the full generation 2.
        Assert.Null(asm.Accept(fragsA[0]));

        byte[] result = null;
        foreach (var f in fragsB) { var r = asm.Accept(f); if (r != null) result = r; }
        Assert.Equal(fullB, result);

        // A stray late chunk from the STALE generation 1 must not complete or corrupt anything.
        for (int i = 1; i < fragsA.Count; i++) Assert.Null(asm.Accept(fragsA[i]));
    }

    [Fact]
    public void TryDecode_RejectsTruncatedFragment()
    {
        var chunks = TacticalDeployChunkCodec.Split(1, 1, MakePayload(60_000));
        var c0 = chunks[0];
        var truncated = new byte[c0.Length - 1];
        Array.Copy(c0, truncated, truncated.Length);
        Assert.False(TacticalDeployChunkCodec.TryDecode(truncated, out _));
    }
}
