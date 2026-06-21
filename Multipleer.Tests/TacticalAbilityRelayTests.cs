using Multipleer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE policy tests for the generic ability-intent relay allowlist (state-spine §3, Inc T2). No engine
/// types. Asserts the relayable set (shoot/grenade=ShootAbility, melee=BashAbility) and that
/// Move/Overwatch/Reload/Heal — which keep dedicated paths or have no outcome surface yet — are NEVER relayed
/// (so the generic Harmony patch can't double-handle them), plus null/empty/unknown safety.
/// </summary>
public class TacticalAbilityRelayTests
{
    [Theory]
    [InlineData("ShootAbility")]   // shoot AND grenade throw
    [InlineData("BashAbility")]    // melee
    public void Relayable_GameplayAbilities_AreRelayed(string typeName)
    {
        Assert.True(TacticalAbilityRelay.IsRelayable(typeName));
    }

    [Theory]
    [InlineData("MoveAbility")]       // dedicated move command-sync + animation
    [InlineData("OverwatchAbility")]  // dedicated 0x8C/0x8D arm + cone
    [InlineData("ReloadAbility")]     // equipment/ammo-clip target — rides the actor-state delta
    [InlineData("HealAbility")]       // Health.Add directly (no tac.damage); no health surface yet
    public void Excluded_DedicatedPathAbilities_AreNotRelayed(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsRelayable(typeName));
    }

    [Theory]
    [InlineData("ApplyStatusAbility")]
    [InlineData("EndTurnAbility")]
    [InlineData("SomeFutureUnknownAbility")]
    public void Unknown_Abilities_AreNotRelayed(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsRelayable(typeName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_IsNotRelayed(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsRelayable(typeName));
    }

    [Fact]
    public void RelayableAndExcluded_DoNotOverlap()
    {
        foreach (var ex in TacticalAbilityRelay.ExcludedAbilityTypeNames)
            Assert.False(TacticalAbilityRelay.IsRelayable(ex),
                "Excluded ability " + ex + " must never be relayable (would risk a Harmony double-prefix).");
    }

    // ── IsClientSuppressedActivation: the predicate SuppressedAbilityViewClearPatch reuses to decide whether the
    //    native post-Activate ClearStackAndPush must be skipped (only for abilities the client actually suppresses).
    //    The view-effect of the prefix (skip the clear, keep UIStateCharacterSelected) is engine-only → in-game test.

    [Theory]
    [InlineData("MoveAbility")]       // MoveAbilityActivatePatch suppresses Activate (dedicated move-sync)
    [InlineData("OverwatchAbility")]  // OverwatchAbilityActivatePatch suppresses Activate (dedicated arm-sync)
    [InlineData("ShootAbility")]      // AbilityActivateRelayPatch suppresses Activate (generic relay)
    [InlineData("BashAbility")]       // AbilityActivateRelayPatch suppresses Activate (generic relay)
    public void Suppressed_Activations_AreDetected(string typeName)
    {
        Assert.True(TacticalAbilityRelay.IsClientSuppressedActivation(typeName));
    }

    [Theory]
    [InlineData("ReloadAbility")]     // NO suppression patch — runs locally; must NOT be intercepted
    [InlineData("HealAbility")]       // NO suppression patch — runs locally; must NOT be intercepted
    [InlineData("ApplyStatusAbility")]
    [InlineData("EndTurnAbility")]
    [InlineData("SomeFutureUnknownAbility")]
    public void NonSuppressed_Activations_AreNotDetected(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsClientSuppressedActivation(typeName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Suppressed_NullOrEmpty_IsFalse(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsClientSuppressedActivation(typeName));
    }

    [Fact]
    public void Suppressed_Superset_Includes_AllRelayable()
    {
        // Every relayed ability is, by construction, a suppressed activation (the relay patch returns false).
        foreach (var r in TacticalAbilityRelay.RelayableAbilityTypeNames)
            Assert.True(TacticalAbilityRelay.IsClientSuppressedActivation(r));
    }
}
