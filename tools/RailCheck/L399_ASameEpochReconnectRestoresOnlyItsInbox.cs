using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L399 / DWI-24 — same-epoch reconnect is restore, not late enrollment.</summary>
    internal static class L399_ASameEpochReconnectRestoresOnlyItsInbox
    {
        internal static IEnumerable<string> Check()
        {
            var member = new MembershipId("player", 4);
            var other = new MembershipId("other", 2);
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            host.Enroll(member, MemberPresence.Active);
            var own = new OccurrenceId("own", "trigger-own", new[] { "subject" });
            host.CreateOccurrence(own);
            host.ApplyLifecycle(member, own, InboxLifecycle.Open, 2);
            host.SetPresence(member, MemberPresence.Disconnected);
            host.Enroll(other, MemberPresence.Active);
            var shared = new OccurrenceId("shared", "trigger-shared", new[] { "subject" });
            host.CreateOccurrence(shared);
            var first = host.Reconnect(member);
            var second = host.Reconnect(member);
            if (first.Count != 2 || second.Count != 2 || first[0].Lifecycle != InboxLifecycle.Open ||
                first.Select(e => e.Occurrence).Except(second.Select(e => e.Occurrence)).Any())
                yield return "L399 same-epoch-reconnect-did-not-idempotently-restore-lifecycle";
            if (host.Reconnect(new MembershipId("player", 5)).Any())
                yield return "L399 unknown-new-epoch-was-granted-history";

            // POSITIVE CONTROL: reconnect must not mint or duplicate an entry.
            if (host.Ledger.EntryCount != 3) yield return "L399 control-not-red: reconnect changed committed ledger cardinality";
        }
    }
}
