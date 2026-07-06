using System.Collections.Generic;
using System.Text;

namespace Multiplayer.Network
{
    /// <summary>
    /// One CRITICAL reflection binding the sync layer depends on, reduced to the only two facts the
    /// verdict logic needs: the human-readable <c>Type.Member</c> label and whether it resolved.
    ///
    /// The real firing (Phase 5 🟡, out of scope here) resolves these via <c>AccessTools</c> at mod
    /// start; that reflection is Unity/Harmony-coupled and cannot live in Core. This struct is the
    /// pure boundary: the game-side code turns each <c>AccessTools</c> lookup into a
    /// <c>CriticalBinding(label, result != null)</c> and hands the ordered list to
    /// <see cref="ReflectionGuardCore.Evaluate"/>, so the ACTUAL verdict/message decision is
    /// game-free and unit-testable here.
    /// </summary>
    public readonly struct CriticalBinding
    {
        /// <summary>The <c>Type.Member</c> label, e.g. <c>"GeoLevelController.OnTurnChanged"</c>.</summary>
        public readonly string Name;

        /// <summary>True iff the reflection lookup for <see cref="Name"/> returned non-null.</summary>
        public readonly bool Resolved;

        public CriticalBinding(string name, bool resolved)
        {
            Name = name;
            Resolved = resolved;
        }
    }

    /// <summary>
    /// The verdict from aggregating an ordered list of <see cref="CriticalBinding"/>s: whether the
    /// installed Phoenix Point version is compatible with this mod build and, if not, WHICH member is
    /// missing plus the exact user-facing message to show.
    /// </summary>
    public readonly struct GuardVerdict
    {
        /// <summary>True iff every checked binding resolved (or the input was empty).</summary>
        public readonly bool Compatible;

        /// <summary>The FIRST unresolved binding's <c>Type.Member</c> label, or null when compatible.</summary>
        public readonly string MissingMember;

        /// <summary>The exact user-facing message when incompatible, or null when compatible.</summary>
        public readonly string Message;

        public GuardVerdict(bool compatible, string missingMember, string message)
        {
            Compatible = compatible;
            MissingMember = missingMember;
            Message = message;
        }
    }

    /// <summary>
    /// Pure, Unity-free aggregation for the Phase 5 reflection version-guard. A game update that
    /// silently changes a signature today causes a mid-game DESYNC instead of a clear error; the
    /// guard resolves a curated set of CRITICAL bindings at startup and, on the FIRST failure, refuses
    /// to network with an actionable message rather than proceeding into corruption.
    ///
    /// This class owns ONLY the decision — no reflection, no Harmony, no UnityEngine — so the
    /// verdict/message rules can be unit-tested here without a game install. The real
    /// <c>AccessTools</c> resolution and the "disable networking + show message" firing are the 🟡
    /// game-facing half and live in the mod project.
    /// </summary>
    public static class ReflectionGuardCore
    {
        /// <summary>
        /// Builds the incompatibility message for a missing member. Single source of truth so the
        /// firing code and the tests agree byte-for-byte on the wording.
        /// </summary>
        public static string BuildMessage(string missingMember)
        {
            return $"Multiplayer mod: incompatible Phoenix Point version (missing {missingMember}). Update the mod.";
        }

        /// <summary>
        /// Aggregates the ordered <paramref name="bindings"/> into a <see cref="GuardVerdict"/>.
        ///
        /// Rules:
        /// <list type="bullet">
        /// <item>Compatible iff EVERY binding resolved. Then MissingMember and Message are null.</item>
        /// <item>On the FIRST unresolved binding (input order is preserved, so the earliest listed
        /// failure wins and reporting is deterministic), Compatible is false, MissingMember is that
        /// binding's Name, and Message is <see cref="BuildMessage"/> for it.</item>
        /// <item>EMPTY (or null) input is treated as Compatible — with nothing to check there is no
        /// evidence of incompatibility, so the guard must not block startup. The curated critical
        /// list is expected to be non-empty in practice; this is the defined degenerate case.</item>
        /// </list>
        /// </summary>
        public static GuardVerdict Evaluate(IEnumerable<CriticalBinding> bindings)
        {
            if (bindings == null)
                return new GuardVerdict(true, null, null);

            foreach (var binding in bindings)
            {
                if (!binding.Resolved)
                    return new GuardVerdict(false, binding.Name, BuildMessage(binding.Name));
            }

            return new GuardVerdict(true, null, null);
        }

        /// <summary>
        /// Collects the labels of ALL unresolved bindings, in input order. <see cref="Evaluate"/>
        /// stops at the FIRST failure (a deterministic verdict + one actionable message); this
        /// companion enumerates EVERY failure so the startup self-check can list every broken binding
        /// in a single log line — a game update can drop several members at once. null/empty input →
        /// empty list.
        /// </summary>
        public static IReadOnlyList<string> UnresolvedMembers(IEnumerable<CriticalBinding> bindings)
        {
            var missing = new List<string>();
            if (bindings != null)
            {
                foreach (var binding in bindings)
                {
                    if (!binding.Resolved)
                        missing.Add(binding.Name);
                }
            }
            return missing;
        }

        /// <summary>
        /// Builds the ONE prominent, multi-line startup report that names every unresolved critical
        /// binding, or null when nothing is unresolved (compatible → no report to show). Single source
        /// of truth for the report wording so the firing code and the tests agree byte-for-byte.
        /// </summary>
        public static string BuildStartupReport(IReadOnlyList<string> unresolvedMembers)
        {
            if (unresolvedMembers == null || unresolvedMembers.Count == 0)
                return null;

            int n = unresolvedMembers.Count;
            string rule = new string('=', 60);
            var sb = new StringBuilder();
            sb.Append(rule).Append('\n');
            sb.Append("Multiplayer mod: INCOMPATIBLE Phoenix Point version.").Append('\n');
            sb.Append(n)
              .Append(n == 1 ? " critical reflection binding" : " critical reflection bindings")
              .Append(" failed to resolve - co-op sync WILL break. Update the mod.").Append('\n');
            sb.Append("Missing:").Append('\n');
            foreach (var member in unresolvedMembers)
                sb.Append("  - ").Append(member).Append('\n');
            sb.Append(rule);
            return sb.ToString();
        }

        /// <summary>
        /// Validates the curated critical-binding label list itself — a dev-facing integrity check on
        /// the list, NOT a game-version check: the list must be non-null and non-empty, and every label
        /// must be non-blank and unique (a blank or duplicated label would make the missing-binding
        /// report ambiguous). Returns the problems found (empty ⇒ the list is well-formed). Pure so the
        /// rules are unit-tested here; the startup self-check runs it and logs any problem without
        /// throwing.
        /// </summary>
        public static IReadOnlyList<string> ValidateLabels(IEnumerable<string> labels)
        {
            var problems = new List<string>();
            if (labels == null)
            {
                problems.Add("binding label list is null");
                return problems;
            }

            var seen = new HashSet<string>();
            int count = 0;
            foreach (var label in labels)
            {
                if (string.IsNullOrWhiteSpace(label))
                    problems.Add($"binding label at index {count} is null or blank");
                else if (!seen.Add(label))
                    problems.Add($"duplicate binding label '{label}'");
                count++;
            }
            if (count == 0)
                problems.Add("binding label list is empty");

            return problems;
        }
    }
}
