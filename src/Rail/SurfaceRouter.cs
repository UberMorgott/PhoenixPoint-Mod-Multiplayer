using System;
using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// The ONE inbound chokepoint for the unified 0x67 <see cref="SyncProtocol"/> envelope. Decodes the
    /// envelope and dispatches to the LIVE tactical replication fast-path (<see cref="TacticalInbound"/>),
    /// then the geoscape hook (<see cref="GeoscapeInbound"/>, armed by <c>SyncEngine</c>: ResearchSync /
    /// ManufactureSync / IntentRail / GenericApplier); any envelope neither hook consumes is dropped
    /// (forward-compat). PURE: references no transport / Unity / HarmonyLib type, so it is unit-tested in
    /// isolation like every other sync primitive. The action-relay surfaces GeoIntent/GeoOutcome/GeoReject
    /// (0xA2-0xA4) are retired tombstones — client intents ride each family's own surface via IntentRail.
    /// Both peers run the same DLL, so there is exactly ONE rail — no double-apply.
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
        /// Geoscape envelope surfaces (spec §2.1 partition 0xA0-0xBF, e.g. <c>GeoRail</c>) ride the SAME 0x67
        /// chokepoint as tactical. INSTANCE-bound (the geoscape handler is an instance method on SyncEngine that
        /// reaches that engine's applier state). Consulted AFTER the tactical fast-path so a tactical surface
        /// always wins its own id range. NULL by default → inert (additive). The senderPeerId is threaded through
        /// (mirrors <see cref="TacticalInbound"/>) so the intent surfaces (0xAB/0xAE/0xAF) can dedup per-peer;
        /// the host→all delta surfaces ignore it.
        /// Signature: <c>(senderPeerId, surfaceId, payload) -&gt; handled?</c>.
        /// </summary>
        public System.Func<ulong, byte, byte[], bool> GeoscapeInbound;

        /// <summary>
        /// THE TURN-EPOCH GATE (law L96). CLIENT-side predicate: "this peer has NOT yet crossed a
        /// faction-turn edge the host has already crossed". Armed by <c>TacticalTurnSync</c>; null = inert
        /// (host, single-player, or no tactical session), which keeps this mechanism invisible to every path
        /// that is not a mirroring client mid-handoff.
        ///
        /// THE REPORT (2026-08-05). An invisible enemy showed its native sound/perception beacon on the host
        /// and moved; every client's camera panned to empty ground. The trace is <c>KnownState.Located</c>
        /// (<c>TacticalActorViewBase.RefreshLocatedBeacon</c>:372-375), raised on the host by a bleed tick —
        /// <c>TacticalActor.ApplyDamageInternal</c> → <c>IncrementKnownCounterToAll(Located)</c>:1505 — and
        /// decayed at every faction-turn start by <c>TacticalFactionVision.OnFactionStartTurn</c>:154-165.
        /// BOTH PEERS APPLIED THE SAME TWO FACTS IN OPPOSITE ORDER: host <c>Player.log</c>:8227 turn change,
        /// then :8272 the 10 HP bleed → decay first, raise after, beacon survives. Client I2 :6946 the same
        /// bleed, then :7051 the turn change → the mirrored raise was wiped by the client's own turn edge.
        /// Same actor, <c>Located</c> on the host and <c>Hidden</c> on both clients, and the settle could not
        /// repair it because that actor was cloaked and every settle path skips cloaked actors
        /// (<c>LocateByDistance</c> returns early on <c>IsCloaked</c>). Hearing was never the gap; ORDER was.
        ///
        /// WHY THE ORDER FLIPS WITHOUT ANY PACKET BEING REORDERED. The transport is per-peer ordered, so the
        /// client DOES receive the turn message first — but receiving it only moves the CURSOR
        /// (<c>TacticalTurnSync.HostFactionGuid</c>). This peer's own turn edge happens later, when its hold
        /// releases and the native <c>NextTurnCrt</c> actually runs. Every record landing in that gap was
        /// stamped by the host AFTER its edge and would be applied here BEFORE ours — one epoch too early.
        /// That is generic: it has nothing to do with vision, damage or beacons, and the fix must not either.
        /// So the records WAIT for the epoch they belong to, instead of any one of them being re-raised by
        /// hand (a per-path guard, explicitly rejected).
        ///
        /// NOT A CROSS-PEER WAIT. The gate closes on THIS peer crossing THIS peer's own edge, driven by a
        /// message already received; no other peer can withhold it, and <see cref="HeldFrameCeiling"/> bounds
        /// it regardless. <see cref="SurfaceIds.TacTurn"/> is never held — it is the surface that OPENS the
        /// gate, and holding it would be the deadlock this exists to avoid.
        /// </summary>
        public static Func<bool> ClientBehindTurnEdge;

        /// <summary>~15 s at 60 fps. The gate normally closes within a few frames (the AI hold releases the
        /// same frame the cursor arrives), so reaching this means the local turn machine stalled — at which
        /// point applying the backlog late is strictly better than dropping it or holding it forever (L91).
        /// static readonly, not const: an inlined const stops being a named bound in the IL.</summary>
        public static readonly int HeldFrameCeiling = 900;

        private readonly List<KeyValuePair<ulong, byte[]>> _held = new List<KeyValuePair<ulong, byte[]>>();
        private int _heldFrames;
        private bool _releasing;

        /// <summary>How many records are waiting for this peer's turn edge (diagnostics + the executed law arm).</summary>
        public int HeldCount { get { return _held.Count; } }

        /// <summary>Per-frame pump (driven from <c>SyncEngine.Tick</c>). Replays the backlog IN ARRIVAL ORDER
        /// once this peer has crossed its own turn edge. Returns the number of records released PAST the
        /// ceiling — 0 is healthy; anything else means the local turn machine stalled and the caller shouts.</summary>
        public int ReleaseHeld()
        {
            if (_held.Count == 0) { _heldFrames = 0; return 0; }
            bool expired = ++_heldFrames >= HeldFrameCeiling;
            var behind = ClientBehindTurnEdge;
            if (!expired && behind != null && behind()) return 0;

            int forced = expired ? _held.Count : 0;
            var batch = _held.ToArray();
            _held.Clear();
            _heldFrames = 0;
            _releasing = true;
            try { foreach (var rec in batch) OnInbound(rec.Key, rec.Value); }
            finally { _releasing = false; }
            return forced;
        }

        /// <summary>Decode + route one inbound envelope to the tactical fast-path. Never throws (forward-compat: drop).
        ///
        /// THE ORDINAL INHERITANCE SEAM (see <see cref="RailOrdinal"/>). The whole dispatch runs inside the
        /// applying message's ordinal, so ANY presentation a family produces synchronously out of an apply —
        /// the research-complete modal raised from inside the 0xAC batch, a mirrored 0xB6 raise, whatever a
        /// future family adds — inherits the ordering key of the message that caused it, with nothing
        /// per-family to wire up. Placement is the universality: the one chokepoint every surface passes.</summary>
        public void OnInbound(ulong senderPeerId, byte[] data)
        {
            if (!SyncProtocol.TryDecodeEnvelope(data, out var surfaceId, out var kind, out var ordinal, out var payload)) return;
            // TURN-EPOCH GATE (see ClientBehindTurnEdge): a record the host stamped after ITS faction-turn
            // edge is not applied before this peer crosses its own. Held BEFORE the ordinal is observed so
            // the replay re-enters this method whole and the batch keeps its arrival order. _releasing stops
            // the replay from re-holding itself.
            if (!_releasing && surfaceId != SurfaceIds.TacTurn)
            {
                var behind = ClientBehindTurnEdge;
                if (behind != null && behind())
                {
                    _held.Add(new KeyValuePair<ulong, byte[]>(senderPeerId, data));
                    return;
                }
            }
            RailOrdinal.Observe(ordinal);
            using (RailOrdinal.Applying(ordinal))
            {
                // Tactical fast-path: a tactical surface is consumed here (tracker-free, idempotent host→all
                // push). Inert unless tactical init armed the hook; any envelope the tactical hook declines falls
                // through to the geoscape hook below (wallet/state/vehicle surfaces, plus the action-relay
                // GeoIntent/GeoOutcome/GeoReject surfaces).
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
}
