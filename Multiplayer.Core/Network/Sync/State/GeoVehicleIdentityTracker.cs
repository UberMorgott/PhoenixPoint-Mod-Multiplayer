using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure host-side vehicle-lifecycle state machine for the mid-session vehicle-creation channel (#6),
    /// plus the pure client-side spawn/despawn predicates. NO Unity / reflection / engine dependency — the
    /// engine glue (<c>GeoVehicleChannel</c> / <c>GeoVehicleIdentityReflection</c>) reads live vehicles and calls
    /// into this, so the diff logic here is directly unit-testable (mirrors the codebase's pure-core / reflection
    /// split, e.g. <see cref="GeoVehicleIdentitySnapshot"/> vs the mirror reflection).
    ///
    /// RESIDENT semantics (channel rides an unacked transport — one lost flush / failed apply must NOT strand a
    /// vehicle until rejoin): a post-bind identity stays RESIDENT after it is emitted; every Snapshot() re-emits
    /// the FULL resident set (the client apply is key-idempotent, so re-delivery is a no-op) and the full
    /// TOMBSTONE set (keys that left the host's live set — the client despawns its mirror; re-delivery of a
    /// tombstone for an absent key is likewise a no-op).
    ///
    /// HOST flow (driven once per <c>GeoVehicleMirror.HostPollAndBroadcast</c> ~4 Hz walk of GeoMap.Vehicles):
    ///   • <see cref="TryMarkNew"/> — cheap membership check on a composite key; a key not yet KNOWN is recorded
    ///     as resident + known and returns true (so the caller does the ONE identity reflection read only for a
    ///     genuinely-new vehicle and marks the channel dirty). Already-known keys are ~free. A re-created key
    ///     clears its tombstone.
    ///   • <see cref="GetResident"/> / <see cref="GetTombstones"/> — the channel's Snapshot() emits both sets
    ///     IN FULL every flush (resident re-emission heals lost flushes / failed applies).
    ///   • <see cref="Prune"/> — keys no longer live (destroyed/despawned vehicle) drop from KNOWN + resident and
    ///     become TOMBSTONES (returns true so the caller marks the channel dirty and the despawn ships).
    /// A seed pass (<see cref="MarkKnown"/>) records the vehicles present when the host binds WITHOUT emitting
    /// them: those already ride the join save-blob to every client, so only post-bind creations are new (but a
    /// seeded vehicle the host later destroys still tombstones — the client got it via the join save).
    /// </summary>
    public sealed class GeoVehicleIdentityTracker
    {
        private readonly HashSet<long> _known = new HashSet<long>();
        private readonly Dictionary<long, GeoVehicleIdentity> _resident = new Dictionary<long, GeoVehicleIdentity>();
        private readonly HashSet<long> _tombstones = new HashSet<long>();

        /// <summary>Number of currently-known composite keys (test/diagnostic).</summary>
        public int KnownCount => _known.Count;

        /// <summary>Number of resident post-bind identities (re-emitted every Snapshot; test/diagnostic).</summary>
        public int ResidentCount => _resident.Count;

        /// <summary>Number of tombstoned keys (re-emitted every Snapshot; test/diagnostic).</summary>
        public int TombstoneCount => _tombstones.Count;

        /// <summary>True when a Snapshot flush would carry anything (resident identities or tombstones).</summary>
        public bool HasPayload => _resident.Count > 0 || _tombstones.Count > 0;

        /// <summary>True if this composite key has already been seen (no new-key detection needed).</summary>
        public bool IsKnown(long key) => _known.Contains(key);

        /// <summary>Seed a key as KNOWN without queuing it for broadcast (bind-time pass: the vehicle is already on
        /// clients via the join save). Clears any stale tombstone for the key. Idempotent.</summary>
        public void MarkKnown(long key)
        {
            _known.Add(key);
            _tombstones.Remove(key);
        }

        /// <summary>Host: record a NEWLY-appeared vehicle's identity as RESIDENT. Returns true ONLY the first time
        /// its key is seen (caller marks the channel dirty then); a known key is a no-op returning false. A
        /// re-created key clears its tombstone (spawn supersedes the earlier despawn).</summary>
        public bool TryMarkNew(GeoVehicleIdentity identity)
        {
            long key = identity.Key;
            if (!_known.Add(key)) return false;   // already known → not new
            _resident[key] = identity;
            _tombstones.Remove(key);
            return true;
        }

        /// <summary>Host (WA-1 behemoth): record/refresh a resident identity whose VALUE changes over time
        /// (status/placement in the sentinel entry). Unlike <see cref="TryMarkNew"/> the value is ALWAYS
        /// updated in place (the next Snapshot re-emits the freshest one); returns true only on the FIRST
        /// sighting of the key — the caller decides dirtiness for value changes (e.g. status edge) itself.
        /// A re-appearing key clears its tombstone (spawn supersedes the earlier despawn).</summary>
        public bool UpsertResident(GeoVehicleIdentity identity)
        {
            long key = identity.Key;
            bool first = _known.Add(key);
            _resident[key] = identity;
            _tombstones.Remove(key);
            return first;
        }

        /// <summary>Host: the FULL resident identity set for one Snapshot flush (NOT drained — re-emitted every
        /// flush so a lost packet / failed client apply heals on the next one; client apply is key-idempotent).</summary>
        public List<GeoVehicleIdentity> GetResident() => new List<GeoVehicleIdentity>(_resident.Values);

        /// <summary>Host: the FULL tombstone key set for one Snapshot flush (kept resident until the key is
        /// re-created or the session ends; client despawn is key-idempotent).</summary>
        public List<long> GetTombstones() => new List<long>(_tombstones);

        /// <summary>Host: any KNOWN key absent from the current live set drops from KNOWN + resident and becomes a
        /// TOMBSTONE (ships the client despawn). Returns true when anything was pruned (caller marks the channel
        /// dirty). No-op for keys still live.</summary>
        public bool Prune(ICollection<long> liveKeys)
        {
            if (_known.Count == 0) return false;
            List<long> drop = null;
            foreach (var k in _known)
                if (!liveKeys.Contains(k))
                    (drop = drop ?? new List<long>()).Add(k);
            if (drop == null) return false;
            foreach (var k in drop)
            {
                _known.Remove(k);
                _resident.Remove(k);
                _tombstones.Add(k);
            }
            return true;
        }

        /// <summary>Drop all state (session end / rebind).</summary>
        public void Clear()
        {
            _known.Clear();
            _resident.Clear();
            _tombstones.Clear();
        }

        /// <summary>CLIENT apply-idempotence predicate: spawn a mirror ONLY when the composite key is not already
        /// live on this client. Re-receiving an identity for an existing vehicle (resident re-emission, or one
        /// already covered by the join save) is a no-op — never a duplicate. Pure.</summary>
        public static bool ShouldSpawn(long key, ICollection<long> liveKeys)
            => liveKeys == null || !liveKeys.Contains(key);

        /// <summary>CLIENT despawn-idempotence predicate: despawn ONLY when the tombstoned key is actually live on
        /// this client. Re-receiving a tombstone for an already-removed (or never-spawned) key is a no-op. Pure.</summary>
        public static bool ShouldDespawn(long key, ICollection<long> liveKeys)
            => liveKeys != null && liveKeys.Contains(key);

        /// <summary>PURE ready-gate for <c>GeoVehicleIdentityReflection.Ensure</c>: the channel is operational ONLY
        /// when ALL FOUR core members resolved. The faction-def property binds off the FIRST live faction — an
        /// early Ensure against an empty Factions list must NOT latch ready with a null faction-def property
        /// (owner guids would never resolve → channel silently dead for the whole process); returning false makes
        /// Ensure retry the bind on the next call.</summary>
        public static bool ReflectionReady(bool hasVehicleIdField, bool hasVehiclesProp, bool hasOwnerProp,
                                           bool hasFactionDefProp)
            => hasVehicleIdField && hasVehiclesProp && hasOwnerProp && hasFactionDefProp;
    }
}
