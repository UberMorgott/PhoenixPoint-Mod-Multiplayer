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
            // Research rail (migration #1): deltas + intents ride the geoscape inbound hook
            // (returns false for other ids). The peer id feeds the host-side IntentDedup.
            Router.GeoscapeInbound = (peer, surfaceId, payload) => ResearchSync.HandleInbound(_engine, peer, surfaceId, payload);
        }

        public bool IsHost => _engine != null && _engine.IsHost;
        public Guid ResolveActor(ulong peerId) => Guid.Empty; // ponytail: rail wires the real peer→player map when intents land
        public void RefreshUi() { }

        /// <summary>Unified 0x67 envelope inbound (routed by NetworkEngine.RouteMessage).</summary>
        public void OnSyncEnvelope(ulong senderPeerId, byte[] payload) => Router.OnInbound(senderPeerId, payload, this);

        // Lifecycle seams NetworkEngine / SessionManager / SaveTransferCoordinator drive.
        // DetachAllChannels = full session teardown (seq streams reset); ResetForReloadBoundary =
        // mid-session reload (rca-3 contract: geoscape refs dropped, seq/nonce counters PERSIST so
        // post-reload deltas keep applying).
        public void Tick() => ResearchSync.HostTick(_engine);
        public void DetachAllChannels() => ResearchSync.Reset();
        public void ResetForReloadBoundary() => ResearchSync.ResetForReloadBoundary();
        public void ResetIntentDedupForPeer(ulong peerId) => ResearchSync.ResetIntentDedupForPeer(peerId);
        public void BroadcastFullWallet() { }
        public void BroadcastAllChannels() { }
    }
}
