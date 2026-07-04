using System;
using Multiplayer.Network.Sync;
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

    // ─── defect 1: oversized payload must fail loud, never silent-truncate ──
    [Fact]
    public void Envelope_Encode_OversizedPayload_Throws()
    {
        var oversized = new byte[ushort.MaxValue + 1];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SyncProtocol.EncodeEnvelope(surfaceId: 1, kind: SyncKind.ActionApply, payload: oversized));
    }

    // ─── defect 2: truncated buffer (declared len > available) must not partial-accept ──
    [Fact]
    public void Envelope_Decode_TruncatedPayload_RejectsNoPartialAccept()
    {
        // header [surfaceId=5][kind=1][len=10] but only 3 payload bytes present
        var truncated = new byte[] { 5, 1, 10, 0, 0xAA, 0xBB, 0xCC };
        Assert.False(SyncProtocol.TryDecodeEnvelope(truncated, out _, out _, out _));
    }

    // ─── defect 3: undefined kind byte must drop gracefully (forward-compat) ──
    [Fact]
    public void Envelope_Decode_UnknownKind_Rejects()
    {
        // header [surfaceId=5][kind=0xFF][len=0] — 0xFF is not a defined SyncKind
        var unknownKind = new byte[] { 5, 0xFF, 0, 0 };
        Assert.False(SyncProtocol.TryDecodeEnvelope(unknownKind, out _, out _, out _));
    }
}
