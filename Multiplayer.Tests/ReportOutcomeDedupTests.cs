using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure guards for the Batch-2 consecutive-duplicate outcome-show filter — the interim STUN double-send
/// protection for the (still occurrence-id-less) 0x69 mission-outcome payloads. Contract: block ONLY an
/// immediate byte-identical repeat; a different payload re-arms, so a genuinely repeated outcome later in
/// the campaign is never falsely dropped.
/// </summary>
public class ReportOutcomeDedupTests
{
    private static byte[] Payload(byte marker) => new byte[] { 0x05, marker, 0x01, 0x02 };

    [Fact]
    public void FirstDelivery_Shows()
        => Assert.True(new ReportOutcomeDedup().ShouldShow(Payload(1)));

    [Fact]
    public void BackToBackDuplicate_Blocked()
    {
        var d = new ReportOutcomeDedup();
        Assert.True(d.ShouldShow(Payload(1)));
        Assert.False(d.ShouldShow(Payload(1)));   // STUN reliable double-send
        Assert.False(d.ShouldShow(Payload(1)));   // triple delivery stays blocked
    }

    [Fact]
    public void DifferentPayload_Shows()
    {
        var d = new ReportOutcomeDedup();
        Assert.True(d.ShouldShow(Payload(1)));
        Assert.True(d.ShouldShow(Payload(2)));
    }

    [Fact]
    public void SamePayloadAfterADifferentOne_ShowsAgain()
    {
        // Consecutive-only: an identical outcome AFTER another outcome is a legitimate new occurrence
        // (same site+def+result later in the campaign) — must never be falsely dropped.
        var d = new ReportOutcomeDedup();
        Assert.True(d.ShouldShow(Payload(1)));
        Assert.True(d.ShouldShow(Payload(2)));
        Assert.True(d.ShouldShow(Payload(1)));
    }

    [Fact]
    public void DuplicateDetection_IsByValue_NotReference()
    {
        var d = new ReportOutcomeDedup();
        Assert.True(d.ShouldShow(new byte[] { 1, 2, 3 }));
        Assert.False(d.ShouldShow(new byte[] { 1, 2, 3 }));   // fresh array, same bytes → duplicate
        Assert.True(d.ShouldShow(new byte[] { 1, 2, 3, 4 })); // longer → different
    }

    [Fact]
    public void NullPayload_NeverShows()
        => Assert.False(new ReportOutcomeDedup().ShouldShow(null));

    [Fact]
    public void Reset_ForgetsTheLastPayload()
    {
        // Save-transfer boundary: a post-reload re-delivery of the same bytes must show again.
        var d = new ReportOutcomeDedup();
        Assert.True(d.ShouldShow(Payload(1)));
        d.Reset();
        Assert.True(d.ShouldShow(Payload(1)));
    }
}
