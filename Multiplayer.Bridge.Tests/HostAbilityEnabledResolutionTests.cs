using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using Xunit;

// Pins the reflection RESOLUTION of TacticalCombatSync.HostAbilityEnabled against the SHIPPED
// 0Harmony.dll (this test project loads the real ModSDK copy at runtime, Private=true). The gated
// leaf abilities (TacticalAbilityRelay.HostEnabledCheckAbilityTypeNames) do NOT declare IsEnabled —
// it is declared on the TacticalAbility/Ability BASE (TacticalAbility.cs:367, Ability.cs:29) — so the
// gate must resolve with base-walking AccessTools.Method. Review of 56558d2 caught the original
// AccessTools.FirstMethod (DECLARED-ONLY) resolution returning null on every gated leaf → a
// permanently fail-open dead gate. These tests fail on that regression (and on a future Harmony
// bump changing either resolution semantic).
public class HostAbilityEnabledResolutionTests
{
    // Stub hierarchy shaped exactly like Ability → TacticalAbility → DeployTurretAbility:
    // the leaf declares NOTHING — IsEnabled is reachable only by walking base types, and the
    // single 1-param overload takes a reference type (invoked with null, like the filter param).
    public abstract class StubAbilityBase
    {
        public virtual bool IsEnabled(object filter = null) => true;
    }

    public class StubTacticalAbility : StubAbilityBase
    {
        public bool Enabled = true;
        public override bool IsEnabled(object filter = null) => Enabled;
    }

    public sealed class StubLeafAbility : StubTacticalAbility { }

    private static bool HostAbilityEnabled(object ability)
    {
        var m = typeof(TacticalCombatSync).GetMethod("HostAbilityEnabled",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return (bool)m.Invoke(null, new[] { ability });
    }

    [Fact]
    public void DisabledLeafAbility_IsGated()   // the exact case the dead FirstMethod gate let through
        => Assert.False(HostAbilityEnabled(new StubLeafAbility { Enabled = false }));

    [Fact]
    public void EnabledLeafAbility_Passes()
        => Assert.True(HostAbilityEnabled(new StubLeafAbility { Enabled = true }));

    [Fact]
    public void ShippedHarmony_FirstMethodIsDeclaredOnly_MethodWalksBases()   // documents WHY Method, not FirstMethod
    {
        Assert.Null(AccessTools.FirstMethod(typeof(StubLeafAbility),
            x => x.Name == "IsEnabled" && x.GetParameters().Length == 1));
        Assert.NotNull(AccessTools.Method(typeof(StubLeafAbility), "IsEnabled"));
    }
}
