using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RailCheck
{
    /// <summary>
    /// L80 — THE ORDER THAT CROSSES IS THE ORDER THE PLAYER GAVE, AND NOTHING ELSE CAN RE-GIVE AN OLD ONE.
    ///
    /// THE REPORT (3 instances, 2026-08-04, build 816128 B). A heavy assault jet-jumped in turn 1; two turns
    /// later the player aimed a shot and the soldier FLEW BACK to where it had started the round while the AP
    /// of the shot were spent. Measured in the peer logs rather than argued: the acting client emitted
    /// <c>Soldier_4 JetJump_AbilityDef (-19.5, 0.1, 7.5)</c> FOUR times across two turns — 21:25:59.443,
    /// 21:26:01.463, 21:27:23.763, 21:28:21.861 — always the SAME cursor-derived position (y=0.1 is
    /// <c>TacticalViewState.GetCursorTargetPosition</c>'s own +0.05 nudge, so it is a picked tile and not a
    /// settled grid position), and three of the four landed in the SAME FRAME as the ability the player had
    /// actually clicked (frame 27968: JetJump then <c>Soldier_2 StandBy</c>; frame 28085: JetJump then
    /// <c>Soldier_4 StandBy</c>). One click, two activations, one of them frozen since a previous turn.
    ///
    /// TWO SEPARATE THINGS HAVE TO HOLD FOR THAT TO BE IMPOSSIBLE, and this law asserts both.
    ///
    /// ─── HALF 1: THE WIRE NAMES AN ABILITY BY ITS DEF, NEVER BY ITS PLACE IN A LIST ───
    /// <c>TacticalActor.GetAbilities()</c> is a live collection whose order is not replicated and not stable
    /// across a turn edge, so an ordinal would name a different ability on the far side the moment a status
    /// added or removed one. A3a already ships <c>AbilityDef.Guid</c> and resolves with
    /// <c>GetAbilityFiltered</c> on that guid; this arm makes that mechanical instead of conventional, so the
    /// cheap "just send the index" refactor turns the harness red rather than shipping a silently mis-aimed
    /// order. It was NOT the cause of the report above — it is the other half of the same question, and the
    /// question deserves an answer that cannot rot.
    ///
    /// ─── HALF 2: A DEAD VIEW STATE MAY NEVER BE RE-ENTERED (this IS the reported bug) ───
    /// <c>TacticalViewState.Update</c>:46-93 asks <c>_stateStack.CurrentState != this</c> THREE times and
    /// returns early each time. That is the engine admitting its own hazard: <c>OnSelect</c> and
    /// <c>OnCancel</c> run INSIDE <c>Update</c> and transition the stack —
    /// <c>UIStateAbilitySelected.OnSelect</c> reaches <c>ActivateAbility</c>:259-277, which calls
    /// <c>ability.Activate(target)</c> and then <c>SwitchToState(new UIStateWaiting(), ClearStackAndPush)</c>,
    /// and <c>StateStack.Clear</c>:107-121 pops the state and runs its <c>Exit</c>.
    ///
    /// <c>TacticalUiRepaint.ViewStateUpdatePatch</c> is a POSTFIX on that very method and does NOT repeat the
    /// guard. So when the player confirms an ability in a frame that is also dirty — routine in co-op, where
    /// every mirrored order marks the screen (law 11), and impossible in single player — the postfix receives
    /// a <c>__instance</c> that has just been exited AND POPPED, sees an allow-listed name, and runs
    /// <c>Repaint</c>: <c>Exit</c> (a no-op on an already-exited state) then <c>Enter</c>.
    ///
    /// That <c>Enter</c> is the whole bug. <c>UIStateAbilitySelected.EnterState</c>:180 re-subscribes
    /// <c>_abilityConfirmationButtonModule.OnAbilityConfirmed += AbilityConfirmed</c>, and that module is
    /// SHARED across every view state (bound once by <c>TacticalViewState.Push</c> →
    /// <c>View.BindFields</c>). The state is off the stack, so nothing will ever <c>Exit</c> it again —
    /// <c>Clear</c> exits only the TOP entry (:113-117), which is correct precisely because every state below
    /// it is already exited. A zombie handler is therefore permanent, and it still holds the
    /// <c>_selectedAbility</c> and the <c>_moveAbilityTarget</c> the player confirmed. From then on EVERY
    /// confirmation of ANY ability on ANY soldier raises the shared event, the zombie fires FIRST (it
    /// subscribed earliest), and <c>AbilityConfirmed</c>:613-630 re-activates last turn's jet jump at last
    /// turn's tile — one frame ahead of the order the player actually gave.
    ///
    /// So the invariant is the engine's own, and it is stated here as the engine states it: this mod may not
    /// <c>Enter</c> a <c>TacticalViewState</c> without first asking the stack whether that state is still
    /// current. Two PREMISE arms guard the reasoning rather than letting it rot — the engine must still
    /// transition inside <c>Update</c> (or the postfix is harmless and this law is theatre), and
    /// <c>UIStateAbilitySelected</c> must still subscribe to the shared confirmation button (or a re-enter
    /// costs nothing).
    ///
    /// Falsify: send an ability index instead of the guid → <c>ability-key-not-def</c>; resolve the far side
    /// by list position → <c>ability-resolve-by-order</c>; delete the current-state guard from the repaint
    /// postfix → <c>repaint-enters-dead-state</c>; patch the engine so <c>Update</c> can no longer transition
    /// → <c>premise-update-cannot-transition</c>; unhook the shared confirm button from
    /// <c>UIStateAbilitySelected</c> → <c>premise-confirm-button-not-shared</c>.
    /// </summary>
    internal static class L80_AbilityKeyStability
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var cmd = typeof(Multiplayer.Tactical.TacticalCommandSync);
            var repaint = typeof(Multiplayer.Tactical.TacticalUiRepaint);

            // ─── HALF 1: the def guid is the key ───

            var capture = cmd.GetMethod("OnAbilityActivated", AllMembers);
            if (capture == null)
                yield return "L80 capture-gone: TacticalCommandSync.OnAbilityActivated no longer exists — the one " +
                             "seam that names the ability on the wire is not there to be checked.";
            else if (!ReadsGuid(capture))
                yield return "L80 ability-key-not-def: OnAbilityActivated no longer reads AbilityDef.Guid, so the " +
                             "order names its ability by something else. A live ability collection has no stable " +
                             "order across peers or across a turn edge — anything but the def guid names a " +
                             "DIFFERENT ability on the far side, which is the wrong-ability-executed class.";

            foreach (var name in new[] { "HandleActivate", "ApplyActivate" })
            {
                var m = cmd.GetMethod(name, AllMembers);
                if (m == null)
                {
                    yield return "L80 resolver-gone: TacticalCommandSync." + name + " no longer exists — one end of " +
                                 "the guid round trip is missing.";
                    continue;
                }
                // The FILTER is a direct call; the guid comparison inside it is a lambda, so its IL lives in a
                // compiler-generated nested type and is invisible on the method itself. Roslyn names that
                // lambda "<HandleActivate>b__N", which is what makes the wider scan precise rather than "any
                // guid read anywhere in the class".
                var callees = Calls(m);
                if (!callees.Any(c => c.Name == "GetAbilityFiltered"))
                    yield return "L80 ability-resolve-by-order: " + name + " no longer resolves the ability through " +
                                 "TacticalActor.GetAbilityFiltered. The only sound lookup is a filter over the def " +
                                 "guid; picking by position replays whatever happens to sit at that index here.";
                if (!ReadsGuid(m) && !LambdaBodies(cmd, name).Any(ReadsGuid))
                    yield return "L80 ability-resolve-guidless: " + name + " no longer compares AbilityDef.Guid, so " +
                                 "whatever filter it now uses is not the identity the sender wrote.";
            }

            // ─── HALF 2: no re-enter of a state the stack has left ───

            var viewState = game.GetType("PhoenixPoint.Tactical.View.TacticalViewState");
            var stateStack = game.GetType("Base.UI.StateStack`1");
            if (viewState == null || stateStack == null)
            {
                yield return "L80 premise-view-types-gone: TacticalViewState or Base.UI.StateStack`1 did not resolve " +
                             "— the repaint's liveness question cannot be checked at all.";
                yield break;
            }

            var update = viewState.GetMethod("Update", AllMembers);
            int selfGuards = update == null ? 0 : Calls(update).Count(c => c.Name == "get_CurrentState");
            if (selfGuards < 2)
                yield return "L80 premise-update-cannot-transition: TacticalViewState.Update asks " + selfGuards +
                             " time(s) whether it is still the current state (it asked 3). The engine no longer " +
                             "admits that a state can be popped inside its own Update, so either the hazard is gone " +
                             "and this law is theatre, or the guard moved and the postfix below is now checking the " +
                             "wrong thing. Re-ground before trusting either half.";

            var confirmModule = game.GetType("PhoenixPoint.Tactical.View.ViewModules.UIModuleAbilityConfirmationButton");
            var abilitySelected = game.GetType("PhoenixPoint.Tactical.View.ViewStates.UIStateAbilitySelected");
            var enterState = abilitySelected == null ? null : abilitySelected.GetMethod("EnterState", AllMembers);
            if (confirmModule == null || enterState == null)
                yield return "L80 premise-confirm-button-not-shared: UIModuleAbilityConfirmationButton or " +
                             "UIStateAbilitySelected.EnterState did not resolve — the reason a re-entered dead state " +
                             "keeps firing old orders cannot be verified.";
            else if (!Calls(enterState).Any(c => c.Name == "add_OnAbilityConfirmed"))
                yield return "L80 premise-confirm-button-not-shared: UIStateAbilitySelected.EnterState no longer " +
                             "subscribes to the shared OnAbilityConfirmed event. Re-entering a dead state would then " +
                             "cost nothing, and the guard below is guarding a hazard that no longer exists.";

            // The repaint may call Enter — but the type that does must ASK. Type-level (nested patch classes
            // included) on purpose: the decision to repaint and the Exit+Enter itself legitimately sit in
            // different methods, and pinning the guard to one of them would fail the next honest refactor.
            var repaintTypes = new List<Type> { repaint };
            repaintTypes.AddRange(repaint.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            var repaintCallees = repaintTypes
                .SelectMany(t => t.GetMethods(AllMembers).Cast<MethodBase>()
                                  .Concat(t.GetConstructors(AllMembers).Cast<MethodBase>()))
                .SelectMany(Calls).ToList();

            bool entersState = repaintCallees.Any(c => c.Name == "Enter" && c.DeclaringType == viewState);
            bool asksCurrent = repaintCallees.Any(c => c.Name == "get_CurrentState" &&
                                                       c.DeclaringType != null &&
                                                       c.DeclaringType.Name == stateStack.Name);
            if (entersState && !asksCurrent)
                yield return "L80 repaint-enters-dead-state: TacticalUiRepaint calls TacticalViewState.Enter without " +
                             "ever reading StateStack.CurrentState. Its postfix on TacticalViewState.Update receives " +
                             "states that were popped INSIDE that Update (a confirmed ability reaches " +
                             "ActivateAbility -> SwitchToState(ClearStackAndPush)), and re-entering one re-subscribes " +
                             "UIStateAbilitySelected.AbilityConfirmed to the SHARED confirmation button forever — " +
                             "after which every later confirmation also re-activates that dead state's ability at its " +
                             "frozen target. That is the 2026-08-04 'the soldier flew back on the jetpack instead of " +
                             "shooting' report, measured one-click-two-activations in the peer logs.";
        }

        // ─── IL, walked with the real operand-size table ───

        /// <summary>The lambda bodies Roslyn lifted out of <paramref name="enclosing"/> into a display class
        /// or the cached-delegate singleton. Named "&lt;Enclosing&gt;b__N", so the enclosing method is still
        /// recoverable and the scan stays scoped to it.</summary>
        private static IEnumerable<MethodBase> LambdaBodies(Type owner, string enclosing)
        {
            string prefix = "<" + enclosing + ">b__";
            foreach (var nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var m in nested.GetMethods(AllMembers))
                    if (m.Name.StartsWith(prefix, StringComparison.Ordinal)) yield return m;
        }

        /// <summary>True if <paramref name="m"/> reads a field named "Guid" (i.e. <c>BaseDef.Guid</c>).
        /// MEASURED against the real metadata: <c>Base.Defs.BaseDef</c> declares <c>public string Guid;</c>
        /// as a FIELD, not a property — so the read is an <c>ldfld</c> and NO <c>get_Guid</c> call token
        /// exists anywhere in the IL. Scanning <see cref="Calls"/> for "get_Guid" therefore asked for
        /// something that can never be there and was red on correct code; the arm only becomes falsifiable
        /// once it looks at the opcode the compiler actually emits.</summary>
        private static bool ReadsGuid(MethodBase m)
        {
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineField) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                typeArgs, methodArgs); } catch { }
                if (f != null && f.Name == "Guid") return true;
            }
            return false;
        }

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

        /// <summary>A naive byte scan would match operand bytes and invent edges, and a law that cries wolf is
        /// a law that gets ignored. Anything unparseable ABANDONS the method rather than guessing.</summary>
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
