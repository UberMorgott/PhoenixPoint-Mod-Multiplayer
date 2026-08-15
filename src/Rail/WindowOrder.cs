using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PhoenixPoint.Geoscape.View;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// MODAL PRESENTATION ORDER: THE CLIENT RECONCILES TO THE HOST'S PUBLISHED ORDER AND NEVER SORTS.
    ///
    /// THE REPORT (2026-08-05, still live 2026-08-15): a research completion and a geoscape event left the
    /// host together; the clients received them 363 ms apart and showed them in the opposite order. Every
    /// peer's window CONTENTS were right — only the sequence differed.
    ///
    /// WHAT USED TO BE HERE AND WHY IT IS GONE (§A.7, L522). This class minted a client-side order KEY
    /// (<c>RailOrdinal</c>), held the drain for a 150 ms SETTLE and then RE-SORTED the pending list by
    /// (priority, ordinal). Two order keys on one stream IS the bug: the settle was shorter than the
    /// measured skew by construction, and the line it would have logged — `settled queue re-ordered by
    /// rail ordinal` — appears ZERO times in all three complete logs of the 2026-08-15 session. So the
    /// comparator, the re-sort, the settle and the per-request stamp are deleted outright rather than
    /// tuned.
    ///
    /// WHAT REPLACES IT is <see cref="WindowJournal"/>: ONE append-only, host-ordered stream of positions
    /// (§A.1). The drain gate below asks the journal head "is the next window this one?" and lets the game
    /// pop its own head only when the local queue agrees. No key is invented here, nothing is compared and
    /// nothing is re-sorted.
    ///
    /// WHY THIS CAN NEVER BLOCK (P13). A head this peer has no native raise for yet holds the drain for a
    /// bounded, ARMED interval that ends by itself (<see cref="WindowGap"/>) — never for an ACTION by
    /// another human. The authoritative resolution of a gap stays the host-minted void of §A.5.
    ///
    /// WHAT STAYS: the open-screen hold (a review window waits for the map), the staleness prune, and the
    /// rank table — <c>ReplenishSync.RankFor</c> is a DEVELOPER decision ACROSS priorities and the journal
    /// is a FACT about what the host raised first; neither can express the other.
    /// </summary>
    internal static class WindowOrder
    {
        /// <summary>ONE bind of the pending list, shared with <c>WindowQueueSync.AnswerQueued</c> — a second
        /// <c>AccessTools.Field</c> for the same private list is a second thing to drift when the game
        /// renames it, and the two consumers are two halves of the same rule (this one HOLDS a window in
        /// that list, the other has to be able to ANSWER one that is sitting in it).</summary>
        internal static readonly FieldInfo RequestsField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_viewStateSwitchRequests"); // GeoscapeViewSwitchQuery.cs:15
        private static readonly FieldInfo CurrentField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_currentStateSwitchRequest"); // :17
        /// <summary>The view the query belongs to (<c>GeoscapeViewSwitchQuery.cs:13</c>, assigned in the
        /// ctor at :22 and readonly) — see <see cref="CurrentViewStateOf"/> for why the gate must read it
        /// rather than look the geoscape up globally.</summary>
        private static readonly FieldInfo ViewField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_view");

        // THE DURABLE BINDING LIVES IN WindowQueueSync NOW (L523). It is answer-once bookkeeping — which
        // occurrence a native request carries — and it was only ever HERE because DurablePriorityHead, the
        // second ordering system, wanted to sort by it. The ordering class must not know an occurrence
        // exists; the class that owns the carrier leases does.

        internal static GeoscapeViewStateSwitchRequest CurrentRequest(GeoscapeViewSwitchQuery query) =>
            query == null ? null : CurrentField.GetValue(query) as GeoscapeViewStateSwitchRequest;

        private static bool _bindLogged;

        /// <summary>Log-once-per-screen for the hold below — a queue that is deliberately not draining must
        /// still say so, but once, not every frame the player spends in the research tree.</summary>
        private static readonly HashSet<string> _heldOnScreen = new HashSet<string>(StringComparer.Ordinal);

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
            // The post-mission resupply screen joined the transition band on 2026-08-10 when
            // ReplenishSync.ReplenishRank went from 20 to int.MaxValue (owner: "первым после миссии"). The
            // rank decides ORDER; it must not silently also decide the YANK, and the 2026-08-07 ruling that
            // a review window waits for the map covers this screen by name. Without this line the promotion
            // would pull a player out of the research tree the moment somebody else's squad came home.
            "UIStateReplenish",
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
        /// <summary>
        /// THE SCREEN THIS QUERY IS ABOUT — ASKED OF THE QUERY'S OWN VIEW, NEVER LOOKED UP GLOBALLY.
        ///
        /// THE REPORT (client, 2026-08-14, multiplayer.log one clock): the player was in the recruits
        /// screen from 23:55:30 (`SetGeoRosterControls`) with no navigation of any kind afterwards; the
        /// host's event window arrived at 23:55:58.631 and this gate HELD it correctly at :58.647
        /// (`queue HELD while UIStateRosterRecruits is open`) — and then at 23:56:02.207 the window opened
        /// anyway, straight onto that screen (`SetGeoscapeEventControls`, with no map control set logged
        /// between the two, i.e. the player never left). Nothing else can produce that: the head was
        /// priority 0, no `order gate failed` line was written, and the hold had already proved
        /// `_currentStateSwitchRequest` was null. The one remaining input is the one this method replaces —
        /// <c>GenericApplier.GeoLevel()?.View?.CurrentViewState</c> read NULL for a single frame, and L163's
        /// own (correct) rule that a null state DRAINS then let the window through. ONE frame is enough:
        /// the push is permanent.
        ///
        /// THE GATE MUST ASK THE OBJECT IT IS GATING. <c>ProcessQueriedStateSwitch</c> runs from
        /// <c>GeoscapeView.Update</c>:1358, so the view that owns this query is alive by construction
        /// whenever this is called, whatever the global level lookup says while a level swap, a curtain or
        /// a save transfer is in flight. Reading <c>_view</c> makes "no current state" mean what L163 says
        /// it means — the state stack is genuinely empty — instead of "the mod could not find the geoscape
        /// this frame". The predicate is untouched; the DEFECT WAS THE READ, and it was a hole under every
        /// window family at once, which is why it is fixed here and not per family.
        ///
        /// The global lookup stays as the fallback for a game rename: a null <c>ViewField</c> must not make
        /// the gate blind, and the old behaviour is what it degrades to.
        /// </summary>
        internal static Type CurrentViewStateOf(GeoscapeViewSwitchQuery query)
        {
            // ReferenceEquals, NOT `== null`: GeoscapeView is a UnityEngine.Object, whose overloaded
            // operator reports "null" for a live managed reference whose native half is not there — which
            // is every view this method is handed outside a player, and would send the read straight back
            // to the global lookup this fix exists to leave. The state stack is a managed field and reads
            // the same either way, and the whole gate runs inside ReadyToDequeue's catch.
            var view = query == null || ViewField == null ? null : ViewField.GetValue(query) as GeoscapeView;
            if (ReferenceEquals(view, null)) view = GenericApplier.GeoLevel()?.View;
            return ReferenceEquals(view, null) ? null : view.CurrentViewState?.GetType();
        }

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
        internal static bool HoldsForOpenScreen(int priority, Type queuedState, Type currentViewState) =>
            (priority < TransitionPriority ||
             (queuedState != null && HeldTransitionStates.Contains(queuedState.Name))) &&
            currentViewState != null &&
            !MapStates.Contains(currentViewState.Name);

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
                        MpLog.Log("[MP][windows] queue DROPS " + gone + " on its way to the screen — that " +
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
                        MpLog.LogError("[MP][windows] queue fields did not bind — modal ORDER falls back to " +
                                       "local arrival order and peers can show the same two windows in " +
                                       "opposite sequences");
                    }
                    return true;
                }
                var currentRequest = CurrentField.GetValue(query) as GeoscapeViewStateSwitchRequest;
                // A switch is in flight; native early-returns. Nothing preempts it: the journal position is
                // the one order, and a window already on screen is not overtaken by a later one (L523).
                if (currentRequest != null) return true;
                if (WindowQueueSync.TryDurableResume(query)) return false;
                if (!(RequestsField.GetValue(query) is IList<GeoscapeViewStateSwitchRequest> pending) ||
                    pending.Count == 0) return true;

                // THE OPEN-SCREEN HOLD (see HoldsForOpenScreen). Asked of the head, because the head is what
                // the game is about to push; a transition further down the queue is not in front of anybody.
                // ponytail: an exempt answer window (NeverHeldAnswerStates) still waits when a HELD window
                // is in front of it — the native drain only ever pops index 0, so skipping past it means
                // reordering, and reordering on a LOCAL condition (which screen this peer has open) is the
                // one thing this class exists to avoid. Promote it to the head only if a session shows an
                // answer window queued behind a held one; the measured defect was the head itself.
                var current = CurrentViewStateOf(query);
                var head = pending[0].State;
                if (HoldsHead(pending[0], current))
                {
                    if (_heldOnScreen.Add(current.Name + "/" + (head == null ? "?" : head.GetType().Name)))
                        MpLog.Log("[MP][windows] queue HELD while " + current.Name + " is open — " +
                                  (head == null ? "a window" : head.GetType().Name) + " is reviewed on the " +
                                  "geoscape, not on top of a screen the player opened. It drains as soon as " +
                                  "this peer is back on the map (logged once per screen/window pair).");
                    return false;
                }

                // THE HOST'S ORDER, AND NOTHING ELSE. The journal head is the next window every peer owes
                // the player; this peer drains only when its own native queue's head IS that window. No
                // key is invented here, nothing is compared and nothing is re-sorted (L522).
                var journalHead = WindowJournal.PeekHead();
                if (journalHead == null) return true;   // nothing journalled: the native queue drains as vanilla
                var localHead = pending[0].State == null ? null : pending[0].State.GetType().Name;
                if (string.Equals(localHead, journalHead.Family, StringComparison.Ordinal))
                {
                    // READ ⇒ DELETED (§A.2). The drain IS the read: the game pops this window the moment we
                    // return true, so spending the cursor anywhere else would leave the head naming a window
                    // already on screen and hold every later one until its gap timer expired.
                    JournalEntry read;
                    WindowJournal.TryRead(out read);
                    WindowGap.Forget(journalHead.Pos);
                    return true;
                }
                // NOT A WAIT ON A HUMAN (§7.6). The armed interval ends by itself and the authoritative
                // resolution is the host-minted void of §A.5; nothing here waits for a peer to ACT.
                return WindowGap.SelfReleased(journalHead.Pos);
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][windows] order gate failed — draining unordered rather than stalling " +
                               "the queue: " + ex);
                return true;   // a broken gate must never be able to hold a window forever
            }
        }

        /// <summary>THE RECONCILE, at the game's own single drain. A prefix returning false skips a
        /// method whose body is a dequeue and a push — there is no side effect to lose by not running it,
        /// and <c>GeoscapeView.Update</c>:1358 calls it again next frame.</summary>
        [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), nameof(GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch))]
        internal static class QueueSettlePatch
        {
            private static bool Prefix(GeoscapeViewSwitchQuery __instance) => ReadyToDequeue(__instance);
            private static void Postfix(GeoscapeViewSwitchQuery __instance) =>
                WindowQueueSync.ConfirmDurableNativeOpen(__instance);
        }

        /// <summary>
        /// THE RESTORE BYPASS, CLOSED (§A.8, L520 arm c). <c>GeoscapeViewSwitchQuery.RestoreData</c>
        /// (<c>GeoscapeViewSwitchQuery.cs:37-55</c>) rebuilds <c>_viewStateSwitchRequests</c> by ADDING to
        /// the list directly — it never calls <c>QueryStateSwitch</c>, so the one mint seam
        /// (<c>GeoWindowCoverageGate</c>) never saw a restored window. A restored request therefore carried
        /// no journal position at all, which meant it presented in whatever local order each peer happened
        /// to rebuild — a second order by another name, and one that survives a save/load across EVERY
        /// battle (<c>GeoscapeView.GetStateSwitchInstanceData</c>:1298-1300 -> <c>RestoreData</c>:37-55,
        /// replayed at <c>GeoscapeView.cs</c>:349). It now claims a position like anything else.
        ///
        /// A POSTFIX, so the native restore happens exactly as it always did whatever this does, and so the
        /// prefix that drops resolved subjects (<c>WindowQueueSync.RestoreDropsResolvedSubjects</c>, the
        /// other patch on this same method) has already run — only the windows that SURVIVED restore claim
        /// a position, and a dropped one never enters the journal.
        ///
        /// THE PENDING IS CLEARED AT THE END. The mint is handed to a family's publisher through
        /// <c>WindowJournal.SetHostPending</c>/<c>TakeHostPending</c>, and <c>HostBroadcastQueued</c>
        /// publishes only the families it has a payload shape for. A position no publisher consumed must
        /// not be left sitting for the NEXT unrelated raise to ship as its own — that is a duplicate entry,
        /// exactly what the take-clears-it rule exists to prevent.
        ///
        /// NO QUORUM, NOTHING WAITS: minting is a local counter increment on the host and a local append.
        /// </summary>
        [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), nameof(GeoscapeViewSwitchQuery.RestoreData))]
        internal static class RestoreClaimsPositions
        {
            private static void Postfix(GeoscapeViewSwitchQuery __instance)
            {
                if (!EventPopup.InSession || RequestsField == null) return;
                try
                {
                    if (!(RequestsField.GetValue(__instance) is IList<GeoscapeViewStateSwitchRequest> restored))
                        return;
                    for (int i = 0; i < restored.Count; i++)
                    {
                        if (!GeoModalMirror.HostMayPublish()) return;   // clients receive, they never mint
                        var request = restored[i];
                        uint pos = WindowJournal.MintHostPosition();
                        string family = request?.State?.GetType().Name ?? "<unknown>";
                        WindowJournal.SetHostPending(pos, family);
                        WindowJournal.Append(pos, family, null);
                        GeoModalMirror.HostBroadcastQueued(request);
                    }
                    WindowJournal.TakeHostPending(out uint _, out string _);
                }
                catch (Exception ex)
                {
                    MpLog.LogError("[MP][windows] restore position claim threw — the restored queue keeps " +
                                   "its windows and simply has no journal order: " + ex);
                }
            }
        }
    }
}
