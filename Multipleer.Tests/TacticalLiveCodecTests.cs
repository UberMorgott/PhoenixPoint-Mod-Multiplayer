using Multipleer.Sync.Tactical;
using Xunit;

public class TacticalLiveCodecTests
{
    // ─── tac.intent.move ──────────────────────────────────────────────
    [Fact]
    public void MoveIntent_RoundTrips()
    {
        var bytes = TacticalLiveCodec.EncodeMoveIntent(42, 1.5f, -2.25f, 3f, nonce: 7u);
        Assert.True(TacticalLiveCodec.TryDecodeMoveIntent(bytes, out var i));
        Assert.Equal(42, i.NetId);
        Assert.Equal(1.5f, i.X);
        Assert.Equal(-2.25f, i.Y);
        Assert.Equal(3f, i.Z);
        Assert.Equal(7u, i.Nonce);
    }

    [Fact]
    public void MoveIntent_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeMoveIntent(new byte[] { 1, 2, 3 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeMoveIntent(null, out _));
    }

    // ─── tac.move ─────────────────────────────────────────────────────
    [Fact]
    public void Move_RoundTrips()
    {
        var bytes = TacticalLiveCodec.EncodeMove(seq: 9u, netId: 100, x: 10f, y: 0f, z: -7.5f, stopReason: 3);
        Assert.True(TacticalLiveCodec.TryDecodeMove(bytes, out var m));
        Assert.Equal(9u, m.Seq);
        Assert.Equal(100, m.NetId);
        Assert.Equal(10f, m.X);
        Assert.Equal(0f, m.Y);
        Assert.Equal(-7.5f, m.Z);
        Assert.Equal(3, m.StopReason);
    }

    [Fact]
    public void Move_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeMove(new byte[] { 0, 0, 0 }, out _));
    }

    // ─── tac.intent.endturn ───────────────────────────────────────────
    [Fact]
    public void EndTurnIntent_RoundTrips()
    {
        var bytes = TacticalLiveCodec.EncodeEndTurnIntent(nonce: 1234u);
        Assert.True(TacticalLiveCodec.TryDecodeEndTurnIntent(bytes, out var nonce));
        Assert.Equal(1234u, nonce);
    }

    [Fact]
    public void EndTurnIntent_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeEndTurnIntent(new byte[] { 1 }, out _));
    }

    // ─── tac.turn ─────────────────────────────────────────────────────
    [Fact]
    public void Turn_RoundTrips()
    {
        var bytes = TacticalLiveCodec.EncodeTurn(seq: 5u, currentFactionIndex: 2, turnNumber: 4,
            factionDefGuid: "abc-123-guid");
        Assert.True(TacticalLiveCodec.TryDecodeTurn(bytes, out var t));
        Assert.Equal(5u, t.Seq);
        Assert.Equal(2, t.CurrentFactionIndex);
        Assert.Equal(4, t.TurnNumber);
        Assert.Equal("abc-123-guid", t.FactionDefGuid);
    }

    [Fact]
    public void Turn_RoundTrips_EmptyGuid()
    {
        var bytes = TacticalLiveCodec.EncodeTurn(1u, 0, 0, null);
        Assert.True(TacticalLiveCodec.TryDecodeTurn(bytes, out var t));
        Assert.Equal("", t.FactionDefGuid);
    }
}

public class TacticalLiveSeqTests
{
    [Fact]
    public void Next_IsMonotonicPerSurface_StartingAtOne()
    {
        var s = new TacticalLiveSeq();
        Assert.Equal(1u, s.Next(TacticalSurfaceIds.TacMove));
        Assert.Equal(2u, s.Next(TacticalSurfaceIds.TacMove));
        // Independent stream per surface.
        Assert.Equal(1u, s.Next(TacticalSurfaceIds.TacTurn));
        Assert.Equal(3u, s.Next(TacticalSurfaceIds.TacMove));
    }

    [Fact]
    public void ShouldApply_LastWriterWins()
    {
        var s = new TacticalLiveSeq();
        Assert.True(s.ShouldApply(TacticalSurfaceIds.TacMove, 1u));
        s.Mark(TacticalSurfaceIds.TacMove, 1u);
        Assert.False(s.ShouldApply(TacticalSurfaceIds.TacMove, 1u));   // duplicate
        Assert.True(s.ShouldApply(TacticalSurfaceIds.TacMove, 2u));    // newer
        s.Mark(TacticalSurfaceIds.TacMove, 2u);
        Assert.False(s.ShouldApply(TacticalSurfaceIds.TacMove, 1u));   // stale arrives late → dropped
    }

    [Fact]
    public void ShouldApply_SurfacesAreIndependent()
    {
        var s = new TacticalLiveSeq();
        s.Mark(TacticalSurfaceIds.TacMove, 10u);
        // A turn seq of 1 is still fresh even though a move seq of 10 was applied.
        Assert.True(s.ShouldApply(TacticalSurfaceIds.TacTurn, 1u));
    }

    [Fact]
    public void Mark_IgnoresStale()
    {
        var s = new TacticalLiveSeq();
        s.Mark(TacticalSurfaceIds.TacTurn, 5u);
        s.Mark(TacticalSurfaceIds.TacTurn, 3u);   // stale → ignored
        Assert.False(s.ShouldApply(TacticalSurfaceIds.TacTurn, 5u));
        Assert.True(s.ShouldApply(TacticalSurfaceIds.TacTurn, 6u));
    }
}

public class TacticalIntentDedupTests
{
    [Fact]
    public void IsNew_FirstTrueRepeatFalse()
    {
        var d = new TacticalIntentDedup();
        Assert.True(d.IsNew(TacticalSurfaceIds.TacIntentMove, 1u));
        Assert.False(d.IsNew(TacticalSurfaceIds.TacIntentMove, 1u));   // reliable-transport double-send
        Assert.True(d.IsNew(TacticalSurfaceIds.TacIntentMove, 2u));
    }

    [Fact]
    public void IsNew_SurfaceNamespaced()
    {
        var d = new TacticalIntentDedup();
        Assert.True(d.IsNew(TacticalSurfaceIds.TacIntentMove, 1u));
        // Same nonce on a different intent surface is a distinct event.
        Assert.True(d.IsNew(TacticalSurfaceIds.TacIntentEndTurn, 1u));
    }

    [Fact]
    public void IsNew_EvictsOldestPastCapacity()
    {
        var d = new TacticalIntentDedup(capacity: 16);
        for (uint n = 1; n <= 16; n++) Assert.True(d.IsNew(TacticalSurfaceIds.TacIntentMove, n));
        // Overflow evicts nonce 1.
        Assert.True(d.IsNew(TacticalSurfaceIds.TacIntentMove, 17u));
        Assert.True(d.IsNew(TacticalSurfaceIds.TacIntentMove, 1u));    // 1 was evicted → seen as new again
        // A recent one is still deduped.
        Assert.False(d.IsNew(TacticalSurfaceIds.TacIntentMove, 17u));
    }
}
