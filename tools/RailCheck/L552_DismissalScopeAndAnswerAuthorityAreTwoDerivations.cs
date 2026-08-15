using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities;

namespace RailCheck
{
    /// <summary>
    /// L552 — DISMISSAL SCOPE AND ANSWER AUTHORITY ARE TWO DERIVATIONS, AND NO SCOPE KEY IS DEAD.
    ///
    /// THE REPORT (2026-08-15, second defect of the day): a client's Accept on the
    /// <c>ModalType.FactionSoldierJoin</c> window did NOTHING — no soldier joined, no log, no refusal.
    /// The cause was ONE seam answering TWO different questions. <c>WindowQueueSync.SendAdvance</c>
    /// gated the outgoing 0xB9 advance on <c>MayRelayAnswer(family)</c>, i.e. on
    /// <c>WindowJournal.ScopeOf(family)</c> — the DISMISSAL-SCOPE table. Those are not the same
    /// question:
    ///   • DISMISSAL SCOPE asks "does MY closing of this window close YOURS?" — per the owner's
    ///     2026-08-15 decision the answer is LOCAL for practically every window, because practically
    ///     every window is informational and per-peer.
    ///   • ANSWER AUTHORITY asks "does answering this window MUTATE SHARED CAMPAIGN STATE, so that the
    ///     HOST has to run the answer?" — <c>FactionSoldierJoin</c>'s Confirm is
    ///     <c>reward.Apply(faction, site, aircraft)</c> (HavenMissionUtil.cs:59), which grants a unit to
    ///     the shared roster. That answer MUST reach the host even though the window itself is
    ///     dismissed locally and nobody else's copy closes.
    /// Fusing them made the second question inherit the first's answer, which is LOCAL for every modal,
    /// so <c>SendAdvance</c> returned before sending and <c>HandleAdvance</c>/<c>AnswerQueued</c> were
    /// unreachable from their only sender.
    ///
    /// THE DEAD KEY THAT HID IT. The scope table declared <c>"UIStateGeoMissionBrief"</c> GLOBAL, a
    /// string that matches NO type in Assembly-CSharp and no type in this repo. <c>FamilyOf</c> is
    /// <c>stateType.Name</c> and a mission brief is a <c>UIStateGeoModal</c>, so the key could never be
    /// hit and the table's Global arm looked broader than it was. A declaration that cannot fire is a
    /// silent constant-false; arm (a) makes that class of bug impossible to reintroduce.
    ///
    /// ARMS:
    ///   (a) dead-scope-key — EXECUTED over the real table: every family the scope table declares must be
    ///       the Name of a type that actually exists, so a scope declaration always has a window it can
    ///       apply to. This is the arm that would have caught the original defect on the day it shipped.
    ///   (b) authority-fused-with-scope — EXECUTED over the real predicate: <c>MayRelayAnswer</c> must
    ///       take the authority question as its OWN input and honour it independently of the family, so
    ///       a LOCAL family with an authoritative answer still relays and the same LOCAL family without
    ///       one still does not.
    ///   (c) authority-is-derived-and-total — the arm that keeps this law from going stale. It asserts the
    ///       DERIVATION, never a verdict for a named window: the signature must take the window's DATA
    ///       (a signature that cannot express a per-ModalType list), the derivation must be TOTAL and
    ///       non-constant over the whole payload space (every <c>DataShape</c>, with and without a ref),
    ///       an UNKNOWN payload — a type this build has never seen, i.e. a DLC or third-party window —
    ///       must get a verdict rather than an exception AND that verdict must be the safe one, and it
    ///       must reach the repo's one definition of "names a rail entity" rather than a private copy.
    ///       A law that instead checked "FactionSoldierJoin is authoritative" would be green forever and
    ///       say nothing about the next window somebody adds.
    ///   (d) send-advance-asks-one-question — IL: <c>SendAdvance</c> must reach BOTH derivations. A
    ///       perfect pair of predicates the emitter never asks is exactly the shape both 2026-08-15
    ///       defects had.
    ///
    /// WHY A DERIVATION AND NOT A LIST. This mod is a universal engine; a hand-written set of ModalTypes
    /// is stale the day the game, a DLC or a third-party mod adds a window, and its staleness is SILENT —
    /// the unlisted window is simply never authoritative and its answer is lost, which is the exact defect
    /// above wearing a different hat. The property used instead is one the repo already computes for every
    /// window that rides the rail at all: DOES THIS WINDOW NAME A REPLICATED ENTITY
    /// (<c>GeoModalMirror.NamesEntity</c> over <c>Describe</c>'s payload). A window about an object the
    /// whole campaign owns is a decision for the authority that owns it; a window over a view DTO, a def
    /// id, or nothing decides nothing anyone else holds. Verified against the shipped game, not asserted:
    /// FactionSoldierJoin's data is a <c>GeoCharacter</c> rail root and its Confirm is <c>reward.Apply</c>
    /// (HavenMissionUtil.cs:59); GeoResearchComplete's handler only navigates the local view
    /// (GeoscapeView.cs:2108); DiplomacyResearchBrief ships a NULL DialogCallback; GeoPhoenixBaseOutcome's
    /// case in ModalResultCallback:798 is empty.
    ///
    /// AND THE UNCLASSIFIABLE WINDOW IS LOUD, not silently local: a modal the coverage table declares
    /// MIRRORED whose data has no 0xB7 shape has no identity and its answer can reach nobody, so
    /// <c>SendAdvance</c> now LogErrors it once per ModalType instead of returning in silence. The silent
    /// fallback is what hid both of today's defects.
    ///
    /// ROLES SEPARATED (§C.3): (a)–(c) are role-free pure calls; (d) is a statement about the shipped
    /// assembly. Nothing here waits on a peer — no quorum.
    ///
    /// Falsify (compile-valid src mutations, each named): re-add
    /// <c>{ "UIStateGeoMissionBrief", DismissScope.Global }</c> to <c>WindowJournal.FamilyScope</c> → (a);
    /// <c>MayRelayAnswer(string family, bool authoritative) =&gt; ScopeOf(family) == Global;</c> — the
    /// literal pre-fix behaviour — → (b); <c>AnswerMutatesSharedState(object d) =&gt; d is GeoCharacter;</c>
    /// (a type check that skips NamesEntity) → (c); drop the <c>AnswerIsHostAuthoritative</c> call from
    /// <c>SendAdvance</c> → (d).
    /// </summary>
    internal static class L552_DismissalScopeAndAnswerAuthorityAreTwoDerivations
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var asm = typeof(WindowJournal).Assembly;
            var sync = asm.GetTypes().FirstOrDefault(t => t.Name == "WindowQueueSync");
            var mirror = asm.GetTypes().FirstOrDefault(t => t.Name == "GeoModalMirror");
            var mayRelay = sync == null ? null : sync.GetMethod("MayRelayAnswer", Any);
            var sendAdvance = sync == null ? null : sync.GetMethod("SendAdvance", Any);
            var hostAuthoritative = sync == null ? null : sync.GetMethod("AnswerIsHostAuthoritative", Any);
            var authority = mirror == null ? null : mirror.GetMethod("AnswerMutatesSharedState", Any);
            var scopeTable = typeof(WindowJournal).GetField("FamilyScope", BindingFlags.Static |
                                                            BindingFlags.NonPublic | BindingFlags.Public);

            // (a) NO DEAD SCOPE KEY. Read the real table, not a copy of its expected contents.
            if (!(scopeTable?.GetValue(null) is IDictionary declared))
            {
                yield return "L552 premise-changed: WindowJournal.FamilyScope did not resolve as a dictionary, " +
                             "so the declaration table this law reads has moved and arm (a) proved nothing.";
            }
            else
            {
                var live = StateTypeNames();
                foreach (var key in declared.Keys.Cast<object>().Select(k => k as string)
                                            .OrderBy(k => k, StringComparer.Ordinal))
                    if (key == null || !live.Contains(key))
                        yield return "L552 dead-scope-key: the dismissal-scope table declares family '" +
                                     (key ?? "<null>") + "', which is the Name of NO type that exists. " +
                                     "WindowJournal.FamilyOf is stateType.Name, so no window can ever carry " +
                                     "this family and the declaration is a silent constant-false — the exact " +
                                     "shape of 'UIStateGeoMissionBrief', which made every modal LOCAL and " +
                                     "killed the FactionSoldierJoin grant. Declare a family that resolves, " +
                                     "or express the intent with a mechanism that does.";
            }

            // (b) TWO QUESTIONS, TWO INPUTS. A relay predicate that reads only the family cannot express
            // "dismissed locally, but the answer is the host's to run" — which is most of this mod.
            var relayParams = mayRelay?.GetParameters();
            if (mayRelay == null || relayParams.Length != 2 ||
                relayParams[0].ParameterType != typeof(string) || relayParams[1].ParameterType != typeof(bool))
            {
                yield return "L552 authority-fused-with-scope: WindowQueueSync.MayRelayAnswer(string, bool) " +
                             "did not resolve. The relay must take the ANSWER-AUTHORITY question as its own " +
                             "input; deriving it from the family's DISMISSAL SCOPE is the fusion that made a " +
                             "client's Accept on FactionSoldierJoin do nothing at all.";
            }
            else
            {
                // UIStateGeoModal is the family of EVERY modal and is LOCAL, which is the whole point.
                if (WindowJournal.ScopeOf("UIStateGeoModal") != DismissScope.Local)
                    yield return "L552 premise-changed: the modal family 'UIStateGeoModal' is no longer LOCAL, " +
                                 "so arm (b) can no longer show the two questions are answered separately.";
                else
                {
                    if (!(bool)mayRelay.Invoke(null, new object[] { "UIStateGeoModal", true }))
                        yield return "L552 authority-fused-with-scope: the relay refuses an AUTHORITATIVE " +
                                     "answer of the LOCAL family 'UIStateGeoModal'. Dismissing my copy is my " +
                                     "own business, but reward.Apply is the host's to run — refusing here is " +
                                     "the measured defect: the client clicks Accept and no soldier joins.";
                    if ((bool)mayRelay.Invoke(null, new object[] { "UIStateGeoModal", false }))
                        yield return "L552 scope-fused-with-authority: the relay sends a NON-authoritative " +
                                     "dismissal of the LOCAL family 'UIStateGeoModal'. The host answers a " +
                                     "relayed advance with modal.FinishDialog, so one peer closing its own " +
                                     "informational window would close everybody else's (L547).";
                }
                if (WindowJournal.ScopeOf("UIStateRosterDeployment") == DismissScope.Global &&
                    !(bool)mayRelay.Invoke(null, new object[] { "UIStateRosterDeployment", false }))
                    yield return "L552 global-family-blocked: the relay refuses the GLOBAL deployment family " +
                                 "even with no authority claim. A GLOBAL family's dismissal must still travel " +
                                 "on the family alone — that is the half L547 owns.";
            }

            // (c) THE AUTHORITY VERDICT IS DERIVED, NOT LISTED — and the signature is the proof. A method
            // that takes the window's DATA cannot express a per-ModalType enumeration, so this arm forbids
            // the hand-written list structurally instead of policing its contents. A list is what goes
            // stale the moment the game, a DLC or a third-party mod adds a window.
            var authParams = authority?.GetParameters();
            if (authority == null || authParams.Length != 1 ||
                authParams[0].ParameterType != typeof(object) || authority.ReturnType != typeof(bool))
            {
                yield return "L552 authority-is-not-derived: GeoModalMirror.AnswerMutatesSharedState(object) " +
                             "did not resolve. Answer authority must be DERIVED from the window's own data — " +
                             "a signature that takes the data cannot hold a per-ModalType list, which is the " +
                             "point. If this became AnswerMutatesSharedState(ModalType), a window nobody " +
                             "remembered to enumerate is silently unauthoritative and its answer is lost.";
            }
            else
            {
                // TOTALITY, EXECUTED over the payload space itself: every DataShape the codec ships must get
                // a verdict with no throw, and the verdict set must contain BOTH answers — a derivation that
                // is constant in either direction classifies nothing.
                bool sawTrue = false, sawFalse = false;
                string threw = null;
                foreach (var shape in Enum.GetValues(typeof(GeoModalMirror.DataShape)).Cast<object>())
                    foreach (var named in new[] { "R#1", "", null })
                    {
                        bool verdict;
                        try { verdict = NamesEntityOf(shape, named); }
                        catch (Exception ex) { threw = threw ?? (shape + "/" + (named ?? "<null>") + ": " + ex.GetType().Name); continue; }
                        if (verdict) sawTrue = true; else sawFalse = true;
                    }
                if (threw != null)
                    yield return "L552 derivation-is-not-total: the authority derivation threw on payload " +
                                 "shape '" + threw + "'. Every reachable window must get a verdict — a shape " +
                                 "that throws is a window whose answer silently goes nowhere.";

                // THE UNKNOWN WINDOW, EXECUTED. A payload type this build has never seen — the shape a DLC
                // or a third-party mod arrives as — must get a verdict, and that verdict must be the SAFE
                // one (not authoritative: nothing crosses, the acting peer's own copy still closes).
                foreach (var unknown in new object[] { null, new object() })
                {
                    bool verdict = false;
                    string blew = null;
                    try { verdict = (bool)authority.Invoke(null, new[] { unknown }); }
                    catch (Exception ex) { blew = (ex.InnerException ?? ex).GetType().Name; }
                    if (blew != null)
                    {
                        yield return "L552 derivation-is-not-total: the authority derivation threw on a " +
                                     (unknown == null ? "NULL" : "type it has never seen") + " payload (" +
                                     blew + "). An unclassifiable window must get a SAFE, announced " +
                                     "verdict, never an exception on a click.";
                        continue;
                    }
                    if (verdict)
                        yield return "L552 unknown-window-is-authoritative: a " +
                                     (unknown == null ? "NULL" : "never-seen") + " payload was classed as " +
                                     "mutating shared state, so an unrecognised window would relay its answer " +
                                     "to the host on nothing but ignorance. The safe default is the other way.";
                }

                if (!sawTrue)
                    yield return "L552 derivation-is-constant: no payload shape is authoritative, so the " +
                                 "derivation is a constant `false` and NO client answer can ever reach the " +
                                 "host — the measured defect, in which a client's Accept on a window that " +
                                 "grants a soldier did nothing at all.";
                if (!sawFalse)
                    yield return "L552 derivation-is-constant: every payload shape is authoritative, so the " +
                                 "derivation is a constant `true` and every informational dismissal is " +
                                 "relayed — the host runs FinishDialog and loses its own copy (L547).";

                // AND IT REALLY IS THE REPO'S ONE DEFINITION OF 'NAMES A RAIL ENTITY', not a copy of it that
                // agrees today: a second implementation is the drift §A.5 forbids on the scope side.
                var namesEntity = mirror?.GetMethod("NamesEntity", Any);
                if (namesEntity == null)
                    yield return "L552 premise-changed: GeoModalMirror.NamesEntity did not resolve, so the " +
                                 "property the authority derivation is built on has moved.";
                else if (!Il.References(authority, namesEntity))
                    yield return "L552 authority-is-not-derived: AnswerMutatesSharedState does not reach " +
                                 "NamesEntity, so it is deciding 'does this window name a replicated entity' " +
                                 "on its own. That question has exactly one definition in this repo and the " +
                                 "park queue and the host's resolve-back check both already ask it.";
            }

            // (d) THE EMITTER ASKS BOTH. Two perfect predicates nobody calls is how both defects shipped.
            if (sendAdvance == null)
                yield return "L552 premise-changed: WindowQueueSync.SendAdvance did not resolve, so the one " +
                             "emitter of the 0xB9 advance has moved and arm (d) proved nothing.";
            else
            {
                if (mayRelay != null && !Il.References(sendAdvance, mayRelay))
                    yield return "L552 send-advance-asks-one-question: SendAdvance does not reach " +
                                 "MayRelayAnswer, so the client emits (or withholds) the 0xB9 advance without " +
                                 "asking either question.";
                if (hostAuthoritative == null || !Il.References(sendAdvance, hostAuthoritative))
                    yield return "L552 send-advance-asks-one-question: SendAdvance does not reach " +
                                 "WindowQueueSync.AnswerIsHostAuthoritative, so the authority half of the " +
                                 "decision is derived and then ignored — the relay is once again gated on " +
                                 "dismissal scope alone, which is constant-false for every modal.";
                else if (authority != null && !Il.References(hostAuthoritative, authority))
                    yield return "L552 authority-is-not-derived: AnswerIsHostAuthoritative does not reach " +
                                 "GeoModalMirror.AnswerMutatesSharedState, so it is deciding authority by " +
                                 "some other means than the window's own data.";
            }
        }

        /// <summary>The core of the derivation, executed over the payload space rather than over a list of
        /// windows: <c>NamesEntity</c> is what decides whether a window is ABOUT a replicated entity.</summary>
        private static bool NamesEntityOf(object shape, string named) =>
            GeoModalMirror.NamesEntity(new GeoModalMirror.Raise
            {
                Shape = (GeoModalMirror.DataShape)shape,
                Ref = named,
            });

        /// <summary>Every type Name the game and the mod actually ship. A family IS a type Name
        /// (WindowJournal.FamilyOf), so this is the exact set a scope key may be drawn from.</summary>
        private static HashSet<string> StateTypeNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in new[] { typeof(GeoCharacter).Assembly, typeof(WindowJournal).Assembly })
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                foreach (var t in types) if (t != null) names.Add(t.Name);
            }
            return names;
        }
    }
}
