using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L397 / DWI-22 — disconnect host-serially ends an epoch; passive local state does not.</summary>
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
            var beforeDisconnectedPresence = host.CommittedRevision;
            if (host.SetPresence(member, MemberPresence.Disconnected) ||
                host.CommittedRevision != beforeDisconnectedPresence || !host.Ledger.Members.ContainsKey(member))
                yield return "L397 active-epoch-accepted-disconnected-as-passive-presence";
            var currentDisconnectedRejected = false;
            try
            {
                new HostLedger(new InboxEntry[0], 0,
                    new[] { new KeyValuePair<MembershipId, MemberPresence>(new MembershipId("current-player", 1),
                        MemberPresence.Disconnected) });
            }
            catch (System.ArgumentException) { currentDisconnectedRejected = true; }
            if (!currentDisconnectedRejected)
                yield return "L397 current-ledger-accepted-disconnected-membership";
            foreach (var failure in LegacyDisconnectedSaveMigration()) yield return failure;
            foreach (var presence in new[] { MemberPresence.Loading, MemberPresence.Tactical, MemberPresence.NonGeoscape, MemberPresence.Active })
                if (!host.SetPresence(member, presence)) yield return "L397 ordinary-presence-change-ended-membership";
            host.CreateOccurrence(queued);
            host.CreateOccurrence(dismissed);
            host.CreateOccurrence(removed);
            host.ApplyLifecycle(member, dismissed, InboxLifecycle.Open, 2);
            host.ApplyLifecycle(member, dismissed, InboxLifecycle.Dismissed, ulong.MaxValue);
            host.Tombstone(removed, ulong.MaxValue);
            if (host.Reconnect(member).Count != 3) yield return "L397 retained-member-lost-entitlement";

            // The connection owner routes disconnect through this host-serialized epoch-end operation.
            if (!host.EndMembership(member) || host.Reconnect(member).Count != 0)
                yield return "L397 disconnect-did-not-end-membership-epoch";
            var queuedEntry = host.Ledger.Get(queued, member);
            var dismissedEntry = host.Ledger.Get(dismissed, member);
            var removedEntry = host.Ledger.Get(removed, member);
            if (queuedEntry.Lifecycle != InboxLifecycle.Removed || queuedEntry.LifecycleRevision != 2 || queuedEntry.TombstoneRevision != 2)
                yield return "L397 nonterminal-entitlement-was-not-removed";
            if (dismissedEntry.Lifecycle != InboxLifecycle.Dismissed || dismissedEntry.LifecycleRevision != ulong.MaxValue || dismissedEntry.TombstoneRevision != 0 ||
                removedEntry.Lifecycle != InboxLifecycle.Removed || removedEntry.LifecycleRevision != ulong.MaxValue || removedEntry.TombstoneRevision != ulong.MaxValue)
                yield return "L397 terminal-entitlement-was-rewritten-or-revision-wrapped";

            var whileDisconnected = new OccurrenceId("event", "while-disconnected", new[] { "subject" });
            host.CreateOccurrence(whileDisconnected);
            if (host.Ledger.EntriesFor(member).Any(entry => entry.Occurrence.Equals(whileDisconnected)))
                yield return "L397 ended-disconnected-epoch-received-new-occurrence";
            var rejoined = new MembershipId("campaign-player-guid", 10);
            if (!host.Enroll(rejoined, MemberPresence.Active) || host.Reconnect(rejoined).Any())
                yield return "L397 reconnect-did-not-enroll-clean-new-epoch";
            var afterEnrollment = new OccurrenceId("event", "after-reconnect-enrollment", new[] { "subject" });
            host.CreateOccurrence(afterEnrollment);
            if (host.Reconnect(rejoined).Count != 1 ||
                !host.Reconnect(rejoined)[0].Occurrence.Equals(afterEnrollment))
                yield return "L397 new-epoch-did-not-receive-only-post-enrollment-occurrence";

            var maxMember = new MembershipId("campaign-player-guid", 11);
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

            var maxLedger = new HostLedger(
                new[] { new InboxEntry(maxOccurrences[0], maxMember, InboxLifecycle.Queued,
                    default(CanonicalChoiceId), 1, 0,
                    new HostOrderKey(ulong.MaxValue, maxOccurrences[0].TriggerId)) }, ulong.MaxValue,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(maxMember, MemberPresence.Active) });
            foreach (var operation in new System.Func<HostInboxSequencer, bool>[]
            {
                sequencer => sequencer.Enroll(new MembershipId("other", 1), MemberPresence.Active),
                sequencer => sequencer.CreateOccurrence(new OccurrenceId("event", "new", new[] { "subject" })),
                sequencer => sequencer.ApplyLifecycle(maxMember, maxOccurrences[0], InboxLifecycle.Open, 2),
                sequencer => sequencer.Tombstone(maxOccurrences[0], 2)
            })
            {
                var sequencer = new HostInboxSequencer(maxLedger);
                var before = sequencer.Ledger.EncodeCanonical();
                var overflowed = false;
                try { operation(sequencer); }
                catch (System.OverflowException) { overflowed = true; }
                if (!overflowed || sequencer.CommittedRevision != ulong.MaxValue ||
                    !before.SequenceEqual(sequencer.Ledger.EncodeCanonical()))
                    yield return "L397 rejected-overflow-changed-authority";
            }

            // POSITIVE CONTROL: no Steam id, roster slot, ACK, readiness, or quorum input exists on the authority methods.
            foreach (var method in typeof(HostInboxSequencer).GetMethods(System.Reflection.BindingFlags.Instance |
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                foreach (var parameter in method.GetParameters())
                    if (parameter.Name.ToLowerInvariant().Contains("steam") || parameter.Name.ToLowerInvariant().Contains("slot") ||
                        parameter.Name.ToLowerInvariant().Contains("ack") || parameter.Name.ToLowerInvariant().Contains("ready") ||
                        parameter.Name.ToLowerInvariant().Contains("quorum"))
                        yield return "L397 forbidden-human-or-connection-identity-input: " + method.Name;
        }

        private static IEnumerable<string> LegacyDisconnectedSaveMigration()
        {
            var member = new MembershipId("legacy-player", 3);
            var queued = new OccurrenceId("event", "legacy-queued", new[] { "subject" });
            var dismissed = new OccurrenceId("event", "legacy-dismissed", new[] { "subject" });
            var removed = new OccurrenceId("event", "legacy-removed", new[] { "subject" });
            var entries = new[]
            {
                new InboxEntry(queued, member, InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0,
                    new HostOrderKey(3, queued.TriggerId)),
                new InboxEntry(dismissed, member, InboxLifecycle.Dismissed, default(CanonicalChoiceId), 7, 0,
                    new HostOrderKey(4, dismissed.TriggerId)),
                new InboxEntry(removed, member, InboxLifecycle.Removed, default(CanonicalChoiceId), 8, 8,
                    new HostOrderKey(5, removed.TriggerId))
            };
            var activeLedger = new HostLedger(entries, 9,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(member, MemberPresence.Active) });
            var legacyRoot = DurableInboxSaveCodec.CreateSchema1ForMigrationTest(activeLedger);
            SetFirstSnapshotPresence(legacyRoot, member, MemberPresence.Disconnected);
            DurableInboxRestore restored; string refusal;
            if (!DurableInboxSaveCodec.TryRestore(legacyRoot, null, out restored, out refusal))
            {
                yield return "L397 legacy-disconnected-save-refused-instead-of-ending-epoch: " + refusal;
                yield break;
            }
            var queuedAfter = restored.Ledger.Get(queued, member);
            var dismissedAfter = restored.Ledger.Get(dismissed, member);
            var removedAfter = restored.Ledger.Get(removed, member);
            if (restored.Ledger.Members.ContainsKey(member) || queuedAfter.Lifecycle != InboxLifecycle.Removed ||
                queuedAfter.LifecycleRevision != 2 || queuedAfter.TombstoneRevision != 2)
                yield return "L397 legacy-disconnected-epoch-was-resurrected-or-not-terminalized";
            if (dismissedAfter.Lifecycle != InboxLifecycle.Dismissed || dismissedAfter.LifecycleRevision != 7 ||
                dismissedAfter.TombstoneRevision != 0 || removedAfter.Lifecycle != InboxLifecycle.Removed ||
                removedAfter.LifecycleRevision != 8 || removedAfter.TombstoneRevision != 8)
                yield return "L397 legacy-terminal-entry-was-rewritten";
            var sequencer = new HostInboxSequencer(restored.Ledger);
            var later = new OccurrenceId("event", "after-legacy-load", new[] { "subject" });
            sequencer.CreateOccurrence(later);
            if (sequencer.Reconnect(member).Any() ||
                sequencer.Ledger.EntriesFor(member).Any(entry => entry.Occurrence.Equals(later)))
                yield return "L397 ended-legacy-epoch-received-later-entitlement";

            var currentRoot = DurableInboxSaveCodec.Create(activeLedger, new DurableInboxJournalRecord[0],
                DurableInboxCanonicalState.Empty);
            SetFirstSnapshotPresence(currentRoot, member, MemberPresence.Disconnected);
            if (DurableInboxSaveCodec.TryRestore(currentRoot, null, out restored, out refusal))
                yield return "L397 current-schema-disconnected-payload-was-migrated-instead-of-rejected";
        }

        private static void SetFirstSnapshotPresence(DurableInboxSaveRoot root, MembershipId member,
            MemberPresence presence)
        {
            var guidBytes = Encoding.UTF8.GetBytes(member.PlayerGuid);
            var presenceOffset = 8 + 4 + 2 + guidBytes.Length + 8;
            root.Snapshot[presenceOffset] = (byte)presence;
            var crc = typeof(DurableInboxSaveCodec).GetMethod("ComputeRootCrc",
                BindingFlags.Static | BindingFlags.NonPublic);
            root.Crc = (uint)crc.Invoke(null, new object[] { root });
        }
    }
}
