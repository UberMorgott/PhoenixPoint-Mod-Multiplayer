using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using Xunit;

// Geoscape action-relay → envelope CUTOVER (spec 2026-07-02). Pins the ONE gate + the three wire builders:
//   • flag OFF  → byte-identical to the legacy raw 0x60/0x61/0x62 relay (un-flipped peers unaffected),
//   • flag ON   → the SAME inner action bytes wrapped in a 0x67 envelope on GeoIntent/GeoOutcome/GeoReject,
//   • the flip is ATOMIC — one flag switches intent + outcome + reject together.
public class GeoActionRelayTests
{
    // ── The gate stays OFF until the in-game 2-instance pass verifies the enveloped path (legacy is live). ──
    [Fact]
    public void UseEnvelope_DefaultsOff()
        => Assert.False(GeoActionRelay.UseEnvelope);

    // ── INTENT (client→host) ─────────────────────────────────────────────
    [Fact]
    public void BuildIntent_FlagOff_IsByteIdenticalToLegacyPacket()
    {
        var legacyInner = SyncProtocol.EncodeActionRequest(11, 7u, new byte[] { 1, 2, 3 });
        var msg = GeoActionRelay.BuildIntent(false, 11, 7u, new byte[] { 1, 2, 3 });
        Assert.Equal(PacketType.ActionRequest, msg.Type);
        Assert.Equal(legacyInner, msg.Payload);   // byte-for-byte the pre-cutover wire
    }

    [Fact]
    public void BuildIntent_FlagOn_EnvelopeGeoIntent_RoundTrips()
    {
        var msg = GeoActionRelay.BuildIntent(true, 11, 7u, new byte[] { 1, 2, 3 });
        Assert.Equal(PacketType.SyncEnvelope, msg.Type);
        Assert.True(SyncProtocol.TryDecodeEnvelope(msg.Payload, out var sid, out var kind, out var inner));
        Assert.Equal(SurfaceIds.GeoIntent, sid);
        Assert.Equal(SyncKind.ActionRequest, kind);
        // Inner bytes are the SAME action-request the legacy packet carried.
        Assert.Equal(SyncProtocol.EncodeActionRequest(11, 7u, new byte[] { 1, 2, 3 }), inner);
        Assert.True(SyncProtocol.TryDecodeActionRequest(inner, out var id, out var nonce, out var payload));
        Assert.Equal((ushort)11, id);
        Assert.Equal(7u, nonce);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload);
    }

    // ── OUTCOME (host→all) ───────────────────────────────────────────────
    [Fact]
    public void BuildOutcome_FlagOff_IsByteIdenticalToLegacyPacket()
    {
        var legacyInner = SyncProtocol.EncodeActionApply(22, 5UL, new byte[] { 9 });
        var msg = GeoActionRelay.BuildOutcome(false, 22, 5UL, new byte[] { 9 });
        Assert.Equal(PacketType.ActionApply, msg.Type);
        Assert.Equal(legacyInner, msg.Payload);
    }

    [Fact]
    public void BuildOutcome_FlagOn_EnvelopeGeoOutcome_RoundTrips()
    {
        var msg = GeoActionRelay.BuildOutcome(true, 22, 5UL, new byte[] { 9 });
        Assert.Equal(PacketType.SyncEnvelope, msg.Type);
        Assert.True(SyncProtocol.TryDecodeEnvelope(msg.Payload, out var sid, out var kind, out var inner));
        Assert.Equal(SurfaceIds.GeoOutcome, sid);
        Assert.Equal(SyncKind.ActionApply, kind);
        Assert.True(SyncProtocol.TryDecodeActionApply(inner, out var id, out var seq, out var payload));
        Assert.Equal((ushort)22, id);
        Assert.Equal(5UL, seq);
        Assert.Equal(new byte[] { 9 }, payload);
    }

    // ── REJECT (host→originator) ─────────────────────────────────────────
    [Fact]
    public void BuildReject_FlagOff_IsByteIdenticalToLegacyPacket()
    {
        var legacyInner = SyncProtocol.EncodeActionReject(7u, 1, "rejected");
        var msg = GeoActionRelay.BuildReject(false, 7u, 1, "rejected");
        Assert.Equal(PacketType.ActionReject, msg.Type);
        Assert.Equal(legacyInner, msg.Payload);
    }

    [Fact]
    public void BuildReject_FlagOn_EnvelopeGeoReject_RoundTrips()
    {
        var msg = GeoActionRelay.BuildReject(true, 7u, 2, "host blocking prompt (ambush) pending");
        Assert.Equal(PacketType.SyncEnvelope, msg.Type);
        Assert.True(SyncProtocol.TryDecodeEnvelope(msg.Payload, out var sid, out var kind, out var inner));
        Assert.Equal(SurfaceIds.GeoReject, sid);
        Assert.True(SyncProtocol.TryDecodeActionReject(inner, out var nonce, out var code, out var reason));
        Assert.Equal(7u, nonce);
        Assert.Equal((byte)2, code);
        Assert.Equal("host blocking prompt (ambush) pending", reason);
    }

    // ── ATOMIC FLIP: one flag switches all three directions together ─────
    [Fact]
    public void Flip_IsAtomic_AllThreeDirectionsSwitchTogetherOnTheOneFlag()
    {
        // OFF → every direction is a legacy raw packet.
        Assert.Equal(PacketType.ActionRequest, GeoActionRelay.BuildIntent(false, 1, 1u, new byte[0]).Type);
        Assert.Equal(PacketType.ActionApply, GeoActionRelay.BuildOutcome(false, 1, 1UL, new byte[0]).Type);
        Assert.Equal(PacketType.ActionReject, GeoActionRelay.BuildReject(false, 1u, 0, "x").Type);

        // ON → every direction is the unified 0x67 envelope (no partial cutover: intent, outcome AND reject flip
        // together on the single gate — the invariant the spec's atomicity requirement rests on).
        Assert.Equal(PacketType.SyncEnvelope, GeoActionRelay.BuildIntent(true, 1, 1u, new byte[0]).Type);
        Assert.Equal(PacketType.SyncEnvelope, GeoActionRelay.BuildOutcome(true, 1, 1UL, new byte[0]).Type);
        Assert.Equal(PacketType.SyncEnvelope, GeoActionRelay.BuildReject(true, 1u, 0, "x").Type);
    }

    // ── The inner action bytes are the SAME on both rails (only the outer header differs). ──
    [Fact]
    public void InnerActionBytes_AreIdentical_AcrossBothRails()
    {
        var off = GeoActionRelay.BuildIntent(false, 3, 4u, new byte[] { 5, 6 }).Payload;   // == inner directly
        SyncProtocol.TryDecodeEnvelope(GeoActionRelay.BuildIntent(true, 3, 4u, new byte[] { 5, 6 }).Payload,
            out _, out _, out var onInner);
        Assert.Equal(off, onInner);
    }
}
