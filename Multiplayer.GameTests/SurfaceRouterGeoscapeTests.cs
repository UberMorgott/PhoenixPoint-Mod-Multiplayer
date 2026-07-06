using System;
using Multiplayer.Network.Sync;
using Xunit;

public class SurfaceRouterGeoscapeTests
{
    private static byte[] Wallet(byte[] inner)
        => SyncProtocol.EncodeEnvelope(SurfaceIds.GeoWallet, SyncKind.StateSnapshot, inner);

    [Fact]
    public void GeoscapeHook_ConsumesItsSurface_WithDecodedPayload()
    {
        SurfaceRouter.TacticalInbound = null;   // tactical does not claim the geoscape range
        try
        {
            var router = new SurfaceRouter();
            byte gotSid = 0; byte[] gotPayload = null; int calls = 0;
            router.GeoscapeInbound = (peer, sid, pl) => { gotSid = sid; gotPayload = pl; calls++; return true; };

            router.OnInbound(7UL, Wallet(new byte[] { 1, 2, 3 }), null);

            Assert.Equal(1, calls);
            Assert.Equal(SurfaceIds.GeoWallet, gotSid);
            Assert.Equal(new byte[] { 1, 2, 3 }, gotPayload);
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void TacticalHook_TakesPrecedence_GeoscapeNotConsultedWhenTacticalClaims()
    {
        SurfaceRouter.TacticalInbound = (peer, sid, pl) => true;   // tactical claims everything
        try
        {
            var router = new SurfaceRouter();
            int geoCalls = 0;
            router.GeoscapeInbound = (peer, sid, pl) => { geoCalls++; return true; };

            router.OnInbound(7UL, Wallet(new byte[] { 9 }), null);

            Assert.Equal(0, geoCalls);   // tactical consumed it → geoscape never consulted
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void TacticalHook_ReceivesSenderPeerId()
    {
        // The peer id feeds the host's per-peer intent dedup (client nonces are client-local monotonic,
        // so without the peer in the key two clients' intents collide) — it must survive the router.
        ulong gotPeer = 0;
        SurfaceRouter.TacticalInbound = (peer, sid, pl) => { gotPeer = peer; return true; };
        try
        {
            var router = new SurfaceRouter();
            router.OnInbound(42UL, Wallet(new byte[] { 1 }), null);
            Assert.Equal(42UL, gotPeer);
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void UnclaimedEnvelope_IsDropped_NeverThrows()
    {
        SurfaceRouter.TacticalInbound = (peer, sid, pl) => false;   // tactical declines
        try
        {
            var router = new SurfaceRouter();
            router.GeoscapeInbound = (peer, sid, pl) => false;       // geoscape declines too
            // No throw, no consumer → graceful drop.
            router.OnInbound(7UL, Wallet(new byte[] { 0 }), null);
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void GeoscapeHook_NullByDefault_GarbageEnvelopeDropped()
    {
        SurfaceRouter.TacticalInbound = null;
        try
        {
            var router = new SurfaceRouter();   // GeoscapeInbound left null (inert)
            router.OnInbound(7UL, new byte[] { 0x01 }, null);   // too short to decode → dropped
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void GeoscapeHook_ReceivesSenderPeerId()
    {
        // The action-INTENT surface (GeoIntent 0xA2, action-relay envelope cutover) resolves the actor + dedups
        // per-peer, so the sender id must reach the GEOSCAPE hook — not only the tactical one.
        SurfaceRouter.TacticalInbound = null;   // tactical declines the geoscape range
        try
        {
            var router = new SurfaceRouter();
            ulong gotPeer = 0;
            router.GeoscapeInbound = (peer, sid, pl) => { gotPeer = peer; return true; };
            router.OnInbound(99UL,
                SyncProtocol.EncodeEnvelope(SurfaceIds.GeoIntent, SyncKind.ActionRequest, new byte[] { 1 }), null);
            Assert.Equal(99UL, gotPeer);
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Theory]
    [InlineData(SurfaceIds.GeoIntent)]
    [InlineData(SurfaceIds.GeoOutcome)]
    [InlineData(SurfaceIds.GeoReject)]
    public void GeoActionSurfaces_RouteToGeoscapeHook_WhenTacticalDeclines(byte surfaceId)
    {
        // The three action-relay envelope surfaces (0xA2/0xA3/0xA4) fall through the tactical fast-path to the
        // geoscape hook (which dispatches them to OnActionRequest/OnActionApply/OnActionReject — the sole rail).
        SurfaceRouter.TacticalInbound = (peer, sid, pl) => false;   // tactical declines
        try
        {
            var router = new SurfaceRouter();
            byte gotSid = 0; int calls = 0;
            router.GeoscapeInbound = (peer, sid, pl) => { gotSid = sid; calls++; return true; };
            router.OnInbound(5UL, SyncProtocol.EncodeEnvelope(surfaceId, SyncKind.ActionApply, new byte[] { 7 }), null);
            Assert.Equal(1, calls);
            Assert.Equal(surfaceId, gotSid);
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }
}
