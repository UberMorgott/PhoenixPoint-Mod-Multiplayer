using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE wire-codec tests for the Inc Equip rail (tac.intent.equip + tac.equip) plus the pure equip-index
/// validity helper. No engine types — mirrors <see cref="TacticalCombatCodecTests"/> /
/// <see cref="TacticalLiveCodecTests"/>. Covers round-trip fidelity (0/typical/edge/null-sentinel values),
/// truncation rejection, and the index-applicability rule the host/client appliers gate on.
/// </summary>
public class TacticalEquipCodecTests
{
    // ─── tac.intent.equip (client→host) ───────────────────────────────
    [Fact]
    public void EquipIntent_RoundTrips_Typical()
    {
        var bytes = TacticalLiveCodec.EncodeEquipIntent(actorNetId: 42, equipIndex: 2, nonce: 7u);
        Assert.True(TacticalLiveCodec.TryDecodeEquipIntent(bytes, out var i));
        Assert.Equal(42, i.ActorNetId);
        Assert.Equal(2, i.EquipIndex);
        Assert.Equal(7u, i.Nonce);
    }

    [Fact]
    public void EquipIntent_RoundTrips_ZeroIndex()
    {
        var bytes = TacticalLiveCodec.EncodeEquipIntent(1, 0, 1u);
        Assert.True(TacticalLiveCodec.TryDecodeEquipIntent(bytes, out var i));
        Assert.Equal(0, i.EquipIndex);
    }

    [Fact]
    public void EquipIntent_RoundTrips_NullSentinel()
    {
        var bytes = TacticalLiveCodec.EncodeEquipIntent(5, TacticalLiveCodec.EquipIndexNone, 9u);
        Assert.True(TacticalLiveCodec.TryDecodeEquipIntent(bytes, out var i));
        Assert.Equal(TacticalLiveCodec.EquipIndexNone, i.EquipIndex);   // -1 survives
        Assert.Equal(5, i.ActorNetId);
        Assert.Equal(9u, i.Nonce);
    }

    [Fact]
    public void EquipIntent_RoundTrips_LargeValues()
    {
        var bytes = TacticalLiveCodec.EncodeEquipIntent(int.MaxValue, 1000, uint.MaxValue);
        Assert.True(TacticalLiveCodec.TryDecodeEquipIntent(bytes, out var i));
        Assert.Equal(int.MaxValue, i.ActorNetId);
        Assert.Equal(1000, i.EquipIndex);
        Assert.Equal(uint.MaxValue, i.Nonce);
    }

    [Fact]
    public void EquipIntent_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeEquipIntent(new byte[] { 1, 2, 3 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeEquipIntent(null, out _));
        Assert.False(TacticalLiveCodec.TryDecodeEquipIntent(new byte[11], out _));   // one byte short of 12
    }

    // ─── tac.equip (host→all) ─────────────────────────────────────────
    [Fact]
    public void Equip_RoundTrips_Typical()
    {
        var bytes = TacticalLiveCodec.EncodeEquip(seq: 3u, actorNetId: 77, equipIndex: 1);
        Assert.True(TacticalLiveCodec.TryDecodeEquip(bytes, out var o));
        Assert.Equal(3u, o.Seq);
        Assert.Equal(77, o.ActorNetId);
        Assert.Equal(1, o.EquipIndex);
    }

    [Fact]
    public void Equip_RoundTrips_NullSentinel()
    {
        var bytes = TacticalLiveCodec.EncodeEquip(1u, 8, TacticalLiveCodec.EquipIndexNone);
        Assert.True(TacticalLiveCodec.TryDecodeEquip(bytes, out var o));
        Assert.Equal(TacticalLiveCodec.EquipIndexNone, o.EquipIndex);
    }

    [Fact]
    public void Equip_RoundTrips_ZeroSeqAndIndex()
    {
        var bytes = TacticalLiveCodec.EncodeEquip(0u, 0, 0);
        Assert.True(TacticalLiveCodec.TryDecodeEquip(bytes, out var o));
        Assert.Equal(0u, o.Seq);
        Assert.Equal(0, o.ActorNetId);
        Assert.Equal(0, o.EquipIndex);
    }

    [Fact]
    public void Equip_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeEquip(new byte[] { 0, 0, 0 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeEquip(null, out _));
    }

    // ─── pure index-applicability helper ──────────────────────────────
    [Fact]
    public void IsApplicableEquipIndex_NullSentinel_AlwaysApplicable()
    {
        Assert.True(TacticalLiveCodec.IsApplicableEquipIndex(TacticalLiveCodec.EquipIndexNone, 0));
        Assert.True(TacticalLiveCodec.IsApplicableEquipIndex(TacticalLiveCodec.EquipIndexNone, 3));
    }

    [Fact]
    public void IsApplicableEquipIndex_InRange_Applicable()
    {
        Assert.True(TacticalLiveCodec.IsApplicableEquipIndex(0, 3));
        Assert.True(TacticalLiveCodec.IsApplicableEquipIndex(2, 3));
    }

    [Fact]
    public void IsApplicableEquipIndex_OutOfRange_Rejected()
    {
        Assert.False(TacticalLiveCodec.IsApplicableEquipIndex(3, 3));    // == count, off the end
        Assert.False(TacticalLiveCodec.IsApplicableEquipIndex(5, 3));    // beyond
        Assert.False(TacticalLiveCodec.IsApplicableEquipIndex(0, 0));    // empty list
        Assert.False(TacticalLiveCodec.IsApplicableEquipIndex(-2, 3));   // a negative that is NOT the sentinel
    }
}
