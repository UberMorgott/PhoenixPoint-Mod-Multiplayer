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
            var member = new MembershipId("player", 3);
            var before = new OccurrenceId("event", "before-enroll", new[] { "subject" });
            var after = new OccurrenceId("event", "after-enroll", new[] { "subject" });
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            host.CreateOccurrence(before);
            host.Enroll(member, MemberPresence.Active);
            host.Enroll(member, MemberPresence.Disconnected); // retry updates presence, not durable order or epoch
            host.CreateOccurrence(after);
            host.CreateOccurrence(after); // retry dedupes
            var restored = host.Reconnect(member);
            if (restored.Count != 1 || !restored.Single().Occurrence.Equals(after))
                yield return "L400 committed-create-enroll-order-not-preserved";
            if (host.CommittedRevision != 3) yield return "L400 retry-mutated-serialized-transaction-order";

            // POSITIVE CONTROL: reversing the first two transactions includes the epoch.
            var reversed = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            reversed.Enroll(member, MemberPresence.Active); reversed.CreateOccurrence(before);
            if (reversed.Reconnect(member).Count != 1) yield return "L400 control-not-red: reversed order was not distinguishable";
        }
    }
}
