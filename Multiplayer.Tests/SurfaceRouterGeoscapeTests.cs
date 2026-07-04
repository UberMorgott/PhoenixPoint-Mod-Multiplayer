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
            router.GeoscapeInbound = (sid, pl) => { gotSid = sid; gotPayload = pl; calls++; return true; };

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
        SurfaceRouter.TacticalInbound = (sid, pl) => true;   // tactical claims everything
        try
        {
            var router = new SurfaceRouter();
            int geoCalls = 0;
            router.GeoscapeInbound = (sid, pl) => { geoCalls++; return true; };

            router.OnInbound(7UL, Wallet(new byte[] { 9 }), null);

            Assert.Equal(0, geoCalls);   // tactical consumed it → geoscape never consulted
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void UnclaimedEnvelope_IsDropped_NeverThrows()
    {
        SurfaceRouter.TacticalInbound = (sid, pl) => false;   // tactical declines
        try
        {
            var router = new SurfaceRouter();
            router.GeoscapeInbound = (sid, pl) => false;       // geoscape declines too
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
}
