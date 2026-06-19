using System.Collections.Generic;
using Multipleer.Sync.Tactical;
using Xunit;

// Round-trip + robustness tests for the tac.actorstate (0x8F) wire codec (Inc T1 state-spine). Engine-free.
public class TacticalActorStateCodecTests
{
    private const ushort Ap = TacticalLiveCodec.ActorFieldAp;
    private const ushort Wp = TacticalLiveCodec.ActorFieldWp;
    private const ushort St = TacticalLiveCodec.ActorFieldStatuses;
    private const ushort Bp = TacticalLiveCodec.ActorFieldBodyPartHp;
    private const ushort He = TacticalLiveCodec.ActorFieldHealth;

    private static TacticalLiveCodec.ActorStateRecord Rec(int netId, ushort mask, float ap, float wp,
        params TacticalLiveCodec.ActorStatus[] statuses)
    {
        var r = new TacticalLiveCodec.ActorStateRecord { NetId = netId, FieldMask = mask, Ap = ap, Wp = wp };
        if (statuses != null) r.Statuses.AddRange(statuses);
        return r;
    }

    private static TacticalLiveCodec.ActorStatus Stat(string guid, int src, float val)
        => new TacticalLiveCodec.ActorStatus(guid, src, val);

    [Fact]
    public void EmptyBatch_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(7u, new List<TacticalLiveCodec.ActorStateRecord>());
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        Assert.Equal(7u, got.Seq);
        Assert.Empty(got.Actors);
    }

    [Fact]
    public void ApOnly_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(1u,
            new List<TacticalLiveCodec.ActorStateRecord> { Rec(42, Ap, 3.5f, 0f) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.Equal(42, a.NetId);
        Assert.True(a.HasAp);
        Assert.False(a.HasWp);
        Assert.False(a.HasStatuses);
        Assert.Equal(3.5f, a.Ap);
        Assert.Empty(a.Statuses);
    }

    [Fact]
    public void WpOnly_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(2u,
            new List<TacticalLiveCodec.ActorStateRecord> { Rec(9, Wp, 0f, 2.0f) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.False(a.HasAp);
        Assert.True(a.HasWp);
        Assert.Equal(2.0f, a.Wp);
    }

    [Fact]
    public void ApWp_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(3u,
            new List<TacticalLiveCodec.ActorStateRecord> { Rec(5, (ushort)(Ap | Wp), 4f, 6f) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasAp);
        Assert.True(a.HasWp);
        Assert.Equal(4f, a.Ap);
        Assert.Equal(6f, a.Wp);
    }

    [Fact]
    public void ApWpStatuses_ZeroStatuses_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(4u,
            new List<TacticalLiveCodec.ActorStateRecord> { Rec(5, (ushort)(Ap | Wp | St), 4f, 6f) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasStatuses);
        Assert.Empty(a.Statuses);
    }

    [Fact]
    public void StatusesOnly_OneStatus_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(5u,
            new List<TacticalLiveCodec.ActorStateRecord> { Rec(5, St, 0f, 0f, Stat("guid-poison", 99, 3f)) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.False(a.HasAp);
        Assert.True(a.HasStatuses);
        var s = Assert.Single(a.Statuses);
        Assert.Equal("guid-poison", s.DefGuid);
        Assert.Equal(99, s.SourceNetId);
        Assert.Equal(3f, s.Value);
    }

    [Fact]
    public void Full_NActors_NStatuses_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(6u, new List<TacticalLiveCodec.ActorStateRecord>
        {
            Rec(1, (ushort)(Ap | Wp | St), 1f, 2f,
                Stat("g1", -1, 0f), Stat("g2", 7, 4f)),
            Rec(2, Ap, 9f, 0f),
            Rec(3, (ushort)(Wp | St), 0f, 1.5f, Stat("g3", 2, 6f)),
        });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        Assert.Equal(3, got.Actors.Count);

        Assert.Equal(2, got.Actors[0].Statuses.Count);
        Assert.Equal("g1", got.Actors[0].Statuses[0].DefGuid);
        Assert.Equal(-1, got.Actors[0].Statuses[0].SourceNetId);
        Assert.Equal("g2", got.Actors[0].Statuses[1].DefGuid);

        Assert.True(got.Actors[1].HasAp);
        Assert.False(got.Actors[1].HasStatuses);
        Assert.Equal(9f, got.Actors[1].Ap);

        Assert.Single(got.Actors[2].Statuses);
        Assert.Equal("g3", got.Actors[2].Statuses[0].DefGuid);
        Assert.Equal(1.5f, got.Actors[2].Wp);
    }

    // ─── Feature B: per-bodypart-HP sub-channel (0x0200) round-trip ──────────────────────────────────

    private static TacticalLiveCodec.BodyPartHp Part(string slot, float hp)
        => new TacticalLiveCodec.BodyPartHp(slot, hp);

    private static TacticalLiveCodec.ActorStateRecord RecBp(int netId, ushort mask, float ap, float wp,
        TacticalLiveCodec.ActorStatus[] statuses, TacticalLiveCodec.BodyPartHp[] parts)
    {
        var r = new TacticalLiveCodec.ActorStateRecord { NetId = netId, FieldMask = mask, Ap = ap, Wp = wp };
        if (statuses != null) r.Statuses.AddRange(statuses);
        if (parts != null) r.BodyParts.AddRange(parts);
        return r;
    }

    [Fact]
    public void BodyPartsOnly_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(8u,
            new List<TacticalLiveCodec.ActorStateRecord>
            {
                RecBp(11, Bp, 0f, 0f, null, new[] { Part("Torso", 100f), Part("LeftArm", 0f) }),
            });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.False(a.HasAp);
        Assert.True(a.HasBodyParts);
        Assert.Equal(2, a.BodyParts.Count);
        Assert.Equal("Torso", a.BodyParts[0].SlotName);
        Assert.Equal(100f, a.BodyParts[0].Hp);
        Assert.Equal("LeftArm", a.BodyParts[1].SlotName);
        Assert.Equal(0f, a.BodyParts[1].Hp);
    }

    [Fact]
    public void ApWpStatusesBodyParts_AllFields_RoundTrip_AscendingBitOrder()
    {
        // All four emitted bits set on ONE record → exercises the ascending-bit-order encode/decode
        // (AP, WP, STATUSES, then BODYPARTHP last). A misordered read would misalign and fail.
        var batch = new TacticalLiveCodec.ActorStateBatch(9u,
            new List<TacticalLiveCodec.ActorStateRecord>
            {
                RecBp(3, (ushort)(Ap | Wp | St | Bp), 2f, 5f,
                    new[] { Stat("g-bleed", 7, 4f) },
                    new[] { Part("Head", 40f), Part("Torso", 90f) }),
            });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.Equal(2f, a.Ap);
        Assert.Equal(5f, a.Wp);
        Assert.Equal("g-bleed", Assert.Single(a.Statuses).DefGuid);
        Assert.Equal(2, a.BodyParts.Count);
        Assert.Equal("Head", a.BodyParts[0].SlotName);
        Assert.Equal(40f, a.BodyParts[0].Hp);
        Assert.Equal("Torso", a.BodyParts[1].SlotName);
    }

    [Fact]
    public void BodyPartsZeroCount_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(10u,
            new List<TacticalLiveCodec.ActorStateRecord> { RecBp(1, Bp, 0f, 0f, null, null) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasBodyParts);
        Assert.Empty(a.BodyParts);
    }

    [Fact]
    public void AbsurdBodyPartCount_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u);          // seq
            w.Write(1);           // count = 1 actor
            w.Write(5);           // netId
            w.Write(Bp);          // fieldMask = bodyparts
            w.Write(int.MaxValue);// bodyPartCount → exceeds cap → false
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }

    // ─── Feature D: actor-level absolute HEALTH field (0x0020) round-trip ─────────────────────────────

    private static TacticalLiveCodec.ActorStateRecord RecHp(int netId, ushort mask, float ap, float wp,
        float health, TacticalLiveCodec.ActorStatus[] statuses, TacticalLiveCodec.BodyPartHp[] parts)
    {
        var r = new TacticalLiveCodec.ActorStateRecord
        { NetId = netId, FieldMask = mask, Ap = ap, Wp = wp, Health = health };
        if (statuses != null) r.Statuses.AddRange(statuses);
        if (parts != null) r.BodyParts.AddRange(parts);
        return r;
    }

    [Fact]
    public void HealthOnly_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(11u,
            new List<TacticalLiveCodec.ActorStateRecord> { RecHp(42, He, 0f, 0f, 137.5f, null, null) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.Equal(42, a.NetId);
        Assert.False(a.HasAp);
        Assert.False(a.HasWp);
        Assert.False(a.HasStatuses);
        Assert.False(a.HasBodyParts);
        Assert.True(a.HasHealth);
        Assert.Equal(137.5f, a.Health);
    }

    [Fact]
    public void ApWpHealth_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(12u,
            new List<TacticalLiveCodec.ActorStateRecord>
            { RecHp(7, (ushort)(Ap | Wp | He), 4f, 6f, 88f, null, null) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasAp);
        Assert.True(a.HasWp);
        Assert.True(a.HasHealth);
        Assert.Equal(4f, a.Ap);
        Assert.Equal(6f, a.Wp);
        Assert.Equal(88f, a.Health);
    }

    [Fact]
    public void AllFieldsWithHealth_RoundTrip_AscendingBitOrder()
    {
        // AP | WP | STATUSES | HEALTH | BODYPARTHP all set on ONE record → exercises ascending bit order
        // (AP, WP, STATUSES, HEALTH(0x0020), then BODYPARTHP(0x0200) last). A misordered read would misalign.
        var batch = new TacticalLiveCodec.ActorStateBatch(13u,
            new List<TacticalLiveCodec.ActorStateRecord>
            {
                RecHp(3, (ushort)(Ap | Wp | St | He | Bp), 2f, 5f, 64.25f,
                    new[] { Stat("g-bleed", 7, 4f) },
                    new[] { Part("Head", 40f), Part("Torso", 90f) }),
            });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.Equal(2f, a.Ap);
        Assert.Equal(5f, a.Wp);
        Assert.Equal("g-bleed", Assert.Single(a.Statuses).DefGuid);
        Assert.True(a.HasHealth);
        Assert.Equal(64.25f, a.Health);
        Assert.Equal(2, a.BodyParts.Count);
        Assert.Equal("Head", a.BodyParts[0].SlotName);
        Assert.Equal(90f, a.BodyParts[1].Hp);
    }

    [Fact]
    public void HealthTruncated_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u);   // seq
            w.Write(1);    // count = 1 actor
            w.Write(5);    // netId
            w.Write(He);   // fieldMask = health, but NO health float follows → safe false
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }

    [Fact]
    public void Truncated_Header_ReturnsFalse()
    {
        Assert.False(TacticalLiveCodec.TryDecodeActorState(new byte[] { 1, 2, 3 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeActorState(null, out _));
    }

    [Fact]
    public void Truncated_MidRecord_ReturnsFalse()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(1u,
            new List<TacticalLiveCodec.ActorStateRecord> { Rec(5, (ushort)(Ap | Wp | St), 4f, 6f, Stat("g", 1, 2f)) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        // Chop the last few bytes (mid status) → safe false, no partial accept.
        var chopped = new byte[bytes.Length - 5];
        System.Array.Copy(bytes, chopped, chopped.Length);
        Assert.False(TacticalLiveCodec.TryDecodeActorState(chopped, out _));
    }

    [Fact]
    public void AbsurdActorCount_ReturnsFalse()
    {
        // seq=1, count=int.MaxValue, no records → count exceeds cap → false.
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u);
            w.Write(int.MaxValue);
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }

    [Fact]
    public void AbsurdStatusCount_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u);          // seq
            w.Write(1);           // count = 1 actor
            w.Write(5);           // netId
            w.Write(St);          // fieldMask = statuses
            w.Write(int.MaxValue);// statusCount → exceeds cap → false
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }

    [Fact]
    public void NegativeActorCount_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u);
            w.Write(-1);
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }
}
