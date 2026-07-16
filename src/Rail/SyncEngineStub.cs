using System;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// STUB replacing the legacy ~2600-line SyncEngine, which stayed in the quarry (law 10: no legacy
    /// channels/codecs). Keeps the NetworkEngine.Sync seam alive and owns the ONE inbound 0x67-envelope
    /// chokepoint (<see cref="SurfaceRouter"/>); the new rail arms Router hooks per surface.
    /// </summary>
    public sealed class SyncEngine : ISyncSink
    {
        private readonly NetworkEngine _engine;
        public readonly SurfaceRouter Router = new SurfaceRouter();

        public SyncEngine(NetworkEngine engine)
        {
            _engine = engine;
            // Spike A: research surface rides the geoscape inbound hook (returns false for other ids).
            Router.GeoscapeInbound = (peer, surfaceId, payload) => ResearchSpike.HandleInbound(_engine, surfaceId, payload);
        }

        public bool IsHost => _engine != null && _engine.IsHost;
        public Guid ResolveActor(ulong peerId) => Guid.Empty; // ponytail: rail wires the real peer→player map when intents land
        public void RefreshUi() { }

        /// <summary>Unified 0x67 envelope inbound (routed by NetworkEngine.RouteMessage).</summary>
        public void OnSyncEnvelope(ulong senderPeerId, byte[] payload) => Router.OnInbound(senderPeerId, payload, this);

        // Lifecycle seams NetworkEngine / SessionManager / SaveTransferCoordinator drive.
        // No-ops until the rail owns real surface state (then each becomes the rail's re-seed/reset).
        public void Tick() => ResearchSpike.HostTick(_engine);
        public void DetachAllChannels() => ResearchSpike.Reset();
        public void ResetForReloadBoundary() => ResearchSpike.Reset();
        public void ResetIntentDedupForPeer(ulong peerId) { }
        public void BroadcastFullWallet() { }
        public void BroadcastAllChannels() { }
    }
}
