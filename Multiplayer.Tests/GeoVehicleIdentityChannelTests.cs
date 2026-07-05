using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync.State;
using Xunit;

// Inc4 S4 mid-session vehicle-creation channel (#6) — pure tests for the wire codec (GeoVehicleIdentitySnapshot),
// the host new-key poll-diff state machine (GeoVehicleIdentityTracker), and the client spawn-idempotence
// predicate. The engine glue (GeoVehicleChannel host-detect/client-spawn, GeoVehicleIdentityReflection) is
// game-bound and in-game-gated; these lock the pure contracts: wire round-trip, "new key exactly once" detection,
// re-create re-emits, and "never spawn a duplicate of a live vehicle".
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

    // ─── host new-key poll-diff detection ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tracker_TryMarkNew_TrueOnceThenKnown()
    {
        var t = new GeoVehicleIdentityTracker();
        var id = Id(3, 9, "fac", "set");

        Assert.True(t.TryMarkNew(id));    // first sighting → new
        Assert.True(t.IsKnown(id.Key));
        Assert.False(t.TryMarkNew(id));   // second sighting → not new (already known)
        Assert.Equal(1, t.PendingCount);  // queued exactly once
    }

    [Fact]
    public void Tracker_DrainPending_EmptiesQueueAndFlushesOnce()
    {
        var t = new GeoVehicleIdentityTracker();
        var a = Id(1, 1, "fA", "sA");
        var b = Id(2, 1, "fB", "sB");
        t.TryMarkNew(a);
        t.TryMarkNew(b);

        var drained = t.DrainPending();
        Assert.Equal(2, drained.Count);
        Assert.Contains(a, drained);
        Assert.Contains(b, drained);

        Assert.Empty(t.DrainPending());   // idempotent flush: nothing pending after a drain
        Assert.True(t.IsKnown(a.Key));    // still KNOWN (won't re-emit while live)
        Assert.True(t.IsKnown(b.Key));
    }

    [Fact]
    public void Tracker_MarkKnown_SeedsWithoutQueuing()
    {
        var t = new GeoVehicleIdentityTracker();
        var id = Id(4, 2, "fac", "set");

        t.MarkKnown(id.Key);              // bind-time seed: already on clients via the join save
        Assert.True(t.IsKnown(id.Key));
        Assert.Equal(0, t.PendingCount);  // seeded, NOT broadcast
        Assert.False(t.TryMarkNew(id));   // and therefore never treated as new
        Assert.Empty(t.DrainPending());
    }

    [Fact]
    public void Tracker_Prune_RecreatedKeyReEmits()
    {
        var t = new GeoVehicleIdentityTracker();
        var id = Id(7, 3, "fac", "set");
        t.TryMarkNew(id);
        t.DrainPending();                                 // known, nothing pending

        t.Prune(new HashSet<long>());                     // vehicle gone (empty live set) → forget it
        Assert.False(t.IsKnown(id.Key));

        Assert.True(t.TryMarkNew(id));                    // re-created same key → emits again
        Assert.Equal(1, t.PendingCount);
    }

    [Fact]
    public void Tracker_Prune_KeepsLiveKeys()
    {
        var t = new GeoVehicleIdentityTracker();
        var id = Id(8, 4, "fac", "set");
        t.TryMarkNew(id);
        t.Prune(new HashSet<long> { id.Key });            // still live → keep known
        Assert.True(t.IsKnown(id.Key));
    }

    // ─── client apply-idempotence ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldSpawn_OnlyWhenKeyNotAlreadyLive()
    {
        var id = Id(9, 5, "fac", "set");
        var live = new HashSet<long> { id.Key };

        Assert.False(GeoVehicleIdentityTracker.ShouldSpawn(id.Key, live));          // present → no duplicate spawn
        Assert.True(GeoVehicleIdentityTracker.ShouldSpawn(id.Key, new HashSet<long>())); // absent → spawn
        Assert.True(GeoVehicleIdentityTracker.ShouldSpawn(id.Key, null));           // null live set → spawn (best-effort)
    }
}
