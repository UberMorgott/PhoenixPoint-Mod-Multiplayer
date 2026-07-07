using System.Collections.Generic;
using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for the TS1 mid-battle actor SPAWN/DESPAWN mirror (surfaces 0x92/0x93). Covers:
///   (a) the <see cref="TacticalActorLifecycleCodec"/> wire round-trips (spawn: seq/netId/faction/pos + the two
///       length-prefixed blobs; despawn: seq/netId/reason) + truncation/garbage → clean drop (no partial accept),
///   (b) the pure host SPAWN-GATE decision (<see cref="TacticalActorLifecycleGate.ShouldBroadcastSpawn"/>) =
///       deploy-captured AND not-already-registered AND not-applying-remote,
///   (c) the pure host DESPAWN-SWEEP (<see cref="TacticalActorLifecycleGate.ComputeDespawnedNetIds"/>) =
///       a registered actor absent from the live set is flagged exactly once,
///   (d) the <see cref="TacticalActorRegistry"/> minting invariants TS1 relies on (post-deploy AssignHost mints
///       at/above MintBase; Register binds the host netId; Remove is idempotent; a re-minted id after Remove never
///       collides). The engine glue (TacticalActorLifecycleSync / ActorLifecyclePatches) is in-game verified.
/// </summary>
public class TacticalActorLifecycleCodecTests
{
    // ─── (a) SPAWN codec round-trip ────────────────────────────────────
    [Fact]
    public void Spawn_RoundTrips_AllFields()
    {
        var create = new byte[] { 1, 2, 3, 4, 5 };
        var inst = new byte[] { 9, 8, 7 };
        var bytes = TacticalActorLifecycleCodec.EncodeSpawn(
            new TacticalActorLifecycleCodec.SpawnPayload(
                seq: 7u, netId: 1_000_042, factionIndex: 2, px: 1.5f, py: -2.25f, pz: 3f,
                createBlob: create, instBlob: inst));

        Assert.True(TacticalActorLifecycleCodec.TryDecodeSpawn(bytes, out var p));
        Assert.Equal(7u, p.Seq);
        Assert.Equal(1_000_042, p.NetId);
        Assert.Equal(2, p.FactionIndex);
        Assert.Equal(1.5f, p.PosX);
        Assert.Equal(-2.25f, p.PosY);
        Assert.Equal(3f, p.PosZ);
        Assert.Equal(create, p.CreateBlob);   // byte-identical blob survives
        Assert.Equal(inst, p.InstBlob);
    }

    [Fact]
    public void Spawn_RoundTrips_EmptyBlobs()
    {
        var bytes = TacticalActorLifecycleCodec.EncodeSpawn(
            new TacticalActorLifecycleCodec.SpawnPayload(1u, 5, -1, 0f, 0f, 0f, null, null));
        Assert.True(TacticalActorLifecycleCodec.TryDecodeSpawn(bytes, out var p));
        Assert.Empty(p.CreateBlob);           // null blob → empty (never null) on decode
        Assert.Empty(p.InstBlob);
        Assert.Equal(-1, p.FactionIndex);     // diagnostic "unknown faction" sentinel survives
    }

    [Fact]
    public void Spawn_RejectsTruncated_AndGarbage()
    {
        Assert.False(TacticalActorLifecycleCodec.TryDecodeSpawn(null, out _));
        Assert.False(TacticalActorLifecycleCodec.TryDecodeSpawn(new byte[3], out _));   // shorter than the header
        // A valid frame chopped mid-blob → clean reject (length-prefix says more bytes than remain).
        var bytes = TacticalActorLifecycleCodec.EncodeSpawn(
            new TacticalActorLifecycleCodec.SpawnPayload(1u, 5, 0, 0f, 0f, 0f, new byte[] { 1, 2, 3, 4 }, new byte[] { 5 }));
        var chopped = new byte[bytes.Length - 3];
        System.Array.Copy(bytes, chopped, chopped.Length);
        Assert.False(TacticalActorLifecycleCodec.TryDecodeSpawn(chopped, out _));
    }

    [Fact]
    public void Spawn_RejectsCorruptBlobLength()
    {
        // Header (u32 seq + i32 netId + i32 faction + 3×f32 pos = 24 bytes) then a bogus huge createLen with no data.
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u); w.Write(5); w.Write(0); w.Write(0f); w.Write(0f); w.Write(0f);
            w.Write(int.MaxValue);   // createLen far exceeds the remaining buffer → guarded reject
            Assert.False(TacticalActorLifecycleCodec.TryDecodeSpawn(ms.ToArray(), out _));
        }
    }

    // ─── (a) DESPAWN codec round-trip ──────────────────────────────────
    [Theory]
    [InlineData(TacticalActorLifecycleCodec.ReasonRemoved)]
    [InlineData(TacticalActorLifecycleCodec.ReasonEvacuated)]
    [InlineData(TacticalActorLifecycleCodec.ReasonMorphed)]
    [InlineData(TacticalActorLifecycleCodec.ReasonRetrieved)]
    [InlineData(TacticalActorLifecycleCodec.ReasonRefreshed)]   // gap-turret-crate-loot content refresh
    public void Despawn_RoundTrips(byte reason)
    {
        var bytes = TacticalActorLifecycleCodec.EncodeDespawn(seq: 42u, netId: 1_000_003, reason: reason);
        Assert.True(TacticalActorLifecycleCodec.TryDecodeDespawn(bytes, out var p));
        Assert.Equal(42u, p.Seq);
        Assert.Equal(1_000_003, p.NetId);
        Assert.Equal(reason, p.Reason);
    }

    [Fact]
    public void Despawn_RejectsTruncated()
    {
        Assert.False(TacticalActorLifecycleCodec.TryDecodeDespawn(null, out _));
        Assert.False(TacticalActorLifecycleCodec.TryDecodeDespawn(new byte[8], out _));   // needs 9 (u32+i32+u8)
        var bytes = TacticalActorLifecycleCodec.EncodeDespawn(1u, 2, 0);
        Assert.Equal(9, bytes.Length);
        Assert.True(TacticalActorLifecycleCodec.TryDecodeDespawn(bytes, out _));
    }

    // ─── (b) pure SPAWN-GATE decision ──────────────────────────────────
    [Fact]
    public void ShouldBroadcastSpawn_True_OnlyWhen_DeployCaptured_NotRegistered_NotApplying()
    {
        // The one TRUE case: deploy captured, actor not already registered, not inside a remote apply.
        Assert.True(TacticalActorLifecycleGate.ShouldBroadcastSpawn(deployCaptured: true, alreadyRegistered: false, applyingRemote: false));
        // Every negation is FALSE.
        Assert.False(TacticalActorLifecycleGate.ShouldBroadcastSpawn(false, false, false));  // deploy not captured yet → deploy-time actor
        Assert.False(TacticalActorLifecycleGate.ShouldBroadcastSpawn(true, true, false));    // already registered → not a new spawn
        Assert.False(TacticalActorLifecycleGate.ShouldBroadcastSpawn(true, false, true));    // inside a remote apply → client materialize, never re-emit
    }

    // ─── (c) pure DESPAWN-SWEEP decision ───────────────────────────────
    [Fact]
    public void ComputeDespawnedNetIds_FlagsRegisteredButAbsent_Once()
    {
        var a = new object(); var b = new object(); var c = new object();
        var registered = new List<KeyValuePair<int, object>>
        {
            new KeyValuePair<int, object>(1_000_001, a),   // live
            new KeyValuePair<int, object>(1_000_002, b),   // GONE from the map → despawned
            new KeyValuePair<int, object>(7, c),           // live soldier
        };
        var live = new HashSet<object> { a, c };           // b left play

        var despawned = TacticalActorLifecycleGate.ComputeDespawnedNetIds(registered, live);
        Assert.Equal(new List<int> { 1_000_002 }, despawned);   // exactly the absent one, exactly once
    }

    [Fact]
    public void ComputeDespawnedNetIds_AllLive_Empty_And_NullLiveSet_AllDespawned()
    {
        var a = new object(); var b = new object();
        var registered = new List<KeyValuePair<int, object>>
        {
            new KeyValuePair<int, object>(1, a),
            new KeyValuePair<int, object>(2, b),
        };
        Assert.Empty(TacticalActorLifecycleGate.ComputeDespawnedNetIds(registered, new HashSet<object> { a, b }));
        // Defensive: a null live set treats everything as despawned.
        Assert.Equal(new List<int> { 1, 2 }, TacticalActorLifecycleGate.ComputeDespawnedNetIds(registered, null));
    }

    // ─── (c') gap-turret-crate-loot: retrieve→despawn pairing + container content refresh ──────────

    [Fact]
    public void RetrievedTurret_LeavesLiveSet_IsSweptExactlyOnce()
    {
        // A relayed RetrieveDeployedItemAbility ends in RemoveActorEffect → ActorSpawner.DestroyActor
        // (RemoveActorEffect.cs:28): the turret leaves the live map set WITHOUT a death report, so the 0x93
        // pairing is the SWEEP flagging its registered netId exactly once — soldiers stay untouched.
        var soldier = new object(); var turret = new object();
        var registered = new List<KeyValuePair<int, object>>
        {
            new KeyValuePair<int, object>(3, soldier),           // deploy-time soldier — live
            new KeyValuePair<int, object>(1_000_010, turret),    // TS1-minted turret — retrieved (destroyed)
        };
        var live = new HashSet<object> { soldier };

        var swept = TacticalActorLifecycleGate.ComputeDespawnedNetIds(registered, live);
        Assert.Equal(new List<int> { 1_000_010 }, swept);

        // Second sweep after the registry removed it → nothing re-fires (pairing is one-shot).
        registered.RemoveAt(1);
        Assert.Empty(TacticalActorLifecycleGate.ComputeDespawnedNetIds(registered, live));
    }

    [Fact]
    public void ContainerContentRefresh_ReusesSpawnCodec_SameNetId_NewBlob()
    {
        // gap-turret-crate-loot: a relayed DROP re-broadcasts the ground container at the SAME netId —
        // despawn(ReasonRefreshed) then a spawn whose inst blob now CARRIES the item. Pure codec reuse: both
        // frames round-trip, the netId pairs up, and the refreshed blob is byte-identical.
        const int netId = 1_000_021;
        var despawn = TacticalActorLifecycleCodec.EncodeDespawn(seq: 10u, netId: netId,
            reason: TacticalActorLifecycleCodec.ReasonRefreshed);
        var refreshedInst = new byte[] { 0xB1, 0x0B, 0x42, 0x42 };   // container blob WITH the dropped item
        var respawn = TacticalActorLifecycleCodec.EncodeSpawn(
            new TacticalActorLifecycleCodec.SpawnPayload(seq: 11u, netId: netId, factionIndex: -1,
                px: 4f, py: 0f, pz: 9f, createBlob: new byte[] { 1 }, instBlob: refreshedInst));

        Assert.True(TacticalActorLifecycleCodec.TryDecodeDespawn(despawn, out var d));
        Assert.True(TacticalActorLifecycleCodec.TryDecodeSpawn(respawn, out var s));
        Assert.Equal(TacticalActorLifecycleCodec.ReasonRefreshed, d.Reason);
        Assert.Equal(d.NetId, s.NetId);                  // the refresh pairs on ONE netId (no re-mint)
        Assert.Equal(refreshedInst, s.InstBlob);         // the new contents survive verbatim
        Assert.True(s.Seq > d.Seq);                      // despawn applies before the respawn (per-surface seqs)
    }

    // ─── (d) registry minting invariants TS1 relies on ─────────────────
    [Fact]
    public void PostDeploy_MintedSpawn_AtOrAboveMintBase_AndRegisterBindsHostNetId()
    {
        var reg = new TacticalActorRegistry();
        // A mid-battle Pandoran/turret spawn has GeoUnitId 0 → the HOST mints at/above MintBase.
        var spawnActor = new FakeActor(geoUnitId: 0, x: 1, y: 0, z: 1);
        int hostNetId = reg.AssignHost(spawnActor);
        Assert.True(hostNetId >= TacticalActorRegistry.MintBase);

        // CLIENT binds the SAME host netId to its own (freshly materialized) actor instance — no minting.
        var clientReg = new TacticalActorRegistry();
        var clientActor = new FakeActor(geoUnitId: 0, x: 1, y: 0, z: 1);
        clientReg.Register(hostNetId, clientActor);
        Assert.True(clientReg.TryGet(hostNetId, out var bound));
        Assert.Same(clientActor, bound);
    }

    [Fact]
    public void Remove_IsIdempotent_And_ReMintedIdNeverCollides()
    {
        var reg = new TacticalActorRegistry();
        int id1 = reg.AssignHost(new FakeActor(0, 0, 0, 0));   // MintBase
        int id2 = reg.AssignHost(new FakeActor(0, 5, 0, 5));   // MintBase+1
        Assert.Equal(TacticalActorRegistry.MintBase, id1);
        Assert.Equal(TacticalActorRegistry.MintBase + 1, id2);

        reg.Remove(id1);
        reg.Remove(id1);   // idempotent — no throw, still gone
        Assert.False(reg.TryGet(id1, out _));

        // A NEW spawn after the remove mints a FRESH id that never collides the still-live id2 (the mint counter
        // does not rewind below the high-water mark).
        int id3 = reg.AssignHost(new FakeActor(0, 9, 0, 9));
        Assert.NotEqual(id2, id3);
        Assert.True(id3 >= TacticalActorRegistry.MintBase);
    }

    // Engine-free test actor: GeoUnitId + position, exactly the IActorRef abstraction the registry operates on.
    private sealed class FakeActor : IActorRef
    {
        public int GeoUnitId { get; }
        public ActorPos Position { get; }
        public FakeActor(int geoUnitId, float x, float y, float z)
        {
            GeoUnitId = geoUnitId;
            Position = new ActorPos(x, y, z);
        }
    }
}
