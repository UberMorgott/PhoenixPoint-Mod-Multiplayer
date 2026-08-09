using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L376 / DWI-01 — creation snapshots every committed membership epoch.</summary>
    internal static class L376_ACreationEntitlesEveryCommittedEpoch
    {
        internal static IEnumerable<string> Check()
        {
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            var members = new[]
            {
                new MembershipId("active-player", 1), new MembershipId("disconnected-player", 2),
                new MembershipId("loading-player", 3), new MembershipId("tactical-player", 4),
                new MembershipId("non-geoscape-player", 5)
            };
            var presence = new[] { MemberPresence.Active, MemberPresence.Disconnected, MemberPresence.Loading,
                MemberPresence.Tactical, MemberPresence.NonGeoscape };
            for (var i = 0; i < members.Length; i++) host.Enroll(members[i], presence[i]);

            var first = new OccurrenceId("event", "trigger-1", new[] { "subject" });
            if (!host.CreateOccurrence(first) || members.Any(m => host.Reconnect(m).Single().Occurrence.CompareTo(first) != 0))
                yield return "L376 committed-creation-set-missing: disconnected/loading/tactical/non-Geoscape epochs must retain entitlement";
            if (host.CreateOccurrence(first) || members.Any(m => host.Reconnect(m).Count != 1))
                yield return "L376 retransmit-created-duplicate";

            var late = new MembershipId("late-player", 6);
            host.Enroll(late, MemberPresence.Active);
            var second = new OccurrenceId("event", "trigger-2", new[] { "subject" });
            host.CreateOccurrence(second);
            if (host.Reconnect(late).Any(e => e.Occurrence.Equals(first)) || host.Reconnect(late).Count != 1 ||
                members.Any(m => host.Reconnect(m).Count != 2))
                yield return "L376 late-epoch-received-history-or-new-trigger-collapsed";

            // POSITIVE CONTROL: an event-id-only dedupe would collapse trigger-2 and fail the two-entry arm above.
            if (first.Equals(second)) yield return "L376 control-not-red: full occurrence identity did not distinguish a new trigger";
        }
    }
}
