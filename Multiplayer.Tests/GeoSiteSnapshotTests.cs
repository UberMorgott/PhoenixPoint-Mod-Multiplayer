using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip tests for the GeoSite state-replication channel (#5) snapshot codec: per-site identity
/// records (SiteId, OwnerFactionDefGuid, SiteType, State, SiteName loc-key, EncounterID). Only the pure
/// encode/decode path is exercised; Snapshot/Apply bind live game types (GeoMap site events + GeoSite
/// fields by reflection) and are not unit-testable. Mirrors <see cref="DiplomacyChannelTests"/> /
/// <see cref="ResearchChannelTests"/>.
/// </summary>
public class GeoSiteSnapshotTests
{
    private static GeoSiteSnapshot RoundTrip(GeoSiteSnapshot snap)
        => GeoSiteSnapshot.Decode(GeoSiteSnapshot.Encode(snap));

    [Fact]
    public void Snapshot_RoundTrips_Sites()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(42, "PX_FactionDef", 10, 1, "KEY_PHOENIX_BASE", "ENC_intro"));
        snap.Sites.Add(new GeoSiteState(7, "AN_FactionDef", 20, 4, "KEY_HAVEN", ""));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(2, rt.Sites.Count);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
        Assert.Equal(snap.Sites[1], rt.Sites[1]);
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new GeoSiteSnapshot());
        Assert.NotNull(rt);
        Assert.Empty(rt.Sites);
    }

    [Fact]
    public void Snapshot_RoundTrips_NullAndEmptyStrings()
    {
        var snap = new GeoSiteSnapshot();
        // null owner-guid / null name / null encounter must round-trip to empty strings (never throw).
        snap.Sites.Add(new GeoSiteState(1, null, 0, 0, null, null));
        snap.Sites.Add(new GeoSiteState(2, "", 110, 2, "", ""));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt);
        Assert.Equal(2, rt.Sites.Count);
        // null encodes as "" — decode yields "".
        Assert.Equal(new GeoSiteState(1, "", 0, 0, "", ""), rt.Sites[0]);
        Assert.Equal(new GeoSiteState(2, "", 110, 2, "", ""), rt.Sites[1]);
    }

    [Fact]
    public void Snapshot_PreservesOrder_AndEnumByteValues()
    {
        var snap = new GeoSiteSnapshot();
        // Type/State carry the raw ENUM integer value (NOT an ordinal): Type up to 110, State up to 4.
        snap.Sites.Add(new GeoSiteState(100, "a", 110, 4, "n0", "e0")); // Marketplace / Abandoned
        snap.Sites.Add(new GeoSiteState(101, "b", 0, 0, "n1", "e1"));   // None / None
        snap.Sites.Add(new GeoSiteState(102, "c", 40, 1, "n2", "e2"));  // AlienBase / Functioning

        var rt = RoundTrip(snap);

        Assert.Equal(new[] { 100, 101, 102 }, rt.Sites.ConvertAll(x => x.SiteId).ToArray());
        Assert.Equal((byte)110, rt.Sites[0].SiteType);
        Assert.Equal((byte)4, rt.Sites[0].State);
        Assert.Equal((byte)40, rt.Sites[2].SiteType);
    }

    [Fact]
    public void Snapshot_RoundTrips_NegativeSiteId()
    {
        // Unassigned sites carry SiteId = -1 (GeoSite default); must survive the i32 round-trip.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(-1, "o", 10, 1, "n", "e"));

        var rt = RoundTrip(snap);
        Assert.Equal(-1, rt.Sites[0].SiteId);
    }

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
        => Assert.Null(GeoSiteSnapshot.Decode(new byte[] { 0xFF }));

    [Fact]
    public void Decode_RejectsTruncatedString_ReturnsNull()
    {
        // count=1, siteId=1, ownerLen=4 but no owner bytes follow → rejected (null, not garbage).
        var truncated = new byte[]
        {
            0x01, 0x00,                 // count = 1
            0x01, 0x00, 0x00, 0x00,     // SiteId = 1 (i32 LE)
            0x04, 0x00,                 // ownerLen = 4
                                        // (no owner bytes) — truncated
        };
        Assert.Null(GeoSiteSnapshot.Decode(truncated));
    }

    [Fact]
    public void Encode_NullSnapshot_ReturnsNull() => Assert.Null(GeoSiteSnapshot.Encode(null));

    [Fact]
    public void Encode_StableWireBytes_Pinned()
    {
        // Pin the EXACT wire layout. One site: id 1, owner "A", type 10, state 1, name "B", encounter "C".
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 10, 1, "B", "C"));

        var bytes = GeoSiteSnapshot.Encode(snap);

        var expected = new byte[]
        {
            0x01, 0x00,                 // count = 1
            0x01, 0x00, 0x00, 0x00,     // SiteId = 1 (i32 LE)
            0x01, 0x00, 0x41,           // ownerLen=1, "A"
            0x0A,                       // SiteType = 10
            0x01,                       // State = 1
            0x01, 0x00, 0x42,           // nameLen=1, "B"
            0x01, 0x00, 0x43,           // encLen=1, "C"
            0x00,                       // exploredFlags = 0 (not inspected/visible/visited)
            0x00,                       // missionClass = 0 (no active mission — tombstone)
        };
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Encode_ExploredFlagsByte_Pinned()
    {
        // bit0=Inspected bit1=Visible bit2=Visited — pin the packing so host/client never disagree.
        // (The flags byte sits directly before the trailing mission-class byte, 0x00 = no mission.)
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 10, 1, "B", "C", inspected: true, visible: true, visited: true));
        var bytes = GeoSiteSnapshot.Encode(snap);
        Assert.Equal(0x07, bytes[bytes.Length - 2]);   // flags byte = all three set
        Assert.Equal(0x00, bytes[bytes.Length - 1]);   // mission tail = tombstone

        snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 10, 1, "B", "C", inspected: false, visible: true, visited: false));
        bytes = GeoSiteSnapshot.Encode(snap);
        Assert.Equal(0x02, bytes[bytes.Length - 2]);   // Visible only → bit1
    }

    [Fact]
    public void Snapshot_RoundTrips_InspectedFlag()
    {
        // The per-faction reveal flag (exploration outcome) must survive the round-trip both ways.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e", inspected: true));
        snap.Sites.Add(new GeoSiteState(2, "o", 10, 1, "n", "e", inspected: false));

        var rt = RoundTrip(snap);

        Assert.True(rt.Sites[0].Inspected);
        Assert.False(rt.Sites[1].Inspected);
        // Inspected participates in structural equality (distinguishes an otherwise-identical revealed site).
        Assert.NotEqual(snap.Sites[0], snap.Sites[1]);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    // ─── explored-state family: host exploration (SetInspected+SetVisited) + reveal-around (SetVisible)
    //     must carry per-flag, independently, in BOTH directions (true and false). ─────────────────────
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]   // inspected only (reveal via RevealSite on an already-visible site)
    [InlineData(false, true, false)]   // newly revealed-around POI: visible, still "?" (not inspected)
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]     // explored POI: inspected + visible + visited
    public void Snapshot_RoundTrips_ExploredStateFamily(bool inspected, bool visible, bool visited)
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(9, "o", 60, 1, "n", "e", inspected, visible, visited));

        var rt = RoundTrip(snap);

        Assert.Equal(inspected, rt.Sites[0].Inspected);
        Assert.Equal(visible, rt.Sites[0].Visible);
        Assert.Equal(visited, rt.Sites[0].Visited);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);   // structural equality covers the whole family
    }

    [Fact]
    public void ExploredStateFamily_FlagsAreIndependentInEquality()
    {
        // Each flag alone must distinguish two otherwise-identical DTOs (no flag aliases another).
        var baseline = new GeoSiteState(3, "o", 60, 1, "n", "e");
        Assert.NotEqual(baseline, new GeoSiteState(3, "o", 60, 1, "n", "e", inspected: true));
        Assert.NotEqual(baseline, new GeoSiteState(3, "o", 60, 1, "n", "e", visible: true));
        Assert.NotEqual(baseline, new GeoSiteState(3, "o", 60, 1, "n", "e", visited: true));
        Assert.NotEqual(new GeoSiteState(3, "o", 60, 1, "n", "e", visible: true),
                        new GeoSiteState(3, "o", 60, 1, "n", "e", visited: true));
    }

    // ─── P1 ActiveMission mirror record (mission-state DTO round-trip, 2026-07-05 popup-mirror spec) ────

    [Fact]
    public void MissionRecord_RoundTrips_AllClasses_MinimalRecord()
    {
        // Every mapped class round-trips with just (class, defGuid) — the pure-base-ctor family.
        var classes = new byte[]
        {
            GeoMissionRecord.HavenDefense, GeoMissionRecord.AlienBase, GeoMissionRecord.AlienBaseAssault,
            GeoMissionRecord.PhoenixBaseDefense, GeoMissionRecord.PhoenixBaseInfestation,
            GeoMissionRecord.InfestationCleanse, GeoMissionRecord.Scavenging, GeoMissionRecord.Ambush,
            GeoMissionRecord.AncientSite, GeoMissionRecord.Unknown,
        };
        var snap = new GeoSiteSnapshot();
        for (int i = 0; i < classes.Length; i++)
            snap.Sites.Add(new GeoSiteState(i, "o", 60, 1, "n", "e",
                mission: new GeoMissionRecord(classes[i], "DEF_" + i)));

        var rt = RoundTrip(snap);

        Assert.Equal(classes.Length, rt.Sites.Count);
        for (int i = 0; i < classes.Length; i++)
        {
            Assert.NotNull(rt.Sites[i].Mission);
            Assert.Equal(classes[i], rt.Sites[i].Mission.MissionClass);
            Assert.Equal("DEF_" + i, rt.Sites[i].Mission.MissionDefGuid);
            Assert.Equal(snap.Sites[i], rt.Sites[i]);
        }
    }

    [Fact]
    public void MissionRecord_RoundTrips_HavenDefenseRuntimeBits()
    {
        // Brief-0 bits: attacker faction + deployments + attacked zone + participating sites.
        var rec = new GeoMissionRecord(GeoMissionRecord.HavenDefense, "TAC_DEF_GUID",
            attackerFactionDefGuid: "ALIEN_PPFAC_GUID", attackerDeployment: 1200, defenderDeployment: 800,
            attackedZoneDefGuid: "ZONE_GUID", attackingSiteIds: new[] { 3, 17, 42 });
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(5, "o", 20, 1, "n", "e", mission: rec));

        var rt = RoundTrip(snap);

        var m = rt.Sites[0].Mission;
        Assert.Equal(rec, m);
        Assert.Equal(1200, m.AttackerDeployment);
        Assert.Equal(800, m.DefenderDeployment);
        Assert.Equal("ZONE_GUID", m.AttackedZoneDefGuid);
        Assert.Equal(new[] { 3, 17, 42 }, m.AttackingSiteIds);
    }

    [Fact]
    public void MissionRecord_RoundTrips_PhoenixBaseDefenseAttackingSites()
    {
        // Brief-11 bits: enemy faction + attackingSites ids (the runtime data the ctor needed live).
        var rec = new GeoMissionRecord(GeoMissionRecord.PhoenixBaseDefense, "TAC_DEF_GUID",
            attackerFactionDefGuid: "ALN_PPFAC", attackingSiteIds: new[] { 101, 102 });
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(8, "o", 10, 1, "n", "e", mission: rec));

        var rt = RoundTrip(snap);
        Assert.Equal(rec, rt.Sites[0].Mission);
        Assert.Equal(new[] { 101, 102 }, rt.Sites[0].Mission.AttackingSiteIds);
    }

    [Fact]
    public void MissionRecord_Tombstone_NullMission_RoundTripsAsNull()
    {
        // Absence = tombstone: a site whose mission cleared carries a NULL record — the client must be able
        // to distinguish "no mission" from any live record.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e", mission: null));
        snap.Sites.Add(new GeoSiteState(2, "o", 10, 1, "n", "e",
            mission: new GeoMissionRecord(GeoMissionRecord.Scavenging, "DEF")));

        var rt = RoundTrip(snap);

        Assert.Null(rt.Sites[0].Mission);
        Assert.NotNull(rt.Sites[1].Mission);
        Assert.NotEqual(rt.Sites[0], new GeoSiteState(1, "o", 10, 1, "n", "e",
            mission: new GeoMissionRecord(GeoMissionRecord.Scavenging, "DEF")));
    }

    [Fact]
    public void MissionRecord_ClassZero_EncodesAsTombstone()
    {
        // Class 0 is the wire sentinel for "no mission" — a (mis)constructed record with class 0 must never
        // alias a live record on the other side.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e", mission: new GeoMissionRecord(0, "DEF")));
        var rt = RoundTrip(snap);
        Assert.Null(rt.Sites[0].Mission);
    }

    [Fact]
    public void MissionRecord_EqualityDistinguishesEveryField()
    {
        var baseline = new GeoMissionRecord(GeoMissionRecord.HavenDefense, "D", "F", 10, 20, "Z", new[] { 1 });
        Assert.NotEqual(baseline, new GeoMissionRecord(GeoMissionRecord.AlienBase, "D", "F", 10, 20, "Z", new[] { 1 }));
        Assert.NotEqual(baseline, new GeoMissionRecord(GeoMissionRecord.HavenDefense, "X", "F", 10, 20, "Z", new[] { 1 }));
        Assert.NotEqual(baseline, new GeoMissionRecord(GeoMissionRecord.HavenDefense, "D", "X", 10, 20, "Z", new[] { 1 }));
        Assert.NotEqual(baseline, new GeoMissionRecord(GeoMissionRecord.HavenDefense, "D", "F", 11, 20, "Z", new[] { 1 }));
        Assert.NotEqual(baseline, new GeoMissionRecord(GeoMissionRecord.HavenDefense, "D", "F", 10, 21, "Z", new[] { 1 }));
        Assert.NotEqual(baseline, new GeoMissionRecord(GeoMissionRecord.HavenDefense, "D", "F", 10, 20, "X", new[] { 1 }));
        Assert.NotEqual(baseline, new GeoMissionRecord(GeoMissionRecord.HavenDefense, "D", "F", 10, 20, "Z", new[] { 2 }));
        Assert.Equal(baseline, new GeoMissionRecord(GeoMissionRecord.HavenDefense, "D", "F", 10, 20, "Z", new[] { 1 }));
    }

    [Fact]
    public void MissionRecord_TruncatedTail_RejectedAsNull()
    {
        // A payload cut inside the mission tail must reject (null), never yield a half-read record.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e",
            mission: new GeoMissionRecord(GeoMissionRecord.HavenDefense, "DEFGUID", "FAC", 1, 2, "ZONE", new[] { 9 })));
        var bytes = GeoSiteSnapshot.Encode(snap);
        var truncated = new byte[bytes.Length - 3];
        System.Array.Copy(bytes, truncated, truncated.Length);
        Assert.Null(GeoSiteSnapshot.Decode(truncated));
    }

    // ─── registration: the GeoSite channel claims a distinct, stable surface/channel id ────
    [Fact]
    public void ChannelId_Is5_AndDistinctFromOtherChannels()
    {
        Assert.Equal((byte)5, SurfaceIds.GeoSiteChannel);
        var ids = new[] { SurfaceIds.InventoryChannel, SurfaceIds.ResearchChannel,
                          SurfaceIds.UnlockChannel, SurfaceIds.DiplomacyChannel, SurfaceIds.GeoSiteChannel };
        Assert.Equal(ids.Length, new System.Collections.Generic.HashSet<byte>(ids).Count);
    }
}
