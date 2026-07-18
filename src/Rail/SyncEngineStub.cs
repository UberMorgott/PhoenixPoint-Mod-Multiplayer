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
            // Geoscape rail surfaces ride the one inbound hook (each returns false for foreign ids):
            // ResearchSync (0xAA/0xAB structural-ish research messages + intents) and the generic value
            // rail (0xAC DiffEngine deltas → GenericApplier). The peer id feeds the host-side dedups.
            Router.GeoscapeInbound = (peer, surfaceId, payload) =>
                ResearchSync.HandleInbound(_engine, peer, surfaceId, payload)
                || ManufactureSync.HandleInbound(_engine, peer, surfaceId, payload)
                || GenericApplier.HandleInbound(_engine, peer, surfaceId, payload);
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
        public void Tick()
        {
            ResearchSync.HostTick(_engine);
            ManufactureSync.HostTick(_engine);
            DiffEngine.HostTick(_engine);
            // Law 11 UNIVERSAL (client): flush one open-screen re-enter per frame if a mirror batch landed
            // this frame. Inert on the host (never marks dirty) and when no batch applied.
            OpenUiRepaint.FlushIfDirty();
        }

        public void DetachAllChannels()
        {
            ResearchSync.Reset();
            ManufactureSync.Reset();
            EquipSync.Reset();
            DiffEngine.Reset();
            GenericApplier.Reset();
        }

        public void ResetForReloadBoundary()
        {
            ResearchSync.ResetForReloadBoundary();
            ManufactureSync.ResetForReloadBoundary();
            EquipSync.ResetForReloadBoundary();
            DiffEngine.ResetForReloadBoundary();
            GenericApplier.ResetForReloadBoundary();
        }
        public void ResetIntentDedupForPeer(ulong peerId)
        {
            ResearchSync.ResetIntentDedupForPeer(peerId);
            ManufactureSync.ResetIntentDedupForPeer(peerId);
        }
        public void BroadcastFullWallet() { }
        public void BroadcastAllChannels() { }
    }
}
