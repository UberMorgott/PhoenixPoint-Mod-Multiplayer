using Multipleer.Sync.Tactical;
using Xunit;

// Pure-logic tests for FIX E (client AP re-max). The CLIENT mirror still replays the native per-actor
// TacticalActor.StartTurn (TacticalActor.cs:1188) at turn start — it is load-bearing for selectability + the
// action HUD (RemoveUnusableAbilities / SetAbilityTraits("start") trait reset / standby), and removing it
// wholesale would wrongly disable "start"-trait-gated abilities on the client (the host does NOT stream
// AbilityTraits). BUT StartTurn → RestartAbilities → ActionPoints.SetToMax() (TacticalActor.cs:1243) re-maxes AP,
// and on a PURE MIRROR AP is host-authoritative (streamed via tac.damage ShooterApAfter + the T1 actor-state
// delta). Selectability + the lit/usable state of ability icons are LIVE functions of CURRENT AP
// (IsActorSelectable → CanAct → IsEnabled → ActionPointRequirementSatisfied, TacticalAbility.cs:146), so letting
// the re-max stick silently restores spent AP every player turn (icons stay lit). This gate pins that the mirror
// captures-then-restores AP/WP across the StartTurn replay so the re-max never sticks.
public class ClientApPreserveGateTests
{
    [Fact]
    public void Preserves_WhenClientMirroring()
    {
        // Mirroring client: AP is host-streamed → the StartTurn re-max must be reverted (capture/restore).
        Assert.True(ClientApPreserveGate.ShouldPreserveActorApWp(isClientMirroring: true));
    }

    [Fact]
    public void DoesNotPreserve_WhenNotMirroring()
    {
        // Host / single-player run the real authoritative turn-start; SetToMax is CORRECT there → never revert it.
        Assert.False(ClientApPreserveGate.ShouldPreserveActorApWp(isClientMirroring: false));
    }
}
