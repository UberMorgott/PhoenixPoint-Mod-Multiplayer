using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure truth tables for the CLIENT-side view-lock of a mirrored MANDATORY blocking modal (ambush 15 /
/// base defense 11 / ancient defence 28 — <see cref="ReportModalClassifier.IsMandatoryBrief"/>). The client
/// renders the same native fullscreen prompt as the host but a MANDATORY one must be inert: no local
/// close/confirm (mission start is host-authoritative; the window is not skippable) and greyed buttons.
/// OPTIONAL mirrored briefs (scavenge 4 etc.) are NOT locked — their native CLOSE performs a pure local
/// dismiss (null mirror DialogCallback); the HOST intent gate alone prevents racing (soak 2026-07-05: the
/// wider lock left the scavenge brief's CLOSE dead on the client). ORIGIN CONTRACT (2026-07-05): the lock
/// applies ONLY to a MIRROR-shown window (<see cref="BlockingModalMirrorRegistry"/>) — a client-native
/// blocking-type window keeps native buttons (Cancel closes locally), and a mirror window whose
/// ReportModalHide landed before it entered (queued-show race) comes up unlocked. The Harmony glue
/// (BlockingModalClientLockPatches) binds game types and is exercised in-game; the decisions are pinned here.
/// </summary>
public class BlockingModalLockDecisionTests
{
    public BlockingModalLockDecisionTests() => BlockingModalMirrorRegistry.Reset();

    // ── ShouldBlockLocalClose: UIStateGeoModal.FinishDialog (all buttons) + OnCancel (Esc/back) prefixes ──
    [Fact]
    public void BlockClose_ClientActiveSessionMirrorShownMandatoryBrief_True()
        => Assert.True(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isMandatoryBrief: true, isMirrorShown: true));

    [Fact]
    public void BlockClose_Host_NeverBlocked()
        // Host transparency: the host's own confirm must run natively (LaunchMission → co-op deploy flow).
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: true, isActiveSession: true, isApplying: false, isMandatoryBrief: true, isMirrorShown: true));

    [Fact]
    public void BlockClose_NoActiveSession_NeverBlocked()
        // Solo / post-session play with the mod loaded: the native ambush prompt must stay fully usable.
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: false, isApplying: false, isMandatoryBrief: true, isMirrorShown: true));

    [Fact]
    public void BlockClose_EngineDrivenReplay_Passes()
        // A mirror-driven close under SyncApplyScope must never be swallowed (host-resolve release path belt).
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: true, isMandatoryBrief: true, isMirrorShown: true));

    [Fact]
    public void BlockClose_NonMandatoryModal_Passes()
        // Mirrored REPORT modals (research complete etc.) AND optional mirrored briefs stay locally
        // dismissible — only the mandatory briefs lock.
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isMandatoryBrief: false, isMirrorShown: true));

    [Fact]
    public void BlockClose_NativeOriginWindow_NeverBlocked()
        // THE 2026-07-05 dead-Cancel bug: a blocking-TYPE window the mirror did NOT show (client-native opener /
        // hide already landed) must keep its native close — no host hide would ever release it.
        => Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isMandatoryBrief: true, isMirrorShown: false));

    [Fact]
    public void BlockClose_MirroredOptionalScavengeBrief_ClientCloseWorks()
    {
        // THE 2026-07-05 soak dead-CLOSE bug (client resource-site brief, GeoScavengeBrief=4): the mirrored
        // OPTIONAL brief was view-locked while the host's copy stayed open, so the client's CLOSE did nothing
        // for as long as the host dawdled. The lock decisions key on IsMandatoryBrief now — the optional
        // mirror-shown brief must stay locally dismissible (pure local pop; the host gate handles racing).
        BlockingModalMirrorRegistry.MarkMirrorShown(ReportModalClassifier.GeoScavengeBrief);   // GeoModalDisplay.Show
        Assert.True(ReportModalClassifier.IsBlockingModal(ReportModalClassifier.GeoScavengeBrief));   // host gate still arms
        Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false,
            isMandatoryBrief: ReportModalClassifier.IsMandatoryBrief(ReportModalClassifier.GeoScavengeBrief),
            isMirrorShown: BlockingModalMirrorRegistry.IsMirrorShown(ReportModalClassifier.GeoScavengeBrief)));
        Assert.False(BlockingModalLockDecision.ShouldGreyButtons(
            isHost: false, isActiveSession: true,
            isMandatoryBrief: ReportModalClassifier.IsMandatoryBrief(ReportModalClassifier.GeoScavengeBrief),
            isMirrorShown: BlockingModalMirrorRegistry.IsMirrorShown(ReportModalClassifier.GeoScavengeBrief)));
    }

    // ── ShouldGreyButtons: UIModuleModal.Show postfix (visual inertness) ──────────────────────────
    [Fact]
    public void Grey_ClientActiveSessionMirrorShownMandatoryBrief_True()
        => Assert.True(BlockingModalLockDecision.ShouldGreyButtons(
            isHost: false, isActiveSession: true, isMandatoryBrief: true, isMirrorShown: true));

    [Theory]
    [InlineData(true, true, true, true)]     // host → pristine native window (host transparency)
    [InlineData(false, false, true, true)]   // no session → native
    [InlineData(false, true, false, true)]   // non-mandatory modal (report / optional brief) → native
    [InlineData(false, true, true, false)]   // NOT mirror-shown (native origin / hide landed first) → native
    public void Grey_AnyOtherCombination_False(bool isHost, bool isActiveSession, bool isMandatoryBrief, bool isMirrorShown)
        => Assert.False(BlockingModalLockDecision.ShouldGreyButtons(isHost, isActiveSession, isMandatoryBrief, isMirrorShown));

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
        // view-locked with no future hide (permanently dead Cancel). Pinned on a MANDATORY brief (15) — the
        // only class the lock still covers.
        BlockingModalMirrorRegistry.MarkMirrorShown(15);     // GeoModalDisplay.Show (queued)
        BlockingModalMirrorRegistry.ClearMirrorShown(15);    // ReportModalHide before the window entered
        Assert.False(BlockingModalLockDecision.ShouldGreyButtons(
            isHost: false, isActiveSession: true, isMandatoryBrief: true,
            isMirrorShown: BlockingModalMirrorRegistry.IsMirrorShown(15)));
        Assert.False(BlockingModalLockDecision.ShouldBlockLocalClose(
            isHost: false, isActiveSession: true, isApplying: false, isMandatoryBrief: true,
            isMirrorShown: BlockingModalMirrorRegistry.IsMirrorShown(15)));
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
