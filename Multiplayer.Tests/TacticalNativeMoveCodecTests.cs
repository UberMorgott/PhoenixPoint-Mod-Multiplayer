using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for the tac.nativemove (0x9D) WIRE FOUNDATION — the origin-native MOVE presentation
/// replay (JetJump), so OBSERVER peers (non-origin clients) play the real flight animation instead of the
/// 4 Hz 0x8F position snaps (frozen-in-air mirror). Mirrors <see cref="TacticalMeleeStartCodecTests"/>
/// MINUS the target actor (a JetJump lands at a POSITION — no actor target). Covers the codec round-trip,
/// empty/null guid, and truncation rejects.
/// </summary>
public class TacticalNativeMoveCodecTests
{
    [Fact]
    public void NativeMove_RoundTrips()
    {
        var bytes = TacticalLiveCodec.EncodeNativeMove(
            seq: 5u, actorNetId: 42, abilityDefGuid: "jetjump-ability-guid-123",
            tx: 1.5f, ty: -2.25f, tz: 3f);
        Assert.True(TacticalLiveCodec.TryDecodeNativeMove(bytes, out var m));
        Assert.Equal(5u, m.Seq);
        Assert.Equal(42, m.ActorNetId);
        Assert.Equal("jetjump-ability-guid-123", m.AbilityDefGuid);
        Assert.Equal(1.5f, m.TX);
        Assert.Equal(-2.25f, m.TY);
        Assert.Equal(3f, m.TZ);
    }

    [Fact]
    public void NativeMove_RoundTrips_EmptyAndNullGuid()
    {
        var empty = TacticalLiveCodec.EncodeNativeMove(2u, 1, "", 0f, 0f, 0f);
        Assert.True(TacticalLiveCodec.TryDecodeNativeMove(empty, out var e));
        Assert.Equal("", e.AbilityDefGuid);

        var nul = TacticalLiveCodec.EncodeNativeMove(4u, 1, null, 0f, 0f, 0f);
        Assert.True(TacticalLiveCodec.TryDecodeNativeMove(nul, out var n));
        Assert.Equal("", n.AbilityDefGuid);   // null guid → empty on the wire
    }

    [Fact]
    public void NativeMove_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeNativeMove(null, out _));
        Assert.False(TacticalLiveCodec.TryDecodeNativeMove(new byte[3], out _));
    }

    [Fact]
    public void NativeMove_RejectsChoppedMidField()
    {
        var bytes = TacticalLiveCodec.EncodeNativeMove(1u, 1, "g", 0f, 0f, 0f);
        // Lop off the trailing part of the vector → a well-formed frame cut short is a clean reject.
        var truncated = new byte[bytes.Length - 6];
        System.Array.Copy(bytes, truncated, truncated.Length);
        Assert.False(TacticalLiveCodec.TryDecodeNativeMove(truncated, out _));
    }

    // ─── origin-native MOVE allowlist gate (JetJump rides the presentation replay, plain moves do not) ─
    [Fact]
    public void IsOriginNativeMove_True_ForJetJump()
        => Assert.True(TacticalAbilityRelay.IsOriginNativeMove("JetJumpAbility"));

    [Theory]
    [InlineData("MoveAbility")]         // dedicated move rail (tac.move.start)
    [InlineData("CaterpillarMoveAbility")]
    [InlineData("RepositionAbility")]
    [InlineData("RamAbility")]
    [InlineData("ShootAbility")]
    [InlineData("")]
    [InlineData(null)]
    public void IsOriginNativeMove_False_ForOthers(string typeName)
        => Assert.False(TacticalAbilityRelay.IsOriginNativeMove(typeName));
}
