using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RailCheck
{
    /// <summary>
    /// L95 — THE SQUAD BAR IS REACTIVE, AND MAKING IT SO MOVES NO STATE STACK (law 11 for the last part of
    /// the tactical screen no repaint path could reach).
    ///
    /// Reported live 2026-08-04: the row of portraits with each soldier's action points under it keeps the
    /// pre-order numbers on every peer that did not click, and only a click on one of the soldiers refreshes
    /// it. The cause is native and exact — those numbers are written by ONE method,
    /// <c>SquadMemberScrollerElement.UpdateActorStats</c>:38-78, whose only caller is the deferred pass
    /// <c>TacticalView.UpdateSquadMembersActionAndWillPointsImpl</c>:284-295, and every native site that ARMS
    /// that pass is a local gesture (<c>TacticalViewState.ActivateAbility</c>:271,
    /// <c>UIStateCharacterSelected.SelectCharacter</c>:245, <c>UIStateShoot.InitState</c>:415,
    /// <c>UIStateWaiting</c>:42, <c>UIStateInitial</c>:30). A mirrored order raises none of them. Exit+Enter
    /// does not help either: <c>SetSquad</c>:216 → <c>SetScroller</c>:119 → <c>InitSquadMemberElement</c>:179
    /// rebinds portraits and never touches those texts, and the elements are POOLED
    /// (<c>UIUtil.EnsureActiveComponentsInContainer</c>), so a rebuilt row shows the previous paint's numbers
    /// until the deferred pass lands — which is also the reported WP flicker (nothing double-applies the kill
    /// bonus: <c>TacticalActor.OnAnotherActorDeath</c>:1856-1863 grants it synchronously inside <c>Die</c>,
    /// and the settle carries the host's number from after that same grant).
    ///
    /// The seam is <c>BaseStat.OnStatChange</c> — one funnel for every stat write in the game — arming the
    /// game's OWN pass and nothing else. That "nothing else" is the second half of this law: the same postfix
    /// runs on every AP tick of every actor, so a state Exit+Enter hung off it would turn law L63's
    /// popped-state hazard into a per-write event.
    ///
    /// Falsify: drop the <c>UpdateSquadMembersActionAndWillPoints</c> call → <c>squad-bar-unwired</c>; repaint
    /// the elements by hand instead → <c>squad-bar-hand-rolled</c>; call <c>MarkDirty</c> from the stat seam →
    /// <c>stat-seam-transitions</c>; clear <c>_dirty</c> before the current-state guard (or delete the guard)
    /// → <c>popped-state-guard-gone</c>; a game patch that stops the native pass repainting the elements →
    /// <c>premise-changed</c>.
    /// </summary>
    internal static class L95_TacUiReactive
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        public static IEnumerable<string> Check(Assembly game)
        {
            var repaint = typeof(Multiplayer.Tactical.TacticalUiRepaint);
            var statSeam = NestedMethod(repaint, "SquadBarStatPatch", "Postfix");

            // ─── (a) THE SQUAD BAR IS ARMED BY A STAT CHANGE, NOT BY A LOCAL CLICK ───
            if (statSeam == null)
            {
                yield return "L95 squad-bar-unwired: TacticalUiRepaint.SquadBarStatPatch.Postfix is gone. " +
                             "Nothing then arms TacticalView.UpdateSquadMembersActionAndWillPoints on a peer " +
                             "that did not click, and SquadMemberScrollerElement.UpdateActorStats:38 is the " +
                             "ONLY writer of the per-soldier AP/HP/WP texts — so every mirrored order leaves " +
                             "the portrait row showing the pre-order numbers until the user clicks a soldier";
                yield break;
            }
            if (!Reaches(statSeam, "TacticalView", "UpdateSquadMembersActionAndWillPoints"))
                yield return "L95 squad-bar-unwired: the stat seam no longer arms the game's own squad pass " +
                             "(TacticalView.UpdateSquadMembersActionAndWillPoints:278), so it observes every " +
                             "stat change and repaints nothing — the seam fires and the screen does not move, " +
                             "this repo's dominant bug shape";
            foreach (var handRolled in new[] { "UpdateActorStats", "GetComponentsInChildren", "SetSquad",
                                               "SetScroller", "SetActionPoints" })
                if (Reaches(statSeam, null, handRolled))
                {
                    yield return "L95 squad-bar-hand-rolled: the stat seam calls " + handRolled + " itself " +
                                 "instead of arming the native pass. The game's own Impl:284-295 is gated on " +
                                 "the module being active and coalesces to one run per burst; a hand-rolled " +
                                 "walk is a second painter with a second set of rules, allocating " +
                                 "GetComponentsInChildren on a seam that fires on EVERY stat write in the game";
                    break;
                }

            // ─── (b) A STAT CHANGE MAY NOT MOVE THE STATE STACK ───
            // MarkDirty means Exit+Enter one frame later (TacticalUiRepaint.Repaint). Hanging that off a
            // funnel that every AP tick of every actor passes through makes L63's popped-state hazard a
            // per-write event, and the selected-actor AP/WP memo already covers the case that needs it.
            foreach (var banned in new[] { "MarkDirty", "Repaint", "Exit", "Enter", "SwitchToState",
                                           "SwitchToPreviousState" })
                if (Reaches(statSeam, null, banned))
                {
                    yield return "L95 stat-seam-transitions: the stat seam reaches " + banned + ". It runs on " +
                                 "BaseStat.OnStatChange — every stat write in the game — so a state Exit+Enter " +
                                 "hung off it fires orders of magnitude more often than the flush it borrows " +
                                 "from, and law L63's already-popped-state hazard becomes a per-write event";
                    break;
                }

            // ─── (c) THE POPPED-STATE GUARD, AND THE FACT THAT _dirty SURVIVES ITS BAIL ───
            // Complementary to L79's repaint-of-popped-state (which asks only whether the guard is REACHED):
            // this asks about ORDER. If the flush clears _dirty before deciding the state is still current,
            // the bail silently EATS the repaint and the real current state never repaints at all.
            var flush = NestedMethod(repaint, "ViewStateUpdatePatch", "Postfix");
            var dirty = repaint.GetField("_dirty", AllMembers);
            if (flush == null || dirty == null)
                yield return "L95 popped-state-guard-gone: TacticalUiRepaint's dirty flush or its _dirty field " +
                             "no longer resolves, so nothing proves the repaint still refuses to Exit+Enter a " +
                             "state the stack has already popped";
            else
            {
                int guardAt = FirstCallTo(flush, "get_CurrentState");
                int lastClear = LastStoreTo(flush, dirty);
                if (guardAt < 0)
                    yield return "L95 popped-state-guard-gone: the dirty flush never asks the stack for its " +
                                 "CurrentState. Confirming an ability transitions INSIDE TacticalViewState" +
                                 ".Update (ActivateAbility:259-277 → SwitchToState(ClearStackAndPush) → " +
                                 "StateStack.Clear:107-121), so without that compare the repaint Exit+ENTERs an " +
                                 "already-popped state — UIStateAbilitySelected.EnterState:180 re-subscribes " +
                                 "AbilityConfirmed on the SHARED confirmation module, Clear only ever exits the " +
                                 "TOP, and the zombie then fires its cached ability FIRST on every later " +
                                 "confirmation (the JetJump that flew instead of shooting)";
                else if (lastClear >= 0 && lastClear < guardAt)
                    yield return "L95 popped-state-guard-gone: the dirty flush clears _dirty BEFORE the " +
                                 "current-state compare (last store at IL " + lastClear + ", guard at IL " +
                                 guardAt + "). The flag must survive the bail: the state that is really " +
                                 "current repaints on its OWN Update next frame, and clearing first drops that " +
                                 "repaint on the floor — the mirrored change is then never painted at all";
            }

            // ─── (d) PREMISE: the native pass this law arms still repaints those elements ───
            var view = game.GetType("PhoenixPoint.Tactical.View.TacticalView");
            var arm = view == null ? null : view.GetMethod("UpdateSquadMembersActionAndWillPoints", AllMembers);
            var impl = view == null ? null : view.GetMethod("UpdateSquadMembersActionAndWillPointsImpl", AllMembers);
            var element = game.GetType("PhoenixPoint.Tactical.View.ViewControllers.SquadMemberScrollerElement");
            var updateStats = element == null ? null : element.GetMethod("UpdateActorStats", AllMembers);
            if (arm == null || impl == null || updateStats == null)
                yield return "L95 premise-changed: TacticalView.UpdateSquadMembersActionAndWillPoints / its " +
                             "Impl / SquadMemberScrollerElement.UpdateActorStats no longer resolve. This law " +
                             "arms the game's own deferred pass by name and asserts nothing else";
            else
            {
                if (!Reaches(impl, "SquadMemberScrollerElement", "UpdateActorStats"))
                    yield return "L95 premise-changed: the game's deferred squad pass no longer calls " +
                                 "SquadMemberScrollerElement.UpdateActorStats, so arming it repaints nothing " +
                                 "and the AP under the portraits is stale again with the seam still green";
                if (!Reaches(updateStats, "UIActionPoints", "SetActionPoints"))
                    yield return "L95 premise-changed: SquadMemberScrollerElement.UpdateActorStats no longer " +
                                 "re-reads the actor's action points (UIActionPoints.SetActionPoints), which is " +
                                 "the exact number the reported defect showed stale";
            }
        }

        // ─── helpers (self-contained: Program.cs is not partial, so its own copies are out of reach) ───

        private static MethodBase Method(Type owner, string name) => owner?.GetMethod(name, AllMembers);

        private static MethodBase NestedMethod(Type owner, string nested, string name) =>
            Method(owner?.GetNestedType(nested, AllMembers), name);

        private static bool Reaches(MethodBase m, string ownerName, string calleeName) =>
            m != null && Callees(m).Any(c => c.Name == calleeName &&
                                             (ownerName == null || (c.DeclaringType != null &&
                                                                    c.DeclaringType.Name == ownerName)));

        /// <summary>IL offset of the first call to <paramref name="calleeName"/>, or -1.</summary>
        private static int FirstCallTo(MethodBase m, string calleeName)
        {
            foreach (var step in Walk(m))
            {
                if (step.Value.Op != OpCodes.Call && step.Value.Op != OpCodes.Callvirt) continue;
                var callee = Resolve(m, step);
                if (callee != null && callee.Name == calleeName) return step.Value.Pos;
            }
            return -1;
        }

        /// <summary>IL offset of the LAST <c>stsfld</c> to <paramref name="field"/>, or -1.</summary>
        private static int LastStoreTo(MethodBase m, FieldInfo field)
        {
            int last = -1;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op != OpCodes.Stsfld) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos)); } catch { }
                if (f == field) last = step.Value.Pos;
            }
            return last;
        }

        private static MethodBase Resolve(MethodBase m, KeyValuePair<byte[], Step> step)
        {
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            try { return m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                typeArgs, methodArgs); }
            catch { return null; }
        }

        private static List<MethodBase> Callees(MethodBase m)
        {
            var seq = new List<MethodBase>();
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineMethod ||
                    (step.Value.Op != OpCodes.Call && step.Value.Op != OpCodes.Callvirt)) continue;
                var callee = Resolve(m, step);
                if (callee != null) seq.Add(callee);
            }
            return seq;
        }

        private struct Step { public OpCode Op; public int Pos; }

        /// <summary>IL walked with the real operand-size table — a naive byte scan would match operand bytes
        /// and invent edges. Anything unparseable ABANDONS the method rather than guessing.</summary>
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
