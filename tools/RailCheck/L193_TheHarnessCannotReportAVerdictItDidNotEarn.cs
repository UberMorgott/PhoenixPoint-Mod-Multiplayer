using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RailCheck
{
    /// <summary>
    /// L193 — THE HARNESS CANNOT REPORT A VERDICT IT DID NOT EARN.
    ///
    /// The only law in this repo whose subject is the RUNNER rather than the mod, and it is here rather
    /// than as a bare comment in `Program.cs` for one reason: every other law is worth exactly as much as
    /// this one is true. A harness that says GREEN gets believed; a law that is red gets argued with. Both
    /// ways of earning an unearned GREEN were observed live on 2026-08-07/08.
    ///
    /// (1) GREEN BY OMISSION. `laws.AddRange(SomeLaw.Check())` let a throwing law abort the whole run —
    /// `RailMeta.CountMiss` reading `UnityEngine.Time.realtimeSinceStartup` (an ECall that throws outside
    /// the player) died at law #31 of 153, so 122 laws reported nothing; and `L98_ApAuthority` reflectively
    /// `Invoke`s a production method by a hardcoded 7-argument array, so ANOTHER AGENT CHANGING THAT
    /// METHOD'S SIGNATURE took the entire safety net down as a `TargetParameterCountException`. Laws that
    /// never ran are indistinguishable from laws that passed. `Program.Add` now contains each law's
    /// failure, and `AbortOnHarnessCrash` refuses any verdict — including a baselined one — while a crash
    /// line exists. Arm (c) is the one that actually holds the line over time: it forbids the raw
    /// `laws.AddRange` shape ever coming back, because ONE uncontained registration among 166 restores the
    /// outage and nothing else would notice.
    ///
    /// (2) A VERDICT ABOUT A DLL NOBODY BUILT. RailCheck reflects over the shipped `Multiplayer.dll`, and
    /// `dotnet run --no-build` (or `RailCheck.exe` directly) will happily reflect over one that predates
    /// the source. That produced thirteen violations that were pure artefact in one run, and — the
    /// dangerous direction — a FALSE GREEN in which an agent DELETED the very call its new law asserted,
    /// watched the law pass, and nearly rewrote the law to match. `StaleBuild` is a hard stop, not a
    /// warning, because the failure mode is a human reading GREEN and believing it.
    ///
    /// NOT COVERED HERE, deliberately: `.githooks/pre-commit` was checked and has no equivalent hole — it
    /// runs plain `dotnet run --project tools/RailCheck`, whose ProjectReference on `Multiplayer.csproj`
    /// rebuilds the mod and fails the hook when the mod does not compile.
    ///
    /// Falsify: strip the try/catch out of `Add` → <c>L193 one-bad-law-kills-the-run</c>; drop the
    /// `AbortOnHarnessCrash` call from `Run` → <c>L193 a-crash-can-be-baselined</c>; drop the `StaleBuild`
    /// call → <c>L193 verdict-without-a-build</c>; write one raw `laws.AddRange(...)` back into `Run` →
    /// <c>L193 uncontained-law-registration</c>; rename or delete any of the three guards →
    /// <c>L193 premise-changed</c>.
    /// </summary>
    internal static class L193_TheHarnessCannotReportAVerdictItDidNotEarn
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var p = typeof(Program);
            var add = p.GetMethod("Add", All, null,
                          new[] { typeof(List<string>), typeof(System.Func<IEnumerable<string>>) }, null);
            var abort = p.GetMethod("AbortOnHarnessCrash", All, null, System.Type.EmptyTypes, null);
            var stale = p.GetMethod("StaleBuild", All, null, System.Type.EmptyTypes, null);
            var execution = p.GetMethod("ValidateLawExecution", All, null, System.Type.EmptyTypes, null);
            var noViolations = p.GetMethod("ValidateNoLawViolations", All);
            var run = p.GetMethod("Run", All, null, new[] { typeof(string[]) }, null);
            if (add == null || abort == null || stale == null || execution == null || noViolations == null || run == null)
            {
                yield return "L193 premise-changed: Program.Add / AbortOnHarnessCrash / StaleBuild / Run no " +
                             "longer resolves. Those three guards are the only thing standing between this " +
                             "harness and a GREEN it did not earn — and an unearned GREEN silently voids " +
                             "every other law in this directory, which is why the runner gets a law at all.";
                yield break;
            }

            // ── (a) one bad law costs one red line, not the run ──────────────
            // A CATCH clause specifically, not "any handler": `foreach` over an IEnumerable always emits a
            // try/FINALLY for the enumerator's Dispose, so counting handlers passes with the catch deleted.
            // Caught by falsifying this arm — which is the whole reason arms get falsified.
            var body = add.GetMethodBody();
            if (body == null || !body.ExceptionHandlingClauses.Any(
                    c => c.Flags == ExceptionHandlingClauseOptions.Clause))
                yield return "L193 one-bad-law-kills-the-run: Program.Add no longer contains a law's " +
                             "exception. A single throw then aborts every law registered after it and they " +
                             "report NOTHING — which reads exactly like passing. That is how CountMiss's " +
                             "Unity ECall silently disabled 122 laws, and how one agent's signature change " +
                             "to a method L98 Invokes by a hardcoded argument array took down the net for " +
                             "six agents at once.";

            // ── (b) a crash is never a verdict, and never baselineable ───────
            var runCalls = Program.Callees(run, p.Assembly).ToList();
            if (!runCalls.Any(c => c.MetadataToken == abort.MetadataToken))
                yield return "L193 a-crash-can-be-baselined: Run no longer aborts on a crashed law. The " +
                             "HARNESS-CRASH line is an ordinary violation string, so without this stop " +
                             "`--update` writes it into docs/rail-baseline.txt and every later run calls it " +
                             "green — the crash becomes a permanent, invisible hole in coverage.";

            if (!runCalls.Any(c => c.MetadataToken == execution.MetadataToken))
                yield return "L193 registration-omission-can-be-green: Run no longer validates the executed " +
                             "registration count. Source text can still contain every Add while if(false) " +
                             "prevents all of them from running, producing an unearned 0/0 GREEN.";

            if (!runCalls.Any(c => c.MetadataToken == noViolations.MetadataToken))
                yield return "L193 law-failure-can-be-baselined: Run no longer refuses executable law " +
                             "violations before snapshot gates; --update could bless a broken rule as GREEN.";

            if (!Program.LawExecutionIsValid(344, 344,
                    "5aced6182eb672892ce193092b0d958c01ce658fd51ff18ea67e3aa143459bb4") ||
                Program.LawExecutionIsValid(344, 343,
                    "5aced6182eb672892ce193092b0d958c01ce658fd51ff18ea67e3aa143459bb4") ||
                Program.LawExecutionIsValid(344, 344, "wrong") ||
                !Program.NoLawViolations(0) || Program.NoLawViolations(1))
                yield return "L193 positive-control: execution identity or zero-violation decisions no " +
                             "longer reject their falsifying rows.";

            // ── (c) no verdict about a DLL nobody just built ─────────────────
            if (!runCalls.Any(c => c.MetadataToken == stale.MetadataToken))
                yield return "L193 verdict-without-a-build: Run no longer refuses a stale Multiplayer.dll. " +
                             "`dotnet run --no-build` and RailCheck.exe then pronounce on code nobody " +
                             "compiled — observed producing both thirteen artefact violations AND a false " +
                             "GREEN with the asserted call deleted, which nearly got a correct law rewritten " +
                             "to match a lie.";

            // ── (d) the containment is not optional for ANY law ──────────────
            // The arm that survives contact with six agents landing in Run: one raw AddRange among 166
            // registrations restores the whole outage and no other law can see it.
            // CalleeSequence, not Callees: List<T>.AddRange lives in mscorlib and Callees filters to one
            // assembly, so the assembly-scoped question can never see it. Also caught by falsification.
            if (Program.CalleeSequence(run).Any(c => c.Name == "AddRange" &&
                                                    c.DeclaringType != null &&
                                                    c.DeclaringType.Name == "List`1"))
                yield return "L193 uncontained-law-registration: Run calls List<string>.AddRange directly " +
                             "again. Every law must be registered through Program.Add — one bare " +
                             "laws.AddRange(X.Check()) is one law that can still take the entire run down " +
                             "with it, and it is invisible in a diff of 166 near-identical lines.";
        }
    }
}
