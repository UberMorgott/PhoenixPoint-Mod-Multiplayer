using System.Collections.Generic;

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
    }
}
