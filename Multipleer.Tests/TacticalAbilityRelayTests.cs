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
}
