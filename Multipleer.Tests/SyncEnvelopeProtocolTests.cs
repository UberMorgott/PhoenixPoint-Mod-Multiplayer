using Multipleer.Network.Sync;
using Xunit;

public class SyncEnvelopeProtocolTests
{
    [Fact]
    public void Envelope_RoundTrips()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        var bytes = SyncProtocol.EncodeEnvelope(surfaceId: 5, kind: SyncKind.ActionApply, payload: payload);
        Assert.True(SyncProtocol.TryDecodeEnvelope(bytes, out var id, out var kind, out var pl));
        Assert.Equal((byte)5, id);
        Assert.Equal(SyncKind.ActionApply, kind);
        Assert.Equal(payload, pl);
    }

    [Fact]
    public void Envelope_RoundTrips_EmptyPayload()
    {
        var bytes = SyncProtocol.EncodeEnvelope(surfaceId: 9, kind: SyncKind.ActionRequest, payload: null);
        Assert.True(SyncProtocol.TryDecodeEnvelope(bytes, out var id, out var kind, out var pl));
        Assert.Equal((byte)9, id);
        Assert.Equal(SyncKind.ActionRequest, kind);
        Assert.Empty(pl);
    }

    [Fact]
    public void Envelope_Decode_RejectsGarbage()
    {
        Assert.False(SyncProtocol.TryDecodeEnvelope(new byte[] { 0x01 }, out _, out _, out _));
    }
}
