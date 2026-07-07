using System.Collections.Generic;
using System.IO;
using System.Text;
using Multiplayer.Harmony.Tactical;
using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for the TS6 structural-destruction mirror (surface <c>tac.structdamage</c> 0x96). Covers:
///   (a) the <see cref="TacticalStructDamageCodec"/> wire round-trips (seq/pin + multi-hit guid/point/hp) and
///       truncation / garbage / corrupt-length → clean drop (no partial accept),
///   (b) the backward/forward-tolerant recLen framing: an UNKNOWN targetKind record is SKIPPED cleanly while the
///       known records around it still decode (a newer peer can add a kind without breaking an older one),
///   (c) the deterministic KEY survives the round-trip byte-identical (guid + aim-point) — the client resolves the
///       same destructible/tile the host damaged (R2: same guid+point → same object host↔client),
///   (d) the pure "no double actor damage" decision (<see cref="ClientStructDamageInertGate.ShouldNeuterExplosionChain"/>).
/// The engine glue (TacticalStructDamageSync / StructDamageCapturePatch — the ApplyDamage funnel capture + the native
/// re-apply + the SceneObjectId resolution) binds game types and is in-game verified.
/// </summary>
public class TacticalStructDamageCodecTests
{
    private static TacticalStructDamageCodec.StructHit Hit(string guid, float px, float py, float pz, float hp)
        => new TacticalStructDamageCodec.StructHit(guid, px, py, pz, hp);

    // ─── (a) codec round-trip ──────────────────────────────────────────
    [Fact]
    public void RoundTrips_MultiHit_GuidPointHp()
    {
        var batch = new TacticalStructDamageCodec.StructBatch(4242u, new List<TacticalStructDamageCodec.StructHit>
        {
            Hit("11111111-1111-1111-1111-111111111111", 1.5f, 0f, 2.5f, 3f),
            Hit("22222222-2222-2222-2222-222222222222", -4f, 2.4f, 8f, 12.5f),
            Hit("33333333-3333-3333-3333-333333333333", 0f, 0f, 0f, 0.25f),
        });

        var bytes = TacticalStructDamageCodec.EncodeStructDamage(batch);
        Assert.True(TacticalStructDamageCodec.TryDecodeStructDamage(bytes, out var d));

        Assert.Equal(4242u, d.Seq);               // pin: seq survives
        Assert.Equal(3, d.Hits.Count);

        Assert.Equal(TacticalStructDamageCodec.KindDestructible, d.Hits[0].TargetKind);
        Assert.Equal("11111111-1111-1111-1111-111111111111", d.Hits[0].Guid);
        Assert.Equal(1.5f, d.Hits[0].Px);
        Assert.Equal(0f, d.Hits[0].Py);
        Assert.Equal(2.5f, d.Hits[0].Pz);
        Assert.Equal(3f, d.Hits[0].HealthDamage);

        Assert.Equal("22222222-2222-2222-2222-222222222222", d.Hits[1].Guid);
        Assert.Equal(-4f, d.Hits[1].Px);
        Assert.Equal(2.4f, d.Hits[1].Py);
        Assert.Equal(12.5f, d.Hits[1].HealthDamage);

        Assert.Equal("33333333-3333-3333-3333-333333333333", d.Hits[2].Guid);
        Assert.Equal(0.25f, d.Hits[2].HealthDamage);
    }

    [Fact]
    public void RoundTrips_EmptyBatch_NoTail()
    {
        var bytes = TacticalStructDamageCodec.EncodeStructDamage(new TacticalStructDamageCodec.StructBatch(1u, null));
        Assert.Equal(6, bytes.Length);            // exactly u32 seq + u16 hitCount, no tail
        Assert.True(TacticalStructDamageCodec.TryDecodeStructDamage(bytes, out var d));
        Assert.Equal(1u, d.Seq);
        Assert.Empty(d.Hits);
    }

    [Fact]
    public void RoundTrips_EmptyGuid()
    {
        var bytes = TacticalStructDamageCodec.EncodeStructDamage(new TacticalStructDamageCodec.StructBatch(7u,
            new List<TacticalStructDamageCodec.StructHit> { Hit("", 1f, 2f, 3f, 4f) }));
        Assert.True(TacticalStructDamageCodec.TryDecodeStructDamage(bytes, out var d));
        Assert.Single(d.Hits);
        Assert.Equal("", d.Hits[0].Guid);
        Assert.Equal(4f, d.Hits[0].HealthDamage);
    }

    [Fact]
    public void Rejects_Null_Truncated_AndGarbage()
    {
        Assert.False(TacticalStructDamageCodec.TryDecodeStructDamage(null, out _));
        Assert.False(TacticalStructDamageCodec.TryDecodeStructDamage(new byte[5], out _));   // shorter than the 6-byte header

        // A valid frame chopped mid-record → clean reject (recLen says more bytes than remain).
        var bytes = TacticalStructDamageCodec.EncodeStructDamage(new TacticalStructDamageCodec.StructBatch(3u,
            new List<TacticalStructDamageCodec.StructHit> { Hit("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1f, 2f, 3f, 4f) }));
        var chopped = new byte[bytes.Length - 5];
        System.Array.Copy(bytes, chopped, chopped.Length);
        Assert.False(TacticalStructDamageCodec.TryDecodeStructDamage(chopped, out _));
    }

    [Fact]
    public void Rejects_CorruptRecLen()
    {
        // header (seq=1, hitCount=1) then a recLen far exceeding the remaining buffer → guarded reject.
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(1u);                          // seq
            w.Write((ushort)1);                   // hitCount
            w.Write((ushort)ushort.MaxValue);     // recLen far exceeds the remaining buffer
            Assert.False(TacticalStructDamageCodec.TryDecodeStructDamage(ms.ToArray(), out _));
        }
    }

    [Fact]
    public void Rejects_CorruptGuidLen()
    {
        // A record whose guidLen overruns its own recLen → guarded reject (no wild allocation / no partial accept).
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(1u);                                  // seq
            w.Write((ushort)1);                           // hitCount
            // record body = kind(1) + guidLen(2) + [no guid bytes] + point/hp missing; recLen claims only 3 bytes.
            w.Write((ushort)3);                           // recLen (kind + guidLen only)
            w.Write(TacticalStructDamageCodec.KindDestructible);
            w.Write((ushort)200);                         // guidLen 200 ≫ recLen → overrun
            Assert.False(TacticalStructDamageCodec.TryDecodeStructDamage(ms.ToArray(), out _));
        }
    }

    // ─── (b) forward-tolerant: skip an unknown targetKind via recLen ─────
    [Fact]
    public void SkipsUnknownKind_KeepsKnownRecords()
    {
        // Hand-craft: [known kind=1] [unknown kind=9 with 5 trailing bytes] [known kind=1]. An older/newer decoder
        // must yield the 2 known hits and cleanly skip the middle unknown record (framed by its recLen).
        byte[] rec1 = KnownRecordBody("guid-A", 1f, 2f, 3f, 10f);
        byte[] rec3 = KnownRecordBody("guid-B", 4f, 5f, 6f, 20f);
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(99u);                 // seq
            w.Write((ushort)3);           // hitCount (2 known + 1 unknown)

            w.Write((ushort)rec1.Length); w.Write(rec1);

            byte[] unknownTail = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 };
            w.Write((ushort)(1 + unknownTail.Length));   // recLen = kind byte + tail
            w.Write((byte)9);                             // unknown targetKind
            w.Write(unknownTail);

            w.Write((ushort)rec3.Length); w.Write(rec3);

            Assert.True(TacticalStructDamageCodec.TryDecodeStructDamage(ms.ToArray(), out var d));
            Assert.Equal(99u, d.Seq);
            Assert.Equal(2, d.Hits.Count);                // the unknown record was skipped, not emitted
            Assert.Equal("guid-A", d.Hits[0].Guid);
            Assert.Equal(10f, d.Hits[0].HealthDamage);
            Assert.Equal("guid-B", d.Hits[1].Guid);
            Assert.Equal(20f, d.Hits[1].HealthDamage);
        }
    }

    [Fact]
    public void ToleratesFutureTrailingBytes_OnKnownRecord()
    {
        // A kind=1 record extended by a future version with extra trailing bytes: an OLD decoder reads the known
        // fields then resyncs past the extra via recLen (no reject, correct values).
        byte[] baseBody = KnownRecordBody("guid-X", 7f, 8f, 9f, 33f);
        byte[] extended = new byte[baseBody.Length + 4];
        System.Array.Copy(baseBody, extended, baseBody.Length);   // +4 unknown trailing bytes (left zero)
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(5u);
            w.Write((ushort)1);
            w.Write((ushort)extended.Length);
            w.Write(extended);
            Assert.True(TacticalStructDamageCodec.TryDecodeStructDamage(ms.ToArray(), out var d));
            Assert.Single(d.Hits);
            Assert.Equal("guid-X", d.Hits[0].Guid);
            Assert.Equal(33f, d.Hits[0].HealthDamage);
        }
    }

    // ─── (c) deterministic key survives round-trip byte-identical ────────
    [Fact]
    public void DeterministicKey_SurvivesRoundTrip()
    {
        // Same guid + point on both sides must decode identically → the client resolves the SAME destructible/tile
        // the host damaged (the cross-side identity contract, R2).
        var hits = new List<TacticalStructDamageCodec.StructHit>();
        for (int i = 0; i < 32; i++)
            hits.Add(Hit("cccccccc-0000-0000-0000-" + i.ToString("D12"), i * 0.5f, i * -1.25f, i + 0.1f, i + 1f));
        var bytes = TacticalStructDamageCodec.EncodeStructDamage(new TacticalStructDamageCodec.StructBatch(1u, hits));

        Assert.True(TacticalStructDamageCodec.TryDecodeStructDamage(bytes, out var d));
        Assert.Equal(32, d.Hits.Count);
        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(hits[i].Guid, d.Hits[i].Guid);
            Assert.Equal(hits[i].Px, d.Hits[i].Px);
            Assert.Equal(hits[i].Py, d.Hits[i].Py);
            Assert.Equal(hits[i].Pz, d.Hits[i].Pz);
        }
    }

    // ─── (e) direct geometry break (window vault/pass-through) rides the SAME 0x96 record ─────
    [Fact]
    public void DirectBreak_RidesSameCodec_Kind1_SentinelHp()
    {
        // A window pane broken by ACTOR BODY PASSAGE (no damage event) is funneled into the SAME kind-1
        // destructible record — guid + break origin + the guaranteed-break sentinel hp. Pins: NO new
        // targetKind / surface for direct breaks, and the sentinel survives the wire bit-exact.
        var bytes = TacticalStructDamageCodec.EncodeStructDamage(new TacticalStructDamageCodec.StructBatch(11u,
            new List<TacticalStructDamageCodec.StructHit>
            {
                Hit("dddddddd-dddd-dddd-dddd-dddddddddddd", 3.5f, 1f, -2f, TacticalStructDamageCodec.DirectBreakHealthDamage),
            }));
        Assert.True(TacticalStructDamageCodec.TryDecodeStructDamage(bytes, out var d));
        Assert.Single(d.Hits);
        Assert.Equal(TacticalStructDamageCodec.KindDestructible, d.Hits[0].TargetKind);
        Assert.Equal("dddddddd-dddd-dddd-dddd-dddddddddddd", d.Hits[0].Guid);
        Assert.Equal(TacticalStructDamageCodec.DirectBreakHealthDamage, d.Hits[0].HealthDamage);
    }

    [Fact]
    public void DirectBreakSentinel_Pinned_GuaranteesBreak()
    {
        // The sentinel must always exceed any receiver toughness (receiver health = GetToughness(), small
        // values; health clamps at min 0 so overkill is inert + idempotent). Pin the exact value — changing
        // it changes what older peers replay.
        Assert.Equal(999999f, TacticalStructDamageCodec.DirectBreakHealthDamage);
    }

    [Fact]
    public void IdempotentRebreak_DuplicateGuidHits_BothDecode()
    {
        // Contract for repeated break events on the SAME pane: struct damage is ADDITIVE (no coalescing),
        // duplicates ride the wire as-is; idempotency lives in the client's native replay guards
        // (receiver health-already-zero / Breakable._broken) — a re-break is a clean no-op, so shipping a
        // duplicate is harmless by construction. The codec must not dedup or reject them.
        var g = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
        var bytes = TacticalStructDamageCodec.EncodeStructDamage(new TacticalStructDamageCodec.StructBatch(12u,
            new List<TacticalStructDamageCodec.StructHit>
            {
                Hit(g, 1f, 2f, 3f, TacticalStructDamageCodec.DirectBreakHealthDamage),
                Hit(g, 1f, 2f, 3f, TacticalStructDamageCodec.DirectBreakHealthDamage),
            }));
        Assert.True(TacticalStructDamageCodec.TryDecodeStructDamage(bytes, out var d));
        Assert.Equal(2, d.Hits.Count);
        Assert.Equal(g, d.Hits[0].Guid);
        Assert.Equal(g, d.Hits[1].Guid);
    }

    // ─── (d) pure inert-suppress decision (no double actor damage) ───────
    [Fact]
    public void ShouldNeuterExplosionChain_OnlyOnClientMirror()
    {
        Assert.True(ClientStructDamageInertGate.ShouldNeuterExplosionChain(isClientMirroring: true));    // client mirror → neuter chain
        Assert.False(ClientStructDamageInertGate.ShouldNeuterExplosionChain(isClientMirroring: false));  // host / single-player → run native
    }

    // A single kind=1 record BODY (the bytes AFTER the recLen prefix), matching the codec's encode layout.
    private static byte[] KnownRecordBody(string guid, float px, float py, float pz, float hp)
    {
        byte[] g = Encoding.UTF8.GetBytes(guid);
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(TacticalStructDamageCodec.KindDestructible);
            w.Write((ushort)g.Length);
            w.Write(g);
            w.Write(px); w.Write(py); w.Write(pz);
            w.Write(hp);
            return ms.ToArray();
        }
    }
}
