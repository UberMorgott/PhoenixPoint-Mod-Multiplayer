using System.IO;
using System.Linq;
using System.Text;
using Multiplayer.Network.Sync.State;
using Xunit;

// PS1 vehicle-crew tail on the mid-session vehicle channel (#6) — pure tests for the optional crew
// block of GeoVehicleIdentitySnapshot (2026-07-05 personnel-sync spec §2.3): round-trip (keys +
// ordered GeoUnitIds), the LEGACY BYTE PIN (a no-crew payload must stay byte-identical to the
// pre-PS1 wire, and a pre-PS1 payload must decode with Crew empty), truncation rejection, and the
// parse-known-then-skip contract for unknown future tail bits. The engine glue (GeoVehicleChannel
// crew poll/apply, PersonnelReflection) is game-bound and in-game-gated.
public class GeoVehicleCrewTailTests
{
    private static GeoVehicleIdentity Id(int owner, int veh, string facGuid, string setGuid)
        => new GeoVehicleIdentity(owner, veh, facGuid, setGuid,
                                  0.1f, 0.2f, 0.3f, 0.927362f, 12.5f, -3.25f, 88.0f);

    /// <summary>The PRE-PS1 #6 wire for one snapshot (identities + tombstones, no crew block) — the
    /// legacy layout the byte pin asserts against.</summary>
    private static byte[] LegacyEncode(GeoVehicleIdentitySnapshot snap)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)snap.Vehicles.Count);
            foreach (var v in snap.Vehicles)
            {
                w.Write(v.OwnerId);
                w.Write(v.VehicleId);
                var fac = Encoding.UTF8.GetBytes(v.OwnerFactionDefGuid);
                w.Write((ushort)fac.Length); w.Write(fac);
                var set = Encoding.UTF8.GetBytes(v.VehicleSetDefGuid);
                w.Write((ushort)set.Length); w.Write(set);
                w.Write(v.QX); w.Write(v.QY); w.Write(v.QZ); w.Write(v.QW);
                w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            }
            w.Write((ushort)snap.Tombstones.Count);
            foreach (var key in snap.Tombstones) w.Write(key);
            return ms.ToArray();
        }
    }

    // ─── crew round-trip ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Crew_RoundTrip_PreservesKeysAndOrderedUnits()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Vehicles.Add(Id(1, 1, "fac", "set"));
        snap.Crew.Add(new GeoVehicleCrewRecord(GeoVehiclePos.MakeKey(1, 1), new long[] { 7, 3, 42 }));
        snap.Crew.Add(new GeoVehicleCrewRecord(GeoVehiclePos.MakeKey(9, 5), new long[] { 1000000007L }));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Equal(snap.Crew.Count, outSnap.Crew.Count);
        for (int i = 0; i < snap.Crew.Count; i++)
            Assert.Equal(snap.Crew[i], outSnap.Crew[i]);   // structural equality: key + ids + ORDER
    }

    [Fact]
    public void Crew_RoundTrip_CrewOnlyPayload()
    {
        // A flush can carry crew alone (no new residents/tombstones) — a pre-existing craft's re-crew.
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Crew.Add(new GeoVehicleCrewRecord(GeoVehiclePos.MakeKey(2, 3), new long[] { 11, 12 }));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Empty(outSnap.Vehicles);
        Assert.Empty(outSnap.Tombstones);
        Assert.Single(outSnap.Crew);
        Assert.Equal(new long[] { 11, 12 }, outSnap.Crew[0].UnitIds);
    }

    [Fact]
    public void Crew_RoundTrip_EmptyCrew_IsHonestNotSkipped()
    {
        // Empty ids = "craft holds no soldiers" (the last one left) — must survive the wire.
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Crew.Add(new GeoVehicleCrewRecord(GeoVehiclePos.MakeKey(4, 4), new long[0]));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Crew);
        Assert.Empty(outSnap.Crew[0].UnitIds);
    }

    // ─── legacy byte pins (backward-tolerant wire) ───────────────────────────────────────────────────────────

    [Fact]
    public void Encode_NoCrew_ByteIdenticalToPrePs1Wire()
    {
        // The crew block is written ONLY when carried: a no-crew payload must stay byte-for-byte the
        // pre-PS1 #6 wire (existing pins + any older decoder hold).
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Vehicles.Add(Id(11, 7, "fac-guid-A", "set-guid-A"));
        snap.Vehicles.Add(Id(-22, 42, "fac-guid-B", "set-guid-B"));
        snap.Tombstones.Add(GeoVehiclePos.MakeKey(7, 3));

        Assert.Equal(LegacyEncode(snap), GeoVehicleIdentitySnapshot.Encode(snap));
    }

    [Fact]
    public void Decode_PrePs1Wire_DecodesWithEmptyCrew()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Vehicles.Add(Id(1, 1, "fac", "set"));
        snap.Tombstones.Add(GeoVehiclePos.MakeKey(5, 9));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(LegacyEncode(snap));

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Vehicles);
        Assert.Single(outSnap.Tombstones);
        Assert.Empty(outSnap.Crew);
    }

    // ─── malformed payloads ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_TruncatedCrewBlock_ReturnsNull()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Crew.Add(new GeoVehicleCrewRecord(GeoVehiclePos.MakeKey(2, 3), new long[] { 11, 12 }));
        byte[] wire = GeoVehicleIdentitySnapshot.Encode(snap);
        byte[] truncated = wire.Take(wire.Length - 4).ToArray();   // clip into the last i64 unit id
        Assert.Null(GeoVehicleIdentitySnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_UnknownFlagCrewRecord_SkippedViaRecLen()
    {
        // A record whose flags carry ONLY unknown (future, higher) bits is skipped whole via recLen;
        // a known crew record alongside still parses (per-record degradation, never a payload reject).
        long knownKey = GeoVehiclePos.MakeKey(1, 1);
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)0);            // vehicle count
            w.Write((ushort)0);            // tombstone count
            w.Write((ushort)2);            // crew count
            w.Write(GeoVehiclePos.MakeKey(9, 9));
            w.Write((ushort)3);            // recLen
            w.Write(new byte[] { 0x02, 0xAB, 0xCD });   // unknown bit1 + opaque payload
            w.Write(knownKey);
            w.Write((ushort)11);           // recLen: flags(1) + n(2) + 1×i64(8)
            w.Write((byte)0x01);           // TailHasCrew
            w.Write((ushort)1);
            w.Write(42L);
            wire = ms.ToArray();
        }

        var outSnap = GeoVehicleIdentitySnapshot.Decode(wire);

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Crew);
        Assert.Equal(knownKey, outSnap.Crew[0].Key);
        Assert.Equal(new long[] { 42 }, outSnap.Crew[0].UnitIds);
    }

    // ─── host poll change-detect helper ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SameCrew_OrderedEquality_NullEqualsEmpty()
    {
        Assert.True(GeoVehicleCrew.SameCrew(null, new long[0]));        // "never observed" ≡ "observed empty" (no dirty oscillation)
        Assert.True(GeoVehicleCrew.SameCrew(new long[] { 1, 2 }, new long[] { 1, 2 }));
        Assert.False(GeoVehicleCrew.SameCrew(new long[] { 1, 2 }, new long[] { 2, 1 }));   // order = wire truth
        Assert.False(GeoVehicleCrew.SameCrew(new long[] { 1 }, new long[] { 1, 2 }));
        Assert.False(GeoVehicleCrew.SameCrew(null, new long[] { 1 }));
    }
}
