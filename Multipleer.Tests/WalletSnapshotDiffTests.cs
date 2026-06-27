using System.Collections.Generic;
using Multipleer.Network.Sync;
using Xunit;

// Pure absolute snapshot comparator behind the SyncEngine.Tick wallet snapshot-diff POLL (the
// binding-independent currency convergence backstop). The event-driven WalletWatcher binding has bitten
// us before (stale-bound instance / missed ResourcesChanged → _walletDirty never set → client stale);
// the poll re-derives "did the live wallet drift from the last broadcast snapshot?" from absolute truth.
// Mirrors the eps + 11-slot shape of WalletApplier.Snapshot.
public class WalletSnapshotDiffTests
{
    private static List<(int type, float value)> Snap(params (int, float)[] slots)
        => new List<(int type, float value)>(slots.Length).Also(slots);

    [Fact]
    public void NullLast_Changed()
    {
        // Never broadcast yet → the poll must seed the first snapshot.
        Assert.True(WalletSnapshotDiff.Changed(null, Snap((1, 100f), (2, 50f))));
    }

    [Fact]
    public void Identical_NotChanged()
    {
        var last = Snap((1, 100f), (2, 50f), (4, 7f));
        var current = Snap((1, 100f), (2, 50f), (4, 7f));
        Assert.False(WalletSnapshotDiff.Changed(last, current));
    }

    [Fact]
    public void OneSlotDeltaAboveEps_Changed()
    {
        var last = Snap((1, 100f), (2, 50f));
        var current = Snap((1, 100f), (2, 51f)); // slot 2 drifted by 1.0 > eps
        Assert.True(WalletSnapshotDiff.Changed(last, current));
    }

    [Fact]
    public void DeltaBelowEps_NotChanged()
    {
        var last = Snap((1, 100f), (2, 50f));
        var current = Snap((1, 100.00001f), (2, 50f)); // sub-eps float jitter → not a change
        Assert.False(WalletSnapshotDiff.Changed(last, current));
    }

    [Fact]
    public void DifferingLength_Changed()
    {
        var last = Snap((1, 100f), (2, 50f));
        var current = Snap((1, 100f), (2, 50f), (4, 7f)); // a slot appeared → changed
        Assert.True(WalletSnapshotDiff.Changed(last, current));
    }

    [Fact]
    public void DifferingTypeSet_Changed()
    {
        var last = Snap((1, 100f), (2, 50f));
        var current = Snap((1, 100f), (8, 50f)); // same length, type 2 → type 8: type-set differs
        Assert.True(WalletSnapshotDiff.Changed(last, current));
    }
}

internal static class SnapBuilderExtensions
{
    public static List<(int type, float value)> Also(
        this List<(int type, float value)> list, (int, float)[] slots)
    {
        foreach (var (t, v) in slots) list.Add((t, v));
        return list;
    }
}
