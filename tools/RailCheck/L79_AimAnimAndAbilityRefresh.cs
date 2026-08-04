using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RailCheck
{
    /// <summary>
    /// L79 — A MIRRORED AIM CHANGE PLAYS THE GAME'S OWN TURN, AND THE BOTTOM BAR REPAINTS ON THE SCREEN
    /// EXIT+ENTER IS FORBIDDEN ON. Two halves of the same reported session (3 instances, 2026-08-04), and
    /// the same shape both times: the mirror reproduces the DESTINATION of a native transition instead of
    /// the transition.
    ///
    /// ─── HALF 1: the swing, not the snap ───
    /// Cycling targets with TAB on the acting peer never re-runs the aim path points.
    /// <c>PathProcessorUtils.GetAimOrPeekPathPoints</c>:620 returns FALSE for an actor already in "Aim Loop"
    /// with no peek — its 90°/180° turn arms are unreachable, both thresholds being the constant
    /// <c>-2f</c> (:590-591) against a dot product that cannot go below -1 — so
    /// <c>IdleAbility.DoAimOrPeek</c>:197 takes its else-branch and the whole visible turn is
    /// <c>TacticalNavigationComponent.FaceWithLerpOnly</c> → <c>NoAnimsFace</c>:1035-1053: a plain
    /// <c>Vector3.Slerp</c> stepped by <c>Timing.Delta * 6f</c>, written with the same
    /// <c>TacticalActorBase.SetForward</c> the mirror already called. The mirror wrote that lerp's ENDPOINT
    /// in one frame — the right facing, reached instantly, which is the teleport every non-acting instance
    /// showed. The repair runs the lerp itself, from A8's own per-frame postfix, because law L82 bans every
    /// method DECLARED on <c>TacticalNavigationComponent</c> from this arc's reachable set.
    ///
    /// The copied RATE is the fragile part, so it has a premise arm: the mod's <c>FacingLerpSpeed</c> const
    /// is compared against the float literals actually compiled into <c>NoAnimsFace</c>. A game patch that
    /// retunes the facing speed turns this red instead of leaving mirrored turns quietly out of step.
    ///
    /// ─── HALF 2: the bar that Exit+Enter may not repaint ───
    /// <c>TacticalUiRepaint.AbilityBarStates</c> excludes every state whose <c>EnterState</c> moves the
    /// state stack (law L63 — a re-entered <c>UIStateShoot</c> runs <c>ExitState</c> twice), and the stated
    /// cost was a stale ability bar for a peer sitting in aim mode. It was reported as a bug the first time
    /// two peers aimed the same sniper: one fired, the other's AP and ability availability did not move.
    /// The repair repaints the two MODULES rather than the state —
    /// <c>UIModuleAbilities.SetAbilities</c>:111 (which re-asks every ability's own enabled test) and
    /// <c>UIModuleActionBar.SetActionBar</c>:238 — gated on each module's own
    /// <c>gameObject.activeInHierarchy</c> (how <c>UIModuleBehavior.SetStateID</c>:21-56 hides one), so it
    /// is generic over every view state instead of a second allow list.
    ///
    /// Falsify: write the destination inside <c>StartTurn</c> → <c>aim-snap-restored</c>; drop the
    /// already-facing test in front of <c>ApplyStance</c>'s own <c>SetForward</c> → <c>aim-snap-ungated</c>;
    /// stop arming the turn at all → <c>aim-turn-unwired</c>; drop the <c>AdvanceTurns()</c> call from the postfix →
    /// <c>aim-turn-frozen</c>; step by <c>Time.deltaTime</c> → <c>aim-turn-wallclock</c>; drop the
    /// <c>_turning.Clear()</c> in <c>Reset</c> → <c>aim-turn-leaks-battle</c>; delete the
    /// current-state guard → <c>repaint-of-popped-state</c>; delete the
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
            var aim = typeof(Multiplayer.Tactical.TacticalAimSync);
            var repaint = typeof(Multiplayer.Tactical.TacticalUiRepaint);

            // ─── (a) THE MIRROR TURNS, IT DOES NOT TELEPORT ───
            var applyStance = Method(aim, "ApplyStance");
            var startTurn = Method(aim, "StartTurn");
            var advance = Method(aim, "AdvanceTurns");
            var aimPostfix = NestedMethod(aim, "AimStatePatch", "Postfix");
            if (applyStance == null || startTurn == null || advance == null)
            {
                yield return "L79 seam-missing: TacticalAimSync.ApplyStance / StartTurn / AdvanceTurns no " +
                             "longer exist, so NOTHING about how a mirrored aim change reaches the screen was " +
                             "checked — the target-switch teleport on every non-acting peer is unguarded";
                yield break;
            }
            if (Reaches(startTurn, null, "SetForward"))
                yield return "L79 aim-snap-restored: StartTurn writes the facing with SetForward. Arming a " +
                             "turn and immediately applying its destination IS the teleport — the soldier " +
                             "lands on the new target in one frame on every mirroring peer while the acting " +
                             "one swings through it, which is the exact defect this law exists for";
            if (!Reaches(applyStance, "TacticalAimSync", "StartTurn"))
                yield return "L79 aim-turn-unwired: ApplyStance no longer starts a turn, so an arriving aim " +
                             "change either poses nothing or poses instantly — a mirrored soldier's facing " +
                             "stops being a reproduction of the game's own aim turn";
            // ApplyStance keeps ONE direct SetForward, and only the branch the game itself snaps on
            // (FaceIn3d:975-980, "already facing it"). That call is also what keeps law L82's
            // mirror-applier-faceless arm honest — it must stay, and it must stay gated.
            if (Reaches(applyStance, null, "SetForward") && !Reaches(applyStance, "Utl", "Equals"))
                yield return "L79 aim-snap-ungated: ApplyStance writes SetForward without the already-facing " +
                             "test in front of it. The one branch the game snaps on is the one where there is " +
                             "nothing to turn; an ungated write is the teleport for every real target switch";
            if (aimPostfix == null || !Reaches(aimPostfix, "TacticalAimSync", "AdvanceTurns"))
                yield return "L79 aim-turn-frozen: the per-frame postfix no longer advances the started " +
                             "turns. A turn is begun and never stepped, so every mirrored soldier stops " +
                             "HALFWAY between its old target and its new one and stays there — strictly " +
                             "worse than the snap this replaced, and silent";
            if (!Reaches(advance, null, "SetForward") || !Reaches(advance, "Vector3", "Slerp"))
                yield return "L79 aim-turn-inert: AdvanceTurns no longer slerps and writes SetForward. Those " +
                             "two calls ARE the game's own facing step (NoAnimsFace:1041-1049); anything else " +
                             "is a second, hand-rolled rotation model on the mirrors";
            if (Reaches(advance, "Time", "get_deltaTime"))
                yield return "L79 aim-turn-wallclock: AdvanceTurns steps by UnityEngine.Time.deltaTime. The " +
                             "game steps by the ACTOR's Timing.Delta, which carries that actor's TimingScale " +
                             "(an unrevealed actor runs at 4x), so mirrored turns would run at a different " +
                             "speed than native ones exactly where the game speeds them up";

            // ─── (b) PREMISE: the copied facing rate is still the game's ───
            var navType = game.GetType("PhoenixPoint.Tactical.Entities.TacticalNavigationComponent");
            var noAnimsFace = navType == null ? null : navType.GetMethod("NoAnimsFace", AllMembers);
            // The body is a compiler-generated iterator; the literal lives in its MoveNext.
            var faceBody = noAnimsFace == null ? null : IteratorBody(navType, "NoAnimsFace") ?? noAnimsFace;
            var rateField = aim.GetField("FacingLerpSpeed", AllMembers);
            var rate = rateField == null ? (float?)null : Convert.ToSingle(rateField.GetRawConstantValue());
            if (noAnimsFace == null || rate == null)
                yield return "L79 premise-changed: TacticalNavigationComponent.NoAnimsFace or the mod's " +
                             "FacingLerpSpeed constant is gone. The mirror's turn rate is a COPIED value; " +
                             "with nothing to compare it against it is a guess";
            else if (!FloatConsts(faceBody).Any(f => Math.Abs(f - rate.Value) < 0.0001f))
                yield return "L79 premise-changed: the game's facing lerp no longer steps by " + rate.Value +
                             " (NoAnimsFace's float literals are now " +
                             string.Join(", ", FloatConsts(faceBody).Select(f => f.ToString()).ToArray()) +
                             "). Mirrored aim turns would run at a different speed than native ones on the " +
                             "acting peer — visible desync, no log line";

            // ─── (c) THE TURN TABLE DIES WITH THE BATTLE (keys are re-derived per battle, law L82) ───
            var turning = aim.GetField("_turning", AllMembers);
            var reset = Method(aim, "Reset");
            if (turning == null || reset == null)
                yield return "L79 aim-turn-leaks-battle: TacticalAimSync._turning / Reset no longer resolve, " +
                             "so nothing proves an in-flight turn is dropped at the battle edge";
            else
            {
                var dict = turning.GetValue(null) as IDictionary;
                var entry = turning.FieldType.GetGenericArguments()[1];
                bool seeded = false;
                if (dict != null) { dict.Add(-4242, Activator.CreateInstance(entry)); seeded = dict.Count > 0; }
                if (!seeded)
                    yield return "L79 aim-turn-leaks-battle: the turn table could not be seeded, so the reset " +
                                 "arm below proves nothing about it";
                else
                {
                    reset.Invoke(null, null);
                    if (dict.Count != 0)
                        yield return "L79 aim-turn-leaks-battle: TacticalAimSync.Reset leaves in-flight turns " +
                                     "in the table. Actor keys are re-derived per battle, so a carried-over " +
                                     "entry rotates whichever actor of the NEXT battle inherits that key";
                    dict.Clear();
                }
            }

            // ─── (d) THE BOTTOM BAR REPAINTS WITHOUT RE-ENTERING THE STATE ───
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

            // ─── (e) PREMISE: the two module entries still have the shape the repaint calls ───
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

        /// <summary>An iterator method's real body lives in its compiler-generated MoveNext, so the literals
        /// a coroutine steps by are invisible on the declaring method itself.</summary>
        private static MethodBase IteratorBody(Type owner, string name)
        {
            var nested = owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                              .FirstOrDefault(t => t.Name.Contains("<" + name + ">"));
            return nested == null ? null : nested.GetMethod("MoveNext", AllMembers);
        }

        private static IEnumerable<float> FloatConsts(MethodBase m)
        {
            foreach (var step in Walk(m))
                if (step.Value.Op == OpCodes.Ldc_R4)
                    yield return BitConverter.ToSingle(step.Key, step.Value.Pos);
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
