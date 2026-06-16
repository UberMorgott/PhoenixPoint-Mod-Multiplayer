using Multipleer.Network.Sync;
using Xunit;

/// <summary>
/// Host-side inbound-request dedup. The reliable transport sends every reliable packet TWICE, so each
/// client ActionRequest arrives twice; the authority must apply each (peerId, nonce) exactly once.
/// </summary>
public class RequestDedupTests
{
    [Fact]
    public void FirstSighting_IsNotDuplicate_RepeatIs()
    {
        var d = new RequestDedup();
        Assert.False(d.IsDuplicate(peerId: 7, nonce: 1)); // first
        Assert.True(d.IsDuplicate(peerId: 7, nonce: 1));  // reliable-transport duplicate → dropped
        Assert.True(d.IsDuplicate(peerId: 7, nonce: 1));  // any further repeat too
    }

    [Fact]
    public void DistinctNoncesSamePeer_AreIndependent()
    {
        var d = new RequestDedup();
        Assert.False(d.IsDuplicate(7, 1));
        Assert.False(d.IsDuplicate(7, 2));
        Assert.True(d.IsDuplicate(7, 1));
        Assert.True(d.IsDuplicate(7, 2));
    }

    [Fact]
    public void SameNonceDifferentPeers_DoNotCollide()
    {
        // Two clients can independently use nonce=1; they must not be conflated.
        var d = new RequestDedup();
        Assert.False(d.IsDuplicate(peerId: 1, nonce: 1));
        Assert.False(d.IsDuplicate(peerId: 2, nonce: 1)); // different peer → new, not a duplicate
        Assert.True(d.IsDuplicate(1, 1));
        Assert.True(d.IsDuplicate(2, 1));
    }

    [Fact]
    public void HighPeerIdUpperBits_DoNotCollideWithNonce()
    {
        // Key packs peerId<<32 ^ nonce; ensure a large peerId doesn't alias a different (peer, nonce).
        var d = new RequestDedup();
        ulong bigPeer = 0xFFFFFFFFUL; // 32-bit-max peer
        Assert.False(d.IsDuplicate(bigPeer, 0));
        Assert.False(d.IsDuplicate(0, 0));        // peer 0 nonce 0 must be distinct
        Assert.False(d.IsDuplicate(bigPeer, 1));
        Assert.True(d.IsDuplicate(bigPeer, 0));
    }

    [Fact]
    public void EvictsOldest_WhenOverCapacity()
    {
        // Tiny capacity: after exceeding it, the oldest key is forgotten and re-arrives as "new".
        var d = new RequestDedup(capacity: 2);
        Assert.False(d.IsDuplicate(1, 1)); // [ (1,1) ]
        Assert.False(d.IsDuplicate(1, 2)); // [ (1,1),(1,2) ]
        Assert.False(d.IsDuplicate(1, 3)); // overflow → evicts (1,1); [ (1,2),(1,3) ]
        Assert.False(d.IsDuplicate(1, 1)); // (1,1) was evicted → treated as new again (bounded, not perfect)
        Assert.True(d.IsDuplicate(1, 3));  // (1,3) still tracked → duplicate
    }
}
