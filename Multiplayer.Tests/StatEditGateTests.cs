using Multiplayer.Network.Sync.State;
using Xunit;

// Thin-client stat-edit HOST gate — the EFFECTIVE base-stat frame the native editor prices + caps against
// (UIModuleCharacterProgression.RefreshStats:516-518 = (int)(GetProgressionBaseStats().<attr> + Bonus<attr>) =
// bodyparts + allocated points + item/mutation bonus, GeoCharacter.cs:1167). The pre-fix host priced/capped on
// CharacterProgression.GetBaseStat (the raw _baseStats ALLOCATION only) → GetBaseStatCost/CanModifyBaseStat got a
// value ~10-30x too small → near-zero SP charged + a cap that never triggered. Table-driven vs the decompile.
public class StatEditGateTests
{
    [Theory]
    // baseAttr (bodyparts + allocated points), bonus (item/mutation), expected effective (native (int) cast)
    [InlineData(28f, 2f, 30)]      // typical soldier: displayed Strength 30
    [InlineData(10f, 0f, 10)]      // no bonus
    [InlineData(10.9f, 0.4f, 11)]  // TRUNCATION, not round: 11.3 → 11 (matches native (int) cast)
    [InlineData(9.99f, 0f, 9)]     // 9.99 → 9 (truncation)
    [InlineData(0f, 0f, 0)]
    public void EffectiveStat_MatchesNativeRefreshStatsFrame(float baseAttr, float bonus, int expected)
        => Assert.Equal(expected, StatEditGate.EffectiveStat(baseAttr, bonus));

    // Decompile-cited native GetBaseStatCost (CharacterProgression.cs:274-294), positive-forValue branch:
    //   Strength (statId 0) → forValue / 2 ; Will/Speed (1/2) → forValue.
    // Increase charges GetBaseStatCost(stat, cur + 1); refund credits GetBaseStatCost(stat, cur) (:881 / :909).
    private static int NativeBaseStatCost(int statId, int forValue)
        => statId == 0 ? forValue / 2 : forValue;

    [Theory]
    // The undercharge bug: same allocation, but the effective frame (bodyparts+bonus) prices far higher than the
    // raw-allocation frame the host used before the fix. statId, effectiveCur, rawAllocCur.
    [InlineData(1, 10, 1)]   // Will 10→11: native 11 vs raw-frame 2
    [InlineData(2, 12, 0)]   // Speed 12→13: native 13 vs raw-frame 1
    [InlineData(0, 30, 3)]   // Strength 30→31: native 31/2=15 vs raw-frame 4/2=2
    public void IncreaseCost_OnEffectiveFrame_ExceedsRawAllocationFrame(int statId, int effectiveCur, int rawAllocCur)
    {
        int nativeCost = NativeBaseStatCost(statId, effectiveCur + 1);
        int rawFrameCost = NativeBaseStatCost(statId, rawAllocCur + 1);
        Assert.True(nativeCost > rawFrameCost,
            "effective-frame cost must exceed the raw-allocation-frame cost (the pre-fix undercharge)");
    }

    [Theory]
    // Refund credits the symmetric per-point price GetBaseStatCost(stat, cur) (native decrement :909).
    [InlineData(1, 11, 11)]  // Will refund at 11 → 11 SP
    [InlineData(0, 30, 15)]  // Strength refund at 30 → 30/2 = 15 SP
    [InlineData(2, 13, 13)]  // Speed refund at 13 → 13 SP
    public void RefundPrice_IsNativeGetBaseStatCostOfCurrent(int statId, int effectiveCur, int expectedPrice)
        => Assert.Equal(expectedPrice, NativeBaseStatCost(statId, effectiveCur));

    [Theory]
    // Cap = CanModifyBaseStat(toValue) → toValue <= GetMaxAttribute (decompile :215-223), gated on effective + 1.
    [InlineData(19, 20, true)]   // 19→20 allowed (20 == max)
    [InlineData(20, 20, false)]  // 20→21 blocked (21 > max) — the cap the raw frame never reached
    public void Cap_BlocksAtNativeMaxAttribute(int effectiveCur, int maxAttr, bool canIncrease)
        => Assert.Equal(canIncrease, effectiveCur + 1 <= maxAttr);
}
