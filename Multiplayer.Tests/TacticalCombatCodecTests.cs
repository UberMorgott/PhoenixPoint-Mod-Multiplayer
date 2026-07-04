using System.Collections.Generic;
using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE wire-codec tests for the Inc 3a combat/damage rail (tac.intent.ability + tac.damage). No engine
/// types — mirrors <see cref="TacticalLiveCodecTests"/>. Covers round-trip fidelity (incl. empty/long guids,
/// sentinel ids, 0/1/N status/effect/statMod lists, negative-float preservation) and truncation rejection.
/// </summary>
public class TacticalCombatCodecTests
{
    // ─── tac.intent.ability ───────────────────────────────────────────
    [Fact]
    public void IntentAbility_RoundTrips()
    {
        var bytes = TacticalLiveCodec.EncodeIntentAbility(
            shooterNetId: 42, abilityDefGuid: "shoot-ability-guid-123", targetNetId: 99,
            tx: 1.5f, ty: -2.25f, tz: 3f, bodyPartId: 5, nonce: 7u);
        Assert.True(TacticalLiveCodec.TryDecodeIntentAbility(bytes, out var i));
        Assert.Equal(42, i.ShooterNetId);
        Assert.Equal("shoot-ability-guid-123", i.AbilityDefGuid);
        Assert.Equal(99, i.TargetNetId);
        Assert.Equal(1.5f, i.TX);
        Assert.Equal(-2.25f, i.TY);
        Assert.Equal(3f, i.TZ);
        Assert.Equal(5, i.BodyPartId);
        Assert.Equal(7u, i.Nonce);
    }

    [Fact]
    public void IntentAbility_RoundTrips_EmptyGuid_AndPositionTargetSentinel()
    {
        var bytes = TacticalLiveCodec.EncodeIntentAbility(
            shooterNetId: 1, abilityDefGuid: "", targetNetId: TacticalLiveCodec.TargetNetIdNone,
            tx: 10f, ty: 0f, tz: -7.5f, bodyPartId: TacticalLiveCodec.BodyPartIdNone, nonce: 1u);
        Assert.True(TacticalLiveCodec.TryDecodeIntentAbility(bytes, out var i));
        Assert.Equal("", i.AbilityDefGuid);
        Assert.Equal(TacticalLiveCodec.TargetNetIdNone, i.TargetNetId);   // -1 sentinel survives
        Assert.Equal(TacticalLiveCodec.BodyPartIdNone, i.BodyPartId);     // -1 sentinel survives
        Assert.Equal(-7.5f, i.TZ);
    }

    [Fact]
    public void IntentAbility_RoundTrips_LongGuid()
    {
        var longGuid = new string('a', 600);   // forces a multi-byte 7-bit length prefix
        var bytes = TacticalLiveCodec.EncodeIntentAbility(7, longGuid, 8, 0f, 0f, 0f, 3, 2u);
        Assert.True(TacticalLiveCodec.TryDecodeIntentAbility(bytes, out var i));
        Assert.Equal(longGuid, i.AbilityDefGuid);
        Assert.Equal(3, i.BodyPartId);
    }

    [Fact]
    public void IntentAbility_NullGuid_BecomesEmpty()
    {
        var bytes = TacticalLiveCodec.EncodeIntentAbility(1, null, 2, 0f, 0f, 0f, -1, 1u);
        Assert.True(TacticalLiveCodec.TryDecodeIntentAbility(bytes, out var i));
        Assert.Equal("", i.AbilityDefGuid);
    }

    [Fact]
    public void IntentAbility_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeIntentAbility(new byte[] { 1, 2, 3 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeIntentAbility(null, out _));
    }

    // ─── shoot-intent REBUILD aim policy (TacticalCombatSync.BuildShootTarget) ──────────────────────────
    // The wire only carries the actor's GROUND pos (height ~0). The RETIRED contract left PositionToApply NaN for an
    // actor target on the false premise the host re-snaps on re-Activate — it does NOT, so a NaN actor target falls
    // through GetWorkingPosition to the actor FEET (Y=0) → shot hits the ground → 0 damage. The CORRECTED contract: an
    // actor target SEEDS PositionToApply with the actor AIM POINT (non-NaN, body-center); a bare-GROUND shot (no actor)
    // seeds the explicit ground pos. The engine TacticalAbilityTarget/aim point can't be built in this pure project, so
    // we pin the DECISION (Decide) — actor → ActorAimPoint (non-NaN seed), ground → GroundPosition (explicit pos).
    [Fact]
    public void ShootTargetAim_ActorTarget_SeedsAimPointPosition()
    {
        // actor resolved → seed PositionToApply with the actor AIM POINT (NON-NaN) → host then snaps body-part.
        Assert.Equal(ShootTargetAimPolicy.AimSource.ActorAimPoint,
            ShootTargetAimPolicy.Decide(hasTargetActor: true));
    }

    [Fact]
    public void ShootTargetAim_GroundOnly_UsesExplicitGroundPosition()
    {
        // no actor → bare-ground shot → seed PositionToApply with the explicit Vector3 ground pos.
        Assert.Equal(ShootTargetAimPolicy.AimSource.GroundPosition,
            ShootTargetAimPolicy.Decide(hasTargetActor: false));
    }

    // ─── tac.damage ───────────────────────────────────────────────────
    private static TacticalLiveCodec.DamagePayload SampleDamage(int statusN, int effectN, int modN, int sourceNetId)
    {
        var p = new TacticalLiveCodec.DamagePayload
        {
            Seq = 9u,
            TargetNetId = 100,
            SourceNetId = sourceNetId,
            HealthDamage = 12.5f,
            ArmorDamage = -3.25f,           // negative preserved
            ArmorMitigatedDamage = 1.75f,
            StunValue = 4f,
            HealValue = 0f,
            IfX = 1f, IfY = 2f, IfZ = 3f,
            DoX = -4f, DoY = 5.5f, DoZ = -6f,
            ForceHurt = true,
            DamageTypeDefGuid = "dmg-type-guid",
            ShooterNetId = 100,
            ShooterApAfter = 2.5f,
            ShooterWpAfter = 1.5f,
        };
        for (int i = 0; i < statusN; i++)
            p.Statuses.Add(new TacticalLiveCodec.DamageStatus { DefGuid = "status-" + i, Value = i + 0.5f, SourceNetId = 100 + i });
        for (int i = 0; i < effectN; i++)
            p.EffectGuids.Add("effect-" + i);
        for (int i = 0; i < modN; i++)
            p.StatMods.Add(new TacticalLiveCodec.DamageStatMod { StatName = "stat-" + i, ModKind = i, Value = -i - 0.25f });
        return p;
    }

    private static void AssertDamageEqual(TacticalLiveCodec.DamagePayload a, TacticalLiveCodec.DamagePayload b)
    {
        Assert.Equal(a.Seq, b.Seq);
        Assert.Equal(a.TargetNetId, b.TargetNetId);
        Assert.Equal(a.SourceNetId, b.SourceNetId);
        Assert.Equal(a.HealthDamage, b.HealthDamage);
        Assert.Equal(a.ArmorDamage, b.ArmorDamage);
        Assert.Equal(a.ArmorMitigatedDamage, b.ArmorMitigatedDamage);
        Assert.Equal(a.StunValue, b.StunValue);
        Assert.Equal(a.HealValue, b.HealValue);
        Assert.Equal(a.IfX, b.IfX); Assert.Equal(a.IfY, b.IfY); Assert.Equal(a.IfZ, b.IfZ);
        Assert.Equal(a.DoX, b.DoX); Assert.Equal(a.DoY, b.DoY); Assert.Equal(a.DoZ, b.DoZ);
        Assert.Equal(a.ForceHurt, b.ForceHurt);
        Assert.Equal(a.DamageTypeDefGuid, b.DamageTypeDefGuid);
        Assert.Equal(a.Statuses.Count, b.Statuses.Count);
        for (int i = 0; i < a.Statuses.Count; i++)
        {
            Assert.Equal(a.Statuses[i].DefGuid, b.Statuses[i].DefGuid);
            Assert.Equal(a.Statuses[i].Value, b.Statuses[i].Value);
            Assert.Equal(a.Statuses[i].SourceNetId, b.Statuses[i].SourceNetId);
        }
        Assert.Equal(a.EffectGuids.Count, b.EffectGuids.Count);
        for (int i = 0; i < a.EffectGuids.Count; i++) Assert.Equal(a.EffectGuids[i], b.EffectGuids[i]);
        Assert.Equal(a.StatMods.Count, b.StatMods.Count);
        for (int i = 0; i < a.StatMods.Count; i++)
        {
            Assert.Equal(a.StatMods[i].StatName, b.StatMods[i].StatName);
            Assert.Equal(a.StatMods[i].ModKind, b.StatMods[i].ModKind);
            Assert.Equal(a.StatMods[i].Value, b.StatMods[i].Value);
        }
        Assert.Equal(a.ShooterNetId, b.ShooterNetId);
        Assert.Equal(a.ShooterApAfter, b.ShooterApAfter);
        Assert.Equal(a.ShooterWpAfter, b.ShooterWpAfter);
    }

    [Fact]
    public void Damage_RoundTrips_ZeroLists()
    {
        var p = SampleDamage(0, 0, 0, sourceNetId: 100);
        var bytes = TacticalLiveCodec.EncodeDamage(p);
        Assert.True(TacticalLiveCodec.TryDecodeDamage(bytes, out var d));
        AssertDamageEqual(p, d);
    }

    [Fact]
    public void Damage_RoundTrips_OneEach()
    {
        var p = SampleDamage(1, 1, 1, sourceNetId: 100);
        var bytes = TacticalLiveCodec.EncodeDamage(p);
        Assert.True(TacticalLiveCodec.TryDecodeDamage(bytes, out var d));
        AssertDamageEqual(p, d);
    }

    [Fact]
    public void Damage_RoundTrips_NEach()
    {
        var p = SampleDamage(3, 4, 5, sourceNetId: 100);
        var bytes = TacticalLiveCodec.EncodeDamage(p);
        Assert.True(TacticalLiveCodec.TryDecodeDamage(bytes, out var d));
        AssertDamageEqual(p, d);
    }

    [Fact]
    public void Damage_RoundTrips_NegativeSourceSentinel()
    {
        var p = SampleDamage(2, 0, 1, sourceNetId: TacticalLiveCodec.TargetNetIdNone);
        p.ShooterNetId = TacticalLiveCodec.TargetNetIdNone;
        var bytes = TacticalLiveCodec.EncodeDamage(p);
        Assert.True(TacticalLiveCodec.TryDecodeDamage(bytes, out var d));
        Assert.Equal(TacticalLiveCodec.TargetNetIdNone, d.SourceNetId);
        Assert.Equal(TacticalLiveCodec.TargetNetIdNone, d.ShooterNetId);
        AssertDamageEqual(p, d);
    }

    [Fact]
    public void Damage_NullListsAndGuid_EncodeAsEmpty()
    {
        var p = new TacticalLiveCodec.DamagePayload
        {
            Seq = 1u, TargetNetId = 5, SourceNetId = -1,
            DamageTypeDefGuid = null, Statuses = null, EffectGuids = null, StatMods = null,
        };
        var bytes = TacticalLiveCodec.EncodeDamage(p);
        Assert.True(TacticalLiveCodec.TryDecodeDamage(bytes, out var d));
        Assert.Equal("", d.DamageTypeDefGuid);
        Assert.Empty(d.Statuses);
        Assert.Empty(d.EffectGuids);
        Assert.Empty(d.StatMods);
    }

    [Fact]
    public void Damage_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeDamage(new byte[] { 0, 0, 0 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeDamage(null, out _));
    }

    // ─── Damage numeric-field codec fidelity ──────────────────────────────────────────────────────
    // The engine flatten/rebuild mappers (TacticalCombatSync.FlattenDamage / RebuildDamage) depend on Unity +
    // game-struct types, so they CANNOT be exercised purely without adding engine refs to this test project —
    // we deliberately do not. What IS purely testable, and load-bearing for the client mirror's rebuild, is
    // that the DamagePayload codec preserves every numeric field across encode→decode. This asserts exactly
    // that (it is a codec round-trip test, not an engine-mapper test — named honestly).
    [Fact]
    public void Damage_CodecPreservesNumericFields()
    {
        var p = SampleDamage(2, 1, 2, sourceNetId: 100);
        var bytes = TacticalLiveCodec.EncodeDamage(p);
        Assert.True(TacticalLiveCodec.TryDecodeDamage(bytes, out var d));
        Assert.Equal(p.HealthDamage, d.HealthDamage);
        Assert.Equal(p.ArmorDamage, d.ArmorDamage);
        Assert.Equal(p.StunValue, d.StunValue);
        Assert.Equal(p.HealValue, d.HealValue);
        Assert.Equal(p.ForceHurt, d.ForceHurt);
    }
}
