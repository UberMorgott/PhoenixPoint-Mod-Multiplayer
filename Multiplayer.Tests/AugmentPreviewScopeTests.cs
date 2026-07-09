using Multiplayer.Network.Sync.State;
using Xunit;

// Preview-write latch (augment preview regression RCA 2026-07-09): while active, the host's
// GeoCharacter.SetItems dirty seam must NOT mark #9/#1 — the augmentation 3D preview (OnAugmentClicked)
// is a transient UI-local write. Depth-counted for re-entrancy safety with an underflow guard
// (a Harmony finalizer can fire for a method whose prefix never ran).
public class AugmentPreviewScopeTests
{
    public AugmentPreviewScopeTests() => AugmentPreviewScope.Reset();   // static latch → isolate each test

    [Fact]
    public void Inactive_ByDefault()
    {
        Assert.False(AugmentPreviewScope.Active);
    }

    [Fact]
    public void Enter_Arms_Exit_Disarms()
    {
        AugmentPreviewScope.Enter();
        Assert.True(AugmentPreviewScope.Active);
        AugmentPreviewScope.Exit();
        Assert.False(AugmentPreviewScope.Active);
    }

    [Fact]
    public void NestedEnter_StaysActive_UntilOutermostExit()
    {
        // Re-entrancy safety: an inner exit must not disarm the outer scope.
        AugmentPreviewScope.Enter();
        AugmentPreviewScope.Enter();
        AugmentPreviewScope.Exit();
        Assert.True(AugmentPreviewScope.Active);
        AugmentPreviewScope.Exit();
        Assert.False(AugmentPreviewScope.Active);
    }

    [Fact]
    public void UnmatchedExit_NeverGoesNegative()
    {
        // An unmatched finalizer must not push depth below zero and permanently disarm the seam skip.
        AugmentPreviewScope.Exit();
        Assert.False(AugmentPreviewScope.Active);
        AugmentPreviewScope.Enter();
        Assert.True(AugmentPreviewScope.Active);
        AugmentPreviewScope.Exit();
        Assert.False(AugmentPreviewScope.Active);
    }

    [Fact]
    public void Reset_DropsAnyDepth()
    {
        AugmentPreviewScope.Enter();
        AugmentPreviewScope.Enter();
        AugmentPreviewScope.Reset();
        Assert.False(AugmentPreviewScope.Active);
    }
}
