using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Multiplayer.Network;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Statuses;

namespace RailCheck
{
    /// <summary>
    /// L320 — A CLIENT NEVER REACHES AI EVALUATION BY ANY DOOR, AND THE HOST STILL EVALUATES.
    ///
    /// THE HOLE (2026-08-08, `69d75d2`). <c>ClientAiEvaluationGate</c> covered ONE caller of
    /// <c>AIEvaluationAbility</c>: <c>TacticalLevelController.ExecuteAIEvaluationAbilities</c>:1257, which
    /// reaches it as <c>ability.Execute(null)</c>. That is not the only caller.
    /// <c>AIEvaluationStatus.OnApply</c>:17 calls <c>GetAbility&lt;AIEvaluationAbility&gt;().Activate()</c>
    /// DIRECTLY under <c>ExecuteActionsOnApply</c> — no coroutine, no turn machinery, nothing that gate can
    /// see. A status merely MIRRORED onto a client therefore walked into <c>ExecuteAIEvaluation</c> →
    /// <c>TacticalFaction.EvaluateAiActionsAsync</c> → <c>AIFaction.EvaluateActionsAsync</c>, and the client
    /// decided for itself — law 5's exact prohibition, reached without tripping a single gate.
    ///
    /// WHY THIS LAW IS NOT "THE GATE EXISTS". A gate per known call path is precisely how the hole opened:
    /// the previous one was present, bound, and green while a second door stood open beside it. So the
    /// subject here is the OUTCOME on both sides of the seam — a client reaches no evaluation through any
    /// door, a host reaches it through all of them — plus the structural fact that makes "any door" a
    /// finite claim rather than a hope.
    ///
    /// THE CLOSURE ARGUMENT, ASSERTED RATHER THAN ASSUMED (arm (a)). <c>ExecuteAIEvaluation</c> is PRIVATE
    /// to <c>AIEvaluationAbility</c>, so by the CLR's own accessibility rules nothing outside that class can
    /// name it, and inside it exactly one method does: <c>Activate</c>, which hands it to <c>PlayAction</c>
    /// as a delegate. Blocking <c>Activate</c> therefore closes the CLASS, not the instance — there is no
    /// third door to enumerate. If the method ever loses its <c>private</c> or gains a second referrer, that
    /// argument dies and this arm says so before anybody discovers it in a battle.
    ///
    /// EVERY DOOR CALLS THROUGH THE BASE SLOT, WHICH IS THE WHOLE HAZARD (arm (b)). Measured, not assumed:
    /// the shipped IL of <c>AIEvaluationStatus.OnApply</c> emits <c>callvirt Ability::Activate(object)</c> —
    /// the ROOT virtual declaration, not the derived one — and <c>TacticalAbility.Execute</c>:1158-1160 and
    /// <c>ExecuteAndWait</c> do the same. A patch sited on <c>AIEvaluationAbility.Activate</c> is reached
    /// from all of them for exactly one reason: it OVERRIDES that slot, so the runtime dispatches an
    /// <c>AIEvaluationAbility</c> receiver into the patched body. Stop overriding and every door silently
    /// bypasses the gate while the gate itself still binds. So both halves are asserted — the override
    /// relationship, and that the slot <c>OnApply</c> actually calls is the one the override claims.
    ///
    /// THE DECISION IS EXECUTED, NOT READ (arms (c)-(e)). <c>ClientAiEvaluationSeamGate.Prefix</c> is run
    /// three times against a real <c>NetworkEngine</c> shaped as a client, as the host, and as no session at
    /// all. A client must be REFUSED (<c>false</c> — the native body never runs), the host must be LET
    /// THROUGH (<c>true</c> — it is the one peer whose decisions are authoritative), and single-player must
    /// be untouched. Arm (f) is the positive control: a prefix that always agrees is run through the same
    /// three shapes and MUST be caught, so a green here is never green-by-not-looking.
    ///
    /// Falsify (each verified RED, then restored): drop <c>|| engine.IsHost</c> from the prefix →
    /// (d) host-refused; return <c>true</c> unconditionally (the whole fix reverted) → (c) client-evaluates;
    /// drop the <c>engine == null</c> term → (e) solo-intercepted; make <c>ExecuteAIEvaluation</c> internal
    /// and call it from a second method of the class → (a); drop <c>override</c> from
    /// <c>AIEvaluationAbility.Activate</c> → (b) execute-door-unpatched.
    /// </summary>
    internal static class L320_AClientNeverReachesAiEvaluationByAnyDoor
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var evalAbility = typeof(AIEvaluationAbility);
            var activate = evalAbility.GetMethod("Activate", All, null, new[] { typeof(object) }, null);
            var execute = evalAbility.GetMethod("ExecuteAIEvaluation", All);
            var onApply = typeof(AIEvaluationStatus).GetMethod("OnApply", All);
            var gate = typeof(Multiplayer.Tactical.ClientAiEvaluationSeamGate);
            var prefix = gate?.GetMethod("Prefix", All);
            var instanceField = typeof(NetworkEngine).GetField("<Instance>k__BackingField", All);

            if (activate == null || execute == null || onApply == null || prefix == null ||
                instanceField == null || prefix.ReturnType != typeof(bool))
            {
                yield return "L320 premise-changed: AIEvaluationAbility.Activate(object) / " +
                             "ExecuteAIEvaluation / AIEvaluationStatus.OnApply / " +
                             "ClientAiEvaluationSeamGate.Prefix -> bool / NetworkEngine's Instance backing " +
                             "field no longer resolves. The seam this law is sited on has been reshaped and " +
                             "every arm below would pass vacuously while a client runs enemy AI of its own.";
                yield break;
            }

            // ── (a) THE CLASS IS CLOSABLE: one private method, one referrer ───────────
            if (!execute.IsPrivate)
                yield return "L320 evaluation-body-not-private: AIEvaluationAbility.ExecuteAIEvaluation is no " +
                             "longer private, so a caller outside the class can now name it and the whole " +
                             "'gate Activate and the class is closed' argument is void. Whatever new door " +
                             "exists reaches TacticalFaction.EvaluateAiActionsAsync with nothing in the way.";
            var referrers = evalAbility.GetNestedTypes(All).SelectMany(t => t.GetMethods(All))
                .Concat(evalAbility.GetMethods(All))
                .Where(m => Program.Callees(m, game).Any(c => Same(c, execute)))
                .Select(m => m.DeclaringType.Name + "." + m.Name).Distinct(StringComparer.Ordinal).ToList();
            if (referrers.Count != 1 || !referrers[0].EndsWith(".Activate", StringComparison.Ordinal))
                yield return "L320 evaluation-has-a-second-door: AIEvaluationAbility.ExecuteAIEvaluation is " +
                             "referenced by [" + string.Join(", ", referrers) + "] instead of by Activate " +
                             "alone. Gating Activate no longer closes the class, which is the entire reason " +
                             "the gate was moved off the call paths and onto the seam — a door nobody " +
                             "enumerated is exactly the 2026-08-08 failure repeating.";

            // ── (b) THE OTHER DOOR STILL LANDS IN THE PATCHED BODY ───────────────────
            if (!activate.IsVirtual || activate.GetBaseDefinition().DeclaringType == evalAbility)
                yield return "L320 execute-door-unpatched: AIEvaluationAbility.Activate no longer overrides a " +
                             "virtual base declaration. TacticalAbility.Execute:1158-1160 and ExecuteAndWait " +
                             "call Activate on the BASE type, so a patch sited on this body would never be " +
                             "reached from them — the ExecuteAIEvaluationAbilities door would run ungated on " +
                             "a client while this law's own decision arms still passed.";
            var door = Program.Callees(onApply, game).OfType<MethodInfo>().FirstOrDefault(
                c => c.Name == "Activate" && c.DeclaringType != null &&
                     c.DeclaringType.IsAssignableFrom(evalAbility));
            if (door == null)
                yield return "L320 status-door-moved: AIEvaluationStatus.OnApply no longer calls Activate on " +
                             "the ability at all. That direct call is the door the per-call-path gate could " +
                             "not see and the reason the seam is where it is; if it has moved, a mirrored " +
                             "status now reaches evaluation by some route this law does not cover and the " +
                             "coverage claim has to be re-derived.";
            else if (door.GetBaseDefinition() != activate.GetBaseDefinition())
                yield return "L320 status-door-misses-the-gate: AIEvaluationStatus.OnApply calls " +
                             door.DeclaringType.Name + ".Activate, whose virtual slot is NOT the one " +
                             "AIEvaluationAbility.Activate overrides. The patched body is therefore never " +
                             "reached from that door and the mirrored status runs the evaluation on a client " +
                             "exactly as it did before 69d75d2.";

            // ── (c)-(e) THE DECISION, EXECUTED ON BOTH SIDES OF THE SEAM ─────────────
            foreach (var v in RunShapes(prefix, instanceField, "L320")) yield return v;

            // ── (f) POSITIVE CONTROL ─────────────────────────────────────────────────
            var control = typeof(FakeSeam).GetMethod("Prefix", All);
            if (!RunShapes(control, instanceField, "CONTROL").Any())
                yield return "L320 control-not-red: FakeSeam.Prefix agrees with every caller and the shape " +
                             "runner reported nothing, so arms (c)-(e) cannot tell a working gate from a " +
                             "missing one and their green means nothing.";
        }

        /// <summary>The three shapes, run against a REAL NetworkEngine put in place of the live one and taken
        /// straight back out. A finally on a plain method (not on the iterator above) so the global cannot
        /// survive an early break by a caller.</summary>
        private static List<string> RunShapes(MethodBase prefix, FieldInfo instanceField, string tag)
        {
            var found = new List<string>();
            var saved = instanceField.GetValue(null);
            try
            {
                if (Run(prefix, instanceField, Client()) != false)
                    found.Add(tag + " client-evaluates: with a live session in which this peer is NOT the " +
                              "host, the seam let AIEvaluationAbility.Activate run. The client then decides " +
                              "its own AI actions — a second brain on the same battle, disagreeing with the " +
                              "host about where a cloaked enemy is and refusing every shot aimed at it. An " +
                              "AI decision is the host's; its result arrives on 0x82 like every other action.");
                if (Run(prefix, instanceField, Host()) != true)
                    found.Add(tag + " host-refused: the seam suppressed AIEvaluationAbility.Activate on the " +
                              "HOST. Nobody then evaluates at all: the aliens stand still for the whole " +
                              "battle and no 0x82 mirror is ever produced for any peer to play.");
                if (Run(prefix, instanceField, null) != true)
                    found.Add(tag + " solo-intercepted: with no NetworkEngine at all the seam still refused " +
                              "the native body, so a single-player campaign — which loads this mod and never " +
                              "opens a session — has its enemy AI switched off.");
            }
            finally { instanceField.SetValue(null, saved); }
            return found;
        }

        private static bool? Run(MethodBase prefix, FieldInfo instanceField, object engine)
        {
            instanceField.SetValue(null, engine);
            return prefix.Invoke(null, new object[] { null }) as bool?;
        }

        private static object Host() => Engine(active: true, host: true, hostPeer: false);
        private static object Client() => Engine(active: true, host: false, hostPeer: true);

        /// <summary>A NetworkEngine shaped exactly as IsActiveSession reads it (NetworkEngine.cs:58):
        /// IsActive plus either being the host or holding a host peer id. Uninitialized — nothing on that
        /// path touches the transport, the sync engine or the dedup ring.</summary>
        private static object Engine(bool active, bool host, bool hostPeer)
        {
            var e = FormatterServices.GetUninitializedObject(typeof(NetworkEngine));
            Set(typeof(NetworkEngine), e, "IsActive", active);
            Set(typeof(NetworkEngine), e, "IsHost", host);
            if (hostPeer)
            {
                var s = FormatterServices.GetUninitializedObject(typeof(SessionManager));
                Set(typeof(SessionManager), s, "HostPeerId", (ulong?)1UL);
                Set(typeof(NetworkEngine), e, "Session", s);
            }
            return e;
        }

        private static void Set(Type owner, object target, string prop, object value) =>
            owner.GetField("<" + prop + ">k__BackingField", All).SetValue(target, value);

        /// <summary>POSITIVE CONTROL — the gate as it was before `69d75d2`: nothing sited on this seam, so
        /// every caller runs the native body whoever is asking.</summary>
        private static class FakeSeam
        {
            private static bool Prefix(AIEvaluationAbility __instance) => true;
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
