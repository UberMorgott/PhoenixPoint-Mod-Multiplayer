using Multiplayer.Network.Sync;
using Xunit;

/// <summary>
/// Pure truth tables for the CLIENT-side view-lock of a mirrored BLOCKING modal (ambush brief). The client
/// renders the same native fullscreen prompt as the host but must be inert: no local close/confirm (mission
/// start is host-authoritative; the window is not skippable) and greyed buttons. The Harmony glue
/// (BlockingModalClientLockPatches) binds game types and is exercised in-game; the decisions are pinned here.
/// </summary>
public class BlockingModalLockDecisionTests
{
    // ── ShouldBlockLocalClose: UIStateGeoModal.FinishDialog (all buttons) + OnCancel (Esc/back) prefixes ──
    [Fact]
    public void BlockClose_ClientActiveSessionBlockingModal_True()
        => Assert.True(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isBlockingModal: true));

    [Fact]
    public void BlockClose_Host_NeverBlocked()
        // Host transparency: the host's own confirm must run natively (LaunchMission → co-op deploy flow).
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: true, isActiveSession: true, isApplying: false, isBlockingModal: true));

    [Fact]
    public void BlockClose_NoActiveSession_NeverBlocked()
        // Solo / post-session play with the mod loaded: the native ambush prompt must stay fully usable.
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: false, isApplying: false, isBlockingModal: true));

    [Fact]
    public void BlockClose_EngineDrivenReplay_Passes()
        // A mirror-driven close under SyncApplyScope must never be swallowed (host-resolve release path belt).
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: true, isBlockingModal: true));

    [Fact]
    public void BlockClose_NonBlockingModal_Passes()
        // Mirrored REPORT modals (research complete etc.) stay locally dismissible — only the ambush locks.
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isBlockingModal: false));

    // ── ShouldGreyButtons: UIModuleModal.Show postfix (visual inertness) ──────────────────────────
    [Fact]
    public void Grey_ClientActiveSessionBlockingModal_True()
        => Assert.True(BlockingModalLockDecision.ShouldGreyButtons(
            isHost: false, isActiveSession: true, isBlockingModal: true));

    [Theory]
    [InlineData(true, true, true)]     // host → pristine native window (host transparency)
    [InlineData(false, false, true)]   // no session → native
    [InlineData(false, true, false)]   // non-blocking modal → native
    public void Grey_AnyOtherCombination_False(bool isHost, bool isActiveSession, bool isBlockingModal)
        => Assert.False(BlockingModalLockDecision.ShouldGreyButtons(isHost, isActiveSession, isBlockingModal));
}
