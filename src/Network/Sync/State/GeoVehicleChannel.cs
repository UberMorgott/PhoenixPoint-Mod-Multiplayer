using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #6 — mid-session VEHICLE-CREATION mirror. The client geoscape sim is frozen, so it never
    /// creates a vehicle the host acquires in-play (manufactured / story-gift / stolen aircraft); the ongoing
    /// position (0xA5) / travel (0xA6) / explore (0xA7) mirrors then SILENTLY SKIP that vehicle's unknown composite
    /// key, leaving the craft invisible on the client forever. This channel closes that gap: the host detects a
    /// genuinely-NEW composite key during the existing ~4 Hz <c>GeoVehicleMirror.HostPollAndBroadcast</c> walk of
    /// <c>GeoMap.Vehicles</c> (near-zero extra cost — that loop already computes each key), queues the new
    /// vehicle's <see cref="GeoVehicleIdentity"/>, and marks the channel dirty. <see cref="Snapshot"/> flushes the
    /// queued identities host→all on the GeoState (0xA1) rail; <see cref="Apply"/> spawns an INERT mirror
    /// (<see cref="GeoVehicleIdentityReflection.SpawnMirrorVehicle"/>) for any key not already live, so 0xA5/0xA6/
    /// 0xA7 then resolve + drive it. Mirrors <see cref="GeoSiteChannel"/> (identity mirror, host-attach-only, pure
    /// no-cascade client apply).
    ///
    /// JOIN / full-sync semantics: the channel's initial-snapshot path (<c>BroadcastAllChannels</c> → Snapshot) is
    /// consistently present, but yields nothing at bind — every vehicle a joining peer needs already rides the
    /// full join save-blob (host-authoritative full-state replication). <see cref="AttachHost"/> SEEDS the
    /// vehicles present at bind as KNOWN (no emit) for exactly that reason; only genuinely post-bind creations are
    /// broadcast. A duplicate/late identity for an already-live vehicle is a no-op (idempotent by composite key).
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

        public GeoVehicleChannel() => _live = this;

        // ─── HOST: new-key detection hooks called from GeoVehicleMirror.HostPollAndBroadcast ───────────────────

        /// <summary>HOST (per vehicle in the existing poll loop): note a live vehicle. On its FIRST sighting the
        /// identity is read (one reflection pass, new vehicles only) and queued + the channel marked dirty;
        /// already-known keys are ~free. No-op off-session.</summary>
        public static void HostObserve(GeoVehiclePos placement, object vehicle)
        {
            var self = _live;
            if (self == null) return;
            lock (self._lock)
            {
                if (self._tracker.IsKnown(placement.Key)) return;   // cheap: already seen → skip the reflection read
                if (!GeoVehicleIdentityReflection.TryReadIdentity(GeoRuntime.Instance, vehicle, placement, out var id))
                    return;                                          // couldn't read guids → retry next poll (not marked known)
                if (self._tracker.TryMarkNew(id))
                    NetworkEngine.Instance?.Sync?.MarkChannelDirty(self.ChannelId);
            }
        }

        /// <summary>HOST (after the poll loop builds the live-key set): forget keys no longer live so a re-created
        /// vehicle re-emits its identity. No-op off-session.</summary>
        public static void HostPrune(ICollection<long> liveKeys)
        {
            var self = _live;
            if (self == null || liveKeys == null) return;
            lock (self._lock) { self._tracker.Prune(liveKeys); }
        }

        // ─── IStateChannel ─────────────────────────────────────────────────────────────────────────────────────

        public byte[] Snapshot(GeoRuntime rt)
        {
            List<GeoVehicleIdentity> pending;
            lock (_lock)
            {
                pending = _tracker.DrainPending();
            }
            if (pending.Count == 0) return null;   // nothing new → no payload (FlushChannel no-ops on null)
            var snap = new GeoVehicleIdentitySnapshot();
            snap.Vehicles.AddRange(pending);
            return GeoVehicleIdentitySnapshot.Encode(snap);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = GeoVehicleIdentitySnapshot.Decode(data);
            if (snap == null || snap.Vehicles.Count == 0) return;
            var liveKeys = GeoVehicleIdentityReflection.ResolveLiveKeys(rt);
            foreach (var id in snap.Vehicles)
            {
                // Idempotent: spawn ONLY when the composite key is not already live (duplicate / join-save-covered
                // identity = no-op, never a second vehicle). SpawnMirrorVehicle re-checks the live keys too.
                if (GeoVehicleIdentityTracker.ShouldSpawn(id.Key, liveKeys))
                    GeoVehicleIdentityReflection.SpawnMirrorVehicle(rt, id);
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
            lock (_lock) { _tracker.Clear(); }
            _bound = false;
        }
    }
}
