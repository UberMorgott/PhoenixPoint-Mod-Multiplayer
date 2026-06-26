using System.Collections.Generic;
using Multipleer.Sync.Tactical;
using Xunit;

public class TacticalDeployCodecTests
{
    private static List<TacticalActorRegistry.ActorRow> SampleTable() => new List<TacticalActorRegistry.ActorRow>
    {
        new TacticalActorRegistry.ActorRow(100, 100, 1.5f, 2.5f, 3.5f),
        new TacticalActorRegistry.ActorRow(101, 101, -4f, 0f, 12.25f),
        new TacticalActorRegistry.ActorRow(TacticalActorRegistry.MintBase, 0, 7f, 8f, 9f),
    };

    [Fact]
    public void Deploy_RoundTrips_FullPayload()
    {
        var gp = new byte[] { 1, 2, 3, 4, 5 };
        var snap = new byte[] { 9, 8, 7, 6 };
        var table = SampleTable();

        var bytes = TacticalDeployCodec.Encode(missionSiteId: 17, gameParamsBytes: gp,
            snapshotBytes: snap, actorTable: table);

        Assert.True(TacticalDeployCodec.TryDecode(bytes, out var p));
        Assert.Equal(17, p.MissionSiteId);
        Assert.Equal(gp, p.GameParamsBytes);
        Assert.Equal(snap, p.SnapshotBytes);
        Assert.Equal(3, p.ActorTable.Count);

        Assert.Equal(100, p.ActorTable[0].NetId);
        Assert.Equal(100, p.ActorTable[0].GeoUnitId);
        Assert.Equal(1.5f, p.ActorTable[0].X);
        Assert.Equal(2.5f, p.ActorTable[0].Y);
        Assert.Equal(3.5f, p.ActorTable[0].Z);

        Assert.Equal(TacticalActorRegistry.MintBase, p.ActorTable[2].NetId);
        Assert.Equal(0, p.ActorTable[2].GeoUnitId);
        Assert.Equal(12.25f, p.ActorTable[1].Z);
    }

    [Fact]
    public void Deploy_RoundTrips_EmptyBlobsAndTable()
    {
        var bytes = TacticalDeployCodec.Encode(missionSiteId: -1, gameParamsBytes: null,
            snapshotBytes: null, actorTable: null);

        Assert.True(TacticalDeployCodec.TryDecode(bytes, out var p));
        Assert.Equal(-1, p.MissionSiteId);
        Assert.Empty(p.GameParamsBytes);
        Assert.Empty(p.SnapshotBytes);
        Assert.Empty(p.ActorTable);
    }

    [Fact]
    public void Deploy_PreservesLargeBlobs()
    {
        // Snapshot blobs are large (full tactical save). Ensure int32 length framing handles > ushort sizes.
        var big = new byte[70000];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i % 251);

        var bytes = TacticalDeployCodec.Encode(1, new byte[0], big, SampleTable());
        Assert.True(TacticalDeployCodec.TryDecode(bytes, out var p));
        Assert.Equal(big, p.SnapshotBytes);
        Assert.Equal(3, p.ActorTable.Count);
    }

    [Fact]
    public void Deploy_Decode_RejectsTruncatedBlob_NoPartialAccept()
    {
        var bytes = TacticalDeployCodec.Encode(1, new byte[] { 1, 2, 3, 4 }, new byte[0], null);
        // Chop the last byte → the declared gameParams length now exceeds the buffer.
        var truncated = new byte[bytes.Length - 1];
        System.Array.Copy(bytes, truncated, truncated.Length);
        Assert.False(TacticalDeployCodec.TryDecode(truncated, out var p));
        Assert.Null(p);
    }

    [Fact]
    public void Deploy_Decode_RejectsGarbage()
    {
        Assert.False(TacticalDeployCodec.TryDecode(new byte[] { 0x01, 0x02 }, out _));
        Assert.False(TacticalDeployCodec.TryDecode(null, out _));
    }

    [Fact]
    public void Deploy_Decode_RejectsAbsurdActorCount()
    {
        // Hand-craft a header with a huge actor count that exceeds the remaining buffer → clean reject,
        // never a wild allocation.
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1);            // siteId
            w.Write(0);            // gameParams len
            w.Write(0);            // snapshot len
            w.Write(int.MaxValue); // absurd actor count
            Assert.False(TacticalDeployCodec.TryDecode(ms.ToArray(), out _));
        }
    }

    [Fact]
    public void Deploy_SurfaceId_IsHighNonCollidingByte()
    {
        // tac.deploy must sit above the geoscape action surfaces (1-30) and state channels (1-5).
        Assert.True(TacticalSurfaceIds.TacDeploy >= 0x80);
    }
}
