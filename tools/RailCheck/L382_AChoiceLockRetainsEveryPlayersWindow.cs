using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    internal static class L382_AChoiceLockRetainsEveryPlayersWindow
    {
        internal static IEnumerable<string> Check()
        {
            if (!EventPopup.IsAuthoritativeRewardSender(42, 42) ||
                EventPopup.IsAuthoritativeRewardSender(42, 41) ||
                EventPopup.IsAuthoritativeRewardSender(null, 42))
                yield return "L382 peer-accepted-decision-or-reward-from-non-host-sender";
            var o = new OccurrenceId("event", "raise:382", new[] { "site" });
            var winner = new MembershipId("winner", 4); var loser = new MembershipId("loser", 7);
            var entries = new[] {
                new InboxEntry(o, winner, InboxLifecycle.Open, default(CanonicalChoiceId), 2, 0, new HostOrderKey(1, o.TriggerId)),
                new InboxEntry(o, loser, InboxLifecycle.Suspended, default(CanonicalChoiceId), 3, 0,
                    new HostOrderKey(1, o.TriggerId), InboxSuspensionReason.PriorityPreemption,
                    new InboxWindowCheckpoint("choices", "", "2")) };
            var store = new DurableInboxStore(new HostLedger(entries, 3, new[] {
                new KeyValuePair<MembershipId, MemberPresence>(winner, MemberPresence.Active),
                new KeyValuePair<MembershipId, MemberPresence>(loser, MemberPresence.Tactical) }));
            int repaints = 0;
            bool applied = false;
            var engine = new DurableSharedChoiceEngine(store, new DelegateDurableChoiceEffect(_ => applied = true,
                _ => applied ? DurableEffectObservation.After : DurableEffectObservation.Before), _ => repaints++);
            SharedChoiceDecision decision;
            if (!engine.TryAnswer(o, winner, new CanonicalChoiceId(o, "stable-choice"),
                new CanonicalResultId(o, "stable-result"), Array.Empty<CanonicalRewardItemId>(), () => true, out decision))
                yield return "L382 valid-choice-refused";
            var after = store.Ledger.AllEntries.Where(x => x.Occurrence.Equals(o)).ToArray();
            if (after.Length != 2 || after[0].Lifecycle != InboxLifecycle.Open || after[1].Lifecycle != InboxLifecycle.Suspended)
                yield return "L382 choice-lock-terminated-an-entitlement";
            if (after.Any(x => x.Choice.Value != "stable-choice") || repaints != 1)
                yield return "L382 copies-not-updated-before-immediate-repaint";
            var dismissed = after[0].WithLifecycle(InboxLifecycle.Dismissed, after[0].LifecycleRevision + 1);
            var next = store.Ledger.Replace(dismissed).WithAuthority(store.Ledger.CommittedRevision + 1, store.Ledger.Members);
            if (!store.Commit(store.Ledger, next) || store.Ledger.Get(o, loser).Lifecycle != InboxLifecycle.Suspended)
                yield return "L382 local-dismiss-changed-another-player";

            // POSITIVE CONTROL: current-only retention would leave exactly one entitlement.
            var currentOnly = after.Where(x => x.Membership.Equals(winner)).ToArray();
            if (currentOnly.Length != 1 || currentOnly.Length == after.Length)
                yield return "L382 positive-control-current-only-removal-did-not-shrink-entitlements";

            var peer = new DurableInboxStore(new HostLedger(entries, 3, new[] {
                new KeyValuePair<MembershipId, MemberPresence>(winner, MemberPresence.Active),
                new KeyValuePair<MembershipId, MemberPresence>(loser, MemberPresence.Tactical) }));
            SharedChoiceDecision decoded;
            decision = decision.WithRewardPayload(new byte[] { 7, 8, 9 });
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) EventSync.WriteDecision(w, decision);
                ms.Position = 0; using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, true)) decoded = EventSync.ReadDecision(r);
            }
            if (!decoded.Equals(decision) || !decoded.RewardPayload.SequenceEqual(new byte[] { 7, 8, 9 }) ||
                !peer.InstallAuthoritativeDecision(decoded) ||
                peer.Ledger.AllEntries.Any(x => x.Choice.Value != "stable-choice") ||
                peer.Ledger.Get(o, loser).Lifecycle != InboxLifecycle.Suspended)
                yield return "L382 replicated-decision-did-not-update-retained-copies";

            var boundaryPayload = new byte[SharedChoiceDecision.MaxRewardPayloadBytes];
            boundaryPayload[boundaryPayload.Length - 1] = 0x82;
            var boundaryDecision = decision.WithRewardPayload(boundaryPayload);
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) EventSync.WriteDecision(w, boundaryDecision);
                ms.Position = 0;
                using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, true)) decoded = EventSync.ReadDecision(r);
            }
            if (decoded.RewardPayload.Length != SharedChoiceDecision.MaxRewardPayloadBytes ||
                decoded.RewardPayload[decoded.RewardPayload.Length - 1] != 0x82)
                yield return "L382 exact-boundary-reward-payload-did-not-roundtrip-wire";
            bool oversizedRejected = false;
            try
            {
                decision.WithRewardPayload(new byte[SharedChoiceDecision.MaxRewardPayloadBytes + 1]);
            }
            catch (ArgumentOutOfRangeException) { oversizedRejected = true; }
            if (!oversizedRejected) yield return "L382 max-plus-one-reward-payload-was-accepted";
        }
    }
}
