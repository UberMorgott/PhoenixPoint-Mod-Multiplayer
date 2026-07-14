using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE wire-codec tests for the heal-presentation rail (tac.heal.start 0xA0). No engine types — mirrors
/// <see cref="TacticalFireStartCodecTests"/>. Covers round-trip fidelity (typical / self-heal / zero / large /
/// string-guid) and truncation rejection.
/// </summary>
public class TacticalHealStartCodecTests
{
    [Fact]
    public void HealStart_RoundTrips_Typical()
    {
        var bytes = TacticalLiveCodec.EncodeHealStart(seq: 6u, healerNetId: 3, abilityDefGuid: "HealAbilityDef", targetNetId: 9);
        Assert.True(TacticalLiveCodec.TryDecodeHealStart(bytes, out var s));
        Assert.Equal(6u, s.Seq);
        Assert.Equal(3, s.HealerNetId);
        Assert.Equal("HealAbilityDef", s.AbilityDefGuid);
        Assert.Equal(9, s.TargetNetId);
    }

    [Fact]
    public void HealStart_RoundTrips_SelfHeal()
    {
        // self-heal → target == healer
        var bytes = TacticalLiveCodec.EncodeHealStart(1u, 7, "Medkit_Heal", 7);
        Assert.True(TacticalLiveCodec.TryDecodeHealStart(bytes, out var s));
        Assert.Equal(7, s.HealerNetId);
        Assert.Equal(7, s.TargetNetId);
    }

    [Fact]
    public void HealStart_RoundTrips_ZeroValues()
    {
        var bytes = TacticalLiveCodec.EncodeHealStart(0u, 0, null, 0);
        Assert.True(TacticalLiveCodec.TryDecodeHealStart(bytes, out var s));
        Assert.Equal(0u, s.Seq);
        Assert.Equal(0, s.HealerNetId);
        Assert.Equal("", s.AbilityDefGuid);   // null encodes as empty
        Assert.Equal(0, s.TargetNetId);
    }

    [Fact]
    public void HealStart_RoundTrips_LargeValues()
    {
        var bytes = TacticalLiveCodec.EncodeHealStart(uint.MaxValue, int.MaxValue, "a-long-heal-ability-def-guid", int.MaxValue);
        Assert.True(TacticalLiveCodec.TryDecodeHealStart(bytes, out var s));
        Assert.Equal(uint.MaxValue, s.Seq);
        Assert.Equal(int.MaxValue, s.HealerNetId);
        Assert.Equal("a-long-heal-ability-def-guid", s.AbilityDefGuid);
        Assert.Equal(int.MaxValue, s.TargetNetId);
    }

    [Fact]
    public void HealStart_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeHealStart(new byte[] { 1, 2, 3 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeHealStart(null, out _));
        Assert.False(TacticalLiveCodec.TryDecodeHealStart(new byte[12], out _));   // one byte short of the minimum (seq+healer+emptyStr+target = 13)
    }
}
