using Multiplayer.Sync.Tactical;
using Xunit;

public class ClientVisionTargetGateTests
{
    // Core fix: a mirroring client must drop an enemy the host has forgotten (invis / out of host vision).
    [Fact]
    public void Mirroring_PlayerSource_EnemyTarget_HostForgot_Blocks()
        => Assert.True(ClientVisionTargetGate.ShouldBlockTarget(
            isClientMirroring: true, sourceIsPlayerFaction: true, targetIsEnemy: true, hostKnowsTarget: false));

    // Still-known enemy stays targetable — native decision untouched.
    [Fact]
    public void Mirroring_PlayerSource_EnemyTarget_HostKnows_Allows()
        => Assert.False(ClientVisionTargetGate.ShouldBlockTarget(
            isClientMirroring: true, sourceIsPlayerFaction: true, targetIsEnemy: true, hostKnowsTarget: true));

    // Host / single-player path is never gated (the mirror is the only authority that forgets).
    [Fact]
    public void NotMirroring_NeverBlocks()
        => Assert.False(ClientVisionTargetGate.ShouldBlockTarget(
            isClientMirroring: false, sourceIsPlayerFaction: true, targetIsEnemy: true, hostKnowsTarget: false));

    // Friendly/own targets (heal, buff) are unaffected — own actors are not tracked in enemy KnownActors.
    [Fact]
    public void FriendlyTarget_NeverBlocks()
        => Assert.False(ClientVisionTargetGate.ShouldBlockTarget(
            isClientMirroring: true, sourceIsPlayerFaction: true, targetIsEnemy: false, hostKnowsTarget: false));

    // Non-player source (e.g. mirrored enemy-turn target evaluation) is untouched — only the mirrored
    // player-faction vision is authoritative on the client.
    [Fact]
    public void NonPlayerSource_NeverBlocks()
        => Assert.False(ClientVisionTargetGate.ShouldBlockTarget(
            isClientMirroring: true, sourceIsPlayerFaction: false, targetIsEnemy: true, hostKnowsTarget: false));
}
