using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Network.Sync
{
    internal interface IDurableWindowCarrierAdapter
    {
        InboxWindowCheckpoint Capture(OccurrenceId occurrence);
        bool Present(OccurrenceId occurrence);
        bool Restore(OccurrenceId occurrence, InboxWindowCheckpoint checkpoint);
        void Abandon(OccurrenceId occurrence);
        void FinalizeRestore(OccurrenceId occurrence);
    }

    /// <summary>
    /// Per-player durable scheduler.  The native request is only a carrier: lifecycle and the captured
    /// read position are committed before a priority carrier is allowed to replace an ordinary one.
    /// No method consults another player's readiness, so an AFK peer cannot veto progress.
    /// </summary>
    internal sealed class DurableInboxEngine
    {
        private readonly DurableInboxStore _store;
        private readonly MembershipId _member;
        private readonly IDurableWindowCarrierAdapter _carrier;

        internal DurableInboxEngine(DurableInboxStore store, MembershipId member,
            IDurableWindowCarrierAdapter carrier)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _member = member;
            _carrier = carrier ?? throw new ArgumentNullException(nameof(carrier));
        }

        internal bool TryPresentNext(bool geoscapeStarted, Type currentViewState)
        {
            if (!DurableWindowRegistry.MayPresent(geoscapeStarted, currentViewState)) return false;
            var entries = _store.Ledger.EntriesFor(_member);
            if (entries.Count(x => x.Lifecycle == InboxLifecycle.Open) > 1) return false;
            var next = entries.Where(x => x.Lifecycle == InboxLifecycle.Queued)
                .OrderByDescending(x => DurableWindowRegistry.PriorityOf(x.Occurrence)).ThenBy(x => x.HostOrderKey).FirstOrDefault();
            if (next == null) return TryResumeSuspended(geoscapeStarted, currentViewState);
            var open = entries.FirstOrDefault(x => x.Lifecycle == InboxLifecycle.Open);
            if (open != null)
                return DurableWindowRegistry.PriorityOf(next.Occurrence) > DurableWindowRegistry.PriorityOf(open.Occurrence) &&
                    TryPreempt(open.Occurrence, next.Occurrence, geoscapeStarted, currentViewState);
            return CommitOpenAndPresent(next);
        }

        internal bool ConfirmNativePresented(OccurrenceId occurrence)
        {
            var expected = _store.Ledger; InboxEntry entry;
            try { entry = expected.Get(occurrence, _member); } catch (InvalidOperationException) { return false; }
            if (entry.Lifecycle == InboxLifecycle.Open) return true;
            if (entry.Lifecycle != InboxLifecycle.Queued) return false;
            var next = expected.Replace(entry.WithLifecycle(InboxLifecycle.Open, checked(entry.LifecycleRevision + 1)))
                .WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
            try { return _store.Commit(expected, next); } catch { return false; }
        }

        internal bool TryPreempt(OccurrenceId ordinary, OccurrenceId priority,
            bool geoscapeStarted, Type currentViewState)
        {
            if (!DurableWindowRegistry.MayPresent(geoscapeStarted, currentViewState) ||
                DurableWindowRegistry.PriorityOf(ordinary) != DurableWindowPriority.Ordinary ||
                DurableWindowRegistry.PriorityOf(priority) == DurableWindowPriority.Ordinary) return false;
            var expected = _store.Ledger;
            InboxEntry ordinaryEntry, priorityEntry;
            try { ordinaryEntry = expected.Get(ordinary, _member); priorityEntry = expected.Get(priority, _member); }
            catch (InvalidOperationException) { return false; }
            if (ordinaryEntry.Lifecycle != InboxLifecycle.Open || priorityEntry.Lifecycle != InboxLifecycle.Queued)
                return false;
            InboxWindowCheckpoint checkpoint;
            try { checkpoint = _carrier.Capture(ordinary); } catch { checkpoint = null; }
            if (checkpoint == null) return false; // capture failure: ordinary remains open, priority remains queued
            ulong revision = checked(expected.CommittedRevision + 1);
            var suspended = ordinaryEntry.Suspend(InboxSuspensionReason.PriorityPreemption, checkpoint,
                checked(ordinaryEntry.LifecycleRevision + 1));
            var next = expected.Replace(suspended).WithAuthority(revision, expected.Members);
            if (!_store.Commit(expected, next)) return false;
            bool presented;
            try { presented = _carrier.Present(priority); } catch { presented = false; }
            if (!presented)
            {
                try { _carrier.Abandon(priority); } catch { }
                RestoreAfterFailedPresent(ordinary, checkpoint); return false;
            }
            var presentedLedger = _store.Ledger; InboxEntry stillQueued, stillSuspended;
            try
            {
                stillQueued = presentedLedger.Get(priority, _member);
                stillSuspended = presentedLedger.Get(ordinary, _member);
            }
            catch
            {
                try { _carrier.Abandon(priority); } catch { }
                return false;
            }
            if (stillQueued.Lifecycle != InboxLifecycle.Queued ||
                stillSuspended.Lifecycle != InboxLifecycle.Suspended ||
                stillSuspended.SuspensionReason != InboxSuspensionReason.PriorityPreemption ||
                !checkpoint.Equals(stillSuspended.Checkpoint))
            {
                try { _carrier.Abandon(priority); } catch { }
                if (stillSuspended.Lifecycle == InboxLifecycle.Suspended && checkpoint.Equals(stillSuspended.Checkpoint))
                    RestoreAfterFailedPresent(ordinary, checkpoint);
                return false;
            }
            bool openedCommitted = false;
            try
            {
                var opened = stillQueued.WithLifecycle(InboxLifecycle.Open, checked(stillQueued.LifecycleRevision + 1));
                var committed = presentedLedger.Replace(opened)
                    .WithAuthority(checked(presentedLedger.CommittedRevision + 1), presentedLedger.Members);
                openedCommitted = _store.Commit(presentedLedger, committed);
            }
            catch { openedCommitted = false; }
            if (openedCommitted) return true;
            try { _carrier.Abandon(priority); } catch { }
            RestoreAfterFailedPresent(ordinary, checkpoint);
            return false;
        }

        internal bool TryResumeSuspended(bool geoscapeStarted, Type currentViewState)
        {
            if (!DurableWindowRegistry.MayPresent(geoscapeStarted, currentViewState)) return false;
            var expected = _store.Ledger;
            var entries = expected.EntriesFor(_member);
            if (entries.Any(x => DurableWindowRegistry.PriorityOf(x.Occurrence) != DurableWindowPriority.Ordinary &&
                (x.Lifecycle == InboxLifecycle.Queued || x.Lifecycle == InboxLifecycle.Open))) return false;
            if (entries.Any(x => x.Lifecycle == InboxLifecycle.Open)) return false;
            var suspended = entries.Where(x => x.Lifecycle == InboxLifecycle.Suspended &&
                x.SuspensionReason == InboxSuspensionReason.PriorityPreemption)
                .OrderBy(x => x.HostOrderKey).FirstOrDefault();
            if (suspended == null) return false;
            if (!_store.IsServable(suspended.Occurrence)) return RemoveInvalidatedSuspended(suspended);
            var checkpoint = suspended.Checkpoint;
            ulong revision = checked(expected.CommittedRevision + 1);
            bool restored;
            try { restored = _carrier.Restore(suspended.Occurrence, checkpoint); } catch { restored = false; }
            if (!restored) return false;
            var next = expected.Replace(suspended.WithLifecycle(InboxLifecycle.Open,
                checked(suspended.LifecycleRevision + 1))).WithAuthority(revision, expected.Members);
            bool committed;
            try { committed = _store.Commit(expected, next); } catch { committed = false; }
            if (!committed)
            {
                try { _carrier.Abandon(suspended.Occurrence); } catch { }
                return false;
            }
            try { _carrier.FinalizeRestore(suspended.Occurrence); } catch { }
            return true;
        }

        private bool CommitOpenAndPresent(InboxEntry entry)
        {
            var expected = _store.Ledger;
            InboxEntry current;
            try { current = expected.Get(entry.Occurrence, _member); }
            catch (InvalidOperationException) { return false; }
            if (current.Lifecycle != InboxLifecycle.Queued) return false;
            ulong revision = checked(expected.CommittedRevision + 1);
            bool presented;
            try { presented = _carrier.Present(entry.Occurrence); } catch { presented = false; }
            if (!presented) return false;
            var next = expected.Replace(current.WithLifecycle(InboxLifecycle.Open,
                checked(current.LifecycleRevision + 1))).WithAuthority(revision, expected.Members);
            bool committed;
            try { committed = _store.Commit(expected, next); } catch { committed = false; }
            if (!committed) { try { _carrier.Abandon(entry.Occurrence); } catch { } }
            return committed;
        }

        private bool RemoveInvalidatedSuspended(InboxEntry suspended)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var expected = _store.Ledger; InboxEntry current;
                try { current = expected.Get(suspended.Occurrence, _member); } catch (InvalidOperationException) { return false; }
                if (current.Lifecycle == InboxLifecycle.Removed || current.Lifecycle == InboxLifecycle.Dismissed) return false;
                if (current.Lifecycle != InboxLifecycle.Suspended || current.LifecycleRevision < suspended.LifecycleRevision) return false;
                ulong lifecycle = checked(current.LifecycleRevision + 1);
                var removed = new InboxEntry(current.Occurrence, current.Membership, InboxLifecycle.Removed,
                    current.Choice, lifecycle, Math.Max(current.TombstoneRevision, lifecycle), current.HostOrderKey);
                var next = expected.Replace(removed).WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
                try { if (_store.Commit(expected, next)) return false; } catch { }
            }
            return false;
        }

        private void RestoreAfterFailedPresent(OccurrenceId occurrence, InboxWindowCheckpoint checkpoint)
        {
            bool restored;
            try { restored = _carrier.Restore(occurrence, checkpoint); } catch { restored = false; }
            if (!restored) return;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var expected = _store.Ledger; InboxEntry current;
                try { current = expected.Get(occurrence, _member); } catch { break; }
                if (current.Lifecycle != InboxLifecycle.Suspended || !current.Checkpoint.Equals(checkpoint)) break;
                var open = current.WithLifecycle(InboxLifecycle.Open, checked(current.LifecycleRevision + 1));
                var next = expected.Replace(open).WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
                bool committed; try { committed = _store.Commit(expected, next); } catch { committed = false; }
                if (!committed) continue;
                try { _carrier.FinalizeRestore(occurrence); } catch { }
                return;
            }
            try { _carrier.Abandon(occurrence); } catch { }
        }

    }
}
