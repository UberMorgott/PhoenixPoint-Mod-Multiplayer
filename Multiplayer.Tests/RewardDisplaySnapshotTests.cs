using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip tests for the geoscape-event REWARD DISPLAY snapshot codec — the per-category delta lines
/// the native <c>UIModuleSiteEncounters.ShowReward</c> draws (resources, diplomacy +/-, items, units, revealed
/// sites, soldier damage/tired sums, faction skillpoints, haven population, etc.), carried host→client on the
/// <c>EventDismiss</c> wire so the client RESULT card mirrors the host's reward lines without re-applying.
/// Only the pure encode/decode path is exercised; <c>RewardDisplaySnapshot.BuildFromReward</c> (host read) and
/// <c>RewardDisplayRender</c> (client native render) bind live game types and are not unit-testable.
/// Mirrors <see cref="GeoSiteSnapshotTests"/> / <see cref="DiplomacyChannelTests"/>.
/// </summary>
public class RewardDisplaySnapshotTests
{
    private static RewardDisplaySnapshot RoundTrip(RewardDisplaySnapshot snap)
        => RewardDisplaySnapshot.Decode(RewardDisplaySnapshot.Encode(snap));

    private static RewardDisplaySnapshot Sample()
    {
        var s = new RewardDisplaySnapshot();
        s.Resources.Add(new RewardResourceLine(1, 300));        // ResourceType, RoundedValue
        s.Resources.Add(new RewardResourceLine(4, -50));
        s.Diplomacy.Add(new RewardDiplomacyLine(0, "PX_Faction", 0, "AN_Faction", -3)); // faction→faction
        s.Diplomacy.Add(new RewardDiplomacyLine(0, "PX_Faction", 1, "777", 5));          // faction→haven leader(siteId)
        s.Items.Add(new RewardItemLine("ITEM_GUID_A", 2));
        s.Units.Add("Cpt. Smith");
        s.Units.Add("Lt. Doe");
        s.RevealedSites.Add(42);
        s.RevealedSites.Add(7);
        s.HavenPopulation.Add(new RewardHavenPopLine(99, -1));
        s.DamageZones.Add(new RewardZoneLine(99, "ZONE_VIEW_GUID", 4));
        s.MaxDiplomacyFactionGuids.Add("AN_Faction");
        s.SpawnedHavenDefensesCount = 2;
        s.DamagedSoldiersSum = 6;
        s.TiredSoldiersSum = 3;
        s.AllSoldiersDamage = 10;
        s.AllSoldiersTiredness = 8;
        s.FactionSkillPoints = 2;
        s.NewPhoenixBaseSiteId = 55;
        return s;
    }

    [Fact]
    public void Snapshot_RoundTrips_AllCategories()
    {
        var src = Sample();
        var rt = RoundTrip(src);

        Assert.NotNull(rt);
        Assert.Equal(src.Resources, rt.Resources);
        Assert.Equal(src.Diplomacy, rt.Diplomacy);
        Assert.Equal(src.Items, rt.Items);
        Assert.Equal(src.Units, rt.Units);
        Assert.Equal(src.RevealedSites, rt.RevealedSites);
        Assert.Equal(src.HavenPopulation, rt.HavenPopulation);
        Assert.Equal(src.DamageZones, rt.DamageZones);
        Assert.Equal(src.MaxDiplomacyFactionGuids, rt.MaxDiplomacyFactionGuids);
        Assert.Equal(src.SpawnedHavenDefensesCount, rt.SpawnedHavenDefensesCount);
        Assert.Equal(src.DamagedSoldiersSum, rt.DamagedSoldiersSum);
        Assert.Equal(src.TiredSoldiersSum, rt.TiredSoldiersSum);
        Assert.Equal(src.AllSoldiersDamage, rt.AllSoldiersDamage);
        Assert.Equal(src.AllSoldiersTiredness, rt.AllSoldiersTiredness);
        Assert.Equal(src.FactionSkillPoints, rt.FactionSkillPoints);
        Assert.Equal(src.NewPhoenixBaseSiteId, rt.NewPhoenixBaseSiteId);
    }

    [Fact]
    public void Snapshot_RoundTrips_CommonRewards()
    {
        // The COMMON set that MUST work: resources + diplomacy + items + units + revealed sites.
        var s = new RewardDisplaySnapshot();
        s.Resources.Add(new RewardResourceLine(2, 120));
        s.Diplomacy.Add(new RewardDiplomacyLine(0, "PX", 0, "SY", -2));
        s.Items.Add(new RewardItemLine("ITM", 1));
        s.Units.Add("Rookie");
        s.RevealedSites.Add(3);

        var rt = RoundTrip(s);
        Assert.Single(rt.Resources);
        Assert.Equal(new RewardResourceLine(2, 120), rt.Resources[0]);
        Assert.Single(rt.Diplomacy);
        Assert.Equal(new RewardDiplomacyLine(0, "PX", 0, "SY", -2), rt.Diplomacy[0]);
        Assert.Single(rt.Items);
        Assert.Equal(new RewardItemLine("ITM", 1), rt.Items[0]);
        Assert.Equal(new[] { "Rookie" }, rt.Units.ToArray());
        Assert.Equal(new[] { 3 }, rt.RevealedSites.ToArray());
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new RewardDisplaySnapshot());
        Assert.NotNull(rt);
        Assert.Empty(rt.Resources);
        Assert.Empty(rt.Diplomacy);
        Assert.Empty(rt.Items);
        Assert.Empty(rt.Units);
        Assert.Empty(rt.RevealedSites);
        Assert.Empty(rt.HavenPopulation);
        Assert.Empty(rt.DamageZones);
        Assert.Empty(rt.MaxDiplomacyFactionGuids);
        Assert.Equal(0, rt.SpawnedHavenDefensesCount);
        Assert.Equal(0, rt.DamagedSoldiersSum);
        Assert.Equal(0, rt.TiredSoldiersSum);
        Assert.Equal(0, rt.AllSoldiersDamage);
        Assert.Equal(0, rt.AllSoldiersTiredness);
        Assert.Equal(0, rt.FactionSkillPoints);
        Assert.Equal(-1, rt.NewPhoenixBaseSiteId); // -1 = none
        Assert.True(rt.IsEmpty);
    }

    [Fact]
    public void EmptySnapshot_IsEmpty_NonEmptyIsNot()
    {
        Assert.True(new RewardDisplaySnapshot().IsEmpty);
        var s = new RewardDisplaySnapshot();
        s.FactionSkillPoints = 1;
        Assert.False(s.IsEmpty);
    }

    [Fact]
    public void Snapshot_RoundTrips_NullAndEmptyStrings()
    {
        var s = new RewardDisplaySnapshot();
        s.Diplomacy.Add(new RewardDiplomacyLine(2, null, 2, null, 0)); // none/none parties, null keys → ""
        s.Items.Add(new RewardItemLine(null, 0));
        s.Units.Add(null);
        s.MaxDiplomacyFactionGuids.Add(null);

        var rt = RoundTrip(s);
        Assert.NotNull(rt);
        Assert.Equal(new RewardDiplomacyLine(2, "", 2, "", 0), rt.Diplomacy[0]);
        Assert.Equal(new RewardItemLine("", 0), rt.Items[0]);
        Assert.Equal("", rt.Units[0]);
        Assert.Equal("", rt.MaxDiplomacyFactionGuids[0]);
    }

    [Fact]
    public void Snapshot_PreservesNegativeValues()
    {
        var s = new RewardDisplaySnapshot();
        s.Resources.Add(new RewardResourceLine(0, -999));          // negative resource delta
        s.Diplomacy.Add(new RewardDiplomacyLine(1, "770", 0, "PX", -7));
        s.HavenPopulation.Add(new RewardHavenPopLine(12, -4));
        var rt = RoundTrip(s);
        Assert.Equal(-999, rt.Resources[0].RoundedValue);
        Assert.Equal(-7, rt.Diplomacy[0].Value);
        Assert.Equal(-4, rt.HavenPopulation[0].Delta);
    }

    [Fact]
    public void Decode_NullInput_ReturnsNull() => Assert.Null(RewardDisplaySnapshot.Decode(null));

    [Fact]
    public void Decode_EmptyBlob_ReturnsEmptySnapshot()
    {
        // An ABSENT reward blob arrives as a zero-length byte[] (legacy / no-reward dismiss): decode to an
        // empty snapshot (NOT null) so the render path is a clean no-op.
        var rt = RewardDisplaySnapshot.Decode(new byte[0]);
        Assert.NotNull(rt);
        Assert.True(rt.IsEmpty);
    }

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
        => Assert.Null(RewardDisplaySnapshot.Decode(new byte[] { 0xFF }));

    [Fact]
    public void Decode_RejectsTruncatedString_ReturnsNull()
    {
        // resources count=0; diplomacy count=0; items count=1 then a string len that overruns the buffer.
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)0);   // resources
            w.Write((ushort)0);   // diplomacy
            w.Write((ushort)1);   // items count = 1
            w.Write((ushort)8);   // itemGuid len = 8 (but no bytes follow)
            Assert.Null(RewardDisplaySnapshot.Decode(ms.ToArray()));
        }
    }

    [Fact]
    public void Encode_NullSnapshot_ReturnsNull() => Assert.Null(RewardDisplaySnapshot.Encode(null));

    // ─── EventDismiss wire now carries the optional trailing reward blob ──────────────

    [Fact]
    public void EventDismiss_WithReward_RoundTrips()
    {
        var blob = RewardDisplaySnapshot.Encode(Sample());
        var bytes = SyncProtocol.EncodeEventDismiss(occurrenceId: 21, "EV_R", 3, blob);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex, out var rewardBlob));
        Assert.Equal(21, occ);
        Assert.Equal("EV_R", id);
        Assert.Equal(3, choiceIndex);
        var rt = RewardDisplaySnapshot.Decode(rewardBlob);
        Assert.NotNull(rt);
        Assert.Equal(Sample().Resources, rt.Resources);
        Assert.Equal(Sample().Units, rt.Units);
    }

    [Fact]
    public void EventDismiss_NoRewardOverload_ProducesThreeFieldBytes()
    {
        // The no-reward overload must stay BYTE-IDENTICAL to the 3-field (occId, eventId, choiceIndex) layout
        // (no trailing blob) so the pinned EventDismiss_WireBytes_AreStable + short-payload decode tests hold.
        var noBlob = SyncProtocol.EncodeEventDismiss(5, "AB", 2);
        var threeArgNull = SyncProtocol.EncodeEventDismiss(5, "AB", 2, null);
        var threeArgEmpty = SyncProtocol.EncodeEventDismiss(5, "AB", 2, new byte[0]);
        var expected = new byte[] { 0x05, 0x00, 0x02, 0x41, 0x42, 0x02, 0x00, 0x00, 0x00 };
        Assert.Equal(expected, noBlob);
        Assert.Equal(expected, threeArgNull);
        Assert.Equal(expected, threeArgEmpty);
    }

    [Fact]
    public void EventDismiss_NoRewardPayload_DecodesEmptyReward()
    {
        // An [occId][eventId][choiceIndex] packet (no reward blob) must decode with an empty (non-null) blob,
        // so a reward-less dismiss still renders a (reward-less) result card.
        var bytes = SyncProtocol.EncodeEventDismiss(6, "EV_NOR", 1);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex, out var rewardBlob));
        Assert.Equal(6, occ);
        Assert.Equal("EV_NOR", id);
        Assert.Equal(1, choiceIndex);
        Assert.NotNull(rewardBlob);
        Assert.Empty(rewardBlob);
    }

    [Fact]
    public void EventDismiss_NoChoicePayload_DecodesNegativeIndexEmptyReward()
    {
        // An [occId][eventId]-only packet must still decode: choiceIndex = -1, reward blob empty.
        byte[] shortPayload;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)6);
            w.Write("EV_OLD");
            shortPayload = ms.ToArray();
        }
        Assert.True(SyncProtocol.TryDecodeEventDismiss(shortPayload, out var occ, out var id, out var choiceIndex, out var rewardBlob));
        Assert.Equal(6, occ);
        Assert.Equal("EV_OLD", id);
        Assert.Equal(-1, choiceIndex);
        Assert.NotNull(rewardBlob);
        Assert.Empty(rewardBlob);
    }

    [Fact]
    public void EventDismiss_ThreeOutOverload_StillWorks()
    {
        // The 3-out decode overload (occId, eventId, choiceIndex) must keep working alongside the 4-out one.
        var bytes = SyncProtocol.EncodeEventDismiss(33, "EV_2", 4, RewardDisplaySnapshot.Encode(Sample()));
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var id, out var choiceIndex));
        Assert.Equal(33, occ);
        Assert.Equal("EV_2", id);
        Assert.Equal(4, choiceIndex);
    }

    // ─── FIX A: optional-tail host-formatted revealed-site strings ─────────────────────────────

    [Fact]
    public void RevealedSiteStrings_RoundTrip_PreservesTailAndBaseFields()
    {
        var s = Sample();   // has RevealedSites ids 42, 7 for the SiteId fallback
        s.RevealedSiteStrings.Add("4 Exploration");
        s.RevealedSiteStrings.Add("Императрица");   // encounter-bearing site → its event Title
        var rt = RoundTrip(s);
        Assert.NotNull(rt);
        Assert.Equal(new[] { "4 Exploration", "Императрица" }, rt.RevealedSiteStrings.ToArray());
        Assert.Equal(new[] { 42, 7 }, rt.RevealedSites.ToArray());   // fallback ids still carried alongside
        Assert.Equal(s.Resources, rt.Resources);                     // base payload unaffected
        Assert.Equal(s.NewPhoenixBaseSiteId, rt.NewPhoenixBaseSiteId);
    }

    [Fact]
    public void RevealedSiteStrings_OmittedWhenEmpty_StaysBackCompat()
    {
        // The tail is written ONLY when non-empty: an empty-strings snapshot encodes SHORTER than one carrying a
        // string, and a no-tail blob decodes with an empty list (a legacy payload without the tail is tolerated).
        var noTail = RewardDisplaySnapshot.Encode(Sample());        // Sample() carries no RevealedSiteStrings
        var withTail = Sample();
        withTail.RevealedSiteStrings.Add("Sentinel Site");
        var withTailBytes = RewardDisplaySnapshot.Encode(withTail);
        Assert.True(withTailBytes.Length > noTail.Length);          // tail bytes only present when non-empty

        var decoded = RewardDisplaySnapshot.Decode(noTail);
        Assert.NotNull(decoded);
        Assert.Empty(decoded.RevealedSiteStrings);                  // no-tail → empty list, base fields intact
        Assert.Equal(Sample().RevealedSites, decoded.RevealedSites);
    }

    [Fact]
    public void RevealedSiteStrings_OnlyStrings_IsNotEmpty_AndRoundTrips()
    {
        // A reward that ONLY carries host-formatted revealed-site strings (pathological: apply-result reveals but
        // no other delta lines) must count as non-empty so it still encodes/renders.
        var s = new RewardDisplaySnapshot();
        s.RevealedSiteStrings.Add("2 Haven");
        Assert.False(s.IsEmpty);
        var rt = RoundTrip(s);
        Assert.NotNull(rt);
        Assert.False(rt.IsEmpty);
        Assert.Equal(new[] { "2 Haven" }, rt.RevealedSiteStrings.ToArray());
    }

    [Fact]
    public void RevealedSiteStrings_EmptyList_KeepsSnapshotEmpty()
        => Assert.True(new RewardDisplaySnapshot().IsEmpty);   // no strings added → still empty (IsEmpty guard)
}
