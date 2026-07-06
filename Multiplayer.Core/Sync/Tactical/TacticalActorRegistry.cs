using System;
using System.Collections.Generic;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Stable NetId &lt;-&gt; actor mapping for tactical replication, identical on host + client.
    ///
    /// NetId scheme (spec §4):
    ///   • Soldiers / vehicles: NetId == <c>GeoUnitId</c> int. A shared campaign save gives a given
    ///     soldier the SAME GeoUnitId on both instances, so the id matches with no negotiation.
    ///   • Pandorans / spawned actors: <c>GeoUnitId == 0</c> (<c>GeoTacUnitId.None</c>) on every such
    ///     actor → they collide. The HOST mints a sequential NetId starting at <see cref="MintBase"/>
    ///     (1_000_000) so a minted id can never collide a real GeoUnitId. Because deploy is
    ///     host-authoritative and the client hydrates from the host snapshot, both sides see the SAME
    ///     ordered actor set; the deploy message carries the host's <c>actorTable</c> so the client
    ///     reproduces the host mapping exactly.
    ///
    /// This is the PURE, engine-free core (spec / plan T1): it operates on the <see cref="IActorRef"/>
    /// abstraction ({ NetId-source GeoUnitId, world position }) so it unit-tests without UnityEngine or
    /// the game assembly. A thin adapter (<c>TacticalActorAdapter</c>, separate file, NOT linked into the
    /// test assembly) wraps a live <c>TacticalActorBase</c> into an <see cref="IActorRef"/>.
    /// </summary>
    public sealed class TacticalActorRegistry
    {
        /// <summary>First host-minted NetId for actors with no GeoUnitId (Pandorans/spawned). High enough
        /// that a minted id never collides a real GeoUnitId (those start at 1 and increment slowly).</summary>
        public const int MintBase = 1_000_000;

        /// <summary>Position match tolerance (squared world units) when matching a snapshot row to a
        /// restored Pandoran/spawned actor that has no GeoUnitId. Restored positions are byte-identical
        /// in the happy path, so a small epsilon only guards float reorder noise.</summary>
        public const float PosEpsilon = 0.05f;

        private readonly Dictionary<int, IActorRef> _byNetId = new Dictionary<int, IActorRef>();
        private readonly Dictionary<IActorRef, int> _netIdByActor = new Dictionary<IActorRef, int>();
        private int _nextMintedId = MintBase;

        /// <summary>One row of the deploy actor table on the wire: the host's NetId for an actor, the
        /// actor's GeoUnitId (0 for Pandorans/spawned), and its world position (for pos-fallback match).</summary>
        public struct ActorRow
        {
            public int NetId;
            public int GeoUnitId;
            public float X, Y, Z;

            public ActorRow(int netId, int geoUnitId, float x, float y, float z)
            {
                NetId = netId; GeoUnitId = geoUnitId; X = x; Y = y; Z = z;
            }
        }

        // ─── Host: assign / build ─────────────────────────────────────────

        /// <summary>
        /// HOST: assign a NetId to an actor. GeoUnitId != 0 → NetId = GeoUnitId (idempotent: re-assigning
        /// the same actor returns the existing id). GeoUnitId == 0 → mint the next sequential id. Returns
        /// the assigned NetId.
        /// </summary>
        public int AssignHost(IActorRef actor)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            if (_netIdByActor.TryGetValue(actor, out var existing)) return existing;

            int geoId = actor.GeoUnitId;
            int netId = geoId != 0 ? geoId : MintNext();
            Bind(netId, actor);
            return netId;
        }

        /// <summary>Mint the next sequential Pandoran/spawned NetId (host only). Skips any value already in use.</summary>
        private int MintNext()
        {
            while (_byNetId.ContainsKey(_nextMintedId)) _nextMintedId++;
            return _nextMintedId++;
        }

        /// <summary>HOST: assign NetIds to every actor in deploy order and return the ordered actor table to
        /// ship in the <c>tac.deploy</c> message. Order is preserved as given (caller enumerates the map once).</summary>
        public List<ActorRow> BuildActorTable(IEnumerable<IActorRef> actors)
        {
            var rows = new List<ActorRow>();
            if (actors == null) return rows;
            foreach (var a in actors)
            {
                if (a == null) continue;
                int netId = AssignHost(a);
                var p = a.Position;
                rows.Add(new ActorRow(netId, a.GeoUnitId, p.x, p.y, p.z));
            }
            return rows;
        }

        // ─── Client: match the host table onto restored actors ────────────

        /// <summary>
        /// CLIENT: reproduce the host NetId mapping. For each host <paramref name="rows"/> entry, find the
        /// matching restored actor and bind it to the host's NetId:
        ///   • GeoUnitId != 0 → match the restored actor with the same GeoUnitId (exact, save-shared).
        ///   • GeoUnitId == 0 → match the nearest still-unmatched restored Pandoran/spawned actor within
        ///     <see cref="PosEpsilon"/> (restored ActorInstanceData.Pos is identical in the happy path).
        /// Returns the count of rows successfully matched. Unmatched rows are skipped (logged by the caller).
        /// </summary>
        public int MatchAndRegister(IReadOnlyList<ActorRow> rows, IEnumerable<IActorRef> restoredActors)
        {
            if (rows == null || restoredActors == null) return 0;

            // Snapshot the candidate set so each actor is consumed at most once.
            var remaining = new List<IActorRef>();
            foreach (var a in restoredActors) if (a != null) remaining.Add(a);

            int matched = 0;
            // Pass 1: GeoUnitId rows (deterministic, save-shared) — do these first so a Pandoran pos-match
            // never accidentally steals a soldier slot.
            foreach (var row in rows)
            {
                if (row.GeoUnitId == 0) continue;
                var hit = TakeByGeoUnitId(remaining, row.GeoUnitId);
                if (hit != null) { Bind(row.NetId, hit); matched++; }
            }
            // Pass 2: Pandoran/spawned rows (GeoUnitId == 0) → nearest-position within epsilon.
            foreach (var row in rows)
            {
                if (row.GeoUnitId != 0) continue;
                var hit = TakeByPosition(remaining, row.X, row.Y, row.Z);
                if (hit != null) { Bind(row.NetId, hit); matched++; }
            }
            return matched;
        }

        /// <summary>
        /// CLIENT lazy re-bind for a SINGLE host netId whose actor is not (yet) in this registry — the
        /// coverage hole for deploy-time position drift (a Pandoran whose client position differed by more
        /// than <see cref="PosEpsilon"/> at deploy, so its row never matched) and mid-mission host spawns.
        /// Reproduces the deploy match for just this netId against the current live actor set, REUSING the
        /// exact same matcher as <see cref="MatchAndRegister"/>. The host netId itself encodes the scheme
        /// (spec §4), so the client needs NO extra wire data: netId &lt; <see cref="MintBase"/> ⇒ the netId IS
        /// a GeoUnitId (soldier/vehicle) → GeoUnitId-EXACT match; netId &gt;= MintBase ⇒ a host-minted Pandoran
        /// (GeoUnitId 0) → position fallback within <see cref="PosEpsilon"/> (requires <paramref name="hasPos"/>).
        /// The client NEVER mints — it only binds the host-authored netId. Pass ONLY unbound candidates so the
        /// position fallback can never steal an already-bound actor. Returns true iff a candidate was matched
        /// and bound to <paramref name="netId"/>.
        /// </summary>
        public bool TryLazyRebind(int netId, bool hasPos, ActorPos pos, IEnumerable<IActorRef> liveActors)
        {
            if (netId < 0 || liveActors == null) return false;
            if (_byNetId.ContainsKey(netId)) return true;          // already bound (defensive) — caller resolves it
            int geoUnitId = netId < MintBase ? netId : 0;          // scheme: sub-MintBase netId == GeoUnitId
            if (geoUnitId == 0 && !hasPos) return false;           // minted id w/o a position → cannot match → drop
            var row = new ActorRow(netId, geoUnitId, pos.x, pos.y, pos.z);
            return MatchAndRegister(new[] { row }, liveActors) > 0;
        }

        private static IActorRef TakeByGeoUnitId(List<IActorRef> remaining, int geoUnitId)
        {
            for (int i = 0; i < remaining.Count; i++)
            {
                if (remaining[i].GeoUnitId == geoUnitId)
                {
                    var a = remaining[i];
                    remaining.RemoveAt(i);
                    return a;
                }
            }
            return null;
        }

        private static IActorRef TakeByPosition(List<IActorRef> remaining, float x, float y, float z)
        {
            int best = -1;
            float bestSq = PosEpsilon * PosEpsilon;
            for (int i = 0; i < remaining.Count; i++)
            {
                // Only pos-match actors that themselves have no GeoUnitId (the Pandoran/spawned set);
                // a soldier already failed pass 1 by id and must not be claimed by a position fallback.
                if (remaining[i].GeoUnitId != 0) continue;
                var p = remaining[i].Position;
                float dx = p.x - x, dy = p.y - y, dz = p.z - z;
                float sq = dx * dx + dy * dy + dz * dz;
                if (sq <= bestSq) { bestSq = sq; best = i; }
            }
            if (best < 0) return null;
            var a = remaining[best];
            remaining.RemoveAt(best);
            return a;
        }

        // ─── Both sides: lookup / mutate ──────────────────────────────────

        /// <summary>Bind a NetId to an actor (idempotent; rebinding a NetId replaces, rebinding an actor
        /// moves its id). Used by AssignHost, MatchAndRegister, and an explicit mid-battle spawn.</summary>
        public void Register(int netId, IActorRef actor)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            Bind(netId, actor);
        }

        private void Bind(int netId, IActorRef actor)
        {
            // If this actor already held a different id, drop the stale forward entry.
            if (_netIdByActor.TryGetValue(actor, out var oldId) && oldId != netId)
                _byNetId.Remove(oldId);
            // If this id pointed at a different actor, drop that actor's reverse entry.
            if (_byNetId.TryGetValue(netId, out var oldActor) && !ReferenceEquals(oldActor, actor))
                _netIdByActor.Remove(oldActor);

            _byNetId[netId] = actor;
            _netIdByActor[actor] = netId;
            if (netId >= _nextMintedId && netId >= MintBase) _nextMintedId = netId + 1;
        }

        public bool TryGet(int netId, out IActorRef actor) => _byNetId.TryGetValue(netId, out actor);

        public int? NetIdOf(IActorRef actor)
            => actor != null && _netIdByActor.TryGetValue(actor, out var id) ? id : (int?)null;

        /// <summary>Remove an actor from the registry (death / despawn). Idempotent.</summary>
        public void Remove(int netId)
        {
            if (_byNetId.TryGetValue(netId, out var actor))
            {
                _byNetId.Remove(netId);
                _netIdByActor.Remove(actor);
            }
        }

        public void RemoveActor(IActorRef actor)
        {
            if (actor != null && _netIdByActor.TryGetValue(actor, out var id))
            {
                _netIdByActor.Remove(actor);
                _byNetId.Remove(id);
            }
        }

        public int Count => _byNetId.Count;

        /// <summary>Clear all mappings + reset the mint counter (mission exit / re-deploy).</summary>
        public void Clear()
        {
            _byNetId.Clear();
            _netIdByActor.Clear();
            _nextMintedId = MintBase;
        }

        /// <summary>Enumerate (netId, actor) pairs — for the host actorTable build / debug dumps.</summary>
        public IEnumerable<KeyValuePair<int, IActorRef>> Entries => _byNetId;
    }

    /// <summary>
    /// Engine-free actor abstraction the registry operates on. The production adapter wraps a live
    /// <c>TacticalActorBase</c> (GeoUnitId via the struct's int conversion, Position via <c>actor.Pos</c>);
    /// tests use a trivial struct. Keeps the registry core unit-testable with no UnityEngine dependency.
    /// </summary>
    public interface IActorRef
    {
        /// <summary>The actor's <c>GeoUnitId</c> as a raw int (0 == GeoTacUnitId.None == Pandoran/spawned).</summary>
        int GeoUnitId { get; }

        /// <summary>The actor's current world position (engine-free 3-float tuple).</summary>
        ActorPos Position { get; }
    }

    /// <summary>Engine-free 3D position (no UnityEngine.Vector3 dependency in the pure core/tests).</summary>
    public struct ActorPos
    {
        public float x, y, z;
        public ActorPos(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
}
