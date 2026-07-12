using System;
using System.IO;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// Security regression guards for the unbounded-allocation class (audit FA-0003 / FA-0012 / FA-0016): a
/// wire-supplied length/count is read raw and sizes an eager allocation BEFORE the bytes are proven present.
/// Each test crafts a hostile frame and asserts it is rejected (thrown/false) with no giant transient alloc,
/// while a well-formed frame still round-trips.
/// </summary>
public class UnboundedAllocGuardTests
{
    // ─── FA-0003: NetworkMessage.Deserialize payloadLen ────────────────────
    [Fact]
    public void NetworkMessage_RejectsOversizedOrAbsentPayloadLen_ButRoundTripsValid()
    {
        // Valid message still round-trips (guard must not reject the happy path).
        var original = new NetworkMessage { Payload = new byte[] { 1, 2, 3, 4, 5 } };
        var round = NetworkMessage.Deserialize(original.Serialize());
        Assert.Equal(original.Payload, round.Payload);

        // A 37-byte header-only frame that declares int.MaxValue payload bytes → rejected before alloc.
        var huge = new byte[37];
        BitConverter.GetBytes(int.MaxValue).CopyTo(huge, 33);
        Assert.Throws<ArgumentException>(() => NetworkMessage.Deserialize(huge));

        // A frame that declares more payload than the buffer actually holds → rejected (presence check).
        var lies = new byte[37];
        BitConverter.GetBytes(100).CopyTo(lies, 33);
        Assert.Throws<ArgumentException>(() => NetworkMessage.Deserialize(lies));
    }

    // ─── FA-0012: parity manifest / JOIN counts + block length ─────────────
    [Fact]
    public void ParityManifest_RejectsOversizedCount()
    {
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(int.MaxValue);   // dlcCount, with no strings following
            Assert.Throws<InvalidDataException>(
                () => MessageSerializer.DeserializeParityManifest(ms.ToArray()));
        }
    }

    [Fact]
    public void Join_RejectsOversizedManifestBlockLength()
    {
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(Guid.NewGuid().ToByteArray());
            bw.Write("nick");
            bw.Write(true);            // manifest present
            bw.Write(int.MaxValue);    // block length → would feed ReadBytes(int.MaxValue)
            Assert.Throws<InvalidDataException>(
                () => MessageSerializer.DeserializeJoin(ms.ToArray()));
        }
    }

    // ─── FA-0016: tactical deploy chunk count / totalLen ───────────────────
    private static byte[] ChunkHeader(int count, int totalLen, int fragLen)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(1);          // siteId
            w.Write(1);          // deployGeneration
            w.Write(0);          // chunkIndex
            w.Write(count);
            w.Write(totalLen);
            w.Write(fragLen);
            return ms.ToArray();
        }
    }

    [Fact]
    public void TacticalDeployChunk_RejectsOversizedCountAndTotalLen()
    {
        Assert.False(TacticalDeployChunkCodec.TryDecode(
            ChunkHeader(count: int.MaxValue, totalLen: 0, fragLen: 0), out _));
        Assert.False(TacticalDeployChunkCodec.TryDecode(
            ChunkHeader(count: 1, totalLen: int.MaxValue, fragLen: 0), out _));
    }
}
