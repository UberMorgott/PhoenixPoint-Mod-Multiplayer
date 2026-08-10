using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L391 / DWI-16 — lifecycle revisions remain monotonic across a store rebuild. The ledger is
    /// no longer saved, so "reload" here is a fresh session store built from the same ledger — the arms name
    /// that, not the save/load and membership epochs this law was originally written against.</summary>
    internal static class L391_ALifecycleRevisionNeverRunsBackward
    {
        internal static IEnumerable<string> Check()
        {
            var member = new MembershipId("player");
            var occurrence = new OccurrenceId("event", "trigger", new[] { "subject" });
            var tombstoned = new OccurrenceId("event", "tombstoned-before-save", new[] { "subject" });
            var host = new HostInboxSequencer(Seeded(member));
            host.CreateOccurrence(occurrence);
            host.CreateOccurrence(tombstoned);
            if (!host.ApplyLifecycle(member, occurrence, InboxLifecycle.Open, 2))
                yield return "L391 valid-monotonic-lifecycle-refused";
            if (!host.Tombstone(tombstoned, 4))
                yield return "L391 pre-rebuild-tombstone-refused";

            // The inbox is session state now: "reload" is a fresh store rebuilt from the same ledger.
            var saveStore = new DurableInboxStore(host.Ledger);
            var restoredStore = new DurableInboxStore(saveStore.Ledger, saveStore.Canonical);
            var afterLoad = new HostInboxSequencer(restoredStore.Ledger);
            if (afterLoad.Reconnect(member).Single(entry => entry.Occurrence.Equals(occurrence)).Lifecycle != InboxLifecycle.Open ||
                afterLoad.Reconnect(member).Single(entry => entry.Occurrence.Equals(occurrence)).LifecycleRevision != 2)
                yield return "L391 store-rebuild-lost-the-lifecycle-or-its-revision";
            if (!afterLoad.ApplyLifecycle(member, occurrence, InboxLifecycle.Dismissed, 3))
                yield return "L391 valid-post-rebuild-monotonic-lifecycle-refused";
            if (afterLoad.ApplyLifecycle(member, occurrence, InboxLifecycle.Open, 4) ||
                afterLoad.ApplyLifecycle(member, occurrence, InboxLifecycle.Dismissed, 2))
                yield return "L391 stale-or-terminal-regression-accepted-after-rebuild";
            var restoredTombstone = afterLoad.Reconnect(member).Single(entry => entry.Occurrence.Equals(tombstoned));
            if (restoredTombstone.Lifecycle != InboxLifecycle.Removed || restoredTombstone.TombstoneRevision != 4 ||
                afterLoad.ApplyLifecycle(member, tombstoned, InboxLifecycle.Dismissed, 99))
                yield return "L391 tombstone-did-not-beat-peer-update-after-rebuild";

            // POSITIVE CONTROL: a newer valid revision must be observable before rebuild and terminal mutation.
            var control = new HostInboxSequencer(Seeded(member));
            control.CreateOccurrence(occurrence);
            if (!control.ApplyLifecycle(member, occurrence, InboxLifecycle.Open, 2))
                yield return "L391 control-not-red: revision gate rejected its valid mutation";
        }

        /// <summary>Membership is derived from entries, so a member exists exactly by owning one.</summary>
        private static HostLedger Seeded(MembershipId member)
        {
            var seed = new OccurrenceId("event", "seed", new[] { "subject" });
            return new HostLedger(new[] { new InboxEntry(seed, member, InboxLifecycle.Queued,
                default(CanonicalChoiceId), 1, 0, new HostOrderKey(1, seed.TriggerId)) }, 1);
        }
    }
}
