using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L544 — NO RAIL PATH SEGMENT IS PRODUCED FROM A LOOP INDEX, AND THE KEYLESS ARM HAS NO FALLBACK.
    ///
    /// Prefix subscriptions (§B.3) are only safe because element addressing is by STABLE ID: an index path
    /// churns on insert and silently mis-scopes every declaration under it (RFC 6902, §2.5). The rule is
    /// already stated in the file — DiffEngine.cs:1230 says "law 2 forbids element indices in the path" —
    /// and already ENFORCED: IdentityResolver.KeyOf derives a key from the ID-probe table
    /// { SiteId, VehicleID, ResearchID, FacilityId, Id, Def } (IdentityResolver.cs:38) via FormatKeyValue
    /// (:96-106), which returns a BaseDef's Guid, a non-empty string, or a NON-NEGATIVE int — and NULL
    /// otherwise. A null key sets keyless = true (DiffEngine.cs:1243) and the whole field is then ABORTED
    /// with a loud Incident (:1298-1301). Duplicate keys abort it too (:1278-1280). There is no index
    /// fallback anywhere: the failure mode is a VISIBLE REFUSAL, never a positional path.
    ///
    /// This law pins that, because scoping now DEPENDS on it. Retiring R4 was a decision about today's
    /// code; without a law it is a decision about today only.
    ///
    /// ARMS, all EXECUTED against the real IdentityResolver with no game:
    ///   (a) unkeyable-yields-a-key — an object with none of the six probe members yields NULL, not an
    ///       index and not a synthesised name.
    ///   (b) positive-control — an object WITH a probe member yields a key. Without this, arm (a) passes
    ///       against a KeyOf that always returns null, which would abort every keyed collection in the game.
    ///   (c) negative-int-accepted — a negative int is refused (FormatKeyValue's own rule): a negative id
    ///       is the game's "not assigned yet", and accepting it would make two unassigned elements share
    ///       a path.
    ///   (d) index-fallback-present — the keyless arm in DiffEngine still reaches the Incident reporter.
    ///
    /// ROLES SEPARATED (§C.3): path construction is identical on both roles by design — that symmetry is
    /// what makes a client resolve the host's paths over its own graph — so there is no role-dependent
    /// behaviour for one role to hide.
    ///
    /// Falsify (compile-valid src mutations, each named): make FormatKeyValue synthesise a key for an
    /// unkeyable element → (a); make KeyOf always return null → (b); make FormatKeyValue's int branch
    /// accept negatives → (c); replace the keyless Incident with a silent `break` → (d).
    /// </summary>
    internal static class L544_NoPathSegmentIsAnIndex
    {
        private sealed class Unkeyable { public int NotAnId; public string AlsoNot; }
        private sealed class Keyed { public int SiteId = 76; }
        private sealed class NegativelyKeyed { public int SiteId = -1; }

        internal static IEnumerable<string> Check()
        {
            // (a)-(c) EXECUTE the real key derivation every prefix declaration depends on.
            var unkeyable = IdentityResolver.KeyOf(new Unkeyable());
            if (unkeyable != null)
                yield return "L544 unkeyable-yields-a-key: an object with none of the six ID probe " +
                             "members produced the key '" + unkeyable + "'. It must produce NULL, so the " +
                             "field is ABORTED with a loud Incident rather than addressed positionally. " +
                             "An index path churns on insert and silently mis-scopes every declared " +
                             "prefix beneath it — the failure mode must be a visible refusal.";

            var keyed = IdentityResolver.KeyOf(new Keyed());
            if (string.IsNullOrEmpty(keyed))
                yield return "L544 positive-control: an object WITH a SiteId produced no key, so arm (a) " +
                             "passed against a derivation that returns null for everything — which would " +
                             "abort every keyed collection in the game and make the whole rail silent.";

            var negative = IdentityResolver.KeyOf(new NegativelyKeyed());
            if (negative != null)
                yield return "L544 negative-int-accepted: a negative id produced the key '" + negative +
                             "'. A negative id is the game's 'not assigned yet'; accepting it would make " +
                             "two unassigned elements share one path, which is an index collision under " +
                             "another name.";

            // (d) THE KEYLESS ARM STILL REACHES THE INCIDENT REPORTER.
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var diff = typeof(DiffEngine);
            var incident = diff.GetMethods(Any).FirstOrDefault(m => m.Name == "Incident");
            var visit = diff.GetMethods(Any).FirstOrDefault(m => m.Name == "VisitEntity");
            if (incident == null || visit == null)
                yield return "L544 premise-changed: DiffEngine.VisitEntity or DiffEngine.Incident did not " +
                             "resolve, so arm (d) cannot see the abort it exists to pin.";
            else if (!Il.References(visit, incident))
                yield return "L544 index-fallback-present: DiffEngine.VisitEntity no longer reaches " +
                             "Incident. The keyless arm's ONLY correct outcome is a loud abort — " +
                             "'unkeyable/duplicate element keys — blob rebuild would husk the elements'. " +
                             "A silent skip or an index fallback would produce positional paths that no " +
                             "declared prefix can safely match.";
        }
    }
}
