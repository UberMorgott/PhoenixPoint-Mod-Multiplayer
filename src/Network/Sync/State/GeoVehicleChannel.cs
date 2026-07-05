using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #6 — mid-session VEHICLE-CREATION mirror. The client geoscape sim is frozen, so it never
    /// creates a vehicle the host acquires in-play (manufactured / story-gift / stolen aircraft); the ongoing
    /// position (0xA5) / travel (0xA6) / explore (0xA7) mirrors then SILENTLY SKIP that vehicle's unknown composite
    /// key, leaving the craft invisible on the client forever. This channel closes that gap: the host detects a
    /// genuinely-NEW composite key during the existing ~4 Hz <c>GeoVehicleMirror.HostPollAndBroadcast</c> walk of
    /// <c>GeoMap.Vehicles</c> (near-zero extra cost — that loop already computes each key), queues the new
    /// vehicle's <see cref="GeoVehicleIdentity"/>, and marks the channel dirty. <see cref="Snapshot"/> ships the
    /// FULL RESIDENT identity set + FULL tombstone set host→all on the GeoState (0xA1) rail EVERY flush (the rail
    /// is unacked: drain-once emission would strand a vehicle behind ONE lost flush or one failed client apply —
    /// mid-load, unresolved guid, spawn-bind miss — until rejoin; re-emission heals because the client apply is
    /// key-idempotent both ways). <see cref="Apply"/> spawns an INERT mirror
    /// (<see cref="GeoVehicleIdentityReflection.SpawnMirrorVehicle"/>) for any identity key not already live, so
    /// 0xA5/0xA6/0xA7 then resolve + drive it — and despawns the live vehicle of any TOMBSTONED key (host
    /// destroyed/lost the craft; without this the client mirror ghosts in GeoMap.Vehicles forever). Mirrors
    /// <see cref="GeoSiteChannel"/> (identity mirror, host-attach-only, pure no-cascade client apply).
    ///
    /// JOIN / full-sync semantics: every vehicle a joining peer needs already rides the full join save-blob
    /// (host-authoritative full-state replication). <see cref="AttachHost"/> SEEDS the vehicles present at bind
    /// as KNOWN (no emit) for exactly that reason; only genuinely post-bind creations become resident. The
    /// initial-snapshot path (<c>BroadcastAllChannels</c> → Snapshot) re-ships the resident + tombstone sets to a
    /// late joiner — all no-ops there (post-bind creations are in its join save; tombstoned keys aren't live).
    ///
    /// The pure new-key state machine + client spawn-idempotence predicate live in
    /// <see cref="GeoVehicleIdentityTracker"/> (unit-tested); the wire codec in
    /// <see cref="GeoVehicleIdentitySnapshot"/>; the game-bound spawn/read reflection in
    /// <see cref="GeoVehicleIdentityReflection"/>.
    /// </summary>
    public sealed class GeoVehicleChannel : IStateChannel
    {
        public byte ChannelId => SurfaceIds.GeoVehicleChannel; // 6

        // Single live instance (the registry creates exactly one per SyncEngine/session), so the static host-poll
        // hook the mirror calls can reach this channel's tracker without threading a reference through SyncEngine.
        private static GeoVehicleChannel _live;

        private readonly GeoVehicleIdentityTracker _tracker = new GeoVehicleIdentityTracker();
        private readonly object _lock = new object();
        private bool _bound;   // host: seeded the pre-existing vehicles (bind-once)
        private byte _lastBehemothStatus = byte.MaxValue;   // host: WA-1 behemoth status edge-detect (255 = none seen)

        // PS1 crew tail: last-observed ordered GeoUnitIds per live PHOENIX vehicle key (poll-detect in
        // HostObserve — pre-existing vehicles never re-emit an identity, but their crew still mutates).
        // The FULL map re-emits every flush (resident-style heal on the unacked rail); pruned with liveKeys.
        private readonly Dictionary<long, long[]> _crew = new Dictionary<long, long[]>();

        public GeoVehicleChannel() => _live = this;

        // ─── HOST: new-key detection hooks called from GeoVehicleMirror.HostPollAndBroadcast ───────────────────

        /// <summary>HOST (per vehicle in the existing poll loop): note a live vehicle. On its FIRST sighting the
        /// identity is read (one reflection pass, new vehicles only) and queued + the channel marked dirty;
        /// already-known keys are ~free. No-op off-session.</summary>
        public static void HostObserve(GeoVehiclePos placement, object vehicle)
        {
            var self = _live;
            if (self == null) return;
            // PS1 crew tail: poll-detect crew changes for EVERY live Phoenix vehicle — known keys included
            // (a pre-bind craft never re-emits an identity, but the host still re-assigns its crew). Value
            // compare vs the last-observed ordered set; a change stores + dirties (next flush ships the
            // full crew map). Non-Phoenix vehicles return false (crew scope = the shared faction only).
            if (PersonnelReflection.TryReadCrewIds(GeoRuntime.Instance, vehicle, out var crew))
            {
                bool crewDirty;
                lock (self._lock)
                {
                    self._crew.TryGetValue(placement.Key, out var prev);
                    crewDirty = !GeoVehicleCrew.SameCrew(prev, crew);
                    if (crewDirty) self._crew[placement.Key] = crew;
                }
                if (crewDirty)
                    NetworkEngine.Instance?.Sync?.MarkChannelDirty(self.ChannelId);
            }
            lock (self._lock)
            {
                if (self._tracker.IsKnown(placement.Key)) return;   // cheap: already seen → skip the reflection read
                if (!GeoVehicleIdentityReflection.TryReadIdentity(GeoRuntime.Instance, vehicle, placement, out var id))
                    return;                                          // couldn't read guids → retry next poll (not marked known)
                if (self._tracker.TryMarkNew(id))
                    NetworkEngine.Instance?.Sync?.MarkChannelDirty(self.ChannelId);
            }
        }

        /// <summary>HOST (WA-1, per poll while a behemoth is live): upsert the behemoth's SENTINEL identity
        /// (see <see cref="GeoBehemothState"/>) so presence + <c>BehemothStatus</c> + (via <see cref="HostPrune"/>,
        /// once the key leaves the live set) the tombstone all reuse this channel's resident/tombstone machinery.
        /// Dirties on FIRST sighting and on every STATUS edge; placement inside the identity is refreshed
        /// silently (whatever flush ships next carries the freshest one). No-op off-session.</summary>
        public static void HostObserveBehemoth(GeoVehiclePos placement, byte status)
        {
            var self = _live;
            if (self == null) return;
            bool dirty;
            lock (self._lock)
            {
                bool first = self._tracker.UpsertResident(GeoBehemothState.MakeIdentity(status, placement));
                dirty = first || status != self._lastBehemothStatus;
                self._lastBehemothStatus = status;
            }
            if (dirty)
                NetworkEngine.Instance?.Sync?.MarkChannelDirty(self.ChannelId);
        }

        /// <summary>HOST (after the poll loop builds the live-key set): keys no longer live become TOMBSTONES (the
        /// channel dirties so the client despawn ships next flush) and a re-created vehicle re-emits its identity.
        /// No-op off-session.</summary>
        public static void HostPrune(ICollection<long> liveKeys)
        {
            var self = _live;
            if (self == null || liveKeys == null) return;
            bool pruned;
            lock (self._lock)
            {
                pruned = self._tracker.Prune(liveKeys);
                // PS1: drop crew entries of dead keys silently — the tombstone despawn (dirtied by the
                // prune above) already removes the whole vehicle client-side.
                if (self._crew.Count > 0)
                {
                    List<long> dead = null;
                    foreach (var k in self._crew.Keys)
                        if (!liveKeys.Contains(k))
                            (dead = dead ?? new List<long>()).Add(k);
                    if (dead != null)
                        foreach (var k in dead) self._crew.Remove(k);
                }
            }
            if (pruned)
                NetworkEngine.Instance?.Sync?.MarkChannelDirty(self.ChannelId);
        }

        // ─── IStateChannel ─────────────────────────────────────────────────────────────────────────────────────

        public byte[] Snapshot(GeoRuntime rt)
        {
            // RESIDENT emission: ship the FULL identity + tombstone sets every flush (never drain). The rail is
            // unacked — a drained-once identity behind one lost flush / failed apply left the vehicle invisible
            // until rejoin. Re-delivery is safe: the client apply is key-idempotent in both directions.
            List<GeoVehicleIdentity> resident;
            List<long> tombstones;
            List<GeoVehicleCrewRecord> crew = null;
            lock (_lock)
            {
                if (!_tracker.HasPayload && _crew.Count == 0) return null;   // nothing to mirror → no payload (FlushChannel no-ops)
                resident = _tracker.GetResident();
                tombstones = _tracker.GetTombstones();
                if (_crew.Count > 0)
                {
                    // PS1: FULL crew map every flush (resident-style — a lost flush / failed apply heals
                    // on the next one; the client reconcile is value-only idempotent).
                    crew = new List<GeoVehicleCrewRecord>(_crew.Count);
                    foreach (var kv in _crew) crew.Add(new GeoVehicleCrewRecord(kv.Key, kv.Value));
                }
            }
            var snap = new GeoVehicleIdentitySnapshot();
            snap.Vehicles.AddRange(resident);
            snap.Tombstones.AddRange(tombstones);
            if (crew != null) snap.Crew.AddRange(crew);
            return GeoVehicleIdentitySnapshot.Encode(snap);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = GeoVehicleIdentitySnapshot.Decode(data);
            if (snap == null || (snap.Vehicles.Count == 0 && snap.Tombstones.Count == 0 && snap.Crew.Count == 0)) return;
            var liveKeys = GeoVehicleIdentityReflection.ResolveLiveKeys(rt);
            foreach (var id in snap.Vehicles)
            {
                // WA-1 BEHEMOTH sentinel entry: not a GeoVehicle (lives outside GeoMap.Vehicles) — its own
                // spawn-if-absent + status stamp path, idempotent by the live AlienFaction.Behemoth accessor.
                if (GeoBehemothState.IsBehemothIdentity(id))
                {
                    GeoBehemothReflection.ApplyPresence(rt, id);
                    continue;
                }
                // Idempotent: spawn ONLY when the composite key is not already live (resident re-emission /
                // join-save-covered identity = no-op, never a second vehicle). SpawnMirrorVehicle re-checks too.
                if (GeoVehicleIdentityTracker.ShouldSpawn(id.Key, liveKeys))
                    GeoVehicleIdentityReflection.SpawnMirrorVehicle(rt, id);
            }
            foreach (var key in snap.Tombstones)
            {
                // WA-1 BEHEMOTH tombstone: host lost/removed the behemoth → despawn the live mirror (no-op when
                // none is live — tombstones re-emit every flush).
                if (GeoBehemothState.IsBehemothKey(key))
                {
                    GeoBehemothReflection.Despawn(rt);
                    continue;
                }
                // Idempotent: despawn ONLY a live key (tombstones are re-emitted every flush; a key already gone
                // — or never spawned on this client — is a no-op). The two sets are disjoint by tracker contract.
                if (GeoVehicleIdentityTracker.ShouldDespawn(key, liveKeys))
                    GeoVehicleIdentityReflection.DespawnVehicle(rt, key);
            }
            // PS1 crew tail — applied AFTER the resident spawn loop (spawn-then-crew ordering, spec §2.3),
            // so a just-mirrored craft's crew lands in the same apply. Value-only _tacUnits reconcile;
            // an unresolvable key (mid-load, failed spawn) logs + skips — the full-map re-emission heals.
            foreach (var c in snap.Crew)
            {
                var vehicle = GeoVehicleIdentityReflection.ResolveVehicleByKey(rt, c.Key);
                if (vehicle == null)
                {
                    Debug.Log("[Multiplayer] GeoVehicleChannel: crew apply skipped — vehicle key=" + c.Key.ToString("X") + " not live on this client");
                    continue;
                }
                PersonnelReflection.ApplyVehicleCrew(rt, vehicle, c.UnitIds);
            }
        }

        public void AttachHost(SyncEngine eng)
        {
            if (_bound) return;                          // seeded; nothing per-frame to do
            if (eng == null) return;
            if (GeoRuntime.Instance?.GeoLevel() == null) return;   // not in geoscape yet / mid-load → retry next frame

            // Seed the vehicles present at bind as KNOWN WITHOUT emitting them: they already ride the join save to
            // every client. Only genuinely post-bind creations become "new". (Empty when vehicles not yet loaded —
            // a late-loading vehicle would then emit once and be a harmless idempotent no-op on clients.)
            lock (_lock)
            {
                foreach (var key in GeoVehicleIdentityReflection.ResolveLiveKeys(GeoRuntime.Instance))
                    _tracker.MarkKnown(key);
            }
            _bound = true;
        }

        public void DetachHost()
        {
            lock (_lock)
            {
                _tracker.Clear();
                _crew.Clear();
                _lastBehemothStatus = byte.MaxValue;
            }
            _bound = false;
        }
    }
}
