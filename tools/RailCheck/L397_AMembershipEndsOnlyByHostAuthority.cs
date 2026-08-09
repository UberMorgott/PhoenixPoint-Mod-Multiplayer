using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L397 / DWI-22 — durable membership ends only by authoritative removal.</summary>
    internal static class L397_AMembershipEndsOnlyByHostAuthority
    {
        internal static IEnumerable<string> Check()
        {
            var member = new MembershipId("campaign-player-guid", 9);
            var queued = new OccurrenceId("event", "queued", new[] { "subject" });
            var dismissed = new OccurrenceId("event", "dismissed", new[] { "subject" });
            var removed = new OccurrenceId("event", "removed", new[] { "subject" });
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            host.Enroll(member, MemberPresence.Active);
            foreach (var presence in new[] { MemberPresence.Disconnected, MemberPresence.Loading, MemberPresence.Tactical, MemberPresence.NonGeoscape })
                if (!host.SetPresence(member, presence)) yield return "L397 ordinary-presence-change-ended-membership";
            host.CreateOccurrence(queued);
            host.CreateOccurrence(dismissed);
            host.CreateOccurrence(removed);
            host.ApplyLifecycle(member, dismissed, InboxLifecycle.Open, 2);
            host.ApplyLifecycle(member, dismissed, InboxLifecycle.Dismissed, ulong.MaxValue);
            host.Tombstone(removed, ulong.MaxValue);
            if (host.Reconnect(member).Count != 3) yield return "L397 retained-member-lost-entitlement";

            if (!host.EndMembership(member) || host.Reconnect(member).Count != 0)
                yield return "L397 authoritative-removal-did-not-end-membership";
            var queuedEntry = host.Ledger.Get(queued, member);
            var dismissedEntry = host.Ledger.Get(dismissed, member);
            var removedEntry = host.Ledger.Get(removed, member);
            if (queuedEntry.Lifecycle != InboxLifecycle.Removed || queuedEntry.LifecycleRevision != 2 || queuedEntry.TombstoneRevision != 2)
                yield return "L397 nonterminal-entitlement-was-not-removed";
            if (dismissedEntry.Lifecycle != InboxLifecycle.Dismissed || dismissedEntry.LifecycleRevision != ulong.MaxValue || dismissedEntry.TombstoneRevision != 0 ||
                removedEntry.Lifecycle != InboxLifecycle.Removed || removedEntry.LifecycleRevision != ulong.MaxValue || removedEntry.TombstoneRevision != ulong.MaxValue)
                yield return "L397 terminal-entitlement-was-rewritten-or-revision-wrapped";

            var maxMember = new MembershipId("campaign-player-guid", 10);
            var maxHost = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            maxHost.Enroll(maxMember, MemberPresence.Active);
            var maxOccurrences = new[]
            {
                new OccurrenceId("event", "max-queued", new[] { "subject" }),
                new OccurrenceId("event", "max-open", new[] { "subject" }),
                new OccurrenceId("event", "max-read", new[] { "subject" }),
                new OccurrenceId("event", "max-terminal", new[] { "subject" })
            };
            foreach (var occurrence in maxOccurrences) maxHost.CreateOccurrence(occurrence);
            maxHost.ApplyLifecycle(maxMember, maxOccurrences[0], InboxLifecycle.Queued, ulong.MaxValue);
            maxHost.ApplyLifecycle(maxMember, maxOccurrences[1], InboxLifecycle.Open, ulong.MaxValue);
            maxHost.ApplyLifecycle(maxMember, maxOccurrences[2], InboxLifecycle.Read, ulong.MaxValue);
            maxHost.ApplyLifecycle(maxMember, maxOccurrences[3], InboxLifecycle.Open, 2);
            maxHost.ApplyLifecycle(maxMember, maxOccurrences[3], InboxLifecycle.Dismissed, ulong.MaxValue);
            var beforeEndLedger = maxHost.Ledger.EncodeCanonical();
            var beforeEndRevision = maxHost.CommittedRevision;
            var endOverflowed = false;
            try
            {
                maxHost.EndMembership(maxMember);
            }
            catch (System.OverflowException)
            {
                endOverflowed = true;
            }
            if (!endOverflowed) yield return "L397 max-revision-membership-end-did-not-reject";
            if (!beforeEndLedger.SequenceEqual(maxHost.Ledger.EncodeCanonical()) ||
                maxHost.CommittedRevision != beforeEndRevision || maxHost.Reconnect(maxMember).Count != 4)
                yield return "L397 rejected-membership-end-changed-authority-state";
            foreach (var occurrence in maxOccurrences)
                if (!maxHost.Ledger.EntriesFor(maxMember).Any(entry => entry.Occurrence.Equals(occurrence)))
                    yield return "L397 rejected-membership-end-partially-removed-ledger";
            if (maxHost.Ledger.Get(maxOccurrences[3], maxMember).Lifecycle != InboxLifecycle.Dismissed ||
                maxHost.Ledger.Get(maxOccurrences[3], maxMember).LifecycleRevision != ulong.MaxValue)
                yield return "L397 terminal-max-control-was-rewritten";

            // POSITIVE CONTROL: no Steam id, roster slot, ACK, readiness, or quorum input exists on the authority methods.
            foreach (var method in typeof(HostInboxSequencer).GetMethods(System.Reflection.BindingFlags.Instance |
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                foreach (var parameter in method.GetParameters())
                    if (parameter.Name.ToLowerInvariant().Contains("steam") || parameter.Name.ToLowerInvariant().Contains("slot") ||
                        parameter.Name.ToLowerInvariant().Contains("ack") || parameter.Name.ToLowerInvariant().Contains("ready") ||
                        parameter.Name.ToLowerInvariant().Contains("quorum"))
                        yield return "L397 forbidden-human-or-connection-identity-input: " + method.Name;
        }
    }
}
