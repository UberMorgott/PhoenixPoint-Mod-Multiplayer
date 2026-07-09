using Multiplayer.Network.Sync.State;
using Xunit;

// Augment-screen repaint scoping (augment preview regression RCA 2026-07-09): the mirror repaint resets
// the module's cached baseline + clears the pending preview — correct ONLY for an apply that stamped the
// character shown on the screen. Unrelated applies must keep the user's local preview transaction alive.
public class AugmentRepaintDecisionTests
{
    [Fact]
    public void NullStampedIds_UnknownSource_RepaintsConservatively()
    {
        Assert.True(AugmentRepaintDecision.ShouldRepaint(42, null));
    }

    [Fact]
    public void EmptyStampedIds_NothingStamped_KeepsLocalPreview()
    {
        Assert.False(AugmentRepaintDecision.ShouldRepaint(42, new long[0]));
    }

    [Fact]
    public void SameCharacterStamped_Repaints()
    {
        Assert.True(AugmentRepaintDecision.ShouldRepaint(42, new long[] { 7, 42, 9 }));
    }

    [Fact]
    public void OtherCharactersStamped_KeepsLocalPreview()
    {
        // The hourly bulk sweep / another soldier's edit must never eat the open screen's preview.
        Assert.False(AugmentRepaintDecision.ShouldRepaint(42, new long[] { 7, 9 }));
    }

    [Fact]
    public void UnresolvedOpenId_RepaintsWhenAnythingStamped()
    {
        // Id read miss (0): a missed repaint = stale mirror forever, an extra one only costs a preview.
        Assert.True(AugmentRepaintDecision.ShouldRepaint(0, new long[] { 7 }));
        Assert.False(AugmentRepaintDecision.ShouldRepaint(0, new long[0]));
    }
}
