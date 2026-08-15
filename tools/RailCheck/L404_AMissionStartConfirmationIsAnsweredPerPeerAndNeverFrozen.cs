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
    ///   (e) <c>freeze-can-reach-the-start-family</c> — RE-AIMED 2026-08-10. The family is ALSO raised as a
    ///       plain geoscape EVENT (a choice whose <c>OutcomeStartMission.MissionTypeDef</c> is set), and that
    ///       is the form the player actually meets: in the 22:42:36-22:45:38 playtest of b63e1aa itself no
    ///       <c>UIStateGeoModal</c> was entered on any peer while <c>PROG_AN0_MISS</c> came up as an event on
    ///       all three, and the second client sat in <c>UIStateGeoscapeEvent</c> from 121,0 s to 179,2 s
    ///       behind buttons another peer's answer had greyed. So the arm no longer asserts that
    ///       <c>EventPopup.IsFrozen</c> cannot NAME the family — it asserts that IsFrozen EXEMPTS it
    ///       (<c>EventPopup.NeverFrozenClass</c>), that the exemption is wired into IsFrozen and into
    ///       <c>EventChoiceClientLock.Prefix</c>, and that it does not spread to any other event.
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
            var neverFrozen = typeof(EventPopup).GetMethod("NeverFrozenClass", AllMembers);
            var eventFamily = typeof(EventPopup).GetMethod("IsMissionStartConfirmationEvent", AllMembers);
            if (startClass == null || perPeerClass == null || live == null || runs == null ||
                prefix == null || frozen == null || neverFrozen == null || eventFamily == null)
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

            // ── (e) the shared first-wins freeze EXEMPTS this family ────────────────────────────────
            // RE-AIMED 2026-08-10. This arm used to assert that IsFrozen "cannot name" the family, on the
            // reasoning that a start confirmation carries a GeoMission and IsFrozen's subject is a
            // GeoscapeEvent. THAT WAS THE WRONG SUBJECT, and it is why L404 stayed green over a game that
            // was broken: the window the player is actually shown for "НАЧАТЬ / ОТМЕНА" is raised as a
            // GEOSCAPE EVENT, not as a UIStateGeoModal — MEASURED in the 22:42:36-22:45:38 run of b63e1aa
            // itself, where no UIStateGeoModal was entered on any peer and PROG_AN0_MISS came up as an event
            // on all three. The freeze DOES reach the family; what the owner's ruling requires is that it
            // step aside there.
            if (!EventPopup.NeverFrozenClass(true))
                yield return "L404 freeze-can-reach-the-start-family: a geoscape-event window with a " +
                             "mission-starting choice is frozen again. FreezeChoiceButtons then greys every " +
                             "losing button on every peer that did not answer first — the reported symptom: " +
                             "the client's copy is unclickable and the host's pick is locked in on it.";
            if (EventPopup.NeverFrozenClass(false))
                yield return "L404 start-family-overreaches: a window with NO mission-starting choice is now " +
                             "exempt from the freeze too, so ordinary story events lost shared first-wins.";
            var frozenCallees = Program.Callees(frozen, typeof(EventPopup).Assembly).ToArray();
            if (!frozenCallees.Any(c => c.Name == "IsMissionStartConfirmationEvent"))
                yield return "L404 wiring-dropped: EventPopup.IsFrozen no longer asks " +
                             "IsMissionStartConfirmationEvent, so the exemption is a pure function nothing " +
                             "reaches and every arm above is green over a dead rule (the L394 trap).";
            var clickPrefix = typeof(EventChoiceClientLock).GetMethod("Prefix", AllMembers);
            if (clickPrefix == null)
                yield return "L404 premise-changed: EventChoiceClientLock.Prefix is gone, so the per-peer " +
                             "answer arm for the geoscape-event mission-start family has no subject.";
            else
            {
                var clickCallees = Program.Callees(clickPrefix, typeof(EventPopup).Assembly).ToArray();
                if (!clickCallees.Any(c => c.Name == "IsMissionStartConfirmationEvent"))
                    yield return "L404 wiring-dropped: EventChoiceClientLock.Prefix no longer asks " +
                                 "IsMissionStartConfirmationEvent, so a peer's click on its own copy is back " +
                                 "on the shared path — replayed, or refused as already answered.";
                if (!clickCallees.Any(c => c.Name == "StartsMission"))
                    yield return "L404 start-answer-was-blocked: EventChoiceClientLock.Prefix no longer " +
                                 "separates the mission-starting choice from the decline, so either a cancel " +
                                 "resolves the offer for the whole team or a Confirm never reaches the host.";
            }

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

            // ── (g) THE HOST ACCEPTS THE RELAYED ANSWER IT CANNOT SECOND-GUESS ──────────────────────
            // Live 2026-08-10: the client's START relayed on 0xB4 and came back "[MP][intent] HOST event
            // REJECT peer=2 — stale occurrence lifecycle revision", dumping that player on the geoscape while
            // the same window worked on the host. The durable inbox is PER-PEER session state
            // (DurableInboxSession.OpenSessionStore:50 seeds Members with the local guid alone), so the host's
            // ledger NEVER holds a client's membership and the equality test could only ever reject. A
            // per-peer answer that cannot reach the host is the carve-out deleted at its last mile.
            var refusal = typeof(EventSync).GetMethod("RevisionRefusal", AllMembers);
            if (refusal == null)
                yield return "L404 premise-changed: EventSync.RevisionRefusal no longer resolves — the relayed " +
                             "answer's acceptance test has moved and arm (g) is asleep";
            else
            {
                var entry = new InboxEntry(new OccurrenceId("event:X", "t", new[] { "event:X" }),
                    new MembershipId("someone-else"), InboxLifecycle.Open, default(CanonicalChoiceId), 7, 0);
                Func<bool, InboxEntry, ulong, string> gate = (knows, e, rev) =>
                    (string)refusal.Invoke(null, new object[] { knows, e, rev });
                if (gate(false, null, 7UL) != null)
                    yield return "L404 relayed-answer-refused: the host refuses a relayed answer whose " +
                                 "membership its own per-peer ledger cannot hold (" + gate(false, null, 7UL) +
                                 "). That is EVERY client's mission start: the ledger has one member, the " +
                                 "local peer, so the answer is authorised by occurrence identity and " +
                                 "SenderOwnsMembership — not by a revision this peer never sees.";
                if (gate(true, entry, 6UL) == null || gate(true, null, 7UL) == null)
                    yield return "L404 relayed-answer-unchecked: the gate accepts a genuinely stale revision, " +
                                 "or an answer for a membership this peer OWNS but has no entry for. The " +
                                 "check keeps its full force where this peer can actually evaluate it.";
                if (gate(true, entry, 7UL) != null)
                    yield return "L404 relayed-answer-refused: the gate refuses a matching revision for a " +
                                 "membership this peer owns, so no answer would ever be accepted at all.";
            }

            // POSITIVE CONTROL: the truth-table driver really does observe a difference.
            if (GeoWindowCoverage.IsMissionStartConfirmationClass(true, true) ==
                GeoWindowCoverage.IsMissionStartConfirmationClass(true, false))
                yield return "L404 control-not-red: IsMissionStartConfirmationClass returns the same answer " +
                             "for a brief and a non-brief, so every arm above is comparing a constant.";
        }
    }
}
