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

        /// <summary>Log-once-per-screen for the hold below — a queue that is deliberately not draining must
        /// still say so, but once, not every frame the player spends in the research tree.</summary>
        private static readonly HashSet<string> _heldOnScreen = new HashSet<string>(StringComparer.Ordinal);

        private static PhoenixPoint.Geoscape.Levels.GeoLevelController GeoLevel() =>
            Base.Core.GameUtl.CurrentLevel() == null
                ? null
                : Base.Core.GameUtl.CurrentLevel().GetComponent<PhoenixPoint.Geoscape.Levels.GeoLevelController>();

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

        // ─── THE OPEN-SCREEN HOLD: a notification waits for the map, it does not take the screen ───

        /// <summary>Priority at or above which a queued request is a TRANSITION, not a notification, and is
        /// never held: the squad screen (<c>int.MaxValue</c>, L144), the mission-outcome modal
        /// (<c>int.MaxValue</c>), a cutscene (100, <c>ToCutsceneState</c>) and the game-over state. Below it
        /// sits the review family this hold is for — the event windows (0 / 10 / 15,
        /// <c>GeoscapeView.OnGeoscapeEventRaised</c>:2044-2059) and the resupply screen
        /// (<see cref="ReplenishSync.ReplenishRank"/> 20). The game's own knob, so this adds no second axis.</summary>
        internal const int TransitionPriority = 100;

        /// <summary>THE GEOSCAPE MAP: the only view states in which the player is "on the geoscape". Everything
        /// else under <c>PhoenixPoint.Geoscape.View.ViewStates</c> is either a screen the player OPENED
        /// (research, manufacturing, base layout, roster, diplomacy, interception, the log, the options) or a
        /// window the queue itself put up — and a window that is up is already covered one step earlier, by
        /// the game's own <c>_currentStateSwitchRequest != null</c> early-out.
        ///
        /// DECLARED AS THE MAP AND NOT AS THE SCREENS, so an unknown state HOLDS rather than interrupts:
        /// holding is recoverable by the player's next click, interrupting is not recoverable at all.</summary>
        private static readonly HashSet<string> MapStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "UIStateNothingSelected",   // the map with nothing selected — owns the time-control module (:99)
            "UIStateVehicleSelected",   // the map with an aircraft selected — likewise (:168)
            "UIStateInitial",           // transient: switches to one of the two above in its own EnterState
            "UIStateLoading",           // no player and no screen to protect
        };

        /// <summary>PURE, and RailCheck L161 executes it. TRUE = keep this request in the queue this frame.
        ///
        /// WHY THE HOLD EXISTS AT ALL, and why it is a co-op-only rule. In vanilla every full-screen section
        /// STOPS THE CLOCK on entry — <c>UIModuleGeoSectionBar</c>:119/:135/:148/:160,
        /// <c>UIStateResearch</c>:22, <c>UIStateManufacturing</c>:51, <c>UIStateDiplomacy</c>:27,
        /// <c>UIStatePhoenixBaseLayout</c>:43, <c>UIStateGeoscapeLog</c>:18 — so a solo player's sim raises
        /// nothing while they read and there is never anything to push on top of them. Co-op deleted that
        /// guarantee ON PURPOSE (2026-08-04 pause rework, <see cref="PauseHold"/>): resume is unconditional
        /// from any peer, so somebody else's play button restarts this peer's sim while they are inside a
        /// screen, and <c>ProcessQueriedStateSwitch</c>:71 then pushes every event that fires on top of it.
        /// The clock used to hold the queue; nothing replaced it. This does — LOCALLY.
        ///
        /// IT WAITS ON NO HUMAN BUT THE ONE HOLDING IT. The predicate reads this peer's own current view
        /// state and the request's own priority: no peer, no roster, no message, no membership. It is
        /// released by the local player's own next navigation, which is the same click they were going to
        /// make anyway, and it is invisible to every other peer's queue.
        /// ponytail: a peer who walks away inside the research screen keeps their OWN notifications queued
        /// (and, on a host, keeps their own <c>_currentStateSwitchRequest</c> null, so a 0xB9 advance has
        /// nothing to answer). That is the same standing exposure an AFK peer already has with a window open,
        /// and the 0xB9 advance cannot help in either case. Give the hold a ceiling only if a live session
        /// shows an idle peer in a menu actually costing another peer something.</summary>
        internal static bool HoldsForOpenScreen(int priority, Type currentViewState) =>
            priority < TransitionPriority &&
            currentViewState != null &&
            !MapStates.Contains(currentViewState.Name);

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

                // THE OPEN-SCREEN HOLD (see HoldsForOpenScreen). Asked of the head, because the head is what
                // the game is about to push; a transition further down the queue is not in front of anybody.
                var current = GeoLevel()?.View?.CurrentViewState;
                if (HoldsForOpenScreen(pending[0].Priority, current == null ? null : current.GetType()))
                {
                    if (_heldOnScreen.Add(current.GetType().Name))
                        Debug.Log("[MP][windows] queue HELD while " + current.GetType().Name + " is open — a " +
                                  "notification is reviewed on the geoscape, not on top of a screen the player " +
                                  "opened. It drains as soon as this peer is back on the map (logged once per " +
                                  "screen).");
                    return false;
                }

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
