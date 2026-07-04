using Multiplayer.Network.Sync.State;
using Xunit;

public class RewardPendingSlotsTests
{
    private static RewardDisplaySnapshot Reward(int skillPoints)
        => new RewardDisplaySnapshot { FactionSkillPoints = skillPoints };   // non-empty (IsEmpty == false)

    [Fact]
    public void Arm_ThenConsume_ReturnsRewardOnceThenNull()
    {
        var slots = new RewardPendingSlots();
        var ev = new object();
        var reward = Reward(7);

        slots.Arm(ev, reward);
        Assert.Same(reward, slots.TryConsume(ev));   // first consume returns it
        Assert.Null(slots.TryConsume(ev));            // one-shot: gone after consume
        Assert.Equal(0, slots.Count);
    }

    [Fact]
    public void Arm_SecondEvent_DoesNotClobberFirst()
    {
        // The burst-desync fix: a 2nd result page arming before the 1st rendered must NOT drop the 1st reward
        // (the exact failure of the legacy single slot — host rewardBytes=112, client drew 0 lines).
        var slots = new RewardPendingSlots();
        var e1 = new object();
        var e2 = new object();
        var r1 = Reward(1);
        var r2 = Reward(2);

        slots.Arm(e1, r1);
        slots.Arm(e2, r2);
        Assert.Equal(2, slots.Count);

        Assert.Same(r2, slots.TryConsume(e2));   // each lands on its own page
        Assert.Same(r1, slots.TryConsume(e1));   // first reward survived the second arm
    }

    [Fact]
    public void TryConsume_UnrelatedOrNullEvent_ReturnsNull()
    {
        var slots = new RewardPendingSlots();
        slots.Arm(new object(), Reward(3));
        Assert.Null(slots.TryConsume(new object()));   // different instance → not our page
        Assert.Null(slots.TryConsume(null));
    }

    [Fact]
    public void Arm_SameInstanceTwice_OverwritesInPlace_NoDuplicate()
    {
        var slots = new RewardPendingSlots();
        var ev = new object();
        slots.Arm(ev, Reward(1));
        var r2 = Reward(2);
        slots.Arm(ev, r2);
        Assert.Equal(1, slots.Count);            // same key → one slot
        Assert.Same(r2, slots.TryConsume(ev));    // latest reward wins
    }

    [Fact]
    public void Arm_NullEventOrEmptyReward_Ignored()
    {
        var slots = new RewardPendingSlots();
        slots.Arm(null, Reward(1));
        slots.Arm(new object(), null);
        slots.Arm(new object(), new RewardDisplaySnapshot());   // empty snapshot → nothing to render
        Assert.Equal(0, slots.Count);
    }

    [Fact]
    public void Clear_DropsAllArmedSlots()
    {
        var slots = new RewardPendingSlots();
        var ev = new object();
        slots.Arm(ev, Reward(1));
        slots.Arm(new object(), Reward(2));
        slots.Clear();
        Assert.Equal(0, slots.Count);
        Assert.Null(slots.TryConsume(ev));
    }

    [Fact]
    public void Arm_PastCapacity_EvictsOldest_BoundedNoLeak()
    {
        var slots = new RewardPendingSlots();
        var first = new object();
        slots.Arm(first, Reward(99));
        // Fill exactly to capacity with fresh events; the first arm is now the oldest and must be evicted.
        for (int i = 0; i < RewardPendingSlots.MaxPending; i++) slots.Arm(new object(), Reward(i + 1));
        Assert.Equal(RewardPendingSlots.MaxPending, slots.Count);   // hard-bounded
        Assert.Null(slots.TryConsume(first));                       // oldest evicted, never leaks
    }
}
