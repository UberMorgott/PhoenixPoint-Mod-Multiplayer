using System.Collections.Generic;
using Multipleer.Sync.Tactical;
using Xunit;

public class TacticalActorRegistryTests
{
    // Engine-free test actor: GeoUnitId + position, exactly the IActorRef abstraction the pure core needs.
    private sealed class FakeActor : IActorRef
    {
        public int GeoUnitId { get; }
        public ActorPos Position { get; set; }
        public FakeActor(int geoUnitId, float x = 0, float y = 0, float z = 0)
        {
            GeoUnitId = geoUnitId;
            Position = new ActorPos(x, y, z);
        }
    }

    [Fact]
    public void Soldier_NetId_EqualsGeoUnitId()
    {
        var reg = new TacticalActorRegistry();
        var soldier = new FakeActor(42, 1, 2, 3);
        int netId = reg.AssignHost(soldier);
        Assert.Equal(42, netId);
        Assert.True(reg.TryGet(42, out var got));
        Assert.Same(soldier, got);
        Assert.Equal(42, reg.NetIdOf(soldier));
    }

    [Fact]
    public void TwoPandorans_GetDistinctMintedIds_AtOrAboveMintBase()
    {
        var reg = new TacticalActorRegistry();
        var p1 = new FakeActor(0, 10, 0, 0);
        var p2 = new FakeActor(0, 20, 0, 0);
        int id1 = reg.AssignHost(p1);
        int id2 = reg.AssignHost(p2);

        Assert.NotEqual(id1, id2);
        Assert.True(id1 >= TacticalActorRegistry.MintBase);
        Assert.True(id2 >= TacticalActorRegistry.MintBase);
        Assert.Equal(TacticalActorRegistry.MintBase, id1);
        Assert.Equal(TacticalActorRegistry.MintBase + 1, id2);
    }

    [Fact]
    public void MintedId_NeverCollidesRealGeoUnitId()
    {
        var reg = new TacticalActorRegistry();
        var soldier = new FakeActor(MintBaseAdjacent());   // a (contrived) high real GeoUnitId
        var pandoran = new FakeActor(0);
        int sId = reg.AssignHost(soldier);
        int pId = reg.AssignHost(pandoran);
        Assert.NotEqual(sId, pId);     // mint skips an already-used id
    }

    private static int MintBaseAdjacent() => TacticalActorRegistry.MintBase; // soldier sitting exactly on the mint base

    [Fact]
    public void AssignHost_Idempotent_SameActorSameId()
    {
        var reg = new TacticalActorRegistry();
        var p = new FakeActor(0, 5, 5, 5);
        int first = reg.AssignHost(p);
        int second = reg.AssignHost(p);
        Assert.Equal(first, second);
        Assert.Equal(1, reg.Count);
    }

    [Fact]
    public void BuildActorTable_PreservesOrder_AndCapturesGeoIdAndPos()
    {
        var reg = new TacticalActorRegistry();
        var actors = new List<IActorRef>
        {
            new FakeActor(7, 1, 2, 3),
            new FakeActor(0, 4, 5, 6),   // pandoran → minted
            new FakeActor(9, 7, 8, 9),
        };
        var table = reg.BuildActorTable(actors);

        Assert.Equal(3, table.Count);
        Assert.Equal(7, table[0].NetId);
        Assert.Equal(7, table[0].GeoUnitId);
        Assert.Equal(1, table[0].X);
        Assert.Equal(TacticalActorRegistry.MintBase, table[1].NetId);
        Assert.Equal(0, table[1].GeoUnitId);
        Assert.Equal(4, table[1].X);
        Assert.Equal(9, table[2].NetId);
    }

    [Fact]
    public void MatchAndRegister_ReproducesHostMapping_MixedSet()
    {
        // HOST side: build a table from soldiers + pandorans.
        var host = new TacticalActorRegistry();
        var hostActors = new List<IActorRef>
        {
            new FakeActor(100, 0, 0, 0),     // soldier
            new FakeActor(0, 10, 0, 0),      // pandoran A
            new FakeActor(0, 20, 0, 0),      // pandoran B
            new FakeActor(101, 30, 0, 0),    // soldier
        };
        var table = host.BuildActorTable(hostActors);

        // CLIENT side: restored actors in a DIFFERENT enumeration order; soldiers keep GeoUnitId,
        // pandorans have GeoUnitId 0 + identical restored positions.
        var clientActors = new List<IActorRef>
        {
            new FakeActor(0, 20, 0, 0),      // pandoran B (pos 20)
            new FakeActor(101, 30, 0, 0),    // soldier 101
            new FakeActor(0, 10, 0, 0),      // pandoran A (pos 10)
            new FakeActor(100, 0, 0, 0),     // soldier 100
        };
        var client = new TacticalActorRegistry();
        int matched = client.MatchAndRegister(table, clientActors);

        Assert.Equal(4, matched);
        // Soldier 100 → netId 100, soldier 101 → netId 101.
        Assert.True(client.TryGet(100, out var s100));
        Assert.Equal(100, ((FakeActor)s100).GeoUnitId);
        Assert.True(client.TryGet(101, out var s101));
        Assert.Equal(101, ((FakeActor)s101).GeoUnitId);
        // Pandoran netIds (minted base + base+1) map to the actors at pos 10 and 20 respectively.
        Assert.True(client.TryGet(TacticalActorRegistry.MintBase, out var pA));
        Assert.Equal(10f, ((FakeActor)pA).Position.x);
        Assert.True(client.TryGet(TacticalActorRegistry.MintBase + 1, out var pB));
        Assert.Equal(20f, ((FakeActor)pB).Position.x);
    }

    [Fact]
    public void MatchAndRegister_PosTolerance_WithinEpsilonMatches()
    {
        var host = new TacticalActorRegistry();
        var table = host.BuildActorTable(new List<IActorRef> { new FakeActor(0, 5f, 5f, 5f) });

        // Restored pos jittered by < epsilon → still matches.
        var jitter = TacticalActorRegistry.PosEpsilon * 0.4f;
        var client = new TacticalActorRegistry();
        int matched = client.MatchAndRegister(table,
            new List<IActorRef> { new FakeActor(0, 5f + jitter, 5f, 5f) });
        Assert.Equal(1, matched);
    }

    [Fact]
    public void MatchAndRegister_PosTolerance_BeyondEpsilonDoesNotMatch()
    {
        var host = new TacticalActorRegistry();
        var table = host.BuildActorTable(new List<IActorRef> { new FakeActor(0, 5f, 5f, 5f) });

        var far = TacticalActorRegistry.PosEpsilon * 10f + 1f;
        var client = new TacticalActorRegistry();
        int matched = client.MatchAndRegister(table,
            new List<IActorRef> { new FakeActor(0, 5f + far, 5f, 5f) });
        Assert.Equal(0, matched);
    }

    [Fact]
    public void MatchAndRegister_GeoIdRowsResolveBeforePosRows_NoSoldierStolenByPandoran()
    {
        // A pandoran row sits AT a soldier's exact position; the soldier must still claim its own id-row,
        // and the pandoran must not steal the soldier (pass 1 id-match runs before pass 2 pos-match, and
        // pos-match only considers GeoUnitId==0 candidates).
        var host = new TacticalActorRegistry();
        var hostActors = new List<IActorRef>
        {
            new FakeActor(55, 9, 9, 9),   // soldier at (9,9,9)
            new FakeActor(0, 9, 9, 9),    // pandoran also at (9,9,9)
        };
        var table = host.BuildActorTable(hostActors);

        var client = new TacticalActorRegistry();
        int matched = client.MatchAndRegister(table, new List<IActorRef>
        {
            new FakeActor(0, 9, 9, 9),     // pandoran
            new FakeActor(55, 9, 9, 9),    // soldier
        });
        Assert.Equal(2, matched);
        Assert.True(client.TryGet(55, out var soldier));
        Assert.Equal(55, ((FakeActor)soldier).GeoUnitId);
        Assert.True(client.TryGet(TacticalActorRegistry.MintBase, out var pand));
        Assert.Equal(0, ((FakeActor)pand).GeoUnitId);
    }

    [Fact]
    public void Remove_DropsBothDirections()
    {
        var reg = new TacticalActorRegistry();
        var soldier = new FakeActor(7);
        reg.AssignHost(soldier);
        Assert.True(reg.TryGet(7, out _));

        reg.Remove(7);
        Assert.False(reg.TryGet(7, out _));
        Assert.Null(reg.NetIdOf(soldier));
        Assert.Equal(0, reg.Count);

        reg.Remove(7);  // idempotent
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void RemoveActor_DropsByActorRef()
    {
        var reg = new TacticalActorRegistry();
        var p = new FakeActor(0);
        int id = reg.AssignHost(p);
        reg.RemoveActor(p);
        Assert.False(reg.TryGet(id, out _));
        Assert.Null(reg.NetIdOf(p));
    }

    [Fact]
    public void Register_RebindingId_ReplacesActor()
    {
        var reg = new TacticalActorRegistry();
        var a = new FakeActor(7);
        var b = new FakeActor(7);
        reg.Register(5, a);
        reg.Register(5, b);   // same id, different actor → replace
        Assert.True(reg.TryGet(5, out var got));
        Assert.Same(b, got);
        Assert.Null(reg.NetIdOf(a));
        Assert.Equal(5, reg.NetIdOf(b));
    }

    [Fact]
    public void Clear_ResetsMappingsAndMintCounter()
    {
        var reg = new TacticalActorRegistry();
        reg.AssignHost(new FakeActor(0));
        reg.AssignHost(new FakeActor(0));
        reg.Clear();
        Assert.Equal(0, reg.Count);
        // After clear, the next minted id restarts at MintBase.
        int id = reg.AssignHost(new FakeActor(0));
        Assert.Equal(TacticalActorRegistry.MintBase, id);
    }

    // ─── TryLazyRebind: client lazy re-bind decision (deploy-drift / mid-mission spawn coverage) ───

    [Fact]
    public void TryLazyRebind_SoldierNetId_BindsByGeoUnitId_PreferredOverPosition()
    {
        // A soldier netId (< MintBase ⇒ the netId IS a GeoUnitId). The host record's position is where the
        // host soldier stands; a DIFFERENT unbound Pandoran happens to sit exactly at that position. The bind
        // MUST resolve by GeoUnitId-exact (the soldier, at its own — different — spot), NOT steal the Pandoran.
        var reg = new TacticalActorRegistry();
        var soldier = new FakeActor(200, 0f, 0f, 0f);          // GeoUnitId 200, at origin
        var pandoranAtRecPos = new FakeActor(0, 50f, 0f, 0f);  // unbound, sitting at the record position
        var candidates = new List<IActorRef> { pandoranAtRecPos, soldier };

        bool bound = reg.TryLazyRebind(200, hasPos: true, new ActorPos(50f, 0f, 0f), candidates);

        Assert.True(bound);
        Assert.True(reg.TryGet(200, out var got));
        Assert.Same(soldier, got);                  // GeoUnitId match, not the pos-coincident Pandoran
        Assert.Null(reg.NetIdOf(pandoranAtRecPos)); // Pandoran left untouched
    }

    [Fact]
    public void TryLazyRebind_MintedNetId_BindsByPosition_WithinEpsilon()
    {
        var reg = new TacticalActorRegistry();
        var jitter = TacticalActorRegistry.PosEpsilon * 0.4f;
        var pandoran = new FakeActor(0, 5f + jitter, 5f, 5f);  // unbound, within epsilon of the record pos
        var candidates = new List<IActorRef> { pandoran };

        bool bound = reg.TryLazyRebind(TacticalActorRegistry.MintBase, hasPos: true,
            new ActorPos(5f, 5f, 5f), candidates);

        Assert.True(bound);
        Assert.True(reg.TryGet(TacticalActorRegistry.MintBase, out var got));
        Assert.Same(pandoran, got);
    }

    [Fact]
    public void TryLazyRebind_MintedNetId_BeyondEpsilon_NoBind()
    {
        var reg = new TacticalActorRegistry();
        var far = TacticalActorRegistry.PosEpsilon * 10f + 1f;
        var pandoran = new FakeActor(0, 5f + far, 5f, 5f);     // too far → no match
        var candidates = new List<IActorRef> { pandoran };

        bool bound = reg.TryLazyRebind(TacticalActorRegistry.MintBase, hasPos: true,
            new ActorPos(5f, 5f, 5f), candidates);

        Assert.False(bound);
        Assert.False(reg.TryGet(TacticalActorRegistry.MintBase, out _));
        Assert.Null(reg.NetIdOf(pandoran));
    }

    [Fact]
    public void TryLazyRebind_SoldierNetId_NoGeoIdCandidate_NoBind_AndDoesNotStealPandoran()
    {
        // No live actor carries the soldier's GeoUnitId; only a Pandoran sits at the record position. A soldier
        // netId must NEVER fall back to a position steal → no bind, and the Pandoran stays unbound.
        var reg = new TacticalActorRegistry();
        var pandoranAtRecPos = new FakeActor(0, 1f, 1f, 1f);
        var candidates = new List<IActorRef> { pandoranAtRecPos };

        bool bound = reg.TryLazyRebind(300, hasPos: true, new ActorPos(1f, 1f, 1f), candidates);

        Assert.False(bound);
        Assert.False(reg.TryGet(300, out _));
        Assert.Null(reg.NetIdOf(pandoranAtRecPos));
    }

    [Fact]
    public void TryLazyRebind_MintedNetId_NoPosition_NoBind()
    {
        // A minted Pandoran netId with no position bit in the record cannot be position-matched → drop.
        var reg = new TacticalActorRegistry();
        var pandoran = new FakeActor(0, 0f, 0f, 0f);
        var candidates = new List<IActorRef> { pandoran };

        bool bound = reg.TryLazyRebind(TacticalActorRegistry.MintBase, hasPos: false,
            new ActorPos(0f, 0f, 0f), candidates);

        Assert.False(bound);
        Assert.False(reg.TryGet(TacticalActorRegistry.MintBase, out _));
    }

    [Fact]
    public void TryLazyRebind_EmptyCandidates_NoBind()
    {
        var reg = new TacticalActorRegistry();
        Assert.False(reg.TryLazyRebind(200, hasPos: true, new ActorPos(0, 0, 0), new List<IActorRef>()));
        Assert.False(reg.TryGet(200, out _));
    }
}
