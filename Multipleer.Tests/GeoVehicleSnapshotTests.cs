using System.Collections.Generic;
using Multipleer.Network.Sync.State;
using Xunit;

// Inc4 S2 host-driven travel mirror — pure wire codec + change-signature tests for the GeoVehiclePos (0xA5)
// surface. The engine glue (GeoVehicleMirror) is game-bound and in-game verified; these lock the wire round-trip
// and the host's per-vehicle "unchanged → skip" signature contract (idle vehicle = 0 bytes).
public class GeoVehicleSnapshotTests
{
    [Fact]
    public void RoundTrip_PreservesSeqAndEveryVehicleField()
    {
        var input = new List<GeoVehiclePos>
        {
            new GeoVehiclePos(7, 1.5f, -2.25f, 3.75f, 0.1f, 0.2f, 0.3f, 0.927362f),
            new GeoVehiclePos(42, -100.5f, 0f, 50.125f, 0f, 0f, 0f, 1f),
        };

        byte[] wire = GeoVehicleSnapshot.Encode(123u, input);
        Assert.True(GeoVehicleSnapshot.TryDecode(wire, out uint seq, out var outList));

        Assert.Equal(123u, seq);
        Assert.Equal(input.Count, outList.Count);
        for (int i = 0; i < input.Count; i++)
            Assert.Equal(input[i], outList[i]);   // struct equality (bit-exact float round-trip)
    }

    [Fact]
    public void Encode_EmptyBatch_DecodesToZeroVehiclesWithSeq()
    {
        byte[] wire = GeoVehicleSnapshot.Encode(9u, new List<GeoVehiclePos>());
        Assert.True(GeoVehicleSnapshot.TryDecode(wire, out uint seq, out var outList));
        Assert.Equal(9u, seq);
        Assert.Empty(outList);
    }

    [Fact]
    public void Encode_NullList_IsTreatedAsEmpty()
    {
        byte[] wire = GeoVehicleSnapshot.Encode(1u, null);
        Assert.True(GeoVehicleSnapshot.TryDecode(wire, out uint seq, out var outList));
        Assert.Equal(1u, seq);
        Assert.Empty(outList);
    }

    [Fact]
    public void TryDecode_Truncated_ReturnsFalse_NoPartialAccept()
    {
        byte[] wire = GeoVehicleSnapshot.Encode(5u, new List<GeoVehiclePos>
        {
            new GeoVehiclePos(1, 1f, 2f, 3f, 0f, 0f, 0f, 1f),
        });
        // Chop the last row's trailing bytes: the declared count (1) no longer fits → clean reject.
        var chopped = new byte[wire.Length - 4];
        System.Array.Copy(wire, chopped, chopped.Length);
        Assert.False(GeoVehicleSnapshot.TryDecode(chopped, out _, out _));
    }

    [Fact]
    public void TryDecode_Null_ReturnsFalse()
        => Assert.False(GeoVehicleSnapshot.TryDecode(null, out _, out _));

    // Field semantics (Inc4 S2 fix 2026-07-04): QX..QW = PivotTransform.localRotation (globe placement —
    // the quaternion NavigateRoutine writes; the SOLE position determinant), X,Y,Z = Surface.localEulerAngles
    // (heading/facing). Signature rounds the pivot quaternion FINE (F6) since it is the primary travel signal,
    // and the heading euler at F2 (0.01°).

    [Fact]
    public void Signature_SkipsSubHundredthHeadingJitter()
    {
        var a = new GeoVehiclePos(1, 10.000f, 20.000f, 30.000f, 0f, 0f, 0f, 1f);
        var b = new GeoVehiclePos(1, 10.004f, 20.003f, 30.002f, 0f, 0f, 0f, 1f);   // < 0.01° heading drift on each axis
        Assert.Equal(GeoVehiclePos.Signature(a), GeoVehiclePos.Signature(b));       // parked → same sig → 0 bytes
    }

    [Fact]
    public void Signature_ChangesOnPivotRotationStep()
    {
        var a = new GeoVehiclePos(1, 10f, 20f, 30f, 0.000f, 0.000f, 0.000f, 1.000f);
        var moved = new GeoVehiclePos(1, 10f, 20f, 30f, 0.000f, 0.383f, 0.000f, 0.924f); // pivot rotated → new globe pos
        Assert.NotEqual(GeoVehiclePos.Signature(a), GeoVehiclePos.Signature(moved));
    }

    [Fact]
    public void Signature_ChangesOnHeadingTurn()
    {
        var a = new GeoVehiclePos(1, 0f, 0f, 0.00f, 0f, 0f, 0f, 1f);
        var turned = new GeoVehiclePos(1, 0f, 0f, 15.00f, 0f, 0f, 0f, 1f);   // pivot (pos) same, heading euler.z turned 15°
        Assert.NotEqual(GeoVehiclePos.Signature(a), GeoVehiclePos.Signature(turned));
    }

    [Fact]
    public void Signature_DetectsSlowPivotTravelStep()
    {
        // A slow craft advances the pivot quaternion by a sub-milli amount per ~0.25s poll. The signature MUST
        // detect it (else slow travel is silently skipped → the client vehicle freezes). Guards the F6 pivot
        // rounding — this FAILS under the old F3 rounding (0.0003 → "0.000" both) and passes under F6.
        var a = new GeoVehiclePos(1, 0f, 0f, 0f, 0.000000f, 0.000000f, 0f, 1f);
        var stepped = new GeoVehiclePos(1, 0f, 0f, 0f, 0.000000f, 0.000300f, 0f, 1f);
        Assert.NotEqual(GeoVehiclePos.Signature(a), GeoVehiclePos.Signature(stepped));
    }
}
