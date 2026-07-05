using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip tests for the faction-DIPLOMACY state channel (#4) snapshot codec: per-relation
/// reputation ints keyed by (ownerFactionDef guid, withPartyDef guid). Only the pure encode/decode path
/// is exercised; Snapshot/Apply bind live game types and are not unit-testable. Mirrors
/// <see cref="ResearchChannelTests"/>.
/// </summary>
public class DiplomacyChannelTests
{
    private static DiplomacySnapshot RoundTrip(DiplomacySnapshot snap)
        => DiplomacySnapshot.Decode(DiplomacySnapshot.Encode(snap));

    [Fact]
    public void Snapshot_RoundTrips_Relations()
    {
        var snap = new DiplomacySnapshot();
        snap.Relations.Add(("PX_FactionDef", "SY_FactionDef", -3));
        snap.Relations.Add(("PX_FactionDef", "AN_FactionDef", 5));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(2, rt.Relations.Count);
        Assert.Equal(("PX_FactionDef", "SY_FactionDef", -3), rt.Relations[0]);
        Assert.Equal(("PX_FactionDef", "AN_FactionDef", 5), rt.Relations[1]);
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new DiplomacySnapshot());
        Assert.NotNull(rt);
        Assert.Empty(rt.Relations);
    }

    [Fact]
    public void Snapshot_PreservesOrder_AndNegativeValues()
    {
        var snap = new DiplomacySnapshot();
        snap.Relations.Add(("o", "a", -2147483647)); // min diplomacy
        snap.Relations.Add(("o", "b", 0));
        snap.Relations.Add(("o", "c", 100));

        var rt = RoundTrip(snap);

        Assert.Equal(new[] { "a", "b", "c" }, rt.Relations.ConvertAll(x => x.with));
        Assert.Equal(-2147483647, rt.Relations[0].value);
        Assert.Equal(100, rt.Relations[2].value);
    }

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
    {
        Assert.Null(DiplomacySnapshot.Decode(new byte[] { 0xFF }));
    }

    [Fact]
    public void Decode_RejectsTruncatedString_ReturnsNull()
    {
        // count=1, ownerLen=4 but no bytes follow → rejected.
        var truncated = new byte[]
        {
            0x01, 0x00,             // count = 1
            0x04, 0x00,             // ownerLen = 4
                                    // (no owner bytes) — truncated
        };
        Assert.Null(DiplomacySnapshot.Decode(truncated));
    }

    [Fact]
    public void Encode_NullSnapshot_ReturnsNull() => Assert.Null(DiplomacySnapshot.Encode(null));

    [Fact]
    public void Encode_StableWireBytes_Pinned()
    {
        // Pin the EXACT wire layout. One relation: owner "A", with "B", value 7.
        var snap = new DiplomacySnapshot();
        snap.Relations.Add(("A", "B", 7));

        var bytes = DiplomacySnapshot.Encode(snap);

        var expected = new byte[]
        {
            0x01, 0x00,                 // count = 1
            0x01, 0x00, 0x41,           // ownerLen=1, "A"
            0x01, 0x00, 0x42,           // withLen=1, "B"
            0x07, 0x00, 0x00, 0x00,     // value 7 (i32 LE)
        };
        Assert.Equal(expected, bytes);
    }

    // ─── registration: the diplomacy channel claims a distinct, stable surface/channel id ────
    [Fact]
    public void ChannelId_Is4_AndDistinctFromOtherChannels()
    {
        Assert.Equal((byte)4, SurfaceIds.DiplomacyChannel);
        var ids = new[] { SurfaceIds.InventoryChannel, SurfaceIds.ResearchChannel,
                          SurfaceIds.UnlockChannel, SurfaceIds.DiplomacyChannel };
        Assert.Equal(ids.Length, new System.Collections.Generic.HashSet<byte>(ids).Count);
    }

    // ─── WA-3 forced-state tail (audit gap 4e): versioned wire-compatible extension ───────────

    [Fact]
    public void ForcedStates_RoundTrip_IndexAligned()
    {
        var snap = new DiplomacySnapshot();
        snap.Relations.Add(("PX", "SY", -75));
        snap.Relations.Add(("PX", "AN", 40));
        snap.Relations.Add(("SY", "AN", 0));
        snap.ForcedStates.Add(0);                                   // Conflict (forced war)
        snap.ForcedStates.Add(DiplomacySnapshot.StateNotCarried);   // host read miss → skipped
        snap.ForcedStates.Add(7);                                   // Allied (unforced cap = MaxDiplomacy)

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(3, rt.Relations.Count);
        Assert.Equal(new byte[] { 0, 255, 7 }, rt.ForcedStates.ToArray());
        Assert.Equal(("PX", "SY", -75), rt.Relations[0]);
    }

    [Fact]
    public void ForcedStates_LegacyPayload_DecodesEmpty()
    {
        // The EXACT pre-WA-3 pinned wire (see Encode_StableWireBytes_Pinned) — an older host's payload.
        var legacy = new byte[]
        {
            0x01, 0x00,                 // count = 1
            0x01, 0x00, 0x41,           // ownerLen=1, "A"
            0x01, 0x00, 0x42,           // withLen=1, "B"
            0x07, 0x00, 0x00, 0x00,     // value 7 (i32 LE)
        };
        var rt = DiplomacySnapshot.Decode(legacy);
        Assert.NotNull(rt);
        Assert.Single(rt.Relations);
        Assert.Empty(rt.ForcedStates);   // absent tail — nothing carried, nothing guessed
    }

    [Fact]
    public void ForcedStates_AllNotCarried_WireByteIdenticalToLegacy()
    {
        var withStates = new DiplomacySnapshot();
        withStates.Relations.Add(("A", "B", 7));
        withStates.ForcedStates.Add(DiplomacySnapshot.StateNotCarried);

        var without = new DiplomacySnapshot();
        without.Relations.Add(("A", "B", 7));

        // No relation carries a valid state → the tail is omitted → byte-identical to the legacy wire
        // (a pre-WA-3 client decodes it unchanged; the pinned layout holds).
        Assert.Equal(DiplomacySnapshot.Encode(without), DiplomacySnapshot.Encode(withStates));
    }

    [Fact]
    public void ForcedStates_WireBytes_Pinned()
    {
        var snap = new DiplomacySnapshot();
        snap.Relations.Add(("A", "B", 7));
        snap.ForcedStates.Add(0);   // Conflict

        var expected = new byte[]
        {
            0x01, 0x00,                 // count = 1
            0x01, 0x00, 0x41,           // ownerLen=1, "A"
            0x01, 0x00, 0x42,           // withLen=1, "B"
            0x07, 0x00, 0x00, 0x00,     // value 7 (i32 LE)
            0x01, 0x00,                 // forced-state tail count = 1 (must equal record count)
            0x00,                       // PartyDiplomacyState.Conflict
        };
        Assert.Equal(expected, DiplomacySnapshot.Encode(snap));
    }

    [Fact]
    public void ForcedStates_MisalignedDto_TailOmitted()
    {
        // A count-misaligned ForcedStates list (host bug belt) must NOT ship a corrupt tail — it is
        // treated as absent and the wire stays legacy-shaped.
        var snap = new DiplomacySnapshot();
        snap.Relations.Add(("A", "B", 7));
        snap.Relations.Add(("A", "C", 9));
        snap.ForcedStates.Add(0);   // 1 state for 2 relations

        var rt = RoundTrip(snap);
        Assert.NotNull(rt);
        Assert.Equal(2, rt.Relations.Count);
        Assert.Empty(rt.ForcedStates);
    }

    [Fact]
    public void ForcedStates_TailCountMismatchOnWire_RejectsPayload()
    {
        // Valid records + a tail declaring the WRONG count → the index alignment is broken → reject (null).
        var good = new DiplomacySnapshot();
        good.Relations.Add(("A", "B", 7));
        good.ForcedStates.Add(3);
        var wire = DiplomacySnapshot.Encode(good);
        wire[wire.Length - 3] = 0x02;   // tail count 1 → 2 (no second state byte follows)
        Assert.Null(DiplomacySnapshot.Decode(wire));
    }

    [Fact]
    public void ForcedStates_TruncatedTail_RejectsPayload()
    {
        var good = new DiplomacySnapshot();
        good.Relations.Add(("A", "B", 7));
        good.ForcedStates.Add(3);
        var wire = DiplomacySnapshot.Encode(good);
        var chopped = new byte[wire.Length - 1];   // drop the state byte, keep the tail count
        System.Array.Copy(wire, chopped, chopped.Length);
        Assert.Null(DiplomacySnapshot.Decode(chopped));
    }

    // ─── the client apply decision: stamp iff the byte is a valid raw PartyDiplomacyState ───
    [Theory]
    [InlineData((byte)0, true)]    // Conflict — forced war
    [InlineData((byte)1, true)]    // Hostile
    [InlineData((byte)4, true)]    // Neutral
    [InlineData((byte)7, true)]    // Allied
    [InlineData((byte)8, false)]   // out of enum range — never guess
    [InlineData((byte)254, false)]
    [InlineData(DiplomacySnapshot.StateNotCarried, false)]  // 255 — not carried
    public void ShouldApplyForcedState_OnlyValidStateBytes(byte wireState, bool expected)
        => Assert.Equal(expected, DiplomacySnapshot.ShouldApplyForcedState(wireState));
}
