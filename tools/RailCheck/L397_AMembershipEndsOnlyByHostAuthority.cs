using System.Collections.Generic;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L397 / DWI-22 — durable membership ends only by authoritative removal.</summary>
    internal static class L397_AMembershipEndsOnlyByHostAuthority
    {
        internal static IEnumerable<string> Check()
        {
            var member = new MembershipId("campaign-player-guid", 9);
            var occurrence = new OccurrenceId("event", "trigger", new[] { "subject" });
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            host.Enroll(member, MemberPresence.Active);
            foreach (var presence in new[] { MemberPresence.Disconnected, MemberPresence.Loading, MemberPresence.Tactical, MemberPresence.NonGeoscape })
                if (!host.SetPresence(member, presence)) yield return "L397 ordinary-presence-change-ended-membership";
            host.CreateOccurrence(occurrence);
            if (host.Reconnect(member).Count != 1) yield return "L397 retained-member-lost-entitlement";
            if (!host.EndMembership(member) || host.Reconnect(member).Count != 0 ||
                host.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Removed)
                yield return "L397 authoritative-removal-did-not-remove-entitlements";

            // POSITIVE CONTROL: no Steam id, roster slot, ACK, readiness, or quorum input exists on the authority methods.
            foreach (var method in typeof(HostInboxSequencer).GetMethods(System.Reflection.BindingFlags.Instance |
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                foreach (var parameter in method.GetParameters())
                    if (parameter.Name.ToLowerInvariant().Contains("steam") || parameter.Name.ToLowerInvariant().Contains("slot") ||
                        parameter.Name.ToLowerInvariant().Contains("ack") || parameter.Name.ToLowerInvariant().Contains("ready") ||
                        parameter.Name.ToLowerInvariant().Contains("quorum"))
                        yield return "L397 forbidden-human-or-connection-identity-input: " + method.Name;
        }
    }
}
