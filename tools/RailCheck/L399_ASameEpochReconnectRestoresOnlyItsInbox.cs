using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L399 / DWI-24 — reconnect enrolls a clean epoch and restores no prior inbox.</summary>
    internal static class L399_ASameEpochReconnectRestoresOnlyItsInbox
    {
        internal static IEnumerable<string> Check()
        {
            var oldEpoch = new MembershipId("player", 4);
            var newEpoch = new MembershipId("player", 5);
            var other = new MembershipId("other", 2);
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            host.Enroll(oldEpoch, MemberPresence.Active);
            host.Enroll(other, MemberPresence.Active);
            var own = new OccurrenceId("event", "z-created-first", new[] { "subject" });
            host.CreateOccurrence(own);
            host.ApplyLifecycle(oldEpoch, own, InboxLifecycle.Open, 2);
            if (!host.EndMembership(oldEpoch) || host.Reconnect(oldEpoch).Any())
                yield return "L399 disconnect-did-not-retire-old-epoch";

            var duringDisconnect = new OccurrenceId("event", "a-created-during-disconnect", new[] { "subject" });
            host.CreateOccurrence(duringDisconnect);
            if (!host.Enroll(newEpoch, MemberPresence.Active) || host.Reconnect(newEpoch).Any())
                yield return "L399 reconnect-enrollment-restored-prior-backlog";
            if (host.ApplyLifecycle(oldEpoch, own, InboxLifecycle.Read, 4))
                yield return "L399 stale-prior-epoch-revision-accepted";

            var afterEnrollment = new OccurrenceId("event", "after-new-enrollment", new[] { "subject" });
            var revision = host.CommittedRevision;
            if (!host.CreateOccurrence(afterEnrollment) || host.CommittedRevision != revision + 1)
                yield return "L399 post-enrollment-create-failed";
            var delivered = host.Reconnect(newEpoch);
            if (delivered.Count != 1 || !delivered[0].Occurrence.Equals(afterEnrollment))
                yield return "L399 new-epoch-did-not-receive-only-post-enrollment-occurrence";

            // POSITIVE CONTROL: another continuously enrolled epoch proves both excluded occurrences exist.
            var otherEntries = host.Reconnect(other);
            if (otherEntries.Count != 3 || !otherEntries.Any(entry => entry.Occurrence.Equals(own)) ||
                !otherEntries.Any(entry => entry.Occurrence.Equals(duringDisconnect)))
                yield return "L399 control-not-red: pre-enrollment-backlog-was-not-observable";
        }
    }

}
