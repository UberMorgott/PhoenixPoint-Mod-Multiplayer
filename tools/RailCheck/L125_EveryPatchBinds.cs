using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RailCheck
{
    /// <summary>
    /// L125 — A PATCH WE DECLARE IS A PATCH THAT BINDS. Every other law in this harness reasons about what
    /// the rail DOES once it is running. This one asserts the thing all of them silently assume: that the
    /// Harmony patches carrying the rail into the game attach to the game at all.
    ///
    /// THE DAY IT WAS WORTH WRITING (2026-08-05). RailCheck was green, the build had 0 errors and
    /// 0 warnings, and the mod was DEAD in the player: no Multiplayer button on the main menu, both late
    /// binders unarmed, the whole tactical rail absent. One line in Player.log:
    /// <c>PatchAll failed: Patching exception in method virtual System.Boolean
    /// PhoenixPoint.Tactical.Levels.&lt;ReturnFire&gt;d__321::MoveNext()</c>. A new transpiler had picked up
    /// an iterator body it could not be emitted into, <c>Harmony.PatchAll</c> is one unguarded loop
    /// (<c>GetTypesFromAssembly(asm).Do(t =&gt; CreateClassProcessor(t).Patch())</c>), and the first throw
    /// abandoned every patch class after it. The harness could not see any of it because nothing in it ever
    /// resolved a patch target against the shipped <c>Assembly-CSharp</c>.
    ///
    /// ARM (a) — <c>unemittable-target</c>. THE ROOT CAUSE, GENERALISED. A target whose body is closed by a
    /// <c>fault</c> or <c>filter</c> handler cannot be patched — by us or by anyone — and the attempt throws
    /// at bind time. MEASURED against the shipped assembly, not reasoned from the API: driving real Harmony
    /// at the three fire-path coroutines answers <c>InvalidProgramException</c> ("the CLR detected an invalid
    /// program") for both bodies carrying a fault clause and for neither body without one. Note what the
    /// mechanism is NOT, because the obvious story is wrong: MonoMod does not simply fail to emit the clause.
    /// <c>DynamicMethodDefinition.Generate</c> INSPECTS the body, sees a fault/filter handler, and
    /// deliberately routes it away from <c>DynamicMethod</c> (whose ILGenerator has no
    /// <c>BeginFaultBlock</c>/<c>BeginExceptFilterBlock</c>) to the MethodBuilder backend — and it is that
    /// backend whose output for these bodies is an invalid method. The discriminator is the same either way,
    /// which is why this arm tests the clause and not the exception.
    ///
    /// C# never writes those handlers by hand; the compiler does, and the shape that matters here is an
    /// ITERATOR whose source carries a <c>try/finally</c>: its <c>MoveNext</c> comes out wrapped in a
    /// <c>fault</c> that calls <c>Dispose</c>. That is <c>&lt;ReturnFire&gt;d__321</c> and
    /// <c>&lt;ShootAndWaitRF&gt;d__323</c>; <c>&lt;FireWeaponAtTargetCrt&gt;d__322</c> carries the same wait
    /// with no handler and binds cleanly. A patch class that derives its own targets structurally (the right
    /// way to write one) will therefore walk into this on its own, and the day the game adds a
    /// <c>using</c> to a method we already patch, this arm is what says so instead of the menu going missing.
    ///
    /// ARM (b) — <c>unresolved-target</c>. This repo's other standing bind trap:
    /// <c>AccessTools.Method(type, name, Type[])</c> matches parameter types EXACTLY, so naming a base type
    /// where the game declares a derived one answers null, the patch never attaches, and nothing anywhere
    /// says a word. Scoped to classes with NO <c>Prepare</c>: a gated class is ALLOWED to resolve to
    /// nothing (that is what the gate is for — see <c>TftvLateBinder</c>), an ungated one is not.
    ///
    /// ARM (c) — <c>transpiler-missed</c> / <c>transpiler-threw</c>. A transpiler that binds is still only
    /// half the promise: it also has to FIND its pattern in the IL it is handed. When it does not, it hands
    /// the stream back unchanged and the mod runs with the feature quietly absent — this repo's dominant
    /// bug shape. So each declared transpiler is run here against the REAL instruction stream of each of
    /// its real targets, and a run that changes nothing, or throws, is a violation.
    ///
    /// WHY IT IS STRUCTURAL AND NOT A LIVE <c>Patch()</c> CALL. Applying the patches would be the strongest
    /// possible arm and is the wrong trade: it detours real game methods inside a harness that goes on to
    /// CALL them, and it JITs a copy of each body — which in a console host fails on any Unity ECall for
    /// reasons that have nothing to do with the patch (see L113's note on <c>GetCachedPtr</c>). Every input
    /// that decides whether a patch binds is metadata, so the arms read metadata: Harmony's own
    /// <c>PatchClassProcessor</c> resolves the targets (its private <c>GetBulkMethods</c> is invoked so
    /// <c>TargetMethod</c>/<c>TargetMethods</c> run for real, coroutine state machines and all), and the
    /// answers are checked against the shipped assembly.
    ///
    /// FALSIFIABILITY (observed 2026-08-05, before the fix). With <c>AShotWaitsOnlyForItsOwnProjectiles</c>
    /// still yielding every body that reads the map-global projectile flag, arm (a) reports
    /// <c>AShotWaitsOnlyForItsOwnProjectiles -> …&lt;ReturnFire&gt;d__321::MoveNext</c> and RailCheck goes
    /// RED. Adding the emittability filter to that class's <c>TargetMethods</c> turns it green.
    /// </summary>
    internal static class L125_EveryPatchBinds
    {
        internal static IEnumerable<string> Check()
        {
            var mod = typeof(Multiplayer.Network.Sync.DiffEngine).Assembly;
            var harmony = new HarmonyLib.Harmony("railcheck.L125");
            var pcpType = typeof(PatchClassProcessor);
            var getBulk = AccessTools.Method(pcpType, "GetBulkMethods");
            var containerField = AccessTools.Field(pcpType, "containerAttributes");
            var patchMethodsField = AccessTools.Field(pcpType, "patchMethods");
            var getOriginal = AccessTools.Method(typeof(HarmonyLib.Harmony).Assembly.GetType("HarmonyLib.PatchTools"),
                                                 "GetOriginalMethod");

            foreach (var type in AccessTools.GetTypesFromAssembly(mod))
            {
                var pcp = new PatchClassProcessor(harmony, type);
                if (containerField.GetValue(pcp) == null) continue;   // not a patch class at all

                // Prepare() runs FIRST, exactly as PatchClassProcessor.Patch does — a gated class is
                // allowed to say "not this run" (TFTV absent, native entry off), and several of them
                // resolve their target INSIDE Prepare, so skipping it would read every one of them as
                // unresolvable. False reds are how a law gets suppressed instead of read.
                var prepare = AccessTools.GetDeclaredMethods(type)
                                         .FirstOrDefault(m => m.Name == "Prepare" && m.GetParameters().Length == 0);
                var gated = prepare != null;

                List<MethodBase> targets = null;
                string blewUp = null;
                try
                {
                    if (!gated || Equals(prepare.Invoke(null, null), true))
                        targets = (List<MethodBase>)getBulk.Invoke(pcp, null);
                }
                catch (Exception e) { blewUp = Innermost(e).Message; }
                if (blewUp != null)
                {
                    yield return "L125 unresolved-target: " + type.Name + " cannot even name its targets — " + blewUp;
                    continue;
                }
                if (targets == null) continue;   // Prepare said "not this run"

                // No TargetMethod/TargetMethods: the targets are the per-patch-method [HarmonyPatch]
                // attributes, already merged with the class-level ones by the processor's constructor.
                if (targets.Count == 0)
                {
                    targets = new List<MethodBase>();
                    foreach (var ap in (IEnumerable<object>)patchMethodsField.GetValue(pcp))
                    {
                        var info = (HarmonyMethod)AccessTools.Field(ap.GetType(), "info").GetValue(ap);
                        var resolved = (MethodBase)getOriginal.Invoke(null, new object[] { info });
                        if (resolved != null) targets.Add(resolved);
                        else if (!gated)
                            yield return "L125 unresolved-target: " + type.Name + " declares " + info +
                                         " and it resolves to nothing — the patch will never attach";
                    }
                }

                var transpiler = AccessTools.GetDeclaredMethods(type)
                                            .FirstOrDefault(m => m.Name == "Transpiler" && IsTranspiler(m));
                foreach (var target in targets)
                {
                    if (!Emittable(target))
                        yield return "L125 unemittable-target: " + type.Name + " -> " + Describe(target) +
                                     " — its body is closed by a fault/filter handler; Harmony cannot rebuild " +
                                     "it and patching it throws InvalidProgramException at bind time";
                    else if (transpiler != null)
                        foreach (var v in ProbeTranspiler(type, transpiler, target)) yield return v;
                }
            }
        }

        /// <summary>The one metadata fact that decides whether Harmony can rebuild a body at all.</summary>
        internal static bool Emittable(MethodBase m)
        {
            var body = m == null ? null : m.GetMethodBody();
            if (body == null) return false;
            foreach (ExceptionHandlingClause c in body.ExceptionHandlingClauses)
                if (c.Flags == ExceptionHandlingClauseOptions.Fault ||
                    c.Flags == ExceptionHandlingClauseOptions.Filter) return false;
            return true;
        }

        /// <summary>Hand the transpiler the target's REAL instruction stream and see whether it finds
        /// anything. Unchanged output is not a neutral result: it is the feature missing in silence.</summary>
        private static IEnumerable<string> ProbeTranspiler(Type type, MethodInfo transpiler, MethodBase target)
        {
            // The shape of the input is taken BEFORE the run: a transpiler is handed the very instruction
            // objects it edits, so "what it was given" has to be recorded while it still is that.
            string beforeShape = null, afterShape = null, blewUp = null;
            try
            {
                var before = PatchProcessor.GetOriginalInstructions(target);
                beforeShape = Shape(before);
                afterShape = Shape(((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { before })).ToList());
            }
            catch (Exception e)
            {
                blewUp = Innermost(e).GetType().Name + ": " + Innermost(e).Message;
            }
            if (blewUp != null)
            {
                yield return "L125 transpiler-threw: " + type.Name + " on " + Describe(target) + " — " + blewUp;
                yield break;
            }
            if (beforeShape == afterShape)
                yield return "L125 transpiler-missed: " + type.Name + " leaves " + Describe(target) +
                             " byte-for-byte unchanged — it is bound to a method its pattern no longer matches";
        }

        private static bool IsTranspiler(MethodInfo m)
        {
            var ps = m.GetParameters();
            return ps.Length == 1 && ps[0].ParameterType == typeof(IEnumerable<CodeInstruction>) &&
                   typeof(IEnumerable<CodeInstruction>).IsAssignableFrom(m.ReturnType);
        }

        private static string Shape(List<CodeInstruction> il) =>
            string.Join(";", il.Select(i => i.opcode.Name + "|" + (i.operand == null ? "" : i.operand.ToString())).ToArray());

        private static string Describe(MethodBase m) =>
            m.DeclaringType.FullName.Replace('+', '/') + "::" + m.Name;

        private static Exception Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}
