using System.Collections.Generic;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Batch-3 P4 regression: <see cref="EventCorrelator"/> as a CONSUMER of the <see cref="UnifiedDisplayQueue"/>
/// — raises reach the correlator only when the queue releases them (host display order), while dismisses/
/// advances keep flowing straight to the correlator (they are resolutions, not displays). This mirrors the
/// SyncEngine glue contract: a released raise that OCCUPIES the correlator slot (ShowDialog) occupies the
/// queue slot; a TERMINAL resolution (ShowResultPage/DropNoop/Ignore) frees it at once; a dismiss/advance
/// that frees the correlator slot closes the queued EVENT display and releases the next. All the correlator's
/// own dedup/correlation behavior (EventCorrelatorTests) is untouched — these tests only pin the composition.
/// </summary>
public class EventCorrelatorConsumerTests
{
    private readonly UnifiedDisplayQueue _queue = new UnifiedDisplayQueue();
    private readonly EventCorrelator _correlator = new EventCorrelator();
    private readonly Dictionary<uint, (ushort OccId, string EventId)> _stash = new Dictionary<uint, (ushort, string)>();
    private readonly List<(EventCorrelator.ActionKind Kind, ushort OccId)> _executed = new List<(EventCorrelator.ActionKind, ushort)>();

    // The SyncEngine.OnEventRaised routing: stamped raise → enqueue + stash + drain.
    private void DeliverRaise(ushort occId, string eventId, uint seq, int priority)
    {
        if (!_queue.Enqueue(seq, priority, UnifiedDisplayQueue.KindEvent)) return;   // dup delivery
        _stash[seq] = (occId, eventId);
        Drain();
    }

    // The SyncEngine.OnEventDismiss routing: resolution goes straight to the correlator, then the freed
    // correlator slot closes the queued EVENT display (NotifyEventDisplayMaybeClosed).
    private EventCorrelator.Decision DeliverDismiss(ushort occId, string eventId, int choiceIndex)
    {
        var decision = _correlator.Dismissed(occId, eventId, choiceIndex);
        _executed.Add((decision.Kind, occId));
        NotifyEventDisplayMaybeClosed();
        return decision;
    }

    // The SyncEngine.TryReleaseDisplays loop for the event kind.
    private void Drain()
    {
        while (_queue.TryRelease(out var seq, out _))
        {
            var (occId, eventId) = _stash[seq];
            _stash.Remove(seq);
            var decision = _correlator.Raised(occId, eventId);
            _executed.Add((decision.Kind, occId));
            if (!_correlator.ShownSlotFree) break;   // ShowDialog occupies both slots → wait for the close
            _queue.NotifyClosed(seq);                // terminal → keep draining
        }
    }

    private void NotifyEventDisplayMaybeClosed()
    {
        if (_queue.HasCurrent && _queue.CurrentKind == UnifiedDisplayQueue.KindEvent && _correlator.ShownSlotFree)
        {
            _queue.NotifyClosed(_queue.CurrentSeq);
            Drain();
        }
    }

    private List<ushort> ShownOrder()
    {
        var shown = new List<ushort>();
        foreach (var (kind, occId) in _executed)
            if (kind == EventCorrelator.ActionKind.ShowDialog) shown.Add(occId);
        return shown;
    }

    [Fact]
    public void Burst_ReversedArrival_ShowsInHostOrder_OneAtATime()
    {
        // Host raised occ 1,2,3 (seqs 10,11,12, equal priority); transport delivered them REVERSED.
        DeliverRaise(3, "C", 12, 0);
        DeliverRaise(2, "B", 11, 0);
        DeliverRaise(1, "A", 10, 0);
        Assert.Equal(new List<ushort> { 3 }, ShownOrder());   // only the first-arrived released yet…
        // …but seqs 10/11 were queued ABOVE 12? No: 12 was released instantly (slot free on arrival).
        // The remaining two release in seq order as each dialog closes.
        DeliverDismiss(3, "C", -1);
        Assert.Equal(new List<ushort> { 3, 1 }, ShownOrder());
        DeliverDismiss(1, "A", -1);
        Assert.Equal(new List<ushort> { 3, 1, 2 }, ShownOrder());
        Assert.True(_correlator.ShownSlotFree == false);      // occ 2 still showing
        DeliverDismiss(2, "B", -1);
        Assert.True(_correlator.ShownSlotFree);
        Assert.False(_queue.HasCurrent);
    }

    [Fact]
    public void FullBurst_QueuedBeforeFirstClose_ReleasesInSeqOrder()
    {
        // First display shows immediately; the REST of the burst arrives while it is open.
        DeliverRaise(1, "A", 10, 0);
        DeliverRaise(4, "D", 13, 0);   // out-of-order arrivals…
        DeliverRaise(2, "B", 11, 0);
        DeliverRaise(3, "C", 12, 0);
        Assert.Equal(new List<ushort> { 1 }, ShownOrder());
        DeliverDismiss(1, "A", -1);
        DeliverDismiss(2, "B", -1);
        DeliverDismiss(3, "C", -1);
        // …but the client shows them in HOST order 1,2,3,4 — the transport shuffle is invisible.
        Assert.Equal(new List<ushort> { 1, 2, 3, 4 }, ShownOrder());
    }

    [Fact]
    public void HigherPriorityDisplay_ReleasesBeforeEarlierLowerPriority()
    {
        DeliverRaise(1, "A", 10, 0);    // showing
        DeliverRaise(2, "B", 11, 0);    // waits (plain prio 0)
        DeliverRaise(3, "C", 12, 10);   // TriggeredByEvent prio 10 → overtakes the waiting seq 11 (native)
        DeliverDismiss(1, "A", -1);
        Assert.Equal(new List<ushort> { 1, 3 }, ShownOrder());
        DeliverDismiss(3, "C", -1);
        Assert.Equal(new List<ushort> { 1, 3, 2 }, ShownOrder());
    }

    [Fact]
    public void DismissBeforeRelease_ResolvesTerminal_AndDrainContinues()
    {
        DeliverRaise(1, "A", 10, 0);            // showing
        DeliverRaise(2, "B", 11, 0);            // still held in the UNIFIED queue (not yet at the correlator)
        DeliverRaise(3, "C", 12, 0);
        // The host already resolved occ 2 (result-bearing dismiss) while its raise waits in the queue:
        // the correlator BUFFERS it (raise unknown there yet)…
        var d = DeliverDismiss(2, "B", 1);
        Assert.Equal(EventCorrelator.ActionKind.BufferDismiss, d.Kind);
        // …so when the queue releases raise 2 it resolves STRAIGHT to the result page (terminal, non-
        // occupying) and the drain continues to raise 3 in the SAME release wave.
        DeliverDismiss(1, "A", -1);
        Assert.Contains((EventCorrelator.ActionKind.ShowResultPage, (ushort)2), _executed);
        Assert.Equal(new List<ushort> { 1, 3 }, ShownOrder());   // 2 never re-prompted — host already decided
        Assert.False(_correlator.ShownSlotFree);                  // 3 is showing
    }

    [Fact]
    public void DuplicateStampedRaise_IsDroppedAtTheQueue()
    {
        DeliverRaise(1, "A", 10, 0);
        DeliverRaise(1, "A", 10, 0);   // STUN double-send of the SAME stamped display
        DeliverDismiss(1, "A", -1);
        Assert.Equal(new List<ushort> { 1 }, ShownOrder());
        Assert.False(_queue.HasCurrent);
        Assert.Equal(0, _queue.QueuedCount);
    }

    [Fact]
    public void CorrelatorDedup_StillGuards_WhenSameOccIdRidesTwoSeqs()
    {
        // Defense-in-depth: even if the same occurrence somehow got two stamps (must not happen, but the
        // correlator's own dedup is the second belt the spec mandates we keep), the second release is Ignore.
        DeliverRaise(1, "A", 10, 0);
        DeliverRaise(1, "A", 11, 0);
        DeliverDismiss(1, "A", -1);
        Assert.Contains((EventCorrelator.ActionKind.Ignore, (ushort)1), _executed);
        Assert.Equal(new List<ushort> { 1 }, ShownOrder());
    }
}
