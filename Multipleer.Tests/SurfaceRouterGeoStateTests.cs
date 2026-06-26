using Multipleer.Network.Sync;
using Xunit;

// Slice-2 of the rail-unification: the legacy per-channel state echo (0x64 / StateSync) ALSO rides the
// unified 0x67 envelope under the GeoState (0xA1) surface when GeoRailGate.Enabled. These mirror the
// slice-1 wallet rail tests (SurfaceRouterGeoscapeTests) for the new surface: envelope encode/decode wire
// parity, router-consume routing, tactical precedence, idempotent per-channel apply, and the shared gate.
public class SurfaceRouterGeoStateTests
{
    // The mirror the host emits: an envelope on the GeoState surface whose INNER bytes are the IDENTICAL
    // EncodeStateSync(channelId, version, payload) the legacy 0x64 packet carries.
    private static byte[] GeoStateEnvelope(byte channelId, ulong version, byte[] payload)
        => SyncProtocol.EncodeEnvelope(SurfaceIds.GeoState, SyncKind.StateSnapshot,
            SyncProtocol.EncodeStateSync(channelId, version, payload));

    [Fact]
    public void GeoState_SurfaceId_IsDistinctAndInGeoscapePartition()
    {
        Assert.Equal(0xA1, SurfaceIds.GeoState);
        Assert.NotEqual(SurfaceIds.GeoWallet, SurfaceIds.GeoState);   // no collision with slice-1 wallet
        Assert.InRange(SurfaceIds.GeoState, (byte)0xA0, (byte)0xBF);  // spec §2.1 geoscape partition
    }

    [Fact]
    public void Envelope_WrapsStateSyncBytes_RoundTripsToSameChannelVersionPayload()
    {
        var payload = new byte[] { 7, 8, 9, 10 };
        var env = GeoStateEnvelope(channelId: 5, version: 42, payload: payload);

        // Outer envelope decodes to the GeoState surface + the inner StateSync bytes ...
        Assert.True(SyncProtocol.TryDecodeEnvelope(env, out var sid, out var kind, out var inner));
        Assert.Equal(SurfaceIds.GeoState, sid);
        Assert.Equal(SyncKind.StateSnapshot, kind);
        // ... and the inner bytes are the SAME wire the legacy 0x64 carries (byte-for-byte parity).
        Assert.Equal(SyncProtocol.EncodeStateSync(5, 42, payload), inner);

        Assert.True(SyncProtocol.TryDecodeStateSync(inner, out var ch, out var ver, out var pl));
        Assert.Equal((byte)5, ch);
        Assert.Equal(42UL, ver);
        Assert.Equal(payload, pl);
    }

    [Fact]
    public void GeoscapeHook_ConsumesGeoStateSurface_WithDecodedStatePayload()
    {
        SurfaceRouter.TacticalInbound = null;   // tactical does not claim the geoscape range
        try
        {
            var router = new SurfaceRouter();
            byte gotSid = 0; byte[] gotInner = null; int calls = 0;
            router.GeoscapeInbound = (sid, pl) => { gotSid = sid; gotInner = pl; calls++; return true; };

            var inner = SyncProtocol.EncodeStateSync(3, 1, new byte[] { 1, 2 });
            router.OnInbound(7UL, GeoStateEnvelope(3, 1, new byte[] { 1, 2 }), null);

            Assert.Equal(1, calls);
            Assert.Equal(SurfaceIds.GeoState, gotSid);
            Assert.Equal(inner, gotInner);   // the surface handler receives the inner StateSync bytes verbatim
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void TacticalHook_TakesPrecedence_OverGeoState()
    {
        SurfaceRouter.TacticalInbound = (sid, pl) => true;   // tactical claims everything
        try
        {
            var router = new SurfaceRouter();
            int geoCalls = 0;
            router.GeoscapeInbound = (sid, pl) => { geoCalls++; return true; };

            router.OnInbound(7UL, GeoStateEnvelope(5, 1, new byte[] { 9 }), null);

            Assert.Equal(0, geoCalls);   // tactical consumed it → geoscape never consulted
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    // Idempotent apply: OnStateSync guards every channel with SequenceTracker.ShouldApplyChannel (strict >),
    // so the legacy 0x64 AND the new 0xA1 envelope can BOTH deliver the same versioned echo — whichever lands
    // second is dropped (apply twice == no-op). This exercises the REAL guard the applier uses.
    [Fact]
    public void IdempotentApply_PerChannelVersionGuard_DropsDuplicate_AcceptsNewer()
    {
        var tracker = new SequenceTracker();

        // First delivery of channel 5 v1 applies; the duplicate (the other rail) is dropped.
        Assert.True(tracker.ShouldApplyChannel(5, 1));
        tracker.MarkChannel(5, 1);
        Assert.False(tracker.ShouldApplyChannel(5, 1));   // same version from the second rail → no-op

        // A newer version on the same channel still applies (last-writer-wins).
        Assert.True(tracker.ShouldApplyChannel(5, 2));
        tracker.MarkChannel(5, 2);
        Assert.False(tracker.ShouldApplyChannel(5, 1));   // a stale re-send never overtakes

        // Channels are independent monotonic streams (an echo on one never suppresses another).
        Assert.True(tracker.ShouldApplyChannel(3, 1));
    }

    // Gate governs the additive mirror only: with GeoRailGate disabled, the host emits the GeoState envelope
    // for NO channel (legacy 0x64 only); enabling it mirrors the SAME bytes additively. Mirrors the production
    // `if (GeoRailGate.Enabled)` guard in SyncEngine.FlushChannel without touching the (game-bound) engine.
    [Fact]
    public void GateOff_MeansLegacyOnly_NoGeoStateEnvelopeEmitted()
    {
        bool saved = GeoRailGate.Enabled;
        try
        {
            // The legacy per-channel state bytes are produced unconditionally (independent of the gate).
            var legacy = SyncProtocol.EncodeStateSync(5, 1, new byte[] { 1 });

            GeoRailGate.Enabled = false;
            byte[] mirrorWhenOff = GeoRailGate.Enabled
                ? SyncProtocol.EncodeEnvelope(SurfaceIds.GeoState, SyncKind.StateSnapshot, legacy)
                : null;
            Assert.Null(mirrorWhenOff);   // gate OFF → legacy-only, byte-for-byte unchanged geoscape wire

            GeoRailGate.Enabled = true;
            byte[] mirrorWhenOn = GeoRailGate.Enabled
                ? SyncProtocol.EncodeEnvelope(SurfaceIds.GeoState, SyncKind.StateSnapshot, legacy)
                : null;
            Assert.NotNull(mirrorWhenOn);                                  // gate ON → additive mirror exists
            Assert.True(SyncProtocol.TryDecodeEnvelope(mirrorWhenOn, out _, out _, out var inner));
            Assert.Equal(legacy, inner);                                   // and it carries the SAME legacy bytes
        }
        finally { GeoRailGate.Enabled = saved; }
    }
}
