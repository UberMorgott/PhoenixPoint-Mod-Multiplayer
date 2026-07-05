using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure host-side new-vehicle poll-diff state machine for the mid-session vehicle-creation channel (#6),
    /// plus the pure client-side spawn-idempotence predicate. NO Unity / reflection / engine dependency — the
    /// engine glue (<c>GeoVehicleChannel</c> / <c>GeoVehicleIdentityReflection</c>) reads live vehicles and calls
    /// into this, so the diff logic here is directly unit-testable (mirrors the codebase's pure-core / reflection
    /// split, e.g. <see cref="GeoVehicleIdentitySnapshot"/> vs the mirror reflection).
    ///
    /// HOST flow (driven once per <c>GeoVehicleMirror.HostPollAndBroadcast</c> ~4 Hz walk of GeoMap.Vehicles):
    ///   • <see cref="TryMarkNew"/> — cheap membership check on a composite key; a key not yet KNOWN is recorded
    ///     as pending + known and returns true (so the caller does the ONE identity reflection read only for a
    ///     genuinely-new vehicle and marks the channel dirty). Already-known keys are ~free.
    ///   • <see cref="DrainPending"/> — the channel's Snapshot() empties the pending identities into one payload.
    ///   • <see cref="Prune"/> — keys no longer live (destroyed/despawned vehicle) drop from KNOWN so a re-created
    ///     key re-emits from scratch (matches the position mirror's stale-signature prune).
    /// A seed pass (<see cref="MarkKnown"/>) records the vehicles present when the host binds WITHOUT emitting
    /// them: those already ride the join save-blob to every client, so only post-bind creations are new.
    /// </summary>
    public sealed class GeoVehicleIdentityTracker
    {
        private readonly HashSet<long> _known = new HashSet<long>();
        private readonly Dictionary<long, GeoVehicleIdentity> _pending = new Dictionary<long, GeoVehicleIdentity>();

        /// <summary>Number of currently-known composite keys (test/diagnostic).</summary>
        public int KnownCount => _known.Count;

        /// <summary>Number of identities awaiting the next Snapshot flush (test/diagnostic).</summary>
        public int PendingCount => _pending.Count;

        /// <summary>True if this composite key has already been seen (no re-emit needed).</summary>
        public bool IsKnown(long key) => _known.Contains(key);

        /// <summary>Seed a key as KNOWN without queuing it for broadcast (bind-time pass: the vehicle is already on
        /// clients via the join save). Idempotent.</summary>
        public void MarkKnown(long key) => _known.Add(key);

        /// <summary>Host: record a NEWLY-appeared vehicle's identity. Returns true and queues it for the next
        /// Snapshot flush ONLY the first time its key is seen; a known key is a no-op returning false. The caller
        /// marks the channel dirty on a true result.</summary>
        public bool TryMarkNew(GeoVehicleIdentity identity)
        {
            long key = identity.Key;
            if (!_known.Add(key)) return false;   // already known → not new
            _pending[key] = identity;
            return true;
        }

        /// <summary>Host: drain the queued new-vehicle identities for one broadcast payload (empties the queue).
        /// Returns an empty list when nothing is pending (the channel's Snapshot then returns null).</summary>
        public List<GeoVehicleIdentity> DrainPending()
        {
            if (_pending.Count == 0) return new List<GeoVehicleIdentity>();
            var list = new List<GeoVehicleIdentity>(_pending.Values);
            _pending.Clear();
            return list;
        }

        /// <summary>Host: forget any KNOWN/pending key absent from the current live set, so a re-created vehicle
        /// (same composite key) re-emits its identity. No-op for keys still live.</summary>
        public void Prune(ICollection<long> liveKeys)
        {
            if (_known.Count == 0 && _pending.Count == 0) return;
            var drop = new List<long>();
            foreach (var k in _known) if (!liveKeys.Contains(k)) drop.Add(k);
            foreach (var k in drop) _known.Remove(k);
            if (_pending.Count > 0)
            {
                drop.Clear();
                foreach (var k in _pending.Keys) if (!liveKeys.Contains(k)) drop.Add(k);
                foreach (var k in drop) _pending.Remove(k);
            }
        }

        /// <summary>Drop all state (session end / rebind).</summary>
        public void Clear()
        {
            _known.Clear();
            _pending.Clear();
        }

        /// <summary>CLIENT apply-idempotence predicate: spawn a mirror ONLY when the composite key is not already
        /// live on this client. Re-receiving an identity for an existing vehicle (a redundant/duplicate broadcast,
        /// or one already covered by the join save) is a no-op — never a duplicate. Pure.</summary>
        public static bool ShouldSpawn(long key, ICollection<long> liveKeys)
            => liveKeys == null || !liveKeys.Contains(key);
    }
}
