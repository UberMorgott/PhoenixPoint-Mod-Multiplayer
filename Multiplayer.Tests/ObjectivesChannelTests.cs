using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip tests for the faction-OBJECTIVES + event-variables state channel (#7, spec §P7)
/// snapshot codec: per-class objective value records + the GeoscapeEventSystem custom-variable table.
/// Only the pure encode/decode path is exercised; Snapshot/Apply bind live game types and are not
/// unit-testable. Mirrors <see cref="DiplomacyChannelTests"/>.
/// </summary>
public class ObjectivesChannelTests
{
    private static ObjectivesSnapshot RoundTrip(ObjectivesSnapshot snap)
        => ObjectivesSnapshot.Decode(ObjectivesSnapshot.Encode(snap));

    private static ObjectivesSnapshot.ObjectiveRecord Rec(
        byte disc, byte flags = 0, string givenBy = "PX", string title = "", string desc = "",
        string payload = "", int aux = 0)
        => new ObjectivesSnapshot.ObjectiveRecord
        {
            Disc = disc, Flags = flags, GivenByGuid = givenBy,
            TitleKey = title, DescKey = desc, Payload = payload, Aux = aux,
        };

    [Fact]
    public void Snapshot_RoundTrips_AllDiscs_AndVariables()
    {
        var snap = new ObjectivesSnapshot();
        snap.Objectives.Add(Rec(ObjectivesSnapshot.DiscEvent,
            (byte)(ObjectivesSnapshot.FlagCritical | ObjectivesSnapshot.FlagCompleted | ObjectivesSnapshot.FlagTitlePresent),
            givenBy: "PX_Guid", title: "KEY_INCIDENT_TITLE", payload: "SDI_07"));
        snap.Objectives.Add(Rec(ObjectivesSnapshot.DiscDiplomatic,
            (byte)(ObjectivesSnapshot.FlagTitlePresent | ObjectivesSnapshot.FlagDescPresent),
            title: "TFTV_VO_TITLE", desc: "TFTV_VO_DESC", payload: "PX_Guid"));
        snap.Objectives.Add(Rec(ObjectivesSnapshot.DiscResearch, payload: "PX_Alien1_ResearchDef"));
        snap.Objectives.Add(Rec(ObjectivesSnapshot.DiscMission, aux: 421));
        snap.Variables.Add(("BC_SDI", 5));
        snap.Variables.Add(("Infestation_Encounter_Variable", -1));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(4, rt.Objectives.Count);
        Assert.Equal(ObjectivesSnapshot.DiscEvent, rt.Objectives[0].Disc);
        Assert.Equal("SDI_07", rt.Objectives[0].Payload);
        Assert.True(rt.Objectives[0].Critical);
        Assert.True(rt.Objectives[0].Completed);
        Assert.Equal("KEY_INCIDENT_TITLE", rt.Objectives[0].TitleKey);
        Assert.Equal("TFTV_VO_DESC", rt.Objectives[1].DescKey);
        Assert.Equal("PX_Alien1_ResearchDef", rt.Objectives[2].Payload);
        Assert.Equal(421, rt.Objectives[3].Aux);
        Assert.Equal(2, rt.Variables.Count);
        Assert.Equal(("BC_SDI", 5), rt.Variables[0]);
        Assert.Equal(("Infestation_Encounter_Variable", -1), rt.Variables[1]);
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new ObjectivesSnapshot());
        Assert.NotNull(rt);
        Assert.Empty(rt.Objectives);
        Assert.Empty(rt.Variables);
    }

    [Fact]
    public void Snapshot_RoundTrips_VariablesOnly()
    {
        // A campaign tick that only wrote variables (no objective churn) still snapshots whole-set.
        var snap = new ObjectivesSnapshot();
        snap.Variables.Add(("Voided_1", 3));
        var rt = RoundTrip(snap);
        Assert.Empty(rt.Objectives);
        Assert.Single(rt.Variables);
        Assert.Equal(("Voided_1", 3), rt.Variables[0]);
    }

    // ─── AB marketplace offers (optional trailing section) ────

    [Fact]
    public void Snapshot_RoundTrips_MarketplaceOffers_WithObjectivesAndVariables()
    {
        var snap = new ObjectivesSnapshot();
        snap.Objectives.Add(Rec(ObjectivesSnapshot.DiscEvent, payload: "SDI_07"));
        snap.Variables.Add(("NumberOfDLC5MissionsCompletedVariable", 2));
        snap.MarketplaceOffers.Add(new ObjectivesSnapshot.MarketplaceOfferRecord
            { Kind = ObjectivesSnapshot.OfferItem, OfferGuid = "item-guid-A", Price = 250f });
        snap.MarketplaceOffers.Add(new ObjectivesSnapshot.MarketplaceOfferRecord
            { Kind = ObjectivesSnapshot.OfferResearch, OfferGuid = "PX_Research_Id", Price = 0f });
        snap.MarketplaceOffers.Add(new ObjectivesSnapshot.MarketplaceOfferRecord
            { Kind = ObjectivesSnapshot.OfferUnit, OfferGuid = "unit-guid-C", Price = 900f });

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Single(rt.Objectives);
        Assert.Single(rt.Variables);
        Assert.Equal(3, rt.MarketplaceOffers.Count);
        Assert.Equal(ObjectivesSnapshot.OfferItem, rt.MarketplaceOffers[0].Kind);
        Assert.Equal("item-guid-A", rt.MarketplaceOffers[0].OfferGuid);
        Assert.Equal(250f, rt.MarketplaceOffers[0].Price);
        Assert.Equal(ObjectivesSnapshot.OfferResearch, rt.MarketplaceOffers[1].Kind);
        Assert.Equal("PX_Research_Id", rt.MarketplaceOffers[1].OfferGuid);
        Assert.Equal(ObjectivesSnapshot.OfferUnit, rt.MarketplaceOffers[2].Kind);
        Assert.Equal(900f, rt.MarketplaceOffers[2].Price);
    }

    [Fact]
    public void MarketplaceOffers_OmittedWhenEmpty_PreAbWireDecodesEmpty()
    {
        // No offers → the trailing section is NOT written; a snapshot with only objectives+variables must
        // decode with an empty offer list (backward tolerance — a pre-AB peer never wrote the section).
        var snap = new ObjectivesSnapshot();
        snap.Objectives.Add(Rec(ObjectivesSnapshot.DiscEvent, payload: "E"));
        snap.Variables.Add(("V", 1));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Empty(rt.MarketplaceOffers);
        // Byte-identity of the no-offers wire is guarded by Encode_StableWireBytes_Pinned (offers write
        // zero bytes when empty), so a pre-AB decoder never trips over a trailing section.
    }

    [Fact]
    public void MarketplaceOffers_OnlyOffers_NoObjectivesNoVariables_RoundTrips()
    {
        var snap = new ObjectivesSnapshot();
        snap.MarketplaceOffers.Add(new ObjectivesSnapshot.MarketplaceOfferRecord
            { Kind = ObjectivesSnapshot.OfferItem, OfferGuid = "g", Price = 42f });

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Empty(rt.Objectives);
        Assert.Empty(rt.Variables);
        Assert.Single(rt.MarketplaceOffers);
        Assert.Equal("g", rt.MarketplaceOffers[0].OfferGuid);
        Assert.Equal(42f, rt.MarketplaceOffers[0].Price);
    }

    [Fact]
    public void UnknownDisc_DecodesFine_ApplySkipsIt()
    {
        // Forward tolerance: a future host's disc 9 record must decode (never reject the payload);
        // the apply-side gate (IsCarriedDisc) skips the record it can't rebuild.
        var snap = new ObjectivesSnapshot();
        snap.Objectives.Add(Rec(9, payload: "future"));
        var rt = RoundTrip(snap);
        Assert.NotNull(rt);
        Assert.Single(rt.Objectives);
        Assert.False(ObjectivesSnapshot.IsCarriedDisc(rt.Objectives[0].Disc));
    }

    [Theory]
    [InlineData(ObjectivesSnapshot.DiscEvent, true)]
    [InlineData(ObjectivesSnapshot.DiscDiplomatic, true)]
    [InlineData(ObjectivesSnapshot.DiscResearch, true)]
    [InlineData(ObjectivesSnapshot.DiscMission, true)]
    [InlineData((byte)0, false)]
    [InlineData((byte)5, false)]
    [InlineData((byte)255, false)]
    public void IsCarriedDisc_ExactVanillaSet(byte disc, bool expected)
        => Assert.Equal(expected, ObjectivesSnapshot.IsCarriedDisc(disc));

    [Fact]
    public void MatchKey_IdentityIncludesTitle_ButNotMutableFlags()
    {
        // TFTV quest steps are distinct Diplomatic objectives differing only by title → distinct keys;
        // a critical/completed flag flip must NOT change identity (stamp in place, no churn).
        var a = Rec(ObjectivesSnapshot.DiscDiplomatic, title: "STEP_1", payload: "PX");
        var b = Rec(ObjectivesSnapshot.DiscDiplomatic, title: "STEP_2", payload: "PX");
        var aFlagged = Rec(ObjectivesSnapshot.DiscDiplomatic,
            (byte)(ObjectivesSnapshot.FlagCritical | ObjectivesSnapshot.FlagCompleted), title: "STEP_1", payload: "PX");

        Assert.NotEqual(a.MatchKey(), b.MatchKey());
        Assert.Equal(a.MatchKey(), aFlagged.MatchKey());
    }

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
    {
        Assert.Null(ObjectivesSnapshot.Decode(new byte[] { 0xFF }));
    }

    [Fact]
    public void Decode_RejectsTruncatedRecord_ReturnsNull()
    {
        // objCount=1, disc+flags present, then a string length with no bytes → rejected whole.
        var truncated = new byte[]
        {
            0x01, 0x00,             // objCount = 1
            0x01, 0x00,             // disc=1, flags=0
            0x04, 0x00,             // givenByLen = 4
                                    // (no bytes) — truncated
        };
        Assert.Null(ObjectivesSnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_RejectsMissingVariableTable_ReturnsNull()
    {
        // A valid record array with the variable table chopped off is NOT a legacy payload (the table
        // is part of the v1 wire) → reject, never guess.
        var snap = new ObjectivesSnapshot();
        snap.Objectives.Add(Rec(ObjectivesSnapshot.DiscEvent, payload: "E"));
        var wire = ObjectivesSnapshot.Encode(snap);
        var chopped = new byte[wire.Length - 2];   // drop [u16 varCount]
        System.Array.Copy(wire, chopped, chopped.Length);
        Assert.Null(ObjectivesSnapshot.Decode(chopped));
    }

    [Fact]
    public void Encode_NullSnapshot_ReturnsNull() => Assert.Null(ObjectivesSnapshot.Encode(null));

    [Fact]
    public void Encode_StableWireBytes_Pinned()
    {
        // Pin the EXACT wire layout. One Event record (critical+completed+titlePresent) + one variable.
        var snap = new ObjectivesSnapshot();
        snap.Objectives.Add(Rec(ObjectivesSnapshot.DiscEvent,
            (byte)(ObjectivesSnapshot.FlagCritical | ObjectivesSnapshot.FlagCompleted | ObjectivesSnapshot.FlagTitlePresent),
            givenBy: "G", title: "T", desc: "", payload: "E"));
        snap.Variables.Add(("V", 7));

        var bytes = ObjectivesSnapshot.Encode(snap);

        var expected = new byte[]
        {
            0x01, 0x00,                 // objCount = 1
            0x01,                       // disc = Event
            0x13,                       // flags = critical|completed|titlePresent (1|2|16)
            0x01, 0x00, 0x47,           // givenByLen=1, "G"
            0x01, 0x00, 0x54,           // titleLen=1, "T"
            0x00, 0x00,                 // descLen=0
            0x01, 0x00, 0x45,           // payloadLen=1, "E"
            0x00, 0x00, 0x00, 0x00,     // aux = 0 (i32 LE)
            0x01, 0x00,                 // varCount = 1
            0x01, 0x00, 0x56,           // nameLen=1, "V"
            0x07, 0x00, 0x00, 0x00,     // value 7 (i32 LE)
        };
        Assert.Equal(expected, bytes);
    }

    // ─── registration: channel #7 claims the reserved id, distinct from every sibling ────
    [Fact]
    public void ChannelId_Is7_AndDistinctFromOtherChannels()
    {
        Assert.Equal((byte)7, SurfaceIds.ObjectivesChannel);
        var ids = new[] { SurfaceIds.InventoryChannel, SurfaceIds.ResearchChannel,
                          SurfaceIds.UnlockChannel, SurfaceIds.DiplomacyChannel,
                          SurfaceIds.GeoSiteChannel, SurfaceIds.GeoVehicleChannel,
                          SurfaceIds.ObjectivesChannel, SurfaceIds.MistChannel };
        Assert.Equal(ids.Length, new System.Collections.Generic.HashSet<byte>(ids).Count);
    }
}
