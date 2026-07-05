using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Batch-3 P4: the pure client-side <see cref="UnifiedDisplayQueue"/> must reproduce the NATIVE
/// <c>GeoscapeViewSwitchQuery</c> semantics the host's own displays obey (decompile-verified reference):
/// QueryStateSwitch = sorted insert before the first STRICTLY-LOWER-priority request (priority DESC, FIFO
/// among equals), ProcessQueriedStateSwitch = one at a time. The property test below drives both the queue
/// and a literal reference model of that native code with the same display set — the queue receives them in
/// RANDOM arrival order (transport reordering), the model in host emission order — and the release sequences
/// must be identical.
/// </summary>
public class UnifiedDisplayQueueTests
{
    /// <summary>Literal reference model of the native GeoscapeViewSwitchQuery (sorted insert + FIFO pop).</summary>
    private sealed class NativeSwitchQueryModel
    {
        private readonly List<(uint Seq, int Priority)> _requests = new List<(uint, int)>();

        // GeoscapeViewSwitchQuery.QueryStateSwitch: insert before the first request with Priority < new.
        public void QueryStateSwitch(uint seq, int priority)
        {
            int idx = _requests.FindIndex(r => r.Priority < priority);
            if (idx < 0) idx = _requests.Count;
            _requests.Insert(idx, (seq, priority));
        }

        // GetNextQueriedStateSwitch: pop head (the caller enforces one-at-a-time via its current slot).
        public bool TryPop(out uint seq)
        {
            seq = 0;
            if (_requests.Count == 0) return false;
            seq = _requests[0].Seq;
            _requests.RemoveAt(0);
            return true;
        }
    }

    private static List<uint> DrainAll(UnifiedDisplayQueue q)
    {
        var released = new List<uint>();
        while (q.TryRelease(out var seq, out _))
        {
            released.Add(seq);
            q.NotifyClosed(seq);   // every display closes immediately → pure order check
        }
        return released;
    }

    // ─── property test vs the native reference model ─────────────────────────────────────────────

    [Fact]
    public void BurstArrival_AnyOrder_MatchesNativeModelOrder()
    {
        var rng = new Random(20260705);   // deterministic seed (repo style: no flaky randomness)
        for (int round = 0; round < 200; round++)
        {
            int n = 1 + rng.Next(12);
            var displays = new List<(uint Seq, int Priority)>();
            for (uint i = 1; i <= n; i++)
                displays.Add((i, PickPriority(rng)));   // native-ish priorities incl. equals

            // Reference: the host enqueues in SEQ order (displaySeq IS host QueryStateSwitch call order).
            var model = new NativeSwitchQueryModel();
            foreach (var d in displays.OrderBy(d => d.Seq))
                model.QueryStateSwitch(d.Seq, d.Priority);
            var expected = new List<uint>();
            while (model.TryPop(out var s)) expected.Add(s);

            // Queue under test: same displays in RANDOM arrival order (transport reorder), then drained.
            var q = new UnifiedDisplayQueue();
            foreach (var d in displays.OrderBy(_ => rng.Next()))
                Assert.True(q.Enqueue(d.Seq, d.Priority, UnifiedDisplayQueue.KindEvent));
            var actual = DrainAll(q);

            Assert.Equal(expected, actual);
        }
    }

    private static int PickPriority(Random rng)
    {
        // The real native values: plain 0 / TriggeredByEvent 10 / completed-upgrade 15 / modal 100 / cutscene 100.
        int[] pool = { 0, 0, 0, 10, 10, 15, 100 };
        return pool[rng.Next(pool.Length)];
    }

    // ─── targeted native-semantics cases ─────────────────────────────────────────────────────────

    [Fact]
    public void HigherPriority_ReleasesFirst_EqualPriority_FifoBySeq()
    {
        var q = new UnifiedDisplayQueue();
        Assert.True(q.Enqueue(3, 0, UnifiedDisplayQueue.KindEvent));     // late-arriving low prio
        Assert.True(q.Enqueue(1, 0, UnifiedDisplayQueue.KindEvent));     // earlier seq, same prio → first among equals
        Assert.True(q.Enqueue(2, 100, UnifiedDisplayQueue.KindReport));  // higher prio → first overall
        Assert.Equal(new List<uint> { 2, 1, 3 }, DrainAll(q));
    }

    [Fact]
    public void OneAtATime_NextReleasesOnlyAfterClose()
    {
        var q = new UnifiedDisplayQueue();
        q.Enqueue(1, 0, UnifiedDisplayQueue.KindEvent);
        q.Enqueue(2, 0, UnifiedDisplayQueue.KindEvent);
        Assert.True(q.TryRelease(out var first, out _));
        Assert.Equal(1u, first);
        Assert.True(q.HasCurrent);
        Assert.False(q.TryRelease(out _, out _));   // slot occupied → held
        q.NotifyClosed(1);
        Assert.False(q.HasCurrent);
        Assert.True(q.TryRelease(out var second, out _));
        Assert.Equal(2u, second);
    }

    [Fact]
    public void LateHigherPriority_OvertakesQueuedLower_ButNeverTheShownCurrent()
    {
        var q = new UnifiedDisplayQueue();
        q.Enqueue(1, 0, UnifiedDisplayQueue.KindEvent);
        Assert.True(q.TryRelease(out _, out _));            // seq 1 showing
        q.Enqueue(2, 0, UnifiedDisplayQueue.KindEvent);     // waits
        q.Enqueue(3, 100, UnifiedDisplayQueue.KindReport);  // higher prio arrives later
        Assert.False(q.TryRelease(out _, out _));           // current still shown — never preempted
        q.NotifyClosed(1);
        Assert.Equal(new List<uint> { 3, 2 }, DrainAll(q)); // then priority wins among the waiters (native)
    }

    // ─── dedup + guards ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DuplicateSeq_IsRejected_Queued_Current_AndCompleted()
    {
        var q = new UnifiedDisplayQueue();
        Assert.True(q.Enqueue(5, 0, UnifiedDisplayQueue.KindEvent));
        Assert.False(q.Enqueue(5, 0, UnifiedDisplayQueue.KindEvent));   // duplicate pending
        Assert.True(q.TryRelease(out _, out _));
        Assert.False(q.Enqueue(5, 0, UnifiedDisplayQueue.KindEvent));   // duplicate of the current
        q.NotifyClosed(5);
        Assert.False(q.Enqueue(5, 0, UnifiedDisplayQueue.KindEvent));   // duplicate of a completed (STUN re-send)
    }

    [Fact]
    public void UnstampedSeqZero_NeverEnqueues()
    {
        var q = new UnifiedDisplayQueue();
        Assert.False(q.Enqueue(0, 100, UnifiedDisplayQueue.KindReport));
        Assert.Equal(0, q.QueuedCount);
    }

    [Fact]
    public void NotifyClosed_Mismatched_DoesNotFreeTheSlot()
    {
        var q = new UnifiedDisplayQueue();
        q.Enqueue(1, 0, UnifiedDisplayQueue.KindEvent);
        q.TryRelease(out _, out _);
        q.NotifyClosed(99);                     // stray/late close for someone else
        Assert.True(q.HasCurrent);
        q.NotifyClosed(1);
        Assert.False(q.HasCurrent);
    }

    [Fact]
    public void ClearCurrent_FreesAndCompletesTheOrphanedDisplay()
    {
        var q = new UnifiedDisplayQueue();
        q.Enqueue(1, 0, UnifiedDisplayQueue.KindReport);
        q.TryRelease(out _, out _);
        q.ClearCurrent();                       // view-teardown belt
        Assert.False(q.HasCurrent);
        Assert.False(q.Enqueue(1, 0, UnifiedDisplayQueue.KindReport));  // orphan is completed → dup-proof
    }

    [Fact]
    public void Reset_ClearsPending_Current_AndDedup()
    {
        var q = new UnifiedDisplayQueue();
        q.Enqueue(1, 0, UnifiedDisplayQueue.KindEvent);
        q.Enqueue(2, 0, UnifiedDisplayQueue.KindEvent);
        q.TryRelease(out _, out _);
        q.Reset();
        Assert.False(q.HasCurrent);
        Assert.Equal(0, q.QueuedCount);
        Assert.True(q.Enqueue(1, 0, UnifiedDisplayQueue.KindEvent));    // post-boundary ids start fresh
    }

    [Fact]
    public void CompletedTracking_IsBounded()
    {
        var q = new UnifiedDisplayQueue();
        for (uint s = 1; s <= UnifiedDisplayQueue.MaxCompletedTracked + 8; s++)
        {
            Assert.True(q.Enqueue(s, 0, UnifiedDisplayQueue.KindEvent));
            Assert.True(q.TryRelease(out _, out _));
            q.NotifyClosed(s);
        }
        // The oldest id fell off the bounded FIFO → re-enqueue accepted (never a leak, never a false dup).
        Assert.True(q.Enqueue(1, 0, UnifiedDisplayQueue.KindEvent));
    }
}
