using System.Collections.Generic;
using Multipleer.Sync.Tactical;
using Xunit;

// Round-trip + robustness tests for the tac.actorstate (0x8F) wire codec (Inc T1 state-spine). Engine-free.
public class TacticalActorStateCodecTests
{
    private const ushort Ap = TacticalLiveCodec.ActorFieldAp;
    private const ushort Wp = TacticalLiveCodec.ActorFieldWp;
    private const ushort St = TacticalLiveCodec.ActorFieldStatuses;

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
