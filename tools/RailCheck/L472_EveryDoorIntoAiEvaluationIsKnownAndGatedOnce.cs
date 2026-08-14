using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.AI;
using HarmonyLib;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Statuses;
using PhoenixPoint.Tactical.Levels;

namespace RailCheck
{
    /// <summary>
    /// L472 — EVERY DOOR INTO AI EVALUATION IS A KNOWN DOOR, AND EACH IS GATED EXACTLY ONCE.
    ///
    /// THE SHAPE THIS LAW EXISTS TO STOP (2026-08-14). A client session logged
    /// <c>client AI EVALUATION suppressed — reached outside the turn coroutine
    /// (ExecuteQueuedAbilitiesEffect → ExecuteQueuedAbilitiesSequence)</c> FORTY-THREE times. Nothing
    /// diverged — but the line came from <c>ClientAiEvaluationGate</c>, a SECOND gate sited on
    /// <c>TacticalLevelController.ExecuteAIEvaluationAbilities</c>, one door upstream of the funnel gate on
    /// <c>AIEvaluationAbility.Activate</c>. Two gates over one decision is the failure this repo has already
    /// paid for twice: they cover different door sets, they can disagree, and the narrow one opening for a
    /// single frame is invisible while the broad one still reports green. The narrow gate is REMOVED and this
    /// law is what keeps it removed.
    ///
    /// WHY A SWEEP AND NOT "THE GATE EXISTS". <c>ClientAiEvaluationGate</c> was present, bound and green on
    /// 2026-08-08 while <c>AIEvaluationStatus.OnApply</c> stood open beside it (L320). A gate per known call
    /// path is how every hole here has opened. So this law quantifies over the GAME ASSEMBLY'S OWN IL rather
    /// than over the patches: it walks every method in Assembly-CSharp, finds every caller of the two
    /// evaluation sinks, and requires the caller set to be EXACTLY the enumerated one. A door nobody
    /// enumerated turns this red the moment the game is decompiled again, not the moment a battle desyncs.
    ///
    /// THE CALLER SET, MEASURED (arms (b)-(c)). Two sinks, <c>AIFaction.EvaluateActionsAsync</c>:109 and
    /// <c>EvaluateActionAsync</c>:183. Three callers, all in <c>TacticalFaction</c>:
    ///   • <c>AIUpdateCrt</c>:594 — the AI turn coroutine. Gated by <c>ClientAiGate</c>, which replaces it on
    ///     a client with a hold paced by the host's handoff.
    ///   • <c>EvaluateAiActionsAsync</c>:668 and <c>EvaluateAiActionAsync</c>:662 — thin wrappers, so arm (c)
    ///     sweeps THEIR callers too or the funnel claim stops one hop short. Exactly two:
    ///     <c>AIEvaluationAbility.ExecuteAIEvaluation</c>:33, which is behind the funnel gate, and
    ///     <c>PanicAbility</c>:75.
    ///
    /// PANIC IS A KNOWN, DELIBERATELY UNGATED DOOR — NAMED HERE SO IT CANNOT BE FORGOTTEN AGAIN.
    /// <c>PanicAbility</c>:75 asks <c>EvaluateAiActionAsync</c> for a run-to-safest-position action, so a
    /// panicking actor on a client DOES evaluate locally. It is left alone on the standing judgement that
    /// panic is a REACTION riding the ordinary 0x82 mirror rather than a turn decision, and closing it blind
    /// risks freezing panicked actors on every peer — but it is a real evaluation on a client and it belongs
    /// in the open, as an asserted member of the caller set, not as an unexamined gap. If the decision
    /// changes, this arm is where it is recorded.
    ///
    /// ONE GATE PER DOOR (arms (d)-(e)). Arm (d) requires exactly one mod type on each of the two gated
    /// doors — none is a hole, two is the disagreement above. Arm (e) requires ZERO mod types on
    /// <c>ExecuteAIEvaluationAbilities</c> and on <c>AIEvaluationStatus.OnApply</c>: those are the two call
    /// paths a future fix would reflexively reach for, and either one re-creates the removed second verdict.
    /// L320 owns what the funnel gate DECIDES on each side of the seam; this law owns that the funnel is the
    /// only thing deciding.
    ///
    /// Falsify (each verified RED, then restored): re-add the <c>ExecuteAIEvaluationAbilities</c> prefix →
    /// (e) second-gate; drop the <c>[HarmonyPatch]</c> from <c>ClientAiEvaluationSeamGate</c> →
    /// (d) door-ungated; add a method that calls <c>_aiFaction.EvaluateActionsAsync</c> → (b) unknown-door.
    /// </summary>
    internal static class L472_EveryDoorIntoAiEvaluationIsKnownAndGatedOnce
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The whole caller set of the two evaluation sinks, read off the decompile
        /// (AIFaction.cs:109/183, TacticalFaction.cs:594/662/668) and asserted, not assumed.</summary>
        private static readonly string[] SinkCallers =
        {
            "TacticalFaction.AIUpdateCrt",
            "TacticalFaction.EvaluateAiActionsAsync",
            "TacticalFaction.EvaluateAiActionAsync",
        };

        /// <summary>…and of the two wrappers. Both are one hop from a decision, so a new entry here is a new
        /// door even though the sink's own caller set never moved.</summary>
        private static readonly string[] WrapperCallers =
        {
            "AIEvaluationAbility.ExecuteAIEvaluation",   // behind the funnel gate
            "PanicAbility.Move",                         // known, deliberately ungated — see the summary
        };

        internal static IEnumerable<string> Check(Assembly game)
        {
            var mod = typeof(Multiplayer.Tactical.ClientAiEvaluationSeamGate).Assembly;
            var faction = typeof(TacticalFaction);
            var sinks = new[]
            {
                typeof(AIFaction).GetMethod("EvaluateActionsAsync", All),
                typeof(AIFaction).GetMethod("EvaluateActionAsync", All),
            };
            var wrappers = new[]
            {
                faction.GetMethod("EvaluateAiActionsAsync", All),
                faction.GetMethod("EvaluateAiActionAsync", All),
            };
            var funnel = typeof(AIEvaluationAbility).GetMethod("Activate", All, null, new[] { typeof(object) }, null);
            var turnDoor = faction.GetMethod("AIUpdateCrt", All);

            if (sinks.Any(m => m == null) || wrappers.Any(m => m == null) || funnel == null || turnDoor == null)
            {
                yield return "L472 premise-changed: one of AIFaction.EvaluateActionsAsync/EvaluateActionAsync, " +
                             "TacticalFaction.EvaluateAiActionsAsync/EvaluateAiActionAsync/AIUpdateCrt or " +
                             "AIEvaluationAbility.Activate(object) no longer resolves. The evaluation seam has " +
                             "been reshaped, so every sweep below would quantify over the wrong thing and pass " +
                             "while a client decides its own AI actions.";
                yield break;
            }

            var universe = AssemblyMethods(game);
            if (universe.Count < 5000)
            {
                yield return "L472 scan-empty: the game IL sweep found only " + universe.Count + " method(s), " +
                             "so the caller sets below are meaningless and this law would pass by seeing " +
                             "nothing rather than by finding nothing wrong.";
                yield break;
            }

            // ── (b) THE SINKS ARE REACHED ONLY FROM THE ENUMERATED DOORS ────────────────
            foreach (var v in Sweep(universe, game, sinks, SinkCallers, "sink",
                                    "AIFaction.EvaluateActionsAsync/EvaluateActionAsync")) yield return v;

            // ── (c) …AND SO ARE THE WRAPPERS, ONE HOP UP ────────────────────────────────
            foreach (var v in Sweep(universe, game, wrappers, WrapperCallers, "wrapper",
                                    "TacticalFaction.EvaluateAiActionsAsync/EvaluateAiActionAsync")) yield return v;

            // ── (d) EACH GATED DOOR CARRIES EXACTLY ONE GATE ────────────────────────────
            foreach (var door in new[] { funnel, turnDoor })
            {
                var gates = PatchersOf(mod, door.DeclaringType, door.Name);
                if (gates.Count == 0)
                    yield return "L472 door-ungated: nothing in the mod patches " + door.DeclaringType.Name +
                                 "." + door.Name + " any more. That door reaches AI evaluation on whichever " +
                                 "peer walks through it, so a client decides its own alien moves and the two " +
                                 "peers disagree about where an enemy is — the 2026-08-05 cloaked-enemy " +
                                 "desync, exactly.";
                else if (gates.Count > 1)
                    yield return "L472 door-double-gated: [" + string.Join(", ", gates) + "] all patch " +
                                 door.DeclaringType.Name + "." + door.Name + ". Two verdicts over one " +
                                 "decision can disagree, and the one that opens for a single frame is " +
                                 "invisible while the other still reports green.";
            }

            // ── (e) THE REMOVED NARROWER GATES STAY REMOVED ─────────────────────────────
            foreach (var pair in new[]
                     {
                         Tuple.Create(typeof(TacticalLevelController), "ExecuteAIEvaluationAbilities"),
                         Tuple.Create(typeof(AIEvaluationStatus), "OnApply"),
                     })
            {
                var extra = PatchersOf(mod, pair.Item1, pair.Item2);
                if (extra.Count > 0)
                    yield return "L472 second-gate: [" + string.Join(", ", extra) + "] patch " +
                                 pair.Item1.Name + "." + pair.Item2 + ", which is a CALL PATH into " +
                                 "AIEvaluationAbility.Activate rather than the funnel itself. That is the " +
                                 "gate removed on 2026-08-14: it covered one of the two doors, fired 43 times " +
                                 "in a session while the funnel gate stayed silent, and could drift into " +
                                 "disagreeing with it. Gate the funnel, not the callers.";
            }
        }

        /// <summary>One sweep, run twice. Returns a verdict when the measured caller set differs from the
        /// enumerated one in EITHER direction: an unknown caller is a new door, and a missing one means the
        /// walk itself broke and the green above it is an artefact.</summary>
        private static IEnumerable<string> Sweep(List<MethodBase> universe, Assembly game, MethodInfo[] targets,
                                                 string[] expected, string tag, string what)
        {
            var found = universe.Where(m => Program.Callees(m, game).Any(c => targets.Any(t => Same(c, t))))
                                .Select(Describe).Distinct(StringComparer.Ordinal).ToList();
            if (found.Count == 0)
            {
                yield return "L472 " + tag + "-scan-empty: nothing in the game assembly calls " + what +
                             ". The sweep cannot see the doors it is asserting about, so this law's green is " +
                             "a broken IL walk rather than a closed seam.";
                yield break;
            }

            var unknown = found.Where(n => !expected.Contains(n, StringComparer.Ordinal)).ToList();
            if (unknown.Count > 0)
                yield return "L472 unknown-door: " + what + " is reached from [" + string.Join(", ", unknown) +
                             "], which no gate was derived for. Every enumerated caller is either held by " +
                             "ClientAiGate or refused by ClientAiEvaluationSeamGate; a caller outside that " +
                             "list lets a client evaluate and execute an AI decision of its own, which is " +
                             "law 5's exact prohibition and the desync class this family exists for.";

            var missing = expected.Where(n => !found.Contains(n, StringComparer.Ordinal)).ToList();
            if (missing.Count > 0)
                yield return "L472 " + tag + "-caller-vanished: [" + string.Join(", ", missing) + "] no longer " +
                             "reach " + what + ". Either the game moved the call — in which case the door it " +
                             "moved to is unenumerated and ungated — or this sweep stopped resolving it and " +
                             "the unknown-door arm above is checking nothing.";
        }

        /// <summary>Mod types carrying a class-level <c>[HarmonyPatch]</c> for exactly this game method.</summary>
        private static List<string> PatchersOf(Assembly mod, Type owner, string method)
        {
            Type[] types;
            try { types = mod.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            return types
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).OfType<HarmonyPatch>()
                             .Any(p => p.info != null && p.info.declaringType == owner &&
                                       string.Equals(p.info.methodName, method, StringComparison.Ordinal)))
                .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        /// <summary>An iterator's IL lives in <c>&lt;Name&gt;d__N.MoveNext</c> and a lambda's in
        /// <c>&lt;Name&gt;b__N</c>. Reporting either as an unrelated caller would let a real door hide behind
        /// the compiler's rewrite — and every method in this family is an iterator.</summary>
        private static string Describe(MethodBase m)
        {
            var t = m.DeclaringType;
            string name = m.Name;
            name = Unwrap(name) ?? name;
            while (t != null && t.IsNested && t.Name.StartsWith("<", StringComparison.Ordinal))
            {
                name = Unwrap(t.Name) ?? name;
                t = t.DeclaringType;
            }
            return (t?.Name ?? "?") + "." + name;
        }

        private static string Unwrap(string name)
        {
            int close = name.IndexOf('>');
            return name.Length > 0 && name[0] == '<' && close > 1 ? name.Substring(1, close - 1) : null;
        }

        private static IEnumerable<MethodBase> Members(Type t)
        {
            MethodBase[] members;
            try { members = t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)).ToArray(); }
            catch { return Enumerable.Empty<MethodBase>(); }
            return members;
        }

        private static List<MethodBase> AssemblyMethods(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            return types.SelectMany(Members).ToList();
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
