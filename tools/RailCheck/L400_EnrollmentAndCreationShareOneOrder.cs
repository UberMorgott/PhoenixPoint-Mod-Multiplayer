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
        }
    }
}
