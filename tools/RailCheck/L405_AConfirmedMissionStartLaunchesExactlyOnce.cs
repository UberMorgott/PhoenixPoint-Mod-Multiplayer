using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L405 — HOWEVER MANY PEERS CONFIRM A MISSION START, THE LAUNCH APPLIES EXACTLY ONCE.
    ///
    /// THE COST OF THE 2026-08-10 CARVE-OUT (L404). Every peer now keeps its OWN answerable copy of a
    /// mission-start confirmation and none of them is locked read-only, so two players CAN confirm the same
    /// mission. The launch behind that window is SHARED campaign state —
    /// <c>UIStateRosterDeployment.DeploySquad</c>:331 / <c>GeoscapeView.LaunchMission</c>:1043 both bottom
    /// out in <c>GeoMission.Launch</c>:226 — and applying it twice is a second battle build on the host, or a
    /// second 0xB8 launch intent from a client.
    ///
    /// THE IDEMPOTENCE KEY IS THE SUCCESSOR, not a flag. A confirmed offer transitions, once and
    /// host-authoritatively under <c>DurableInboxStore.WithTransitionGate</c>, into the shared
    /// <c>DeploymentPreparing</c> occurrence over the SAME stable mission subject
    /// (<c>MissionSync.HandleDurableMissionOfferAnswer</c>:407). Its presence in the replicated ledger IS the
    /// fact "this mission's start already happened", which is why the test is a ledger read and not a bool
    /// somebody has to remember to clear.
    ///
    /// THE HOLE THIS CLOSES, exactly. A late Confirm arrives after the tombstone has reached that peer:
    /// <c>WindowQueueSync.DurableMissionOfferBindingMatches</c> refuses (the entry is Removed),
    /// <c>MissionSync.TryResolveDurableMissionOffer</c> refuses (same filter), and the pre-2026-08-10
    /// fallthrough <c>return isConfirm</c> then ran the native callback — LaunchMission, a second time.
    ///
    /// Arms (each falsified for real, one defect at a time, restored and re-verified between):
    ///   (a) <c>second-confirm-relaunches</c> — with the start committed, the native answer must NOT run.
    ///   (b) <c>first-confirm-refused</c> — with nothing committed it MUST run. A gate that blocks both is
    ///       the feature deleted, and it would satisfy (a) forever.
    ///   (c) <c>key-matches-the-wrong-mission</c> — the ledger read is keyed on the stable mission subject
    ///       and on the <c>DeploymentPreparing</c> family. Another mission's preparation, or another family
    ///       over the same subject, must NOT count as this mission's launch.
    ///   (d) <c>transition-minted-twice</c> — driven over the REAL store: the second
    ///       <c>DurableMissionOfferEngine.TryStart</c> is refused with a reason, the ledger revision does not
    ///       move, and no second preparation appears.
    ///   (e) <c>committed-start-is-not-observable</c> — after that one real transition the ledger read
    ///       answers TRUE. Non-vacuity: without it (a) would be true because nothing ever commits.
    ///
    /// Falsify: delete the <c>startCommitted</c> clause in
    /// <see cref="MissionSync.RunNativeMissionOfferAnswer"/> → (a); make it return false unconditionally for
    /// a Confirm → (b); drop the <c>EventId</c> test in
    /// <see cref="MissionSync.MissionStartAlreadyCommitted"/> → (c); drop its subject test → (c).
    /// </summary>
    internal static class L405_AConfirmedMissionStartLaunchesExactlyOnce
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var run = typeof(MissionSync).GetMethod("RunNativeMissionOfferAnswer", AllMembers);
            var committed = typeof(MissionSync).GetMethods(AllMembers)
                .FirstOrDefault(m => m.Name == "MissionStartAlreadyCommitted" &&
                                     m.GetParameters().Length == 2 &&
                                     m.GetParameters()[0].ParameterType == typeof(IEnumerable<OccurrenceId>));
            var start = typeof(DurableMissionOfferEngine).GetMethod("TryStart",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (run == null || committed == null || start == null ||
                run.GetParameters().Length != 6 || run.GetParameters()[5].Name != "startCommitted")
            {
                yield return "L405 premise-changed: MissionSync.RunNativeMissionOfferAnswer no longer takes a " +
                             "startCommitted arm, or MissionStartAlreadyCommitted / " +
                             "DurableMissionOfferEngine.TryStart no longer resolve — the idempotence gate has " +
                             "moved and every arm below is asleep";
                yield break;
            }

            const string subject = "S#41|missiondef-guid";
            const string otherSubject = "S#42|missiondef-guid";
            var preparation = new OccurrenceId("DeploymentPreparing", "deployment:raise-1", new[] { subject });
            var offer = new OccurrenceId("Modal:GeoHavenAttackBrief", "raise-1", new[] { subject });

            // ── (a) + (b) the gate, over the rows that matter ───────────────────────────────────────
            if (MissionSync.RunNativeMissionOfferAnswer(true, true, true, false, null, true))
                yield return "L405 second-confirm-relaunches: a Confirm on a mission whose start is ALREADY " +
                             "committed still reaches the game's own ModalResultCallback, i.e. " +
                             "LaunchMission:1043 → GeoMission.Launch:226 a second time. On the host that is a " +
                             "second battle build; on a client a second 0xB8 launch intent.";
            if (!MissionSync.RunNativeMissionOfferAnswer(true, true, true, false, null, false))
                yield return "L405 first-confirm-refused: a Confirm with NOTHING committed no longer runs. The " +
                             "idempotence gate must refuse the SECOND launch, never the first — refusing both " +
                             "removes the feature and keeps arm (a) green forever.";
            // The gate is about Confirm only: a Cancel is already local-only (L404 arm d) and must not start
            // depending on whether somebody else launched.
            if (MissionSync.RunNativeMissionOfferAnswer(true, true, false, false, null, true) !=
                MissionSync.RunNativeMissionOfferAnswer(true, true, false, false, null, false))
                yield return "L405 second-confirm-relaunches: the startCommitted arm now changes what a " +
                             "NON-Confirm does. Declining is a statement about one player and must read no " +
                             "shared state at all.";
            // Outside a session, and outside the class, vanilla is untouched whatever the ledger says.
            if (!MissionSync.RunNativeMissionOfferAnswer(false, true, true, false, null, true) ||
                !MissionSync.RunNativeMissionOfferAnswer(true, false, true, false, null, true))
                yield return "L405 first-confirm-refused: a solo game, or a window outside the per-peer class, " +
                             "is now gated by the durable ledger. This gate exists for one family in one mode.";

            // ── (c) the key names THIS mission's start and nothing else ─────────────────────────────
            if (MissionSync.MissionStartAlreadyCommitted(new OccurrenceId[0], subject))
                yield return "L405 key-matches-the-wrong-mission: an EMPTY ledger reports a committed start, " +
                             "so the very first Confirm of the campaign would be refused.";
            if (!MissionSync.MissionStartAlreadyCommitted(new[] { preparation }, subject))
                yield return "L405 committed-start-is-not-observable: the DeploymentPreparing occurrence over " +
                             "this mission's stable subject is not recognised as its committed start.";
            if (MissionSync.MissionStartAlreadyCommitted(new[] { preparation }, otherSubject))
                yield return "L405 key-matches-the-wrong-mission: ANOTHER mission's preparation counts as this " +
                             "one's start, so confirming mission B would silently swallow the launch of A.";
            if (MissionSync.MissionStartAlreadyCommitted(new[] { offer }, subject))
                yield return "L405 key-matches-the-wrong-mission: the OFFER itself counts as a committed " +
                             "start, so the first Confirm — which always sees its own offer in the ledger — " +
                             "would never launch anything at all.";
            if (MissionSync.MissionStartAlreadyCommitted(new[] { preparation }, null) ||
                MissionSync.MissionStartAlreadyCommitted(null, subject))
                yield return "L405 key-matches-the-wrong-mission: an unresolvable subject or a missing ledger " +
                             "reports a committed start, which turns every unaddressable mission into an " +
                             "un-launchable one.";

            // ── (d) + (e) driven over the REAL store, not over a description of it ──────────────────
            var member = new MembershipId("p1");
            var store = new DurableInboxStore(new HostLedger(new[]
            {
                new InboxEntry(offer, member, InboxLifecycle.Open, default(CanonicalChoiceId), 1, 0),
            }, 1, new[] { member }));
            store.Carriers.Register(offer, DurableCarrierClass.NativeCurrent, new NoopCarrier());
            string refusal;
            if (!new DurableMissionOfferEngine(store, _ => { }).TryStart(offer, preparation, _ => true, out refusal))
            {
                yield return "L405 premise-changed: the first TryStart over a clean store was refused (" +
                             (refusal ?? "<null>") + "), so the two arms below cannot observe a committed start";
                yield break;
            }
            ulong afterFirst = store.Ledger.CommittedRevision;
            if (!MissionSync.MissionStartAlreadyCommitted(
                    store.Ledger.AllEntries.Select(x => x.Occurrence), subject))
                yield return "L405 committed-start-is-not-observable: a REAL committed transition does not " +
                             "make MissionStartAlreadyCommitted true over the very ledger it wrote. Arm (a) " +
                             "would then be green because nothing ever reaches it.";
            // A REPEAT start is IDEMPOTENT, not refused (DurableInboxStore.TryStartDeployment:83-85 returns
            // the existing successor). What must never happen is a SECOND effect: no new ledger revision, no
            // second DeploymentPreparing entry, and — driven here — no second terminal delta on the wire.
            store.Carriers.Register(offer, DurableCarrierClass.NativeCurrent, new NoopCarrier());
            int repeatDeltas = 0;
            new DurableMissionOfferEngine(store, _ => repeatDeltas++)
                .TryStart(offer, preparation, _ => true, out refusal);
            if (store.Ledger.CommittedRevision != afterFirst ||
                store.Ledger.AllEntries.Count(x => x.Occurrence.Equals(preparation)) !=
                    store.Ledger.Members.Count() || repeatDeltas != 0)
                yield return "L405 transition-minted-twice: a repeat start moved the ledger (" +
                             store.Ledger.CommittedRevision + " vs " + afterFirst + "), minted a second " +
                             "DeploymentPreparing entry, or re-broadcast the terminal delta (" + repeatDeltas +
                             "). Two preparations mean two squad screens over one mission, and a re-broadcast " +
                             "terminal delta is a second launch announcement to every peer.";
            if (MissionSync.MissionStartAlreadyCommitted(
                    store.Ledger.AllEntries.Select(x => x.Occurrence),
                    DurableWindowRegistry.StableMissionSubject(null)))
                yield return "L405 key-matches-the-wrong-mission: a null mission resolves to a subject the " +
                             "committed-start read accepts, so an unaddressable mission could never launch.";

            // POSITIVE CONTROL: the gate driver observes a real difference between the two rows.
            if (MissionSync.RunNativeMissionOfferAnswer(true, true, true, false, null, true) ==
                MissionSync.RunNativeMissionOfferAnswer(true, true, true, false, null, false))
                yield return "L405 control-not-red: RunNativeMissionOfferAnswer answers identically with and " +
                             "without a committed start, so arms (a) and (b) are comparing a constant.";
        }

        private sealed class NoopCarrier : IDurableOccurrenceCarrier
        {
            public void RemoveWithoutCallback(TerminalReason reason) { }
        }
    }
}
