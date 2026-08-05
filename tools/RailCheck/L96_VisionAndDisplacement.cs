using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RailCheck
{
    /// <summary>
    /// L96 — A SETTLE RE-TESTS VISION IN BOTH DIRECTIONS, AND A DROP LIST MAY NOT SWALLOW AN ORDER.
    ///
    /// THE REPORT (3 instances, 2026-08-04, build 843264 B @ 22:09:44). Two failures, one shape: something
    /// that is true on the ACTING peer and on no other. (1) A sniper ran on a client and saw a bandit; on
    /// every other window that bandit stayed in fog for rounds, then reappeared and killed a soldier —
    /// measured in the peer logs as <c>MIRROR play Scab Move_AbilityDef shownMode=Hidden</c> and
    /// <c>shownMode=Located</c> on BOTH clients at 22:23. (2) A melee specialist's Dash sprinted on the
    /// acting client while every other window kept the soldier at his old tile, until a later reload settled
    /// him BACK — <c>22:23:17.758 'Dash_AbilityDef' is DECLARED LOCAL</c>.
    ///
    /// WHY L81 WAS GREEN THROUGH ALL OF IT. L81 proves the SEAM: <c>ApplySettle</c> calls
    /// <c>RefreshVisionTowards</c>, which calls the game's own
    /// <c>TacticalFactionVision.UpdateVisibilityOfAllTowardsActor</c> rather than a hand-rolled visibility
    /// system. Every one of those facts stayed true while the behaviour was broken, because native vision is
    /// a RELATION recomputed from BOTH ends — <c>TacticalFactionVision.OnActorMoved</c>:273-306 sends an
    /// actor of the observing faction to <c>UpdateVisibilityForImpl</c> ("what it now SEES") and a foreign one
    /// to <c>ReUpdateVisibilityTowardsActorImpl</c> ("who now sees IT") — and the repair implemented only the
    /// second. A settle for this peer's own soldier therefore re-tested who could see him and never re-tested
    /// what he could see. L81 asked "is the call there"; the call was there and covered half the graph.
    ///
    /// HALF 1 — the settle's repair must run BOTH directions. The own-faction half has no public native
    /// entry (<c>UpdateVisibilityForImpl</c> is private), so it is assembled by INVERTING the same public
    /// method over the foreign actors, which is why the arms look for a second call site and for the foreign
    /// enumeration that only that half needs. A premise arm guards the reasoning: if the engine ever stops
    /// recomputing vision from two different methods, the second half is theatre and the law says so.
    ///
    /// HALF 2 — <c>LocalAbilities</c> is a per-CLASS answer to a per-ACTIVATION question. "Ambient" is a
    /// property of the ACTIVATION, exactly as A5 already established for <c>AttackType</c>: the
    /// <c>TacticalHurtReactionAbility</c> row was added for damage-triggered reactions and swallowed Dash,
    /// a clicked ability of the same family, because <c>RepositionAbility</c> is both. The discriminator is
    /// the game's own <c>TacticalHurtReactionAbilityDef.TriggerOnDamage</c>, which
    /// <c>TacticalHurtReactionAbility.Activate</c>:43-53 branches on and <c>SubscribeEvents</c>:146-152 uses
    /// to decide whether the ability is wired to damage at all. The GENERIC arm is the one that matters: no
    /// drop-list row may unconditionally swallow a game ability that MOVES an actor
    /// (<c>PhoenixPoint.Tactical.Entities.Abilities.IMoveAbility</c>), because a displacement that never
    /// crosses is the failure mode this whole arc exists to prevent — it fires for the row that caused this
    /// report and for the next one anybody adds.
    ///
    /// Falsify: delete the own-faction half of <c>RefreshVisionTowards</c> → <c>settle-vision-one-way</c> +
    /// <c>settle-vision-no-foreign-sweep</c>; revert <c>LocalReason</c> to the unconditional lookup →
    /// <c>drop-list-unconditional</c> and <c>drop-list-swallows-movement</c>; add <c>MoveAbility</c> to
    /// <c>LocalAbilities</c> → <c>drop-list-swallows-movement</c>; patch the engine so <c>OnActorMoved</c>
    /// recomputes through one method → <c>premise-vision-one-directional</c>; make
    /// <c>TacticalHurtReactionAbility.Activate</c> stop reading <c>TriggerOnDamage</c> →
    /// <c>premise-hurt-reaction-undiscriminated</c>.
    /// </summary>
    internal static class L96_VisionAndDisplacement
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>The drop-list rows that are allowed to cover a moving ability BECAUSE the reason is
        /// re-decided per activation rather than per class. Anything else covering an
        /// <c>IMoveAbility</c> is the Dash bug again.</summary>
        private static readonly string[] ConditionalDropRows = { "TacticalHurtReactionAbility" };

        internal static IEnumerable<string> Check(Assembly game)
        {
            var cmd = typeof(Multiplayer.Tactical.TacticalCommandSync);

            // ─── HALF 1: the settle re-tests vision in BOTH directions ───

            var refresh = cmd.GetMethod("RefreshVisionTowards", AllMembers);
            if (refresh == null)
            {
                yield return "L96 settle-repair-gone: TacticalCommandSync.RefreshVisionTowards no longer exists — " +
                             "the settle's vision repair is not there to be checked in either direction.";
            }
            else
            {
                var callees = Calls(refresh);
                int towards = callees.Count(c => c.Name == "UpdateVisibilityOfAllTowardsActor");
                if (towards < 2)
                    yield return "L96 settle-vision-one-way: RefreshVisionTowards reaches " +
                                 "UpdateVisibilityOfAllTowardsActor " + towards + " time(s). Native vision is a " +
                                 "RELATION (TacticalFactionVision.OnActorMoved:279-286 vs :294-301) and the settle " +
                                 "is the ONLY word a mirroring peer gets about the authoritative position, so it " +
                                 "must re-test BOTH 'who sees this actor' and 'what this actor sees'. One call site " +
                                 "is the half that leaves a spotted enemy in fog on every peer but the one that " +
                                 "walked (the 2026-08-04 sniper report), and L81 stays green through it.";
                if (!callees.Any(c => c.Name == "get_Actors"))
                    yield return "L96 settle-vision-no-foreign-sweep: RefreshVisionTowards no longer enumerates a " +
                                 "faction's Actors. The own-faction half has no public native entry " +
                                 "(UpdateVisibilityForImpl is private), so it exists ONLY as the inverted sweep over " +
                                 "the foreign actors — without that enumeration whatever is left cannot be it.";

                // ─── the LOCATED half: distance, not sight ───
                // UpdateVisibilityOfAllTowardsActor raises Revealed and ONLY Revealed
                // (ReUpdateVisibilityTowardsActorImpl:651-662), so without a second arm a mirroring peer knows
                // an enemy is there only once it can SEE it — the orange beacon is lost between settles.
                var locate = cmd.GetMethod("LocateByDistance", AllMembers);
                if (locate == null || !callees.Any(c => c.Name == "LocateByDistance"))
                    yield return "L96 settle-located-gone: the settle repair no longer reaches LocateByDistance, so " +
                                 "it raises Revealed and nothing else. KnownState.Located — in DetectionRange, no " +
                                 "line of sight — is then unreachable on every peer whose only word about the " +
                                 "authoritative position is this settle, and the 'something is there' beacon simply " +
                                 "does not appear until the next faction turn edge recomputes it.";
                else
                {
                    var lc = Calls(locate);
                    if (!lc.Any(c => c.Name == "IncrementKnownCounter"))
                        yield return "L96 located-not-raised: LocateByDistance never calls " +
                                     "TacticalFactionVision.IncrementKnownCounter. It is the ONLY public entry that " +
                                     "can raise Located (UpdateVisibilityForImpl, which the game uses for exactly " +
                                     "this list, is private) — without it the method computes a distance and " +
                                     "throws the answer away.";
                    else if (!LoadsInt(locate, (int)16 /* KnownState.Located */))
                        yield return "L96 located-wrong-state: LocateByDistance calls IncrementKnownCounter but " +
                                     "never loads KnownState.Located (16). Raising Revealed by distance alone would " +
                                     "show an enemy through walls, which is precisely what law L81 bans; raising " +
                                     "Hidden would do nothing at all.";
                    if (!lc.Any(c => c.Name == "LesserThanOrEqualTo") || !lc.Any(c => c.Name == "get_magnitude"))
                        yield return "L96 located-ungated: LocateByDistance no longer compares a MAGNITUDE with " +
                                     "Utl.LesserThanOrEqualTo, so the Located raise is not gated on range at all. " +
                                     "The game's own rule is GatherKnowableActors:640-647 — within " +
                                     "TacticalLevelControllerDef.DetectionRange — and an ungated raise beacons every " +
                                     "enemy on the map to every peer, which is a worse lie than the missing beacon " +
                                     "this arm was added to fix.";
                }
                if (!ReadsField(refresh, "DetectionRange") && !callees.Any(c => c.Name == "get_DetectionRange"))
                    yield return "L96 settle-range-not-native: RefreshVisionTowards no longer reads " +
                                 "TacticalLevelControllerDef.DetectionRange, so whatever range it now hands to the " +
                                 "native vision calls and to LocateByDistance is this mod's number rather than the " +
                                 "game's — two peers on different numbers see different boards.";
            }

            var vision = game.GetType("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
            if (vision == null)
            {
                yield return "L96 premise-vision-type-gone: PhoenixPoint.Tactical.Levels.TacticalFactionVision did " +
                             "not resolve — nothing about the settle's vision repair can be checked.";
            }
            else
            {
                var onMoved = vision.GetMethods(AllMembers)
                                    .FirstOrDefault(m => m.Name == "OnActorMoved" && m.GetParameters().Length == 1);
                var reached = onMoved == null
                    ? new List<string>()
                    : Calls(onMoved).Select(c => c.Name).ToList();
                if (onMoved == null ||
                    !reached.Contains("UpdateVisibilityForImpl") ||
                    !reached.Contains("ReUpdateVisibilityTowardsActorImpl"))
                    yield return "L96 premise-vision-one-directional: TacticalFactionVision.OnActorMoved no longer " +
                                 "recomputes through BOTH UpdateVisibilityForImpl and " +
                                 "ReUpdateVisibilityTowardsActorImpl. The settle repair's whole shape — one native " +
                                 "call per direction — is derived from that split; if the engine collapsed it, the " +
                                 "second half is copying a distinction the game no longer makes.";

                var pub = vision.GetMethod("UpdateVisibilityOfAllTowardsActor", AllMembers);
                if (pub == null || !pub.IsPublic)
                    yield return "L96 premise-towards-entry-gone: " +
                                 "TacticalFactionVision.UpdateVisibilityOfAllTowardsActor is no longer a public " +
                                 "method. It is the ONLY native entry the repair is built from, in both directions.";

                var impl = vision.GetMethod("UpdateVisibilityForImpl", AllMembers);
                if (impl != null && impl.IsPublic)
                    yield return "L96 premise-own-half-now-public: TacticalFactionVision.UpdateVisibilityForImpl " +
                                 "became public. The inverted sweep exists only because it was not — replace it " +
                                 "with this one call per settled actor and drop the |own| x |foreign| cost.";
            }

            // ─── HALF 2: a per-class drop may not swallow a per-activation order ───

            var localReason = cmd.GetMethod("LocalReason", AllMembers);
            if (localReason == null)
            {
                yield return "L96 drop-decision-gone: TacticalCommandSync.LocalReason no longer exists — the one " +
                             "place that decides whether an ability crosses is not there to be checked.";
            }
            else if (!ReadsField(localReason, "TriggerOnDamage") &&
                     !Calls(localReason).Any(c => c.Name == "IsOrderedHurtReaction"))
            {
                yield return "L96 drop-list-unconditional: LocalReason drops purely by CLASS again. 'Ambient' is a " +
                             "property of the ACTIVATION — the same distinction IsAutonomous already draws with " +
                             "AttackType — and TacticalHurtReactionAbility is a FAMILY that contains both damage " +
                             "reactions and ordinary clicked abilities. Without the game's own " +
                             "TacticalHurtReactionAbilityDef.TriggerOnDamage discriminator, a player's Dash is " +
                             "declared local and its displacement never crosses (2026-08-04).";
            }

            var move = game.GetType("PhoenixPoint.Tactical.Entities.Abilities.IMoveAbility");
            if (move == null)
            {
                yield return "L96 premise-move-marker-gone: IMoveAbility did not resolve, so 'this drop row " +
                             "swallows an ability that MOVES an actor' cannot be asked at all.";
            }
            else
            {
                var movers = SafeTypes(game).Where(t => !t.IsInterface && !t.IsAbstract && move.IsAssignableFrom(t))
                                            .ToList();
                foreach (var row in Multiplayer.Tactical.TacticalCommandSync.LocalAbilities.Keys)
                {
                    if (ConditionalDropRows.Contains(row.Name)) continue;
                    var swallowed = movers.Where(t => row.IsAssignableFrom(t)).Select(t => t.Name).ToArray();
                    if (swallowed.Length > 0)
                        yield return "L96 drop-list-swallows-movement: LocalAbilities row " + row.Name +
                                     " unconditionally drops " + string.Join(", ", swallowed) + ", which " +
                                     "implement(s) IMoveAbility. A displacement that never crosses leaves every " +
                                     "other peer holding a stale position until a settle rubber-bands the actor " +
                                     "back — the Dash report. Either the row must re-decide per activation (add it " +
                                     "to ConditionalDropRows with the game marker that discriminates it) or it must " +
                                     "not cover a moving ability.";
                }
            }

            var hurt = game.GetType("PhoenixPoint.Tactical.Entities.Abilities.TacticalHurtReactionAbility");
            var activate = hurt == null ? null : hurt.GetMethod("Activate", AllMembers);
            if (hurt == null || activate == null || !ReadsField(activate, "TriggerOnDamage"))
                yield return "L96 premise-hurt-reaction-undiscriminated: TacticalHurtReactionAbility.Activate no " +
                             "longer branches on TriggerOnDamage. That branch IS the discriminator the un-drop " +
                             "relies on — false takes PlayAction(HurtReaction_Implementation, parameter), i.e. the " +
                             "caller's own target, and true ignores the parameter for GetHurtReactionTarget(). " +
                             "Without it there is no sound way to tell an ordered reposition from an ambient one.";
        }

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }

        /// <summary>True if <paramref name="m"/> reads a field with this name. Def flags are public FIELDS in
        /// this codebase (<c>TacticalHurtReactionAbilityDef.TriggerOnDamage</c> is
        /// <c>public bool TriggerOnDamage = true;</c>), so the read is an <c>ldfld</c> and no getter token
        /// exists to scan for — the same correction L80's <c>ReadsGuid</c> had to make.</summary>
        private static bool ReadsField(MethodBase m, string name)
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
                if (f != null && f.Name == name) return true;
            }
            return false;
        }

        /// <summary>Does this method ever push <paramref name="value"/> as an int constant? Enum arguments
        /// are invisible to a callee list — <c>IncrementKnownCounter(actor, Located, 1, true)</c> and
        /// <c>(actor, Revealed, 1, true)</c> are the same edge — so the constant is the only place the
        /// STATE being raised is written down.</summary>
        private static bool LoadsInt(MethodBase m, int value)
        {
            foreach (var step in Walk(m))
            {
                var op = step.Value.Op;
                int got;
                if (op == OpCodes.Ldc_I4) got = BitConverter.ToInt32(step.Key, step.Value.Pos);
                else if (op == OpCodes.Ldc_I4_S) got = (sbyte)step.Key[step.Value.Pos];
                else if (op == OpCodes.Ldc_I4_M1) got = -1;
                else if (op.Value >= OpCodes.Ldc_I4_0.Value && op.Value <= OpCodes.Ldc_I4_8.Value)
                    got = op.Value - OpCodes.Ldc_I4_0.Value;
                else continue;
                if (got == value) return true;
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
