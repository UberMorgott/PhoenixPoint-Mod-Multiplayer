using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L400 / DWI-25 — one host sequencer fixes create-vs-enroll order.</summary>
    internal static class L400_EnrollmentAndCreationShareOneOrder
    {
        internal static IEnumerable<string> Check()
        {
            const int occurrenceCount = 256;
            var member = new MembershipId("player", 3);
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            var start = new System.Threading.ManualResetEventSlim(false);
            var failures = new System.Collections.Concurrent.ConcurrentQueue<System.Exception>();
            var enrolls = Enumerable.Range(0, occurrenceCount).Select(_ => System.Threading.Tasks.Task.Run(() =>
            {
                start.Wait();
                try { host.Enroll(member, MemberPresence.Active); }
                catch (System.Exception ex) { failures.Enqueue(ex); }
            })).ToArray();
            var creates = Enumerable.Range(0, occurrenceCount).Select(i => System.Threading.Tasks.Task.Run(() =>
            {
                start.Wait();
                var occurrence = new OccurrenceId("event", "trigger-" + i.ToString("D3"), new[] { "subject" });
                try
                {
                    host.CreateOccurrence(occurrence);
                    host.CreateOccurrence(occurrence); // concurrent retry must be idempotent
                }
                catch (System.Exception ex) { failures.Enqueue(ex); }
            })).ToArray();
            start.Set();
            System.Threading.Tasks.Task.WaitAll(enrolls.Concat(creates).ToArray());

            if (!failures.IsEmpty) yield return "L400 concurrent-authority-operation-threw";
            if (host.CommittedRevision != occurrenceCount + 1UL)
                yield return "L400 concurrent-retry-lost-or-duplicated-committed-order";
            var restored = host.Reconnect(member);
            if (restored.Select(e => e.Occurrence).Distinct().Count() != restored.Count)
                yield return "L400 concurrent-retry-duplicated-entitlement";

            // POSITIVE CONTROL: after the concurrent transaction batch, the next create is exactly one later commit.
            var revision = host.CommittedRevision;
            var after = new OccurrenceId("event", "after-concurrency", new[] { "subject" });
            if (!host.CreateOccurrence(after) || host.CommittedRevision != revision + 1 ||
                !host.Reconnect(member).Any(e => e.Occurrence.Equals(after)))
                yield return "L400 control-not-red: sequencer-did-not-remain-usable";
        }
    }
}
