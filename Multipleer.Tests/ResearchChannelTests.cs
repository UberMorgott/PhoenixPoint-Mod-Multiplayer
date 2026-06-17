using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip tests for the research state channel (#2) snapshot codec: completed research ids +
/// the ordered (id, progress) queue. Only the pure encode/decode path is exercised; Snapshot/Apply bind
/// live game types and are not unit-testable.
/// </summary>
public class ResearchChannelTests
{
    private static ResearchSnapshot RoundTrip(ResearchSnapshot snap)
    {
        var bytes = ResearchSnapshot.Encode(snap);
        return ResearchSnapshot.Decode(bytes);
    }

    [Fact]
    public void Snapshot_RoundTrips_CompletedAndQueue()
    {
        var snap = new ResearchSnapshot();
        snap.Completed.Add("PX_AlienBiology_ResearchDef");
        snap.Completed.Add("PX_LaserTech_ResearchDef");
        snap.Queue.Add(("PX_Vehicles_ResearchDef", 125.5f));
        snap.Queue.Add(("PX_Mutoid_ResearchDef", 0f));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(new[] { "PX_AlienBiology_ResearchDef", "PX_LaserTech_ResearchDef" }, rt.Completed);
        Assert.Equal(2, rt.Queue.Count);
        Assert.Equal("PX_Vehicles_ResearchDef", rt.Queue[0].id);
        Assert.Equal(125.5f, rt.Queue[0].progress);
        Assert.Equal("PX_Mutoid_ResearchDef", rt.Queue[1].id);
        Assert.Equal(0f, rt.Queue[1].progress);
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new ResearchSnapshot());
        Assert.NotNull(rt);
        Assert.Empty(rt.Completed);
        Assert.Empty(rt.Queue);
    }

    [Fact]
    public void Snapshot_PreservesQueueOrder()
    {
        var snap = new ResearchSnapshot();
        snap.Queue.Add(("a", 1f));
        snap.Queue.Add(("b", 2f));
        snap.Queue.Add(("c", 3f));

        var rt = RoundTrip(snap);

        Assert.Equal(new[] { "a", "b", "c" }, rt.Queue.ConvertAll(q => q.id));
    }

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
    {
        // Truncated: claims completedCount but no payload bytes follow.
        Assert.Null(ResearchSnapshot.Decode(new byte[] { 0xFF }));
    }

    [Fact]
    public void Decode_RejectsTruncatedString_ReturnsNull()
    {
        // completedCount=1, idLen=5, but only 2 of the 5 string bytes follow. BinaryReader.ReadBytes
        // would SILENTLY return the 2 available bytes (no throw) → garbage id. ReadStr now verifies the
        // returned length == declared len and bails, so Decode returns null (rejected, not garbage).
        var truncated = new byte[]
        {
            0x01, 0x00,             // completedCount = 1
            0x05, 0x00,             // idLen = 5
            0x41, 0x42,             // only "AB" (2 bytes) — 3 short
        };
        Assert.Null(ResearchSnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_RejectsTruncatedQueueString_ReturnsNull()
    {
        // completedCount=0, queueCount=1, then a queue id claiming len=4 with 0 bytes following.
        var truncated = new byte[]
        {
            0x00, 0x00,             // completedCount = 0
            0x01, 0x00,             // queueCount = 1
            0x04, 0x00,             // queue idLen = 4
                                    // (no id bytes, no f32 progress) — truncated
        };
        Assert.Null(ResearchSnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_AcceptsWellFormed_NotRejectedByLengthCheck()
    {
        // Regression guard: the truncation length-check must NOT reject a valid payload. A real
        // round-trip of a 1-id snapshot must still decode (id "AB", length 2, exactly 2 bytes present).
        var snap = new ResearchSnapshot();
        snap.Completed.Add("AB");
        var rt = ResearchSnapshot.Decode(ResearchSnapshot.Encode(snap));
        Assert.NotNull(rt);
        Assert.Equal(new[] { "AB" }, rt.Completed);
    }

    [Fact]
    public void Encode_NullSnapshot_ReturnsNull()
    {
        Assert.Null(ResearchSnapshot.Encode(null));
    }

    // ─── FIX#2: Revealed/Unlocked Available-list state block ───────────────────

    [Fact]
    public void Snapshot_RoundTrips_StatesBlock()
    {
        var snap = new ResearchSnapshot();
        snap.Completed.Add("PX_AlienBiology_ResearchDef");
        snap.Queue.Add(("PX_Vehicles_ResearchDef", 10f));
        snap.States.Add(("PX_LaserTech_ResearchDef", 2));   // Unlocked
        snap.States.Add(("PX_Mutoid_ResearchDef", 1));      // Revealed

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(new[] { "PX_AlienBiology_ResearchDef" }, rt.Completed);
        Assert.Single(rt.Queue);
        Assert.Equal(2, rt.States.Count);
        Assert.Equal("PX_LaserTech_ResearchDef", rt.States[0].id);
        Assert.Equal((byte)2, rt.States[0].state);
        Assert.Equal("PX_Mutoid_ResearchDef", rt.States[1].id);
        Assert.Equal((byte)1, rt.States[1].state);
    }

    [Fact]
    public void Snapshot_RoundTrips_EmptyStatesBlock()
    {
        // No States entries → the trailing block is still written (u16 zero) and decodes as empty.
        var snap = new ResearchSnapshot();
        snap.Queue.Add(("a", 5f));
        var rt = RoundTrip(snap);
        Assert.NotNull(rt);
        Assert.Empty(rt.States);
        Assert.Single(rt.Queue);
    }

    [Fact]
    public void Decode_V1Payload_NoStatesBlock_DecodesAsEmptyStates()
    {
        // Backward compatibility: a v1 wire payload (completed+queue only, NO trailing state block) must
        // decode cleanly with an empty States list rather than throwing. Hand-build a v1 payload:
        //   completedCount=0, queueCount=1, id "a" (len 1), progress 5.0f — and NOTHING after.
        var v1 = new byte[]
        {
            0x00, 0x00,                         // completedCount = 0
            0x01, 0x00,                         // queueCount = 1
            0x01, 0x00,                         // idLen = 1
            0x61,                               // "a"
            0x00, 0x00, 0xA0, 0x40,             // 5.0f little-endian
            // (no trailing state block — v1)
        };
        var snap = ResearchSnapshot.Decode(v1);
        Assert.NotNull(snap);
        Assert.Empty(snap.Completed);
        Assert.Single(snap.Queue);
        Assert.Equal("a", snap.Queue[0].id);
        Assert.Equal(5.0f, snap.Queue[0].progress);
        Assert.Empty(snap.States);
    }

    [Fact]
    public void Decode_RejectsTruncatedStateBlock_ReturnsNull()
    {
        // completed=0, queue=0, stateCount=1, then a state id claiming len=3 with 0 bytes following.
        var truncated = new byte[]
        {
            0x00, 0x00,             // completedCount = 0
            0x00, 0x00,             // queueCount = 0
            0x01, 0x00,             // stateCount = 1
            0x03, 0x00,             // state idLen = 3
                                    // (no id bytes, no state byte) — truncated
        };
        Assert.Null(ResearchSnapshot.Decode(truncated));
    }

    [Fact]
    public void Encode_StableWireBytes_Pinned()
    {
        // Pin the EXACT v2 wire layout so an accidental format change is caught. Snapshot:
        //   Completed = ["AB"]; Queue = [("C", 2.0f)]; States = [("D", 2)].
        var snap = new ResearchSnapshot();
        snap.Completed.Add("AB");
        snap.Queue.Add(("C", 2.0f));
        snap.States.Add(("D", 2));

        var bytes = ResearchSnapshot.Encode(snap);

        var expected = new byte[]
        {
            0x01, 0x00,                         // completedCount = 1
            0x02, 0x00, 0x41, 0x42,             // idLen=2, "AB"
            0x01, 0x00,                         // queueCount = 1
            0x01, 0x00, 0x43,                   // idLen=1, "C"
            0x00, 0x00, 0x00, 0x40,             // progress 2.0f LE
            0x01, 0x00,                         // stateCount = 1
            0x01, 0x00, 0x44,                   // idLen=1, "D"
            0x02,                               // state byte = 2 (Unlocked)
        };
        Assert.Equal(expected, bytes);
    }
}
