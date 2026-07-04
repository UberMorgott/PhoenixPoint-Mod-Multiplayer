using Multiplayer.Network.Sync;
using Xunit;

// Rail-unification (phase 1 complete): the per-channel state echo rides the unified 0x67 envelope under the
// GeoState (0xA1) surface as the SOLE rail — the legacy 0x64 / StateSync send is retired. These mirror the
// slice-1 wallet rail tests (SurfaceRouterGeoscapeTests) for this surface: envelope encode/decode wire parity,
// router-consume routing, tactical precedence, idempotent per-channel apply, and the sole-rail byte parity.
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

    // Rail-unify phase 1: the legacy 0x64 StateSync send is RETIRED — the per-channel state echo now rides the
    // unified 0x67 envelope under the GeoState (0xA1) surface as the SOLE rail, emitted UNCONDITIONALLY (the
    // former `if (GeoRailGate.Enabled)` send-guard was removed from SyncEngine.FlushChannel, and the gate itself
    // has since been deleted — rail unification is complete). This pins that the sole rail carries the SAME
    // per-channel StateSync bytes the retired
    // 0x64 used to, so the wire payload is byte-for-byte preserved regardless of any gate state.
    [Fact]
    public void GeoStateEnvelope_IsSoleRail_CarriesLegacyStateSyncBytesVerbatim()
    {
        var legacy = SyncProtocol.EncodeStateSync(5, 1, new byte[] { 1 });   // the exact bytes the retired 0x64 carried
        var mirror = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoState, SyncKind.StateSnapshot, legacy);

        Assert.True(SyncProtocol.TryDecodeEnvelope(mirror, out var sid, out var kind, out var inner));
        Assert.Equal(SurfaceIds.GeoState, sid);                             // sole rail = GeoState 0xA1 surface
        Assert.Equal(SyncKind.StateSnapshot, kind);
        Assert.Equal(legacy, inner);                                        // carries the retired-0x64 bytes verbatim
    }
}
