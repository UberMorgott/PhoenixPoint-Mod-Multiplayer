using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L98 — A STALE VALUE MAY NOT WIN OVER A FRESHER ONE, IN THE MODEL OR ON THE SCREEN.
    ///
    /// THE REPORT (3 instances, 2026-08-04, build 842240 B @ 23:22). "The action points sync between the
    /// CLIENTS. On the host they do not — every soldier shows FULL AP there. And it is not only the panel: I
    /// jumped onto a roof with the jetpack heavy and had 1 AP left, on the host that soldier had his full AP.
    /// The points keep jumping all battle; if I walk with another soldier and come back to him they are there
    /// again."
    ///
    /// THE MEASUREMENT, and it is not a paint bug. The host shipped 94 settles that battle
    /// (<c>multiplayer.log</c>, "HOST settle … ap=…"). Neither client applied ONE — zero
    /// "CLIENT settled" lines in either peer log — because <c>ApplySettle</c> threw a
    /// <c>NullReferenceException</c> on its first call and every call after it: 15305 identical traces in
    /// <c>Player.log</c>, none of them in the mod's own log. Every peer therefore kept whatever AP its own
    /// native re-execution computed and the host's authority never landed anywhere. Two independent defects
    /// produced it, and each gets an arm here.
    ///
    /// DEFECT 1 — THE UNGUARDED toActor. <c>TacticalFactionVision.UpdateVisibilityOfAllTowardsActor</c>:549-554
    /// guards the LOOKER (<c>if (!(actor.TacticalPerceptionBase == null))</c>) and passes the actor being
    /// looked AT straight through to <c>CheckVisibleLineBetweenActors</c>:755 →
    /// <c>GetSizeAndStealthVisibilityMultiplier</c>:842, whose first line dereferences
    /// <c>actor.TacticalPerceptionBase.TacticalPerceptionBaseDef</c> with no test at all. Natively that is
    /// sound: the only caller is <c>OnActorMoved</c>, which can only name an actor that walked. L96's inverted
    /// sweep names EVERY member of a foreign faction, and <c>TacticalActorBase</c> is also what crates, ground
    /// piles and destructibles are (A6) — none of which carry a perception component.
    ///
    /// DEFECT 2 — THE THROW WEDGED THE QUEUE. The exception escaped <c>ApplySettle</c>, escaped
    /// <c>ClientTick</c>'s loop and escaped <c>SyncEngine.Tick</c>. The failing entry was never added to
    /// <c>done</c>, so it sat at the head of <c>_pending</c> and head-of-line blocked EVERY later settle for
    /// EVERY actor for the rest of the battle — and the tick steps after that call (the damage resnapshot, the
    /// lifecycle ship, the law-11 repaint flush) never ran either. One bad actor cost the whole rail.
    ///
    /// DEFECT 3 — THE SQUAD BAR ARMED A RESETTABLE COUNTDOWN. The paint half shipped the same day called
    /// <c>TacticalView.UpdateSquadMembersActionAndWillPoints</c>:278-281 from a postfix on
    /// <c>BaseStat.OnStatChange</c>. That method is not a request, it is a 2-frame countdown
    /// (<c>_updateSquadMembersWillAndActionPointsIn = 1</c>, and <c>Impl</c>:286 paints only once
    /// <c>_in-- &lt;= 0</c>) — so it needs two consecutive <c>TacticalView.Update</c>s with no further arming
    /// in between. Every NATIVE arming site is a discrete human gesture, so natively that always holds. The
    /// stat stream is not: while it runs at one write per frame or better, the countdown is re-armed before it
    /// can expire and the bar simply holds its last paint, which at the top of a turn is every soldier at FULL
    /// AP. Worst on the host, which alone runs the real damage/status/reaction math (a client's is neutered at
    /// <c>DamageAccumulation.ApplyAddedDamage</c>:550) — which is the asymmetry the report describes verbatim.
    ///
    /// WHY L81 AND L96 WERE BOTH GREEN THROUGH ALL OF IT. L81 asserted that <c>ApplySettle</c> CALLS the
    /// game's own vision method; L96 asserted it calls it TWICE, once per direction. Both facts stayed true
    /// while not one settle in the battle survived the call. A law about the shape of a call graph cannot see
    /// a value that never lands, so every arm below is written against the LANDING instead: the applier must
    /// be fault-isolated per actor, the entry must leave the queue on the failure path, every argument fed to
    /// the native sweep must be guarded the way the game guards its own, the fresher queued settle must be the
    /// one that survives, and the bar must be painted rather than requested.
    ///
    /// Falsify: delete the try/catch around <c>ApplySettle</c> → <c>settle-not-fault-isolated</c>; widen it
    /// over the <c>done</c> bookkeeping → <c>settle-drop-not-recorded</c>; drop either <c>CanBeSeen</c> guard
    /// in <c>RefreshVisionTowards</c> → <c>vision-sweep-unguarded</c>; make <c>QueueSettle</c> keep the first
    /// entry instead of the newest → <c>stale-settle-wins</c>; put the native armer back in
    /// <c>TacticalUiRepaint</c> → <c>squad-bar-uses-resettable-countdown</c>; delete
    /// <c>PaintSquadBar</c>'s <c>UpdateActorStats</c> loop → <c>squad-bar-not-painted</c>; patch the game so
    /// <c>GetSizeAndStealthVisibilityMultiplier</c> stops dereferencing the perception component →
    /// <c>premise-visibility-deref-guarded</c>; make the game's armer idempotent →
    /// <c>premise-countdown-now-idempotent</c>.
    /// </summary>
    internal static class L98_ApAuthority
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var cmd = typeof(Multiplayer.Tactical.TacticalCommandSync);
            var repaint = typeof(Multiplayer.Tactical.TacticalUiRepaint);

            // ─── HALF 1: one actor's failure may not cost every other actor its correction ───

            var tick = cmd.GetMethod("ClientTick", AllMembers);
            var applyOffset = tick == null ? -1 : FirstCallOffset(tick, "ApplySettle");
            if (tick == null || applyOffset < 0)
            {
                yield return "L98 settle-applier-gone: TacticalCommandSync.ClientTick no longer reaches " +
                             "ApplySettle — the standing applier that lands the host's authoritative AP and " +
                             "position on this peer is not there to be checked.";
            }
            else
            {
                var guard = Clauses(tick).FirstOrDefault(
                    c => c.Flags == ExceptionHandlingClauseOptions.Clause &&
                         applyOffset >= c.TryOffset && applyOffset < c.TryOffset + c.TryLength);
                if (guard == null)
                {
                    yield return "L98 settle-not-fault-isolated: the ApplySettle call in ClientTick is not " +
                                 "inside a catch. A settle that throws then escapes the loop AND SyncEngine.Tick: " +
                                 "the entry is never removed from _pending, so it head-of-line blocks every later " +
                                 "settle for every actor for the rest of the battle, and the tick steps after it " +
                                 "(damage resnapshot, lifecycle ship, law-11 repaint flush) stop running too. " +
                                 "Measured 2026-08-04: 94 settles shipped, 0 applied, 15305 NREs — the host's AP " +
                                 "never landed on any peer and each one kept its own stale number.";
                }
                else
                {
                    int bookkeeping = FirstCallOffsetAfter(tick, "Add", applyOffset);
                    if (bookkeeping >= 0 &&
                        bookkeeping >= guard.TryOffset && bookkeeping < guard.TryOffset + guard.TryLength)
                        yield return "L98 settle-drop-not-recorded: the catch around ApplySettle also covers the " +
                                     "list Add that records the entry as handled, so a throw skips it and the " +
                                     "settle stays in _pending — caught, logged, and still head-of-line blocking " +
                                     "every actor behind it. The protected region must be the APPLY only; the " +
                                     "entry has to leave the queue on the failure path exactly as on the success " +
                                     "path (it is dropped, not retried: a settle that throws once throws every " +
                                     "frame, and the 0x84 resnapshot is what re-converges that actor).";
                }
            }

            // ─── HALF 2: every actor fed to the native sweep must be guarded the way the game guards its own ─

            var refresh = cmd.GetMethod("RefreshVisionTowards", AllMembers);
            if (refresh == null)
            {
                yield return "L98 settle-repair-gone: TacticalCommandSync.RefreshVisionTowards no longer exists.";
            }
            else
            {
                var callees = Calls(refresh).Select(c => c.Name).ToList();
                int sweeps = callees.Count(n => n == "UpdateVisibilityOfAllTowardsActor");
                int guards = callees.Count(n => n == "CanBeSeen");
                if (sweeps > guards)
                    yield return "L98 vision-sweep-unguarded: RefreshVisionTowards makes " + sweeps +
                                 " UpdateVisibilityOfAllTowardsActor call(s) behind only " + guards +
                                 " CanBeSeen guard(s). The game guards the LOOKER at :549-554 and NOTHING guards " +
                                 "the actor being looked at — GetSizeAndStealthVisibilityMultiplier:842 " +
                                 "dereferences its TacticalPerceptionBase blind. The inverted sweep enumerates a " +
                                 "whole faction, and a faction's Actors include crates, ground piles and " +
                                 "destructibles (A6), none of which have one. Every argument this method hands to " +
                                 "the native sweep needs its own guard, not just the ones the current shape " +
                                 "happens to reach.";
            }

            var vision = game.GetType("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
            var multiplier = vision == null ? null : vision.GetMethod("GetSizeAndStealthVisibilityMultiplier", AllMembers);
            if (multiplier == null ||
                !Calls(multiplier).Any(c => c.Name == "get_TacticalPerceptionBase"))
                yield return "L98 premise-visibility-deref-guarded: " +
                             "TacticalFactionVision.GetSizeAndStealthVisibilityMultiplier no longer dereferences " +
                             "TacticalPerceptionBase. That blind dereference IS the reason CanBeSeen exists — if " +
                             "the engine now tests it, the guard is dead weight and the arm above is asserting a " +
                             "hazard the game no longer has.";

            // ─── HALF 3: the newest queued settle is the one that must survive ───

            foreach (var v in StaleSettleArm(cmd)) yield return v;

            // ─── HALF 4: the squad bar is PAINTED, never requested through a resettable countdown ───

            var view = game.GetType("PhoenixPoint.Tactical.View.TacticalView");
            var armer = view == null ? null : view.GetMethod("UpdateSquadMembersActionAndWillPoints", AllMembers);

            var repaintCalls = MethodsOf(repaint).SelectMany(m => Calls(m)).Select(c => c.Name).ToList();
            if (repaintCalls.Contains("UpdateSquadMembersActionAndWillPoints"))
                yield return "L98 squad-bar-uses-resettable-countdown: TacticalUiRepaint arms " +
                             "TacticalView.UpdateSquadMembersActionAndWillPoints again. That is not a request, it " +
                             "is a 2-frame countdown (:280-281 sets _in = 1; Impl:286 paints only once " +
                             "_in-- <= 0), so it needs two consecutive Updates with no further arming in between. " +
                             "Off BaseStat.OnStatChange — every stat of every actor — a steady stream re-arms it " +
                             "before it can expire and the bar holds its last paint forever, which at the top of a " +
                             "turn is every soldier at FULL AP. Worst exactly on the host, whose damage math is " +
                             "not neutered. Paint the rows instead of asking the game to.";
            if (!repaintCalls.Contains("UpdateActorStats"))
                yield return "L98 squad-bar-not-painted: nothing in TacticalUiRepaint calls " +
                             "SquadMemberScrollerElement.UpdateActorStats. It is the ONLY method in the assembly " +
                             "that writes the AP/WP/HP texts under the portraits (its single native caller is " +
                             "TacticalView.UpdateSquadMembersActionAndWillPointsImpl:288-292), and no other " +
                             "repaint path reaches them: SetSquad → InitSquadMemberElement rebinds portrait, class " +
                             "icons and tooltip and never touches those texts, over POOLED elements. Without this " +
                             "call the squad bar has no reactivity at all.";

            // ─── HALF 5: the host's turn-edge sweep is stamped in the turn it ANNOUNCES, not inside the edge ─

            foreach (var v in SweepEpochArm()) yield return v;

            if (armer == null || !WritesField(armer, "_updateSquadMembersWillAndActionPointsIn"))
                yield return "L98 premise-countdown-now-idempotent: " +
                             "TacticalView.UpdateSquadMembersActionAndWillPoints no longer writes its countdown " +
                             "field. The bypass above exists ONLY because arming was resettable — if the game made " +
                             "it idempotent, delete PaintSquadBar and arm the native pass again.";
        }

        /// <summary>THE ARM THIS LAW WAS MISSING, and the one that would have caught 2026-08-05: every arm
        /// above is about a settle LANDING, and none of them says anything about WHEN the host stamped the
        /// value it carries. AP restore is native and per-peer (<c>PlayTurnCrt</c>:422-425 →
        /// <c>TacticalActor.RestartAbilities</c>:1244 <c>ActionPoints.SetToMax</c>), so the host's authority
        /// for the new turn only exists AFTER the host's own restore has run. A sweep emitted from
        /// <c>TacMission.OnNewTurn</c> — raised by <c>NextTurnCrt</c>:716 BEFORE <c>PlayTurnCrt</c> — carries
        /// the AP every soldier ENDED the previous turn on, in neither epoch. While clients applied it early
        /// that was harmless; under the turn-epoch gate (L96) it is held until after the client's own restore
        /// and overwrites it, and every soldier on every client starts the turn spent. Nothing was red.
        ///
        /// Decidable headless as three facts: <c>HostBroadcastTurn</c> makes NO direct sweep call, the sweep
        /// is fired behind a <c>TacticalFaction.IsPlayingTurn</c> read (TacticalFaction.cs:441, the game's own
        /// "this turn has started"), and something on the standing tick drives it — a gated emitter nobody
        /// calls is the silent swallow this repo is made of.
        ///
        /// Falsify: call HostSettleAllLive straight from HostBroadcastTurn again → sweep-stamped-inside-the-edge;
        /// drop the IsPlayingTurn test → sweep-not-gated-on-the-turn-start; drop the HostSweepTick call from
        /// SyncEngine.Tick → sweep-has-no-driver.</summary>
        private static IEnumerable<string> SweepEpochArm()
        {
            var turnSync = typeof(Multiplayer.Tactical.TacticalTurnSync);
            var broadcast = turnSync.GetMethod("HostBroadcastTurn", AllMembers);
            var sweepTick = turnSync.GetMethod("HostSweepTick", AllMembers);
            var sweep = typeof(Multiplayer.Tactical.TacticalCommandSync).GetMethod("HostSettleAllLive", AllMembers);
            var engineTick = typeof(Multiplayer.Network.Sync.SyncEngine).GetMethod("Tick", AllMembers);
            if (broadcast == null || sweepTick == null || sweep == null || engineTick == null)
            {
                yield return "L98 premise-turn-edge-sweep-gone: TacticalTurnSync.HostBroadcastTurn / " +
                             "HostSweepTick, TacticalCommandSync.HostSettleAllLive or SyncEngine.Tick no " +
                             "longer resolve, so 'the host's AP authority is stamped after the host's own " +
                             "restore' is UNCHECKED rather than satisfied.";
                yield break;
            }

            if (Calls(broadcast).Any(c => c.Name == "HostSettleAllLive"))
                yield return "L98 sweep-stamped-inside-the-edge: TacticalTurnSync.HostBroadcastTurn calls " +
                             "HostSettleAllLive directly again. It runs from TacMission.OnNewTurn, which " +
                             "NextTurnCrt:716 raises BEFORE PlayTurnCrt — so every AP in that sweep is the " +
                             "value the actor ENDED the previous turn on, stamped in neither epoch. Held by " +
                             "the L96 turn-epoch gate it is replayed AFTER this peer's own " +
                             "RestartAbilities:1244 SetToMax and wins over it: measured 2026-08-05, six " +
                             "'CLIENT settled … ap=0' lines one frame after 'Changing turn to Phoenix'.";

            int gate = FirstCallOffset(sweepTick, "get_IsPlayingTurn");
            int fire = FirstCallOffset(sweepTick, "HostSettleAllLive");
            if (fire < 0)
                yield return "L98 sweep-not-gated-on-the-turn-start: TacticalTurnSync.HostSweepTick does not " +
                             "reach HostSettleAllLive, so the deferred sweep never ships and no actor the " +
                             "host is not animating is corrected at all (law L123's whole point).";
            else if (gate < 0 || gate > fire)
                yield return "L98 sweep-not-gated-on-the-turn-start: HostSweepTick fires HostSettleAllLive " +
                             "without first reading TacticalFaction.IsPlayingTurn. That flag " +
                             "(TacticalFaction.cs:441) is set immediately after every actor's StartTurn and is " +
                             "the ONLY headless-checkable evidence that this host's own AP restore has already " +
                             "run; without it the sweep is back to shipping last turn's leftovers.";

            if (!Calls(engineTick).Any(c => c.Name == "HostSweepTick"))
                yield return "L98 sweep-has-no-driver: SyncEngine.Tick does not call " +
                             "TacticalTurnSync.HostSweepTick. HostBroadcastTurn only ARMS the sweep — under " +
                             "the native ordering its own eager evaluation is always false — so without the " +
                             "standing tick the turn-edge sweep silently never happens.";
        }

        /// <summary>EXECUTED, not inspected: the queue must be last-writer-wins per actor. A settle is the
        /// host's authority and they arrive in seq order, so an applier that keeps the FIRST pending entry for
        /// an actor (a tempting way to "fix" a queue that will not drain) makes a stale number outlive a
        /// fresher one — the exact defect this whole law is named for, arriving through the repair rather than
        /// the bug.</summary>
        private static IEnumerable<string> StaleSettleArm(Type cmd)
        {
            var queue = cmd.GetMethod("QueueSettle", AllMembers);
            var pendingField = cmd.GetField("_pending", BindingFlags.NonPublic | BindingFlags.Static);
            if (queue == null || pendingField == null)
            {
                yield return "L98 settle-queue-gone: TacticalCommandSync.QueueSettle / _pending no longer exist — " +
                             "the arrival side of the settle cannot be exercised.";
                yield break;
            }

            var pending = (System.Collections.IDictionary)pendingField.GetValue(null);
            var saved = new List<System.Collections.DictionaryEntry>();
            foreach (System.Collections.DictionaryEntry e in pending) saved.Add(e);
            pending.Clear();

            string violation = null;
            try
            {
                const int key = 4242;
                // Trailing args are the settle's carried STATE (statuses, ability traits, selected equipment,
                // per-turn ability uses, TFTV champ identity — L131/L137/L186/L242/L262). All empty here: this
                // arm is about which ENTRY survives, not what rides in it.
                queue.Invoke(null, new object[] { key, new Vector3(1f, 0f, 1f), 9f, 5f, false,
                                                  new List<string>(), new List<string>(), null, null, null });
                queue.Invoke(null, new object[] { key, new Vector3(2f, 0f, 2f), 1f, 5f, false,
                                                  new List<string>(), new List<string>(), null, null, null });

                var entry = pending[key];
                var ap = entry == null ? null : entry.GetType().GetField("Ap", AllMembers);
                string held = ap == null ? "<no pending entry>" : ((float)ap.GetValue(entry)).ToString("0.##");
                if (held != "1")
                    violation = "L98 stale-settle-wins: after queueing ap=9 then ap=1 for one actor, the pending " +
                                "settle holds ap=" + held + ". Settles arrive in seq order and each one is the " +
                                "host's latest word, so the queue is last-writer-wins per actor: keeping the older " +
                                "entry lets a stale authoritative number overwrite a fresher one the moment the " +
                                "queue drains, which is worse than not settling at all.";
            }
            finally
            {
                pending.Clear();
                foreach (var e in saved) pending[e.Key] = e.Value;
            }
            if (violation != null) yield return violation;
        }

        private static IEnumerable<MethodBase> MethodsOf(Type t)
        {
            foreach (var nested in new[] { t }.Concat(t.GetNestedTypes(AllMembers)))
            {
                foreach (var m in nested.GetMethods(AllMembers)) yield return m;
                foreach (var c in nested.GetConstructors(AllMembers)) yield return c;
            }
        }

        private static IList<ExceptionHandlingClause> Clauses(MethodBase m)
        {
            try { return m.GetMethodBody()?.ExceptionHandlingClauses ?? new List<ExceptionHandlingClause>(); }
            catch { return new List<ExceptionHandlingClause>(); }
        }

        /// <summary>IL offset of the first call to <paramref name="name"/>, or -1. The offset returned is the
        /// CALL instruction's own, which is what an <c>ExceptionHandlingClause</c>'s try range is expressed
        /// in.</summary>
        private static int FirstCallOffset(MethodBase m, string name) => FirstCallOffsetAfter(m, name, -1);

        private static int FirstCallOffsetAfter(MethodBase m, string name, int after)
        {
            foreach (var call in CallSites(m))
                if (call.Value.Name == name && call.Key > after) return call.Key;
            return -1;
        }

        private static List<MethodBase> Calls(MethodBase m) => CallSites(m).Select(c => c.Value).ToList();

        private static List<KeyValuePair<int, MethodBase>> CallSites(MethodBase m)
        {
            var seq = new List<KeyValuePair<int, MethodBase>>();
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Op.OperandType != OperandType.InlineMethod ||
                    (step.Op != OpCodes.Call && step.Op != OpCodes.Callvirt)) continue;
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Il, step.Pos),
                                                      typeArgs, methodArgs); } catch { }
                if (callee != null) seq.Add(new KeyValuePair<int, MethodBase>(step.Start, callee));
            }
            return seq;
        }

        /// <summary>Countdown state is a private FIELD, so arming it is an <c>stfld</c> with no setter token
        /// to scan for — the same correction L96's <c>ReadsField</c> had to make, one store further on.</summary>
        private static bool WritesField(MethodBase m, string name)
        {
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Op != OpCodes.Stfld && step.Op != OpCodes.Stsfld) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Il, step.Pos),
                                                typeArgs, methodArgs); } catch { }
                if (f != null && f.Name == name) return true;
            }
            return false;
        }

        private struct Step { public byte[] Il; public OpCode Op; public int Start; public int Pos; }

        /// <summary>A naive byte scan would match operand bytes and invent edges, and a law that cries wolf is
        /// a law that gets ignored. Anything unparseable ABANDONS the method rather than guessing.</summary>
        private static IEnumerable<Step> Walk(MethodBase m)
        {
            byte[] il = null;
            try { il = m == null ? null : m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            int i = 0;
            while (i < il.Length)
            {
                int start = i;
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
                yield return new Step { Il = il, Op = op, Start = start, Pos = i };
                i += size;
            }
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
                case OperandType.InlineSwitch:
                    if (pos + 4 > il.Length) return -1;
                    return 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return -1;
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
    }
}
