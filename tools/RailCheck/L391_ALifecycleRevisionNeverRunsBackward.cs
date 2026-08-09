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
            var member = new MembershipId("player", 7);
            var occurrence = new OccurrenceId("event", "trigger", new[] { "subject" });
            var tombstoned = new OccurrenceId("event", "tombstoned-before-save", new[] { "subject" });
            var host = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            host.Enroll(member, MemberPresence.Active);
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

            if (!afterLoad.EndMembership(member))
                yield return "L391 active-epoch-end-refused";
            var endedRoot = new DurableInboxStore(afterLoad.Ledger).CreateSaveRoot();
            DurableInboxRestore endedRestore;
            if (!DurableInboxSaveCodec.TryRestore(endedRoot, null, out endedRestore, out refusal))
                yield return "L391 ended-epoch-save-root-refused: " + refusal;
            else
            {
                var endedAfterLoad = new HostInboxSequencer(
                    new DurableInboxStore(endedRestore.Ledger, endedRestore.Canonical, endedRestore.Journal,
                        endedRestore.SnapshotLedger, endedRestore.SnapshotCanonical).Ledger);
                if (endedAfterLoad.Reconnect(member).Any() ||
                    endedAfterLoad.ApplyLifecycle(member, occurrence, InboxLifecycle.Open, 99))
                    yield return "L391 ended-epoch-revision-accepted-after-save-load";
            }

            // POSITIVE CONTROL: a newer valid revision must be observable before save/load and terminal mutation.
            var control = new HostInboxSequencer(new HostLedger(new InboxEntry[0]));
            control.Enroll(member, MemberPresence.Active); control.CreateOccurrence(occurrence);
            if (!control.ApplyLifecycle(member, occurrence, InboxLifecycle.Open, 2))
                yield return "L391 control-not-red: revision gate rejected its valid mutation";
        }
    }
}
