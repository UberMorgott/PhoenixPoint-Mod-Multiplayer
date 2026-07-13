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

    // ─── (a2) rca-grenade-vfx: optional srcDefGuid tail ────────────────
    [Fact]
    public void Vfx_RoundTrips_WithSourceWeaponDefGuid()
    {
        var bytes = TacticalLiveCodec.EncodeVfx(
            seq: 7u, vfxDefGuid: "explosion-def", x: 1f, y: 2f, z: 3f, actorNetId: 3,
            srcDefGuid: "hand-grenade-weapon-def-guid");
        Assert.True(TacticalLiveCodec.TryDecodeVfx(bytes, out var e));
        Assert.Equal("hand-grenade-weapon-def-guid", e.SrcDefGuid);
        Assert.Equal("explosion-def", e.VfxDefGuid);
        Assert.Equal(3, e.ActorNetId);
    }

    [Fact]
    public void Vfx_OldPeerPayload_NoTail_DecodesWithEmptySrcGuid()
    {
        // An OLD peer's payload ends at actorNetId. Simulate it by stripping the empty-string tail
        // (a single 0x00 length byte) the new encoder appends for srcDefGuid:"".
        var withEmptyTail = TacticalLiveCodec.EncodeVfx(
            seq: 2u, vfxDefGuid: "g", x: 0f, y: 0f, z: 0f, actorNetId: -1, srcDefGuid: "");
        var oldShape = new byte[withEmptyTail.Length - 1];
        System.Array.Copy(withEmptyTail, oldShape, oldShape.Length);

        Assert.True(TacticalLiveCodec.TryDecodeVfx(oldShape, out var e));
        Assert.Equal("", e.SrcDefGuid);   // missing tail → "" → client falls back to def.ObjectToSpawn
        Assert.Equal("g", e.VfxDefGuid);
    }

    // ─── (b) host broadcast gate — real application only ──────────────
    [Fact]
    public void Gate_RealApplication_Broadcasts()
        => Assert.True(TacticalVfxGate.ShouldBroadcastVfx(isSimulation: false));

    [Fact]
    public void Gate_SimulationPass_DoesNotBroadcast()
        => Assert.False(TacticalVfxGate.ShouldBroadcastVfx(isSimulation: true));
}
