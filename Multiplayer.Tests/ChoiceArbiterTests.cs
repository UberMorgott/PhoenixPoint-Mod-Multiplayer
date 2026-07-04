using Multiplayer.Network.Sync.State;
using Xunit;

// Pure host-side first-claim-wins arbitration for geoscape-event choices. The first ChoiceClaim per
// occurrence id is ACCEPTED (host runs CompleteEvent + broadcasts the outcome); every later claim for that
// occId is IGNORED (the double-claim race converges on the first winner). Unity-free → directly testable.
public class ChoiceArbiterTests
{
    [Fact]
    public void FirstClaim_IsAccepted()
    {
        var a = new ChoiceArbiter();
        Assert.True(a.Claim(occurrenceId: 1));
    }

    [Fact]
    public void SecondClaim_SameOcc_IsIgnored()
    {
        var a = new ChoiceArbiter();
        Assert.True(a.Claim(1));   // winner
        Assert.False(a.Claim(1));  // late/second claim for a resolved occId → ignored
        Assert.False(a.Claim(1));  // and again
    }

    [Fact]
    public void DistinctOccurrences_EachAcceptedOnce()
    {
        var a = new ChoiceArbiter();
        Assert.True(a.Claim(1));
        Assert.True(a.Claim(2));
        Assert.False(a.Claim(1));
        Assert.False(a.Claim(2));
    }

    [Fact]
    public void ZeroOcc_IsNeverAccepted()
    {
        // occId 0 is the "null/none" sentinel (EventOccurrenceIds) → never a valid claim.
        var a = new ChoiceArbiter();
        Assert.False(a.Claim(0));
        Assert.False(a.Claim(0));
    }

    [Fact]
    public void ResolvedSet_IsBounded()
    {
        var a = new ChoiceArbiter();
        // Accept far more than the cap; the oldest resolved ids age out so the set never leaks.
        for (int i = 1; i <= ChoiceArbiter.MaxResolvedTracked + 50; i++)
            Assert.True(a.Claim((ushort)i));
        Assert.True(a.ResolvedCount <= ChoiceArbiter.MaxResolvedTracked);
        // The OLDEST resolved id (1) aged out → a re-claim is accepted again (harmless: its event is long gone,
        // and EventOccurrenceIds never reissues a live id while it is still in flight).
        Assert.True(a.Claim(1));
        // A recent id is still resolved → still ignored.
        ushort recent = (ushort)(ChoiceArbiter.MaxResolvedTracked + 50);
        Assert.False(a.Claim(recent));
    }

    [Fact]
    public void Reset_ClearsResolved()
    {
        var a = new ChoiceArbiter();
        a.Claim(1);
        Assert.False(a.Claim(1));
        a.Reset();
        Assert.True(a.Claim(1));   // forgotten after reset
    }

    [Fact]
    public void Reset_OnSaveLoad_LetsReusedOccId_BeClaimedAgain()
    {
        // Regression: after a save-load / co-op save-transfer the engine REUSES occurrence ids. Without a
        // boundary reset, a stale resolved entry for occId N (resolved BEFORE the reload) would make the
        // legit post-reload HOST claim for the SAME occId N return false → the native grant is skipped.
        // PrepareEntryFromBlobCrt calls Reset() on that boundary; here we assert Reset() restores the claim.
        var a = new ChoiceArbiter();
        const ushort reusedOccId = 7;
        Assert.True(a.Claim(reusedOccId));    // pre-reload winner resolved this occId
        Assert.False(a.Claim(reusedOccId));   // still resolved → stale entry would block a reissue

        a.Reset();                            // save-load boundary clears the resolved set

        Assert.True(a.Claim(reusedOccId));    // post-reload reissue of the SAME occId is granted again
    }
}
