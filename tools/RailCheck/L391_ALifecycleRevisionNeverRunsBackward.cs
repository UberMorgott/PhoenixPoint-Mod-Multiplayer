using System.Collections.Generic;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L391 / DWI-16 — same-epoch reconnect preserves monotonic terminal lifecycle.</summary>
    internal static class L391_ALifecycleRevisionNeverRunsBackward
    {
        internal static IEnumerable<string> Check()
        {
            var member = new MembershipId("player", 7);
            var occurrence = new OccurrenceId("event", "trigger", new[] { "subject" });
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            host.Enroll(member, MemberPresence.Active);
            host.CreateOccurrence(occurrence);
            if (!host.ApplyLifecycle(member, occurrence, InboxLifecycle.Open, 2) ||
                !host.ApplyLifecycle(member, occurrence, InboxLifecycle.Dismissed, 3))
                yield return "L391 valid-monotonic-lifecycle-refused";
            if (host.ApplyLifecycle(member, occurrence, InboxLifecycle.Open, 4) ||
                host.ApplyLifecycle(member, occurrence, InboxLifecycle.Dismissed, 2))
                yield return "L391 stale-or-terminal-regression-accepted";
            if (!host.Tombstone(occurrence, 5) || host.ApplyLifecycle(member, occurrence, InboxLifecycle.Dismissed, 6) ||
                host.Reconnect(member)[0].Lifecycle != InboxLifecycle.Removed)
                yield return "L391 tombstone-did-not-beat-peer-update";
            if (host.ApplyLifecycle(new MembershipId("player", 6), occurrence, InboxLifecycle.Open, 99))
                yield return "L391 stale-membership-epoch-accepted";

            // POSITIVE CONTROL: a newer valid revision must be observable before the terminal mutation.
            var control = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            control.Enroll(member, MemberPresence.Active); control.CreateOccurrence(occurrence);
            if (!control.ApplyLifecycle(member, occurrence, InboxLifecycle.Open, 2))
                yield return "L391 control-not-red: revision gate rejected its valid mutation";
        }
    }
}
