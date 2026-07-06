using System.Collections.Generic;
using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for the TS4 MISSION-CONCLUSION mirror (surface <c>tac.missionend</c> 0x95). Covers:
///   (a) the <see cref="TacticalMissionEndCodec"/> wire round-trips (seq/phase/outcome pins + result blob + evac +
///       objective lists) and truncation / garbage / corrupt-count → clean drop (no partial accept), plus the
///       forward-compat trailing-byte tolerance and the phase-ordering pin (wrappingup before gameover),
///   (b) the CONCLUSION-HANDOFF seam (<see cref="TacticalMissionEndGate.ShouldEndClientMission"/> — close EXACTLY
///       once on gameover, never when already over, never on wrappingup) + the no-double-outcome contract
///       (<see cref="TacticalMissionEndGate.ShouldDisplayOutcome"/> is constant-false — the 0x69 popup-mirror owns
///       the modal),
///   (c) the pure objective-state apply mapping (<see cref="TacticalMissionEndGate.ResolveObjectiveApplies"/>) —
///       ordinal id → in-range client index, degrade-to-notify on unknown / out-of-range ids.
/// The engine glue (TacticalMissionEndSync / MissionEndPatches — the GameOver() postfix, GetMissionResult()
/// serialize, IsGameOver flip, objective reflection) binds game types and is in-game verified.
/// </summary>
public class TacticalMissionEndCodecTests
{
    private static TacticalMissionEndCodec.MissionEndPayload Decode(byte[] bytes)
    {
        Assert.True(TacticalMissionEndCodec.TryDecode(bytes, out var p));
        return p;
    }

    // ─── (a) codec round-trip + pins ───────────────────────────────────
    [Fact]
    public void RoundTrips_AllFields_ResultEvacObjectives()
    {
        var payload = new TacticalMissionEndCodec.MissionEndPayload(
            seq: 555u,
            phase: TacticalMissionEndCodec.PhaseGameOver,
            outcome: 2,
            resultBlob: new byte[] { 9, 8, 7, 6, 5 },
            evacZones: new List<TacticalMissionEndCodec.EvacRec>
            {
                new TacticalMissionEndCodec.EvacRec(10, true),
                new TacticalMissionEndCodec.EvacRec(20, false),
            },
            objectives: new List<TacticalMissionEndCodec.ObjectiveRec>
            {
                new TacticalMissionEndCodec.ObjectiveRec("0", 1),
                new TacticalMissionEndCodec.ObjectiveRec("1", 2),
            });

        var d = Decode(TacticalMissionEndCodec.Encode(payload));

        Assert.Equal(555u, d.Seq);                                       // pin: seq survives
        Assert.Equal(TacticalMissionEndCodec.PhaseGameOver, d.Phase);
        Assert.Equal(2, d.Outcome);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, d.ResultBlob);        // byte-identical blob survives

        Assert.Equal(2, d.EvacZones.Count);
        Assert.Equal(10, d.EvacZones[0].ZoneId);
        Assert.True(d.EvacZones[0].Unlocked);
        Assert.Equal(20, d.EvacZones[1].ZoneId);
        Assert.False(d.EvacZones[1].Unlocked);

        Assert.Equal(2, d.Objectives.Count);
        Assert.Equal("0", d.Objectives[0].ObjectiveId);
        Assert.Equal(1, d.Objectives[0].State);
        Assert.Equal("1", d.Objectives[1].ObjectiveId);
        Assert.Equal(2, d.Objectives[1].State);
    }

    [Fact]
    public void RoundTrips_Wrappingup_Minimal_NoBlobNoLists()
    {
        var bytes = TacticalMissionEndCodec.Encode(new TacticalMissionEndCodec.MissionEndPayload(
            1u, TacticalMissionEndCodec.PhaseWrappingUp, TacticalMissionEndCodec.OutcomeUnknown, null, null, null));

        // Exactly u32 seq + u8 phase + i32 outcome + i32 resultLen + u16 evacCount + u16 objCount, no tail.
        Assert.Equal(4 + 1 + 4 + 4 + 2 + 2, bytes.Length);

        var d = Decode(bytes);
        Assert.Equal(1u, d.Seq);
        Assert.Equal(TacticalMissionEndCodec.PhaseWrappingUp, d.Phase);
        Assert.Equal(TacticalMissionEndCodec.OutcomeUnknown, d.Outcome);
        Assert.Empty(d.ResultBlob);
        Assert.Empty(d.EvacZones);
        Assert.Empty(d.Objectives);
    }

    [Fact]
    public void Pins_PhaseValues_AndOrdering()
    {
        // The wire constants + the ordering contract (a client applies wrappingup before gameover).
        Assert.Equal(0, TacticalMissionEndCodec.PhaseWrappingUp);
        Assert.Equal(1, TacticalMissionEndCodec.PhaseGameOver);
        Assert.True(TacticalMissionEndCodec.PhaseWrappingUp < TacticalMissionEndCodec.PhaseGameOver);
        Assert.Equal(-1, TacticalMissionEndCodec.OutcomeUnknown);
    }

    [Fact]
    public void ForwardCompat_IgnoresTrailingBytes()
    {
        var bytes = TacticalMissionEndCodec.Encode(new TacticalMissionEndCodec.MissionEndPayload(
            7u, TacticalMissionEndCodec.PhaseGameOver, 3, new byte[] { 1, 2 },
            new List<TacticalMissionEndCodec.EvacRec> { new TacticalMissionEndCodec.EvacRec(4, true) },
            new List<TacticalMissionEndCodec.ObjectiveRec> { new TacticalMissionEndCodec.ObjectiveRec("2", 1) }));

        // A newer peer appends extra trailing fields; an older decoder reads what it knows + ignores the rest.
        var extended = new byte[bytes.Length + 8];
        System.Array.Copy(bytes, extended, bytes.Length);
        for (int i = bytes.Length; i < extended.Length; i++) extended[i] = 0xEE;

        var d = Decode(extended);
        Assert.Equal(7u, d.Seq);
        Assert.Equal(3, d.Outcome);
        Assert.Equal(new byte[] { 1, 2 }, d.ResultBlob);
        Assert.Single(d.EvacZones);
        Assert.Single(d.Objectives);
        Assert.Equal("2", d.Objectives[0].ObjectiveId);
    }

    [Fact]
    public void Rejects_Null_Truncated_AndGarbage()
    {
        Assert.False(TacticalMissionEndCodec.TryDecode(null, out _));
        Assert.False(TacticalMissionEndCodec.TryDecode(new byte[16], out _));   // shorter than the 17-byte minimum

        // A valid frame chopped mid-blob → clean reject (resultLen says more bytes than remain).
        var bytes = TacticalMissionEndCodec.Encode(new TacticalMissionEndCodec.MissionEndPayload(
            1u, TacticalMissionEndCodec.PhaseGameOver, 0, new byte[] { 1, 2, 3, 4, 5, 6 }, null, null));
        var chopped = new byte[bytes.Length - 3];
        System.Array.Copy(bytes, chopped, chopped.Length);
        Assert.False(TacticalMissionEndCodec.TryDecode(chopped, out _));
    }

    [Fact]
    public void Rejects_CorruptResultLength_AndCounts()
    {
        // Header then a bogus huge resultLen with no data → guarded reject.
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u); w.Write(TacticalMissionEndCodec.PhaseGameOver); w.Write(0);
            w.Write(int.MaxValue);   // resultLen far exceeds the remaining buffer
            Assert.False(TacticalMissionEndCodec.TryDecode(ms.ToArray(), out _));
        }

        // Header (empty blob) then a bogus huge evacCount with no records → guarded reject.
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u); w.Write(TacticalMissionEndCodec.PhaseGameOver); w.Write(0);
            w.Write(0);                            // resultLen = 0
            w.Write((ushort)ushort.MaxValue);      // evacCount far exceeds the remaining buffer
            Assert.False(TacticalMissionEndCodec.TryDecode(ms.ToArray(), out _));
        }
    }

    // ─── (b) conclusion-handoff seam (no double close / no double outcome) ─────
    [Fact]
    public void ShouldEndClientMission_OnceOnGameOver_NeverWhenOver_NeverOnWrappingup()
    {
        // The one TRUE case: terminal gameover phase, client not already game-over → close exactly once.
        Assert.True(TacticalMissionEndGate.ShouldEndClientMission(TacticalMissionEndCodec.PhaseGameOver, alreadyGameOver: false));
        // Idempotent: a re-sent gameover after the flag is set → no double close.
        Assert.False(TacticalMissionEndGate.ShouldEndClientMission(TacticalMissionEndCodec.PhaseGameOver, alreadyGameOver: true));
        // wrappingup is a pre-notify — it NEVER ends the mission, regardless of state.
        Assert.False(TacticalMissionEndGate.ShouldEndClientMission(TacticalMissionEndCodec.PhaseWrappingUp, alreadyGameOver: false));
        Assert.False(TacticalMissionEndGate.ShouldEndClientMission(TacticalMissionEndCodec.PhaseWrappingUp, alreadyGameOver: true));
    }

    [Fact]
    public void ShouldDisplayOutcome_IsAlwaysFalse_NoDoubleOutcome()
    {
        // TS4 shows NO outcome modal of its own — the geoscape popup-mirror (0x69) owns it.
        Assert.False(TacticalMissionEndGate.ShouldDisplayOutcome());
    }

    // ─── (c) pure objective-state apply mapping ────────────────────────
    [Fact]
    public void ResolveObjectiveApplies_MapsOrdinal_SkipsUnknownAndOutOfRange()
    {
        var objectives = new List<TacticalMissionEndCodec.ObjectiveRec>
        {
            new TacticalMissionEndCodec.ObjectiveRec("0", 1),   // in range
            new TacticalMissionEndCodec.ObjectiveRec("2", 2),   // in range (last slot)
            new TacticalMissionEndCodec.ObjectiveRec("5", 1),   // out of range → skipped
            new TacticalMissionEndCodec.ObjectiveRec("x", 1),   // unparseable → skipped
        };
        var applies = TacticalMissionEndGate.ResolveObjectiveApplies(objectives, clientObjectiveCount: 3);

        Assert.Equal(2, applies.Count);
        Assert.Equal(0, applies[0].Key); Assert.Equal(1, applies[0].Value);
        Assert.Equal(2, applies[1].Key); Assert.Equal(2, applies[1].Value);
    }

    [Fact]
    public void ResolveObjectiveApplies_Empty_OnNullOrZeroCount()
    {
        Assert.Empty(TacticalMissionEndGate.ResolveObjectiveApplies(null, 3));
        Assert.Empty(TacticalMissionEndGate.ResolveObjectiveApplies(
            new List<TacticalMissionEndCodec.ObjectiveRec> { new TacticalMissionEndCodec.ObjectiveRec("0", 1) }, 0));
    }
}
