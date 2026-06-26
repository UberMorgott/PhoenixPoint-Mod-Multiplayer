using Multipleer.Network.Sync;
using Xunit;

public class SurfaceSeqTests
{
    [Fact]
    public void Next_IsMonotonicPerSurface_StartingAtOne()
    {
        var s = new SurfaceSeq();
        Assert.Equal(1u, s.Next(10));
        Assert.Equal(2u, s.Next(10));
        Assert.Equal(1u, s.Next(20));   // independent stream per surface
        Assert.Equal(3u, s.Next(10));
    }

    [Fact]
    public void ShouldApply_LastWriterWins()
    {
        var s = new SurfaceSeq();
        Assert.True(s.ShouldApply(10, 1u));
        s.Mark(10, 1u);
        Assert.False(s.ShouldApply(10, 1u));   // duplicate
        Assert.True(s.ShouldApply(10, 2u));     // newer
        s.Mark(10, 2u);
        Assert.False(s.ShouldApply(10, 1u));    // stale arrives late → dropped
    }

    [Fact]
    public void ShouldApply_SurfacesAreIndependent()
    {
        var s = new SurfaceSeq();
        s.Mark(10, 10u);
        Assert.True(s.ShouldApply(20, 1u));     // a fresh seq on another surface is still new
    }

    [Fact]
    public void Mark_IgnoresStale_AndResetClears()
    {
        var s = new SurfaceSeq();
        s.Mark(20, 5u);
        s.Mark(20, 3u);                         // stale → ignored
        Assert.False(s.ShouldApply(20, 5u));
        Assert.True(s.ShouldApply(20, 6u));
        s.Reset();
        Assert.True(s.ShouldApply(20, 1u));     // reset clears the client guard
        Assert.Equal(1u, s.Next(20));            // reset clears the host stream
    }
}
