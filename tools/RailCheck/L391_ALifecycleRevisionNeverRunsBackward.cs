using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L391 / DWI-16 — active-epoch/save-load lifecycle revisions remain monotonic.</summary>
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
                yield return "L391 pre-save-tombstone-refused";

            var saveStore = new DurableInboxStore(host.Ledger);
            var root = saveStore.CreateSaveRoot();
            DurableInboxRestore restored; string refusal;
            if (!DurableInboxSaveCodec.TryRestore(root, null, out restored, out refusal))
            {
                yield return "L391 active-epoch-save-root-refused: " + refusal;
                yield break;
            }
            var restoredStore = new DurableInboxStore(restored.Ledger, restored.Canonical, restored.Journal,
                restored.SnapshotLedger, restored.SnapshotCanonical);
            var afterLoad = new HostInboxSequencer(restoredStore.Ledger);
            if (afterLoad.Reconnect(member).Single(entry => entry.Occurrence.Equals(occurrence)).Lifecycle != InboxLifecycle.Open ||
                afterLoad.Reconnect(member).Single(entry => entry.Occurrence.Equals(occurrence)).LifecycleRevision != 2)
                yield return "L391 save-load-lost-active-epoch-lifecycle";
            if (!afterLoad.ApplyLifecycle(member, occurrence, InboxLifecycle.Dismissed, 3))
                yield return "L391 valid-post-load-monotonic-lifecycle-refused";
            if (afterLoad.ApplyLifecycle(member, occurrence, InboxLifecycle.Open, 4) ||
                afterLoad.ApplyLifecycle(member, occurrence, InboxLifecycle.Dismissed, 2))
                yield return "L391 stale-or-terminal-regression-accepted-after-save-load";
            var restoredTombstone = afterLoad.Reconnect(member).Single(entry => entry.Occurrence.Equals(tombstoned));
            if (restoredTombstone.Lifecycle != InboxLifecycle.Removed || restoredTombstone.TombstoneRevision != 4 ||
                afterLoad.ApplyLifecycle(member, tombstoned, InboxLifecycle.Dismissed, 99))
                yield return "L391 tombstone-did-not-beat-peer-update-after-save-load";

            // POSITIVE CONTROL: a newer valid revision must be observable before save/load and terminal mutation.
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
