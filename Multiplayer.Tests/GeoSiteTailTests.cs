using System.Linq;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// WA-2 optional-tail extension of the GeoSite channel (#5) — spec 2026-07-05 §5 WA-2 (audit gaps
/// 4b/4d/3c/1c). Covers the versioned EXTRAS BLOCK codec (haven tail), the older-format tolerance pins
/// (pre-WA-2 payload → null tails; no-tail payload → byte-identical pre-WA-2 wire), the
/// parse-known-then-skip forward-compat contract, and the dirty-subscription decision pin
/// (GeoMap aggregate event names the channel binds).
/// </summary>
public class GeoSiteTailTests
{
    private static GeoSiteSnapshot RoundTrip(GeoSiteSnapshot snap)
        => GeoSiteSnapshot.Decode(GeoSiteSnapshot.Encode(snap));

    private static GeoSiteState Site(int id, GeoHavenTail haven = null)
        => new GeoSiteState(id, "o", 20, 1, "n", "e", haven: haven);

    // ─── haven tail round-trip ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2450, false)]
    [InlineData(int.MaxValue, true)]
    public void HavenTail_RoundTrips(int population, bool infested)
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(Site(7, new GeoHavenTail(population, infested)));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].Haven);
        Assert.Equal(population, rt.Sites[0].Haven.Population);
        Assert.Equal(infested, rt.Sites[0].Haven.Infested);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    [Fact]
    public void HavenTail_NullStaysNull_AndMixesWithCarriedTails()
    {
        // Mixed payload: only the haven site carries a tail; the POI keeps null (not carried ≠ cleared).
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(Site(1));                                     // plain POI — no tail
        snap.Sites.Add(Site(2, new GeoHavenTail(1800, false)));      // haven
        snap.Sites.Add(Site(3));                                     // plain POI — no tail

        var rt = RoundTrip(snap);

        Assert.Null(rt.Sites[0].Haven);
        Assert.NotNull(rt.Sites[1].Haven);
        Assert.Equal(1800, rt.Sites[1].Haven.Population);
        Assert.Null(rt.Sites[2].Haven);
    }

    [Fact]
    public void HavenTail_RidesFullIdentityRecord()
    {
        // The tail must coexist with every existing per-record field (explored flags + mission record).
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(9, "AN_FAC", 20, 1, "KEY_HAVEN", "ENC",
            inspected: true, visible: true, visited: true,
            mission: new GeoMissionRecord(GeoMissionRecord.HavenDefense, "DEF", "ALN", 1200, 800, "ZONE", new[] { 3 }),
            haven: new GeoHavenTail(970, true)));

        var rt = RoundTrip(snap);

        Assert.Equal(snap.Sites[0], rt.Sites[0]);
        Assert.Equal(970, rt.Sites[0].Haven.Population);
        Assert.True(rt.Sites[0].Haven.Infested);
        Assert.NotNull(rt.Sites[0].Mission);
    }

    // ─── older-format tolerance (the versioned-extension contract) ──────────────────────────────

    [Fact]
    public void PreWa2Payload_DecodesWithNullTails()
    {
        // EXACT pre-WA-2 wire bytes (the pinned v1 layout from Encode_StableWireBytes_Pinned): a newer
        // decoder must accept them and yield null tails — never reject an older host's payload.
        var v1 = new byte[]
        {
            0x01, 0x00,                 // count = 1
            0x01, 0x00, 0x00, 0x00,     // SiteId = 1 (i32 LE)
            0x01, 0x00, 0x41,           // ownerLen=1, "A"
            0x0A,                       // SiteType = 10
            0x01,                       // State = 1
            0x01, 0x00, 0x42,           // nameLen=1, "B"
            0x01, 0x00, 0x43,           // encLen=1, "C"
            0x00,                       // exploredFlags = 0
            0x00,                       // missionClass = 0 (tombstone)
        };

        var snap = GeoSiteSnapshot.Decode(v1);

        Assert.NotNull(snap);
        Assert.Single(snap.Sites);
        Assert.Equal(1, snap.Sites[0].SiteId);
        Assert.Null(snap.Sites[0].Haven);
    }

    [Fact]
    public void NoTailPayload_IsByteIdenticalToPreWa2Wire()
    {
        // A snapshot with NO tails must not emit the extras block at all — older decoders read it
        // byte-for-byte as before (and the existing pinned-wire tests keep pinning the same bytes).
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 10, 1, "B", "C"));

        var bytes = GeoSiteSnapshot.Encode(snap);

        Assert.Equal(19, bytes.Length);              // exactly the pinned v1 record — no trailing block
        Assert.Equal(0x00, bytes[bytes.Length - 1]); // still ends at the mission tombstone byte
    }

    [Fact]
    public void ExtrasBlock_WireBytes_Pinned()
    {
        // Pin the EXACT extras-block layout: [u16 count][i32 siteId][u16 recLen][u8 flags][i32 population].
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 20, 1, "B", "C", haven: new GeoHavenTail(2450, true)));

        var bytes = GeoSiteSnapshot.Encode(snap);

        var extras = new byte[]
        {
            0x01, 0x00,                 // extrasCount = 1
            0x01, 0x00, 0x00, 0x00,     // siteId = 1 (i32 LE)
            0x05, 0x00,                 // recLen = 5 (flags + i32 population)
            0x11,                       // tailFlags = bit0 HasHaven | bit4 Infested
            0x92, 0x09, 0x00, 0x00,     // population = 2450 (i32 LE)
        };
        Assert.Equal(19 + extras.Length, bytes.Length);
        Assert.Equal(extras, bytes.Skip(19).ToArray());
    }

    [Fact]
    public void ExtrasRecord_UnknownFutureBits_SkippedNotRejected()
    {
        // Forward-compat contract: a record whose flags carry an UNKNOWN higher bit (future tail) with its
        // payload bytes after the known ones must still parse the known tails and skip the rest via recLen.
        var known = new GeoSiteSnapshot();
        known.Sites.Add(Site(1, new GeoHavenTail(500, false)));
        var bytes = GeoSiteSnapshot.Encode(known);

        // Rewrite the extras record: flags |= bit6 (unknown), append 3 payload bytes, recLen 5 → 8.
        var patched = new byte[bytes.Length + 3];
        System.Array.Copy(bytes, patched, bytes.Length);
        int recLenAt = 19 + 2 + 4;              // after record array + extrasCount + siteId
        patched[recLenAt] = 0x08;               // recLen = 8
        patched[recLenAt + 2] |= 0x40;          // flags |= unknown bit6
        // 3 unknown payload bytes appended at the end (they belong to the record slice).
        patched[bytes.Length] = 0xDE; patched[bytes.Length + 1] = 0xAD; patched[bytes.Length + 2] = 0xBF;

        var snap = GeoSiteSnapshot.Decode(patched);

        Assert.NotNull(snap);
        Assert.NotNull(snap.Sites[0].Haven);
        Assert.Equal(500, snap.Sites[0].Haven.Population);
    }

    [Fact]
    public void ExtrasBlock_Truncated_RejectsWholePayload()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(Site(1, new GeoHavenTail(500, false)));
        var bytes = GeoSiteSnapshot.Encode(snap);

        var truncated = new byte[bytes.Length - 2];   // cut inside the population i32
        System.Array.Copy(bytes, truncated, truncated.Length);

        Assert.Null(GeoSiteSnapshot.Decode(truncated));
    }

    [Fact]
    public void ExtrasRecord_ForUnknownSiteId_Ignored()
    {
        // Join-by-id: an extras record whose siteId is absent from the record array is skipped (never a throw).
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(Site(1, new GeoHavenTail(500, false)));
        var bytes = GeoSiteSnapshot.Encode(snap);
        bytes[19 + 2] = 0x63;   // extras siteId 1 → 99 (no such record)

        var rt = GeoSiteSnapshot.Decode(bytes);

        Assert.NotNull(rt);
        Assert.Null(rt.Sites[0].Haven);   // tail dropped with its unknown id — identity intact
    }

    // ─── equality / idempotence pins ────────────────────────────────────────────────────────────

    [Fact]
    public void HavenTail_ParticipatesInEquality()
    {
        var baseline = Site(3);
        Assert.NotEqual(baseline, Site(3, new GeoHavenTail(100, false)));
        Assert.NotEqual(Site(3, new GeoHavenTail(100, false)), Site(3, new GeoHavenTail(101, false)));
        Assert.NotEqual(Site(3, new GeoHavenTail(100, false)), Site(3, new GeoHavenTail(100, true)));
        Assert.Equal(Site(3, new GeoHavenTail(100, true)), Site(3, new GeoHavenTail(100, true)));
    }

    // ─── alien-base tail (commit 2, gap 4b) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("NEST_GUID", new string[0])]
    [InlineData("LAIR_GUID", new[] { "SPAWNERY_GUID" })]
    [InlineData("CITADEL_GUID", new[] { "A_GUID", "B_GUID", "C_GUID" })]
    [InlineData("", new[] { "A_GUID" })]   // unreadable type still carries addons (client skips type stamp)
    public void AlienBaseTail_RoundTrips(string typeGuid, string[] addons)
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(4, "o", 40, 1, "n", "e",
            alienBase: new GeoAlienBaseTail(typeGuid, addons)));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].AlienBase);
        Assert.Equal(typeGuid, rt.Sites[0].AlienBase.TypeDefGuid);
        Assert.Equal(addons, rt.Sites[0].AlienBase.AddonDefGuids);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    [Fact]
    public void AlienBaseTail_EmptyAddons_IsHonestClear_DistinctFromAbsent()
    {
        // Empty addon list must survive as EMPTY (a clear), never collapse into "tail absent".
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 40, 1, "n", "e", alienBase: new GeoAlienBaseTail("T", new string[0])));
        snap.Sites.Add(new GeoSiteState(2, "o", 40, 1, "n", "e"));   // no tail at all

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].AlienBase);
        Assert.Empty(rt.Sites[0].AlienBase.AddonDefGuids);
        Assert.Null(rt.Sites[1].AlienBase);
    }

    // ─── excavation tail (commit 2, gap 3c) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 637500000000000000L)]   // digging: end date carried
    [InlineData(false, 0L)]                   // completed: IsExcavated=true on the client, dates Zero
    public void ExcavationTail_RoundTrips(bool excavating, long ticks)
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(6, "o", 90, 1, "n", "e",
            excavation: new GeoExcavationTail(excavating, ticks)));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].Excavation);
        Assert.Equal(excavating, rt.Sites[0].Excavation.Excavating);
        Assert.Equal(ticks, rt.Sites[0].Excavation.EndDateTicks);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    // ─── combined tails ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllTails_CoexistOnOneRecord_AndPerSiteIndependently()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 20, 1, "n", "e",
            haven: new GeoHavenTail(1200, true),
            alienBase: new GeoAlienBaseTail("TYPE", new[] { "ADDON" }),
            excavation: new GeoExcavationTail(true, 42L)));
        snap.Sites.Add(new GeoSiteState(2, "o", 40, 1, "n", "e",
            alienBase: new GeoAlienBaseTail("LAIR", null)));
        snap.Sites.Add(new GeoSiteState(3, "o", 90, 1, "n", "e",
            excavation: new GeoExcavationTail(false, 0L)));

        var rt = RoundTrip(snap);

        Assert.Equal(snap.Sites[0], rt.Sites[0]);
        Assert.Equal(snap.Sites[1], rt.Sites[1]);
        Assert.Equal(snap.Sites[2], rt.Sites[2]);
        Assert.Null(rt.Sites[1].Haven);
        Assert.Null(rt.Sites[1].Excavation);
        Assert.Null(rt.Sites[2].AlienBase);
    }

    [Fact]
    public void NewTails_ParticipateInEquality()
    {
        var baseline = Site(3);
        Assert.NotEqual(baseline, new GeoSiteState(3, "o", 20, 1, "n", "e", alienBase: new GeoAlienBaseTail("T")));
        Assert.NotEqual(baseline, new GeoSiteState(3, "o", 20, 1, "n", "e", excavation: new GeoExcavationTail(true, 1)));
        Assert.NotEqual(new GeoAlienBaseTail("T", new[] { "A" }), new GeoAlienBaseTail("T", new[] { "B" }));
        Assert.NotEqual(new GeoAlienBaseTail("T"), new GeoAlienBaseTail("U"));
        Assert.NotEqual(new GeoExcavationTail(true, 1), new GeoExcavationTail(false, 1));
        Assert.NotEqual(new GeoExcavationTail(true, 1), new GeoExcavationTail(true, 2));
        Assert.Equal(new GeoAlienBaseTail("T", new[] { "A" }), new GeoAlienBaseTail("T", new[] { "A" }));
        Assert.Equal(new GeoExcavationTail(true, 1), new GeoExcavationTail(true, 1));
    }

    [Fact]
    public void CombinedTails_TruncatedInsideExcavation_RejectsWholePayload()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 20, 1, "n", "e",
            haven: new GeoHavenTail(5, false), excavation: new GeoExcavationTail(true, long.MaxValue)));
        var bytes = GeoSiteSnapshot.Encode(snap);
        var truncated = new byte[bytes.Length - 4];   // cut inside the i64 end-date
        System.Array.Copy(bytes, truncated, truncated.Length);
        Assert.Null(GeoSiteSnapshot.Decode(truncated));
    }

    // ─── dirty-subscription decision pins (WA-2 families join the bind lists) ───────────────────

    [Fact]
    public void SubscribedGeoMapEvents_IncludeHavenFamily()
    {
        var names = GeoSiteDirtyEvents.GeoMapEventNames;
        Assert.Contains("HavenPopulationChanged", names);
        Assert.Contains("HavenPopulationZoneAttrition", names);
        Assert.Contains("HavenInfestationStateChanged", names);
        // Pre-WA-2 bindings must survive (identity + explored + mission families).
        Assert.Contains("SiteOwnerChanged", names);
        Assert.Contains("SiteStateChanged", names);
        Assert.Contains("SiteMissionStarted", names);
        // WA-2 commit 2: alien-base family.
        Assert.Contains("SiteAddonsChanged", names);
        Assert.Contains("SiteAlienBaseTypeChanged", names);
        Assert.Equal(15, names.Length);
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void SubscribedPhoenixFactionEvents_AreTheExcavationPair()
    {
        var names = GeoSiteDirtyEvents.PhoenixFactionEventNames;
        Assert.Equal(new[] { "OnExcavationStarted", "OnExcavationCompleted" }, names);
    }
}
