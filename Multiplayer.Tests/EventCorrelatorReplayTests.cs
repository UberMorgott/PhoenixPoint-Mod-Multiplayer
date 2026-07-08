using Multiplayer.Network.Sync.State;
using Xunit;

using Kind = Multiplayer.Network.Sync.State.EventCorrelator.ActionKind;

/// <summary>
/// Pure replay-mode transitions for <see cref="EventCorrelator"/> (co-op event-window replay, gate
/// <c>EventReplayModeGate</c>). Replay applies ONLY to an actually-OPEN choice window on this peer: when the
/// decided signal arrives and this peer did not win, the window stays open and is replay-armed (the caller greys
/// non-winning buttons + highlights the winner) instead of force-transitioning. The winner (locally-picked ==
/// decided winning index) keeps the legacy auto in-place transition. When <c>replayMode</c> is false EVERY path
/// is byte-for-byte the legacy behavior (covered by EventCorrelatorTests).
/// </summary>
public class EventCorrelatorReplayTests
{
    [Fact]
    public void Winner_OpenThenDecidedMatchesPick_AutoTransitionsInPlace()
    {
        var c = new EventCorrelator();
        Assert.Equal(Kind.ShowDialog, c.Raised(1, "EX").Kind);
        c.MarkPickedChoice(1, 2);   // this peer clicked choice 2 (its answer relay is in flight)

        // Decided signal: choice 2 won == this peer's pick → WINNER → auto in-place result page (legacy).
        var d = c.Dismissed(1, "EX", choiceIndex: 2, replayMode: true);
        Assert.Equal(Kind.ShowResultInPlace, d.Kind);
        Assert.Equal(2, d.ChoiceIndex);
        Assert.Equal(0, c.OpenCount);
        Assert.Equal(0, c.DecidedCount);   // winner never arms replay
    }

    [Fact]
    public void NonWinner_OpenOnChoicePage_ArmsReplayAndKeepsWindowOpen()
    {
        var c = new EventCorrelator();
        c.Raised(1, "EX");   // window open on the choice page, this peer never clicked

        var d = c.Dismissed(1, "EX", choiceIndex: 0, replayMode: true);
        Assert.Equal(Kind.ArmReplay, d.Kind);
        Assert.Equal(0, d.ChoiceIndex);           // winning index carried for the highlight
        Assert.Equal(1, c.OpenCount);             // window stays LIVE (not force-transitioned)
        Assert.Equal(1, c.DecidedCount);
        Assert.True(c.TryGetDecided(1, out var w) && w == 0);
        Assert.False(c.ShownSlotFree);            // single slot still occupied by the open window
    }

    [Fact]
    public void RaceLoser_PickedDifferentChoice_ArmsReplay()
    {
        var c = new EventCorrelator();
        c.Raised(1, "EX");
        c.MarkPickedChoice(1, 1);   // this peer clicked choice 1 but lost the claim

        var d = c.Dismissed(1, "EX", choiceIndex: 0, replayMode: true);   // choice 0 won
        Assert.Equal(Kind.ArmReplay, d.Kind);
        Assert.Equal(0, d.ChoiceIndex);
        Assert.Equal(1, c.DecidedCount);
        Assert.True(c.TryGetDecided(1, out var w) && w == 0);
    }

    [Fact]
    public void ReplayLocalClick_AfterArm_ShowsResultPageAndResolvesTerminally()
    {
        var c = new EventCorrelator();
        c.Raised(1, "EX");
        Assert.Equal(Kind.ArmReplay, c.Dismissed(1, "EX", 0, replayMode: true).Kind);

        var d = c.ReplayLocalClick(1, "EX");
        Assert.Equal(Kind.ShowResultPage, d.Kind);
        Assert.Equal(0, d.ChoiceIndex);
        Assert.Equal(0, c.DecidedCount);   // consumed
        Assert.Equal(0, c.OpenCount);
        Assert.True(c.ShownSlotFree);      // slot freed → the next deferred raise may release

        // Terminal: a duplicate raise / dismiss for it is now an idempotent no-op (dedup).
        Assert.Equal(Kind.Ignore, c.Raised(1, "EX").Kind);
        Assert.Equal(Kind.Ignore, c.Dismissed(1, "EX", 0, replayMode: true).Kind);
    }

    [Fact]
    public void ReplayLocalClick_NotArmed_Ignored()
    {
        var c = new EventCorrelator();
        c.Raised(1, "EX");
        Assert.Equal(Kind.Ignore, c.ReplayLocalClick(1, "EX").Kind);   // never decided → no-op
    }

    [Fact]
    public void DuplicateDecidedSignal_WhileArmed_Ignored()
    {
        var c = new EventCorrelator();
        c.Raised(1, "EX");
        Assert.Equal(Kind.ArmReplay, c.Dismissed(1, "EX", 0, replayMode: true).Kind);

        // A transport double-send of the decided signal must NOT re-arm / re-record.
        Assert.Equal(Kind.Ignore, c.Dismissed(1, "EX", 0, replayMode: true).Kind);
        Assert.Equal(1, c.DecidedCount);
        Assert.Equal(1, c.OpenCount);   // still the SAME live armed window
    }

    [Fact]
    public void GateOff_OpenNonWinner_KeepsLegacyForcedTransition()
    {
        var c = new EventCorrelator();
        c.Raised(1, "EX");

        // replayMode:false → byte-for-byte legacy: the open dialog force-transitions to its result page.
        var d = c.Dismissed(1, "EX", choiceIndex: 0, replayMode: false);
        Assert.Equal(Kind.ShowResultInPlace, d.Kind);
        Assert.Equal(0, c.DecidedCount);   // nothing armed off-gate
        Assert.Equal(0, c.OpenCount);
    }

    [Fact]
    public void CloseOnlyDecided_OnOpenWindow_ArmsAndLocalClickCloses()
    {
        // UNIFIED RULE: a close-only terminal (decline / bare close) for an OPEN window this peer didn't answer
        // ARMS exactly like a result-bearing one (terminal kind affects only the visuals + the consume action) —
        // the local click then closes locally (ShowResultPage with ChoiceIndex -1 → the caller dismisses instead
        // of building a result page). No forced close.
        var c = new EventCorrelator();
        c.Raised(1, "EX");

        var d = c.Dismissed(1, "EX", choiceIndex: -1, replayMode: true);
        Assert.Equal(Kind.ArmReplay, d.Kind);
        Assert.Equal(-1, d.ChoiceIndex);
        Assert.Equal(1, c.OpenCount);   // window stays live
        Assert.True(c.TryGetDecided(1, out var w) && w == -1);

        var click = c.ReplayLocalClick(1, "EX");
        Assert.Equal(Kind.ShowResultPage, click.Kind);
        Assert.Equal(-1, click.ChoiceIndex);
        Assert.Equal(0, c.OpenCount);
        Assert.True(c.ShownSlotFree);
    }

    [Fact]
    public void LocalDecliner_CloseOnlyDecided_IsWinner_LegacyClose()
    {
        // The peer that itself clicked DECLINE (picked -1) and won: the close-only decided signal matches its
        // pick → legacy auto close (winner path), never an arm.
        var c = new EventCorrelator();
        c.Raised(1, "EX");
        c.MarkPickedChoice(1, -1);

        var d = c.Dismissed(1, "EX", choiceIndex: -1, replayMode: true);
        Assert.Equal(Kind.CloseDialog, d.Kind);
        Assert.Equal(0, c.DecidedCount);
        Assert.Equal(0, c.OpenCount);
    }

    [Fact]
    public void PromptMirror_HostAdvance_NotLocallyAnswered_ArmsInsteadOfForcing()
    {
        // Single-OK info (2-window single-choice): dismiss beats raise → prompt mirror shown. The HOST player
        // clicks its prompt (EventAdvanceResult) while THIS peer is still reading → ARM, no forced transition.
        var c = new EventCorrelator();
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(1, "EX", choiceIndex: 0).Kind);
        var raised = c.Raised(1, "EX", singleChoice: true, oneWindow: false, replayMode: true);
        Assert.Equal(Kind.ShowDialog, raised.Kind);   // window-1 prompt mirror
        Assert.Equal(1, c.PromptMirrorCount);

        var adv = c.Advanced(1, "EX", choiceIndex: 0, replayMode: true);
        Assert.Equal(Kind.ArmReplay, adv.Kind);
        Assert.Equal(1, c.OpenCount);          // prompt stays open at the reader's pace
        Assert.Equal(1, c.PromptMirrorCount);  // mirror tracking intact
        Assert.True(c.TryGetDecided(1, out var w) && w == 0);

        // Duplicate advance while armed → idempotent no-op.
        Assert.Equal(Kind.Ignore, c.Advanced(1, "EX", 0, replayMode: true).Kind);

        // Local OK consumes → result page; occurrence terminal.
        var click = c.ReplayLocalClick(1, "EX");
        Assert.Equal(Kind.ShowResultPage, click.Kind);
        Assert.Equal(0, click.ChoiceIndex);
        Assert.Equal(0, c.PromptMirrorCount);
        Assert.True(c.ShownSlotFree);
        Assert.Equal(Kind.Ignore, c.Advanced(1, "EX", 0, replayMode: true).Kind);   // late dup after terminal
    }

    [Fact]
    public void PromptMirror_LocallyAnswered_HostAdvance_KeepsWinnerInPlaceTransition()
    {
        // The ANSWERING peer (single-choice modal-hold, e4d1252): it clicked the lone prompt button (greyed,
        // relayed the advance) → the host's EventAdvanceResult still auto-transitions the SAME window in place.
        var c = new EventCorrelator();
        c.Dismissed(1, "EX", choiceIndex: 0);
        c.Raised(1, "EX", singleChoice: true, oneWindow: false, replayMode: true);
        c.MarkLocallyAnswered(1);

        var adv = c.Advanced(1, "EX", choiceIndex: 0, replayMode: true);
        Assert.Equal(Kind.ShowResultPage, adv.Kind);   // winner path unchanged — no arm
        Assert.Equal(0, c.DecidedCount);
        Assert.Equal(0, c.OpenCount);
    }

    [Fact]
    public void BufferedAdvanceBeforeRaise_Replay_ShowsPromptArmedInsteadOfJumping()
    {
        // Advance beat the raise (host clicked before this peer ever showed the prompt). Legacy jumped straight
        // to the result page (window-1 skipped); replay shows the prompt ARMED so the reader still gets window-1.
        var c = new EventCorrelator();
        Assert.Equal(Kind.BufferDismiss, c.Dismissed(1, "EX", choiceIndex: 0).Kind);
        Assert.Equal(Kind.DropNoop, c.Advanced(1, "EX", 0, replayMode: true).Kind);   // buffered (no mirror yet)

        var raised = c.Raised(1, "EX", singleChoice: true, oneWindow: false, replayMode: true);
        Assert.Equal(Kind.ShowDialog, raised.Kind);            // prompt shown, NOT ShowResultPage
        Assert.True(c.TryGetDecided(1, out var w) && w == 0);  // ... already armed
        Assert.Equal(1, c.OpenCount);
        Assert.False(c.ShownSlotFree);

        var click = c.ReplayLocalClick(1, "EX");
        Assert.Equal(Kind.ShowResultPage, click.Kind);
        Assert.True(c.ShownSlotFree);
    }

    [Fact]
    public void OneWindowSingleChoice_Replay_KeepsLegacyDirectResultPage()
    {
        // 1-WINDOW single-choice (host itself showed ONE combined window): no window was ever open on this peer
        // and the jump IS the faithful mirror → legacy ShowResultPage even under replay.
        var c = new EventCorrelator();
        c.Dismissed(1, "EX", choiceIndex: 0);
        var raised = c.Raised(1, "EX", singleChoice: true, oneWindow: true, replayMode: true);
        Assert.Equal(Kind.ShowResultPage, raised.Kind);
        Assert.Equal(0, c.DecidedCount);
    }

    [Fact]
    public void StackedQueue_HostFastClicks_EachWindowPresentsInOrderArmed()
    {
        // Host fast-clicks through W1..W3 while this peer reads W1. Every terminal ARMS (open or queued —
        // queued raises stay queued), and each window presents IN occId ORDER, each waiting for the local click.
        var c = new EventCorrelator();
        Assert.Equal(Kind.ShowDialog, c.Raised(1, "EX1", replayMode: true).Kind);   // W1 open
        Assert.Equal(Kind.Enqueue, c.Raised(2, "EX2", replayMode: true).Kind);      // W2, W3 deferred
        Assert.Equal(Kind.Enqueue, c.Raised(3, "EX3", replayMode: true).Kind);

        Assert.Equal(Kind.ArmReplay, c.Dismissed(1, "EX1", 0, replayMode: true).Kind);    // open → armed
        Assert.Equal(Kind.ArmReplay, c.Dismissed(2, "EX2", 1, replayMode: true).Kind);    // queued → armed, stays queued
        Assert.Equal(Kind.ArmReplay, c.Dismissed(3, "EX3", -1, replayMode: true).Kind);   // queued close-only → armed too
        Assert.Equal(2, c.QueuedCount);   // nothing force-popped or skipped
        Assert.Equal(1, c.OpenCount);     // W1 still live

        // Duplicate dismiss for an armed-queued occurrence → idempotent no-op.
        Assert.Equal(Kind.Ignore, c.Dismissed(2, "EX2", 1, replayMode: true).Kind);

        // W1 consumed → W2 releases (lowest occId first), opens as a plain dialog still armed.
        Assert.Equal(Kind.ShowResultPage, c.ReplayLocalClick(1, "EX1").Kind);
        Assert.True(c.TryDequeueNext(out var w2, replayMode: true));
        Assert.Equal(Kind.ShowDialog, w2.Kind);
        Assert.Equal((ushort)2, w2.OccurrenceId);
        Assert.True(c.TryGetDecided(2, out var w2win) && w2win == 1);
        Assert.False(c.TryDequeueNext(out _, true));   // slot busy again → W3 waits

        // W2 consumed → W3 releases, armed close-only (local click will close it).
        Assert.Equal(Kind.ShowResultPage, c.ReplayLocalClick(2, "EX2").Kind);
        Assert.True(c.TryDequeueNext(out var w3, replayMode: true));
        Assert.Equal(Kind.ShowDialog, w3.Kind);
        Assert.Equal((ushort)3, w3.OccurrenceId);
        Assert.True(c.TryGetDecided(3, out var w3win) && w3win == -1);
        var w3click = c.ReplayLocalClick(3, "EX3");
        Assert.Equal(Kind.ShowResultPage, w3click.Kind);
        Assert.Equal(-1, w3click.ChoiceIndex);
        Assert.True(c.ShownSlotFree);
        Assert.Equal(0, c.QueuedCount);
        Assert.Equal(0, c.DecidedCount);
    }

    [Fact]
    public void GateOff_QueuedDismiss_KeepsLegacyPopAndResolve()
    {
        // replayMode:false → the queued raise is popped + resolved terminally exactly as before (byte-identical).
        var c = new EventCorrelator();
        c.Raised(1, "EX1");
        c.Raised(2, "EX2");
        var d = c.Dismissed(2, "EX2", 0, replayMode: false);
        Assert.Equal(Kind.ShowResultInPlace, d.Kind);
        Assert.Equal(0, c.QueuedCount);
        Assert.Equal(0, c.DecidedCount);
    }

    [Fact]
    public void GateOff_PromptMirrorAdvance_KeepsLegacyForcedTransition()
    {
        var c = new EventCorrelator();
        c.Dismissed(1, "EX", choiceIndex: 0);
        c.Raised(1, "EX", singleChoice: true);
        var adv = c.Advanced(1, "EX", 0);   // replayMode default false → legacy
        Assert.Equal(Kind.ShowResultPage, adv.Kind);
        Assert.Equal(0, c.DecidedCount);
    }

    [Fact]
    public void ArmReplay_HoldsSingleSlot_NextRaiseEnqueuedUntilLocalClick()
    {
        var c = new EventCorrelator();
        c.Raised(1, "EX1");
        Assert.Equal(Kind.ArmReplay, c.Dismissed(1, "EX1", 0, replayMode: true).Kind);

        // A second raise arriving while the replay-armed window still occupies the slot must DEFER.
        Assert.Equal(Kind.Enqueue, c.Raised(2, "EX2").Kind);
        Assert.Equal(1, c.QueuedCount);
        Assert.False(c.TryDequeueNext(out _));   // slot still busy

        // The local winner click frees the slot → the deferred raise releases in occId order.
        Assert.Equal(Kind.ShowResultPage, c.ReplayLocalClick(1, "EX1").Kind);
        Assert.True(c.TryDequeueNext(out var next));
        Assert.Equal(Kind.ShowDialog, next.Kind);
        Assert.Equal((ushort)2, next.OccurrenceId);
    }

    [Fact]
    public void RealDismissAfterArm_ForcesResultAndSupersedesArm()
    {
        // Defensive: if a genuine EventDismiss for the winner's own pick lands after an arm (shouldn't normally),
        // it must resolve terminally and drop the stale replay arm, never leave a wedged decided entry.
        var c = new EventCorrelator();
        c.Raised(1, "EX");
        c.MarkPickedChoice(1, 0);   // this peer IS the winner (picked 0)
        var d = c.Dismissed(1, "EX", choiceIndex: 0, replayMode: true);
        Assert.Equal(Kind.ShowResultInPlace, d.Kind);   // winner → terminal, never armed
        Assert.Equal(0, c.DecidedCount);
        Assert.Equal(0, c.OpenCount);
    }

    [Fact]
    public void DecidedRegistry_NeverLeaksAcrossManyOccurrences()
    {
        // The single display slot means at most ONE window is open-and-armed at a time, so the live decided set is
        // tiny in practice; RecordDecided's FIFO cap (>=128) is a defensive belt. Drive many open→arm→click cycles
        // and assert the registry stays bounded (never leaks) — each terminal click drops its entry.
        Assert.True(EventCorrelator.MaxDecidedTracked >= 128);
        var c = new EventCorrelator();
        for (int i = 1; i <= EventCorrelator.MaxDecidedTracked + 200; i++)
        {
            ushort occ = (ushort)i;
            c.Raised(occ, "EX");
            Assert.Equal(Kind.ArmReplay, c.Dismissed(occ, "EX", 0, replayMode: true).Kind);
            Assert.Equal(Kind.ShowResultPage, c.ReplayLocalClick(occ, "EX").Kind);
        }
        Assert.True(c.DecidedCount <= EventCorrelator.MaxDecidedTracked);
        Assert.Equal(0, c.DecidedCount);   // every cycle resolved terminally → nothing left armed
    }

    [Fact]
    public void Reset_ClearsDecidedAndPicked()
    {
        var c = new EventCorrelator();
        c.Raised(1, "EX");
        c.MarkPickedChoice(1, 1);
        c.Dismissed(1, "EX", 0, replayMode: true);
        Assert.Equal(1, c.DecidedCount);

        c.Reset();
        Assert.Equal(0, c.DecidedCount);
        Assert.False(c.TryGetDecided(1, out _));
    }
}
