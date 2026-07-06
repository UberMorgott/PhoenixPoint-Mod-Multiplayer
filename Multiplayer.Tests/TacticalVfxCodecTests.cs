using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for TS7 AoE/explosion VFX replay (<c>tac.vfx</c> 0x98):
///   (a) the wire codec round-trip (resolved source actor AND the -1 unresolved sentinel) + clean truncation drop,
///   (b) the HOST broadcast GATE (<see cref="TacticalVfxGate.ShouldBroadcastVfx"/>): broadcast a REAL application,
///       NEVER a simulation/prediction pass (SpawnObject early-returns without drawing on simulation).
/// </summary>
public class TacticalVfxCodecTests
{
    // ─── (a) codec round-trip ─────────────────────────────────────────
    [Fact]
    public void Vfx_RoundTrips_WithSourceActor()
    {
        var bytes = TacticalLiveCodec.EncodeVfx(
            seq: 11u, vfxDefGuid: "explosion-effect-def-guid", x: 4.5f, y: 0.25f, z: -8f, actorNetId: 55);
        Assert.True(TacticalLiveCodec.TryDecodeVfx(bytes, out var e));
        Assert.Equal(11u, e.Seq);
        Assert.Equal("explosion-effect-def-guid", e.VfxDefGuid);
        Assert.Equal(4.5f, e.X);
        Assert.Equal(0.25f, e.Y);
        Assert.Equal(-8f, e.Z);
        Assert.Equal(55, e.ActorNetId);
    }

    [Fact]
    public void Vfx_RoundTrips_UnresolvedSourceSentinel()
    {
        var bytes = TacticalLiveCodec.EncodeVfx(
            seq: 1u, vfxDefGuid: "g", x: 0f, y: 0f, z: 0f, actorNetId: TacticalLiveCodec.TargetNetIdNone);
        Assert.True(TacticalLiveCodec.TryDecodeVfx(bytes, out var e));
        Assert.Equal(TacticalLiveCodec.TargetNetIdNone, e.ActorNetId);
    }

    [Fact]
    public void Vfx_Truncated_CleanDrop()
    {
        Assert.False(TacticalLiveCodec.TryDecodeVfx(new byte[10], out _));   // < seq+string+3f+netId minimum
        Assert.False(TacticalLiveCodec.TryDecodeVfx(null, out _));
    }

    // ─── (b) host broadcast gate — real application only ──────────────
    [Fact]
    public void Gate_RealApplication_Broadcasts()
        => Assert.True(TacticalVfxGate.ShouldBroadcastVfx(isSimulation: false));

    [Fact]
    public void Gate_SimulationPass_DoesNotBroadcast()
        => Assert.False(TacticalVfxGate.ShouldBroadcastVfx(isSimulation: true));
}
