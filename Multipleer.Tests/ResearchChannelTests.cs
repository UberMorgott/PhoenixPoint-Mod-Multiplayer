using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip tests for the research state channel (#2) snapshot codec: completed research ids +
/// the ordered (id, progress) queue. Only the pure encode/decode path is exercised; Snapshot/Apply bind
/// live game types and are not unit-testable.
/// </summary>
public class ResearchChannelTests
{
    private static ResearchSnapshot RoundTrip(ResearchSnapshot snap)
    {
        var bytes = ResearchSnapshot.Encode(snap);
        return ResearchSnapshot.Decode(bytes);
    }

    [Fact]
    public void Snapshot_RoundTrips_CompletedAndQueue()
    {
        var snap = new ResearchSnapshot();
        snap.Completed.Add("PX_AlienBiology_ResearchDef");
        snap.Completed.Add("PX_LaserTech_ResearchDef");
        snap.Queue.Add(("PX_Vehicles_ResearchDef", 125.5f));
        snap.Queue.Add(("PX_Mutoid_ResearchDef", 0f));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(new[] { "PX_AlienBiology_ResearchDef", "PX_LaserTech_ResearchDef" }, rt.Completed);
        Assert.Equal(2, rt.Queue.Count);
        Assert.Equal("PX_Vehicles_ResearchDef", rt.Queue[0].id);
        Assert.Equal(125.5f, rt.Queue[0].progress);
        Assert.Equal("PX_Mutoid_ResearchDef", rt.Queue[1].id);
        Assert.Equal(0f, rt.Queue[1].progress);
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new ResearchSnapshot());
        Assert.NotNull(rt);
        Assert.Empty(rt.Completed);
        Assert.Empty(rt.Queue);
    }

    [Fact]
    public void Snapshot_PreservesQueueOrder()
    {
        var snap = new ResearchSnapshot();
        snap.Queue.Add(("a", 1f));
        snap.Queue.Add(("b", 2f));
        snap.Queue.Add(("c", 3f));

        var rt = RoundTrip(snap);

        Assert.Equal(new[] { "a", "b", "c" }, rt.Queue.ConvertAll(q => q.id));
    }

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
    {
        // Truncated: claims completedCount but no payload bytes follow.
        Assert.Null(ResearchSnapshot.Decode(new byte[] { 0xFF }));
    }

    [Fact]
    public void Encode_NullSnapshot_ReturnsNull()
    {
        Assert.Null(ResearchSnapshot.Encode(null));
    }
}
