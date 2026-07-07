using Multiplayer.Network.Sync;
using Xunit;

// Pure host-side SP spend split for the progression intents (LevelUpAbility / SpendStatPoints) —
// soldier SkillPoints pay first, shortfall spills into the shared faction pool, combined-pool
// affordability gate (mirrors UIModuleCharacterProgression.ConsumeAbilityCost + the stat branch).
public class ProgressionSpendTests
{
    [Fact]
    public void SoldierPoolCoversCost_FactionUntouched()
    {
        Assert.True(ProgressionSpend.TrySplit(10, 25, 5, out int sp, out int fsp));
        Assert.Equal(15, sp);
        Assert.Equal(5, fsp);
    }

    [Fact]
    public void ExactSoldierPool_DrainsToZero_FactionUntouched()
    {
        Assert.True(ProgressionSpend.TrySplit(25, 25, 7, out int sp, out int fsp));
        Assert.Equal(0, sp);
        Assert.Equal(7, fsp);
    }

    [Fact]
    public void Shortfall_SpillsIntoFactionPool()
    {
        // Native ConsumeAbilityCost: soldier SP floors at 0, remainder comes off the faction pool.
        Assert.True(ProgressionSpend.TrySplit(30, 25, 10, out int sp, out int fsp));
        Assert.Equal(0, sp);
        Assert.Equal(5, fsp);
    }

    [Fact]
    public void ZeroSoldierPool_FactionPaysAll()
    {
        Assert.True(ProgressionSpend.TrySplit(4, 0, 9, out int sp, out int fsp));
        Assert.Equal(0, sp);
        Assert.Equal(5, fsp);
    }

    [Fact]
    public void CombinedPoolExactlyCost_Affordable_BothDrainToZero()
    {
        Assert.True(ProgressionSpend.TrySplit(35, 25, 10, out int sp, out int fsp));
        Assert.Equal(0, sp);
        Assert.Equal(0, fsp);
    }

    [Fact]
    public void CombinedPoolInsufficient_RejectsWithoutMutating()
    {
        Assert.False(ProgressionSpend.TrySplit(36, 25, 10, out int sp, out int fsp));
        Assert.Equal(25, sp);   // all-or-nothing: echo inputs, host rejects the intent
        Assert.Equal(10, fsp);
    }

    [Fact]
    public void ZeroCost_IsFreeNoOp()
    {
        Assert.True(ProgressionSpend.TrySplit(0, 3, 4, out int sp, out int fsp));
        Assert.Equal(3, sp);
        Assert.Equal(4, fsp);
    }

    [Fact]
    public void NegativeCost_RejectedNeverRefunds()
    {
        // A negative cost would silently CREDIT points — fail closed.
        Assert.False(ProgressionSpend.TrySplit(-5, 3, 4, out int sp, out int fsp));
        Assert.Equal(3, sp);
        Assert.Equal(4, fsp);
    }
}
