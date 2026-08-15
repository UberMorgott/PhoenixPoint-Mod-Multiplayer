using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L526 — DISMISSAL SCOPE IS A DECLARED PROPERTY OF A FAMILY, NEVER A SPECIAL CASE.
    ///
    /// Default is LOCAL and an UNDECLARED family IS local, so a new window family needs NO new code
    /// (§A.5). Only the mission family is GLOBAL, and a GLOBAL dismissal is effected by an explicit
    /// host-minted void record — never by each peer independently deciding, because without an explicit
    /// void two peers time out differently and diverge (FIX gap-fill, §2.5).
    ///
    /// ARMS:
    ///   (a) undeclared-is-not-local — EXECUTED: a family nobody has ever heard of is LOCAL.
    ///   (b) mission-is-not-global — EXECUTED, the other direction, so ScopeOf cannot be a constant.
    ///   (c) scope-decided-elsewhere — the declaration table is the ONLY place a family's scope is
    ///       written: no method outside WindowJournal both names a mission window state AND calls ScopeOf.
    ///       (Naming one for another property is fine — WindowOrder.HeldTransitionStates names it for the
    ///       open-screen hold; deciding a DISMISSAL SCOPE from a name is what is forbidden.)
    ///   (d) client-removes-on-its-own-verdict — DeploymentWindowClose.DropUnservableQueued does not reach
    ///       WindowJournal.ApplyVoid. The predicate reads GAME state and is asymmetric per peer; a client
    ///       evaluating it and removing its own journal entry re-creates the divergence the void exists
    ///       to prevent.
    ///   (e) positive-control — GeoModalMirror.HostMintVoid exists, so the GLOBAL scope has an effect.
    ///
    /// ROLES SEPARATED (§C.3): (a)/(b) are role-free pure calls; (c)/(d)/(e) are statements about the
    /// shipped assembly. The HOST-only arm is that HostMintVoid exists and is the only minter.
    ///
    /// Falsify (compile-valid src mutations, each named): make ScopeOf return Global by default → (a);
    /// remove the mission rows from FamilyScope → (b); add an `if (family == "UIStateRosterDeployment")`
    /// outside WindowJournal → (c); call WindowJournal.ApplyVoid from DropUnservableQueued → (d).
    /// </summary>
    internal static class L526_AnUndeclaredFamilyIsLocal
    {
        internal static IEnumerable<string> Check()
        {
            var journal = typeof(WindowJournal);
            if (journal.GetMethod("ScopeOf", BindingFlags.Static | BindingFlags.NonPublic |
                                             BindingFlags.Public) == null)
            {
                yield return "L526 premise-changed: WindowJournal.ScopeOf did not resolve, so the " +
                             "declaration table this law protects has moved.";
                yield break;
            }

            if (WindowJournal.ScopeOf("UIStateSomeFamilyThatDoesNotExistYet") != DismissScope.Local)
                yield return "L526 undeclared-is-not-local: an undeclared family did not come back LOCAL. " +
                             "A new window family — including one raised by a mod we do not control — must " +
                             "need NO new code, and LOCAL is the recoverable direction: a window that " +
                             "stays for the other peers costs a click, a window that vanishes for them is " +
                             "a decision they were never asked about.";
            if (WindowJournal.ScopeOf(null) != DismissScope.Local)
                yield return "L526 undeclared-is-not-local: a null family did not come back LOCAL.";

            if (WindowJournal.ScopeOf("UIStateRosterDeployment") != DismissScope.Global)
                yield return "L526 mission-is-not-global: the mission family is not GLOBAL. Once ANYONE " +
                             "has acted on a mission the decision to deploy is taken, so it is meaningless " +
                             "for the others to accept or refuse. Without this arm ScopeOf could simply " +
                             "return Local always.";

            var asm = typeof(WindowJournal).Assembly;
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // (c) THE TABLE IS THE ONLY PLACE A SCOPE IS DECIDED. Naming a mission state is legitimate
            // elsewhere (WindowOrder's held-transition table names it for a DIFFERENT property); deciding
            // its DISMISSAL SCOPE from that name is not. So the arm is the conjunction: a method that
            // both names a mission state AND calls ScopeOf is asking the table and then second-guessing
            // it — the `if (family == …)` special case §A.5 forbids.
            var scopeOf = journal.GetMethod("ScopeOf", BindingFlags.Static | BindingFlags.NonPublic |
                                                       BindingFlags.Public);
            var missionNames = new[] { "UIStateRosterDeployment", "UIStateGeoMissionBrief" };
            var elsewhere = asm.GetTypes().Where(t => t != journal)
                .SelectMany(t => t.GetMethods(Any).Cast<MethodBase>().Concat(t.GetConstructors(Any)))
                .Where(m => Il.MentionsAnyString(m, missionNames) && Il.References(m, scopeOf))
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (elsewhere.Count > 0)
                yield return "L526 scope-decided-elsewhere: " + string.Join(", ", elsewhere) + " name(s) a " +
                             "mission window state outside WindowJournal's declaration table. The table is " +
                             "the ONLY place a family's scope may be written; an `if (family == …)` " +
                             "anywhere else is the special case §A.5 forbids.";

            // (d) NO CLIENT REMOVES ON ITS OWN VERDICT.
            var deployment = asm.GetTypes().FirstOrDefault(t => t.Name == "DeploymentWindowClose");
            var drop = deployment == null ? null : deployment.GetMethod("DropUnservableQueued", Any);
            var applyVoid = journal.GetMethod("ApplyVoid", BindingFlags.Static | BindingFlags.NonPublic |
                                                           BindingFlags.Public);
            if (drop == null || applyVoid == null)
                yield return "L526 premise-changed: DropUnservableQueued or WindowJournal.ApplyVoid did not " +
                             "resolve, so the sweep this arm watches has moved.";
            else if (Il.References(drop, applyVoid))
                yield return "L526 client-removes-on-its-own-verdict: DropUnservableQueued reaches " +
                             "WindowJournal.ApplyVoid. Its Servable() predicate reads GAME state and is " +
                             "PER-PEER and ASYMMETRIC — the host evaluates it and mints the void, and a " +
                             "client applies what it is sent. It may remain native-queue hygiene only.";

            // (e) POSITIVE CONTROL, HOST ROLE: the void minter exists. Every arm above forbids things; if
            // the minter were gone, a GLOBAL family would simply never be dismissed anywhere.
            var mirror = asm.GetTypes().FirstOrDefault(t => t.Name == "GeoModalMirror");
            if (mirror == null || mirror.GetMethod("HostMintVoid", Any) == null)
                yield return "L526 positive-control: GeoModalMirror.HostMintVoid does not exist, so " +
                             "nothing can ever mint a void and the GLOBAL scope declared above has no " +
                             "effect at all.";
        }
    }
}
