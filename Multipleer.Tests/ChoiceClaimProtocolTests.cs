using System.IO;
using System.Text;
using Multipleer.Network.Sync;
using Xunit;

// Covers the ChoiceClaim wire (client->host): a client click captures its choiceIndex and routes it to the
// host arbiter (first-claim-wins). Wire: [occId:u16][choiceIndex:i32].
public class ChoiceClaimProtocolTests
{
    [Fact]
    public void ChoiceClaim_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeChoiceClaim(occurrenceId: 4242, choiceIndex: 2);
        Assert.True(SyncProtocol.TryDecodeChoiceClaim(bytes, out var occ, out var choiceIndex));
        Assert.Equal(4242, occ);
        Assert.Equal(2, choiceIndex);
    }

    [Fact]
    public void ChoiceClaim_DeclineIndex_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeChoiceClaim(7, -1);
        Assert.True(SyncProtocol.TryDecodeChoiceClaim(bytes, out var occ, out var choiceIndex));
        Assert.Equal(7, occ);
        Assert.Equal(-1, choiceIndex);
    }

    [Fact]
    public void ChoiceClaim_WireBytes_AreStable()
    {
        // Pin the exact on-wire layout: [occId:u16 LE][choiceIndex:i32 LE]. occId 5 = 05 00; choiceIndex 2 = 02 00 00 00.
        var bytes = SyncProtocol.EncodeChoiceClaim(5, 2);
        var expected = new byte[] { 0x05, 0x00, 0x02, 0x00, 0x00, 0x00 };
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void ChoiceClaim_TruncatedPayload_FailsClean()
    {
        // A short buffer must return false, not throw (the arbiter drops a malformed claim).
        byte[] truncated;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)9); // occId only — missing choiceIndex
            truncated = ms.ToArray();
        }
        Assert.False(SyncProtocol.TryDecodeChoiceClaim(truncated, out _, out _));
    }
}
