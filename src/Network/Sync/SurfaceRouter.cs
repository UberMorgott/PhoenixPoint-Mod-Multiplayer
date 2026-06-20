using System;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// The ONE inbound chokepoint for the unified 0x67 <see cref="SyncProtocol"/> envelope. Decodes the
    /// envelope and dispatches to the LIVE tactical replication fast-path (<see cref="TacticalInbound"/>);
    /// any envelope the tactical hook does not consume is dropped (forward-compat). PURE: references no
    /// transport / Unity / HarmonyLib type, so it is unit-tested in isolation like every other sync primitive.
    ///
    /// NOTE: the geoscape ACTION relay rides the LEGACY 0x60/0x61/0x62 path in <c>SyncEngine</c>
    /// (OnActionRequest / OnActionApply / OnActionReject) — NOT this router. The dead 0x67 action-relay arms
    /// (HandleActionRequest/HandleActionApply + the SurfaceRegistry/SequenceTracker/RequestDedup coupling)
    /// were never wired (zero senders) and have been removed; this router now services tactical only.
    /// </summary>
    public sealed class SurfaceRouter
    {
        /// <summary>
        /// Tactical replication fast-path hook (armed by <c>TacticalDeploySync.ArmInboundHook</c>). Tactical
        /// surfaces (host→ALL one-way snapshot pushes, e.g. <c>tac.deploy</c>, plus the live move/combat/
        /// vision/equip/overwatch/anim surfaces) ride the SAME 0x67 envelope inbound chokepoint. This delegate
        /// is consulted with the decoded (surfaceId, payload): it returns true if it consumed the surface.
        /// NULL by default → the router is inert (every envelope is dropped). Signature:
        /// <c>(surfaceId, payload) -&gt; handled?</c>.
        /// </summary>
        public static System.Func<byte, byte[], bool> TacticalInbound;

        /// <summary>Decode + route one inbound envelope to the tactical fast-path. Never throws (forward-compat: drop).</summary>
        public void OnInbound(ulong senderPeerId, byte[] data, ISyncSink sink)
        {
            if (!SyncProtocol.TryDecodeEnvelope(data, out var surfaceId, out var kind, out var payload)) return;
            // Tactical fast-path: a tactical surface is consumed here (tracker-free, idempotent host→all
            // push). Inert unless tactical init armed the hook; any non-tactical envelope is dropped (the
            // geoscape action relay rides the legacy 0x60/0x61/0x62 path in SyncEngine, not this router).
            var tac = TacticalInbound;
            if (tac != null && tac(surfaceId, payload)) return;
        }
    }
}
