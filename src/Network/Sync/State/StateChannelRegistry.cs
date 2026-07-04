using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Owns the live <see cref="IStateChannel"/> instances and maps channel id → channel. The
    /// <see cref="SyncEngine"/> holds one registry; it drives all channels uniformly (host attach /
    /// dirty flush, client apply). Increment A registers only the Inventory channel.
    /// </summary>
    public sealed class StateChannelRegistry
    {
        private readonly Dictionary<byte, IStateChannel> _byId = new Dictionary<byte, IStateChannel>();
        private readonly List<IStateChannel> _all = new List<IStateChannel>();

        public StateChannelRegistry()
        {
            Register(new InventoryChannel());
            Register(new ResearchChannel());
            Register(new UnlockChannel());      // #3 — research-unlock availability (facilities/manufacture/augmentations)
            Register(new DiplomacyChannel());   // #4 — faction diplomacy / reputation (value-only mirror)
            Register(new GeoSiteChannel());     // #5 — GeoSite identity mirror (Owner/Type/State/name/EncounterID), Case A
        }

        private void Register(IStateChannel channel)
        {
            _byId[channel.ChannelId] = channel;
            _all.Add(channel);
        }

        public IStateChannel Get(byte id) => _byId.TryGetValue(id, out var c) ? c : null;

        public IReadOnlyList<IStateChannel> All => _all;

        /// <summary>Map a channel id to the geoscape screen its apply should refresh (best-effort), or null.</summary>
        public GeoUiRefresh.Screen? ScreenFor(byte channelId)
        {
            switch (channelId)
            {
                case 1: return GeoUiRefresh.Screen.Manufacturing; // inventory feeds the manufacturing screen
                case 2: return GeoUiRefresh.Screen.Research;       // research channel feeds the research screen
                // ch3 (unlock) + ch4 (diplomacy) + ch5 (GeoSite identity) have NO single screen: an unlock
                // surfaces in BOTH the manufacturing list AND the base-layout facility picker, diplomacy has
                // no commonly-open module, and the geoscape map is not a single UI module (the event modal
                // reads the refreshed site lazily on next open). SyncEngine.OnStateSync drives the full
                // RefreshNeedsKick fan-out for channel ids ≥ 3 instead of a single targeted Refresh, so
                // ScreenFor returns null for them.
                default: return null;
            }
        }
    }
}
