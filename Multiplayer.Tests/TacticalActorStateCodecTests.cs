using System.Collections.Generic;
using Multiplayer.Sync.Tactical;
using Xunit;

// Round-trip + robustness tests for the tac.actorstate (0x8F) wire codec (Inc T1 state-spine). Engine-free.
public class TacticalActorStateCodecTests
{
    private const ushort Ap = TacticalLiveCodec.ActorFieldAp;
    private const ushort Wp = TacticalLiveCodec.ActorFieldWp;
    private const ushort St = TacticalLiveCodec.ActorFieldStatuses;
    private const ushort Bp = TacticalLiveCodec.ActorFieldBodyPartHp;
    private const ushort He = TacticalLiveCodec.ActorFieldHealth;
    private const ushort Po = TacticalLiveCodec.ActorFieldPos;   // Inc1 full-state: absolute position
    private const ushort Fa = TacticalLiveCodec.ActorFieldFacing; // Inc2: absolute forward vector

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

    // ─── Inc1 full-state: absolute POSITION field (0x0008) round-trip ─────────────────────────────────
    //
    // Position sits BETWEEN statuses (0x0004) and health (0x0020) in ascending bit order, so these tests
    // also guard that an all-fields record keeps the encode/decode aligned with pos wedged in the middle.

    private static TacticalLiveCodec.ActorStateRecord RecPos(int netId, ushort mask, float ap, float wp,
        float health, float px, float py, float pz,
        TacticalLiveCodec.ActorStatus[] statuses, TacticalLiveCodec.BodyPartHp[] parts)
    {
        var r = new TacticalLiveCodec.ActorStateRecord
        { NetId = netId, FieldMask = mask, Ap = ap, Wp = wp, Health = health, PosX = px, PosY = py, PosZ = pz };
        if (statuses != null) r.Statuses.AddRange(statuses);
        if (parts != null) r.BodyParts.AddRange(parts);
        return r;
    }

    [Fact]
    public void PosOnly_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(20u,
            new List<TacticalLiveCodec.ActorStateRecord> { RecPos(42, Po, 0f, 0f, 0f, 12.5f, 1.0f, -7.25f, null, null) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.Equal(42, a.NetId);
        Assert.False(a.HasAp);
        Assert.False(a.HasWp);
        Assert.False(a.HasStatuses);
        Assert.False(a.HasHealth);
        Assert.True(a.HasPos);
        Assert.Equal(12.5f, a.PosX);
        Assert.Equal(1.0f, a.PosY);
        Assert.Equal(-7.25f, a.PosZ);
    }

    [Fact]
    public void ApWpPos_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(21u,
            new List<TacticalLiveCodec.ActorStateRecord>
            { RecPos(7, (ushort)(Ap | Wp | Po), 4f, 6f, 0f, 3f, 0f, 9f, null, null) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasAp);
        Assert.True(a.HasWp);
        Assert.True(a.HasPos);
        Assert.Equal(4f, a.Ap);
        Assert.Equal(6f, a.Wp);
        Assert.Equal(3f, a.PosX);
        Assert.Equal(0f, a.PosY);
        Assert.Equal(9f, a.PosZ);
    }

    [Fact]
    public void AllFieldsWithPos_RoundTrip_AscendingBitOrder()
    {
        // AP | WP | STATUSES | POS | HEALTH | BODYPARTHP all set on ONE record → exercises the full ascending
        // bit order with POS wedged between STATUSES(0x04) and HEALTH(0x20). A misordered read would misalign.
        var batch = new TacticalLiveCodec.ActorStateBatch(22u,
            new List<TacticalLiveCodec.ActorStateRecord>
            {
                RecPos(3, (ushort)(Ap | Wp | St | Po | He | Bp), 2f, 5f, 64.25f, 11f, 2f, 33f,
                    new[] { Stat("g-bleed", 7, 4f) },
                    new[] { Part("Head", 40f), Part("Torso", 90f) }),
            });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.Equal(2f, a.Ap);
        Assert.Equal(5f, a.Wp);
        Assert.Equal("g-bleed", Assert.Single(a.Statuses).DefGuid);
        Assert.True(a.HasPos);
        Assert.Equal(11f, a.PosX);
        Assert.Equal(2f, a.PosY);
        Assert.Equal(33f, a.PosZ);
        Assert.True(a.HasHealth);
        Assert.Equal(64.25f, a.Health);
        Assert.Equal(2, a.BodyParts.Count);
        Assert.Equal("Head", a.BodyParts[0].SlotName);
        Assert.Equal(90f, a.BodyParts[1].Hp);
    }

    [Fact]
    public void PosTruncated_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u);     // seq
            w.Write(1);      // count = 1 actor
            w.Write(5);      // netId
            w.Write(Po);     // fieldMask = pos, but NO 3 floats follow → safe false
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }

    [Fact]
    public void PosPartiallyTruncated_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u);     // seq
            w.Write(1);      // count = 1 actor
            w.Write(5);      // netId
            w.Write(Po);     // fieldMask = pos
            w.Write(1.0f);   // only PosX present; PosY/PosZ missing → safe false (12-byte guard)
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }

    // ─── Inc2: absolute FACING field (0x0010) round-trip ──────────────────────────────────────────────
    //
    // Facing sits BETWEEN pos (0x0008) and health (0x0020) in ascending bit order, so these tests guard
    // that an all-fields record keeps the encode/decode aligned with facing wedged between pos and health.

    private static TacticalLiveCodec.ActorStateRecord RecFac(int netId, ushort mask, float ap, float wp,
        float health, float px, float py, float pz, float fx, float fy, float fz)
    {
        return new TacticalLiveCodec.ActorStateRecord
        {
            NetId = netId, FieldMask = mask, Ap = ap, Wp = wp, Health = health,
            PosX = px, PosY = py, PosZ = pz, FacingX = fx, FacingY = fy, FacingZ = fz
        };
    }

    [Fact]
    public void FacingOnly_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(30u,
            new List<TacticalLiveCodec.ActorStateRecord> { RecFac(42, Fa, 0, 0, 0, 0, 0, 0, 0f, 0f, 1f) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasFacing);
        Assert.False(a.HasPos);
        Assert.Equal(0f, a.FacingX); Assert.Equal(0f, a.FacingY); Assert.Equal(1f, a.FacingZ);
    }

    [Fact]
    public void PosThenFacing_RoundTrips_AscendingBitOrder()
    {
        // Pos(0x08) then Facing(0x10) on one record → guards the ascending-bit-order insertion.
        var batch = new TacticalLiveCodec.ActorStateBatch(31u,
            new List<TacticalLiveCodec.ActorStateRecord>
            { RecFac(7, (ushort)(Po | Fa), 0, 0, 0, 1f, 2f, 3f, 1f, 0f, 0f) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasPos); Assert.True(a.HasFacing);
        Assert.Equal(3f, a.PosZ); Assert.Equal(1f, a.FacingX);
    }

    [Fact]
    public void AllFieldsWithFacing_RoundTrip_AscendingBitOrder()
    {
        // AP|WP|STATUSES|POS|FACING|HEALTH|BODYPARTHP → full ascending order with Facing wedged between Pos and Health.
        var r = RecFac(3, (ushort)(Ap | Wp | St | Po | Fa | He | Bp), 2f, 5f, 64.25f, 11f, 2f, 33f, 0f, 0f, 1f);
        r.Statuses.Add(Stat("g-bleed", 7, 4f));
        r.BodyParts.Add(Part("Head", 40f));
        var batch = new TacticalLiveCodec.ActorStateBatch(32u,
            new List<TacticalLiveCodec.ActorStateRecord> { r });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.Equal(33f, a.PosZ);
        Assert.True(a.HasFacing); Assert.Equal(1f, a.FacingZ);
        Assert.Equal(64.25f, a.Health);
        Assert.Equal("Head", a.BodyParts[0].SlotName);
    }

    [Fact]
    public void FacingTruncated_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u); w.Write(1); w.Write(5);
            w.Write(Fa);     // facing bit, but NO 3 floats follow → safe false (12-byte guard)
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

    // ─── TS5 (a): per-weapon AMMO field (0x0400) + (b): faction DISPLAY field (0x0800) round-trip ────────
    //
    // Ammo (0x0400) and faction (0x0800) are the two HIGHEST bits we emit → encoded LAST, ascending bit order
    // AFTER bodypart-HP (0x0200): Ammo then Faction. These tests pin the new fields AND assert the extension is
    // BACKWARD-TOLERANT (a record WITHOUT the new bits keeps its old byte layout + decodes with them absent).

    private const ushort Am = TacticalLiveCodec.ActorFieldAmmo;
    private const ushort Fc = TacticalLiveCodec.ActorFieldFaction;

    private static TacticalLiveCodec.WeaponAmmo Wa(int slot, int charges)
        => new TacticalLiveCodec.WeaponAmmo(slot, charges);

    private static TacticalLiveCodec.ActorStateRecord RecAmmoFac(int netId, ushort mask, int faction,
        params TacticalLiveCodec.WeaponAmmo[] ammo)
    {
        var r = new TacticalLiveCodec.ActorStateRecord { NetId = netId, FieldMask = mask, Faction = faction };
        if (ammo != null) r.Ammo.AddRange(ammo);
        return r;
    }

    [Fact]
    public void AmmoOnly_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(40u,
            new List<TacticalLiveCodec.ActorStateRecord>
            { RecAmmoFac(42, Am, 0, Wa(0, 6), Wa(2, 12)) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasAmmo);
        Assert.False(a.HasFaction);
        Assert.False(a.HasAp);
        Assert.Equal(2, a.Ammo.Count);
        Assert.Equal(0, a.Ammo[0].SlotIndex);
        Assert.Equal(6, a.Ammo[0].Charges);
        Assert.Equal(2, a.Ammo[1].SlotIndex);
        Assert.Equal(12, a.Ammo[1].Charges);
    }

    [Fact]
    public void AmmoZeroCount_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(41u,
            new List<TacticalLiveCodec.ActorStateRecord> { RecAmmoFac(1, Am, 0) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasAmmo);
        Assert.Empty(a.Ammo);
    }

    [Fact]
    public void FactionOnly_RoundTrips()
    {
        var batch = new TacticalLiveCodec.ActorStateBatch(42u,
            new List<TacticalLiveCodec.ActorStateRecord> { RecAmmoFac(9, Fc, 3) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasFaction);
        Assert.False(a.HasAmmo);
        Assert.Equal(3, a.Faction);
    }

    [Fact]
    public void AmmoAndFaction_RoundTrips_AscendingBitOrder()
    {
        // Both new bits on one record → Ammo(0x0400) then Faction(0x0800) in ascending order. A misordered
        // read would misalign (faction int consumed as an ammo pair or vice-versa).
        var batch = new TacticalLiveCodec.ActorStateBatch(43u,
            new List<TacticalLiveCodec.ActorStateRecord>
            { RecAmmoFac(5, (ushort)(Am | Fc), 2, Wa(1, 4)) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.True(a.HasAmmo);
        Assert.True(a.HasFaction);
        Assert.Equal(1, Assert.Single(a.Ammo).SlotIndex);
        Assert.Equal(4, a.Ammo[0].Charges);
        Assert.Equal(2, a.Faction);
    }

    [Fact]
    public void AllFields_WithAmmoAndFaction_RoundTrip_FullAscendingBitOrder()
    {
        // Every emitted bit on ONE record → the full ascending order AP,WP,STATUSES,POS,FACING,HEALTH,
        // BODYPARTHP,AMMO,FACTION with ammo+faction as the two highest tail fields.
        var r = new TacticalLiveCodec.ActorStateRecord
        {
            NetId = 3,
            FieldMask = (ushort)(Ap | Wp | St | Po | Fa | He | Bp | Am | Fc),
            Ap = 2f, Wp = 5f, Health = 64.25f,
            PosX = 11f, PosY = 2f, PosZ = 33f, FacingX = 0f, FacingY = 0f, FacingZ = 1f,
            Faction = 4,
        };
        r.Statuses.Add(Stat("g-bleed", 7, 4f));
        r.BodyParts.Add(Part("Head", 40f));
        r.Ammo.Add(Wa(0, 9));
        r.Ammo.Add(Wa(3, 1));
        var batch = new TacticalLiveCodec.ActorStateBatch(44u,
            new List<TacticalLiveCodec.ActorStateRecord> { r });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.Equal(33f, a.PosZ);
        Assert.Equal(1f, a.FacingZ);
        Assert.Equal(64.25f, a.Health);
        Assert.Equal("Head", a.BodyParts[0].SlotName);
        Assert.Equal(2, a.Ammo.Count);
        Assert.Equal(9, a.Ammo[0].Charges);
        Assert.Equal(3, a.Ammo[1].SlotIndex);
        Assert.Equal(4, a.Faction);
    }

    [Fact]
    public void LegacyMask_NoAmmoNoFaction_ByteLayoutUnchanged_AndDecodes()
    {
        // BACKWARD-TOLERANCE PIN: a record whose mask omits the new bits must be BYTE-IDENTICAL to the
        // pre-TS5 layout (no ammo/faction bytes leak). An Ap-only record is exactly seq(4)+count(4)+netId(4)
        // +mask(2)+ap(4) = 18 bytes — unchanged by the extension.
        var batch = new TacticalLiveCodec.ActorStateBatch(1u,
            new List<TacticalLiveCodec.ActorStateRecord> { Rec(42, Ap, 3.5f, 0f) });
        byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
        Assert.Equal(18, bytes.Length);
        Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
        var a = Assert.Single(got.Actors);
        Assert.False(a.HasAmmo);
        Assert.False(a.HasFaction);
        Assert.Empty(a.Ammo);
    }

    [Fact]
    public void LegacyBuffer_OldDecoderLayout_DecodesCleanWithNewBitsAbsent()
    {
        // A hand-written buffer produced by an OLDER peer (mask=Ap|Wp|Health, NO ammo/faction tail) decodes
        // green on the extended decoder with the new bits reported absent — proving the mask extension does
        // not break an old-mask frame.
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(2u);                        // seq
            w.Write(1);                         // 1 actor
            w.Write(7);                         // netId
            w.Write((ushort)(Ap | Wp | He));    // legacy mask, ascending: ap, wp, health
            w.Write(4f);                        // ap
            w.Write(6f);                        // wp
            w.Write(88f);                       // health
            Assert.True(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out var got));
            var a = Assert.Single(got.Actors);
            Assert.True(a.HasAp); Assert.True(a.HasWp); Assert.True(a.HasHealth);
            Assert.False(a.HasAmmo); Assert.False(a.HasFaction);
            Assert.Equal(88f, a.Health);
        }
    }

    [Fact]
    public void AmmoTruncated_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u); w.Write(1); w.Write(5);
            w.Write(Am);          // ammo bit, but NO count follows → safe false
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }

    [Fact]
    public void AmmoCountAbsurd_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u); w.Write(1); w.Write(5);
            w.Write(Am);
            w.Write(int.MaxValue);// weaponCount → exceeds cap → false
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }

    [Fact]
    public void FactionTruncated_ReturnsFalse()
    {
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u); w.Write(1); w.Write(5);
            w.Write(Fc);          // faction bit, but NO i32 follows → safe false
            Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
        }
    }
}
