using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for TS7 enemy-turn camera follow (<c>tac.camerahint</c> 0x97):
///   (a) the wire codec round-trip + clean truncation drop,
///   (b) the HOST broadcast GATE (<see cref="ClientEnemyTurnCameraGate.ShouldBroadcastEnemyCameraHint"/>):
///       broadcast ONLY when the native camera would track AND the actor is an ENEMY AND VISIBLE ("no fog
///       reveals") — any one false → no broadcast.
/// </summary>
public class TacticalCameraHintCodecTests
{
    // ─── (a) codec round-trip ─────────────────────────────────────────
    [Fact]
    public void CameraHint_RoundTrips()
    {
        var bytes = TacticalLiveCodec.EncodeCameraHint(seq: 7u, actorNetId: 1_000_042);
        Assert.True(TacticalLiveCodec.TryDecodeCameraHint(bytes, out var h));
        Assert.Equal(7u, h.Seq);
        Assert.Equal(1_000_042, h.ActorNetId);
    }

    [Fact]
    public void CameraHint_Truncated_CleanDrop()
    {
        Assert.False(TacticalLiveCodec.TryDecodeCameraHint(new byte[7], out _));   // < seq(4)+netId(4)
        Assert.False(TacticalLiveCodec.TryDecodeCameraHint(null, out _));
    }

    // ─── (b) host broadcast gate — visible-only enemy ─────────────────
    [Fact]
    public void Gate_VisibleTrackedEnemy_Broadcasts()
        => Assert.True(ClientEnemyTurnCameraGate.ShouldBroadcastEnemyCameraHint(
            trackWithCamera: true, actorIsPlayerFaction: false, actorVisibleToPlayer: true));

    [Fact]
    public void Gate_NotTracked_DoesNotBroadcast()
        => Assert.False(ClientEnemyTurnCameraGate.ShouldBroadcastEnemyCameraHint(
            trackWithCamera: false, actorIsPlayerFaction: false, actorVisibleToPlayer: true));

    [Fact]
    public void Gate_PlayerFactionActor_DoesNotBroadcast()
        => Assert.False(ClientEnemyTurnCameraGate.ShouldBroadcastEnemyCameraHint(
            trackWithCamera: true, actorIsPlayerFaction: true, actorVisibleToPlayer: true));

    [Fact]
    public void Gate_InvisibleActor_DoesNotBroadcast_NoFogReveal()
        => Assert.False(ClientEnemyTurnCameraGate.ShouldBroadcastEnemyCameraHint(
            trackWithCamera: true, actorIsPlayerFaction: false, actorVisibleToPlayer: false));
}
