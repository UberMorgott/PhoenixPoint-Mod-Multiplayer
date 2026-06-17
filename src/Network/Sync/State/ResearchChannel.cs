namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// State channel #2 — Phoenix faction <c>Research</c> (fixes host cancel/switch not reaching the
    /// frozen client). Host snapshots completed research ids + the ordered (id, progress) queue + the
    /// Revealed/Unlocked Available-list state on the faction research start/complete events AND every
    /// in-game hour (progress heartbeat); client reconciles its research to match exactly (idempotent,
    /// version-gated). Mirrors <see cref="InventoryChannel"/>. The wire codec + <see cref="ResearchSnapshot"/>
    /// live in their own pure file for unit testability.
    ///
    /// FIX#1 (progress heartbeat): the faction start/complete events are DISCRETE — between them the client
    /// progress bar would freeze and only jump to 100% on completion. Host research accrues per in-game hour
    /// (<c>LevelHourlyUpdateCrt</c> → <c>UpdateResearch()</c>), so we ALSO re-mark ch2 dirty on
    /// <c>GeoLevelController.HourTicked</c>, re-sending the queue snapshot (which already carries progress)
    /// every hour. The client bar then tracks the host at its real granularity, and a cancel's last
    /// authoritative progress is never left stale.
    /// </summary>
    public sealed class ResearchChannel : IStateChannel
    {
        public byte ChannelId => 2;

        // ─── host subscription state (mirrors WalletWatcher / InventoryChannel) ───
        private object _token;       // opaque faction-event token (Start/Complete) from ResearchStateReflection
        private object _hourToken;   // opaque hourly-tick (HourTicked) token — FIX#1 progress heartbeat
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
            // FIX#1: hourly progress heartbeat — re-snapshot the queue (with progress) every in-game hour so
            // the client bar tracks the host between the discrete start/complete events. Same dirty-mark sink.
            _hourToken = ResearchStateReflection.SubscribeHourlyTick(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            // Seed clients with the authoritative research the moment we bind.
            eng.MarkChannelDirty(id);
            _bound = true;
        }

        public void DetachHost()
        {
            if (_token != null) ResearchStateReflection.Unsubscribe(_token);
            if (_hourToken != null) ResearchStateReflection.Unsubscribe(_hourToken);
            _token = null;
            _hourToken = null;
            _research = null;
            _bound = false;
        }
    }
}
