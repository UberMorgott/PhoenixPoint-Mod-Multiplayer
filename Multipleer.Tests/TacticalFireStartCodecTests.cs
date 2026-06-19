using Multipleer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for Feature C (tac.fire.start — client-side ATTACK ANIMATION). No engine types —
/// mirrors <see cref="TacticalCombatCodecTests"/>. Covers:
///   (a) the tac.fire.start wire codec round-trip (actor-netId target AND bare-position sentinel -1),
///   (b) the host-broadcast GATE decision (relayable type → broadcast; non-relayable → no),
///   (c) target encode (actor netId vs bare-position sentinel -1, reusing the same target shape as
///       tac.intent.ability so the host/client resolve identically).
/// </summary>
public class TacticalFireStartCodecTests
{
    // ─── (a) codec round-trip ─────────────────────────────────────────
    [Fact]
    public void FireStart_RoundTrips_ActorTarget()
    {
        var bytes = TacticalLiveCodec.EncodeFireStart(
            seq: 5u, shooterNetId: 42, abilityDefGuid: "shoot-ability-guid-123", targetNetId: 99,
            tx: 1.5f, ty: -2.25f, tz: 3f, shotCount: 4);
        Assert.True(TacticalLiveCodec.TryDecodeFireStart(bytes, out var s));
        Assert.Equal(5u, s.Seq);
        Assert.Equal(42, s.ShooterNetId);
        Assert.Equal("shoot-ability-guid-123", s.AbilityDefGuid);
        Assert.Equal(99, s.TargetNetId);
        Assert.Equal(1.5f, s.TX);
        Assert.Equal(-2.25f, s.TY);
        Assert.Equal(3f, s.TZ);
        Assert.Equal(4, s.ShotCount);
    }

    [Fact]
    public void FireStart_RoundTrips_BarePositionSentinel()
    {
        // (c) target encode: a bare-position shot/throw carries TargetNetIdNone (-1) and a world point.
        var bytes = TacticalLiveCodec.EncodeFireStart(
            seq: 1u, shooterNetId: 7, abilityDefGuid: "grenade-guid", targetNetId: TacticalLiveCodec.TargetNetIdNone,
            tx: 10f, ty: 0f, tz: -7.5f, shotCount: 1);
        Assert.True(TacticalLiveCodec.TryDecodeFireStart(bytes, out var s));
        Assert.Equal(TacticalLiveCodec.TargetNetIdNone, s.TargetNetId);   // -1 sentinel survives
        Assert.Equal(10f, s.TX);
        Assert.Equal(-7.5f, s.TZ);
        Assert.Equal(1, s.ShotCount);
    }

    [Fact]
    public void FireStart_RoundTrips_EmptyAndLongAndNullGuid()
    {
        var empty = TacticalLiveCodec.EncodeFireStart(2u, 1, "", 2, 0f, 0f, 0f, 0);
        Assert.True(TacticalLiveCodec.TryDecodeFireStart(empty, out var e));
        Assert.Equal("", e.AbilityDefGuid);

        var longGuid = new string('a', 600);   // forces a multi-byte 7-bit length prefix
        var lng = TacticalLiveCodec.EncodeFireStart(3u, 7, longGuid, 8, 0f, 0f, 0f, 2);
        Assert.True(TacticalLiveCodec.TryDecodeFireStart(lng, out var l));
        Assert.Equal(longGuid, l.AbilityDefGuid);

        var nul = TacticalLiveCodec.EncodeFireStart(4u, 1, null, 2, 0f, 0f, 0f, 1);
        Assert.True(TacticalLiveCodec.TryDecodeFireStart(nul, out var n));
        Assert.Equal("", n.AbilityDefGuid);   // null guid → empty on the wire
    }

    [Fact]
    public void FireStart_RejectsTruncated()
    {
        var bytes = TacticalLiveCodec.EncodeFireStart(1u, 1, "g", 2, 0f, 0f, 0f, 1);
        // Lop off the trailing shotCount + part of the vector → a short buffer is a clean reject (no partial).
        var truncated = new byte[bytes.Length - 6];
        System.Array.Copy(bytes, truncated, truncated.Length);
        Assert.False(TacticalLiveCodec.TryDecodeFireStart(truncated, out _));
        Assert.False(TacticalLiveCodec.TryDecodeFireStart(null, out _));
        Assert.False(TacticalLiveCodec.TryDecodeFireStart(new byte[3], out _));
    }

    // ─── (b) host-broadcast gate decision ─────────────────────────────
    [Fact]
    public void ShouldBroadcastFireStart_True_ForShootAndGrenade()
        => Assert.True(TacticalAbilityRelay.ShouldBroadcastFireStart("ShootAbility"));  // shoot + grenade

    [Theory]
    [InlineData("BashAbility")]       // melee — DEFERRED (BashAbility animates via BashCrt, not FireWeaponAtTargetCrt)
    [InlineData("MoveAbility")]       // dedicated move command-sync + animation (tac.move.start)
    [InlineData("OverwatchAbility")]  // dedicated arm + cone
    [InlineData("ReloadAbility")]     // equipment/ammo target — not actor/pos
    [InlineData("HealAbility")]       // Health.Add directly — no tac.damage
    [InlineData("SomeUnknownAbility")]
    [InlineData("")]
    [InlineData(null)]
    public void ShouldBroadcastFireStart_False_ForNonShootAttacks(string typeName)
        => Assert.False(TacticalAbilityRelay.ShouldBroadcastFireStart(typeName));

    // The fire-start ANIMATION allowlist is a SUBSET of the damage-relay allowlist (shoot+grenade only): every
    // type that broadcasts a fire-start must also be damage-relayable, but NOT every relayable type (melee)
    // gets an animation yet. Damage and animation share the shoot subset; melee damage still rides tac.damage.
    [Fact]
    public void ShouldBroadcastFireStart_IsSubsetOfRelayable()
    {
        foreach (var name in TacticalAbilityRelay.FireStartAnimAbilityTypeNames)
        {
            Assert.True(TacticalAbilityRelay.ShouldBroadcastFireStart(name));
            Assert.True(TacticalAbilityRelay.IsRelayable(name));   // anim ⊆ damage relay
        }
        // Melee is damage-relayable but has NO animation broadcast (deferred).
        Assert.True(TacticalAbilityRelay.IsRelayable("BashAbility"));
        Assert.False(TacticalAbilityRelay.ShouldBroadcastFireStart("BashAbility"));
        // Damage-excluded types are also animation-excluded.
        foreach (var name in TacticalAbilityRelay.ExcludedAbilityTypeNames)
            Assert.False(TacticalAbilityRelay.ShouldBroadcastFireStart(name));
    }
}
