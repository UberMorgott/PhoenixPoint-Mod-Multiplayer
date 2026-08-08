using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RailCheck
{
    /// <summary>
    /// L338 — A REVEAL THE HOST DOES NOT HOLD IS REMOVED FROM THE CLIENT.
    ///
    /// THE REPORT (2026-08-08, the owner, repeated): "the same soldier sees an enemy on one peer and not on
    /// the other, in BOTH directions." The second direction is the whole reason this law exists, and it was
    /// UNREACHABLE by construction until the settle started carrying the host's known-state. Every version of
    /// the settle's vision repair before that was MONOTONE: it ran the peer's own line-of-sight test and fed
    /// the result to <c>TacticalFactionVision.IncrementKnownCounter</c>:444, which lands in
    /// <c>KnownCounters.IncrementCounterTo</c>:55-67 — <c>if (num &lt; counter) _counters[type] = counter</c>,
    /// a maximum. It could ADD a reveal the host had. It could not remove one the host did not, ever, and
    /// nothing else re-tested until the faction-turn edge. So a law that only checked reveals being ADDED
    /// would have been green on the broken code for the entire year it was broken — which is exactly what
    /// happened to L81 and L96, both green through the live report.
    ///
    /// WHY IT NEEDED WIRE STATE AND NOT A BETTER LOCAL TEST. The peers do not share the geometry a local test
    /// is computed from: <c>SceneObjectIdsComponent.MergeWith</c>:29-34 mints a FRESH RANDOM guid when a
    /// combined destructible guid collides, and a random guid differs on every peer — measured 26 collisions
    /// over 52 objects in one mission and 63 over 126 in another, each then addressed by POSITION. Different
    /// cover is different line of sight. Two peers recomputing vision cannot agree in principle.
    ///
    /// WHAT THIS LAW DOES, AND WHY IT RUNS INSTEAD OF READING. The game exposes no "set this counter to N": a
    /// monotone raise, and ONE lowering mechanism — <c>DecrementKnownCounters</c>:494-505 →
    /// <c>DecrementAllCounters</c>:89-95, the same one the faction-turn edge decays knowledge with
    /// (<c>OnFactionStartTurn</c>:154-175 → <c>DecrementMyCountersForFaction</c>). Exact assignment is
    /// therefore an ARITHMETIC argument — decrement by the largest per-counter surplus, then raise each
    /// counter back to its target — and an arithmetic argument is worth nothing asserted. So the arms below
    /// EXECUTE the shipped <c>TacticalCommandSync.PlanVisionDrops</c> against the GAME'S OWN
    /// <c>KnownCounters</c> instance over a table of transitions, and demand the counters land on the host's
    /// numbers exactly — including the two rows that only go DOWN, and the row that must reach
    /// <c>AreCountersZero</c> so <c>DecrementKnownCounters</c> drops the actor out of <c>KnownActors</c>
    /// altogether and <c>TacticalView.OnFactionKnowledgeChanged</c>:486-516 paints it
    /// <c>KnownState.Hidden</c>.
    ///
    /// POSITIVE CONTROL: the same table is replayed with the drop count forced to zero — the pre-2026-08-08
    /// raise-only repair — and at least one row MUST come out wrong. If raise-only satisfies the table, the
    /// table is not exercising the lowering direction and this whole law is theatre, so it says so.
    ///
    /// Falsify: make <c>PlanVisionDrops</c> return 0 → <c>reveal-not-removed</c>; make it return the SUM of
    /// the surpluses instead of the max → <c>reveal-not-removed</c> on the rows that must keep a beacon;
    /// delete the <c>DecrementKnownCounters</c> call from <c>ApplyVision</c> → <c>applier-cannot-lower</c>;
    /// stop routing the applier through <c>PlanVisionDrops</c> → <c>applier-plan-bypassed</c>; patch
    /// <c>IncrementCounterTo</c> so it assigns unconditionally → <c>premise-changed</c>.
    /// </summary>
    internal static class L338_AHostlessRevealIsRemoved
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>curLocated, curRevealed, tgtLocated, tgtRevealed. Rows 2-6 go DOWN in at least one
        /// counter — the direction the monotone repair could never reach.</summary>
        private static readonly int[][] Transitions =
        {
            new[] { 0, 0, 0, 1 },   // the host sees it, this peer does not — a reveal APPEARS
            new[] { 0, 1, 0, 0 },   // this peer sees it, the host does not — THE REVEAL IS REMOVED
            new[] { 1, 1, 1, 0 },   // the reveal goes, the "something is there" beacon stays
            new[] { 0, 2, 0, 1 },   // an AI faction's counter of 2 comes down to the host's 1
            new[] { 1, 2, 0, 0 },   // everything goes — the actor must leave KnownActors entirely
            new[] { 1, 0, 0, 2 },   // beacon out, reveal in, in one assignment
            new[] { 0, 1, 0, 1 },   // already equal: nothing may be disturbed
        };

        internal static IEnumerable<string> Check(Assembly game)
        {
            var cmd = typeof(Multiplayer.Tactical.TacticalCommandSync);

            // ─── (a) THE SHIPPED APPLIER IS THE ONE THIS ARITHMETIC BELONGS TO ───
            var applyVision = cmd.GetMethod("ApplyVision", AllMembers);
            var plan = cmd.GetMethod("PlanVisionDrops", AllMembers);
            if (applyVision == null || plan == null)
            {
                yield return "L338 applier-gone: TacticalCommandSync.ApplyVision / PlanVisionDrops no longer " +
                             "exist, so nothing assigns the host's known-state onto this peer and the removal " +
                             "direction is unguarded again.";
                yield break;
            }
            var callees = Calls(applyVision).ToList();
            if (!callees.Any(c => c.Name == "PlanVisionDrops"))
                yield return "L338 applier-plan-bypassed: ApplyVision no longer routes through PlanVisionDrops. " +
                             "The arms below execute that method against the game's own counters — if the shipped " +
                             "applier computes its drop count some other way, they are testing code the battle " +
                             "does not run.";
            if (!callees.Any(c => c.Name == "DecrementKnownCounters"))
                yield return "L338 applier-cannot-lower: ApplyVision never calls " +
                             "TacticalFactionVision.DecrementKnownCounters — the ONLY lowering entry the game " +
                             "exposes (ResetKnownCounterImpl is private). However good the plan, a reveal this " +
                             "peer holds and the host does not can then never be taken away, which is half of the " +
                             "2026-08-08 report and the entire reason this law exists.";

            // ─── (b) THE OUTCOME, EXECUTED AGAINST THE GAME'S OWN COUNTERS ───
            var counters = game.GetType("PhoenixPoint.Tactical.Levels.TacticalFactionVision+KnownCounters");
            var stateType = game.GetType("PhoenixPoint.Tactical.Levels.KnownState");
            var raise = counters == null ? null : counters.GetMethod("IncrementCounterTo", AllMembers);
            var decay = counters == null ? null : counters.GetMethod("DecrementAllCounters", AllMembers);
            var read = counters == null ? null : counters.GetMethod("get_Item", AllMembers);
            var allZero = counters == null ? null : counters.GetMethod("get_AreCountersZero", AllMembers);
            if (counters == null || stateType == null || raise == null || decay == null || read == null ||
                allZero == null)
            {
                yield return "L338 premise-changed: TacticalFactionVision.KnownCounters no longer exposes " +
                             "IncrementCounterTo / DecrementAllCounters / the indexer / AreCountersZero. That " +
                             "class IS the visibility state — TacticalView:486-516 paints straight out of it — so " +
                             "the settle's assignment is written against those four members and nothing here can " +
                             "be executed without them.";
                yield break;
            }
            object located = Enum.ToObject(stateType, 16);   // KnownState.Located
            object revealed = Enum.ToObject(stateType, 256); // KnownState.Revealed

            // PREMISE, executed: the raise really is a MAXIMUM and the decay really floors at zero. Both are
            // the reason the plan is "drop by the largest surplus, then raise back", and neither is asserted.
            var probe = Activator.CreateInstance(counters);
            raise.Invoke(probe, new[] { revealed, (object)2 });
            raise.Invoke(probe, new[] { revealed, (object)1 });
            if ((int)read.Invoke(probe, new[] { revealed }) != 2)
                yield return "L338 premise-changed: KnownCounters.IncrementCounterTo is no longer monotone — " +
                             "raising an existing 2 to 1 lowered it. The whole shape of this repair (and the " +
                             "reason the old local re-run could not converge) rests on that method being a " +
                             "maximum; if it now assigns, the drop pass is unnecessary and this law is arguing " +
                             "about a game that no longer exists.";
            decay.Invoke(probe, null);
            decay.Invoke(probe, null);
            decay.Invoke(probe, null);
            if ((int)read.Invoke(probe, new[] { revealed }) != 0)
                yield return "L338 premise-changed: KnownCounters.DecrementAllCounters no longer floors at zero. " +
                             "PlanVisionDrops relies on a decrement past zero being harmless, so a decay that " +
                             "goes negative would leave a counter that can never be raised back to the host's " +
                             "value.";

            int rawFailures = 0;
            foreach (var row in Transitions)
            {
                string wrong = Run(counters, raise, decay, read, allZero, located, revealed, row, honourPlan: true);
                if (wrong != null)
                    yield return "L338 reveal-not-removed: assigning the host's known-state " + Describe(row) +
                                 " did not land on the host's numbers — " + wrong + ". After a settle a peer's " +
                                 "visibility for that actor must EQUAL the host's in both directions: a reveal " +
                                 "the host holds appears, and a reveal the host does NOT hold is removed. The " +
                                 "second half is the one no local recomputation could ever reach, because " +
                                 "IncrementCounterTo:55-67 only raises — and it is half of the 2026-08-08 report.";
                if (Run(counters, raise, decay, read, allZero, located, revealed, row, honourPlan: false) != null)
                    rawFailures++;
            }

            // POSITIVE CONTROL — without the drop pass the table must NOT be satisfiable.
            if (rawFailures == 0)
                yield return "L338 table-does-not-lower: every transition in this law's table is satisfied by a " +
                             "RAISE-ONLY applier — i.e. by the exact pre-2026-08-08 code whose inability to " +
                             "remove a reveal is what this law was written for. The table has stopped exercising " +
                             "the removal direction, so every green above proves nothing.";
        }

        /// <summary>Drives the game's own <c>KnownCounters</c> through one transition and returns null when it
        /// lands on the target exactly. <paramref name="honourPlan"/> false is the positive control: the same
        /// assignment with the drop pass removed.</summary>
        private static string Run(Type counters, MethodInfo raise, MethodInfo decay, MethodInfo read,
                                  MethodInfo allZero, object located, object revealed, int[] row, bool honourPlan)
        {
            int curL = row[0], curR = row[1], tgtL = row[2], tgtR = row[3];
            var c = Activator.CreateInstance(counters);
            if (curL > 0) raise.Invoke(c, new[] { located, (object)curL });
            if (curR > 0) raise.Invoke(c, new[] { revealed, (object)curR });

            int drops = honourPlan
                ? Multiplayer.Tactical.TacticalCommandSync.PlanVisionDrops(curL, curR, tgtL, tgtR)
                : 0;
            for (int i = 0; i < drops; i++) decay.Invoke(c, null);
            if (tgtL > 0) raise.Invoke(c, new[] { located, (object)tgtL });
            if (tgtR > 0) raise.Invoke(c, new[] { revealed, (object)tgtR });

            int gotL = (int)read.Invoke(c, new[] { located });
            int gotR = (int)read.Invoke(c, new[] { revealed });
            if (gotL != tgtL || gotR != tgtR)
                return "counters ended at (" + gotL + "," + gotR + ") instead of (" + tgtL + "," + tgtR + ")";
            if (tgtL == 0 && tgtR == 0 && !(bool)allZero.Invoke(c, null))
                return "the counters are not all zero, so DecrementKnownCounters:494-505 would keep the actor in " +
                       "KnownActors and TacticalView:486-516 would keep painting it Located instead of Hidden";
            return null;
        }

        private static string Describe(int[] row) =>
            "(" + row[0] + "," + row[1] + ") → (" + row[2] + "," + row[3] + ")";

        private static List<MethodBase> Calls(MethodBase m)
        {
            var seq = new List<MethodBase>();
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
                if (callee != null) seq.Add(callee);
            }
            return seq;
        }

        private struct Step { public OpCode Op; public int Pos; }

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
