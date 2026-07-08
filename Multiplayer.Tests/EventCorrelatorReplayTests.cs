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
    public void CloseOnlyDecided_OnOpenWindow_StaysLegacyClose()
    {
        var c = new EventCorrelator();
        c.Raised(1, "EX");

        // A close-only decided signal (choiceIndex < 0, e.g. decline) has no winner to highlight → legacy close,
        // never a replay arm, even under the gate.
        var d = c.Dismissed(1, "EX", choiceIndex: -1, replayMode: true);
        Assert.Equal(Kind.CloseDialog, d.Kind);
        Assert.Equal(0, c.DecidedCount);
        Assert.Equal(0, c.OpenCount);
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
