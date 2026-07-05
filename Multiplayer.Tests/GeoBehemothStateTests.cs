using Multiplayer.Network.Sync.State;
using Xunit;

// WA-1 behemoth mirror — pure convention tests. The behemoth rides the EXISTING surfaces under one reserved
// composite key: 0xA5 placement records + channel-#6 sentinel identities (presence/status/tombstone). These
// pin the key derivation, the sentinel identity round-trip through the UNCHANGED #6 wire codec, the status
// display mapping, and the tracker upsert semantics (first-sighting emit, silent value refresh, tombstone on
// prune, re-emerge clears the tombstone). The game-bound glue (GeoBehemothReflection spawn/stamp/despawn) is
// in-game verified.
public class GeoBehemothStateTests
{
    private static GeoVehiclePos Placement(float seed = 1f)
        => new GeoVehiclePos(GeoBehemothState.OwnerId, GeoBehemothState.VehicleId,
                             10f * seed, 20f * seed, 30f * seed, 0.1f * seed, 0.2f * seed, 0.3f * seed, 0.9f);

    // ─── reserved key ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Key_IsCompositeOfSentinelOwnerHashAndFixedId()
    {
        Assert.Equal(GeoVehiclePos.StableOwnerKey("__behemoth"), GeoBehemothState.OwnerId);
        Assert.Equal(GeoVehiclePos.MakeKey(GeoBehemothState.OwnerId, 1), GeoBehemothState.Key);
        Assert.True(GeoBehemothState.IsBehemothKey(GeoBehemothState.Key));
        Assert.False(GeoBehemothState.IsBehemothKey(GeoVehiclePos.MakeKey(GeoBehemothState.OwnerId, 2)));
        Assert.False(GeoBehemothState.IsBehemothKey(GeoVehiclePos.MakeKey(12345, 1)));
    }

    [Fact]
    public void PlacementRecord_CarriesTheReservedKey()
    {
        Assert.Equal(GeoBehemothState.Key, Placement().Key);
    }

    // ─── sentinel identity + wire round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void MakeIdentity_IsSentinel_AndStatusRoundTrips_ForEveryStatus()
    {
        for (byte s = GeoBehemothState.StatusNone; s <= GeoBehemothState.StatusDead; s++)
        {
            var id = GeoBehemothState.MakeIdentity(s, Placement());
            Assert.True(GeoBehemothState.IsBehemothIdentity(id));
            Assert.Equal(GeoBehemothState.Key, id.Key);
            Assert.True(GeoBehemothState.TryParseStatus(id, out byte parsed));
            Assert.Equal(s, parsed);
        }
    }

    [Fact]
    public void VehicleIdentity_IsNotSentinel_AndParseFailsClosed()
    {
        var vehicle = new GeoVehicleIdentity(77, 3, "real-faction-guid", "real-set-guid",
                                             0f, 0f, 0f, 1f, 0f, 0f, 0f);
        Assert.False(GeoBehemothState.IsBehemothIdentity(vehicle));
        Assert.False(GeoBehemothState.TryParseStatus(vehicle, out _));
        // Malformed sentinel digit → fail closed (apply skipped, healed by re-emission).
        var malformed = new GeoVehicleIdentity(GeoBehemothState.OwnerId, GeoBehemothState.VehicleId,
                                               GeoBehemothState.OwnerSentinel, "not-a-digit",
                                               0f, 0f, 0f, 1f, 0f, 0f, 0f);
        Assert.False(GeoBehemothState.TryParseStatus(malformed, out _));
    }

    [Fact]
    public void SentinelIdentity_SurvivesTheUnchangedChannel6WireCodec()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Vehicles.Add(GeoBehemothState.MakeIdentity(GeoBehemothState.StatusMoving, Placement(2f)));
        snap.Tombstones.Add(GeoBehemothState.Key);
        var decoded = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));
        Assert.NotNull(decoded);
        var id = Assert.Single(decoded.Vehicles);
        Assert.True(GeoBehemothState.IsBehemothIdentity(id));
        Assert.True(GeoBehemothState.TryParseStatus(id, out byte status));
        Assert.Equal(GeoBehemothState.StatusMoving, status);
        Assert.Equal(Placement(2f).QX, id.QX);
        Assert.Equal(Placement(2f).X, id.X);
        Assert.Equal(GeoBehemothState.Key, Assert.Single(decoded.Tombstones));
    }

    // ─── status display mapping (native contract) ──────────────────────────────────────────────────────

    [Fact]
    public void VisualsVisible_HiddenOnlyWhenDormantOrDead()
    {
        Assert.True(GeoBehemothState.VisualsVisible(GeoBehemothState.StatusNone));
        Assert.True(GeoBehemothState.VisualsVisible(GeoBehemothState.StatusIdle));
        Assert.True(GeoBehemothState.VisualsVisible(GeoBehemothState.StatusMoving));
        Assert.False(GeoBehemothState.VisualsVisible(GeoBehemothState.StatusDormant));
        Assert.False(GeoBehemothState.VisualsVisible(GeoBehemothState.StatusDead));
    }

    [Fact]
    public void AnimatorState_MovingIsOne_EverythingElseIdle()
    {
        Assert.Equal(1, GeoBehemothState.AnimatorState(GeoBehemothState.StatusMoving));
        Assert.Equal(0, GeoBehemothState.AnimatorState(GeoBehemothState.StatusNone));
        Assert.Equal(0, GeoBehemothState.AnimatorState(GeoBehemothState.StatusIdle));
        Assert.Equal(0, GeoBehemothState.AnimatorState(GeoBehemothState.StatusDormant));
        Assert.Equal(0, GeoBehemothState.AnimatorState(GeoBehemothState.StatusDead));
    }

    // ─── tracker upsert semantics (presence / refresh / tombstone / re-emerge) ─────────────────────────

    [Fact]
    public void UpsertResident_FirstSightingTrue_RefreshSilent_ValueUpdatedInPlace()
    {
        var tracker = new GeoVehicleIdentityTracker();
        Assert.True(tracker.UpsertResident(GeoBehemothState.MakeIdentity(GeoBehemothState.StatusIdle, Placement(1f))));
        Assert.False(tracker.UpsertResident(GeoBehemothState.MakeIdentity(GeoBehemothState.StatusIdle, Placement(1f))));
        // Value refresh (status/placement) is silent — the caller decides dirtiness — but the resident set
        // always carries the FRESHEST identity for the next flush.
        Assert.False(tracker.UpsertResident(GeoBehemothState.MakeIdentity(GeoBehemothState.StatusMoving, Placement(3f))));
        var resident = Assert.Single(tracker.GetResident());
        Assert.True(GeoBehemothState.TryParseStatus(resident, out byte status));
        Assert.Equal(GeoBehemothState.StatusMoving, status);
        Assert.Equal(1, tracker.ResidentCount);
    }

    [Fact]
    public void Prune_TombstonesTheBehemothKey_AndUpsertAfterwardsClearsIt()
    {
        var tracker = new GeoVehicleIdentityTracker();
        tracker.UpsertResident(GeoBehemothState.MakeIdentity(GeoBehemothState.StatusMoving, Placement()));
        // Behemoth removed on the host → its key leaves the live set → tombstone (client despawn ships).
        Assert.True(tracker.Prune(new long[0]));
        Assert.Equal(GeoBehemothState.Key, Assert.Single(tracker.GetTombstones()));
        Assert.Empty(tracker.GetResident());
        // A later behemoth (new FS arc) re-emerges under the SAME reserved key → spawn supersedes despawn.
        Assert.True(tracker.UpsertResident(GeoBehemothState.MakeIdentity(GeoBehemothState.StatusNone, Placement())));
        Assert.Empty(tracker.GetTombstones());
        Assert.Single(tracker.GetResident());
    }

    [Fact]
    public void Prune_KeepsBehemothWhileItsKeyIsLive_NoRegressForVehicleKeys()
    {
        var tracker = new GeoVehicleIdentityTracker();
        tracker.UpsertResident(GeoBehemothState.MakeIdentity(GeoBehemothState.StatusIdle, Placement()));
        var vehicle = new GeoVehicleIdentity(77, 3, "fac", "set", 0f, 0f, 0f, 1f, 0f, 0f, 0f);
        tracker.TryMarkNew(vehicle);
        // Both keys live → nothing pruned (the walk adds the behemoth key to liveKeys while it is alive).
        Assert.False(tracker.Prune(new[] { GeoBehemothState.Key, vehicle.Key }));
        // Vehicle gone, behemoth still live → only the vehicle tombstones.
        Assert.True(tracker.Prune(new[] { GeoBehemothState.Key }));
        Assert.Equal(vehicle.Key, Assert.Single(tracker.GetTombstones()));
        Assert.Equal(GeoBehemothState.Key, Assert.Single(tracker.GetResident()).Key);
    }
}
