using System.Linq;
using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// Spawn-funnel coverage PIN (scripted mission events part 2, tactical audit D11/D12/D26).
///
/// The 2026-07-07 decompile audit verified that EVERY native mid-battle spawn path reaches the single
/// 0x92 chokepoint <c>TacticalLevelController.ActorEnteredPlay</c> (called unconditionally from
/// <c>TacticalActorBase.FinalizeEnterPlay</c>, TacticalActorBase.cs:550; <c>ActorSpawner.SpawnActor</c>
/// defaults <c>callEnterPlayOnActor:true</c>, and the one tactical <c>false</c> caller —
/// StructuralTargetDeployment — calls <c>DoEnterPlay()</c> explicitly). The audited source list lives in
/// <see cref="TacticalActorLifecycleGate.AuditedSpawnFunnelSources"/> next to the gate that decides the
/// broadcast; these pins make a silent edit of that contract trip review, and re-assert the gate's funnel
/// behavior for the scripted-spawn scenario every audited source produces.
/// </summary>
public class TacticalSpawnFunnelPinTests
{
    // The exact audited set (file:line evidence in the gate's doc comment). Adding a source is fine —
    // UPDATE THIS PIN with the new audit evidence; removing/renaming one must be a conscious decision.
    private static readonly string[] PinnedSources =
    {
        "SpawnActorAbility",
        "MorphIntoActorAbility",
        "MassHatchAbility",
        "CallReinforcementsAbility",
        "DeathBelcherAbility",
        "ResurrectAbility",
        "SpawnChildActorStatus",
        "EnterPlayAbility",
        "SpawnActorEffect",
        "OffMapActorDeployment",
        "StructuralTargetDeployment",
        "HulkDieAbility",
        "TacticalItem",
        "UIStateInventory",
    };

    [Fact]
    public void AuditedSpawnFunnelSources_ExactPin()
    {
        Assert.Equal(PinnedSources, TacticalActorLifecycleGate.AuditedSpawnFunnelSources);
    }

    [Fact]
    public void AuditedSpawnFunnelSources_CoverEveryAuditD11Name()
    {
        // The tactical audit's D11 list verbatim — the USER-mandated coverage floor.
        string[] auditD11 =
        {
            "SpawnActorAbility", "CallReinforcementsAbility", "MassHatchAbility", "DeathBelcherAbility",
            "ResurrectAbility", "SpawnChildActorStatus", "EnterPlayAbility", "MorphIntoActorAbility",
        };
        foreach (var name in auditD11)
            Assert.Contains(name, TacticalActorLifecycleGate.AuditedSpawnFunnelSources);
    }

    [Fact]
    public void EveryAuditedSource_MapsToTheChokepointBroadcast()
    {
        // Every audited spawn source produces the same chokepoint scenario: a POST-DEPLOY enter-play of a
        // not-yet-registered actor, outside a remote apply → the gate must broadcast. One scenario per source
        // (the gate is source-agnostic BY DESIGN — that is the funnel guarantee).
        foreach (var source in TacticalActorLifecycleGate.AuditedSpawnFunnelSources)
        {
            Assert.True(
                TacticalActorLifecycleGate.ShouldBroadcastSpawn(
                    deployCaptured: true, alreadyRegistered: false, applyingRemote: false),
                source + " must funnel into the 0x92 spawn broadcast");
        }

        // And the three suppressions stay intact (deploy snapshot / double-emit / client materialize echo).
        Assert.False(TacticalActorLifecycleGate.ShouldBroadcastSpawn(false, false, false));
        Assert.False(TacticalActorLifecycleGate.ShouldBroadcastSpawn(true, true, false));
        Assert.False(TacticalActorLifecycleGate.ShouldBroadcastSpawn(true, false, true));
    }

    [Fact]
    public void AuditedSources_AreDistinct_AndNonEmpty()
    {
        var s = TacticalActorLifecycleGate.AuditedSpawnFunnelSources;
        Assert.NotEmpty(s);
        Assert.Equal(s.Length, s.Distinct().Count());
        Assert.All(s, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }
}
