using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L398 / DWI-23 — enrollment never scans or grants the existing backlog.</summary>
    internal static class L398_ALateEnrollmentReceivesNoHistory
    {
        internal static IEnumerable<string> Check()
        {
            var early = new MembershipId("early", 1);
            var late = new MembershipId("late", 1);
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            host.Enroll(early, MemberPresence.Active);
            for (var i = 0; i < 4; i++) host.CreateOccurrence(new OccurrenceId("event-" + i, "trigger-" + i, new[] { "subject" }));
            host.Enroll(late, MemberPresence.Active);
            if (host.Reconnect(late).Any()) yield return "L398 enrollment-scanned-preexisting-backlog";
            var next = new OccurrenceId("event-next", "trigger-next", new[] { "subject" });
            host.CreateOccurrence(next);
            if (host.Reconnect(late).Count != 1 || !host.Reconnect(late)[0].Occurrence.Equals(next))
                yield return "L398 next-creation-did-not-include-late-epoch";

            // POSITIVE CONTROL: the early epoch proves the backlog is nonempty and observable.
            if (host.Reconnect(early).Count != 5) yield return "L398 control-not-red: backlog premise was not populated";
        }
    }
}
