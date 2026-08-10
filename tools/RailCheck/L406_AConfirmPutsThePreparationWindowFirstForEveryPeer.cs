using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L406 — ONE PEER'S CONFIRM PUTS THE PREPARATION WINDOW FIRST IN EVERY PEER'S QUEUE.
    ///
    /// OWNER DECISION 2026-08-10, verbatim: "а окно подготовки к высадке у всех на первое место перемещается
    /// если кто-то выбрал начать миссию." A PUSH, not a wait: nothing here reads another peer's readiness and
    /// nothing blocks on a human action (P13, NO QUORUM). A peer that is AFK simply finds the window on top
    /// of its own queue whenever it looks.
    ///
    /// THE PROMOTION IS THE OCCURRENCE, NOT A NUMBER OF OURS. Two facts do the whole job and both already
    /// existed; this law is what stops either from being quietly dropped:
    ///   1. <c>DurableInboxStore.TryStartDeployment</c>:99-101 mints the <c>DeploymentPreparing</c> successor
    ///      for <c>_ledger.Members</c> — EVERY member — off ONE peer's Confirm, and tombstones the offer for
    ///      every member in the same commit.
    ///   2. <c>DurableWindowRegistry.PriorityOf</c>:318 ranks that family <c>Deployment</c>, the MAXIMUM of
    ///      <c>DurableWindowPriority</c>, and <c>WindowOrder.DurablePriorityHead</c>:131 sorts
    ///      <c>PriorityOf</c> DESCENDING before <c>HostOrderKey</c> — so it beats an older brief, an ambush
    ///      or any ordinary notice already sitting in that peer's list.
    /// The native side needs nothing: <c>GeoscapeView.ToDeploymentState</c>:596 already queues
    /// <c>UIStateRosterDeployment</c> at <c>int.MaxValue</c>, which is why the rank table deliberately does
    /// NOT name it (<see cref="ReplenishSync.RankFor"/>).
    ///
    /// Arms (each falsified for real, one defect at a time, restored and re-verified between):
    ///   (a) <c>preparation-not-top-priority</c> — the preparation family outranks every other family the
    ///       registry knows, and <c>Deployment</c> is the maximum of the enum.
    ///   (b) <c>promotion-missed-a-peer</c> — driven over the REAL store with THREE members, only ONE of
    ///       which holds the offer Open: all three get a Queued preparation entry at the same
    ///       <c>HostOrderKey</c>, and none of them keeps a live offer.
    ///   (c) <c>promotion-waited-on-a-peer</c> — the commit does not consult any member's lifecycle: a member
    ///       who never opened the window, and a member who never had a copy at all, are promoted anyway. A
    ///       transition that required every member to be Open would be a quorum.
    ///   (d) <c>native-rank-was-overridden</c> — the rank table still leaves the squad screen alone, so the
    ///       promotion cannot be re-implemented as a second, competing number.
    ///
    /// Falsify: replace <c>_ledger.Members</c> with the answering member in
    /// <c>TryStartDeployment</c>:99 → (b); make <c>PriorityOf</c> return <c>Priority</c> for
    /// <c>DeploymentPreparing</c> → (a); add a "all members Open" precondition to the transition → (c);
    /// add <c>UIStateRosterDeployment</c> to <see cref="ReplenishSync.RankFor"/>'s table → (d).
    /// </summary>
    internal static class L406_AConfirmPutsThePreparationWindowFirstForEveryPeer
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var priorityOf = typeof(DurableWindowRegistry).GetMethod("PriorityOf", AllMembers);
            var head = typeof(WindowOrder).GetMethod("DurablePriorityHead", AllMembers);
            var transition = typeof(DurableInboxStore).GetMethod("TryStartDeployment", AllMembers);
            if (priorityOf == null || head == null || transition == null)
            {
                yield return "L406 premise-changed: DurableWindowRegistry.PriorityOf, " +
                             "WindowOrder.DurablePriorityHead or DurableInboxStore.TryStartDeployment no " +
                             "longer resolves — the promotion has moved and every arm below is asleep";
                yield break;
            }

            const string subject = "S#77|missiondef-guid";
            var offer = new OccurrenceId("Modal:GeoAlienBaseBrief", "raise-7", new[] { subject });
            var preparation = new OccurrenceId("DeploymentPreparing", "deployment:raise-7", new[] { subject });

            // ── (a) top of the durable order, over every family the registry actually knows ─────────
            var prepPriority = DurableWindowRegistry.PriorityOf(preparation);
            if (prepPriority != DurableWindowPriority.Deployment)
                yield return "L406 preparation-not-top-priority: DeploymentPreparing ranks " + prepPriority +
                             ", not Deployment. DurablePriorityHead sorts PriorityOf DESCENDING, so anything " +
                             "below the top means a brief, an ambush or an ordinary notice can sit in front " +
                             "of the window the owner ruled goes first.";
            foreach (DurableWindowPriority value in Enum.GetValues(typeof(DurableWindowPriority)))
                if (value > prepPriority)
                    yield return "L406 preparation-not-top-priority: DurableWindowPriority." + value +
                                 " now outranks the preparation window. Deployment must stay the maximum of " +
                                 "the enum — a higher band is a window that legally goes in front of it.";
            foreach (var family in new[] { "Modal:GeoAlienBaseBrief", "Modal:GeoAmbushBrief", "AssetDestination",
                                           "mission-offer", "ambush", "SomeOrdinaryNotice" })
            {
                var other = new OccurrenceId(family, "raise-7", new[] { subject });
                if (DurableWindowRegistry.PriorityOf(other) >= prepPriority)
                    yield return "L406 preparation-not-top-priority: the family '" + family + "' ranks at or " +
                                 "above the preparation window, so it would tie or win the head and the " +
                                 "promotion would depend on HostOrderKey — i.e. on who was raised first.";
            }

            // ── (b) + (c) ONE Confirm, EVERY member, no readiness consulted ─────────────────────────
            var answering = new MembershipId("clicked");
            var afk = new MembershipId("afk-suspended");
            var never = new MembershipId("never-had-the-offer");
            var members = new[] { answering, afk, never };
            var store = new DurableInboxStore(new HostLedger(new[]
            {
                new InboxEntry(offer, answering, InboxLifecycle.Open, default(CanonicalChoiceId), 1, 0,
                    new HostOrderKey(1, offer.TriggerId)),
                // The AFK peer's copy is still QUEUED — it never even opened the window and never will.
                new InboxEntry(offer, afk, InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0,
                    new HostOrderKey(1, offer.TriggerId)),
            }, 1, members));
            store.Carriers.Register(offer, DurableCarrierClass.NativeCurrent, new NoopCarrier());
            string refusal;
            if (!new DurableMissionOfferEngine(store, _ => { }).TryStart(offer, preparation, _ => true, out refusal))
            {
                yield return "L406 promotion-waited-on-a-peer: ONE peer's Confirm was refused (" +
                             (refusal ?? "<null>") + ") over a roster whose other members never opened it or " +
                             "have no copy at all. That is a quorum by another name — the transition must " +
                             "never read another member's lifecycle (P13).";
                yield break;
            }
            var prepEntries = store.Ledger.AllEntries.Where(x => x.Occurrence.Equals(preparation)).ToArray();
            foreach (var m in members)
                if (!prepEntries.Any(x => x.Membership.Equals(m)))
                    yield return "L406 promotion-missed-a-peer: member '" + m.PlayerGuid + "' got no " +
                                 "DeploymentPreparing entry from another peer's Confirm, so that player's " +
                                 "queue never receives the window at all — not first, not last, not ever.";
            if (prepEntries.Length != members.Length ||
                prepEntries.Select(x => x.HostOrderKey).Distinct().Count() != 1)
                yield return "L406 promotion-missed-a-peer: the preparation landed " + prepEntries.Length +
                             " time(s) for " + members.Length + " member(s), or at differing HostOrderKeys. " +
                             "One shared window means one order key, or the peers disagree about what is " +
                             "first the moment a second window exists.";
            if (prepEntries.Any(x => x.Lifecycle == InboxLifecycle.Removed))
                yield return "L406 promotion-missed-a-peer: a freshly promoted preparation entry is already " +
                             "Removed, so that peer's queue drops it before it can ever be presented.";
            if (store.Ledger.AllEntries.Any(x => x.Occurrence.Equals(offer) &&
                                                 x.Lifecycle != InboxLifecycle.Removed))
                yield return "L406 promotion-missed-a-peer: an offer copy survived the transition, so some " +
                             "peer keeps a mission-start window over a mission whose start already happened " +
                             "and the preparation is not what it sees first.";

            // ── (d) the native rank is the game's, and stays the game's ─────────────────────────────
            if (ReplenishSync.RankFor(typeof(UIStateRosterDeployment)) != null)
                yield return "L406 native-rank-was-overridden: the rank table now names " +
                             "UIStateRosterDeployment. ToDeploymentState:596 already queues it at int.MaxValue " +
                             "— a second number for the same decision is two mechanisms that can disagree, " +
                             "and the table's whole safety property is that it only moves what it names.";

            // POSITIVE CONTROL: the priority comparison observes a real difference.
            if (DurableWindowRegistry.PriorityOf(preparation) ==
                DurableWindowRegistry.PriorityOf(new OccurrenceId("SomeOrdinaryNotice", "t", new[] { subject })))
                yield return "L406 control-not-red: PriorityOf answers the same for the preparation window and " +
                             "for an ordinary notice, so arm (a) is comparing a constant.";
        }

        private sealed class NoopCarrier : IDurableOccurrenceCarrier
        {
            public void RemoveWithoutCallback(TerminalReason reason) { }
        }
    }
}
