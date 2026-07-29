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
            // Every client-intent surface (0xAB research / 0xAE manufacture / 0xAF personnel / 0xB0 time
            // / 0xB1 base / 0xB3 equip / 0xB4 event / 0xB5 vehicle) rides the ONE generic intent engine —
            // families register their op tables, the engine owns nonce/dedup/dispatch/reject (idempotent
            // re-registration).
            ResearchSync.RegisterIntents();
            ManufactureSync.RegisterIntents();
            EquipSync.RegisterIntents();
            PersonnelSync.RegisterIntents();
            TimeSync.RegisterIntents();
            FacilitySync.RegisterIntents();
            EventSync.RegisterIntents();
            VehicleSync.RegisterIntents();
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
            GenericApplier.ClientCrcTick(_engine); // client-only inside: law-7 drift backstop, one root per second
            EventPopup.ClientTick(_engine);        // client-only inside: event-window backlog, ~1 Hz (a joiner's
                                                  // records arrive with the SAVE, so a delta-driven pump alone
                                                  // never fires for them)
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
            VehicleSync.Reset();
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

        /// <summary>Deliberate no-op: the wallet is ordinary covered state and rides the value rail.</summary>
        public void BroadcastFullWallet() { }

        /// <summary>The HOST re-seed seam, driven by both join paths (SaveTransferCoordinator.OnJoinReady
        /// for a mid-session joiner that reached the live geoscape, HostReseedAfterReveal after a reload)
        /// — both host-guarded at the call site. GAME roots ride the save transfer (law 1), but two things
        /// do NOT: MOD roots (IdentityResolver mod-root contract — the host's M#cart can hold staged items
        /// the joiner's empty store never learns, and dict deltas ship per-subkey so it never converges),
        /// and the kind-id table (DiffEngine._sentKinds is per-SESSION, not per-peer — a joiner would get
        /// deltas whose kindIds it was never taught). The joiner cannot heal itself either: its own
        /// gap-resync is suppressed on the first delta it ever sees (GenericApplier "_lastSeq != 0").
        /// ONE full resend fixes all of it generically — every root, every future mod root.</summary>
        public void BroadcastAllChannels() => DiffEngine.RequestFullResend();
    }
}
