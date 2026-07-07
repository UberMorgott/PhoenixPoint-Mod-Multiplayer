using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for the LIVE mission-objective mirror (surface <c>tac.objective</c> 0x99). Covers:
///   (a) the <see cref="TacticalObjectiveCodec"/> wire round-trips (state + add records, progress ints, empty
///       fields), recLen framing (unknown-kind / longer-newer record skipped cleanly, trailing bytes ignored)
///       and truncation / corrupt counts → clean drop (no partial accept),
///   (b) the HOST diff seam (<see cref="TacticalObjectiveGate.BuildRecords"/>) — seed-all, change-only diff
///       (state / progress / class), tail appends → ADD records, list shrink → full reseed,
///   (c) the CLIENT apply seams — index/class-mismatch reject (<see cref="TacticalObjectiveGate.ResolveStateApplies"/>,
///       the unknown-TFTV-subclass degrade path) and scripted-add resolution
///       (<see cref="TacticalObjectiveGate.ResolveAddMatch"/>),
///   (d) the surface-id pins: 0x99 for tac.objective, all TacticalSurfaceIds unique, none on a tombstoned
///       wire id (0x21-0x24 / 0x27 / 0x63 / 0x64 — retired PacketType ids that must never be re-fronted).
/// The engine glue (TacticalObjectiveSync / ObjectiveSyncPatches / FactionObjectiveReflect) binds game types
/// and is in-game verified.
/// </summary>
public class TacticalObjectiveCodecTests
{
    private static TacticalObjectiveCodec.ObjectiveBatch Decode(byte[] bytes)
    {
        Assert.True(TacticalObjectiveCodec.TryDecode(bytes, out var b));
        return b;
    }

    private static TacticalObjectiveCodec.ObjectiveRec State(int idx, byte state, string cls, string desc = "", int[] prog = null)
        => new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindState, idx, state, cls, desc, prog);

    private static TacticalObjectiveGate.ObjSnap Snap(string cls, byte state, int[] prog = null, string desc = "")
        => new TacticalObjectiveGate.ObjSnap(cls, desc, state, prog);

    // ─── (a) codec round-trip + framing ─────────────────────────────────

    [Fact]
    public void RoundTrips_StateAndAddRecords_AllFields()
    {
        var batch = new TacticalObjectiveCodec.ObjectiveBatch(77u, new List<TacticalObjectiveCodec.ObjectiveRec>
        {
            State(0, 1, "WipeEnemyFactionObjective", "KEY_KILL_ALL", new[] { 3, -2, 42 }),
            new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindAdd, 2, 0,
                "SurviveTurnsFactionObjective", "KEY_SURVIVE", new[] { 5 }),
        });

        var d = Decode(TacticalObjectiveCodec.Encode(batch));

        Assert.Equal(77u, d.Seq);
        Assert.Equal(2, d.Records.Count);

        Assert.Equal(TacticalObjectiveCodec.KindState, d.Records[0].Kind);
        Assert.Equal(0, d.Records[0].Index);
        Assert.Equal(1, d.Records[0].State);
        Assert.Equal("WipeEnemyFactionObjective", d.Records[0].ClassName);
        Assert.Equal("KEY_KILL_ALL", d.Records[0].DescKey);
        Assert.Equal(new[] { 3, -2, 42 }, d.Records[0].Progress);

        Assert.Equal(TacticalObjectiveCodec.KindAdd, d.Records[1].Kind);
        Assert.Equal(2, d.Records[1].Index);
        Assert.Equal(0, d.Records[1].State);
        Assert.Equal("SurviveTurnsFactionObjective", d.Records[1].ClassName);
        Assert.Equal("KEY_SURVIVE", d.Records[1].DescKey);
        Assert.Equal(new[] { 5 }, d.Records[1].Progress);
    }

    [Fact]
    public void RoundTrips_EmptyBatch_And_EmptyFields()
    {
        var d = Decode(TacticalObjectiveCodec.Encode(new TacticalObjectiveCodec.ObjectiveBatch(1u, null)));
        Assert.Equal(1u, d.Seq);
        Assert.Empty(d.Records);

        var d2 = Decode(TacticalObjectiveCodec.Encode(new TacticalObjectiveCodec.ObjectiveBatch(2u,
            new List<TacticalObjectiveCodec.ObjectiveRec> { State(0, 2, "", "", null) })));
        Assert.Equal("", d2.Records[0].ClassName);
        Assert.Equal("", d2.Records[0].DescKey);
        Assert.Empty(d2.Records[0].Progress);
    }

    [Fact]
    public void Decode_SkipsUnknownRecordTail_ViaRecLenFraming_AndIgnoresTrailingBytes()
    {
        // Hand-frame a batch whose single record carries EXTRA bytes past the known fields (a newer peer's
        // longer record) + trailing garbage after the last record — both must be ignored cleanly.
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(9u);            // seq
            w.Write((ushort)1);     // recCount

            byte[] body;
            using (var rs = new MemoryStream())
            using (var rw = new BinaryWriter(rs, Encoding.UTF8))
            {
                rw.Write(TacticalObjectiveCodec.KindState);
                rw.Write((ushort)1);           // index
                rw.Write((byte)2);             // state
                var cls = Encoding.UTF8.GetBytes("KillActorFactionObjective");
                rw.Write((byte)cls.Length); rw.Write(cls);
                rw.Write((ushort)0);           // descKey ""
                rw.Write((byte)0);             // progCount 0
                rw.Write(0xDEADBEEF);          // EXTRA future field → must be skipped by recLen
                body = rs.ToArray();
            }
            w.Write((ushort)body.Length);
            w.Write(body);
            w.Write((byte)0xFF);               // trailing garbage after the last record

            var d = Decode(ms.ToArray());
            Assert.Equal(9u, d.Seq);
            Assert.Single(d.Records);
            Assert.Equal(1, d.Records[0].Index);
            Assert.Equal(2, d.Records[0].State);
            Assert.Equal("KillActorFactionObjective", d.Records[0].ClassName);
        }
    }

    [Fact]
    public void Decode_Rejects_Truncation_And_CorruptCounts()
    {
        Assert.False(TacticalObjectiveCodec.TryDecode(null, out _));
        Assert.False(TacticalObjectiveCodec.TryDecode(new byte[0], out _));
        Assert.False(TacticalObjectiveCodec.TryDecode(new byte[] { 1, 2, 3 }, out _));   // < header

        var good = TacticalObjectiveCodec.Encode(new TacticalObjectiveCodec.ObjectiveBatch(5u,
            new List<TacticalObjectiveCodec.ObjectiveRec> { State(0, 1, "WipeEnemyFactionObjective", "K", new[] { 7 }) }));

        // Truncate anywhere inside the record → clean false, no partial accept.
        for (int cut = 7; cut < good.Length; cut++)
        {
            var t = new byte[cut];
            Array.Copy(good, t, cut);
            Assert.False(TacticalObjectiveCodec.TryDecode(t, out _));
        }

        // Corrupt recCount far beyond the buffer → clean false (no wild allocation).
        var corrupt = (byte[])good.Clone();
        corrupt[4] = 0xFF; corrupt[5] = 0xFF;
        Assert.False(TacticalObjectiveCodec.TryDecode(corrupt, out _));
    }

    // ─── (b) HOST diff seam ─────────────────────────────────────────────

    [Fact]
    public void BuildRecords_SeedAll_EmitsStateRecordForEveryIndex()
    {
        var current = new[] { Snap("A", 0, new[] { 1 }), Snap("B", 1), Snap("C", 2) };
        var recs = TacticalObjectiveGate.BuildRecords(current, new List<TacticalObjectiveGate.ObjSnap>(), seedAll: true);

        Assert.Equal(3, recs.Count);
        Assert.All(recs, r => Assert.Equal(TacticalObjectiveCodec.KindState, r.Kind));
        Assert.Equal(new[] { 0, 1, 2 }, recs.Select(r => r.Index).ToArray());
        Assert.Equal(new[] { "A", "B", "C" }, recs.Select(r => r.ClassName).ToArray());
    }

    [Fact]
    public void BuildRecords_EmptyCache_ReseedsEvenWithoutSeedFlag()
    {
        var current = new[] { Snap("A", 1) };
        var recs = TacticalObjectiveGate.BuildRecords(current, new List<TacticalObjectiveGate.ObjSnap>(), seedAll: false);
        Assert.Single(recs);
        Assert.Equal(TacticalObjectiveCodec.KindState, recs[0].Kind);   // never an unresolvable ADD
    }

    [Fact]
    public void BuildRecords_Diff_EmitsOnlyChanged_StateOrProgress()
    {
        var last = new[] { Snap("A", 0, new[] { 5 }), Snap("B", 0), Snap("C", 0, new[] { 1, 2 }) };
        var now  = new[] { Snap("A", 1, new[] { 5 }), Snap("B", 0), Snap("C", 0, new[] { 1, 3 }) };

        var recs = TacticalObjectiveGate.BuildRecords(now, last, seedAll: false);

        Assert.Equal(2, recs.Count);                        // B unchanged → silent
        Assert.Equal(0, recs[0].Index);                     // A: state flip
        Assert.Equal(1, recs[0].State);
        Assert.Equal(2, recs[1].Index);                     // C: progress-only change (TurnsRemaining-style)
        Assert.Equal(new[] { 1, 3 }, recs[1].Progress);
    }

    [Fact]
    public void BuildRecords_TailAppend_BecomesAddRecord()
    {
        var last = new[] { Snap("A", 0) };
        var now  = new[] { Snap("A", 0), Snap("Chained", 0, new[] { 4 }, "KEY_CHAINED") };

        var recs = TacticalObjectiveGate.BuildRecords(now, last, seedAll: false);

        Assert.Single(recs);
        Assert.Equal(TacticalObjectiveCodec.KindAdd, recs[0].Kind);
        Assert.Equal(1, recs[0].Index);
        Assert.Equal("Chained", recs[0].ClassName);
        Assert.Equal("KEY_CHAINED", recs[0].DescKey);
    }

    [Fact]
    public void BuildRecords_ListShrink_FallsBackToFullReseed()
    {
        var last = new[] { Snap("A", 0), Snap("B", 0) };
        var now  = new[] { Snap("A", 1) };

        var recs = TacticalObjectiveGate.BuildRecords(now, last, seedAll: false);

        Assert.Single(recs);                                              // full state reseed of what remains
        Assert.Equal(TacticalObjectiveCodec.KindState, recs[0].Kind);     // degrade, never misaligned ADDs
    }

    // ─── (c) CLIENT apply seams ─────────────────────────────────────────

    [Fact]
    public void ResolveStateApplies_ClassMatch_Applies_MismatchAndOutOfRange_Skip()
    {
        var records = new List<TacticalObjectiveCodec.ObjectiveRec>
        {
            State(0, 1, "A"),                    // in range, class match → apply
            State(1, 2, "TftvCustomObjective"),  // class MISMATCH (unknown subclass) → skip, never mis-stamp
            State(9, 1, "A"),                    // out of range (index drift) → skip
            new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindAdd, 3, 0, "A", "", null), // ADD → not a state apply
        };
        var skipped = new List<TacticalObjectiveCodec.ObjectiveRec>();

        var applies = TacticalObjectiveGate.ResolveStateApplies(records, new[] { "A", "B" }, skipped);

        Assert.Single(applies);
        Assert.Equal(0, applies[0].Key);
        Assert.Equal(1, applies[0].Value.State);
        Assert.Equal(2, skipped.Count);   // the mismatch + the out-of-range (ADD is not "skipped", it rides its own path)
        Assert.Equal("TftvCustomObjective", skipped[0].ClassName);
    }

    [Fact]
    public void ResolveAddMatch_ByClassAndDescKey_SkipsPresent_UnresolvableIsMinusOne()
    {
        var add = new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindAdd, 2, 0, "Chained", "KEY_2", null);
        var candidates = new List<TacticalObjectiveGate.AddCandidate>
        {
            new TacticalObjectiveGate.AddCandidate("Chained", "KEY_1", alreadyPresent: false),  // wrong descKey
            new TacticalObjectiveGate.AddCandidate("Chained", "KEY_2", alreadyPresent: true),   // already mirrored
            new TacticalObjectiveGate.AddCandidate("Other",   "KEY_2", alreadyPresent: false),  // wrong class
            new TacticalObjectiveGate.AddCandidate("Chained", "KEY_2", alreadyPresent: false),  // ← the match
        };

        Assert.Equal(3, TacticalObjectiveGate.ResolveAddMatch(add, candidates));

        // Empty descKey on either side → class-name-only match (vanilla objectives without a Description).
        var addNoKey = new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindAdd, 0, 0, "Chained", "", null);
        Assert.Equal(0, TacticalObjectiveGate.ResolveAddMatch(addNoKey, candidates));

        // Nothing resolvable (scripted direct add outside the def graph) → -1 (caller degrades, log-once).
        var unresolvable = new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindAdd, 0, 0, "NopeObjective", "", null);
        Assert.Equal(-1, TacticalObjectiveGate.ResolveAddMatch(unresolvable, candidates));
    }

    // ─── (c2) ZONE_UNLOCK records (audit D20 — scripted zone unlock rides this surface) ─

    [Fact]
    public void ZoneUnlock_KindConstants_ArePinned()
    {
        // Wire-compat pins: a re-numbering silently mis-routes records between peers on different builds.
        Assert.Equal(0, TacticalObjectiveCodec.KindState);
        Assert.Equal(1, TacticalObjectiveCodec.KindAdd);
        Assert.Equal(2, TacticalObjectiveCodec.KindZoneUnlock);
    }

    [Fact]
    public void ZoneUnlock_RoundTrips_GuidInDescKey()
    {
        var recs = TacticalObjectiveGate.BuildZoneUnlockRecords(new[] { "guid-a", "guid-b" });
        var d = Decode(TacticalObjectiveCodec.Encode(new TacticalObjectiveCodec.ObjectiveBatch(9u, recs)));

        Assert.Equal(2, d.Records.Count);
        Assert.All(d.Records, r => Assert.Equal(TacticalObjectiveCodec.KindZoneUnlock, r.Kind));
        Assert.Equal(new List<string> { "guid-a", "guid-b" },
            TacticalObjectiveGate.CollectZoneUnlockGuids(d.Records));
    }

    [Fact]
    public void BuildZoneUnlockRecords_SkipsEmpty_DedupesPreservingOrder()
    {
        var recs = TacticalObjectiveGate.BuildZoneUnlockRecords(
            new[] { "g1", null, "", "g2", "g1" });
        Assert.Equal(2, recs.Count);
        Assert.Equal("g1", recs[0].DescKey);
        Assert.Equal("g2", recs[1].DescKey);

        Assert.Empty(TacticalObjectiveGate.BuildZoneUnlockRecords(null));
        Assert.Empty(TacticalObjectiveGate.BuildZoneUnlockRecords(new string[0]));
    }

    [Fact]
    public void CollectZoneUnlockGuids_IgnoresOtherKinds_AndEmptyGuids()
    {
        var mixed = new List<TacticalObjectiveCodec.ObjectiveRec>
        {
            State(0, 1, "WipeEnemyFactionObjective"),
            new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindAdd, 1, 0, "Chained", "KEY", null),
            new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindZoneUnlock, 0, 0, "", "gz", null),
            new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindZoneUnlock, 0, 0, "", "", null),
            null,
        };
        Assert.Equal(new List<string> { "gz" }, TacticalObjectiveGate.CollectZoneUnlockGuids(mixed));
        Assert.Empty(TacticalObjectiveGate.CollectZoneUnlockGuids(null));
    }

    [Fact]
    public void ZoneUnlock_NoRegress_StateAppliesIgnoreIt_AndDiffNeverEmitsIt()
    {
        // A ZONE_UNLOCK record must be INVISIBLE to the state-apply resolver: not applied, not "skipped"
        // (skipped feeds the degrade log — zone unlocks are a first-class kind, not drift).
        var recs = new List<TacticalObjectiveCodec.ObjectiveRec>
        {
            new TacticalObjectiveCodec.ObjectiveRec(TacticalObjectiveCodec.KindZoneUnlock, 0, 0, "", "gz", null),
            State(0, 1, "WipeEnemyFactionObjective"),
        };
        var skipped = new List<TacticalObjectiveCodec.ObjectiveRec>();
        var applies = TacticalObjectiveGate.ResolveStateApplies(
            recs, new[] { "WipeEnemyFactionObjective" }, skipped);
        Assert.Single(applies);
        Assert.Empty(skipped);

        // And the host objective DIFF never fabricates zone-unlock records (they are event-driven only).
        var built = TacticalObjectiveGate.BuildRecords(
            new[] { Snap("A", 1) }, new[] { Snap("A", 0) }, seedAll: false);
        Assert.All(built, r => Assert.NotEqual(TacticalObjectiveCodec.KindZoneUnlock, r.Kind));
    }

    // ─── (d) surface-id pins ────────────────────────────────────────────

    // PacketType wire ids retired forever (PacketType.cs tombstone block) — no surface may be FRONTED on a
    // reused one, and the new surface id must be unique in the tactical surface registry.
    private static readonly ushort[] TombstonedWireIds =
        { 0x21, 0x22, 0x23, 0x24, 0x27, 0x63, 0x64, 0x10, 0x12, 0x13, 0x25, 0x26,
          0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x60, 0x61, 0x62, 0x68 };

    [Fact]
    public void TacObjective_IsPinnedTo_0x99_NextFreeTacticalSurfaceId()
    {
        Assert.Equal(0x99, TacticalSurfaceIds.TacObjective);
    }

    [Fact]
    public void TacticalSurfaceIds_AreUnique_AndNeverOnATombstonedWireId()
    {
        var values = typeof(TacticalSurfaceIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(ushort))
            .Select(f => (ushort)f.GetRawConstantValue())
            .ToList();

        Assert.NotEmpty(values);
        Assert.Equal(values.Count, values.Distinct().Count());               // no id collision
        Assert.All(values, v => Assert.DoesNotContain(v, TombstonedWireIds)); // no tombstone reuse
        Assert.All(values, v => Assert.True(v >= 0x80, "tactical surface ids live in the 0x80+ range"));
    }
}
