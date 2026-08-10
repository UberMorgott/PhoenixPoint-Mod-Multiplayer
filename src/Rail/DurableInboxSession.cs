using System;

namespace Multiplayer.Network.Sync
{
    /// <summary>Holds the one live durable inbox store.  The store is SESSION state, not save state: the
    /// native game already persists the window queue and <see cref="WindowQueueSync"/> rides that restore,
    /// so this ledger is created empty when a co-op geoscape starts and dropped at level teardown.</summary>
    internal static class DurableInboxSession
    {
        private static readonly object Gate = new object();
        private static DurableInboxStore _activeStore;
        internal static DurableInboxStore ActiveStore
        {
            get { lock (Gate) return _activeStore; }
            set
            {
                bool changed; DurableInboxStore old;
                lock (Gate) { old = _activeStore; changed = !ReferenceEquals(old, value); _activeStore = value; }
                if (changed) { MissionSync.ClearScheduledSourceRevalidationDeltas();
                    WindowQueueSync.ClearDurableRuntimeCarriers(); old?.Carriers.AbandonStore(); }
            }
        }

        /// <summary>Called from the game's own "the geoscape is built" callback.  Outside an active co-op
        /// session there is nothing to reconcile, so no store is minted.</summary>
        internal static void OpenSessionStore()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            ActiveStore = new DurableInboxStore(new HostLedger(Array.Empty<InboxEntry>()));
        }
    }
}
