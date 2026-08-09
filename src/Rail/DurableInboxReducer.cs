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

            var current = ledger.Get(command.Occurrence, command.Membership);
            if (command.Revision <= current.LifecycleRevision || current.Lifecycle == command.Lifecycle)
                return new ReduceResult(ledger, false);
            return new ReduceResult(ledger.Replace(current.WithLifecycle(command.Lifecycle, command.Revision)), true);
        }
    }
}
