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

        /// <summary>ONE bind of the pending list, shared with <c>WindowQueueSync.AnswerQueued</c> — a second
        /// <c>AccessTools.Field</c> for the same private list is a second thing to drift when the game
        /// renames it, and the two consumers are two halves of the same rule (this one HOLDS a window in
        /// that list, the other has to be able to ANSWER one that is sitting in it).</summary>
        internal static readonly FieldInfo RequestsField =
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

        /// <summary>Priority at or above which a queued request is a TRANSITION, not a notification: the
        /// mission-outcome modal (<c>int.MaxValue</c>), a cutscene (100, <c>ToCutsceneState</c>), the
        /// game-over state — and the squad screen (<c>int.MaxValue</c>, <c>ToDeploymentState</c>:596), which
        /// is the ONE exception carved out below. Under it sits the review family this hold was written for —
        /// the event windows (0 / 10 / 15, <c>GeoscapeView.OnGeoscapeEventRaised</c>:2044-2059) and the
        /// resupply screen (<see cref="ReplenishSync.ReplenishRank"/> 20). The game's own knob, so this adds
        /// no second axis.</summary>
        internal const int TransitionPriority = 100;

        /// <summary>THE DEPLOYMENT WINDOW IS IN THE HISTORY LIKE EVERY OTHER WINDOW (owner's ruling,
        /// 2026-08-07). <c>GeoscapeView.ToDeploymentState</c>:596 queues <c>UIStateRosterDeployment</c> at
        /// <c>int.MaxValue</c>, so the priority band alone exempted it from the hold and ANOTHER PEER'S
        /// press of "start mission" pulled this peer out of the research screen he was working in and
        /// dropped him on the squad screen. Priority is the game's answer to "which of two queued windows
        /// goes first"; it was never an answer to "may this window take a screen the player opened", and
        /// <see cref="HoldsForOpenScreen"/> reading it for both is what made the yank legal.
        ///
        /// DECLARED BY STATE NAME AND ONLY FOR THIS ONE STATE, because the other three transitions must
        /// still never be held: a cutscene and the game-over state are the game ENDING a thing rather than
        /// offering one, and the mission-outcome modal follows a battle, i.e. arrives on a peer whose view
        /// state is <c>UIStateInitial</c> — a MAP state — where the hold does not engage at all.
        ///
        /// TFTV-SAFE WITHOUT NAMING TFTV, the same argument <c>MissionSync.AlreadyHeadedForDeployment</c>
        /// already rests on: TFTV adds no view state of its own here, it decorates <c>UIStateRosterDeployment</c>.
        ///
        /// IT COSTS THIS PEER NOTHING HE DOES NOT ALREADY LOSE. Holding the squad screen does not hold the
        /// BATTLE: whoever presses Deploy launches for everyone through the host's own save transfer, which
        /// curtains every peer regardless of what its window queue holds. So a peer who never leaves his
        /// screen is carried into the battle exactly as before — he simply is not yanked onto a screen he
        /// did not ask for in order to get there.</summary>
        private static readonly HashSet<string> HeldTransitionStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "UIStateRosterDeployment",   // the pre-mission squad/deployment screen (ToDeploymentState:596)
        };

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
        /// WHAT THIS CLASS SAID ABOUT THE 0xB9 ADVANCE WAS WRONG, AND IT COST A WINDOW. The paragraph that
        /// stood here ruled "a peer who walks away inside the research screen keeps their own
        /// <c>_currentStateSwitchRequest</c> null, so a 0xB9 advance has nothing to answer" an acceptable
        /// standing exposure. It is not the same as an AFK peer: the held window is one the HOST RAISED to
        /// every peer, so another peer's answer really does name it, and refusing that answer left the
        /// mission-brief modal unanswered on the host FOREVER — no <c>ModalResultCallback</c>, no
        /// <c>LaunchMission</c>, no deployment screen at all (the 2026-08-07 ambush report). The advance now
        /// reaches a QUEUED window as well as the current one (<c>WindowQueueSync.AnswerQueued</c>), which is
        /// what makes holding this family safe rather than merely quiet.
        /// ponytail: a peer who walks away still keeps his OWN queue held, which is the point of the feature.
        /// Give the hold a ceiling only if a live session shows an idle peer in a menu costing another peer
        /// something the advance cannot already deliver.</summary>
        /// <summary>PURE, and RailCheck L175 executes it over a REAL <c>GeoscapeViewStateSwitchRequest</c>.
        /// The reason this one-line wrapper exists rather than the extraction sitting inline in
        /// <see cref="ReadyToDequeue"/>: the whole defect was that the drain gate asked the hold about the
        /// PRIORITY and not about the WINDOW, and a law can only execute that if the extraction — request →
        /// (priority, state type) — is on the pure side of the seam. Inline, it is only assertable by IL,
        /// and IL cannot tell "reads the head's type" from "reads the head's type for the log line".</summary>
        internal static bool HoldsHead(GeoscapeViewStateSwitchRequest head, Type currentViewState) =>
            head != null &&
            HoldsForOpenScreen(head.Priority, head.State == null ? null : head.State.GetType(), currentViewState);

        /// <summary>WINDOWS THAT ARE AN ANSWER, NOT AN INTERRUPTION — never held for an open screen.
        ///
        /// THE REPORT (2026-08-08), measured end to end in multiplayer.log:3283-3291 on one clock: the
        /// client's hire intent went at 20:36:32.146 (personnel hire S#89), the host replayed it, the panel
        /// re-entered at :32.298, the character arrived structurally at :32.719 ('U#9'), the destination
        /// popup was raised at :32.721 — and at :32.747 `queue HELD while UIStateHavenDetailsScreen is
        /// open`. It drained 71 SECONDS later, when the player finally left the view. The player bought a
        /// soldier and the game simply did not ask where to send them.
        ///
        /// THE HOLD'S OWN RATIONALE DOES NOT REACH THIS WINDOW. <see cref="HoldsForOpenScreen"/> exists so
        /// that an UNRELATED host event is not pushed on top of a screen the player deliberately opened
        /// (see the long note above). <c>UIStateAssetDeployment</c> is the opposite of unrelated: every one
        /// of its three native raise sites is an ACQUISITION completing — a recruit hired
        /// (GeoPhoenixFaction.cs:708), an aircraft manufactured (VehicleItemDef.cs:47), a ground vehicle
        /// manufactured (GroundVehicleItemDef.cs:48) — each reached through
        /// <c>GeoscapeView.PrepareDeployAsset:1308-1326</c>, which already gates on
        /// <c>faction == _context.ViewerFaction</c>. It is the game asking a question the purchase raised;
        /// holding it strands the asset with nowhere to go and no visible reason.
        ///
        /// SHOWN ON EVERY PEER, IMMEDIATELY (explicit product decision, and it may differ from vanilla):
        /// the co-op faction is shared, so the asset every peer is being asked about is one they all own.
        /// This is not a quorum and cannot become one — nothing waits for anybody to answer; each peer's
        /// own queue simply stops swallowing the question.
        ///
        /// DELIBERATELY A NAMED SET AND NOT A WEAKENING. Everything else keeps the hold exactly as it was:
        /// declared by NAME so that a new window is held by default, which is the recoverable direction.</summary>
        private static readonly HashSet<string> NeverHeldAnswerStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "UIStateAssetDeployment",   // "where do you want this?" — the direct answer to an acquisition
        };

        internal static bool HoldsForOpenScreen(int priority, Type queuedState, Type currentViewState) =>
            (priority < TransitionPriority ||
             (queuedState != null && HeldTransitionStates.Contains(queuedState.Name))) &&
            !(queuedState != null && NeverHeldAnswerStates.Contains(queuedState.Name)) &&
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

        /// <summary>
        /// A QUEUED WINDOW WHOSE SUBJECT HAS ALREADY RESOLVED IS NEVER SERVED. Pure, and RailCheck L260
        /// executes it over a REAL <c>IList&lt;GeoscapeViewStateSwitchRequest&gt;</c>.
        ///
        /// THE REPORT (2026-08-08): returning from a battle, the host was met by the START-MISSION window for
        /// the mission it had just finished, and the clients by a different set. Both halves are ONE cause.
        /// The mod already filtered resolved subjects — but only inside
        /// <c>RestoreDropsResolvedSubjects</c>, i.e. ONCE, while <c>RestoreData</c> rebuilds the list. The
        /// list is not consumed there: <c>GeoscapeViewSwitchQuery</c>:110-118 pops entries one at a time out
        /// of <c>ProcessQueriedStateSwitch</c>, whenever the previous switch finishes. Measured on the host,
        /// one clock: restore said "1 kept — 0 dropped (subject already resolved)" at 02:49:11.734
        /// (multiplayer.log:3378) and the mission resolved at 02:49:12.015 (`activeMission=none`) — the check
        /// ran 281 ms before the fact it was checking. So the window was VALID when asked and STALE when
        /// shown, and no second question was ever put to it.
        ///
        /// THE FIX IS THE MOMENT, NOT THE WINDOW. Validity is asked at SERVE time, on every entry, every
        /// frame the game pumps its own drain — so a subject that resolves at ANY point between queueing and
        /// showing is caught, whatever queued the window and whenever. This is deliberately not
        /// `if (mission-brief && completed) drop`: nothing here names a <c>ModalType</c>, a window kind or a
        /// mission family. It reads the SUBJECT the request's own state carries
        /// (<c>RestoreDropsResolvedSubjects.ResolvedSubjectName</c> field-walks for a <c>GeoMission</c>) and asks the
        /// GAME's verdict on it (<c>UIStateInitial.EnterState</c>:102's own predicate).
        ///
        /// WHAT IT REACHES THAT THE RESTORE FILTER NEVER COULD. Every window in the game is queued through
        /// <c>QueryStateSwitch</c> and served through this one drain, so live raises ride it as well as
        /// restored ones — including <c>UIStateRosterDeployment</c>, the "start mission" squad screen, which
        /// is NOT an <c>IGeoscapeRestorableViewState</c> and therefore never appears in a save's queue at
        /// all. It also closes the REPLAY shape of the same bug at the other boundary: a held one-shot
        /// (<see cref="EventPopup"/>'s <c>_held</c>, L189) is drained back into this queue, and the
        /// `PROG_NJ0_MISS 'REPLAYED'` of 2026-08-08 01:11:25.70 was a held entry replayed blindly. Held is
        /// correct; released-without-revalidating is not, and release lands here.
        ///
        /// IT DROPS NOTHING IT CANNOT NAME. A request whose state carries no mission returns null and is left
        /// exactly as it was — which is why the post-mission REWARD windows are untouched and not by
        /// exemption: none of the event states holds a <c>GeoMission</c> (see
        /// <c>RestoreDropsResolvedSubjects.SubjectMission</c>), and the outcome modal is not in this queue at all, it is
        /// opened by <c>UIStateInitial.EnterState</c>:112 after the queue is rebuilt (L117).
        ///
        /// NO QUORUM AND NO PEER. Every value read is this peer's own heap: the request list, the state
        /// object, the mission on it. No roster, no message, no acknowledgement — each peer decides that a
        /// window is dead from state it already holds, which is exactly why host and clients converge instead
        /// of negotiating.
        ///
        /// Returns one description per dropped entry so the CALLER logs — a dropped window must never be
        /// silent, and this stays free of <c>UnityEngine.Debug</c> so the law can execute it outside a
        /// player.</summary>
        internal static List<string> DropResolvedSubjects(IList<GeoscapeViewStateSwitchRequest> pending,
                                                          Func<object, string> resolvedSubjectName)
        {
            var dropped = new List<string>();
            if (pending == null || resolvedSubjectName == null) return dropped;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var request = pending[i];
                var subject = resolvedSubjectName(request == null ? null : request.State);
                if (subject == null) continue;
                pending.RemoveAt(i);
                dropped.Add((request.State == null ? "a window" : request.State.GetType().Name) +
                            " (mission '" + subject + "')");
            }
            return dropped;
        }

        /// <summary>The drain gate: false = HOLD this frame (the game's own <c>Update</c> retries next
        /// frame), true = the game pops its head as it always did. Everything this reads is local.</summary>
        internal static bool ReadyToDequeue(GeoscapeViewSwitchQuery query)
        {
            try
            {
                var queued = query == null || RequestsField == null
                    ? null
                    : RequestsField.GetValue(query) as IList<GeoscapeViewStateSwitchRequest>;

                // THE STALENESS PRUNE, BEFORE EVERY OTHER GATE AND IN SOLO TOO. Not co-op-gated, because
                // the restore-time half it completes is not either: a window for a finished mission is dead
                // for the same reason with or without a session, and gating one moment and not the other is
                // how the two answers drift apart. It also has to run while a switch is IN FLIGHT (below
                // returns early on that) — the entry behind the open window is exactly the one that goes
                // stale while the player reads.
                if (queued != null && queued.Count > 0)
                    foreach (var gone in DropResolvedSubjects(queued, RestoreDropsResolvedSubjects.ResolvedSubjectName))
                        Debug.Log("[MP][windows] queue DROPS " + gone + " on its way to the screen — that " +
                                  "mission has already resolved, so the offer this window carried is dead. " +
                                  "It is not being shown for a battle that is already over. (The restore " +
                                  "filter cannot catch this one: it runs while the queue is rebuilt, which " +
                                  "on a mission return is before the mission is marked resolved.)");

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
                // ponytail: an exempt answer window (NeverHeldAnswerStates) still waits when a HELD window
                // is in front of it — the native drain only ever pops index 0, so skipping past it means
                // reordering, and reordering on a LOCAL condition (which screen this peer has open) is the
                // one thing this class exists to avoid. Promote it to the head only if a session shows an
                // answer window queued behind a held one; the measured defect was the head itself.
                var current = GeoLevel()?.View?.CurrentViewState;
                var head = pending[0].State;
                if (HoldsHead(pending[0], current == null ? null : current.GetType()))
                {
                    if (_heldOnScreen.Add(current.GetType().Name + "/" + (head == null ? "?" : head.GetType().Name)))
                        Debug.Log("[MP][windows] queue HELD while " + current.GetType().Name + " is open — " +
                                  (head == null ? "a window" : head.GetType().Name) + " is reviewed on the " +
                                  "geoscape, not on top of a screen the player opened. It drains as soon as " +
                                  "this peer is back on the map (logged once per screen/window pair).");
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
