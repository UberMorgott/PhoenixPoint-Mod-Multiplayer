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
        /// session there is nothing to reconcile, so no store is minted.
        ///
        /// THE MEMBERSHIP BOOTSTRAP IS LOAD-BEARING, and its absence made every durable window eat every
        /// click. <c>HostLedger</c> DERIVES <c>Members</c> from its own entries when none is supplied
        /// (DurableInboxModel.cs:485) and <c>HostInboxSequencer.CreateOccurrence</c> mints one entry PER
        /// MEMBER (:534) — so a ledger opened empty had no member, therefore created no entry, therefore
        /// still had no member: a bootstrap that can never start. Every entitlement lookup then failed
        /// closed for the whole session on EVERY peer, host included —
        /// <c>WindowQueueSync.TryLocalMember</c>:315, <c>EventPopup</c>'s click path :1838 ("answer dropped
        /// — no active local entitlement") and <c>EventSync</c>:378 ("host-local answer blocked") — which is
        /// a mission-start event window that swallows eleven consecutive clicks (live 2026-08-10, both
        /// clients and the host). <c>HostInboxSequencer.Enroll</c> was deleted in da02dce as having "zero
        /// production callers"; that was true and was itself the defect.
        ///
        /// THIS PEER'S OWN GUID IS THE WHOLE MEMBER SET, deliberately. The store is per-peer SESSION state
        /// that no rail surface carries (bc9b404) — every peer runs its own copy and reads only its own
        /// entitlement — so a row for anybody else would be a ghost nothing ever looks up.</summary>
        internal static void OpenSessionStore()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            ActiveStore = new DurableInboxStore(new HostLedger(Array.Empty<InboxEntry>(), 0,
                new[] { new MembershipId(ClientIdentity.PlayerGuid.ToString("D")) }));
        }
    }
}
