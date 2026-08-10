using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;
using System.Linq;
using System.Reflection;

namespace RailCheck
{
    /// <summary>L393 — every window family the geoscape router actually dispatches through, and every
    /// <c>ModalType</c> the game ships, must carry a VERDICT. Nothing here says WHICH verdict.
    ///
    /// It used to. The previous shape carried a hand-written <c>durableFormerGaps</c> set of fifteen
    /// ModalType values and demanded an exact disposition match against it, plus a hardcoded list of the
    /// five expected router families. That is a law asserting SHAPE: making a sixteenth outcome modal
    /// durable, or making one of the fifteen local, was a CORRECT change that this law failed until the
    /// law itself was edited first — so the law had to be edited to describe whatever the code now did,
    /// which is not a law at all. This repo has been burned by exactly that before; HANDOFF.md:52 records
    /// L127(d) and L182(d) MANDATING the ready-button defect.
    ///
    /// So the family set is now DERIVED from <c>DurableWindowRegistry.RoutedPresentations</c> — the very
    /// array <c>HandleRoutedPresentation</c> iterates — and the only modal expectation left is an
    /// implication between two independently maintained sources: a window this mod MIRRORS must be
    /// durable. Both sides of that move together when the code legitimately changes.</summary>
    internal static class L393_EveryRoutedWindowFamilyHasAVerdict
    {
        internal static IEnumerable<string> Check()
        {
            var registrations = DurableWindowRegistry.RoutedPresentations;

            // The registry must be the router the geoscape traffic really goes through, or every arm below
            // is describing a table nothing reads.
            var routed = typeof(SyncEngine).Assembly.GetTypes()
                .Where(t => t == typeof(SyncEngine) || t.DeclaringType == typeof(SyncEngine))
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                              BindingFlags.Public | BindingFlags.NonPublic))
                .Any(m => Program.Callees(m, typeof(SyncEngine).Assembly)
                                 .Any(c => c.Name == "HandleRoutedPresentation"));
            if (!routed)
                yield return "L393 registry-is-not-the-actual-geoscape-router-source";

            if (registrations.Count == 0)
                yield return "L393 router-has-no-registrations: the family set is derived from " +
                             "RoutedPresentations, so an empty array would make every arm below vacuous";

            // MUST come before the loop. RouterFamilies is RoutedPresentations.ToDictionary(x => x.Family),
            // so a duplicate family throws ArgumentException the first time the loop below touches it — the
            // harness then reports a CRASH and aborts the whole run, and this arm never gets to speak.
            if (registrations.Select(r => r.Family).Distinct(StringComparer.Ordinal).Count() != registrations.Count)
            {
                yield return "L393 duplicate-router-registration: " + string.Join(", ",
                    registrations.GroupBy(r => r.Family, StringComparer.Ordinal).Where(g => g.Count() > 1)
                        .Select(g => g.Key + " x" + g.Count())) +
                    " — two verdicts for one family means the second is unreachable and which one applies " +
                    "depends on dictionary order";
                yield break; // everything below reads RouterFamilies and would crash instead of reporting
            }

            // DERIVED, not listed: whatever the router dispatches through must have a verdict, and the name
            // it is registered under must be the type that actually handles it. A registration whose Family
            // string drifted from its handler would classify one family while routing another.
            foreach (var route in registrations)
            {
                if (route.Handle == null)
                { yield return "L393 router-verdict-has-no-runtime-handler-" + route.Family; continue; }

                string owner = route.Handle.Method.DeclaringType?.Name;
                if (!string.Equals(owner, route.Family, StringComparison.Ordinal))
                    yield return "L393 routed-family-handler-mismatch-" + route.Family + ": registered under '" +
                                 route.Family + "' but dispatched into '" + (owner ?? "<null>") + "' — the " +
                                 "verdict is recorded against a family name the router does not use";

                if (!DurableWindowRegistry.RouterFamilies.ContainsKey(route.Family))
                    yield return "L393 routed-family-unclassified-" + route.Family;

                bool answered = true;
                try { var ignored = DurableWindowRegistry.RouterDisposition(route.Family); }
                catch (InvalidOperationException) { answered = false; }
                if (!answered)
                    yield return "L393 routed-family-has-no-verdict-" + route.Family +
                                 ": the router dispatches this family but the registry refuses to classify it";
            }

            // premise-changed / POSITIVE CONTROL: a family nobody registered must be REFUSED, never
            // defaulted to ordinary or local-only. If this ever stops throwing, every "unclassified" arm
            // above goes quiet by construction and the law protects nothing.
            bool loud = false;
            try { DurableWindowRegistry.RouterDisposition("FutureWindowRouter"); }
            catch (InvalidOperationException) { loud = true; }
            if (!loud) yield return "L393 unknown-router-fell-through-silently";

            var modalValues = Enum.GetValues(typeof(PhoenixPoint.Common.Utils.ModalType))
                                  .Cast<PhoenixPoint.Common.Utils.ModalType>().ToArray();
            if (modalValues.Any(m => GeoWindowCoverage.RuleForModal(m) == null) ||
                GeoWindowCoverage.DeclaredModals.Count != modalValues.Length)
                yield return "L393 modal-taxonomy-is-not-exhaustive";

            foreach (var modal in modalValues)
            {
                var rule = GeoWindowCoverage.RuleForModal(modal);
                bool classified = true; DurableWindowDisposition disposition = default(DurableWindowDisposition);
                try { disposition = DurableWindowRegistry.ModalDisposition(modal); }
                catch (InvalidOperationException) { classified = false; }
                if (!classified)
                { yield return "L393 modal-verdict-drift-" + modal; continue; }

                // The ONE expectation left. Being honest about its strength: ModalDisposition READS
                // RuleForModal, so this restates that mapping rather than checking two independent
                // sources against each other, and only a defect in the mapping line itself turns it red.
                // It is kept because that line is where "we mirror this window to the other peer" turns
                // into "so the other peer can find it again", and a silent flip there is invisible
                // otherwise. What it deliberately does NOT do is say which verdict a NON-mirrored modal
                // gets — that was the fifteen-entry frozen list this law used to carry.
                if (rule != null && rule.Sync == WindowSync.Mirrored &&
                    disposition != DurableWindowDisposition.Durable)
                    yield return "L393 mirrored-modal-not-durable-" + modal + ": the mod mirrors this window " +
                                 "to the other peer but classifies it " + disposition + ", so the copy it " +
                                 "raises there is never entered in the inbox and cannot be reopened";
            }
        }
    }
}
