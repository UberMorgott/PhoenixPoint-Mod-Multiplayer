namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #4 — faction DIPLOMACY / reputation (fixes
    /// <c>ResearchElement.Complete → RewardReputation() → PartyDiplomacy.ModifyDiplomacy</c> changing
    /// faction reputation HOST-ONLY, never mirrored). Host snapshots every faction-to-faction relation's
    /// reputation int keyed by (ownerFactionDef guid, withPartyDef guid); client overwrites each to the
    /// host value WITHOUT firing the change cascade (pure value mirror, like the wallet echo). Codec +
    /// <see cref="DiplomacySnapshot"/> live in their own pure file for unit testability;
    /// <see cref="DiplomacyReflection"/> is the bridge.
    ///
    /// Dirty triggers: the faction research-complete event (the reputation reward runs in research
    /// completion — same trigger as ch2/ch3) PLUS the hourly tick (diplomacy also shifts from the daily
    /// update / missions; the value mirror re-converges within an in-game hour). The snapshot is a handful
    /// of int relations → cheap to re-send.
    /// </summary>
    public sealed class DiplomacyChannel : IStateChannel
    {
        public byte ChannelId => 4;

        private object _token;     // opaque faction research-event token (Start/Complete)
        private object _hourToken; // opaque hourly-tick token (diplomacy heartbeat)
        private object _faction;   // bound faction instance (rebind guard)

        public byte[] Snapshot(GeoRuntime rt)
        {
            var snap = DiplomacyReflection.Snapshot(rt);
            if (snap == null) return null;
            return DiplomacySnapshot.Encode(snap);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = DiplomacySnapshot.Decode(data);
            if (snap == null) return;
            DiplomacyReflection.Apply(rt, snap);
        }

        public void AttachHost(SyncEngine eng)
        {
            // NO hard "already bound" gate — rebind when the LIVE faction instance changes (geoscape reload builds
            // a fresh GeoPhoenixFaction with no mid-session Detach; the old `if (_bound) return;` left this channel
            // subscribed to the dead one forever → diplomacy sync silently stopped after the first tactical
            // round-trip). The WalletWatcher lesson (WalletWatcher.cs:20-28) applied to every event channel.
            if (eng == null) return;
            var fac = GeoRuntime.Instance.PhoenixFaction();
            if (fac == null) return;                          // not in geoscape yet / mid-load
            if (ReferenceEquals(fac, _faction)) return;       // already bound to this instance

            DetachHost();
            _faction = fac;
            byte id = ChannelId;
            _token = ResearchStateReflection.SubscribeFactionResearchEvents(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            _hourToken = ResearchStateReflection.SubscribeHourlyTick(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            // Seed clients with the authoritative diplomacy table the moment we bind.
            eng.MarkChannelDirty(id);
        }

        public void DetachHost()
        {
            if (_token != null) ResearchStateReflection.Unsubscribe(_token);
            if (_hourToken != null) ResearchStateReflection.Unsubscribe(_hourToken);
            _token = null;
            _hourToken = null;
            _faction = null;
        }
    }
}
