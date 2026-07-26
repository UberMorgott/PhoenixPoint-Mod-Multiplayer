using System;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// STUB replacing the legacy ~2600-line SyncEngine, which stayed in the quarry (law 10: no legacy
    /// channels/codecs). Keeps the NetworkEngine.Sync seam alive and owns the ONE inbound 0x67-envelope
    /// chokepoint (<see cref="SurfaceRouter"/>); the new rail arms Router hooks per surface.
    /// </summary>
    public sealed class SyncEngine
    {
        private readonly NetworkEngine _engine;
        public readonly SurfaceRouter Router = new SurfaceRouter();

        public SyncEngine(NetworkEngine engine)
        {
            _engine = engine;
            // Every client-intent surface (0xAB research / 0xAE manufacture+equip / 0xAF personnel /
            // 0xB0 time) rides the ONE generic intent engine — families register their op tables, the
            // engine owns nonce/dedup/dispatch/reject (idempotent re-registration).
            ResearchSync.RegisterIntents();
            ManufactureSync.RegisterIntents();
            PersonnelSync.RegisterIntents();
            TimeSync.RegisterIntents();
            FacilitySync.RegisterIntents();
            // Geoscape rail surfaces ride the one inbound hook (each returns false for foreign ids):
            // the 0xAD manufacture order channel, the intent engine, and the generic value rail
            // (0xAC DiffEngine deltas → GenericApplier). The peer id feeds the host-side intent dedup.
            // (0xAA research retired 2026-07-26 — research rides 0xAC + 0xAB only.)
            Router.GeoscapeInbound = (peer, surfaceId, payload) =>
                ManufactureSync.HandleInbound(_engine, peer, surfaceId, payload)
                || IntentRail.HandleInbound(_engine, peer, surfaceId, payload)
                || GenericApplier.HandleInbound(_engine, peer, surfaceId, payload);
        }

        /// <summary>Unified 0x67 envelope inbound (routed by NetworkEngine.RouteMessage).</summary>
        public void OnSyncEnvelope(ulong senderPeerId, byte[] payload) => Router.OnInbound(senderPeerId, payload);

        // Lifecycle seams NetworkEngine / SessionManager / SaveTransferCoordinator drive.
        // DetachAllChannels = full session teardown (seq streams reset); ResetForReloadBoundary =
        // mid-session reload (rca-3 contract: geoscape refs dropped, seq/nonce counters PERSIST so
        // post-reload deltas keep applying).
        public void Tick()
        {
            ManufactureSync.HostTick(_engine);
            DiffEngine.HostTick(_engine);
            TimeSync.ClientTick(_engine); // client-only inside: TimeAnchor drift enforcement (~1 Hz)
            // Law 11 UNIVERSAL: flush one open-screen re-enter per frame if anything marked dirty —
            // client mirror batches AND host post-intent reseeds (EquipSync/PersonnelSync) both land here.
            OpenUiRepaint.FlushIfDirty();
        }

        public void DetachAllChannels()
        {
            IntentRail.Reset();
            ResearchSync.Reset();
            ManufactureSync.Reset();
            EquipSync.Reset();
            TimeSync.Reset();
            DiffEngine.Reset();
            GenericApplier.Reset();
            // Rail statics that survive an engine teardown and had no home in this aggregate until the
            // SessionEnd seam went in. Kept HERE, not in SessionEnd: this is the one full-teardown reset
            // list, and TearDown (which SessionEnd drives) is what calls it.
            OpenUiRepaint.Reset();
            SyncApplyScope.Reset();
        }

        public void ResetForReloadBoundary()
        {
            ResearchSync.ResetForReloadBoundary();
            ManufactureSync.ResetForReloadBoundary();
            EquipSync.ResetForReloadBoundary();
            TimeSync.ResetForReloadBoundary();
            DiffEngine.ResetForReloadBoundary();
            GenericApplier.ResetForReloadBoundary();
        }
        public void ResetIntentDedupForPeer(ulong peerId) => IntentRail.ResetIntentDedupForPeer(peerId);
        // Deliberate no-ops: the legacy push-reseed seam. SessionManager (JoinReady) and
        // SaveTransferCoordinator still drive it on the join paths, but on the rail a joiner is
        // seeded by the save transfer + resync-on-gap (law 7) — nothing to broadcast here.
        public void BroadcastFullWallet() { }
        public void BroadcastAllChannels() { }
    }
}
