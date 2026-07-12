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
        // Pin the EXACT extras-block layout: [u16 count][i32 siteId][u16 recLen][u8 flags][i32 population][u8 stockCount].
        // (Empty stock here → stockCount 0; the haven payload rides bit0 with population then the stock list.)
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 20, 1, "B", "C", haven: new GeoHavenTail(2450, true)));

        var bytes = GeoSiteSnapshot.Encode(snap);

        var extras = new byte[]
        {
            0x01, 0x00,                 // extrasCount = 1
            0x01, 0x00, 0x00, 0x00,     // siteId = 1 (i32 LE)
            0x06, 0x00,                 // recLen = 6 (flags + i32 population + u8 stockCount)
            0x11,                       // tailFlags = bit0 HasHaven | bit4 Infested
            0x92, 0x09, 0x00, 0x00,     // population = 2450 (i32 LE)
            0x00,                       // stockCount = 0 (empty shelf)
        };
        Assert.Equal(19 + extras.Length, bytes.Length);
        Assert.Equal(extras, bytes.Skip(19).ToArray());
    }

    [Fact]
    public void ExtrasRecord_UnknownTrailingBytes_SkippedNotRejected()
    {
        // Forward-compat contract (now that all 8 tail-flag bits are assigned — bit6 weather, bit7 expiring):
        // a FUTURE format may APPEND extra bytes AFTER the known payloads and grow recLen; a current decoder
        // reads the known tails and skips the trailing bytes via the length-prefixed record slice — never
        // rejecting the payload.
        var known = new GeoSiteSnapshot();
        known.Sites.Add(Site(1, new GeoHavenTail(500, false)));
        var bytes = GeoSiteSnapshot.Encode(known);

        // Grow the extras record by 3 trailing bytes (recLen 6 → 9) without touching the flags byte.
        var patched = new byte[bytes.Length + 3];
        System.Array.Copy(bytes, patched, bytes.Length);
        int recLenAt = 19 + 2 + 4;              // after record array + extrasCount + siteId
        patched[recLenAt] = 0x09;               // recLen = 9 (flags + i32 population + u8 stockCount + 3 future bytes)
        patched[bytes.Length] = 0xDE; patched[bytes.Length + 1] = 0xAD; patched[bytes.Length + 2] = 0xBF;

        var snap = GeoSiteSnapshot.Decode(patched);

        Assert.NotNull(snap);
        Assert.NotNull(snap.Sites[0].Haven);
        Assert.Equal(500, snap.Sites[0].Haven.Population);
    }

    [Fact]
    public void ExtrasRecord_UnknownTrailingBytes_AfterNonEmptyStock_SkippedNotRejected()
    {
        // Forward-compat with a POPULATED shelf (E2, review of 89b52c7): the haven payload is
        // [flags][i32 population][u8 stockCount]{[i32 type][i32 amount]}*; a FUTURE format may append bytes AFTER
        // the stock list and grow recLen. Proves the reader consumes EXACTLY stockCount units (both survive,
        // value-for-value) and then skips the trailing bytes via the length-prefixed record slice — it never
        // mis-reads a future field as a stock unit and never rejects the payload.
        var known = new GeoSiteSnapshot();
        known.Sites.Add(Site(1, new GeoHavenTail(500, true, Stock((2, 340), (1, 120)))));
        var bytes = GeoSiteSnapshot.Encode(known);

        int recLenAt = 19 + 2 + 4;               // after record array + extrasCount + siteId
        Assert.Equal(22, bytes[recLenAt]);       // recLen = flags(1)+i32 pop(4)+u8 count(1)+2 units×8 = 22 (low byte)
        Assert.Equal(0, bytes[recLenAt + 1]);    // recLen high byte

        // Grow the extras record by 3 trailing bytes (recLen 22 → 25) AFTER the stock list, without touching flags.
        var patched = new byte[bytes.Length + 3];
        System.Array.Copy(bytes, patched, bytes.Length);
        patched[recLenAt] = 25;                  // recLen = 25 — extend the length-prefixed slice over the future bytes
        patched[bytes.Length] = 0xDE; patched[bytes.Length + 1] = 0xAD; patched[bytes.Length + 2] = 0xBF;

        var snap = GeoSiteSnapshot.Decode(patched);

        Assert.NotNull(snap);
        Assert.NotNull(snap.Sites[0].Haven);
        Assert.Equal(500, snap.Sites[0].Haven.Population);
        Assert.True(snap.Sites[0].Haven.Infested);
        Assert.Equal(2, snap.Sites[0].Haven.Stock.Length);        // exactly stockCount units — the tail was NOT read as a unit
        Assert.Equal(2, snap.Sites[0].Haven.Stock[0].ResourceType);
        Assert.Equal(340, snap.Sites[0].Haven.Stock[0].Amount);
        Assert.Equal(1, snap.Sites[0].Haven.Stock[1].ResourceType);
        Assert.Equal(120, snap.Sites[0].Haven.Stock[1].Amount);
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

    // ─── haven trade-stock mirror (audit gap 2, 2026-07-12) ─────────────────────────────────────

    private static HavenStockUnit[] Stock(params (int type, int amount)[] units)
    {
        var arr = new HavenStockUnit[units.Length];
        for (int i = 0; i < units.Length; i++) arr[i] = new HavenStockUnit(units[i].type, units[i].amount);
        return arr;
    }

    [Fact]
    public void HavenStock_RoundTrips()
    {
        // A haven trades Materials(2)/Supplies(1)/Tech(4) — the full shelf must survive value-for-value.
        var stock = Stock((2, 340), (1, 120), (4, 0));   // includes a zero-amount unit (honest mirror)
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(Site(7, new GeoHavenTail(1800, false, stock)));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].Haven);
        Assert.Equal(1800, rt.Sites[0].Haven.Population);
        Assert.Equal(stock, rt.Sites[0].Haven.Stock);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    [Fact]
    public void HavenStock_EmptyShelf_DistinctFromCarried()
    {
        // Empty stock (the 2-arg ctor default) round-trips as empty and stays equal — never null, never a phantom.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(Site(1, new GeoHavenTail(500, false)));                       // empty shelf
        snap.Sites.Add(Site(2, new GeoHavenTail(500, false, Stock((2, 99)))));       // one unit

        var rt = RoundTrip(snap);

        Assert.Empty(rt.Sites[0].Haven.Stock);
        Assert.Single(rt.Sites[1].Haven.Stock);
        Assert.Equal(99, rt.Sites[1].Haven.Stock[0].Amount);
        Assert.NotEqual(rt.Sites[0].Haven, rt.Sites[1].Haven);
    }

    [Fact]
    public void HavenStock_ParticipatesInEquality()
    {
        Assert.NotEqual(new GeoHavenTail(100, false), new GeoHavenTail(100, false, Stock((2, 1))));
        Assert.NotEqual(new GeoHavenTail(100, false, Stock((2, 1))), new GeoHavenTail(100, false, Stock((2, 2))));
        Assert.NotEqual(new GeoHavenTail(100, false, Stock((2, 1))), new GeoHavenTail(100, false, Stock((1, 1))));
        Assert.NotEqual(new GeoHavenTail(100, false, Stock((2, 1))), new GeoHavenTail(100, false, Stock((2, 1), (1, 1))));
        Assert.Equal(new GeoHavenTail(100, false, Stock((2, 1), (1, 3))), new GeoHavenTail(100, false, Stock((2, 1), (1, 3))));
        Assert.NotEqual(new HavenStockUnit(2, 1), new HavenStockUnit(2, 2));
        Assert.Equal(new HavenStockUnit(2, 1), new HavenStockUnit(2, 1));
    }

    [Fact]
    public void HavenStock_CoexistsWithAllOtherTails()
    {
        // Stock rides bit0 alongside population/infested AND every higher-bit tail on the same record.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 20, 1, "n", "e",
            haven: new GeoHavenTail(1200, true, Stock((2, 500), (1, 250))),
            alienBase: new GeoAlienBaseTail("TYPE", new[] { "ADDON" }),
            excavation: new GeoExcavationTail(true, 42L),
            weather: new GeoWeatherTail(4),
            expiringTimer: new GeoExpiringTimerTail(9999L)));

        var rt = RoundTrip(snap);

        Assert.Equal(snap.Sites[0], rt.Sites[0]);
        Assert.Equal(2, rt.Sites[0].Haven.Stock.Length);
        Assert.Equal(500, rt.Sites[0].Haven.Stock[0].Amount);
        Assert.NotNull(rt.Sites[0].AlienBase);
    }

    [Fact]
    public void HavenStock_TruncatedInsideUnit_RejectsWholePayload()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(Site(1, new GeoHavenTail(5, false, Stock((2, int.MaxValue)))));
        var bytes = GeoSiteSnapshot.Encode(snap);
        var truncated = new byte[bytes.Length - 2];   // cut inside the stock unit's i32 amount
        System.Array.Copy(bytes, truncated, truncated.Length);
        Assert.Null(GeoSiteSnapshot.Decode(truncated));
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

    // ─── attack-schedule tail (gap 6b: pre-attack countdown mirror) ─────────────────────────────

    [Theory]
    [InlineData("ALIEN_FAC", 637000000000000000L, 637000648000000000L)]
    [InlineData("NJ_FAC", 0L, 1L)]
    [InlineData("", long.MaxValue, long.MinValue)]   // unreadable faction guid still round-trips
    public void AttackTail_RoundTrips(string guid, long at, long forT)
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(5, "o", 10, 1, "n", "e",
            attack: new GeoAttackTail(new[] { new GeoAttackEntry(guid, at, forT) })));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].Attack);
        Assert.Single(rt.Sites[0].Attack.Entries);
        Assert.Equal(guid, rt.Sites[0].Attack.Entries[0].AttackerFactionDefGuid);
        Assert.Equal(at, rt.Sites[0].Attack.Entries[0].ScheduledAtTicks);
        Assert.Equal(forT, rt.Sites[0].Attack.Entries[0].ScheduledForTicks);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    [Fact]
    public void AttackTail_MultipleAttackers_RoundTrip()
    {
        // Two factions with concurrent armed schedules on the same site (alien + human aggressor).
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(5, "o", 10, 1, "n", "e",
            attack: new GeoAttackTail(new[]
            {
                new GeoAttackEntry("ALN", 100L, 200L),
                new GeoAttackEntry("NJ", 150L, 260L),
            })));

        var rt = RoundTrip(snap);

        Assert.Equal(2, rt.Sites[0].Attack.Entries.Length);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    [Fact]
    public void AttackTail_EmptyEntries_IsHonestClear_DistinctFromAbsent()
    {
        // Empty tail (schedule entries exist, none armed) must survive as EMPTY — the clear signal after
        // the attack fires — and never collapse into "tail absent" (no schedule entry at all).
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e", attack: new GeoAttackTail()));
        snap.Sites.Add(new GeoSiteState(2, "o", 10, 1, "n", "e"));   // no tail at all

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].Attack);
        Assert.Empty(rt.Sites[0].Attack.Entries);
        Assert.Null(rt.Sites[1].Attack);
    }

    [Fact]
    public void AttackTail_CoexistsWithAllOtherTails()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 20, 1, "n", "e",
            haven: new GeoHavenTail(1200, true),
            alienBase: new GeoAlienBaseTail("TYPE", new[] { "ADDON" }),
            excavation: new GeoExcavationTail(true, 42L),
            attack: new GeoAttackTail(new[] { new GeoAttackEntry("ALN", 1L, 2L) })));

        var rt = RoundTrip(snap);

        Assert.Equal(snap.Sites[0], rt.Sites[0]);
        Assert.Single(rt.Sites[0].Attack.Entries);
    }

    [Fact]
    public void AttackTail_ParticipatesInEquality()
    {
        var baseline = Site(3);
        Assert.NotEqual(baseline, new GeoSiteState(3, "o", 20, 1, "n", "e", attack: new GeoAttackTail()));
        Assert.NotEqual(new GeoAttackTail(), new GeoAttackTail(new[] { new GeoAttackEntry("A", 1, 2) }));
        Assert.NotEqual(new GeoAttackTail(new[] { new GeoAttackEntry("A", 1, 2) }),
                        new GeoAttackTail(new[] { new GeoAttackEntry("A", 1, 3) }));
        Assert.NotEqual(new GeoAttackTail(new[] { new GeoAttackEntry("A", 1, 2) }),
                        new GeoAttackTail(new[] { new GeoAttackEntry("B", 1, 2) }));
        Assert.Equal(new GeoAttackTail(new[] { new GeoAttackEntry("A", 1, 2) }),
                     new GeoAttackTail(new[] { new GeoAttackEntry("A", 1, 2) }));
    }

    [Fact]
    public void AttackTail_TruncatedInsideEntry_RejectsWholePayload()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e",
            attack: new GeoAttackTail(new[] { new GeoAttackEntry("ALN", 1L, long.MaxValue) })));
        var bytes = GeoSiteSnapshot.Encode(snap);
        var truncated = new byte[bytes.Length - 4];   // cut inside the trailing i64
        System.Array.Copy(bytes, truncated, truncated.Length);
        Assert.Null(GeoSiteSnapshot.Decode(truncated));
    }

    // ─── weather tail (gap 6f, bit6) + expiring-timer tail (bit7) ───────────────────────────────

    [Theory]
    [InlineData(0)]     // None
    [InlineData(2)]     // Mist
    [InlineData(3)]     // Overcast
    [InlineData(4)]     // Storm
    public void WeatherTail_RoundTrips(byte weather)
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(7, "o", 20, 1, "n", "e", weather: new GeoWeatherTail(weather)));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].Weather);
        Assert.Equal(weather, rt.Sites[0].Weather.Weather);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(637500000000000000L)]
    [InlineData(long.MaxValue)]
    public void ExpiringTimerTail_RoundTrips(long ticks)
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(8, "o", 10, 1, "n", "e", expiringTimer: new GeoExpiringTimerTail(ticks)));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].ExpiringTimer);
        Assert.Equal(ticks, rt.Sites[0].ExpiringTimer.ExpiringTicks);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    [Fact]
    public void WeatherAndExpiringTimer_CoexistWithAllOtherTails()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 20, 1, "n", "e",
            haven: new GeoHavenTail(1200, true),
            alienBase: new GeoAlienBaseTail("TYPE", new[] { "ADDON" }),
            excavation: new GeoExcavationTail(true, 42L),
            attack: new GeoAttackTail(new[] { new GeoAttackEntry("ALN", 1L, 2L) }),
            weather: new GeoWeatherTail(4),
            expiringTimer: new GeoExpiringTimerTail(9999L)));
        snap.Sites.Add(new GeoSiteState(2, "o", 10, 1, "n", "e", weather: new GeoWeatherTail(2)));   // weather only
        snap.Sites.Add(new GeoSiteState(3, "o", 10, 1, "n", "e"));                                    // no tail at all

        var rt = RoundTrip(snap);

        Assert.Equal(snap.Sites[0], rt.Sites[0]);
        Assert.Equal(4, rt.Sites[0].Weather.Weather);
        Assert.Equal(9999L, rt.Sites[0].ExpiringTimer.ExpiringTicks);
        Assert.NotNull(rt.Sites[1].Weather);
        Assert.Null(rt.Sites[1].ExpiringTimer);
        Assert.Null(rt.Sites[2].Weather);
        Assert.Null(rt.Sites[2].ExpiringTimer);
    }

    [Fact]
    public void WeatherExpiringTimer_ParticipateInEquality()
    {
        var baseline = Site(3);
        Assert.NotEqual(baseline, new GeoSiteState(3, "o", 20, 1, "n", "e", weather: new GeoWeatherTail(4)));
        Assert.NotEqual(baseline, new GeoSiteState(3, "o", 20, 1, "n", "e", expiringTimer: new GeoExpiringTimerTail(1)));
        Assert.NotEqual(new GeoWeatherTail(4), new GeoWeatherTail(2));
        Assert.Equal(new GeoWeatherTail(4), new GeoWeatherTail(4));
        Assert.NotEqual(new GeoExpiringTimerTail(1), new GeoExpiringTimerTail(2));
        Assert.Equal(new GeoExpiringTimerTail(1), new GeoExpiringTimerTail(1));
    }

    [Fact]
    public void WeatherExpiringTimer_WireBytes_Pinned()
    {
        // Pin the EXACT bits-6/7 extras layout: flags=0xC0, then the lower-bit payloads (none) followed by
        // [u8 weather][i64 expiringTicks] in ascending bit order.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 20, 1, "B", "C",
            weather: new GeoWeatherTail(4), expiringTimer: new GeoExpiringTimerTail(1)));

        var bytes = GeoSiteSnapshot.Encode(snap);

        var extras = new byte[]
        {
            0x01, 0x00,                                     // extrasCount = 1
            0x01, 0x00, 0x00, 0x00,                         // siteId = 1 (i32 LE)
            0x0A, 0x00,                                     // recLen = 10 (flags + u8 weather + i64 ticks)
            0xC0,                                           // tailFlags = bit6 HasWeather | bit7 HasExpiringTimer
            0x04,                                           // weather = 4 (Storm)
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // expiringTicks = 1 (i64 LE)
        };
        Assert.Equal(19 + extras.Length, bytes.Length);
        Assert.Equal(extras, bytes.Skip(19).ToArray());
    }

    [Fact]
    public void ExpiringTimerTail_TruncatedInsideTicks_RejectsWholePayload()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e", expiringTimer: new GeoExpiringTimerTail(long.MaxValue)));
        var bytes = GeoSiteSnapshot.Encode(snap);
        var truncated = new byte[bytes.Length - 4];   // cut inside the trailing i64
        System.Array.Copy(bytes, truncated, truncated.Length);
        Assert.Null(GeoSiteSnapshot.Decode(truncated));
    }

    // ─── W1 facility working-state tail (separate facility section, 2026-07-08 §W1.1) ───────────

    private static GeoFacilityEntry Fac(uint id, int gx, int gy, byte state, bool powered)
        => new GeoFacilityEntry(id, gx, gy, state, powered);

    [Theory]
    [InlineData(3, true)]    // Functioning + powered → working
    [InlineData(3, false)]   // Functioning but unpowered → NOT working (the power-deficit case)
    [InlineData(0, false)]   // UnderConstruction
    [InlineData(4, false)]   // Destroyed
    public void FacilityTail_RoundTrips(byte state, bool powered)
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(5, "o", 10, 1, "n", "e",
            facility: new GeoFacilityTail(new[] { Fac(42u, 1, 2, state, powered) })));

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].Facility);
        Assert.Single(rt.Sites[0].Facility.Entries);
        var e = rt.Sites[0].Facility.Entries[0];
        Assert.Equal(42u, e.FacilityId);
        Assert.Equal(1, e.GridX);
        Assert.Equal(2, e.GridY);
        Assert.Equal(state, e.State);
        Assert.Equal(powered, e.IsPowered);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
    }

    [Fact]
    public void FacilityTail_MultipleFacilities_RoundTrip()
    {
        // A base carries many facilities; each {id, grid, state, powered} must survive independently.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(5, "o", 10, 1, "n", "e", facility: new GeoFacilityTail(new[]
        {
            Fac(1u, 0, 0, 3, true),   // access lift, working
            Fac(2u, 1, 0, 3, false),  // lab, unpowered (power deficit)
            Fac(3u, 2, 0, 2, false),  // repairing
            Fac(uint.MaxValue, -1, -5, 4, false),
        })));

        var rt = RoundTrip(snap);

        Assert.Equal(4, rt.Sites[0].Facility.Entries.Length);
        Assert.Equal(snap.Sites[0], rt.Sites[0]);
        Assert.Equal(uint.MaxValue, rt.Sites[0].Facility.Entries[3].FacilityId);
        Assert.Equal(-5, rt.Sites[0].Facility.Entries[3].GridY);
    }

    [Fact]
    public void FacilityTail_EmptyEntries_IsHonestClear_DistinctFromAbsent()
    {
        // A base with an EMPTY facility list must survive as empty (last-wins), never collapse into "absent".
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e", facility: new GeoFacilityTail()));
        snap.Sites.Add(new GeoSiteState(2, "o", 60, 1, "n", "e"));   // POI — no facility tail at all

        var rt = RoundTrip(snap);

        Assert.NotNull(rt.Sites[0].Facility);
        Assert.Empty(rt.Sites[0].Facility.Entries);
        Assert.Null(rt.Sites[1].Facility);
    }

    [Fact]
    public void FacilityTail_NullStaysNull_AndMixesWithCarriedFacilities()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 60, 1, "n", "e"));                                 // POI
        snap.Sites.Add(new GeoSiteState(2, "o", 10, 1, "n", "e", facility: new GeoFacilityTail(new[] { Fac(9u, 0, 0, 3, true) }))); // base
        snap.Sites.Add(new GeoSiteState(3, "o", 60, 1, "n", "e"));                                 // POI

        var rt = RoundTrip(snap);

        Assert.Null(rt.Sites[0].Facility);
        Assert.NotNull(rt.Sites[1].Facility);
        Assert.Null(rt.Sites[2].Facility);
    }

    [Fact]
    public void FacilityTail_CoexistsWithAllOtherTails()
    {
        // The facility section must ride alongside a full per-record extras record on the SAME site.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e",
            haven: new GeoHavenTail(1200, true),
            alienBase: new GeoAlienBaseTail("TYPE", new[] { "ADDON" }),
            excavation: new GeoExcavationTail(true, 42L),
            attack: new GeoAttackTail(new[] { new GeoAttackEntry("ALN", 1L, 2L) }),
            weather: new GeoWeatherTail(4),
            expiringTimer: new GeoExpiringTimerTail(9999L),
            facility: new GeoFacilityTail(new[] { Fac(7u, 3, 4, 3, false) })));

        var rt = RoundTrip(snap);

        Assert.Equal(snap.Sites[0], rt.Sites[0]);
        Assert.NotNull(rt.Sites[0].Haven);
        Assert.NotNull(rt.Sites[0].Facility);
        Assert.Single(rt.Sites[0].Facility.Entries);
    }

    [Fact]
    public void FacilityTail_ParticipatesInEquality()
    {
        var baseline = new GeoSiteState(3, "o", 10, 1, "n", "e");
        Assert.NotEqual(baseline, new GeoSiteState(3, "o", 10, 1, "n", "e", facility: new GeoFacilityTail()));
        Assert.NotEqual(new GeoFacilityTail(), new GeoFacilityTail(new[] { Fac(1u, 0, 0, 3, true) }));
        Assert.NotEqual(new GeoFacilityTail(new[] { Fac(1u, 0, 0, 3, true) }),
                        new GeoFacilityTail(new[] { Fac(1u, 0, 0, 3, false) }));   // powered differs
        Assert.NotEqual(new GeoFacilityTail(new[] { Fac(1u, 0, 0, 3, true) }),
                        new GeoFacilityTail(new[] { Fac(1u, 0, 0, 2, true) }));    // state differs
        Assert.NotEqual(new GeoFacilityTail(new[] { Fac(1u, 0, 0, 3, true) }),
                        new GeoFacilityTail(new[] { Fac(2u, 0, 0, 3, true) }));    // id differs
        Assert.NotEqual(new GeoFacilityTail(new[] { Fac(1u, 0, 0, 3, true) }),
                        new GeoFacilityTail(new[] { Fac(1u, 9, 0, 3, true) }));    // grid differs
        Assert.Equal(new GeoFacilityTail(new[] { Fac(1u, 0, 0, 3, true) }),
                     new GeoFacilityTail(new[] { Fac(1u, 0, 0, 3, true) }));
    }

    [Fact]
    public void FacilityTail_WireBytes_Pinned()
    {
        // Pin the EXACT wire: a facility-only site emits the extras block with extrasCount=0 (so the facility
        // section is unambiguously ordered after it), then the facility section.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 10, 1, "B", "C",
            facility: new GeoFacilityTail(new[] { Fac(7u, 2, 3, 3, true) })));

        var bytes = GeoSiteSnapshot.Encode(snap);

        var tail = new byte[]
        {
            0x00, 0x00,                 // extrasCount = 0 (no per-record tail — but a facility section follows)
            0x01, 0x00,                 // facCount = 1
            0x01, 0x00, 0x00, 0x00,     // siteId = 1
            0x0F, 0x00,                 // recLen = 15 (nFac + one 14-byte entry)
            0x01,                       // nFac = 1
            0x07, 0x00, 0x00, 0x00,     // facilityId = 7 (u32 LE)
            0x02, 0x00, 0x00, 0x00,     // gridX = 2 (i32 LE)
            0x03, 0x00, 0x00, 0x00,     // gridY = 3 (i32 LE)
            0x03,                       // state = 3 (Functioning)
            0x01,                       // powered = 1
        };
        Assert.Equal(19 + tail.Length, bytes.Length);
        Assert.Equal(tail, bytes.Skip(19).ToArray());
    }

    [Fact]
    public void PreW1Payload_ExtrasOnly_DecodesWithNullFacility()
    {
        // A haven-only extras payload (no facility section) must decode with a null Facility — an older host's
        // wire is never rejected and never grows a phantom facility tail.
        var v = new GeoSiteSnapshot();
        v.Sites.Add(new GeoSiteState(1, "A", 20, 1, "B", "C", haven: new GeoHavenTail(2450, true)));
        var bytes = GeoSiteSnapshot.Encode(v);   // extras block only, no facility section

        var rt = GeoSiteSnapshot.Decode(bytes);

        Assert.NotNull(rt);
        Assert.NotNull(rt.Sites[0].Haven);
        Assert.Null(rt.Sites[0].Facility);
    }

    [Fact]
    public void FacilitySection_UnknownTrailingBytes_SkippedNotRejected()
    {
        // Forward-compat: a FUTURE format may append per-record fields after the known entries and grow recLen;
        // a current decoder reads the known entries and skips the trailing bytes via the length-prefixed slice.
        var known = new GeoSiteSnapshot();
        known.Sites.Add(new GeoSiteState(1, "A", 10, 1, "B", "C",
            facility: new GeoFacilityTail(new[] { Fac(7u, 2, 3, 3, true) })));
        var bytes = GeoSiteSnapshot.Encode(known);

        // Grow the facility record by 3 trailing bytes (recLen 15 → 18) without touching nFac / the entry.
        var patched = new byte[bytes.Length + 3];
        System.Array.Copy(bytes, patched, bytes.Length);
        int recLenAt = 19 + 2 /*extrasCount*/ + 2 /*facCount*/ + 4 /*siteId*/;   // position of the facility recLen u16
        patched[recLenAt] = 0x12;   // recLen = 18 (15 + 3 future bytes)
        patched[bytes.Length] = 0xDE; patched[bytes.Length + 1] = 0xAD; patched[bytes.Length + 2] = 0xBF;

        var snap = GeoSiteSnapshot.Decode(patched);

        Assert.NotNull(snap);
        Assert.NotNull(snap.Sites[0].Facility);
        Assert.Single(snap.Sites[0].Facility.Entries);
        Assert.Equal(7u, snap.Sites[0].Facility.Entries[0].FacilityId);
    }

    [Fact]
    public void FacilitySection_Truncated_RejectsWholePayload()
    {
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e",
            facility: new GeoFacilityTail(new[] { Fac(7u, 2, 3, 3, true) })));
        var bytes = GeoSiteSnapshot.Encode(snap);

        var truncated = new byte[bytes.Length - 3];   // cut inside the facility entry
        System.Array.Copy(bytes, truncated, truncated.Length);

        Assert.Null(GeoSiteSnapshot.Decode(truncated));
    }

    [Fact]
    public void FacilitySection_ForUnknownSiteId_Ignored()
    {
        // Join-by-id: a facility record whose siteId is absent from the record array is skipped (never a throw).
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "o", 10, 1, "n", "e",
            facility: new GeoFacilityTail(new[] { Fac(7u, 2, 3, 3, true) })));
        var bytes = GeoSiteSnapshot.Encode(snap);
        // facility-section siteId sits at: 19 record + 2 extrasCount + 2 facCount = 23.
        bytes[23] = 0x63;   // siteId 1 → 99 (no such record)

        var rt = GeoSiteSnapshot.Decode(bytes);

        Assert.NotNull(rt);
        Assert.Null(rt.Sites[0].Facility);   // record dropped with its unknown id — identity intact
    }

    [Fact]
    public void FacilitySection_DoesNotDisturbNoTailByteIdentity()
    {
        // A site with NEITHER a per-record tail NOR a facility tail must still emit the bare 19-byte record
        // (no extras block, no facility section) — the byte-identical-when-empty invariant.
        var snap = new GeoSiteSnapshot();
        snap.Sites.Add(new GeoSiteState(1, "A", 10, 1, "B", "C"));
        var bytes = GeoSiteSnapshot.Encode(snap);
        Assert.Equal(19, bytes.Length);
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

    [Fact]
    public void SubscribedGeoFactionEvents_AreTheAttackScheduleTrigger()
    {
        // Gap 6b: SiteAttackScheduled (GeoFaction.cs:319), subscribed on EVERY faction; carrier = arg 1
        // (the SiteAttackSchedule — GetOwningSiteId unwraps its readonly Site field).
        Assert.Equal(new[] { "SiteAttackScheduled" }, GeoSiteDirtyEvents.GeoFactionEventNames);
    }
}
