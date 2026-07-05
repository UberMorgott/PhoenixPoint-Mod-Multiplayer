using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Multiplayer.Network.Sync.State;
using Xunit;

// PS3 recruit-pool channel (#10) — pure tests for the wire codec (RecruitPoolSnapshot: flag-framed
// haven/naked/captured blocks, absent-vs-empty semantics, forward tolerance), the host haven-flush core
// (RecruitPoolFlush: per-haven diff / hash-skip / budget defer / failed-drop) and the value-only pool
// reconcile (RecruitPoolReconcile: refresh / hire / capture / kill on fake collections). The engine glue
// (RecruitPoolChannel, RecruitPoolReflection, RecruitPoolPatches) is game-bound and in-game-gated; these
// lock the pure contracts per the 2026-07-05 personnel-sync spec §2.4 (PS3).
public class RecruitPoolSnapshotTests
{
    // ─── wire codec ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_HavenRecords_PreserveSiteIdAndBlobAndClear()
    {
        var snap = new RecruitPoolSnapshot();
        snap.Havens.Add(new RecruitHavenRecord(101, new byte[] { 1, 2, 3 }));
        snap.Havens.Add(new RecruitHavenRecord(-7, null));   // cleared slot (hired/expired) — honest tombstone

        var outSnap = RecruitPoolSnapshot.Decode(RecruitPoolSnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Equal(2, outSnap.Havens.Count);
        Assert.Equal(101, outSnap.Havens[0].SiteId);
        Assert.Equal(new byte[] { 1, 2, 3 }, outSnap.Havens[0].Blob);
        Assert.Equal(-7, outSnap.Havens[1].SiteId);
        Assert.Null(outSnap.Havens[1].Blob);
        Assert.False(outSnap.HasNaked);      // absent blocks stay absent
        Assert.False(outSnap.HasCaptured);
    }

    [Fact]
    public void RoundTrip_NakedRecords_PreserveBlobAndGenericCostLines()
    {
        // Cost is generic (ResourceType, float) pairs — vanilla price is Supplies as a FLOAT (decompile:
        // GenerateNakedRecruitsCost), so the codec must carry any type id + fractional values exactly.
        var snap = new RecruitPoolSnapshot { HasNaked = true };
        snap.Naked.Add(new RecruitNakedRecord(new byte[] { 9, 8 }, new[]
        {
            new RecruitCostEntry(1, 137.5f),      // Supplies
            new RecruitCostEntry(0x800, 2f),      // ProteanMutane (flags enum tail — beyond any u8)
        }));
        snap.Naked.Add(new RecruitNakedRecord(new byte[] { 5 }, null));   // null cost → empty, never null

        var outSnap = RecruitPoolSnapshot.Decode(RecruitPoolSnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.True(outSnap.HasNaked);
        Assert.Equal(2, outSnap.Naked.Count);
        Assert.Equal(new byte[] { 9, 8 }, outSnap.Naked[0].Blob);
        Assert.Equal(2, outSnap.Naked[0].Cost.Length);
        Assert.Equal(1, outSnap.Naked[0].Cost[0].ResourceType);
        Assert.Equal(137.5f, outSnap.Naked[0].Cost[0].Value);
        Assert.Equal(0x800, outSnap.Naked[0].Cost[1].ResourceType);
        Assert.Empty(outSnap.Naked[1].Cost);
    }

    [Fact]
    public void RoundTrip_CapturedRecords_PreserveBlobs()
    {
        var snap = new RecruitPoolSnapshot { HasCaptured = true };
        snap.Captured.Add(new byte[] { 0xAA });
        snap.Captured.Add(new byte[] { 0xBB, 0xCC });

        var outSnap = RecruitPoolSnapshot.Decode(RecruitPoolSnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.True(outSnap.HasCaptured);
        Assert.Equal(2, outSnap.Captured.Count);
        Assert.Equal(new byte[] { 0xAA }, outSnap.Captured[0]);
        Assert.Equal(new byte[] { 0xBB, 0xCC }, outSnap.Captured[1]);
    }

    [Fact]
    public void RoundTrip_AbsentVsEmpty_IsDistinct()
    {
        // ABSENT block = "unchanged, hash-skipped" (client leaves the pool alone); PRESENT with n=0 =
        // "pool is EMPTY" (honest clear). This distinction is the whole reason for the flag framing.
        var snap = new RecruitPoolSnapshot { HasNaked = true };   // present-empty naked; captured absent

        var outSnap = RecruitPoolSnapshot.Decode(RecruitPoolSnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.True(outSnap.HasNaked);
        Assert.Empty(outSnap.Naked);
        Assert.False(outSnap.HasCaptured);
        Assert.Empty(outSnap.Havens);
    }

    [Fact]
    public void Encode_EmptySnapshot_IsSingleZeroFlagByte_LegacyPin()
    {
        // v1 wire pin: no blocks → exactly [0x00]. Future versions may add flag bits but must keep the
        // leading flags byte + [i32 len][block] framing so THIS decoder length-skips what it can't parse.
        Assert.Equal(new byte[] { 0x00 }, RecruitPoolSnapshot.Encode(new RecruitPoolSnapshot()));
    }

    [Fact]
    public void Encode_KnownSnapshot_ExactBytes_LegacyPin()
    {
        // Golden v1 layout pin: one cleared haven slot. flags=0x01(Havens), blockLen=7,
        // block=[u16 n=1][i32 siteId=5][u8 hasRecruit=0].
        var snap = new RecruitPoolSnapshot();
        snap.Havens.Add(new RecruitHavenRecord(5, null));

        Assert.Equal(new byte[]
        {
            0x01,                     // blockFlags: Havens
            0x07, 0x00, 0x00, 0x00,   // blockLen = 7
            0x01, 0x00,               // n = 1
            0x05, 0x00, 0x00, 0x00,   // siteId = 5
            0x00,                     // hasRecruit = 0 (no blob fields follow)
        }, RecruitPoolSnapshot.Encode(snap));
    }

    [Fact]
    public void Decode_UnknownHigherFlagBit_LengthSkipped()
    {
        // Forward tolerance: a future bit3 block after the known ones is length-skipped whole; the known
        // haven block still decodes (parse-known-then-skip).
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((byte)(0x01 | 0x08));            // Havens + unknown future bit3
            byte[] havens = RecruitPoolSnapshot.EncodeHavenBlock(
                new List<RecruitHavenRecord> { new RecruitHavenRecord(42, new byte[] { 7 }) });
            w.Write(havens.Length); w.Write(havens);
            w.Write(3); w.Write(new byte[] { 0xDE, 0xAD, 0xBF });   // unknown block: [len=3][bytes]
            wire = ms.ToArray();
        }

        var outSnap = RecruitPoolSnapshot.Decode(wire);

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Havens);
        Assert.Equal(42, outSnap.Havens[0].SiteId);
        Assert.Equal(new byte[] { 7 }, outSnap.Havens[0].Blob);
    }

    [Fact]
    public void Decode_TruncatedBlock_ReturnsNull()
    {
        var snap = new RecruitPoolSnapshot { HasCaptured = true };
        snap.Captured.Add(new byte[] { 1, 2, 3, 4 });
        byte[] wire = RecruitPoolSnapshot.Encode(snap);
        byte[] truncated = wire.Take(wire.Length - 3).ToArray();
        Assert.Null(RecruitPoolSnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_BlockLenBeyondPayload_ReturnsNull()
    {
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((byte)0x02);      // Naked present
            w.Write(1000);            // blockLen lies beyond the payload
            w.Write((byte)0);
            wire = ms.ToArray();
        }
        Assert.Null(RecruitPoolSnapshot.Decode(wire));
    }

    [Fact]
    public void Decode_BlobLenLiesInsideBlock_ReturnsNull()
    {
        // A KNOWN block whose inner blobLen exceeds its own frame is corruption → all-or-nothing null.
        byte[] block;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)1);       // n=1
            w.Write(9);               // siteId
            w.Write((byte)1);         // hasRecruit
            w.Write(50);              // blobLen 50 > remaining
            w.Write(new byte[] { 1, 2 });
            block = ms.ToArray();
        }
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((byte)0x01);
            w.Write(block.Length); w.Write(block);
            wire = ms.ToArray();
        }
        Assert.Null(RecruitPoolSnapshot.Decode(wire));
    }

    [Fact]
    public void Decode_TrailingGarbageInsideKnownBlock_ReturnsNull()
    {
        // A known block with unconsumed trailing bytes is a framing error, not a tolerated extra.
        byte[] block;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)0);       // n=0 …
            w.Write((byte)0xEE);      // … plus a stray byte
            block = ms.ToArray();
        }
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((byte)0x04);      // Captured
            w.Write(block.Length); w.Write(block);
            wire = ms.ToArray();
        }
        Assert.Null(RecruitPoolSnapshot.Decode(wire));
    }

    [Fact]
    public void Decode_Garbage_ReturnsNull() =>
        Assert.Null(RecruitPoolSnapshot.Decode(new byte[] { 0xFF, 0xFF, 0xAA }));

    [Fact]
    public void Decode_Null_ReturnsNull() => Assert.Null(RecruitPoolSnapshot.Decode(null));

    // ─── RecruitPoolFlush (host per-haven diff core) ─────────────────────────────────────────────────────────

    private static RecruitPoolFlush.HavenSource Slot(byte[] blob) =>
        new RecruitPoolFlush.HavenSource { Resolved = true, Blob = blob };

    [Fact]
    public void Flush_EmitsOnlyChangedHavens_PerHavenDiff()
    {
        var lastSent = new Dictionary<int, ulong>();
        var world = new Dictionary<int, byte[]> { [1] = new byte[] { 1 }, [2] = new byte[] { 2 } };
        Func<int, RecruitPoolFlush.HavenSource> read = id => Slot(world[id]);

        var first = RecruitPoolFlush.Run(new[] { 1, 2 }, read, lastSent, 1 << 20);
        Assert.Equal(2, first.Emit.Count);

        // Haven 1 re-marked but UNCHANGED; haven 2 changed → only 2 ships (the per-haven diff).
        world[2] = new byte[] { 2, 2 };
        var second = RecruitPoolFlush.Run(new[] { 1, 2 }, read, lastSent, 1 << 20);
        Assert.Single(second.Emit);
        Assert.Equal(2, second.Emit[0].SiteId);
        Assert.Equal(1, second.SkippedUnchanged);
    }

    [Fact]
    public void Flush_ClearedSlot_EmitsAndDiffersFromBlob()
    {
        // spawn → clear → spawn must emit every step (cleared has its OWN hash, distinct from any blob).
        var lastSent = new Dictionary<int, ulong>();
        Assert.Single(RecruitPoolFlush.Run(new[] { 1 }, _ => Slot(new byte[] { 5 }), lastSent, 1 << 20).Emit);
        var cleared = RecruitPoolFlush.Run(new[] { 1 }, _ => Slot(null), lastSent, 1 << 20);
        Assert.Single(cleared.Emit);
        Assert.Null(cleared.Emit[0].Blob);
        Assert.Single(RecruitPoolFlush.Run(new[] { 1 }, _ => Slot(new byte[] { 5 }), lastSent, 1 << 20).Emit);
        // …and a repeated clear-state mark skips (hash equal).
        Assert.Equal(1, RecruitPoolFlush.Run(new[] { 1 }, _ => Slot(new byte[] { 5 }), lastSent, 1 << 20).SkippedUnchanged);
    }

    [Fact]
    public void Flush_BudgetOverflow_DefersUnstamped_EmitAtLeastOne()
    {
        var lastSent = new Dictionary<int, ulong>();
        var big = new byte[100];
        // Budget fits ~1 record: first emits (emit-at-least-one), rest defer UNREAD and UNSTAMPED.
        var res = RecruitPoolFlush.Run(new[] { 1, 2, 3 }, _ => Slot(big), lastSent, 120);

        Assert.Single(res.Emit);
        Assert.Equal(new List<int> { 2, 3 }, res.Deferred);
        Assert.True(lastSent.ContainsKey(1));
        Assert.False(lastSent.ContainsKey(2));   // deferred ids re-blob next flush (latest state wins)
        Assert.False(lastSent.ContainsKey(3));
    }

    [Fact]
    public void Flush_UnresolvedHaven_FailedNotDeferred()
    {
        // A dead id (site gone / serializer miss) must not spin the drain loop forever.
        var res = RecruitPoolFlush.Run(new[] { 7 }, _ => new RecruitPoolFlush.HavenSource(), new Dictionary<int, ulong>(), 1 << 20);
        Assert.Empty(res.Emit);
        Assert.Empty(res.Deferred);
        Assert.Equal(1, res.Failed);
    }

    [Fact]
    public void Flush_DuplicateIds_Coalesce()
    {
        var res = RecruitPoolFlush.Run(new[] { 1, 1, 1 }, _ => Slot(new byte[] { 9 }), new Dictionary<int, ulong>(), 1 << 20);
        Assert.Single(res.Emit);
    }

    // ─── RecruitPoolReconcile (client value-only clear+refill) ──────────────────────────────────────────────

    [Fact]
    public void ReconcileNaked_Refresh_ReplacesAllEntries_KeepsInstance()
    {
        var dict = new Hashtable { ["oldDesc"] = "oldCost" };
        var target = dict;

        int applied = RecruitPoolReconcile.ApplyNaked(dict, new List<KeyValuePair<object, object>>
        {
            new KeyValuePair<object, object>("descA", "costA"),
            new KeyValuePair<object, object>("descB", "costB"),
        });

        Assert.Equal(2, applied);
        Assert.Same(target, dict);               // live instance kept (UI references stay valid)
        Assert.False(dict.ContainsKey("oldDesc"));
        Assert.Equal("costA", dict["descA"]);
        Assert.Equal("costB", dict["descB"]);
    }

    [Fact]
    public void ReconcileNaked_Hire_SmallerFullSetRemovesHiredEntry()
    {
        // Host hire = dict-remove → the next FULL set simply lacks the hired descriptor.
        var dict = new Hashtable { ["descA"] = "costA", ["descB"] = "costB" };

        RecruitPoolReconcile.ApplyNaked(dict, new List<KeyValuePair<object, object>>
        {
            new KeyValuePair<object, object>("descB", "costB"),
        });

        Assert.Single(dict);
        Assert.True(dict.ContainsKey("descB"));
    }

    [Fact]
    public void ReconcileNaked_EmptySet_HonestClear_NullEntries_NoTouch()
    {
        var dict = new Hashtable { ["descA"] = "costA" };
        Assert.Equal(0, RecruitPoolReconcile.ApplyNaked(dict, new List<KeyValuePair<object, object>>()));
        Assert.Empty(dict);

        dict["descA"] = "costA";
        Assert.Equal(-1, RecruitPoolReconcile.ApplyNaked(dict, null));   // no payload → pool untouched
        Assert.Single(dict);
    }

    [Fact]
    public void ReconcileCaptured_CaptureAndKill_MirrorFullSet()
    {
        var list = new ArrayList { "pandoranA" };

        // Host captured B → full set {A,B}.
        RecruitPoolReconcile.ApplyCaptured(list, new List<object> { "pandoranA", "pandoranB" });
        Assert.Equal(new object[] { "pandoranA", "pandoranB" }, list.ToArray());

        // Host killed/harvested A → full set {B}.
        RecruitPoolReconcile.ApplyCaptured(list, new List<object> { "pandoranB" });
        Assert.Equal(new object[] { "pandoranB" }, list.ToArray());

        // Idempotent re-apply (re-delivered flush) → same content, no dupes.
        RecruitPoolReconcile.ApplyCaptured(list, new List<object> { "pandoranB" });
        Assert.Equal(new object[] { "pandoranB" }, list.ToArray());
    }

    [Fact]
    public void ReconcileCaptured_SkipsNullDescriptors()
    {
        // A blob that failed to decode arrives as null — skipped, the rest applies (degrade-to-notify).
        var list = new ArrayList();
        int applied = RecruitPoolReconcile.ApplyCaptured(list, new List<object> { "ok", null, "ok2" });
        Assert.Equal(2, applied);
        Assert.Equal(new object[] { "ok", "ok2" }, list.ToArray());
    }
}
