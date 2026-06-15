namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// State channel #2 — Phoenix faction <c>Research</c> (fixes host cancel/switch not reaching the
    /// frozen client). Host snapshots completed research ids + the ordered (id, progress) queue on the
    /// faction research start/complete events; client reconciles its research to match exactly
    /// (idempotent, version-gated). Mirrors <see cref="InventoryChannel"/>. The wire codec +
    /// <see cref="ResearchSnapshot"/> live in their own pure file for unit testability.
    /// </summary>
    public sealed class ResearchChannel : IStateChannel
    {
        public byte ChannelId => 2;

        // ─── host subscription state (mirrors WalletWatcher / InventoryChannel) ───
        private object _token;       // opaque faction-event token from ResearchStateReflection
        private object _research;    // the bound Research instance (rebind guard)
        private bool _bound;

        public byte[] Snapshot(GeoRuntime rt)
        {
            var snap = ResearchStateReflection.Snapshot(rt);
            if (snap == null) return null;
            return ResearchSnapshot.Encode(snap);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = ResearchSnapshot.Decode(data);
            if (snap == null) return;
            ResearchStateReflection.Apply(rt, snap);
        }

        public void AttachHost(SyncEngine eng)
        {
            if (_bound) return;                                  // bound; skip the per-frame reflection
            if (eng == null) return;
            var research = ResearchStateReflection.GetResearch(GeoRuntime.Instance);
            if (research == null) return;                        // not in geoscape yet / mid-load
            if (ReferenceEquals(research, _research)) return;    // already bound to this instance

            DetachHost();                                        // drop any stale binding
            _research = research;
            byte id = ChannelId;
            _token = ResearchStateReflection.SubscribeFactionResearchEvents(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            // Seed clients with the authoritative research the moment we bind.
            eng.MarkChannelDirty(id);
            _bound = true;
        }

        public void DetachHost()
        {
            if (_token != null) ResearchStateReflection.Unsubscribe(_token);
            _token = null;
            _research = null;
            _bound = false;
        }
    }
}
