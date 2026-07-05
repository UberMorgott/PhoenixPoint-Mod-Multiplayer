using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Batch-3 P5: the 0x69/0x6C occurrence-id dedup set — the STUN reliable transport deliberately sends every
/// reliable packet twice, so the second delivery of the SAME occId must be an idempotent no-op, while occId 0
/// (legacy unstamped wire) must never dedup (fail-open; the byte-level ReportOutcomeDedup belt covers it).
/// </summary>
public class ReportOccurrenceDedupTests
{
    [Fact]
    public void FirstDelivery_Passes_SecondIsDuplicate()
    {
        var d = new ReportOccurrenceDedup();
        Assert.False(d.SeenBefore(7));   // first → show
        Assert.True(d.SeenBefore(7));    // STUN double-send → drop
        Assert.True(d.SeenBefore(7));    // any later re-delivery → still dropped
    }

    [Fact]
    public void DistinctIds_NeverCollide()
    {
        var d = new ReportOccurrenceDedup();
        Assert.False(d.SeenBefore(1));
        Assert.False(d.SeenBefore(2));
        Assert.False(d.SeenBefore(3));
        Assert.True(d.SeenBefore(2));
    }

    [Fact]
    public void LegacyZero_IsNeverDeduped()
    {
        var d = new ReportOccurrenceDedup();
        Assert.False(d.SeenBefore(0));
        Assert.False(d.SeenBefore(0));   // unstamped wire keeps its pre-Batch-3 behavior
    }

    [Fact]
    public void Tracking_IsBounded_OldestEvicted()
    {
        var d = new ReportOccurrenceDedup();
        for (ushort id = 1; id <= ReportOccurrenceDedup.MaxTracked + 1; id++)
            Assert.False(d.SeenBefore(id));
        // id 1 fell off the bounded FIFO → treated as fresh again (bounded memory beats stale dedup).
        Assert.False(d.SeenBefore(1));
        // …and is re-tracked from here.
        Assert.True(d.SeenBefore(1));
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var d = new ReportOccurrenceDedup();
        Assert.False(d.SeenBefore(42));
        d.Reset();
        Assert.False(d.SeenBefore(42));   // save-transfer boundary → post-transfer ids start fresh
    }
}
