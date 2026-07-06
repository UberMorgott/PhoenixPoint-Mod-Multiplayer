using System.Collections.Generic;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE decision core for the mid-battle actor-lifecycle mirror (spec TS1): the host spawn-broadcast gate and
    /// the host despawn sweep. Engine-free so both decisions unit-test without UnityEngine or the game assembly
    /// (the reflection glue lives in <see cref="TacticalActorLifecycleSync"/>), matching the repo's pure-core +
    /// thin-engine-glue pattern (cf. <c>TacticalActorStateDiff</c>, <c>NullFactionEnterPlayGate</c>).
    /// </summary>
    public static class TacticalActorLifecycleGate
    {
        /// <summary>HOST: broadcast a mid-battle spawn for an actor entering play? True iff the turn-0 deploy
        /// snapshot has already been captured (so deploy-time actors — which ride the 0x80 snapshot — are excluded)
        /// AND the actor is not already registered (a deploy actor / an already-mirrored spawn) AND we are not inside
        /// a remote apply (the client's own materialize must never re-emit). The session/host/side checks are engine
        /// concerns handled by the caller.</summary>
        public static bool ShouldBroadcastSpawn(bool deployCaptured, bool alreadyRegistered, bool applyingRemote)
            => deployCaptured && !alreadyRegistered && !applyingRemote;

        /// <summary>HOST despawn sweep (pure set arithmetic): given the registered (netId → actor-key) pairs and the
        /// set of CURRENTLY-LIVE actor keys, return the netIds whose actor is no longer live — a non-damage despawn
        /// (evac / morph-consume / off-map-depart / expiry). Order is preserved (registry enumeration order). A null
        /// actor-key is skipped; a null live set treats everything as despawned (defensive).</summary>
        public static List<int> ComputeDespawnedNetIds<TKey>(
            IEnumerable<KeyValuePair<int, TKey>> registered, ISet<TKey> liveKeys) where TKey : class
        {
            var result = new List<int>();
            if (registered == null) return result;
            foreach (var kv in registered)
            {
                if (kv.Value == null) continue;
                if (liveKeys == null || !liveKeys.Contains(kv.Value)) result.Add(kv.Key);
            }
            return result;
        }
    }
}
