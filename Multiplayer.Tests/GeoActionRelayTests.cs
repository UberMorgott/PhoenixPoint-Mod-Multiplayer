using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using Xunit;

// Geoscape action-relay envelope rail (spec 2026-07-02). Pins the three wire builders now that the envelope is
// the SOLE rail (the legacy raw 0x60/0x61/0x62 packets + the UseEnvelope gate were deleted at cutover):
// every direction wraps the SAME inner EncodeAction* bytes in a 0x67 envelope on GeoIntent/GeoOutcome/GeoReject,
// and each round-trips back to its inputs (send-side build ↔ receive-side decode).
public class GeoActionRelayTests
{
    // ── INTENT (client→host) → GeoIntent 0xA2 envelope ───────────────────
    [Fact]
    public void BuildIntent_EnvelopeGeoIntent_RoundTrips()
    {
        var msg = GeoActionRelay.BuildIntent(11, 7u, new byte[] { 1, 2, 3 });
        Assert.Equal(PacketType.SyncEnvelope, msg.Type);
        Assert.True(SyncProtocol.TryDecodeEnvelope(msg.Payload, out var sid, out var kind, out var inner));
        Assert.Equal(SurfaceIds.GeoIntent, sid);
        Assert.Equal(SyncKind.ActionRequest, kind);
        // Inner bytes are exactly the EncodeActionRequest the relay wraps — and decode back to the inputs.
        Assert.Equal(SyncProtocol.EncodeActionRequest(11, 7u, new byte[] { 1, 2, 3 }), inner);
        Assert.True(SyncProtocol.TryDecodeActionRequest(inner, out var id, out var nonce, out var payload));
        Assert.Equal((ushort)11, id);
        Assert.Equal(7u, nonce);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload);
    }

    // ── OUTCOME (host→all) → GeoOutcome 0xA3 envelope ────────────────────
    [Fact]
    public void BuildOutcome_EnvelopeGeoOutcome_RoundTrips()
    {
        var msg = GeoActionRelay.BuildOutcome(22, 5UL, new byte[] { 9 });
        Assert.Equal(PacketType.SyncEnvelope, msg.Type);
        Assert.True(SyncProtocol.TryDecodeEnvelope(msg.Payload, out var sid, out var kind, out var inner));
        Assert.Equal(SurfaceIds.GeoOutcome, sid);
        Assert.Equal(SyncKind.ActionApply, kind);
        Assert.True(SyncProtocol.TryDecodeActionApply(inner, out var id, out var seq, out var payload));
        Assert.Equal((ushort)22, id);
        Assert.Equal(5UL, seq);
        Assert.Equal(new byte[] { 9 }, payload);
    }

    // ── REJECT (host→originator) → GeoReject 0xA4 envelope ───────────────
    [Fact]
    public void BuildReject_EnvelopeGeoReject_RoundTrips()
    {
        var msg = GeoActionRelay.BuildReject(7u, 2, "host blocking prompt (ambush) pending");
        Assert.Equal(PacketType.SyncEnvelope, msg.Type);
        Assert.True(SyncProtocol.TryDecodeEnvelope(msg.Payload, out var sid, out _, out var inner));
        Assert.Equal(SurfaceIds.GeoReject, sid);
        Assert.True(SyncProtocol.TryDecodeActionReject(inner, out var nonce, out var code, out var reason));
        Assert.Equal(7u, nonce);
        Assert.Equal((byte)2, code);
        Assert.Equal("host blocking prompt (ambush) pending", reason);
    }

    // ── All three directions are the unified 0x67 envelope — one rail, no legacy raw packet type ─
    [Fact]
    public void AllThreeDirections_AreTheUnifiedEnvelope()
    {
        Assert.Equal(PacketType.SyncEnvelope, GeoActionRelay.BuildIntent(1, 1u, new byte[0]).Type);
        Assert.Equal(PacketType.SyncEnvelope, GeoActionRelay.BuildOutcome(1, 1UL, new byte[0]).Type);
        Assert.Equal(PacketType.SyncEnvelope, GeoActionRelay.BuildReject(1u, 0, "x").Type);
    }

    // ── The envelope carries the inner action bytes verbatim (only the outer header is the wrapper). ──
    [Fact]
    public void EnvelopeInner_IsTheRawActionRequestBytes()
    {
        var expected = SyncProtocol.EncodeActionRequest(3, 4u, new byte[] { 5, 6 });
        Assert.True(SyncProtocol.TryDecodeEnvelope(GeoActionRelay.BuildIntent(3, 4u, new byte[] { 5, 6 }).Payload,
            out _, out _, out var inner));
        Assert.Equal(expected, inner);
    }
}
