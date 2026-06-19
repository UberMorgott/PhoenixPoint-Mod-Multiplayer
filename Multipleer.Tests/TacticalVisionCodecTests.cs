using System.Collections.Generic;
using Multipleer.Sync.Tactical;
using Xunit;

public class TacticalVisionCodecTests
{
    // ─── VisionSnapshot wire codec round-trip ─────────────────────────

    [Fact]
    public void Vision_RoundTrips_Empty()
    {
        var bytes = TacticalLiveCodec.EncodeVision(seq: 1u, viewerFactionIndex: 0,
            entries: new List<TacticalLiveCodec.VisionEntry>());
        Assert.True(TacticalLiveCodec.TryDecodeVision(bytes, out var v));
        Assert.Equal(1u, v.Seq);
        Assert.Equal(0, v.ViewerFactionIndex);
        Assert.Empty(v.Entries);
    }

    [Fact]
    public void Vision_RoundTrips_Single()
    {
        var bytes = TacticalLiveCodec.EncodeVision(seq: 5u, viewerFactionIndex: 2,
            new List<TacticalLiveCodec.VisionEntry> { new TacticalLiveCodec.VisionEntry(42, 2) });
        Assert.True(TacticalLiveCodec.TryDecodeVision(bytes, out var v));
        Assert.Equal(5u, v.Seq);
        Assert.Equal(2, v.ViewerFactionIndex);
        Assert.Single(v.Entries);
        Assert.Equal(42, v.Entries[0].NetId);
        Assert.Equal(2, v.Entries[0].KnownState);   // Revealed
    }

    [Fact]
    public void Vision_RoundTrips_N_MixedStates()
    {
        var src = new List<TacticalLiveCodec.VisionEntry>
        {
            new TacticalLiveCodec.VisionEntry(10, 2),   // Revealed (red)
            new TacticalLiveCodec.VisionEntry(11, 1),   // Located (grey)
            new TacticalLiveCodec.VisionEntry(12, 2),
            new TacticalLiveCodec.VisionEntry(-7, 1),   // negative netId tolerated by codec
        };
        var bytes = TacticalLiveCodec.EncodeVision(seq: 99u, viewerFactionIndex: 1, src);
        Assert.True(TacticalLiveCodec.TryDecodeVision(bytes, out var v));
        Assert.Equal(99u, v.Seq);
        Assert.Equal(1, v.ViewerFactionIndex);
        Assert.Equal(src.Count, v.Entries.Count);
        for (int i = 0; i < src.Count; i++)
        {
            Assert.Equal(src[i].NetId, v.Entries[i].NetId);
            Assert.Equal(src[i].KnownState, v.Entries[i].KnownState);
        }
    }

    [Fact]
    public void Vision_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeVision(new byte[] { 1, 2, 3 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeVision(null, out _));
    }

    [Fact]
    public void Vision_RejectsTruncatedMidEntry()
    {
        // Valid header claiming 2 entries, but only 1.x entries' worth of bytes follow.
        var full = TacticalLiveCodec.EncodeVision(2u, 0,
            new List<TacticalLiveCodec.VisionEntry>
            { new TacticalLiveCodec.VisionEntry(1, 2), new TacticalLiveCodec.VisionEntry(2, 1) });
        // Chop the last entry (each entry = 8 bytes: i32 netId + i32 state). Drop 5 bytes.
        var truncated = new byte[full.Length - 5];
        System.Array.Copy(full, truncated, truncated.Length);
        Assert.False(TacticalLiveCodec.TryDecodeVision(truncated, out _));
    }

    [Fact]
    public void Vision_RejectsAbsurdCount()
    {
        // Hand-build a header with a huge count that overruns the buffer → safe false (no wild alloc).
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write((uint)1);     // seq
            w.Write((int)0);      // viewerFactionIndex
            w.Write(int.MaxValue);// count
            Assert.False(TacticalLiveCodec.TryDecodeVision(ms.ToArray(), out _));
        }
    }
}
