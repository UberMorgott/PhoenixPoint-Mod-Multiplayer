namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #3 — research-UNLOCK availability (fixes facility-type / manufacture-item /
    /// augmentation unlocks that a research completion grants HOST-ONLY and which never reach the client).
    /// Host snapshots the three monotonic def-id sets (<c>AvailableFacilities</c>,
    /// <c>Manufacture.ManufacturableItems</c>, <c>UnlockedAugmentations</c>) on every faction
    /// research-complete event; client ADDs any missing unlock (idempotent — these are monotonic unlock
    /// lists, never removed). Mirrors <see cref="ResearchChannel"/>. Codec + <see cref="UnlockSnapshot"/>
    /// live in their own pure file for unit testability; <see cref="UnlockReflection"/> is the bridge.
    ///
    /// Dirty trigger: the same faction <c>ResearchCompletedEventHandler</c> that drives ch2 — the reward
    /// cascade that mutates these sets runs inside research completion. (Unlocks are static between
    /// completions, so no hourly heartbeat is needed; late joiners are seeded by
    /// <c>SyncEngine.BroadcastAllChannels</c> + the bind-time mark below.)
    /// </summary>
    public sealed class UnlockChannel : IStateChannel
    {
        public byte ChannelId => 3;

        private object _token;     // opaque faction research-event token (Start/Complete)
        private object _faction;   // bound faction instance (rebind guard)

        public byte[] Snapshot(GeoRuntime rt)
        {
            var snap = UnlockReflection.Snapshot(rt);
            if (snap == null) return null;
            return UnlockSnapshot.Encode(snap);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = UnlockSnapshot.Decode(data);
            if (snap == null) return;
            UnlockReflection.Apply(rt, snap);
        }

        public void AttachHost(SyncEngine eng)
        {
            // NO hard "already bound" gate — rebind when the LIVE faction instance changes (geoscape reload builds
            // a fresh GeoPhoenixFaction; the old `if (_bound) return;` left this channel on the dead instance
            // forever → unlock sync silently stopped after a tactical round-trip. WalletWatcher lesson).
            if (eng == null) return;
            var fac = GeoRuntime.Instance.PhoenixFaction();
            if (fac == null) return;                          // not in geoscape yet / mid-load
            if (ReferenceEquals(fac, _faction)) return;       // already bound to this instance

            DetachHost();
            _faction = fac;
            byte id = ChannelId;
            // Reuse the research Start/Complete faction-event subscription (DynamicMethod adapter) — the
            // unlock sets only change in the research-complete reward cascade. A redundant mark on Start is
            // harmless (idempotent reconcile).
            _token = ResearchStateReflection.SubscribeFactionResearchEvents(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            // Seed clients with the authoritative unlock set the moment we bind.
            eng.MarkChannelDirty(id);
        }

        public void DetachHost()
        {
            if (_token != null) ResearchStateReflection.Unsubscribe(_token);
            _token = null;
            _faction = null;
        }
    }
}
