using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure guards for the destSite → initiator-peer tag that routes an arrival mission brief to the peer who
/// ordered the travel (co-op brief-on-all fix). Static state, so each test resets first.
/// </summary>
public class VehicleTravelInitiatorTests
{
    public VehicleTravelInitiatorTests() => VehicleTravelInitiator.Reset();

    [Fact]
    public void RecordedSite_ConsumesOnceToThatPeer()
    {
        VehicleTravelInitiator.Record(42, 7UL);

        Assert.True(VehicleTravelInitiator.TryConsume(42, out ulong peer));
        Assert.Equal(7UL, peer);

        // One brief per arrival — the second consume of the same site finds nothing (→ caller broadcasts to all).
        Assert.False(VehicleTravelInitiator.TryConsume(42, out _));
    }

    [Fact]
    public void UntaggedSite_IsAWorldEvent_TryConsumeFalse()
    {
        VehicleTravelInitiator.Record(42, 7UL);
        Assert.False(VehicleTravelInitiator.TryConsume(99, out _));   // no travel targeted 99 → broadcast-to-all
    }

    [Fact]
    public void HostSelf_IsDistinctFromAnyRealPeer()
    {
        VehicleTravelInitiator.Record(5, VehicleTravelInitiator.HostSelf);
        Assert.True(VehicleTravelInitiator.TryConsume(5, out ulong peer));
        Assert.Equal(VehicleTravelInitiator.HostSelf, peer);
    }

    [Fact]
    public void LatestOrderForASiteWins()
    {
        VehicleTravelInitiator.Record(3, 1UL);
        VehicleTravelInitiator.Record(3, 2UL);   // client 2 re-targets the same site
        Assert.True(VehicleTravelInitiator.TryConsume(3, out ulong peer));
        Assert.Equal(2UL, peer);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    public void InvalidSiteId_NeverTagged(int badSiteId)
    {
        VehicleTravelInitiator.Record(badSiteId, 7UL);
        Assert.False(VehicleTravelInitiator.TryConsume(badSiteId, out _));
    }

    [Fact]
    public void Reset_DropsAllTags()
    {
        VehicleTravelInitiator.Record(1, 1UL);
        VehicleTravelInitiator.Record(2, 2UL);
        VehicleTravelInitiator.Reset();
        Assert.False(VehicleTravelInitiator.TryConsume(1, out _));
        Assert.False(VehicleTravelInitiator.TryConsume(2, out _));
    }
}
