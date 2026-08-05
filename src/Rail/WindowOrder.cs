using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PhoenixPoint.Geoscape.View;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// MODAL PRESENTATION ORDER, BY THE ONE CROSS-SURFACE KEY.
    ///
    /// THE REPORT (2026-08-05): a research completion and a geoscape event left the host together; the
    /// client received them 384 ms apart and showed them in the opposite order. Every peer's window
    /// CONTENTS were right — only the sequence differed. The client's event window rides the synchronous
    /// 0xB6 raise (<see cref="EventPopup"/>), while its research window is produced LOCALLY inside the
    /// 0xAC value-rail apply (<see cref="UiEventMap"/> → <c>ResearchSync.PresentFromMirror</c>), so the two
    /// arrive on unrelated cadences and <see cref="SurfaceSeq"/> — per-surface by design — cannot compare
    /// them. Presentation order was therefore LOCAL ARRIVAL ORDER, which is not a property any peer shares.
    ///
    /// THE KEY is <see cref="RailOrdinal"/>: one monotonic number minted for every outbound envelope
    /// regardless of surface, and inherited by anything a peer produces from inside an apply. This file is
    /// its only consumer, and it does three things and nothing else:
    ///
    ///   • STAMP at the game's own single queue entry point. Every window in the game is queued through
    ///     <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>, so stamping there covers every modal KIND by
    ///     construction rather than by enumeration — the same argument (and the same prefix) the rank in
    ///     <see cref="ReplenishSync.QueueRankPatch"/> already rests on.
    ///   • SETTLE, briefly and LOCALLY, before the drain. Ordering is worthless if the queue is emptied
    ///     the instant the first window lands, so the drain holds until the head has been queued for
    ///     <see cref="SettleSeconds"/> — about one diff tick, the window in which a lower ordinal can still
    ///     arrive and take its place.
    ///   • REORDER by (priority, ordinal) at that same drain, then let the game pop its own head.
    ///
    /// PRIORITY STAYS DOMINANT, AND THAT IS WHY THE RANK TABLE STAYS. <see cref="Compare"/> only consults
    /// the ordinal when two requests carry the SAME priority — which is what the 2026-08-04 report was
    /// about (three windows, all priority 0). <c>ReplenishSync.RankFor</c> answers a different question
    /// entirely: a DEVELOPER DECISION that the resupply screen outranks the event family on every peer.
    /// One decides ACROSS priorities, the other WITHIN one; neither can express the other, so L93's rank
    /// arm and this ordinal are orthogonal and both stay.
    ///
    /// WHY THIS CAN NEVER BLOCK. The settle is a comparison of two readings of <c>Time
    /// .realtimeSinceStartup</c> on THIS peer against a constant. It reads no peer, no roster, no
    /// membership and no message; nothing another peer does — or fails to do — appears in the predicate.
    /// Once <see cref="SettleExpired"/> is true it stays true forever, so the drain resumes unconditionally
    /// and the worst case for any window is one <see cref="SettleSeconds"/> delay, once.
    ///
    /// THE CEILING, STATED RATHER THAN HIDDEN. Every peer that receives a window agrees with every other
    /// peer that receives it, because both read the same host-minted key. The HOST agrees too for every
    /// family whose window and whose message are produced in the SAME call — the mirrored raises (0xB6
    /// events, 0xB7 modals, 0xBA cutscenes, 0xBB outcomes, 0xBF marketplace), where the broadcast postfix
    /// mints exactly the ordinal <see cref="RailOrdinal.ForNewWindow"/> handed the window a moment earlier.
    /// It does NOT yet agree for a window whose cause rides the PERIODIC value rail: research-complete is
    /// the one today. The host raises that modal natively at completion, a whole diff cycle before the 0xAC
    /// batch carrying the research field goes out, so it claims the same provisional ordinal as a raise that
    /// beat that batch to the wire and the tie falls back to the host's own insert order — while the clients
    /// order it by the batch that actually produced it. Host and clients can therefore still swap those two.
    /// ponytail: closing it means the host must not present a window until the message carrying its cause is
    /// on the wire (a real design change: the host would have to defer its own modals). Do it when a live
    /// session shows the host/client swap actually mattering — the peer-to-peer half, which is what the
    /// 2026-08-05 report measured between two clients, is closed here.
    ///
    /// COST, STATED: in a co-op session every queued window opens up to 150 ms later than native. Solo is
    /// untouched (the prefix returns immediately without an active session), and an UNSTAMPED request —
    /// one restored from the save through <c>RestoreData</c>, which bypasses <c>QueryStateSwitch</c> —
    /// carries no queue time and is therefore never held and never re-sorted behind a live one.
    /// </summary>
    internal static class WindowOrder
    {
        /// <summary>The BOUNDED LOCAL SETTLE. ~One <c>DiffEngine.TickInterval</c> (0.1 s) plus margin: the
        /// window in which the value-rail batch that produces a locally-raised modal can still land after a
        /// raise that beat it to the wire. Larger buys nothing (the next batch is a whole cycle away) and
        /// is paid by the user on every window; smaller stops covering one tick of jitter.</summary>
        internal const float SettleSeconds = 0.15f;

        private static readonly FieldInfo RequestsField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_viewStateSwitchRequests"); // GeoscapeViewSwitchQuery.cs:15
        private static readonly FieldInfo CurrentField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_currentStateSwitchRequest"); // :17

        /// <summary>Per-request order key. A CLASS because the store below needs a reference value, and a
        /// ConditionalWeakTable because a request the game drops must not be kept alive by our bookkeeping —
        /// no pruning pass, no leak, no lifetime rule to get wrong.</summary>
        private sealed class OrderKey
        {
            internal uint Ordinal;
            internal float QueuedAt;
        }

        private static readonly ConditionalWeakTable<GeoscapeViewStateSwitchRequest, OrderKey> _stamps =
            new ConditionalWeakTable<GeoscapeViewStateSwitchRequest, OrderKey>();

        private static bool _bindLogged;

        /// <summary>Called from the ONE queue entry point, on the request the game is actually about to
        /// insert (after any rank rebuild — a rebuilt request is a different instance, and stamping the one
        /// that never reaches the list would key nothing). Never throws into game code.</summary>
        internal static void Stamp(GeoscapeViewStateSwitchRequest request)
        {
            if (request == null) return;
            try
            {
                _stamps.Remove(request); // a re-queued instance restarts its own settle
                _stamps.Add(request, new OrderKey
                {
                    Ordinal = RailOrdinal.ForNewWindow(),
                    QueuedAt = Time.realtimeSinceStartup,
                });
            }
            catch (Exception ex)
            { Debug.LogWarning("[MP][windows] order stamp failed — this window falls back to insert order: " + ex.Message); }
        }

        /// <summary>THE COMPARATOR, pure and named so RailCheck executes the real one. Priority first and
        /// DESCENDING (the game's own rule: <c>QueryStateSwitch</c>:77 inserts before the first strictly
        /// lower priority), ordinal second and ASCENDING. Equal on both = the caller's stable tie-break, so
        /// the game's insert order still decides what nothing else can.</summary>
        internal static int Compare(int priorityA, uint ordinalA, int priorityB, uint ordinalB) =>
            priorityA != priorityB ? priorityB.CompareTo(priorityA) : ordinalA.CompareTo(ordinalB);

        /// <summary>THE SETTLE PREDICATE, pure and named for the same reason. Monotone in
        /// <paramref name="now"/> and bounded by a constant: once true it cannot become false again, which
        /// is the whole "this cannot become an unbounded wait" argument in one line.</summary>
        internal static bool SettleExpired(float queuedAt, float now) => now - queuedAt >= SettleSeconds;

        /// <summary>Re-sort <paramref name="pending"/> in place by <see cref="Compare"/>, STABLY (the
        /// original index is the final tie-break, so equal keys keep the game's insert order). Returns true
        /// when the order actually changed. Internal + injectable key lookup so RailCheck can drive the REAL
        /// sort over a REAL queue without a live game clock.</summary>
        internal static bool Reorder(IList<GeoscapeViewStateSwitchRequest> pending,
                                     Func<GeoscapeViewStateSwitchRequest, uint> ordinalOf)
        {
            if (pending == null || pending.Count < 2) return false;
            int n = pending.Count;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            var snapshot = new GeoscapeViewStateSwitchRequest[n];
            for (int i = 0; i < n; i++) snapshot[i] = pending[i];

            Array.Sort(order, (a, b) =>
            {
                int c = Compare(snapshot[a].Priority, ordinalOf(snapshot[a]),
                                snapshot[b].Priority, ordinalOf(snapshot[b]));
                return c != 0 ? c : a.CompareTo(b); // stability: never let Array.Sort invent an order
            });

            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (order[i] != i) changed = true;
                pending[i] = snapshot[order[i]];
            }
            return changed;
        }

        /// <summary>The drain gate: false = HOLD this frame (the game's own <c>Update</c> retries next
        /// frame), true = the game pops its head as it always did. Everything this reads is local.</summary>
        internal static bool ReadyToDequeue(GeoscapeViewSwitchQuery query)
        {
            try
            {
                // Co-op only, same argument as the rank prefix: a solo player has nobody to agree with, and
                // holding their windows for 150 ms would be an unrequested change to vanilla.
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return true;
                if (query == null || RequestsField == null || CurrentField == null)
                {
                    if (!_bindLogged)
                    {
                        _bindLogged = true;
                        Debug.LogError("[MP][windows] queue fields did not bind — modal ORDER falls back to " +
                                       "local arrival order and peers can show the same two windows in " +
                                       "opposite sequences");
                    }
                    return true;
                }
                if (CurrentField.GetValue(query) != null) return true;   // a switch is in flight; native early-returns
                if (!(RequestsField.GetValue(query) is IList<GeoscapeViewStateSwitchRequest> pending) ||
                    pending.Count == 0) return true;

                // BOUNDED LOCAL SETTLE, measured from when WE queued the head. A later arrival never
                // extends it — the head's queue time is fixed — so the hold is one SettleSeconds, once.
                if (!SettleExpired(QueuedAt(pending[0]), Time.realtimeSinceStartup)) return false;

                if (Reorder(pending, OrdinalOf))
                    Debug.Log("[MP][windows] settled queue re-ordered by rail ordinal — next window is " +
                              pending[0].State?.GetType().Name + " (ordinal " + OrdinalOf(pending[0]) + ")");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][windows] order gate failed — draining unordered rather than stalling " +
                               "the queue: " + ex);
                return true;   // a broken gate must never be able to hold a window forever
            }
        }

        /// <summary>An UNSTAMPED request sorts first among its priority peers and is never held: it was
        /// restored from the save (<c>RestoreData</c> bypasses <c>QueryStateSwitch</c>), i.e. it is older
        /// than anything this session minted.</summary>
        private static uint OrdinalOf(GeoscapeViewStateSwitchRequest request) =>
            request != null && _stamps.TryGetValue(request, out var s) ? s.Ordinal : 0u;

        private static float QueuedAt(GeoscapeViewStateSwitchRequest request) =>
            request != null && _stamps.TryGetValue(request, out var s) ? s.QueuedAt : float.NegativeInfinity;

        /// <summary>THE SETTLE + REORDER, at the game's own single drain. A prefix returning false skips a
        /// method whose body is a dequeue and a push — there is no side effect to lose by not running it,
        /// and <c>GeoscapeView.Update</c>:1358 calls it again next frame.</summary>
        [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), nameof(GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch))]
        internal static class QueueSettlePatch
        {
            private static bool Prefix(GeoscapeViewSwitchQuery __instance) => ReadyToDequeue(__instance);
        }
    }
}
