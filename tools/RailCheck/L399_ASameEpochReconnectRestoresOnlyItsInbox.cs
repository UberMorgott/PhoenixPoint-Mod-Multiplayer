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
            var own = new OccurrenceId("event", "z-created-first", new[] { "subject" });
            host.CreateOccurrence(own);
            host.ApplyLifecycle(member, own, InboxLifecycle.Open, 2);
            host.SetPresence(member, MemberPresence.Disconnected);
            host.Enroll(other, MemberPresence.Active);
            var shared = new OccurrenceId("event", "a-created-second", new[] { "subject" });
            host.CreateOccurrence(shared);
            var first = host.Reconnect(member);
            var second = host.Reconnect(member);
            var expectedOrder = new[] { own, shared };
            if (first.Count != 2 || second.Count != 2 || first[0].Lifecycle != InboxLifecycle.Open ||
                !first.Select(e => e.Occurrence).SequenceEqual(expectedOrder) ||
                !second.Select(e => e.Occurrence).SequenceEqual(expectedOrder))
                yield return "L399 same-epoch-reconnect-did-not-idempotently-restore-lifecycle";
            if (host.Reconnect(new MembershipId("player", 5)).Any())
                yield return "L399 unknown-new-epoch-was-granted-history";

            var revision = host.CommittedRevision;
            var rebuilt = new HostInboxSequencer(host.Ledger);
            if (rebuilt.CommittedRevision != revision ||
                !rebuilt.Reconnect(member).Select(entry => entry.Occurrence).SequenceEqual(expectedOrder))
                yield return "L399 reconstruction-lost-membership-revision-or-host-order";
            var next = new OccurrenceId("event", "after-reconstruction", new[] { "subject" });
            if (!rebuilt.CreateOccurrence(next) || rebuilt.CommittedRevision != revision + 1 ||
                !rebuilt.Reconnect(member).Last().Occurrence.Equals(next))
                yield return "L399 reconstructed-authority-did-not-continue";
            if (!host.Ledger.EncodeCanonical().SequenceEqual(
                new HostLedger(host.Ledger.AllEntries.Reverse(), host.Ledger.CommittedRevision,
                    host.Ledger.Members).EncodeCanonical()))
                yield return "L399 canonical-ledger-changed-with-incidental-list-order";

            // POSITIVE CONTROL: reconnect must not mint or duplicate an entry, and identity order opposes host order.
            if (host.Ledger.EntryCount != 3 || own.CompareTo(shared) <= 0)
                yield return "L399 control-not-red: fixture-cannot-detect-reconnect-mutation-or-order-loss";
        }
    }

}
