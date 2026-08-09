using System;

namespace Multiplayer.Network.Sync
{
    internal static class DurableInboxReducer
    {
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
                                       (command.Lifecycle == InboxLifecycle.Read || command.Lifecycle == InboxLifecycle.Dismissed) ||
                                   current.Lifecycle == InboxLifecycle.Read && command.Lifecycle == InboxLifecycle.Dismissed;
            if (!sameLifecycle && !validTransition) return new ReduceResult(ledger, false);

            return new ReduceResult(ledger.Replace(current.WithLifecycle(command.Lifecycle, command.Revision)), true);
        }
    }
}
