using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L400 / DWI-25 — one host sequencer fixes create-vs-enroll order.</summary>
    internal static class L400_EnrollmentAndCreationShareOneOrder
    {
        internal static IEnumerable<string> Check()
        {
            var member = new MembershipId("player", 3);
            var before = new OccurrenceId("event", "create-before-enroll", new[] { "subject" });
            var after = new OccurrenceId("event", "enroll-before-create", new[] { "subject" });
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));

            if (!host.CreateOccurrence(before) || !host.Enroll(member, MemberPresence.Active) ||
                !host.CreateOccurrence(after))
                yield return "L400 ordered-authority-operations-were-rejected";
            var restored = host.Reconnect(member);
            if (restored.Count != 1 || !restored[0].Occurrence.Equals(after))
                yield return "L400 enrollment-boundary-did-not-exclude-before-and-include-after";
            if (host.CommittedRevision != 3)
                yield return "L400 ordered-authority-revision-cardinality-wrong";

            var createFirst = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            createFirst.CreateOccurrence(before);
            createFirst.Enroll(member, MemberPresence.Active);
            if (createFirst.Reconnect(member).Any())
                yield return "L400 create-before-enroll-granted-history";

            var enrollFirst = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            enrollFirst.Enroll(member, MemberPresence.Active);
            enrollFirst.CreateOccurrence(after);
            if (enrollFirst.Reconnect(member).Count != 1)
                yield return "L400 enroll-before-create-lost-entitlement";

            // POSITIVE CONTROL: retries must preserve both the enrollment presence and committed revision.
            var revision = host.CommittedRevision;
            if (host.Enroll(member, MemberPresence.Disconnected) || host.CreateOccurrence(after) ||
                host.CommittedRevision != revision || host.Reconnect(member)[0].Occurrence.Equals(before))
                yield return "L400 authority-retry-was-not-idempotent";

            const int occurrenceCount = 32;
            var concurrent = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            var concurrentMember = new MembershipId("concurrent-player", 1);
            var gate = new ManualResetEventSlim(false);
            var exceptions = new ConcurrentQueue<Exception>();
            var enrollmentWorker = Task.Run(() =>
            {
                try { gate.Wait(); concurrent.Enroll(concurrentMember, MemberPresence.Active); }
                catch (Exception ex) { exceptions.Enqueue(ex); }
            });
            var creationWorker = Task.Run(() =>
            {
                try
                {
                    gate.Wait();
                    for (var i = 0; i < occurrenceCount; i++)
                    {
                        var occurrence = new OccurrenceId("event-" + i, "trigger-" + i, new[] { "subject" });
                        concurrent.CreateOccurrence(occurrence);
                        concurrent.CreateOccurrence(occurrence);
                    }
                }
                catch (Exception ex) { exceptions.Enqueue(ex); }
            });
            gate.Set();
            Task.WaitAll(enrollmentWorker, creationWorker);

            var all = concurrent.Ledger.AllEntries;
            if (!exceptions.IsEmpty)
                yield return "L400 bounded-concurrency-threw";
            if (concurrent.CommittedRevision != occurrenceCount + 1UL)
                yield return "L400 bounded-concurrency-revision-cardinality-wrong";
            if (all.Select(e => e.Occurrence).Distinct().Count() != all.Count ||
                all.Any(e => e.HostOrderKey.TriggerId != e.Occurrence.TriggerId) ||
                all.Select(e => e.HostOrderKey).Distinct().Count() != all.Count ||
                all.Any(e => e.HostOrderKey.CampaignOrdinal < 1 ||
                             e.HostOrderKey.CampaignOrdinal > concurrent.CommittedRevision))
                yield return "L400 bounded-concurrency-produced-invalid-identity-or-order";

            var afterContention = new OccurrenceId("event-after", "trigger-after", new[] { "subject" });
            var beforeContentionRevision = concurrent.CommittedRevision;
            if (!concurrent.CreateOccurrence(afterContention) ||
                concurrent.CommittedRevision != beforeContentionRevision + 1)
                yield return "L400 post-contention-create-did-not-advance-exactly-once";
            var post = concurrent.Reconnect(concurrentMember)
                .SingleOrDefault(e => e.Occurrence.Equals(afterContention));
            if (post == null || post.HostOrderKey.CampaignOrdinal != concurrent.CommittedRevision ||
                post.HostOrderKey.TriggerId != afterContention.TriggerId)
                yield return "L400 post-contention-create-not-restored-with-full-key";
        }
    }
}
