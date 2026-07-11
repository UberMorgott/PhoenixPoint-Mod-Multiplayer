using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE wire tests for the load-phase heartbeat codec (surface 0x9C <c>tac.load.phase</c>). Covers:
///   (a) phase + progress round-trip byte-identically (incl. the 0 and 1 endpoints),
///   (b) progress clamps to 0..1 on encode AND decode (out-of-range and NaN coerce to a clean fraction),
///   (c) truncation / null → clean <c>false</c> (no partial accept),
///   (d) forward-tolerance: extra trailing bytes past the fixed 5-byte payload are ignored.
/// The engine glue (TacticalLoadPhaseSync host driver / client curtain) binds game types and is in-game verified.
/// </summary>
public class TacLoadPhaseCodecTests
{
    // ─── (a) round-trip ─────────────────────────────────────────────────
    [Fact]
    public void RoundTrips()
    {
        var bytes = TacLoadPhaseCodec.Encode(TacLoadPhaseCodec.PhaseHostLoading, 0.42f);
        Assert.Equal(TacLoadPhaseCodec.Size, bytes.Length);
        Assert.True(TacLoadPhaseCodec.TryDecode(bytes, out var phase, out var progress));
        Assert.Equal(TacLoadPhaseCodec.PhaseHostLoading, phase);
        Assert.Equal(0.42f, progress, 5);
    }

    [Fact]
    public void Endpoints_RoundTrip()
    {
        Assert.True(TacLoadPhaseCodec.TryDecode(TacLoadPhaseCodec.Encode(0, 0f), out _, out var zero));
        Assert.Equal(0f, zero);
        Assert.True(TacLoadPhaseCodec.TryDecode(TacLoadPhaseCodec.Encode(0, 1f), out _, out var one));
        Assert.Equal(1f, one);
    }

    [Fact]
    public void NonZeroPhase_RoundTrips()
    {
        // A future phase byte must survive the wire unchanged (byte leaves room to grow).
        var bytes = TacLoadPhaseCodec.Encode(7, 0.5f);
        Assert.True(TacLoadPhaseCodec.TryDecode(bytes, out var phase, out _));
        Assert.Equal((byte)7, phase);
    }

    // ─── (b) clamp / bad input ──────────────────────────────────────────
    [Fact]
    public void Progress_ClampsOnEncode()
    {
        Assert.True(TacLoadPhaseCodec.TryDecode(TacLoadPhaseCodec.Encode(0, 2.5f), out _, out var high));
        Assert.Equal(1f, high);
        Assert.True(TacLoadPhaseCodec.TryDecode(TacLoadPhaseCodec.Encode(0, -3f), out _, out var low));
        Assert.Equal(0f, low);
    }

    [Fact]
    public void Progress_NaNOnWire_DecodesToZero()
    {
        // A corrupt float on the wire (NaN bit pattern) must coerce to a clamped 0, not a poisoned bar.
        byte[] bytes = { 0x00, 0x00, 0x00, 0xC0, 0x7F };   // phase 0 + float.NaN (little-endian)
        Assert.True(TacLoadPhaseCodec.TryDecode(bytes, out _, out var p));
        Assert.Equal(0f, p);
    }

    // ─── (c) truncation / null ──────────────────────────────────────────
    [Fact]
    public void Truncated_ReturnsFalse()
    {
        var bytes = TacLoadPhaseCodec.Encode(0, 0.5f);
        var cut = new byte[bytes.Length - 1];               // one byte short of the fixed payload
        System.Array.Copy(bytes, cut, cut.Length);
        Assert.False(TacLoadPhaseCodec.TryDecode(cut, out _, out _));
    }

    [Fact]
    public void Null_ReturnsFalse()
    {
        Assert.False(TacLoadPhaseCodec.TryDecode(null, out _, out _));
        Assert.False(TacLoadPhaseCodec.TryDecode(new byte[0], out _, out _));
    }

    // ─── (d) forward-tolerance ──────────────────────────────────────────
    [Fact]
    public void TrailingBytes_Ignored()
    {
        var bytes = TacLoadPhaseCodec.Encode(0, 0.75f);
        var extended = new byte[bytes.Length + 3];
        System.Array.Copy(bytes, extended, bytes.Length);   // 3 unknown future bytes appended past the payload
        Assert.True(TacLoadPhaseCodec.TryDecode(extended, out _, out var p));
        Assert.Equal(0.75f, p, 5);
    }
}
