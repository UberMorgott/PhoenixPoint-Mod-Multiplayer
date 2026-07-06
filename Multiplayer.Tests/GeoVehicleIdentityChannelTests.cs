using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync.State;
using Xunit;

// Inc4 S4 mid-session vehicle-creation channel (#6) — pure tests for the wire codec (GeoVehicleIdentitySnapshot),
// the host vehicle-lifecycle state machine (GeoVehicleIdentityTracker), and the client spawn/despawn-idempotence
// predicates. The engine glue (GeoVehicleChannel host-detect/client-spawn, GeoVehicleIdentityReflection) is
// game-bound and in-game-gated; these lock the pure contracts: wire round-trip (identities + tombstones),
// "new key exactly once" detection, RESIDENT re-emission (unacked rail — a lost flush must heal), tombstone
// lifecycle (prune → despawn ships; re-create clears), "never spawn a duplicate of a live vehicle", "never
// despawn an absent key", and the reflection ready-gate (null faction-def prop must NOT latch ready).
public class GeoVehicleIdentityChannelTests
{
    private static GeoVehicleIdentity Id(int owner, int veh, string facGuid, string setGuid)
        => new GeoVehicleIdentity(owner, veh, facGuid, setGuid,
                                  0.1f, 0.2f, 0.3f, 0.927362f, 12.5f, -3.25f, 88.0f);

    // ─── wire codec ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Vehicles.Add(new GeoVehicleIdentity(11, 7, "fac-guid-A", "set-guid-A",
                                                 0.1f, 0.2f, 0.3f, 0.927362f, 1.5f, -2.25f, 3.75f));
        snap.Vehicles.Add(new GeoVehicleIdentity(-22, 42, "fac-guid-B", "set-guid-B",
                                                 0f, 0f, 0f, 1f, -100.5f, 0f, 50.125f));

        byte[] wire = GeoVehicleIdentitySnapshot.Encode(snap);
        var outSnap = GeoVehicleIdentitySnapshot.Decode(wire);

        Assert.NotNull(outSnap);
        Assert.Equal(snap.Vehicles.Count, outSnap.Vehicles.Count);
        for (int i = 0; i < snap.Vehicles.Count; i++)
            Assert.Equal(snap.Vehicles[i], outSnap.Vehicles[i]);   // struct equality (bit-exact floats + strings)
    }

    [Fact]
    public void RoundTrip_EmptyBatch()
    {
        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(new GeoVehicleIdentitySnapshot()));
        Assert.NotNull(outSnap);
        Assert.Empty(outSnap.Vehicles);
        Assert.Empty(outSnap.Tombstones);
    }

    [Fact]
    public void RoundTrip_PreservesTombstones()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Vehicles.Add(Id(1, 1, "fac", "set"));
        snap.Tombstones.Add(GeoVehiclePos.MakeKey(7, 3));
        snap.Tombstones.Add(GeoVehiclePos.MakeKey(-2, 42));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Vehicles);
        Assert.Equal(snap.Tombstones, outSnap.Tombstones);
    }

    [Fact]
    public void RoundTrip_TombstonesOnly()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Tombstones.Add(GeoVehiclePos.MakeKey(5, 9));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Empty(outSnap.Vehicles);
        Assert.Equal(snap.Tombstones, outSnap.Tombstones);
    }

    [Fact]
    public void Decode_TruncatedTombstoneSection_ReturnsNull()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Tombstones.Add(GeoVehiclePos.MakeKey(5, 9));
        byte[] wire = GeoVehicleIdentitySnapshot.Encode(snap);
        byte[] truncated = wire.Take(wire.Length - 4).ToArray();   // clip half the i64 key
        Assert.Null(GeoVehicleIdentitySnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_Truncated_ReturnsNull()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Vehicles.Add(Id(1, 1, "fac", "set"));
        byte[] wire = GeoVehicleIdentitySnapshot.Encode(snap);
        byte[] truncated = wire.Take(wire.Length - 4).ToArray();   // clip the trailing floats
        Assert.Null(GeoVehicleIdentitySnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_Null_ReturnsNull() => Assert.Null(GeoVehicleIdentitySnapshot.Decode(null));

    // ─── composite key alignment (the spawned mirror must be resolvable by 0xA5/0xA6/0xA7) ──────────────────────

    [Fact]
    public void Key_MatchesPositionMirrorCompositeKey()
    {
        int owner = GeoVehiclePos.StableOwnerKey("PP_PhoenixFactionDef");
        var id = Id(owner, 5, "fac", "set");
        Assert.Equal(GeoVehiclePos.MakeKey(owner, 5), id.Key);
    }

    [Fact]
    public void Key_SameVehicleIdDifferentOwners_StayDistinct()
    {
        int a = GeoVehiclePos.StableOwnerKey("PP_PhoenixFactionDef");
        int b = GeoVehiclePos.StableOwnerKey("PP_SynedrionFactionDef");
        Assert.NotEqual(Id(a, 1, "fac", "set").Key, Id(b, 1, "fac", "set").Key);
    }

    // ─── host new-key poll-diff detection + RESIDENT re-emission ───────────────────────────────────────────────

    [Fact]
    public void Tracker_TryMarkNew_TrueOnceThenKnown()
    {
        var t = new GeoVehicleIdentityTracker();
        var id = Id(3, 9, "fac", "set");

        Assert.True(t.TryMarkNew(id));    // first sighting → new
        Assert.True(t.IsKnown(id.Key));
        Assert.False(t.TryMarkNew(id));   // second sighting → not new (already known)
        Assert.Equal(1, t.ResidentCount); // recorded exactly once
    }

    [Fact]
    public void Tracker_Resident_ReEmittedEveryFlush()
    {
        // Unacked rail: the identity set must survive a lost flush / failed client apply — GetResident is a
        // FULL re-emission every time, never a drain.
        var t = new GeoVehicleIdentityTracker();
        var a = Id(1, 1, "fA", "sA");
        var b = Id(2, 1, "fB", "sB");
        t.TryMarkNew(a);
        t.TryMarkNew(b);

        var flush1 = t.GetResident();
        Assert.Equal(2, flush1.Count);
        Assert.Contains(a, flush1);
        Assert.Contains(b, flush1);

        var flush2 = t.GetResident();     // second flush = SAME full set (resident, not drained)
        Assert.Equal(2, flush2.Count);
        Assert.Contains(a, flush2);
        Assert.Contains(b, flush2);
        Assert.True(t.HasPayload);
    }

    [Fact]
    public void Tracker_MarkKnown_SeedsWithoutEmitting()
    {
        var t = new GeoVehicleIdentityTracker();
        var id = Id(4, 2, "fac", "set");

        t.MarkKnown(id.Key);              // bind-time seed: already on clients via the join save
        Assert.True(t.IsKnown(id.Key));
        Assert.Equal(0, t.ResidentCount); // seeded, NOT broadcast
        Assert.False(t.TryMarkNew(id));   // and therefore never treated as new
        Assert.Empty(t.GetResident());
        Assert.False(t.HasPayload);
    }

    // ─── tombstones (host destroy → client despawn) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tracker_Prune_TombstonesDeadKeyAndDropsResident()
    {
        var t = new GeoVehicleIdentityTracker();
        var id = Id(7, 3, "fac", "set");
        t.TryMarkNew(id);

        Assert.True(t.Prune(new HashSet<long>()));        // vehicle gone → prune reports a change (dirty-mark)
        Assert.False(t.IsKnown(id.Key));
        Assert.Empty(t.GetResident());                    // identity no longer re-emitted
        Assert.Equal(new List<long> { id.Key }, t.GetTombstones());   // despawn ships instead
        Assert.True(t.HasPayload);                        // tombstone alone still flushes
    }

    [Fact]
    public void Tracker_Prune_TombstonesSeededKeyToo()
    {
        // A join-save-covered (seeded) vehicle the host later destroys must also despawn on the client.
        var t = new GeoVehicleIdentityTracker();
        t.MarkKnown(42L);
        Assert.True(t.Prune(new HashSet<long>()));
        Assert.Contains(42L, t.GetTombstones());
    }

    [Fact]
    public void Tracker_Prune_NoChange_ReturnsFalse()
    {
        var t = new GeoVehicleIdentityTracker();
        var id = Id(8, 4, "fac", "set");
        t.TryMarkNew(id);

        Assert.False(t.Prune(new HashSet<long> { id.Key }));   // still live → nothing pruned, no dirty-mark
        Assert.True(t.IsKnown(id.Key));
        Assert.Empty(t.GetTombstones());
    }

    [Fact]
    public void Tracker_RecreatedKey_ClearsTombstoneAndReEmits()
    {
        var t = new GeoVehicleIdentityTracker();
        var id = Id(7, 3, "fac", "set");
        t.TryMarkNew(id);
        t.Prune(new HashSet<long>());                     // destroyed → tombstoned

        Assert.True(t.TryMarkNew(id));                    // re-created same key → new again
        Assert.Empty(t.GetTombstones());                  // spawn supersedes the earlier despawn
        Assert.Single(t.GetResident());
    }

    [Fact]
    public void Tracker_TombstonesAndResident_ReEmittedTogether()
    {
        var t = new GeoVehicleIdentityTracker();
        var dead = Id(1, 1, "fA", "sA");
        var alive = Id(2, 1, "fB", "sB");
        t.TryMarkNew(dead);
        t.TryMarkNew(alive);
        t.Prune(new HashSet<long> { alive.Key });

        // Both sets re-emit on every flush; they stay disjoint by construction.
        for (int flush = 0; flush < 2; flush++)
        {
            Assert.Equal(new List<GeoVehicleIdentity> { alive }, t.GetResident());
            Assert.Equal(new List<long> { dead.Key }, t.GetTombstones());
        }
    }

    // ─── reload rebind (co-op F2 load / tactical round-trip: fresh GeoMap ⇒ AttachHost reset + reseed) ─────────

    [Fact]
    public void Tracker_RebindReset_DropsStaleStateAndReseedsWithoutEmitting()
    {
        // GeoVehicleChannel.AttachHost's instance-compare rebind path: a FRESH GeoMap means the pre-reload
        // known/resident/tombstone sets describe a DEAD level — a stale tombstone would despawn a craft that
        // EXISTS in the reloaded save on every flush. The rebind must Clear() then MarkKnown(live keys):
        // nothing stale left to emit, reloaded vehicles seeded (they ride the transferred save), and only
        // genuinely post-rebind creations become new.
        var t = new GeoVehicleIdentityTracker();
        var preReload = Id(1, 1, "fA", "sA");
        var destroyedPreReload = Id(2, 1, "fB", "sB");
        t.TryMarkNew(preReload);
        t.TryMarkNew(destroyedPreReload);
        t.Prune(new HashSet<long> { preReload.Key });     // destroyed before the reload → tombstoned

        // Reload an OLDER save where the "destroyed" craft exists again → rebind = reset + reseed.
        t.Clear();
        t.MarkKnown(preReload.Key);
        t.MarkKnown(destroyedPreReload.Key);

        Assert.False(t.HasPayload);                       // no stale resident/tombstone survives the rebind
        Assert.Empty(t.GetTombstones());                  // the stale despawn can never ship
        Assert.False(t.TryMarkNew(destroyedPreReload));   // reloaded craft = seeded, never re-emitted as "new"
        Assert.True(t.TryMarkNew(Id(3, 1, "fC", "sC")));  // genuinely new post-rebind craft still detected
    }

    // ─── client apply-idempotence (spawn + despawn decisions) ───────────────────────────────────────────────────

    [Fact]
    public void ShouldSpawn_OnlyWhenKeyNotAlreadyLive()
    {
        var id = Id(9, 5, "fac", "set");
        var live = new HashSet<long> { id.Key };

        Assert.False(GeoVehicleIdentityTracker.ShouldSpawn(id.Key, live));          // present → no duplicate spawn
        Assert.True(GeoVehicleIdentityTracker.ShouldSpawn(id.Key, new HashSet<long>())); // absent → spawn
        Assert.True(GeoVehicleIdentityTracker.ShouldSpawn(id.Key, null));           // null live set → spawn (best-effort)
    }

    [Fact]
    public void ShouldDespawn_OnlyWhenKeyLive()
    {
        long key = GeoVehiclePos.MakeKey(9, 5);
        var live = new HashSet<long> { key };

        Assert.True(GeoVehicleIdentityTracker.ShouldDespawn(key, live));                 // live → despawn
        Assert.False(GeoVehicleIdentityTracker.ShouldDespawn(key, new HashSet<long>())); // already gone → no-op
        Assert.False(GeoVehicleIdentityTracker.ShouldDespawn(key, null));                // no live set → no-op (safe)
    }

    // ─── reflection ready-gate (channel must not latch ready with a null faction-def property) ─────────────────

    [Fact]
    public void ReflectionReady_False_WhileFactionDefPropUnresolved()
    {
        // Early Ensure against an empty Factions list leaves the faction-def property null; latching ready then
        // would kill the channel for the whole process (owner guids never resolve). Must stay NOT ready → retry.
        Assert.False(GeoVehicleIdentityTracker.ReflectionReady(
            hasVehicleIdField: true, hasVehiclesProp: true, hasOwnerProp: true, hasFactionDefProp: false));
    }

    [Fact]
    public void ReflectionReady_RequiresAllCoreMembers()
    {
        Assert.True(GeoVehicleIdentityTracker.ReflectionReady(true, true, true, true));
        Assert.False(GeoVehicleIdentityTracker.ReflectionReady(false, true, true, true));
        Assert.False(GeoVehicleIdentityTracker.ReflectionReady(true, false, true, true));
        Assert.False(GeoVehicleIdentityTracker.ReflectionReady(true, true, false, true));
    }
}
