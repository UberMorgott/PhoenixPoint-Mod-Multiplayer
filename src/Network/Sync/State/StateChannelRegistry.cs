using System.Collections.Generic;

namespace Multipleer.Network.Sync.State
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
                default: return null;
            }
        }
    }
}
