using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RailCheck
{
    /// <summary>
    /// L79 — THE BOTTOM BAR REPAINTS ON THE SCREEN EXIT+ENTER IS FORBIDDEN ON.
    /// <c>TacticalUiRepaint.AbilityBarStates</c> excludes every state whose <c>EnterState</c> moves the
    /// state stack (law L63 — a re-entered <c>UIStateShoot</c> runs <c>ExitState</c> twice), and the stated
    /// cost was a stale ability bar for a peer sitting in aim mode. It was reported as a bug the first time
    /// two peers looked at the same sniper: one fired, the other's AP and ability availability did not move.
    /// The repair repaints the two MODULES rather than the state —
    /// <c>UIModuleAbilities.SetAbilities</c>:111 (which re-asks every ability's own enabled test) and
    /// <c>UIModuleActionBar.SetActionBar</c>:238 — gated on each module's own
    /// <c>gameObject.activeInHierarchy</c> (how <c>UIModuleBehavior.SetStateID</c>:21-56 hides one), so it
    /// is generic over every view state instead of a second allow list.
    ///
    /// THE AIM HALF OF THIS LAW IS GONE (2026-08-04, user decision). The mirrored aim STANCE — the shared
    /// <c>(actorKey, targetKey)</c> table, its 0x85/0x86 surfaces, the auto-enter into a watcher's aim view
    /// and the mirrored facing lerp — was removed entirely: a peer watching a soldier someone else aims now
    /// keeps its own free camera and its own screen. The arms that guarded HOW a mirrored aim change reached
    /// the screen (<c>aim-snap-restored</c>, <c>aim-snap-ungated</c>, <c>aim-turn-unwired</c>,
    /// <c>aim-turn-frozen</c>, <c>aim-turn-inert</c>, <c>aim-turn-wallclock</c>, the copied-facing-rate
    /// premise and <c>aim-turn-leaks-battle</c>) would now all be red against the CORRECT behaviour, and a
    /// law that forbids the correct behaviour is worse than no law — so they were deleted with the code they
    /// described rather than left to be muted. The shot itself is untouched: it rides the ordinary 0x82
    /// command seam, and the camera work on a peer that has the firing soldier selected is native.
    ///
    /// Falsify: delete the current-state guard → <c>repaint-of-popped-state</c>; delete the
    /// <c>RepaintModules</c> call → <c>bar-repaint-unwired</c>; hand-roll the bar instead of calling the
    /// two native module entries → <c>bar-repaint-hand-rolled</c>; make it Exit+Enter the state →
    /// <c>bar-repaint-transitions</c>.
    /// </summary>
    internal static class L79AimAnimAndAbilityRefresh
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        public static IEnumerable<string> Check(Assembly game)
        {
            var repaint = typeof(Multiplayer.Tactical.TacticalUiRepaint);

            // ─── (a) THE BOTTOM BAR REPAINTS WITHOUT RE-ENTERING THE STATE ───
            var repaintModules = Method(repaint, "RepaintModules");
            var flush = NestedMethod(repaint, "ViewStateUpdatePatch", "Postfix");
            if (repaintModules == null)
            {
                yield return "L79 bar-repaint-unwired: TacticalUiRepaint.RepaintModules is gone. A peer " +
                             "sitting in aim mode is the ONE screen Exit+Enter may not repaint (law L63), so " +
                             "without it its AP and ability availability freeze at the pre-shot values while " +
                             "another peer fires the same soldier";
                yield break;
            }
            if (flush == null || !Reaches(flush, "TacticalUiRepaint", "RepaintModules"))
                yield return "L79 bar-repaint-unwired: the dirty flush no longer reaches RepaintModules, so " +
                             "the module repaint is dead code and the aim-mode peer keeps a stale bar";
            if (flush != null && !Reaches(flush, "StateStack`1", "get_CurrentState"))
                yield return "L79 repaint-of-popped-state: the dirty flush no longer checks that the state it " +
                             "is about to repaint is still the CURRENT one — the guard TacticalViewState" +
                             ".Update:53/74 applies to itself three times. Confirming an ability transitions " +
                             "INSIDE Update (ActivateAbility:259-277 → SwitchToState(ClearStackAndPush) → " +
                             "StateStack.Clear:107-121), so the repaint Exit+ENTERs an already-popped state; " +
                             "UIStateAbilitySelected.EnterState:180 re-subscribes AbilityConfirmed on the " +
                             "SHARED confirmation module and Clear only ever exits the TOP, so that zombie " +
                             "fires its old ability FIRST on every later confirmation";
            if (!Reaches(repaintModules, "UIModuleAbilities", "SetAbilities"))
                yield return "L79 bar-repaint-hand-rolled: RepaintModules no longer calls the native " +
                             "UIModuleAbilities.SetAbilities. That method is what re-asks every ability's own " +
                             "enabled test; anything else paints availability from a second source of truth";
            if (!Reaches(repaintModules, "UIModuleActionBar", "SetActionBar"))
                yield return "L79 bar-repaint-hand-rolled: RepaintModules no longer calls the native " +
                             "UIModuleActionBar.SetActionBar, so the AP bar is not re-read from the actor";
            // Owner-qualified on purpose: the bare names would match this method's own
            // `using (SyncApplyScope.Enter())`, and a law that cries wolf is a law that gets ignored.
            foreach (var banned in new[] { "Exit", "Enter", "SwitchToState", "SwitchToPreviousState" })
                if (Reaches(repaintModules, "TacticalViewState", banned))
                {
                    yield return "L79 bar-repaint-transitions: RepaintModules reaches " + banned + ". This " +
                                 "path exists precisely BECAUSE the states it runs on may not be re-entered — " +
                                 "UIStateShoot.EnterState:348/:352 moves the state stack, so an Exit+Enter " +
                                 "runs its ExitState twice (law L63, 7933bbe)";
                    break;
                }

            // ─── (b) PREMISE: the two module entries still have the shape the repaint calls ───
            var actorType = game.GetType("PhoenixPoint.Tactical.Entities.TacticalActor");
            var inputType = game.GetType("Base.Input.InputController");
            var abilitiesModule = game.GetType("PhoenixPoint.Tactical.View.ViewModules.UIModuleAbilities");
            var actionBarModule = game.GetType("PhoenixPoint.Tactical.View.ViewModules.UIModuleActionBar");
            var setAbilities = abilitiesModule == null || actorType == null || inputType == null
                ? null
                : abilitiesModule.GetMethod("SetAbilities", AllMembers, null, new[] { actorType, inputType }, null);
            var setActionBar = actionBarModule == null || actorType == null
                ? null
                : actionBarModule.GetMethod("SetActionBar", AllMembers, null, new[] { actorType }, null);
            if (setAbilities == null || setActionBar == null)
                yield return "L79 premise-changed: UIModuleAbilities.SetAbilities(TacticalActor, " +
                             "InputController) / UIModuleActionBar.SetActionBar(TacticalActor) no longer " +
                             "resolve with those shapes. The repaint is written against them verbatim";
            else if (!ClosureReaches(setAbilities, new[] { "IsEnabled", "GetDisabledState" }, 4))
                yield return "L79 premise-changed: UIModuleAbilities.SetAbilities no longer re-asks an " +
                             "ability whether it is enabled (SetAbilityLists → UpdateButton:452). Calling it " +
                             "would then repaint the SAME lit icons after a shot spent the AP — the repair " +
                             "would run and change nothing, this repo's dominant bug shape";
            var moduleBehavior = game.GetType("Base.UI.UIModuleBehavior");
            var setStateId = moduleBehavior == null ? null : moduleBehavior.GetMethod("SetStateID", AllMembers);
            if (setStateId == null || !Reaches(setStateId, "GameObject", "SetActive"))
                yield return "L79 premise-changed: UIModuleBehavior.SetStateID no longer hides a module by " +
                             "deactivating its GameObject, so the repaint's activeInHierarchy gate no longer " +
                             "means \"on screen\". It fails OPEN (everything gets refreshed), which is safe " +
                             "but no longer what the comment claims — say so rather than let the premise rot";
        }

        // ─── helpers (self-contained: Program.cs is not partial, so its own copies are out of reach) ───

        private static MethodBase Method(Type owner, string name) => owner?.GetMethod(name, AllMembers);

        private static MethodBase NestedMethod(Type owner, string nested, string name) =>
            Method(owner?.GetNestedType(nested, AllMembers), name);

        private static bool Reaches(MethodBase m, string ownerName, string calleeName) =>
            m != null && Callees(m).Any(c => c.Name == calleeName &&
                                             (ownerName == null || (c.DeclaringType != null &&
                                                                    c.DeclaringType.Name == ownerName)));

        /// <summary>Transitive callee walk, bounded by depth and to the ROOT's own declaring type — enough
        /// to answer "does this entry point still end up asking X?" without dragging in the whole UI.</summary>
        private static bool ClosureReaches(MethodBase root, string[] names, int depth)
        {
            var seen = new HashSet<MethodBase>();
            var frontier = new List<MethodBase> { root };
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in frontier)
                    foreach (var c in Callees(m))
                    {
                        if (names.Contains(c.Name)) return true;
                        if (c.DeclaringType == root.DeclaringType && seen.Add(c)) next.Add(c);
                    }
                frontier = next;
            }
            return false;
        }

        private static List<MethodBase> Callees(MethodBase m)
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

        /// <summary>IL walked with the real operand-size table — a naive byte scan would match operand bytes
        /// and invent edges, and a law that cries wolf is a law that gets ignored. Anything unparseable
        /// ABANDONS the method rather than guessing.</summary>
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
