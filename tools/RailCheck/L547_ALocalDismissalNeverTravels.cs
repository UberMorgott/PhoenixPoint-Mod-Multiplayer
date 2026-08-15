using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L547 — A LOCAL FAMILY'S DISMISSAL NEVER TRAVELS: THE ANSWER RELAY ASKS THE SAME TABLE THE VOID DOES.
    ///
    /// THE REPORT (2026-08-15): dismissing the research-completed window on a CLIENT closed it on the HOST.
    /// The mod had TWO independent dismissal-propagation systems and only one of them was governed by
    /// §A.5's declaration table. Governed: the host-minted void (GeoModalMirror.HostMintVoid →
    /// WindowJournal.ScopeOf, L526). UNGOVERNED: the ANSWER RELAY —
    /// WindowQueueSync.SendAdvance → IntentRail.Send(SurfaceIds.GeoWindowIntent, OpAdvance) →
    /// host HandleAdvance → modal.FinishDialog((ModalResult)result) — which never asked ScopeOf at all. It
    /// was merely UNREACHABLE for that family until ModalType.GeoResearchComplete became Mirrored (e496ea5),
    /// because IdentityOf names only a Mirrored rule. Live evidence: client
    /// `CLIENT window advance GeoResearchComplete|…|PX_Alien_Fishman_ResearchDef result=0 nonce=2`,
    /// host `HOST advanced 'GeoResearchComplete|…' with Confirm for peer=2`.
    ///
    /// THE INVARIANT: a dismissal may leave the acting peer ONLY for a family the table declares GLOBAL —
    /// the same question, asked of the same table, for both propagation systems. §A.5: default is LOCAL,
    /// only the mission family is GLOBAL, and no `if (family == …)` may live outside the table.
    ///
    /// ARMS:
    ///   (a) local-answer-travels — EXECUTED over the real predicate: every LOCAL family (the modal family
    ///       UIStateGeoModal that carries the reported defect, an undeclared name, null) must be refused.
    ///   (b) global-answer-blocked — EXECUTED, the other direction, so the predicate cannot be a constant
    ///       `false`: the two GLOBAL families must still relay exactly as they do today.
    ///   (c) relay-does-not-consult-the-table — IL: WindowQueueSync.SendAdvance must actually reach
    ///       MayRelayAnswer. Without this arm the predicate could be perfect and simply unused, which is
    ///       precisely the shape of the defect (a governed decision beside an ungoverned code path).
    ///   (d) two-tables — IL: MayRelayAnswer must reach WindowJournal.ScopeOf. A second hardcoded list of
    ///       family names that happens to agree today is the special case §A.5 forbids.
    ///
    /// ROLES SEPARATED (§C.3): (a)/(b) are role-free pure calls — the CLIENT role is the one that runs
    /// SendAdvance, and the arm asserts what it may emit; (c)/(d) are statements about the shipped assembly.
    ///
    /// RE-EXPRESSED 2026-08-15, NOT WEAKENED (L552). MayRelayAnswer now takes a SECOND input — whether this
    /// window's answer mutates shared campaign state — because the family alone could not express "dismissed
    /// locally, but the answer is the host's to run" and the fused form was constant-false for every modal.
    /// The invariant this law owns is unchanged and its arms still execute the real predicate; they simply
    /// pin the new input to `false`, which is the exact case this law is about: a dismissal that carries no
    /// authority may never travel for a LOCAL family. The authority half is L552's, and arm (b) dropped
    /// "UIStateGeoMissionBrief" because that string names no type and was never a family any window carried.
    ///
    /// Falsify (compile-valid src mutations, each named):
    /// `MayRelayAnswer(string family, bool a) => true;` — the literal pre-fix behaviour — → (a);
    /// `=> a;` → (b); delete the `if (!MayRelayAnswer(family, authoritative))` gate from SendAdvance → (c);
    /// `=> a || family == "UIStateRosterDeployment";` → (d).
    /// </summary>
    internal static class L547_ALocalDismissalNeverTravels
    {
        internal static IEnumerable<string> Check()
        {
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var asm = typeof(WindowJournal).Assembly;
            var sync = asm.GetTypes().FirstOrDefault(t => t.Name == "WindowQueueSync");
            var mayRelay = sync == null ? null : sync.GetMethod("MayRelayAnswer", Any);
            var sendAdvance = sync == null ? null : sync.GetMethod("SendAdvance", Any);
            var scopeOf = typeof(WindowJournal).GetMethod("ScopeOf", BindingFlags.Static |
                                                          BindingFlags.NonPublic | BindingFlags.Public);
            if (mayRelay == null || sendAdvance == null || scopeOf == null)
            {
                yield return "L547 premise-changed: WindowQueueSync.MayRelayAnswer / SendAdvance / " +
                             "WindowJournal.ScopeOf did not all resolve, so the answer relay this law " +
                             "governs has moved and nothing below proved anything.";
                yield break;
            }

            // (a) A LOCAL DISMISSAL STAYS HOME. UIStateGeoModal is the family of EVERY modal — the mint seam
            // appends the UI state's type name — so it is the family the reported defect travelled under.
            foreach (var family in new[] { "UIStateGeoModal", "UIStateGeoscapeEvent",
                                           "UIStateSomeFamilyThatDoesNotExistYet", null })
            {
                if (WindowJournal.ScopeOf(family) != DismissScope.Local) continue;   // (b) owns the others
                if ((bool)mayRelay.Invoke(null, new object[] { family, false }))
                    yield return "L547 local-answer-travels: the answer relay would send this peer's " +
                                 "dismissal of family '" + (family ?? "<null>") + "', which the declaration " +
                                 "table declares LOCAL. The host answers a relayed advance with " +
                                 "modal.FinishDialog, so one peer closing its own window closes everybody " +
                                 "else's — the measured 2026-08-15 defect on the research-completed window. " +
                                 "A LOCAL family dismisses on the acting peer and nowhere else.";
            }

            // (b) NOT A CONSTANT `false`. The relay was written for the mission/deployment flow and must
            // keep working exactly as it does today, or this law would have deleted a feature, not a bug.
            foreach (var family in new[] { "UIStateRosterDeployment" })
            {
                if (WindowJournal.ScopeOf(family) != DismissScope.Global)
                {
                    yield return "L547 premise-changed: family '" + family + "' is no longer GLOBAL, so " +
                                 "this arm can no longer show the relay still crosses for anything.";
                    continue;
                }
                if (!(bool)mayRelay.Invoke(null, new object[] { family, false }))
                    yield return "L547 global-answer-blocked: the answer relay refuses family '" + family +
                                 "', which the table declares GLOBAL. Once ANYONE has acted on a mission the " +
                                 "decision is taken for everyone; blocking it here would leave the other " +
                                 "peers holding a window about a mission that has already launched.";
            }

            // (c) THE PREDICATE IS ACTUALLY ASKED. A governed decision beside an ungoverned code path is the
            // exact shape of the defect, so the law refuses to be green on the predicate alone.
            if (!Il.References(sendAdvance, mayRelay))
                yield return "L547 relay-does-not-consult-the-table: WindowQueueSync.SendAdvance does not " +
                             "reach MayRelayAnswer, so the client emits the 0xB9 advance without ever asking " +
                             "what scope the family declares. That is the second, ungoverned " +
                             "dismissal-propagation system this law exists to close.";

            // (d) ONE TABLE, NOT TWO. A hardcoded family list here would agree today and drift tomorrow.
            if (!Il.References(mayRelay, scopeOf))
                yield return "L547 two-tables: MayRelayAnswer does not reach WindowJournal.ScopeOf, so the " +
                             "relay is deciding a dismissal scope on its own. §A.5: the declaration table is " +
                             "the ONLY place a family's scope may be written.";
        }
    }
}
