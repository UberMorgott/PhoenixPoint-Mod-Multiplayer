using System;

namespace Multiplayer.Network.Sync
{
    internal static class DurableInboxReducer
    {
        internal static HostLedger CloneAndValidate(HostLedger ledger)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            var entries = new InboxEntry[ledger.AllEntries.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = ledger.AllEntries[i];
                entries[i] = new InboxEntry(entry.Occurrence, entry.Membership, entry.Lifecycle, entry.Choice,
                    entry.LifecycleRevision, entry.TombstoneRevision, entry.HostOrderKey,
                    entry.SuspensionReason, entry.Checkpoint, entry.TerminalReason,
                    entry.SupersededBy, entry.Predecessor);
            }
            return new HostLedger(entries, ledger.CommittedRevision, ledger.Members);
        }

        internal static ReduceResult Apply(HostLedger ledger, InboxCommand command)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.Kind == InboxCommandKind.TransportAck) return new ReduceResult(ledger, false);

            InboxEntry current;
            try { current = ledger.Get(command.Occurrence, command.Membership); }
            catch (InvalidOperationException) { return new ReduceResult(ledger, false); }
            if (command.Revision <= current.LifecycleRevision || current.Lifecycle == InboxLifecycle.Removed ||
                !Enum.IsDefined(typeof(InboxLifecycle), command.Lifecycle) || command.Lifecycle == InboxLifecycle.Removed)
                return new ReduceResult(ledger, false);

            bool sameLifecycle = current.Lifecycle == command.Lifecycle;
            bool validTransition = current.Lifecycle == InboxLifecycle.Queued && command.Lifecycle == InboxLifecycle.Open ||
                                   current.Lifecycle == InboxLifecycle.Open &&
                                       (command.Lifecycle == InboxLifecycle.Suspended || command.Lifecycle == InboxLifecycle.Read || command.Lifecycle == InboxLifecycle.Dismissed) ||
                                   current.Lifecycle == InboxLifecycle.Suspended && command.Lifecycle == InboxLifecycle.Open ||
                                   current.Lifecycle == InboxLifecycle.Read && command.Lifecycle == InboxLifecycle.Dismissed;
            if (!sameLifecycle && !validTransition) return new ReduceResult(ledger, false);

            var replacement = command.Lifecycle == InboxLifecycle.Suspended
                ? current.Suspend(command.SuspensionReason == InboxSuspensionReason.None ? current.SuspensionReason : command.SuspensionReason,
                    command.Checkpoint ?? current.Checkpoint, command.Revision)
                : current.WithLifecycle(command.Lifecycle, command.Revision);
            return new ReduceResult(ledger.Replace(replacement), true);
        }
    }
}
