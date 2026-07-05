using Multiplayer.Sync.Tactical;
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

    // ─── tac.fire.start (0x90) ────────────────────────────────────────
    [Fact]
    public void FireStart_RoundTrips_ActorTarget()
    {
        // Representative shoot against an actor target: non-round target coords catch f32 width/order bugs.
        var bytes = TacticalLiveCodec.EncodeFireStart(seq: 17u, shooterNetId: 12,
            abilityDefGuid: "ability-shoot-guid", targetNetId: 99,
            tx: 1.5f, ty: -2.25f, tz: 37.5f, shotCount: 3);
        Assert.True(TacticalLiveCodec.TryDecodeFireStart(bytes, out var s));
        Assert.Equal(17u, s.Seq);
        Assert.Equal(12, s.ShooterNetId);
        Assert.Equal("ability-shoot-guid", s.AbilityDefGuid);
        Assert.Equal(99, s.TargetNetId);
        Assert.Equal(1.5f, s.TX);
        Assert.Equal(-2.25f, s.TY);
        Assert.Equal(37.5f, s.TZ);
        Assert.Equal(3, s.ShotCount);
    }

    [Fact]
    public void FireStart_RoundTrips_PositionTarget_NoActor()
    {
        // Bare-position target (e.g. grenade at a point): TargetNetId carries the -1 sentinel.
        var bytes = TacticalLiveCodec.EncodeFireStart(seq: 1u, shooterNetId: 4,
            abilityDefGuid: "ability-grenade-guid", targetNetId: -1,
            tx: 0f, ty: 0f, tz: 0f, shotCount: 1);
        Assert.True(TacticalLiveCodec.TryDecodeFireStart(bytes, out var s));
        Assert.Equal(-1, s.TargetNetId);
        Assert.Equal("ability-grenade-guid", s.AbilityDefGuid);
        Assert.Equal(1, s.ShotCount);
    }

    [Fact]
    public void FireStart_RoundTrips_EmptyGuid()
    {
        var bytes = TacticalLiveCodec.EncodeFireStart(2u, 1, null, -1, 0f, 0f, 0f, 0);
        Assert.True(TacticalLiveCodec.TryDecodeFireStart(bytes, out var s));
        Assert.Equal("", s.AbilityDefGuid);
    }

    [Fact]
    public void FireStart_RejectsTruncated()
    {
        // Below the documented minimum frame → safe false, never throws.
        Assert.False(TacticalLiveCodec.TryDecodeFireStart(new byte[] { 1, 2, 3 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeFireStart(null, out _));
    }

    [Fact]
    public void FireStart_RejectsChoppedMidField()
    {
        // A well-formed frame cut short mid-field (here: dropping the trailing shotCount i32) must
        // not partially-accept — TryDecode returns false rather than throwing past the buffer end.
        var bytes = TacticalLiveCodec.EncodeFireStart(5u, 8, "g", 2, 1f, 2f, 3f, 4);
        var chopped = new byte[bytes.Length - 2];
        System.Array.Copy(bytes, chopped, chopped.Length);
        Assert.False(TacticalLiveCodec.TryDecodeFireStart(chopped, out _));
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
    public void HostSeq_SurvivesDeployCapture_NeverReEmitsForSurface()
    {
        // Regression (RCA 2026-06-20): the host deploy capture used to recreate the seq stream
        // (`LiveSeq = new TacticalLiveSeq()`) AFTER the pre-deploy tac.turn (seq=1) had already been
        // emitted, rewinding _hostNext[TacTurn] to 0. The next turn then re-emitted seq=1, and the
        // client's strict `seq > last` guard silently dropped it → "turn doesn't end". The capture-time
        // seq hook (BeginDeployCaptureMission) MUST preserve the host stream so it stays monotonic.
        var s = new TacticalLiveSeq();
        uint preDeploy = s.Next(TacticalSurfaceIds.TacTurn);   // pre-deploy tac.turn → 1
        s.BeginDeployCaptureMission();                          // host deploy capture (must NOT rewind)
        uint postDeploy = s.Next(TacticalSurfaceIds.TacTurn);  // post-deploy tac.turn
        Assert.Equal(1u, preDeploy);
        Assert.True(postDeploy > preDeploy,
            $"host seq rewound across deploy capture: pre={preDeploy} post={postDeploy} " +
            "→ client drops the post-deploy turn via its strict seq>last guard");
        Assert.Equal(2u, postDeploy);
        // The move stream is independent and equally unaffected by the capture.
        Assert.Equal(1u, s.Next(TacticalSurfaceIds.TacMove));
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
        Assert.True(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, 1u));
        Assert.False(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, 1u));   // reliable-transport double-send
        Assert.True(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, 2u));
    }

    [Fact]
    public void IsNew_SurfaceNamespaced()
    {
        var d = new TacticalIntentDedup();
        Assert.True(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, 1u));
        // Same nonce on a different intent surface is a distinct event.
        Assert.True(d.IsNew(7UL, TacticalSurfaceIds.TacIntentEndTurn, 1u));
    }

    [Fact]
    public void IsNew_PeerNamespaced_TwoClientsSameNonceBothAccepted()
    {
        // 3+ player regression: each client's _nonceCounter starts at 1, so client A and client B both
        // send (TacIntentMove, nonce=1). Peer-less keying dropped client B's move as a "duplicate".
        var d = new TacticalIntentDedup();
        Assert.True(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, 1u));    // client A
        Assert.True(d.IsNew(8UL, TacticalSurfaceIds.TacIntentMove, 1u));    // client B, same surface+nonce
        Assert.False(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, 1u));   // A's double-send → dropped
        Assert.False(d.IsNew(8UL, TacticalSurfaceIds.TacIntentMove, 1u));   // B's double-send → dropped
    }

    [Fact]
    public void IsNew_EvictsOldestPastCapacity()
    {
        var d = new TacticalIntentDedup(capacity: 16);
        for (uint n = 1; n <= 16; n++) Assert.True(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, n));
        // Overflow evicts nonce 1.
        Assert.True(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, 17u));
        Assert.True(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, 1u));    // 1 was evicted → seen as new again
        // A recent one is still deduped.
        Assert.False(d.IsNew(7UL, TacticalSurfaceIds.TacIntentMove, 17u));
    }
}
