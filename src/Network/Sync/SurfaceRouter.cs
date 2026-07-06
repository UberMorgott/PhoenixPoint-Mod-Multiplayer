using System;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// The ONE inbound chokepoint for the unified 0x67 <see cref="SyncProtocol"/> envelope. Decodes the
    /// envelope and dispatches to the LIVE tactical replication fast-path (<see cref="TacticalInbound"/>);
    /// any envelope the tactical hook does not consume is dropped (forward-compat). PURE: references no
    /// transport / Unity / HarmonyLib type, so it is unit-tested in isolation like every other sync primitive.
    ///
    /// NOTE: the geoscape ACTION relay rides the LEGACY 0x60/0x61/0x62 path in <c>SyncEngine</c>
    /// (OnActionRequest / OnActionApply / OnActionReject) by default; under the <c>GeoActionRelay.UseEnvelope</c>
    /// cutover gate it instead rides the geoscape action surfaces GeoIntent/GeoOutcome/GeoReject (0xA2-0xA4)
    /// through THIS router (SyncEngine.HandleGeoscapeEnvelope). Exactly one rail is live per build (both peers
    /// run the same DLL) — never both, so no double-apply. The old dead 0x67 action-relay arms
    /// (HandleActionRequest/HandleActionApply + the SurfaceRegistry coupling) were never wired and removed.
    /// </summary>
    public sealed class SurfaceRouter
    {
        /// <summary>
        /// Tactical replication fast-path hook (armed by <c>TacticalDeploySync.ArmInboundHook</c>). Tactical
        /// surfaces (host→ALL one-way snapshot pushes, e.g. <c>tac.deploy</c>, plus the live move/combat/
        /// vision/equip/overwatch/anim surfaces) ride the SAME 0x67 envelope inbound chokepoint. This delegate
        /// is consulted with the sender + decoded (surfaceId, payload): it returns true if it consumed the
        /// surface. The senderPeerId is threaded through so host intent handlers can dedup per-peer (client
        /// intent nonces are client-LOCAL monotonic counters — without the peer in the key, two clients'
        /// nonces collide and the later client's intents are silently dropped). NULL by default → the router
        /// is inert (every envelope is dropped). Signature:
        /// <c>(senderPeerId, surfaceId, payload) -&gt; handled?</c>.
        /// </summary>
        public static System.Func<ulong, byte, byte[], bool> TacticalInbound;

        /// <summary>
        /// Geoscape replication hook (armed by the owning <c>SyncEngine</c> via <c>_router.GeoscapeInbound</c>).
        /// Geoscape envelope surfaces (spec §2.1 partition 0xA0-0xBF, e.g. <c>GeoWallet</c>) ride the SAME 0x67
        /// chokepoint as tactical. INSTANCE-bound (the geoscape handler is an instance method on SyncEngine that
        /// reaches that engine's applier state). Consulted AFTER the tactical fast-path so a tactical surface
        /// always wins its own id range. NULL by default → inert (additive). The senderPeerId is threaded through
        /// (mirrors <see cref="TacticalInbound"/>) so the geoscape action-INTENT surface (0xA2, action-relay
        /// envelope cutover) can resolve the actor + dedup per-peer; the wallet/state/vehicle surfaces ignore it.
        /// Signature: <c>(senderPeerId, surfaceId, payload) -&gt; handled?</c>.
        /// </summary>
        public System.Func<ulong, byte, byte[], bool> GeoscapeInbound;

        /// <summary>Decode + route one inbound envelope to the tactical fast-path. Never throws (forward-compat: drop).</summary>
        public void OnInbound(ulong senderPeerId, byte[] data, ISyncSink sink)
        {
            if (!SyncProtocol.TryDecodeEnvelope(data, out var surfaceId, out var kind, out var payload)) return;
            // Tactical fast-path: a tactical surface is consumed here (tracker-free, idempotent host→all
            // push). Inert unless tactical init armed the hook; any envelope the tactical hook declines falls
            // through to the geoscape hook below (wallet/state/vehicle surfaces, plus the action-relay
            // GeoIntent/GeoOutcome/GeoReject surfaces when the GeoActionRelay.UseEnvelope cutover is flipped on).
            var tac = TacticalInbound;
            if (tac != null && tac(senderPeerId, surfaceId, payload)) return;
            // Geoscape fast-path (additive, instance-bound): a geoscape envelope surface (0xA0-0xBF) is
            // consumed here. Inert unless the owning SyncEngine armed the hook; consulted AFTER tactical so a
            // tactical surface always wins its own id range.
            var geo = GeoscapeInbound;
            if (geo != null && geo(senderPeerId, surfaceId, payload)) return;
        }
    }
}
