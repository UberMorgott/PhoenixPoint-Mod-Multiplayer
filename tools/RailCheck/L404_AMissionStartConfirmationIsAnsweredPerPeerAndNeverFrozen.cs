using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L404 — THE MISSION-START CONFIRMATION IS THE ONE EXTRA PER-PEER FAMILY, AND NOTHING FREEZES IT.
    ///
    /// OWNER DECISION 2026-08-10, verbatim: "для каждого своё окно ТОЛЬКО У ОКОН У КОТОРЫХ ИДЁТ
    /// ПОДТВЕРЖДЕНИЕ НАЧАЛА МИССИИ, без блокировки выбора. остальные окна по правилам как уже есть."
    /// Every peer gets its OWN live copy of the window that confirms entering a mission, no copy is locked
    /// read-only by somebody else's answer, and EVERY OTHER FAMILY keeps shared first-wins
    /// (docs/superpowers/specs/2026-08-09-durable-window-inbox-design.md:89, HANDOFF.md:44, L44/L45).
    ///
    /// THE FAMILY IS MATCHED ON THE NATIVE RAISER, never on a name:
    /// <c>GeoscapeView.ShowMissionBriefing</c>:1903 pushes a <c>UIStateGeoModal</c> whose <c>ModalType</c> IS
    /// <c>GeoscapeView.GetMissionBriefModal(mission)</c> (:1724) over a live <c>GeoMission</c>, and its
    /// Confirm arm is <c>ModalResultCallback</c>:825 → <c>LaunchMission</c>:1043. The <c>*Outcome</c> sibling
    /// (<c>GetMissionOutcomeModal</c>:1800) shares the per-peer ANSWER class but starts nothing, which is why
    /// <see cref="GeoWindowCoverage.IsMissionStartConfirmationClass"/> exists apart from
    /// <see cref="GeoWindowCoverage.IsPerPeerAnswerClass"/>.
    ///
    /// Arms (each falsified for real, one defect at a time, restored and re-verified between):
    ///   (a) <c>start-family-not-per-peer</c> — a brief over a live mission IS a start confirmation and IS in
    ///       the per-peer answer class. Break the conjunction and the whole carve-out is gone.
    ///   (b) <c>start-family-overreaches</c> — an outcome is NOT a start confirmation, and a mission modal
    ///       that is neither brief nor outcome is in NEITHER class. This is the "не расширять" half: the
    ///       carve-out may not swallow a second family by accident.
    ///   (c) <c>other-families-went-per-peer</c> — for the whole truth table, a window with no live
    ///       <c>GeoMission</c> is never per-peer and never a start confirmation, so every other family still
    ///       answers shared first-wins.
    ///   (d) <c>start-answer-was-blocked</c> — "без блокировки выбора": a Confirm on this class RUNS for a
    ///       peer whose offer never resolved, and a non-Confirm never runs the native
    ///       <c>GeoMission.Cancel</c> arm. A peer is never locked out of answering its own copy.
    ///   (e) <c>freeze-can-reach-the-start-family</c> — <c>EventPopup.IsFrozen</c> is the shared first-wins
    ///       lock and its subject is a <c>GeoscapeEvent</c>. A mission-start confirmation carries a
    ///       <c>GeoMission</c>, so the freeze path cannot name it; if that parameter ever widens, the
    ///       read-only lock the owner ruled out becomes reachable.
    ///   (f) <c>wiring-dropped</c> — <c>PerPeerModalAnswer.Prefix</c> actually calls both
    ///       <c>IsMissionStartConfirmation</c> and <c>MissionStartAlreadyCommitted</c>. A pure predicate
    ///       nobody calls is a law that checks nothing (the L394 trap).
    ///
    /// Falsify: make <c>IsMissionStartConfirmationClass</c> ignore <c>isBrief</c> → (b); make it ignore
    /// <c>hasMission</c> → (c); return <c>false</c> from <see cref="PerPeerModalAnswer.Runs"/> for a Confirm
    /// → (d); delete the <c>IsMissionStartConfirmation</c> call from the prefix → (f).
    /// </summary>
    internal static class L404_AMissionStartConfirmationIsAnsweredPerPeerAndNeverFrozen
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var startClass = typeof(GeoWindowCoverage).GetMethod("IsMissionStartConfirmationClass", AllMembers);
            var perPeerClass = typeof(GeoWindowCoverage).GetMethod("IsPerPeerAnswerClass", AllMembers);
            var live = typeof(GeoWindowCoverage).GetMethod("IsMissionStartConfirmation", AllMembers);
            var runs = typeof(PerPeerModalAnswer).GetMethod("Runs", AllMembers);
            var prefix = typeof(PerPeerModalAnswer).GetMethod("Prefix", AllMembers);
            var frozen = typeof(EventPopup).GetMethod("IsFrozen", AllMembers);
            if (startClass == null || perPeerClass == null || live == null || runs == null ||
                prefix == null || frozen == null)
            {
                yield return "L404 premise-changed: one of GeoWindowCoverage.{IsMissionStartConfirmationClass," +
                             "IsPerPeerAnswerClass,IsMissionStartConfirmation}, PerPeerModalAnswer.{Runs,Prefix} " +
                             "or EventPopup.IsFrozen no longer resolves — the per-peer carve-out has moved and " +
                             "every arm below is asleep";
                yield break;
            }
            // The raiser this family is DEFINED by. If either native method goes, the live predicate answers
            // "not a start confirmation" for everything and the class is silently empty.
            if (typeof(GeoscapeView).GetMethod("GetMissionBriefModal",
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(GeoMission) }, null) == null ||
                typeof(GeoscapeView).GetMethod("GetMissionOutcomeModal", AllMembers) == null)
            {
                yield return "L404 premise-changed: GeoscapeView.GetMissionBriefModal / GetMissionOutcomeModal " +
                             "no longer resolve, so the family cannot be matched on its native raiser at all";
                yield break;
            }

            // ── (a) the start family IS per-peer ────────────────────────────────────────────────────
            if (!GeoWindowCoverage.IsMissionStartConfirmationClass(true, true) ||
                !GeoWindowCoverage.IsPerPeerAnswerClass(true, true, false))
                yield return "L404 start-family-not-per-peer: a mission BRIEF over a live GeoMission is no " +
                             "longer a mission-start confirmation, or no longer in the per-peer answer class. " +
                             "That is the whole carve-out the owner decided on 2026-08-10 — without it one " +
                             "peer's answer decides the window for everybody again.";

            // ── (b) it does not swallow a second family ─────────────────────────────────────────────
            if (GeoWindowCoverage.IsMissionStartConfirmationClass(true, false))
                yield return "L404 start-family-overreaches: a mission modal that is NOT the brief now counts " +
                             "as a mission-start confirmation. The outcome sibling starts nothing and the " +
                             "owner's ruling names exactly one family; widening it puts the idempotence " +
                             "gate (L405) on windows that never launch anything.";
            if (!GeoWindowCoverage.IsPerPeerAnswerClass(true, false, true) ||
                GeoWindowCoverage.IsMissionStartConfirmationClass(true, false))
                yield return "L404 start-family-overreaches: the OUTCOME sibling must stay in the per-peer " +
                             "answer class and OUT of the start family. Collapsing the two loses the " +
                             "distinction the 2026-08-10 decision is made of.";

            // ── (c) every other family is untouched ─────────────────────────────────────────────────
            foreach (var brief in new[] { true, false })
                foreach (var outcome in new[] { true, false })
                {
                    if (GeoWindowCoverage.IsMissionStartConfirmationClass(false, brief))
                        yield return "L404 other-families-went-per-peer: a window with NO live GeoMission " +
                                     "(isBrief=" + brief + ") is a mission-start confirmation. Interception " +
                                     "briefs, haven infiltration and every ability prompt carry no GeoMission " +
                                     "— they are LocalOnly or shared, never this class.";
                    if (GeoWindowCoverage.IsPerPeerAnswerClass(false, brief, outcome))
                        yield return "L404 other-families-went-per-peer: a window with NO live GeoMission " +
                                     "(isBrief=" + brief + ", isOutcome=" + outcome + ") is per-peer. Shared " +
                                     "first-wins is still the RULE for every family but this one.";
                }
            if (GeoWindowCoverage.IsPerPeerAnswerClass(true, false, false))
                yield return "L404 other-families-went-per-peer: a mission modal that is neither the brief nor " +
                             "the outcome is per-peer. The class is the GAME'S — anything else the game would " +
                             "answer over a GeoMission keeps shared semantics.";
            // The start family must be a strict SUBSET of the per-peer answer class: a start confirmation
            // that is not per-peer would be answered once for everybody, which is the defect inverted.
            foreach (var mission in new[] { true, false })
                foreach (var brief in new[] { true, false })
                    foreach (var outcome in new[] { true, false })
                        if (GeoWindowCoverage.IsMissionStartConfirmationClass(mission, brief) &&
                            !GeoWindowCoverage.IsPerPeerAnswerClass(mission, brief, outcome))
                            yield return "L404 start-family-not-per-peer: (" + mission + "," + brief + "," +
                                         outcome + ") is a start confirmation that is NOT in the per-peer " +
                                         "answer class, so its Cancel would run GeoMission.Cancel:253 for the " +
                                         "whole team — the 2026-08-08 report, back.";

            // ── (d) nobody is locked out of answering their own copy ────────────────────────────────
            if (!PerPeerModalAnswer.Runs(true, true, true))
                yield return "L404 start-answer-was-blocked: a Confirm on the per-peer class no longer runs. " +
                             "\"без блокировки выбора\" — every peer's copy stays answerable; a wholesale block " +
                             "would mean no peer can ever deploy.";
            if (PerPeerModalAnswer.Runs(true, true, false))
                yield return "L404 start-answer-was-blocked: a NON-Confirm on the per-peer class runs the game's " +
                             "own arm again, i.e. GeoMission.Cancel:253 deletes the mission for the team when " +
                             "one player says \"not now\".";
            if (!PerPeerModalAnswer.Runs(true, false, false) || !PerPeerModalAnswer.Runs(false, true, false))
                yield return "L404 start-answer-was-blocked: a window outside the class, or any window outside " +
                             "a session, no longer runs its own Cancel. The carve-out may only ever remove " +
                             "behaviour from the one class it names.";
            // A first Confirm with nothing yet committed still reaches the native LaunchMission — the peer
            // that clicks is never waiting on anybody (P13, no quorum).
            if (!MissionSync.RunNativeMissionOfferAnswer(true, true, true, false, null, false))
                yield return "L404 start-answer-was-blocked: a Confirm with no durable offer resolved and no " +
                             "start committed no longer reaches LaunchMission:1043. That is the legacy path " +
                             "every non-durable brief still rides.";

            // ── (e) the shared first-wins freeze cannot name this family ────────────────────────────
            var frozenParams = frozen.GetParameters();
            if (frozenParams.Length == 0 || frozenParams[0].ParameterType != typeof(GeoscapeEvent))
                yield return "L404 freeze-can-reach-the-start-family: EventPopup.IsFrozen's subject is no " +
                             "longer a GeoscapeEvent (" + (frozenParams.Length == 0 ? "<none>" :
                             frozenParams[0].ParameterType.Name) + "). That predicate IS the shared first-wins " +
                             "read-only lock (EventPopup.cs:1225, painted by FreezeChoiceButtons:1150); a " +
                             "mission-start confirmation carries a GeoMission and must stay unreachable from it.";

            // ── (f) the predicates are actually wired into the answer path ──────────────────────────
            var callees = Program.Callees(prefix, typeof(MissionSync).Assembly).ToArray();
            if (!callees.Any(c => c.Name == "IsMissionStartConfirmation"))
                yield return "L404 wiring-dropped: PerPeerModalAnswer.Prefix no longer calls " +
                             "GeoWindowCoverage.IsMissionStartConfirmation. The carve-out would then be a pure " +
                             "function nothing reaches, and every arm above would stay green over a dead rule.";
            if (!callees.Any(c => c.Name == "MissionStartAlreadyCommitted"))
                yield return "L404 wiring-dropped: PerPeerModalAnswer.Prefix no longer calls " +
                             "MissionSync.MissionStartAlreadyCommitted, so a second peer's Confirm falls " +
                             "through to the native LaunchMission a second time (L405).";

            // POSITIVE CONTROL: the truth-table driver really does observe a difference.
            if (GeoWindowCoverage.IsMissionStartConfirmationClass(true, true) ==
                GeoWindowCoverage.IsMissionStartConfirmationClass(true, false))
                yield return "L404 control-not-red: IsMissionStartConfirmationClass returns the same answer " +
                             "for a brief and a non-brief, so every arm above is comparing a constant.";
        }
    }
}
