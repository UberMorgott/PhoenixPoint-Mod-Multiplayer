using System.Collections.Generic;
using Multiplayer.Network.Sync.State;
using Xunit;

// Inc4 S2 route-line metadata mirror (0xA6) — pure wire codec + change-signature tests for the
// GeoVehicleTravel surface. The engine glue (GeoVehicleTravelMirror) is game-bound and in-game verified; these
// lock the wire round-trip and the host's "unchanged → skip" travel-transition signature.
public class GeoVehicleTravelSnapshotTests
{
    [Fact]
    public void RoundTrip_PreservesSeqAndEveryField()
    {
        var input = new List<GeoVehicleTravelMeta>
        {
            new GeoVehicleTravelMeta(11, 7, travelling: true, currentSiteId: -1, destSiteIds: new[] { 5, 9, 2 }),
            new GeoVehicleTravelMeta(-22, 42, travelling: false, currentSiteId: 116, destSiteIds: new int[0]),
        };

        byte[] wire = GeoVehicleTravelSnapshot.Encode(123u, input);
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(wire, out uint seq, out var outList));

        Assert.Equal(123u, seq);
        Assert.Equal(input.Count, outList.Count);
        for (int i = 0; i < input.Count; i++)
            Assert.Equal(input[i], outList[i]);   // structural equality incl. the dest-id array
    }

    // Same VehicleID under two owners must stay distinct (per-faction VehicleID collision — the bug that broke
    // the 0xA5 mirror). The composite Key must differ so neither host sig cache nor client lookup collapses them.
    [Fact]
    public void RoundTrip_SameVehicleIdDifferentOwners_StayDistinct()
    {
        int ownerA = GeoVehiclePos.StableOwnerKey("PP_PhoenixFactionDef");
        int ownerB = GeoVehiclePos.StableOwnerKey("PP_SynedrionFactionDef");
        var input = new List<GeoVehicleTravelMeta>
        {
            new GeoVehicleTravelMeta(ownerA, 1, true, -1, new[] { 3 }),
            new GeoVehicleTravelMeta(ownerB, 1, true, -1, new[] { 4 }),
        };

        byte[] wire = GeoVehicleTravelSnapshot.Encode(1u, input);
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(wire, out _, out var outList));

        Assert.Equal(2, outList.Count);
        Assert.NotEqual(outList[0], outList[1]);
        Assert.NotEqual(outList[0].Key, outList[1].Key);   // shared key-space with GeoVehiclePos → composite key
        Assert.Equal(input[0], outList[0]);
        Assert.Equal(input[1], outList[1]);
    }

    [Fact]
    public void Encode_NullDestList_TreatedAsEmpty()
    {
        var input = new List<GeoVehicleTravelMeta> { new GeoVehicleTravelMeta(1, 1, false, -1, null) };
        byte[] wire = GeoVehicleTravelSnapshot.Encode(2u, input);
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(wire, out _, out var outList));
        Assert.Empty(outList[0].DestSiteIds);
    }

    [Fact]
    public void Encode_EmptyAndNullBatch_DecodeToZeroWithSeq()
    {
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(
            GeoVehicleTravelSnapshot.Encode(9u, new List<GeoVehicleTravelMeta>()), out uint s1, out var l1));
        Assert.Equal(9u, s1);
        Assert.Empty(l1);

        Assert.True(GeoVehicleTravelSnapshot.TryDecode(
            GeoVehicleTravelSnapshot.Encode(4u, null), out uint s2, out var l2));
        Assert.Equal(4u, s2);
        Assert.Empty(l2);
    }

    [Fact]
    public void TryDecode_Truncated_ReturnsFalse_NoPartialAccept()
    {
        byte[] wire = GeoVehicleTravelSnapshot.Encode(5u, new List<GeoVehicleTravelMeta>
        {
            new GeoVehicleTravelMeta(3, 1, true, -1, new[] { 7, 8 }),
        });
        var chopped = new byte[wire.Length - 4];   // drop the last dest id → declared count no longer fits
        System.Array.Copy(wire, chopped, chopped.Length);
        Assert.False(GeoVehicleTravelSnapshot.TryDecode(chopped, out _, out _));
    }

    [Fact]
    public void TryDecode_Null_ReturnsFalse()
        => Assert.False(GeoVehicleTravelSnapshot.TryDecode(null, out _, out _));

    // ─── change signature: host skips a vehicle whose travel metadata is unchanged ───

    [Fact]
    public void Signature_SameMeta_Equal()
    {
        var a = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 5, 6 });
        var b = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 5, 6 });
        Assert.Equal(GeoVehicleTravelMeta.Signature(a), GeoVehicleTravelMeta.Signature(b));
    }

    [Fact]
    public void Signature_ChangesOnTravellingFlag()
    {
        var moving = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 5 });
        var stopped = new GeoVehicleTravelMeta(9, 1, false, 5, new int[0]);   // arrived → stop ships once to clear line
        Assert.NotEqual(GeoVehicleTravelMeta.Signature(moving), GeoVehicleTravelMeta.Signature(stopped));
    }

    [Fact]
    public void Signature_ChangesWhenWaypointPassed()
    {
        var before = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 5, 6, 7 });
        var after = new GeoVehicleTravelMeta(9, 1, true, -1, new[] { 6, 7 });   // first waypoint popped
        Assert.NotEqual(GeoVehicleTravelMeta.Signature(before), GeoVehicleTravelMeta.Signature(after));
    }

    [Fact]
    public void Signature_ChangesOnCurrentSite()
    {
        var inTransit = new GeoVehicleTravelMeta(9, 1, false, -1, new int[0]);
        var atSite = new GeoVehicleTravelMeta(9, 1, false, 42, new int[0]);
        Assert.NotEqual(GeoVehicleTravelMeta.Signature(inTransit), GeoVehicleTravelMeta.Signature(atSite));
    }

    // ─── WA-3 aircraft HP/repair tail (audit gap 5d): optional extras block on the 0xA6 wire ───

    [Fact]
    public void HealthTail_RoundTrips_MixedCarriedAndAbsent()
    {
        var input = new List<GeoVehicleTravelMeta>
        {
            new GeoVehicleTravelMeta(11, 7, true, -1, new[] { 5 }, new GeoVehicleHealthTail(37, 100, isRepairing: true)),
            new GeoVehicleTravelMeta(11, 8, false, 42, new int[0]),                       // no tail
            new GeoVehicleTravelMeta(-22, 7, false, 9, new int[0], new GeoVehicleHealthTail(100, 100, isRepairing: false)),
        };

        byte[] wire = GeoVehicleTravelSnapshot.Encode(77u, input);
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(wire, out uint seq, out var outList));

        Assert.Equal(77u, seq);
        Assert.Equal(3, outList.Count);
        for (int i = 0; i < input.Count; i++)
            Assert.Equal(input[i], outList[i]);   // structural equality incl. the health tail
        Assert.Equal(new GeoVehicleHealthTail(37, 100, true), outList[0].Health);
        Assert.Null(outList[1].Health);
        Assert.False(outList[2].Health.IsRepairing);
    }

    // Wire-compat pin: a batch with NO health tails must stay BYTE-IDENTICAL to the pre-WA-3 wire (no
    // extras block) — and decoding such a payload (any older payload) yields null tails.
    [Fact]
    public void HealthTail_NoTails_WireByteIdentical_AndDecodesNullHealth()
    {
        var meta = new GeoVehicleTravelMeta(3, 1, true, -1, new[] { 7 });
        byte[] wire = GeoVehicleTravelSnapshot.Encode(5u, new List<GeoVehicleTravelMeta> { meta });

        var expected = new byte[]
        {
            0x05, 0x00, 0x00, 0x00,     // seq = 5 (u32 LE)
            0x01, 0x00,                 // count = 1
            0x03, 0x00, 0x00, 0x00,     // OwnerId = 3
            0x01, 0x00, 0x00, 0x00,     // VehicleId = 1
            0x01,                       // travelling = 1
            0xFF, 0xFF, 0xFF, 0xFF,     // currentSiteId = -1
            0x01, 0x00,                 // destCount = 1
            0x07, 0x00, 0x00, 0x00,     // destSiteId = 7
        };                              // NO extras block
        Assert.Equal(expected, wire);

        Assert.True(GeoVehicleTravelSnapshot.TryDecode(wire, out _, out var outList));
        Assert.Null(outList[0].Health);   // absent-tail compat: older payload → tail not carried
    }

    [Fact]
    public void HealthTail_TruncatedExtras_RejectsWholePayload()
    {
        byte[] wire = GeoVehicleTravelSnapshot.Encode(6u, new List<GeoVehicleTravelMeta>
        {
            new GeoVehicleTravelMeta(3, 1, false, 9, new int[0], new GeoVehicleHealthTail(50, 100, false)),
        });
        var chopped = new byte[wire.Length - 3];   // cut into the extras record → declared recLen no longer fits
        System.Array.Copy(wire, chopped, chopped.Length);
        Assert.False(GeoVehicleTravelSnapshot.TryDecode(chopped, out _, out _));
    }

    [Fact]
    public void HealthTail_ExtrasForUnknownKey_Skipped()
    {
        // Splice: encode a 1-vehicle batch with a tail, then decode after swapping the record's VehicleId so
        // the extras key no longer matches — the join-by-key must skip it, not throw.
        byte[] wire = GeoVehicleTravelSnapshot.Encode(8u, new List<GeoVehicleTravelMeta>
        {
            new GeoVehicleTravelMeta(3, 1, false, 9, new int[0], new GeoVehicleHealthTail(50, 100, false)),
        });
        wire[10] = 0x02;   // record VehicleId 1 → 2 (extras block still says 1)
        Assert.True(GeoVehicleTravelSnapshot.TryDecode(wire, out _, out var outList));
        Assert.Single(outList);
        Assert.Null(outList[0].Health);   // unknown-key extras record ignored
    }

    [Fact]
    public void Signature_ChangesOnHpTick_AndOnRepairFlag()
    {
        var parked = new GeoVehicleTravelMeta(9, 1, false, 42, new int[0]);
        var damaged = new GeoVehicleTravelMeta(9, 1, false, 42, new int[0], new GeoVehicleHealthTail(60, 100, true));
        var repairedTick = new GeoVehicleTravelMeta(9, 1, false, 42, new int[0], new GeoVehicleHealthTail(70, 100, true));
        var repairDone = new GeoVehicleTravelMeta(9, 1, false, 42, new int[0], new GeoVehicleHealthTail(70, 100, false));

        Assert.NotEqual(GeoVehicleTravelMeta.Signature(parked), GeoVehicleTravelMeta.Signature(damaged));
        Assert.NotEqual(GeoVehicleTravelMeta.Signature(damaged), GeoVehicleTravelMeta.Signature(repairedTick));
        Assert.NotEqual(GeoVehicleTravelMeta.Signature(repairedTick), GeoVehicleTravelMeta.Signature(repairDone));
        // Same tail → same signature (no re-ship storm while parked at stable HP).
        Assert.Equal(GeoVehicleTravelMeta.Signature(repairDone),
                     GeoVehicleTravelMeta.Signature(new GeoVehicleTravelMeta(9, 1, false, 42, new int[0],
                         new GeoVehicleHealthTail(70, 100, false))));
    }

    [Fact]
    public void HealthTail_Equality_AndPristine()
    {
        Assert.Equal(new GeoVehicleHealthTail(5, 10, true), new GeoVehicleHealthTail(5, 10, true));
        Assert.NotEqual(new GeoVehicleHealthTail(5, 10, true), new GeoVehicleHealthTail(5, 10, false));
        Assert.NotEqual(new GeoVehicleHealthTail(5, 10, true), new GeoVehicleHealthTail(6, 10, true));

        // IsPristine drives the host's initial-suppress rule: full HP + not repairing = nothing to mirror.
        Assert.True(new GeoVehicleHealthTail(100, 100, false).IsPristine);
        Assert.False(new GeoVehicleHealthTail(99, 100, false).IsPristine);
        Assert.False(new GeoVehicleHealthTail(100, 100, true).IsPristine);

        // Meta equality includes the tail (change detection can never collapse a tail flip).
        var a = new GeoVehicleTravelMeta(9, 1, false, 42, new int[0], new GeoVehicleHealthTail(70, 100, false));
        var b = new GeoVehicleTravelMeta(9, 1, false, 42, new int[0]);
        Assert.NotEqual(a, b);
    }
}
