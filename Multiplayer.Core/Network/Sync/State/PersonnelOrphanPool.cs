using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE client-side parking lot for GeoCharacter instances a value-only roster reconcile removed
    /// while NO live container claims them (the #9 site ↔ #6 vehicle-crew cross-channel race, RCA
    /// 2026-07-09): the #9 site apply strips a site→vehicle-transferred soldier from his site first;
    /// the #6 crew apply lands ticks later with a FRESH container-scan index that can no longer resolve
    /// his GeoUnitId — without the pool he ends in NO container (invisible in every roster, every later
    /// #9 live-state edit for him dropped as "not live"). Parked instances re-enter resolution via the
    /// CharacterIndex pool merge (<c>PersonnelReflection.BuildCharacterIndex</c>), so the NEXT #6/#9
    /// apply that references the id adopts it — the #6 crew map re-emits in FULL every flush, which is
    /// the built-in self-heal retry. Eviction: an id the reconcile placed into a container drops on
    /// that apply; an id a container scan claims again drops as superseded; everything else (dismissed/
    /// dead soldiers — the host's single-writer rosters never reference such an id again, so a parked
    /// instance can never resurrect one) drops at the session/reload/rebind reset, where the glue logs
    /// the never-reclaimed ids once. BCL-only (no UnityEngine.Debug): mutations RETURN what happened;
    /// the game-glue caller (<c>PersonnelReflection</c>) logs.
    /// </summary>
    public static class PersonnelOrphanPool
    {
        private static readonly Dictionary<long, object> _pool = new Dictionary<long, object>();
        private static readonly object _lock = new object();

        public static int Count { get { lock (_lock) return _pool.Count; } }

        /// <summary>Park a just-removed instance under its GeoUnitId (last-writer-wins on a re-park).
        /// Id 0 (None/unresolved — never a valid roster id) and null instances are ignored.</summary>
        public static void Park(long unitId, object instance)
        {
            if (unitId == 0 || instance == null) return;
            lock (_lock) _pool[unitId] = instance;
        }

        /// <summary>Drop one entry (adoption into a container / superseded by a live container scan).
        /// False when the id was never parked — the caller skips its adoption log.</summary>
        public static bool Evict(long unitId)
        {
            lock (_lock) return _pool.Remove(unitId);
        }

        /// <summary>Copy of the current entries, safe to iterate while callers evict (the
        /// CharacterIndex merge walks this while dropping superseded ids).</summary>
        public static List<KeyValuePair<long, object>> SnapshotEntries()
        {
            lock (_lock) return new List<KeyValuePair<long, object>>(_pool);
        }

        /// <summary>Session/reload/rebind seam: drop EVERYTHING. Returns the dropped ids so the glue
        /// logs never-reclaimed orphans once (they are gone with the level — never latch an instance
        /// across a geoscape reload).</summary>
        public static List<long> Reset()
        {
            lock (_lock)
            {
                var dropped = new List<long>(_pool.Keys);
                _pool.Clear();
                return dropped;
            }
        }
    }
}
