using Multipleer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for the tac.melee.start (0x91) WIRE FOUNDATION — Phase 1 of client melee-animation
/// replay. Mirrors <see cref="TacticalFireStartCodecTests"/> EXACTLY (minus shotCount: a melee is one
/// swing). Covers:
///   (a) the tac.melee.start wire codec round-trip (actor-netId target AND bare-position sentinel -1),
///   (b) the host-broadcast GATE decision (BashAbility → broadcast; shoot/grenade/move/overwatch/reload/
///       heal/unknown/null → no),
///   (c) target encode (actor netId vs bare-position sentinel -1, reusing the SAME TargetNetIdNone
///       sentinel the fire/intent surfaces use so host/client resolve identically).
/// Phase 1 is wire/host-emit ONLY — the client replay coroutine + damage/cost neuter patches are Phase 2.
/// </summary>
public class TacticalMeleeStartCodecTests
{
    // ─── (a) codec round-trip ─────────────────────────────────────────
    [Fact]
    public void MeleeStart_RoundTrips_ActorTarget()
    {
        var bytes = TacticalLiveCodec.EncodeMeleeStart(
            seq: 5u, attackerNetId: 42, abilityDefGuid: "bash-ability-guid-123", targetNetId: 99,
            tx: 1.5f, ty: -2.25f, tz: 3f);
        Assert.True(TacticalLiveCodec.TryDecodeMeleeStart(bytes, out var s));
        Assert.Equal(5u, s.Seq);
        Assert.Equal(42, s.AttackerNetId);
        Assert.Equal("bash-ability-guid-123", s.AbilityDefGuid);
        Assert.Equal(99, s.TargetNetId);
        Assert.Equal(1.5f, s.TX);
        Assert.Equal(-2.25f, s.TY);
        Assert.Equal(3f, s.TZ);
    }

    [Fact]
    public void MeleeStart_RoundTrips_PositionTarget_NoActor()
    {
        // (c) target encode: a bare-position swing carries TargetNetIdNone (-1) and a world point. Reuse the
        // SAME sentinel constant the fire/intent surfaces use.
        var bytes = TacticalLiveCodec.EncodeMeleeStart(
            seq: 1u, attackerNetId: 7, abilityDefGuid: "bash-guid", targetNetId: TacticalLiveCodec.TargetNetIdNone,
            tx: 10f, ty: 0f, tz: -7.5f);
        Assert.True(TacticalLiveCodec.TryDecodeMeleeStart(bytes, out var s));
        Assert.Equal(TacticalLiveCodec.TargetNetIdNone, s.TargetNetId);   // -1 sentinel survives
        Assert.Equal(10f, s.TX);
        Assert.Equal(-7.5f, s.TZ);
    }

    [Fact]
    public void MeleeStart_RoundTrips_EmptyGuid()
    {
        var empty = TacticalLiveCodec.EncodeMeleeStart(2u, 1, "", 2, 0f, 0f, 0f);
        Assert.True(TacticalLiveCodec.TryDecodeMeleeStart(empty, out var e));
        Assert.Equal("", e.AbilityDefGuid);

        var nul = TacticalLiveCodec.EncodeMeleeStart(4u, 1, null, 2, 0f, 0f, 0f);
        Assert.True(TacticalLiveCodec.TryDecodeMeleeStart(nul, out var n));
        Assert.Equal("", n.AbilityDefGuid);   // null guid → empty on the wire
    }

    [Fact]
    public void MeleeStart_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeMeleeStart(null, out _));
        Assert.False(TacticalLiveCodec.TryDecodeMeleeStart(new byte[3], out _));
    }

    [Fact]
    public void MeleeStart_RejectsChoppedMidField()
    {
        var bytes = TacticalLiveCodec.EncodeMeleeStart(1u, 1, "g", 2, 0f, 0f, 0f);
        // Lop off the trailing part of the vector → a well-formed frame cut short is a clean reject (no partial).
        var truncated = new byte[bytes.Length - 6];
        System.Array.Copy(bytes, truncated, truncated.Length);
        Assert.False(TacticalLiveCodec.TryDecodeMeleeStart(truncated, out _));
    }

    // ─── (b) host-broadcast gate decision ─────────────────────────────
    [Fact]
    public void ShouldBroadcastMeleeStart_True_ForBash()
        => Assert.True(TacticalAbilityRelay.ShouldBroadcastMeleeStart("BashAbility"));  // melee swing

    [Theory]
    [InlineData("ShootAbility")]      // shoot + grenade — fire-start animation, not melee
    [InlineData("MoveAbility")]       // dedicated move command-sync + animation (tac.move.start)
    [InlineData("OverwatchAbility")]  // dedicated arm + cone
    [InlineData("ReloadAbility")]     // equipment/ammo target — not actor/pos
    [InlineData("HealAbility")]       // Health.Add directly — no tac.damage
    [InlineData("SomeUnknownAbility")]
    [InlineData("")]
    [InlineData(null)]
    public void ShouldBroadcastMeleeStart_False_ForNonMelee(string typeName)
        => Assert.False(TacticalAbilityRelay.ShouldBroadcastMeleeStart(typeName));

    // The melee-start ANIMATION allowlist is a SUBSET of the damage-relay allowlist (BashAbility only): every
    // type that broadcasts a melee-start must also be damage-relayable. Melee damage rides tac.damage; this
    // surface adds ONLY the swing animation.
    [Fact]
    public void ShouldBroadcastMeleeStart_IsSubsetOfRelayable()
    {
        foreach (var name in TacticalAbilityRelay.MeleeStartAnimAbilityTypeNames)
        {
            Assert.True(TacticalAbilityRelay.ShouldBroadcastMeleeStart(name));
            Assert.True(TacticalAbilityRelay.IsRelayable(name));   // anim ⊆ damage relay
        }
        // Shoot is fire-start (not melee); damage-excluded types are also melee-animation-excluded.
        Assert.False(TacticalAbilityRelay.ShouldBroadcastMeleeStart("ShootAbility"));
        foreach (var name in TacticalAbilityRelay.ExcludedAbilityTypeNames)
            Assert.False(TacticalAbilityRelay.ShouldBroadcastMeleeStart(name));
    }
}
