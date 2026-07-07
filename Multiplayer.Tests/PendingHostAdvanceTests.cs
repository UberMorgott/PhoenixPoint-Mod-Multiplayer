using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Host-side buffer for CLIENT single-choice advance requests that beat the host's prompt show (occId=1 PROG_PX12
/// regression: the host's dialog was queued behind a cutscene so the advance found nothing showing and was dropped,
/// leaving the client stuck on window-1). Bounded FIFO, process-global static state → runs serially (Collection).
/// </summary>
[Collection("PendingHostAdvance")]
public class PendingHostAdvanceTests
{
    [Fact]
    public void Buffer_ThenTryGet_ReturnsEventId()
    {
        PendingHostAdvance.Reset();
        PendingHostAdvance.Buffer(5, "PROG_PX12");
        Assert.True(PendingHostAdvance.TryGet(5, out var id));
        Assert.Equal("PROG_PX12", id);
        Assert.Equal(1, PendingHostAdvance.Count);
    }

    [Fact]
    public void TryGet_UnknownOrZero_IsFalse()
    {
        PendingHostAdvance.Reset();
        Assert.False(PendingHostAdvance.TryGet(99, out _));
        PendingHostAdvance.Buffer(0, "ignored");           // 0 is the null sentinel — never buffered
        Assert.False(PendingHostAdvance.TryGet(0, out _));
        Assert.Equal(0, PendingHostAdvance.Count);
    }

    [Fact]
    public void Remove_DropsEntry()
    {
        PendingHostAdvance.Reset();
        PendingHostAdvance.Buffer(3, "SDI_07");
        PendingHostAdvance.Remove(3);
        Assert.False(PendingHostAdvance.TryGet(3, out _));
        Assert.Equal(0, PendingHostAdvance.Count);
    }

    [Fact]
    public void Buffer_SameOcc_RefreshesInPlace_NoDuplicateSlot()
    {
        PendingHostAdvance.Reset();
        PendingHostAdvance.Buffer(7, "A");
        PendingHostAdvance.Buffer(7, "B");                  // same occId → overwrite, still one slot
        Assert.Equal(1, PendingHostAdvance.Count);
        Assert.True(PendingHostAdvance.TryGet(7, out var id));
        Assert.Equal("B", id);
    }

    [Fact]
    public void Buffer_EvictsOldestPastCapacity()
    {
        PendingHostAdvance.Reset();
        for (ushort n = 1; n <= PendingHostAdvance.MaxTracked; n++)
            PendingHostAdvance.Buffer(n, "e" + n);
        Assert.Equal(PendingHostAdvance.MaxTracked, PendingHostAdvance.Count);

        PendingHostAdvance.Buffer((ushort)(PendingHostAdvance.MaxTracked + 1), "overflow");
        Assert.Equal(PendingHostAdvance.MaxTracked, PendingHostAdvance.Count);   // stays bounded
        Assert.False(PendingHostAdvance.TryGet(1, out _));                       // oldest evicted
        Assert.True(PendingHostAdvance.TryGet((ushort)(PendingHostAdvance.MaxTracked + 1), out _));
    }

    [Fact]
    public void Remove_ThenReAdd_KeepsFifoConsistent()
    {
        PendingHostAdvance.Reset();
        PendingHostAdvance.Buffer(1, "a");
        PendingHostAdvance.Buffer(2, "b");
        PendingHostAdvance.Remove(1);                       // prunes the FIFO token too
        for (ushort n = 3; n <= (ushort)(PendingHostAdvance.MaxTracked + 1); n++)
            PendingHostAdvance.Buffer(n, "x");
        Assert.Equal(PendingHostAdvance.MaxTracked, PendingHostAdvance.Count);
        Assert.True(PendingHostAdvance.TryGet(2, out _));   // still present (not spuriously evicted by a stale token)
    }

    [Fact]
    public void Reset_ClearsAll()
    {
        PendingHostAdvance.Reset();
        PendingHostAdvance.Buffer(1, "a");
        PendingHostAdvance.Buffer(2, "b");
        PendingHostAdvance.Reset();
        Assert.Equal(0, PendingHostAdvance.Count);
        Assert.False(PendingHostAdvance.TryGet(1, out _));
    }
}
