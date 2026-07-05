using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure truth tables for the CLIENT-side view-lock of a mirrored BLOCKING modal (ambush / site-mission briefs).
/// The client renders the same native fullscreen prompt as the host but must be inert: no local close/confirm
/// (mission start is host-authoritative; the window is not skippable) and greyed buttons. ORIGIN CONTRACT
/// (2026-07-05): the lock applies ONLY to a MIRROR-shown window (<see cref="BlockingModalMirrorRegistry"/>) —
/// a client-native blocking-type window keeps native buttons (Cancel closes locally), and a mirror window whose
/// ReportModalHide landed before it entered (queued-show race) comes up unlocked. The Harmony glue
/// (BlockingModalClientLockPatches) binds game types and is exercised in-game; the decisions are pinned here.
/// </summary>
public class BlockingModalLockDecisionTests
{
    public BlockingModalLockDecisionTests() => BlockingModalMirrorRegistry.Reset();

    // ── ShouldBlockLocalClose: UIStateGeoModal.FinishDialog (all buttons) + OnCancel (Esc/back) prefixes ──
    [Fact]
    public void BlockClose_ClientActiveSessionMirrorShownBlockingModal_True()
        => Assert.True(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isBlockingModal: true, isMirrorShown: true));

    [Fact]
    public void BlockClose_Host_NeverBlocked()
        // Host transparency: the host's own confirm must run natively (LaunchMission → co-op deploy flow).
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: true, isActiveSession: true, isApplying: false, isBlockingModal: true, isMirrorShown: true));

    [Fact]
    public void BlockClose_NoActiveSession_NeverBlocked()
        // Solo / post-session play with the mod loaded: the native ambush prompt must stay fully usable.
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: false, isApplying: false, isBlockingModal: true, isMirrorShown: true));

    [Fact]
    public void BlockClose_EngineDrivenReplay_Passes()
        // A mirror-driven close under SyncApplyScope must never be swallowed (host-resolve release path belt).
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: true, isBlockingModal: true, isMirrorShown: true));

    [Fact]
    public void BlockClose_NonBlockingModal_Passes()
        // Mirrored REPORT modals (research complete etc.) stay locally dismissible — only blocking briefs lock.
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isBlockingModal: false, isMirrorShown: true));

    [Fact]
    public void BlockClose_NativeOriginWindow_NeverBlocked()
        // THE 2026-07-05 dead-Cancel bug: a blocking-TYPE window the mirror did NOT show (client-native opener /
        // hide already landed) must keep its native close — no host hide would ever release it.
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isBlockingModal: true, isMirrorShown: false));

    // ── ShouldGreyButtons: UIModuleModal.Show postfix (visual inertness) ──────────────────────────
    [Fact]
    public void Grey_ClientActiveSessionMirrorShownBlockingModal_True()
        => Assert.True(BlockingModalLockDecision.ShouldGreyButtons(
            isHost: false, isActiveSession: true, isBlockingModal: true, isMirrorShown: true));

    [Theory]
    [InlineData(true, true, true, true)]     // host → pristine native window (host transparency)
    [InlineData(false, false, true, true)]   // no session → native
    [InlineData(false, true, false, true)]   // non-blocking modal → native
    [InlineData(false, true, true, false)]   // NOT mirror-shown (native origin / hide landed first) → native
    public void Grey_AnyOtherCombination_False(bool isHost, bool isActiveSession, bool isBlockingModal, bool isMirrorShown)
        => Assert.False(BlockingModalLockDecision.ShouldGreyButtons(isHost, isActiveSession, isBlockingModal, isMirrorShown));

    // ── BlockingModalMirrorRegistry: the origin tag driving isMirrorShown ─────────────────────────
    [Fact]
    public void Registry_MarkThenClear_TagLifecycle()
    {
        Assert.False(BlockingModalMirrorRegistry.IsMirrorShown(4));
        BlockingModalMirrorRegistry.MarkMirrorShown(4);
        Assert.True(BlockingModalMirrorRegistry.IsMirrorShown(4));
        Assert.False(BlockingModalMirrorRegistry.IsMirrorShown(15));   // per-type isolation
        BlockingModalMirrorRegistry.ClearMirrorShown(4);
        Assert.False(BlockingModalMirrorRegistry.IsMirrorShown(4));
    }

    [Fact]
    public void Registry_HideBeforeShowRace_WindowEntersUnlocked()
    {
        // Mirrored Show queues + tags → fast host cancel lands ReportModalHide while the modal is still QUEUED
        // (CloseBlocking finds no current window but ALWAYS clears the tag) → the modal then enters: with the
        // tag gone, both lock decisions must leave it fully native (locally closeable) — previously it entered
        // view-locked with no future hide (permanently dead Cancel).
        BlockingModalMirrorRegistry.MarkMirrorShown(4);      // GeoModalDisplay.Show (queued)
        BlockingModalMirrorRegistry.ClearMirrorShown(4);     // ReportModalHide before the window entered
        Assert.False(BlockingModalLockDecision.ShouldGreyButtons(
            isHost: false, isActiveSession: true, isBlockingModal: true,
            isMirrorShown: BlockingModalMirrorRegistry.IsMirrorShown(4)));
        Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isBlockingModal: true,
            isMirrorShown: BlockingModalMirrorRegistry.IsMirrorShown(4)));
    }

    [Fact]
    public void Registry_MarkIdempotent_ClearUnknownSafe_ResetDropsAll()
    {
        BlockingModalMirrorRegistry.MarkMirrorShown(15);
        BlockingModalMirrorRegistry.MarkMirrorShown(15);     // duplicate mirror push → still one tag
        BlockingModalMirrorRegistry.ClearMirrorShown(26);    // stray/late hide for an untagged type → no-op
        Assert.True(BlockingModalMirrorRegistry.IsMirrorShown(15));
        BlockingModalMirrorRegistry.MarkMirrorShown(4);
        BlockingModalMirrorRegistry.Reset();                 // save-transfer/reload boundary belt
        Assert.False(BlockingModalMirrorRegistry.IsMirrorShown(15));
        Assert.False(BlockingModalMirrorRegistry.IsMirrorShown(4));
    }
}
