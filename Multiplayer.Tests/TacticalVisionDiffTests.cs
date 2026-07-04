using System.Collections.Generic;
using System.Linq;
using Multiplayer.Sync.Tactical;
using Xunit;

public class TacticalVisionDiffTests
{
    // State constants mirror the wire/engine mapping: 2 = Revealed (red), 1 = Located (grey).
    private const int Revealed = TacticalVisionDiff.StateRevealed;
    private const int Located = TacticalVisionDiff.StateLocated;

    private static Dictionary<int, int> Map(params (int netId, int state)[] pairs)
        => pairs.ToDictionary(p => p.netId, p => p.state);

    [Fact]
    public void AddNew_ProducesSet()
    {
        var current = Map();
        var incoming = Map((10, Revealed), (11, Located));
        var diff = TacticalVisionDiff.Compute(current, incoming);

        Assert.Equal(2, diff.ToSet.Count);
        Assert.Equal(Revealed, diff.ToSet[10]);
        Assert.Equal(Located, diff.ToSet[11]);
        Assert.Empty(diff.ToForget);
    }

    [Fact]
    public void UpgradeLocatedToRevealed_ProducesSet()
    {
        var current = Map((10, Located));
        var incoming = Map((10, Revealed));
        var diff = TacticalVisionDiff.Compute(current, incoming);

        Assert.Single(diff.ToSet);
        Assert.Equal(Revealed, diff.ToSet[10]);
        Assert.Empty(diff.ToForget);
    }

    [Fact]
    public void DowngradeRevealedToLocated_ProducesSet()
    {
        var current = Map((10, Revealed));
        var incoming = Map((10, Located));
        var diff = TacticalVisionDiff.Compute(current, incoming);

        Assert.Single(diff.ToSet);
        Assert.Equal(Located, diff.ToSet[10]);
        Assert.Empty(diff.ToForget);
    }

    [Fact]
    public void RemoveAbsent_ProducesForget()
    {
        var current = Map((10, Revealed), (11, Located));
        var incoming = Map((10, Revealed));          // 11 dropped out of sight
        var diff = TacticalVisionDiff.Compute(current, incoming);

        Assert.Empty(diff.ToSet);                    // 10 unchanged → no set
        Assert.Single(diff.ToForget);
        Assert.Contains(11, diff.ToForget);
    }

    [Fact]
    public void IdempotentSameSnapshot_ProducesNoChange()
    {
        var current = Map((10, Revealed), (11, Located));
        var incoming = Map((10, Revealed), (11, Located));
        var diff = TacticalVisionDiff.Compute(current, incoming);

        Assert.Empty(diff.ToSet);
        Assert.Empty(diff.ToForget);
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Mixed_AddUpgradeDowngradeRemove()
    {
        var current = Map((10, Revealed), (11, Located), (12, Revealed), (13, Located));
        var incoming = Map(
            (10, Located),    // downgrade
            (11, Revealed),   // upgrade
            (12, Revealed),   // unchanged
            (14, Located));   // new; 13 absent → forget
        var diff = TacticalVisionDiff.Compute(current, incoming);

        Assert.Equal(Located, diff.ToSet[10]);
        Assert.Equal(Revealed, diff.ToSet[11]);
        Assert.Equal(Located, diff.ToSet[14]);
        Assert.False(diff.ToSet.ContainsKey(12));    // unchanged not in set
        Assert.Single(diff.ToForget);
        Assert.Contains(13, diff.ToForget);
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void EmptyIncoming_ForgetsAll()
    {
        var current = Map((10, Revealed), (11, Located));
        var incoming = Map();
        var diff = TacticalVisionDiff.Compute(current, incoming);

        Assert.Empty(diff.ToSet);
        Assert.Equal(2, diff.ToForget.Count);
        Assert.Contains(10, diff.ToForget);
        Assert.Contains(11, diff.ToForget);
    }
}
