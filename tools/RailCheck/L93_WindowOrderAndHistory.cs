using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RailCheck
{
    /// <summary>
    /// L93 — THE WINDOW QUEUE IS ORDERED BY THE HOST, AND A QUEUED WINDOW SURVIVES A BATTLE.
    ///
    /// THE REPORT (3 instances, 2026-08-04, DLL 843264 B @ 22:09:44). A mission ended; every peer pressed
    /// Return. The HOST came back showing an event window; both CLIENTS came back showing the resupply
    /// screen. Measured in the peer logs rather than argued, off the game's own <c>Queuerd state switch</c>
    /// line (<c>GeoscapeViewSwitchQuery</c>:83 — the typo is the game's):
    ///
    ///   HOST   Player.log:26726-26750, all inside ONE frame t=104905, in this order:
    ///            UIStateGeoscapeEvent (RE26, prio 0) · UIStateGeoscapeEvent (PROG_AN2_WIN, prio 0) ·
    ///            UIStateReplenish (prio 0)
    ///   CLIENT D:\PP-Instance2\Player.log:24161-24278:
    ///            UIStateReplenish (prio 0) at t=104321 — and ENTERED at t=104322, before the curtain lift —
    ///            then UIStateGeoscapeEvent RE26 seq=9 and PROG_AN2_WIN seq=10 at t=104424, 103 ms LATER.
    ///
    /// Same three windows, same three priorities, OPPOSITE order. The host runs unmodified native code on
    /// this path, so the host's order IS the native order: both events are triggered by the mission's own
    /// completion consequences INSIDE <c>UIStateInitial.EnterState</c> (:102-125 — the outcome modal at :112,
    /// <c>GeoCustomMission.TriggerOutcomeEncounter</c> at :108), and <c>QueueReplenishState</c> at :127 is the
    /// LAST line of that same batch. The client's own native raise is suppressed (which events fire is the
    /// host's decision) and replaced by the mirrored 0xB6 raise, which lands a burst later — after the
    /// client's own :127 has already queued AND drained the resupply screen.
    ///
    /// SO THE FALSE CLAIM IS NAMED AND IT IS OURS. <c>EventPopup</c>'s <c>Priority</c> doc says ties "need
    /// nothing extra: equal priorities append in insert order on both sides, and the client's inserts are the
    /// host's own order". The second half is false whenever a peer raises a window of its OWN between two
    /// mirrored raises — which is exactly what the post-mission arrival batch does, every mission. Priority
    /// cannot repair it (all three windows here were priority 0) and neither can a re-sort on the client: the
    /// client's own locally-raised windows carry no host ordering key at all.
    ///
    /// THE FIX IS NOT A HOST→CLIENT SIGNAL, because the DEVELOPER DECISION (2026-08-04) changed the target
    /// from "identical to native" to "THE RESUPPLY SCREEN COMES FIRST, on every peer including the host".
    /// That is a rule each peer applies to its own queue, so there is nothing to agree on and nothing to
    /// race: <c>ReplenishSync.RankFor</c> is a declared rank per window KIND, applied in a prefix on the
    /// game's own single queue entry point, and arm G EXECUTES it (a rank that does not clear the event
    /// family's ceiling of 15 decides nothing; a rank table that names the event family overreaches).
    /// History (arm H) is carried by re-holding the peer's own unanswered 0xB6 raises after
    /// <c>GeoscapeView.RestoreState</c>, replayed by the drain that already existed.
    ///
    /// THE SUB-SCREEN QUESTION, ANSWERED BY ARM D: there is no generic sub-screen criterion to derive because
    /// the game has no sub-screen concept. <c>ProcessQueriedStateSwitch</c>:71 pushes with
    /// <c>StateStackAction.PushOnTop</c> onto the single <c>_statesStack</c> — a window opens on top of
    /// whatever screen the peer is in, and <c>FinishCurrentStateSwitch</c>:119 returns to it via
    /// <c>SwitchToPreviousState</c>. Its ONE caller is <c>GeoscapeView.Update</c>:1355-1358, gated by
    /// <c>UpdateStateStack</c>, which the game clears only for LEVEL transitions (interception :1207, tactical
    /// launch :1405; restored :692/:1266/:1440/:1461) — never for screens. <c>_viewStateSwitchRequests</c>
    /// already IS the per-peer interrupt queue and it already drains on return, natively, for every screen.
    /// Arm D turns that answer red the day the game grows a sub-screen gate, instead of letting it rot.
    ///
    /// Falsify: make <c>QueryStateSwitch</c> append or sort instead of insert → <c>premise-priority-insert</c>;
    /// pop anything but the head → <c>premise-fifo-within-priority</c>; let a third method write
    /// <c>_currentStateSwitchRequest</c> → <c>premise-single-drain-gate</c>; give the drain a second caller or
    /// stop pushing on top → <c>premise-no-subscreen-gate</c>; move <c>QueueReplenishState</c> out of
    /// <c>UIStateInitial.EnterState</c> → <c>premise-arrival-batch-last</c>; drop
    /// <c>GeoLevelInstanceData.ViewStateSwitchQuery</c> or stop clearing in <c>RestoreData</c> →
    /// <c>premise-history-rides-the-save</c>; drop <c>UIStateReplenish</c> from the rank table, or rank it at
    /// or below 15 → <c>replenish-not-ranked</c>; add the event family to the table →
    /// <c>rank-table-overreaches</c>; delete <c>EventPopup.RequeueUnanswered</c> or the
    /// <c>RestoreState</c> postfix → <c>history-not-carried-across-mission</c>.
    /// </summary>
    internal static class L93_WindowOrderAndHistory
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var query = game.GetType("PhoenixPoint.Geoscape.View.GeoscapeViewSwitchQuery");
            var view = game.GetType("PhoenixPoint.Geoscape.View.GeoscapeView");
            var request = game.GetType("PhoenixPoint.Geoscape.View.GeoscapeViewStateSwitchRequest");
            var initial = game.GetType("PhoenixPoint.Geoscape.View.ViewStates.UIStateInitial");
            var instData = game.GetType("PhoenixPoint.Geoscape.GeoLevelInstanceData");
            var restorable = game.GetType("PhoenixPoint.Geoscape.View.IGeoscapeRestorableViewState");

            if (query == null || view == null)
            {
                yield return "L93 premise-gone: GeoscapeViewSwitchQuery / GeoscapeView no longer resolve — the " +
                             "window queue this law reasons about is not there to be checked, so every conclusion " +
                             "below is void rather than green.";
                yield break;
            }

            // ─── ARM A: ordering is a priority INSERT, never an append and never a sort ───
            // QueryStateSwitch:77-82 finds the first STRICTLY lower priority and inserts there. That is what makes
            // priority the only ordering knob the mirrored raises can ship, AND what makes equal priorities settle
            // by insert order. An Add would drop priority; a Sort would make ties non-deterministic across peers.
            var insert = query.GetMethod("QueryStateSwitch", AllMembers);
            if (insert == null)
                yield return "L93 premise-priority-insert: GeoscapeViewSwitchQuery.QueryStateSwitch is gone — the " +
                             "one entry point every window in the game is queued through no longer exists.";
            else if (!CallsNamed(insert, "Insert") || CallsNamed(insert, "Sort"))
                yield return "L93 premise-priority-insert: QueryStateSwitch no longer positions the request with a " +
                             "List.Insert (or it now sorts). Priority then stops being the ordering knob the " +
                             "0xB6/0xB7/0xBA raises ship, and equal-priority ties stop settling by insert order — " +
                             "both peers would order the same queue differently by construction.";

            // ─── ARM B: the pop is FIFO, so ties are decided by insert order and by NOTHING else ───
            var pop = query.GetMethod("GetNextQueriedStateSwitch", AllMembers);
            if (pop == null)
                yield return "L93 premise-fifo-within-priority: GetNextQueriedStateSwitch is gone — what decides " +
                             "which queued window shows next is no longer where this law looks.";
            else if (!CallsNamed(pop, "RemoveAt"))
                yield return "L93 premise-fifo-within-priority: GetNextQueriedStateSwitch no longer pops the head of " +
                             "_viewStateSwitchRequests. The tie-break is then something other than insert order and " +
                             "the measured host order this law records stops being reproducible from native code.";

            // ─── ARM C: one window at a time, and only an ANSWER advances it ───
            // Why an un-dismissed window on an idle peer wedges that peer's whole queue, and why 0xB9 exists.
            var current = query.GetField("_currentStateSwitchRequest", AllMembers);
            if (current == null)
                yield return "L93 premise-single-drain-gate: _currentStateSwitchRequest is gone — the " +
                             "one-window-at-a-time invariant WindowQueueSync's peer autonomy is built on cannot " +
                             "be checked.";
            else
            {
                var writers = query.GetMethods(AllMembers)
                                   .Where(m => WritesField(m, current))
                                   .Select(m => m.Name)
                                   .Distinct()
                                   .OrderBy(n => n, StringComparer.Ordinal)
                                   .ToArray();
                // Exactly two: ProcessQueriedStateSwitch takes the slot, FinishCurrentStateSwitch frees it.
                if (writers.Length != 2 ||
                    !writers.Contains("ProcessQueriedStateSwitch") ||
                    !writers.Contains("FinishCurrentStateSwitch"))
                    yield return "L93 premise-single-drain-gate: _currentStateSwitchRequest is now written by {" +
                                 string.Join(", ", writers) + "} instead of exactly ProcessQueriedStateSwitch + " +
                                 "FinishCurrentStateSwitch. A third writer means a window can be advanced by " +
                                 "something that is not an answer, and WindowQueueSync.HandleAdvance's safety " +
                                 "argument — only the named window INSTANCE is ever dismissed — no longer holds.";
            }

            // ─── ARM D: THE SUB-SCREEN ANSWER, made mechanical ───
            // One caller, gated by a LEVEL-transition flag, pushing on top of whatever screen the peer is in. If
            // any of the three stops holding, "the game has no sub-screen concept" must be re-derived BEFORE
            // anyone builds a per-peer interrupt queue on top of it.
            var drain = query.GetMethod("ProcessQueriedStateSwitch", AllMembers);
            var update = view.GetMethod("Update", AllMembers);
            if (drain == null)
                yield return "L93 premise-no-subscreen-gate: ProcessQueriedStateSwitch is gone — the drain this law " +
                             "declares un-gated by any screen no longer exists.";
            else
            {
                if (!CallsNamed(drain, "SwitchToState"))
                    yield return "L93 premise-no-subscreen-gate: ProcessQueriedStateSwitch no longer pushes through " +
                                 "StateStack.SwitchToState. A queued window may no longer open on top of the screen " +
                                 "the peer is in, which is the exact premise behind 'no sub-screen criterion exists, " +
                                 "because the game has no sub-screens'.";

                // Bounded to PhoenixPoint.Geoscape on purpose: the queue is a geoscape-only object and scanning the
                // whole of Assembly-CSharp would cost seconds for an answer that can only live here. A caller
                // appearing OUTSIDE that namespace would not be a second drain site, it would be a different game.
                var callers = SafeTypes(game)
                                  .Where(t => t.Namespace != null && t.Namespace.StartsWith("PhoenixPoint.Geoscape",
                                                                                            StringComparison.Ordinal))
                                  .SelectMany(SafeMethods)
                                  .Where(m => CallsNamed(m, "ProcessQueriedStateSwitch"))
                                  .Select(m => m.DeclaringType.Name + "." + m.Name)
                                  .Distinct()
                                  .OrderBy(n => n, StringComparer.Ordinal)
                                  .ToArray();
                if (callers.Length != 1 || callers[0] != "GeoscapeView.Update")
                    yield return "L93 premise-no-subscreen-gate: the window-queue drain is reached from {" +
                                 string.Join(", ", callers) + "} instead of GeoscapeView.Update alone. A second " +
                                 "drain site is exactly where a sub-screen gate would live, so the answer 'derive " +
                                 "nothing per-state, the game already drains everywhere' must be re-derived.";

                if (update == null || !ReadsMember(update, "UpdateStateStack"))
                    yield return "L93 premise-no-subscreen-gate: GeoscapeView.Update no longer reads UpdateStateStack " +
                                 "before draining. That flag is the game's ONLY native suppression of the queue, and " +
                                 "it means 'mid level transition', never 'in a sub-screen' — without it there is no " +
                                 "native gate left to point at.";
            }

            // ─── ARM E: the peer's OWN arrival batch is raised LAST, in one native method ───
            // The ordering premise the whole RCA rests on: on the host the mirrored raises come out of the
            // mission's completion consequences, and the peer's own windows come out of UIStateInitial:112-127
            // AFTER them. Move QueueReplenishState and the measured order stops being the native order.
            var enter = initial?.GetMethod("EnterState", AllMembers);
            if (enter == null)
                yield return "L93 premise-arrival-batch-last: UIStateInitial.EnterState is gone — the post-mission " +
                             "arrival batch (outcome modal :112, pandoran reveal :118/:122, resupply :127) is no " +
                             "longer where this law says a peer's own windows are raised.";
            else if (!CallsNamed(enter, "QueueReplenishState") || !CallsNamed(enter, "OpenModalPersistent"))
                yield return "L93 premise-arrival-batch-last: UIStateInitial.EnterState no longer raises BOTH the " +
                             "outcome modal and the resupply screen. A peer's own post-mission windows are raised " +
                             "somewhere else, so 'the arrival batch is always last on the host' — the only thing " +
                             "that makes the host's order predictable from native code — is no longer true.";

            // ─── ARM F: window history rides the save, and RestoreData CLEARS ───
            // GeoLevelController.RecordInstanceData:415 records GetRestorableData(); :690 restores it. The clear at
            // RestoreData:40 is why any carried-over queue must be re-queued AFTER View.RestoreState, never before.
            if (instData == null || instData.GetField("ViewStateSwitchQuery", AllMembers) == null)
                yield return "L93 premise-history-rides-the-save: GeoLevelInstanceData.ViewStateSwitchQuery is gone — " +
                             "the queue no longer persists with the geoscape, so window history across a battle " +
                             "stops being a mirroring bug and becomes a missing engine feature.";
            if (restorable == null)
                yield return "L93 premise-history-rides-the-save: IGeoscapeRestorableViewState is gone — which windows " +
                             "are even ELIGIBLE to survive a battle is no longer declared by the game.";
            var restore = query.GetMethod("RestoreData", AllMembers);
            if (restore == null || !CallsNamed(restore, "Clear"))
                yield return "L93 premise-history-rides-the-save: RestoreData no longer clears " +
                             "_viewStateSwitchRequests first. Any fix that carries a peer's own unviewed windows " +
                             "across a mission is ordered against that clear; without it the constraint 're-queue " +
                             "AFTER RestoreState' is silently wrong.";

            // ─── ARM G: THE ORDERING RANK, EXECUTED ───
            // The developer decision (2026-08-04) is NOT "identical to native" — it is "the resupply screen
            // comes first, on every peer including the host". That is deliberately a rule each peer applies to
            // its own queue, so it needs no host→client signal and cannot race the wire. The rank function is
            // PURE, so it is EXECUTED here on the cases that matter rather than described.
            var replenishState = game.GetType("PhoenixPoint.Geoscape.View.ViewStates.UIStateReplenish");
            var eventState = game.GetType("PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoscapeEvent");
            int? replenishRank = Multiplayer.Network.Sync.ReplenishSync.RankFor(replenishState);
            if (replenishRank == null)
                yield return "L93 replenish-not-ranked: ReplenishSync.RankFor no longer ranks UIStateReplenish, so " +
                             "the resupply screen falls back to the game's own priority 0 and orders by whichever " +
                             "window that peer happened to queue first — the exact 2026-08-04 split where the host " +
                             "showed an event and both clients showed the resupply screen.";
            // 15 is the event family's ceiling: OnGeoscapeEventRaised:2044 gives 0 or 10 and :2049/:2057 bump a
            // superseding window to 15. A rank that does not CLEAR it decides nothing.
            else if (replenishRank.Value <= 15)
                yield return "L93 replenish-not-ranked: UIStateReplenish ranks " + replenishRank.Value + ", which " +
                             "does not clear the event family's ceiling of 15 (OnGeoscapeEventRaised:2044/:2049/" +
                             ":2057). Equal or lower means the queue falls back to insert order and the peers split " +
                             "again the moment a mission triggers an event.";
            if (eventState != null && Multiplayer.Network.Sync.ReplenishSync.RankFor(eventState) != null)
                yield return "L93 rank-table-overreaches: the rank table now names UIStateGeoscapeEvent too. The " +
                             "table's safety property is that anything it does NOT name keeps the game's own " +
                             "priority — ranking the event family re-orders windows this decision never covered.";
            // The request is REPLACED rather than mutated because Priority is readonly. If that ever stops
            // being true the replacement is dead weight, and if the prefix goes the rank is unreachable.
            var priorityField = request.GetField("Priority", AllMembers);
            if (priorityField == null || !priorityField.IsInitOnly)
                yield return "L93 premise-priority-readonly: GeoscapeViewStateSwitchRequest.Priority is no longer " +
                             "readonly. QueueRankPatch rebuilds the whole request purely because it could not be " +
                             "assigned — simplify it to a field write, or the indirection is unexplained.";
            if (typeof(Multiplayer.Network.Sync.ReplenishSync.QueueRankPatch).GetMethod("Prefix", AllMembers) == null)
                yield return "L93 replenish-not-ranked: ReplenishSync.QueueRankPatch has no Prefix — the rank table " +
                             "is computed by nothing and every window rides the game's own priority again.";

            // ─── ARM H: THE HISTORY CARRY, ASSERTED AT BOTH ENDS ───
            if (typeof(Multiplayer.Network.Sync.EventPopup).GetMethod("RequeueUnanswered", AllMembers) == null)
                yield return "L93 history-not-carried-across-mission: EventPopup.RequeueUnanswered is gone, so a " +
                             "client's unread windows are not handed back to the held-raise drain after a battle. " +
                             "A client enters the battle from the host's save transfer, so the restored " +
                             "ViewStateSwitchQuery is the HOST's and this peer's own queue died with its level.";
            if (typeof(Multiplayer.Network.Sync.ReplenishSync.CarryUnreadWindowsPatch).GetMethod("Postfix", AllMembers) == null)
                yield return "L93 history-not-carried-across-mission: ReplenishSync.CarryUnreadWindowsPatch has no " +
                             "Postfix — nothing calls the carry at the one moment the queue has just been rebuilt " +
                             "from the save (GeoscapeView.RestoreState:344, GeoLevelController:691).";

            // ─── ARM I: THE MIRRORED OUTCOME PAGE NEVER MINTS, AND NEVER RENDERS A NULL REWARD ───
            // CompleteEvent is the only writer of ChoiceReward (GeoscapeEvent.cs:101) and its next line GRANTS
            // it (:102). So the mirrored replay half must reach it from nowhere, and must always leave a
            // dereferenceable reward behind for the two unguarded reads on the native page.
            var popup = typeof(Multiplayer.Network.Sync.EventPopup);
            foreach (var name in new[] { "ReplayResolution", "MarkResolvedInstance" })
            {
                var m = popup.GetMethod(name, AllMembers);
                if (m == null)
                    yield return "L93 replay-half-gone: EventPopup." + name + " no longer exists — the mirrored " +
                                 "outcome page is built somewhere this law does not check.";
                else if (CallsNamed(m, "CompleteEvent"))
                    yield return "L93 mirror-mints-reward: EventPopup." + name + " reaches " +
                                 "GeoscapeEvent.CompleteEvent, whose :102 line APPLIES the reward. A mirroring " +
                                 "peer running it grants itself resources the host already granted everyone — " +
                                 "the law-3 double-grant this whole family exists to avoid.";
            }
            var mark = popup.GetMethod("MarkResolvedInstance", AllMembers);
            if (mark != null && !ReadsMember(mark, "SetChoiceReward"))
                yield return "L93 mirror-reward-stub-gone: MarkResolvedInstance no longer writes a reward " +
                             "stub. SetClosingEncounter:357 (geoEvent.ChoiceReward.ApplyResult) and " +
                             "SelectChoice:604 dereference ChoiceReward UNGUARDED, so a mirroring peer's outcome " +
                             "page NREs the line after its text renders — no green list, and a dead window.";
            // PREMISE for that stub: the day the game guards those reads, the stub is dead weight.
            var encounters = game.GetType("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            var closing = encounters?.GetMethod("SetClosingEncounter", AllMembers);
            if (closing != null && !ReadsMember(closing, "ChoiceReward"))
                yield return "L93 premise-reward-deref-unguarded: SetClosingEncounter no longer reads " +
                             "ChoiceReward. The empty-stub half of MarkResolvedInstance exists ONLY because that " +
                             "read is unguarded — re-check whether it is still needed.";
            // The reward is a GRANTED amount, so it can only be captured AFTER the native body ran.
            var rewardPatch = typeof(Multiplayer.Network.Sync.EventRewardBroadcast);
            if (rewardPatch.GetMethod("Postfix", AllMembers) == null)
                yield return "L93 reward-not-captured: EventRewardBroadcast has no Postfix. A PREFIX there would " +
                             "ship a ChoiceReward that CompleteEvent has not written yet (GeoscapeEvent.cs:101), " +
                             "i.e. an empty reward on every mirroring peer.";
        }

        // ─── IL helpers — same shape as L80's, self-contained per law file (this repo's idiom) ───

        private static IEnumerable<MethodInfo> SafeMethods(Type t)
        {
            try { return t.GetMethods(AllMembers); }
            catch { return Enumerable.Empty<MethodInfo>(); }
        }

        /// <summary>Assembly-CSharp drags in Unity types the console host cannot load, so GetTypes() throws a
        /// ReflectionTypeLoadException with the resolvable half in Types. Take that half — a law that dies on
        /// one unloadable type would take the whole harness with it.</summary>
        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Enumerable.Empty<Type>(); }
        }

        private static bool CallsNamed(MethodBase m, string calleeName)
        {
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineMethod ||
                    (step.Value.Op != OpCodes.Call && step.Value.Op != OpCodes.Callvirt)) continue;
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                      typeArgs, methodArgs); } catch { }
                if (callee != null && callee.Name == calleeName) return true;
            }
            return false;
        }

        private static bool WritesField(MethodBase m, FieldInfo target)
        {
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op != OpCodes.Stfld && step.Value.Op != OpCodes.Stsfld) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                typeArgs, methodArgs); } catch { }
                if (f != null && f.MetadataToken == target.MetadataToken && f.Module == target.Module) return true;
            }
            return false;
        }

        /// <summary>Reads a field or auto-property named <paramref name="memberName"/> (the getter counts —
        /// <c>UpdateStateStack</c> is a property and Update reads it through <c>get_UpdateStateStack</c>).</summary>
        private static bool ReadsMember(MethodBase m, string memberName)
        {
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                          typeArgs, methodArgs); } catch { }
                    if (callee != null && callee.Name == "get_" + memberName) return true;
                }
                else if (step.Value.Op.OperandType == OperandType.InlineField)
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                    typeArgs, methodArgs); } catch { }
                    if (f != null && (f.Name == memberName ||
                                      f.Name == "<" + memberName + ">k__BackingField")) return true;
                }
            }
            return false;
        }

        private struct Step { public OpCode Op; public int Pos; }

        /// <summary>A naive byte scan would match operand bytes and invent edges, and a law that cries wolf is a
        /// law that gets ignored. Anything unparseable ABANDONS the method rather than guessing.</summary>
        private static IEnumerable<KeyValuePair<byte[], Step>> Walk(MethodBase m)
        {
            byte[] il = null;
            try { il = m == null ? null : m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                OpCode op;
                if (!OpCodeByValue.TryGetValue(code, out op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                yield return new KeyValuePair<byte[], Step>(il, new Step { Op = op, Pos = i });
                i += size;
            }
        }

        private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodes();

        private static Dictionary<short, OpCode> BuildOpCodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); map[op.Value] = op; }
            return map;
        }

        private static int OperandSize(OperandType t, byte[] il, int pos)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return pos + 4 > il.Length ? -1 : 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return -1;
            }
        }
    }
}
