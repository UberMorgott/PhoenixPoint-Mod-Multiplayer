namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// A host-authoritative state echo channel. Generalizes the working wallet echo to any faction
    /// subsystem: the host snapshots authoritative state on a change event, versions + broadcasts it,
    /// and clients overwrite their local copy and refresh the open UI. One channel per subsystem
    /// (Inventory, Research, …), keyed by <see cref="ChannelId"/>.
    /// </summary>
    public interface IStateChannel
    {
        /// <summary>Stable wire id (must be unique across all registered channels).</summary>
        byte ChannelId { get; }

        /// <summary>Host: serialize the authoritative subsystem state, or null if not yet available.</summary>
        byte[] Snapshot(GeoRuntime rt);

        /// <summary>Client: overwrite the local subsystem state to match <paramref name="data"/>.
        /// Always invoked inside a <see cref="SyncApplyScope"/> so interceptors pass through.</summary>
        void Apply(GeoRuntime rt, byte[] data);

        /// <summary>Host: subscribe this channel's game change-event so it marks the engine dirty.
        /// Idempotent — safe to call every frame; no-ops until its model is live and once bound.</summary>
        void AttachHost(SyncEngine eng);

        /// <summary>Host: drop the change-event subscription (session end / rebind).</summary>
        void DetachHost();
    }
}
