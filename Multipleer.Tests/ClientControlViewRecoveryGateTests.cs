using Multipleer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the CLIENT non-shoot-action VIEW-FREEZE recovery gate. On a mirroring client a non-shoot
// player action (plain MOVE — hence melee/charge, which begin with a move — plus OVERWATCH) is suppressed locally,
// but the native TacticalViewState.ActivateAbility had ALREADY run SwitchToState(UIStateWaiting, ClearStackAndPush)
// with the DEFAULT ClearStackAndPush (TacticalViewState.cs:289-307) — emptying the live control state off a bare
// [UIStateCharacterSelected] stack (the move confirm uses the default, UIStateCharacterSelected.cs:958) and
// wedging the HUD-less view (TacticalView.Update guards on !IsEmpty, TacticalView.cs:1051). FIRE escapes this
// because the shoot confirm uses ReplaceTop from a UIStateShoot pushed ON TOP of the control state
// (UIStateShoot.cs:1361). The fix re-establishes the control view via the native TacticalView.ResetViewState()
// AFTER the mirrored outcome applies — but ONLY when the view is actually wedged, so a host/enemy action that
// never disturbed this client's healthy view is left untouched. This gate pins that wedge-detection.
public class ClientControlViewRecoveryGateTests
{
    [Fact]
    public void Recovers_WhenMirroringAndStackEmpty()
    {
        // Empty stack (null state name) is the worst wedge — nothing ticks → must recover.
        Assert.True(ClientControlViewRecoveryGate.ShouldRecoverControlView(isClientMirroring: true, currentStateTypeName: null));
        Assert.True(ClientControlViewRecoveryGate.ShouldRecoverControlView(isClientMirroring: true, currentStateTypeName: ""));
    }

    [Fact]
    public void Recovers_WhenMirroringAndWaiting()
    {
        // Stuck in UIStateWaiting (no HUD/control) → recover.
        Assert.True(ClientControlViewRecoveryGate.ShouldRecoverControlView(isClientMirroring: true, currentStateTypeName: "UIStateWaiting"));
    }

    [Fact]
    public void Recovers_WhenMirroringAndInitial()
    {
        // Dead-spinning in UIStateInitial (the empty-stack fallback) → recover.
        Assert.True(ClientControlViewRecoveryGate.ShouldRecoverControlView(isClientMirroring: true, currentStateTypeName: "UIStateInitial"));
    }

    [Fact]
    public void DoesNotRecover_WhenViewHealthy_CharacterSelected()
    {
        // A live control state — the view is healthy (e.g. a host/enemy move that never wedged this client). Never
        // clobber it: ResetViewState here would tear down a working UIStateCharacterSelected.
        Assert.False(ClientControlViewRecoveryGate.ShouldRecoverControlView(isClientMirroring: true, currentStateTypeName: "UIStateCharacterSelected"));
    }

    [Fact]
    public void DoesNotRecover_WhenViewHealthy_Shoot()
    {
        // UIStateShoot is also a live control state (targeting) — leave it alone.
        Assert.False(ClientControlViewRecoveryGate.ShouldRecoverControlView(isClientMirroring: true, currentStateTypeName: "UIStateShoot"));
    }

    [Fact]
    public void DoesNotRecover_WhenViewHealthy_OtherFactionTurn()
    {
        // Enemy-turn presentation is a legitimate non-wedge state — never override it.
        Assert.False(ClientControlViewRecoveryGate.ShouldRecoverControlView(isClientMirroring: true, currentStateTypeName: "UIStateOtherFactionTurn"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UIStateWaiting")]
    [InlineData("UIStateInitial")]
    [InlineData("UIStateCharacterSelected")]
    public void NeverRecovers_WhenNotMirroring(string stateName)
    {
        // Host / single-player owns its own view natively — the mirror recovery must NEVER fire, regardless of the
        // view state. This is the hard off-switch (mirrors every other client-only gate in this suite).
        Assert.False(ClientControlViewRecoveryGate.ShouldRecoverControlView(isClientMirroring: false, currentStateTypeName: stateName));
    }
}
