using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure guards for the Batch-2 P6 resource-harvest float mirror: wire codec round-trip, occurrence-id
/// authority, and the bounded client dedup window (STUN reliable transport deliberately sends twice — a
/// doubled float would visibly stutter/stack at the site).
/// Wire: [occId:u16][siteId:i32][resourceType:i32][value:f32] on envelope surface GeoHarvestFloat (0xA8).
/// </summary>
public class HarvestFloatTests
{
    // ── codec ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData((ushort)1, 42, 2, 150.5f)]      // Materials at site 42
    [InlineData((ushort)65535, 0, 1, 0.25f)]    // max occId, Supplies
    [InlineData((ushort)7, -1, 0, 0f)]          // no-site sentinel round-trips too
    public void Codec_RoundTrips(ushort occId, int siteId, int resourceType, float value)
    {
        var bytes = HarvestFloatCodec.Encode(occId, siteId, resourceType, value);
        Assert.True(HarvestFloatCodec.TryDecode(bytes, out var o, out var s, out var t, out var v));
        Assert.Equal(occId, o);
        Assert.Equal(siteId, s);
        Assert.Equal(resourceType, t);
        Assert.Equal(value, v);
    }

    [Fact]
    public void Codec_FixedWireSize_FourteenBytes()
        => Assert.Equal(14, HarvestFloatCodec.Encode(1, 2, 3, 4f).Length);   // u16 + i32 + i32 + f32 pin

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]   // truncated
    public void Codec_ShortOrNull_DecodesFalse(byte[] data)
        => Assert.False(HarvestFloatCodec.TryDecode(data, out _, out _, out _, out _));

    // ── occurrence-id authority (host) ───────────────────────────────────

    [Fact]
    public void Ids_AreMonotonic_AndWrapAsUShort()
    {
        HarvestFloatIds.ResetForTests();
        ushort a = HarvestFloatIds.Next();
        ushort b = HarvestFloatIds.Next();
        Assert.Equal(1, a);
        Assert.Equal(2, b);
        // u16 wrap is natural (the dedup window is far smaller than 65536, so no alias hazard).
        // Counter is at 2; advance to 65535, then the next two calls wrap through 0.
        for (int i = 0; i < 65533; i++) HarvestFloatIds.Next();
        Assert.Equal((ushort)0, HarvestFloatIds.Next());   // 65536 % 65536
        Assert.Equal((ushort)1, HarvestFloatIds.Next());
        HarvestFloatIds.ResetForTests();
    }

    // ── client dedup window ───────────────────────────────────────────────

    [Fact]
    public void Dedup_FirstApply_True_DuplicateBlocked()
    {
        var d = new HarvestFloatDedup();
        Assert.True(d.ShouldApply(10));
        Assert.False(d.ShouldApply(10));   // STUN double-send
        Assert.True(d.ShouldApply(11));
        Assert.False(d.ShouldApply(10));   // still remembered inside the window
    }

    [Fact]
    public void Dedup_WindowEviction_ReadmitsOldest()
    {
        // FIFO-bounded: after Capacity newer ids the oldest is evicted (harmless by construction — the
        // transport double-send is back-to-back, never Capacity deliveries apart).
        var d = new HarvestFloatDedup();
        Assert.True(d.ShouldApply(0));
        for (ushort i = 1; i <= HarvestFloatDedup.Capacity; i++)
            Assert.True(d.ShouldApply(i));
        Assert.True(d.ShouldApply(0));     // evicted → re-applies
        Assert.False(d.ShouldApply(HarvestFloatDedup.Capacity));   // recent ones still blocked
    }

    [Fact]
    public void Dedup_Reset_ForgetsEverything()
    {
        var d = new HarvestFloatDedup();
        Assert.True(d.ShouldApply(5));
        d.Reset();
        Assert.True(d.ShouldApply(5));
    }
}
