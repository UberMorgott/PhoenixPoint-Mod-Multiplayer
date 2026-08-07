using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Base.Entities;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;

namespace RailCheck
{
    /// <summary>
    /// L178 — AN ABILITY WHOSE OWN PLAYBACK CANNOT READ WHAT THE SEAM SHIPS NEVER RIDES THE SEAM.
    ///
    /// THE SEAM SHIPS EXACTLY ONE SHAPE. <c>TacticalCommandSync</c> encodes and decodes a
    /// <c>TacticalAbilityTarget</c> and nothing else (<c>TacAbilityTargetCodec</c>), because that is the one
    /// payload <c>TacticalAbility.Activate</c>:1086 keeps (<c>LastAbilityTarget = parameter as ...</c>). An
    /// ability activated with anything ELSE therefore crosses as a NULL — silently, since a parameter the codec
    /// has no field for is not a dropped FIELD it can name.
    ///
    /// THE INCIDENT (2026-08-06, ambush mission, and it cost the whole mission).
    /// <c>OpenCrateAbility.OnActorAbilityExecuted</c>:96 activates with a <c>CrateComponent</c>, and
    /// <c>OpenCrate</c>:55-58 opens with the HARD cast <c>(CrateComponent)action.Param</c> and immediately
    /// dereferences it. The wire carried <c>&lt;no target&gt;</c> (client log 23:20:24.464, nonce=113; host
    /// mirror 23:18:19.606), the mirror logged <c>Parameter: &lt;&gt;</c> and threw
    /// <c>NullReferenceException … &lt;OpenCrate&gt;d__10.MoveNext</c> followed by the engine's own
    /// <c>Broken coroutine call chain</c>. A torn coroutine never reaches <c>PlayingAction.CompleteAction</c>,
    /// so <c>ClearPlayingAction</c> never runs and the actor never leaves <c>ExecutingAbilities</c> —
    /// <c>holding the settle … 2s/4s/6s/8s</c>, forced at ten. On the HOST, replaying a client's INTENT, there
    /// is no ceiling at all (<c>ClientTick</c> is a client's), and
    /// <c>TacticalView.IsWaitingForActiveAbilitiesAndMapUpdate</c>:864 folds <c>ShouldViewWaitForMe</c> over the
    /// WHOLE MAP: during a non-player faction's turn L145's narrowing does not apply, so every turn-pump wait in
    /// <c>TacticalLevelController</c> (:1245/:1260/:1280/:1296/:1318/:1329) spun forever. That is the reported
    /// freeze — enemy turn, no enemies, Alt+F4.
    ///
    /// THE LAW IS THE CLASS, NOT THE CRATE. It reads the GAME's IL: for every <c>TacticalAbility</c> subclass,
    /// every hard cast (<c>castclass</c>/<c>unbox.any</c>) applied directly to <c>PlayingAction.Param</c>. If
    /// the cast's type cannot accept a <c>TacticalAbilityTarget</c>, that ability's playback requires a
    /// parameter this seam cannot carry, and <see cref="TacticalCommandSync.LocalAbilities"/> must declare it —
    /// whichever ability it is, including one a future patch or another mod adds. <c>isinst</c> is deliberately
    /// NOT counted: <c>action.Param as T</c> answers null and the ability is written to survive it
    /// (<c>InteractWithObjectAbility.InteractCrt</c>:135-139 falls back to <c>GetTargets().FirstOrDefault()</c>).
    ///
    /// THE ARMS. (a) is the outcome, over every ability the scan finds. (b) is the vacuity guard — a walker
    /// that resolves nothing would pass (a) in silence, so the scan must still SEE both shapes: at least one
    /// uncarriable ability and at least one carriable one. (c) is the POSITIVE CONTROL: <c>MoveAbility</c> casts
    /// <c>PlayingAction.Param</c> just as hard, to <c>TacticalAbilityTarget</c>, and must stay a rider — the
    /// cheap wrong version of this law ("any hard cast means local") would ground every order in the mod.
    ///
    /// Falsify: delete the <c>OpenCrateAbility</c> row from <c>LocalAbilities</c> → <c>L178 uncarriable-rider
    /// OpenCrateAbility</c>; make the scan count <c>isinst</c> too → <c>L178 carriable-ability-grounded
    /// MoveAbility</c> is still green but <c>L178 uncarriable-rider</c> fires for every <c>as</c>-guarded
    /// ability; make it match every cast rather than casts of <c>Param</c> → the same.
    /// </summary>
    internal static class L178_SeamCarriesWhatTheAbilityReads
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var param = typeof(PlayingAction).GetField("Param", BindingFlags.Public | BindingFlags.Instance);
            var declared = TacticalCommandSync.LocalAbilities;
            if (param == null || param.FieldType != typeof(object) || declared == null || declared.Count == 0)
            {
                yield return "L178 premise-changed: Base.Entities.PlayingAction.Param is no longer a public " +
                             "object field, or TacticalCommandSync.LocalAbilities is empty. This law reads the " +
                             "game's IL for hard casts of that field and checks the answer against that map; " +
                             "with either gone it is asserting nothing.";
                yield break;
            }

            var game = typeof(TacticalAbility).Assembly;
            var uncarriable = new SortedDictionary<string, string>(StringComparer.Ordinal);
            int carriable = 0;

            foreach (var t in game.GetTypes())
            {
                if (!typeof(TacticalAbility).IsAssignableFrom(t) || t.IsAbstract) continue;
                foreach (var cast in ParamCasts(t, param))
                {
                    if (cast.IsAssignableFrom(typeof(TacticalAbilityTarget))) { carriable++; continue; }
                    if (!uncarriable.ContainsKey(t.FullName)) uncarriable[t.FullName] = cast.Name;
                }
            }

            // ── (b) VACUITY: the walker must still see both shapes ───────────
            if (uncarriable.Count == 0 || carriable == 0)
            {
                yield return "L178 premise-changed: the IL scan found " + uncarriable.Count + " ability(ies) " +
                             "that hard-cast PlayingAction.Param to something a TacticalAbilityTarget cannot " +
                             "satisfy and " + carriable + " that cast it to one. Both were non-zero when this " +
                             "law was written (OpenCrateAbility/CrateComponent and RagdollDieAbility/" +
                             "DeathReport against MoveAbility/ShootAbility/JetJumpAbility and a dozen more), so " +
                             "a zero means the walker stopped resolving, not that the game stopped casting — " +
                             "and every arm below is then proved about an empty set.";
                yield break;
            }

            // ── (a) THE OUTCOME ──────────────────────────────────────────────
            foreach (var kv in uncarriable)
            {
                var type = game.GetType(kv.Key);
                if (type != null && declared.Keys.Any(k => k.IsAssignableFrom(type))) continue;
                yield return "L178 uncarriable-rider " + kv.Key + ": its playback hard-casts " +
                             "PlayingAction.Param to " + kv.Value + ", which a TacticalAbilityTarget is not — " +
                             "and TacticalCommandSync.LocalAbilities does not declare it local, so the seam " +
                             "relays it. The codec has no field for that parameter, so the wire carries " +
                             "'<no target>' and the cast lands on null: the coroutine tears, ClearPlayingAction " +
                             "never runs, the actor never leaves ExecutingAbilities, and the first non-player " +
                             "turn after that spins TacticalLevelController's wait fold forever. That is the " +
                             "2026-08-06 frozen ambush, verbatim.";
            }

            // ── (c) POSITIVE CONTROL: a hard cast is not by itself a reason ──
            foreach (var rider in new[] { typeof(MoveAbility), typeof(ShootAbility) })
            {
                if (!ParamCasts(rider, param).Any(c => c.IsAssignableFrom(typeof(TacticalAbilityTarget))))
                    continue;   // this build's shape moved; (b) already guards the empty case
                if (declared.Keys.Any(k => k.IsAssignableFrom(rider)))
                    yield return "L178 carriable-ability-grounded " + rider.Name + ": an ability that reads " +
                                 "exactly what the seam ships has been declared local anyway. Grounding the " +
                                 "carriable set is not a safe over-approximation — it is the arc's whole " +
                                 "content going local one ability at a time, silently, with a 'declared local' " +
                                 "log line where an order used to be.";
            }
        }

        /// <summary>Every type a hard cast of <c>PlayingAction.Param</c> demands, over a type's own methods AND
        /// its compiler-generated state machines (the coroutine IS where playback reads the parameter).
        ///
        /// A SECOND IL WALKER, deliberately: <see cref="Program.CallSites"/> resolves <c>InlineMethod</c> call
        /// edges and cannot express this question at all, which is about an <c>ldfld</c> and the <c>castclass</c>
        /// standing immediately behind it. Adjacency is exact for the shape being looked for — Roslyn emits
        /// <c>ldfld Param; castclass T</c> for <c>(T)action.Param</c> with nothing between. Anything
        /// unparseable ABANDONS the method rather than guessing, same rule as Program's walker: under-reporting
        /// is survivable, a false red is not.</summary>
        private static IEnumerable<Type> ParamCasts(Type ability, FieldInfo param)
        {
            foreach (var m in Bodies(ability))
                foreach (var t in ParamCasts(m, param))
                    yield return t;
        }

        private static IEnumerable<MethodBase> Bodies(Type ability)
        {
            foreach (var m in ability.GetMethods(All)) yield return m;
            foreach (var n in ability.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var m in n.GetMethods(All)) yield return m;
        }

        private static IEnumerable<Type> ParamCasts(MethodBase m, FieldInfo param)
        {
            byte[] il = null;
            try { il = m.GetMethodBody() == null ? null : m.GetMethodBody().GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                           ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;

            int i = 0;
            bool onParam = false;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                OpCode op;
                if (!Ops.TryGetValue(code, out op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;

                if (onParam && (op == OpCodes.Castclass || op == OpCodes.Unbox_Any))
                {
                    Type cast = null;
                    try { cast = m.Module.ResolveType(BitConverter.ToInt32(il, i), typeArgs, methodArgs); }
                    catch { }
                    if (cast != null) yield return cast;
                }
                onParam = false;
                if (op == OpCodes.Ldfld)
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i), typeArgs, methodArgs); }
                    catch { }
                    onParam = f != null && f.MetadataToken == param.MetadataToken &&
                              f.DeclaringType == param.DeclaringType;
                }
                i += size;
            }
        }

        private static readonly Dictionary<short, OpCode> Ops = BuildOps();

        private static Dictionary<short, OpCode> BuildOps()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); map[op.Value] = op; }
            return map;
        }

        private static int OperandSize(OperandType t, byte[] il, int i)
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
                    if (i + 4 > il.Length) return -1;
                    return 4 + 4 * BitConverter.ToInt32(il, i);
                default: return -1;
            }
        }
    }
}
