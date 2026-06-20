using System.Collections.Generic;
using System.Linq;
using Multipleer.Network;
using Xunit;

// Fix #2 coverage: on a crash/timeout peer drop the host re-broadcasts only PEER_LIST (no
// ClientLeave packet → no client-side RemoveClient), so HandlePeerList must PRUNE _clients entries
// absent from the freshly received roster. Without pruning a remaining client keeps the dropped
// peer in _clients forever → ClientCount / GetConnectedClients over-count (status-bar drift).
//
// SessionManager(NetworkEngine) is NOT unit-isolatable headless (NetworkEngine drags
// SaveTransferCoordinator / TimeSyncManager / SyncEngine + transport — all Unity-coupled, none
// linked into this test project — mirroring the ClientUnreadyToggleTests note). So the pure prune
// DECISION is extracted into PeerListPrune.PruneKeys (Unity-free set arithmetic over the
// dictionary keys) and tested directly here, mirroring the project's gate-helper pattern
// (NullFactionEnterPlayGate / TacticalDeployReadinessGate). The HandlePeerList wire-through that
// calls it is verified by the mod build + careful mirroring of RemoveClient.
public class PeerListPruneTests
{
    private const ulong HostId = 1000;

    [Fact]
    public void Prune_RemovesPeersAbsentFromNewRoster()
    {
        var current = new ulong[] { 1, 2, 3 };          // _clients has A, B, C
        var newClientPeers = new ulong[] { 1 };          // new roster keeps only A (B, C dropped)

        var toRemove = PeerListPrune.PruneKeys(current, newClientPeers).ToList();

        Assert.DoesNotContain(1UL, toRemove);
        Assert.Contains(2UL, toRemove);
        Assert.Contains(3UL, toRemove);
        Assert.Equal(2, toRemove.Count);
    }

    [Fact]
    public void Prune_EmptyClientRoster_RemovesAll()
    {
        var current = new ulong[] { 1, 2 };
        var toRemove = PeerListPrune.PruneKeys(current, new ulong[0]).ToList();
        Assert.Equal(new ulong[] { 1, 2 }.OrderBy(x => x), toRemove.OrderBy(x => x));
    }

    [Fact]
    public void Prune_NoDrop_RemovesNothing()
    {
        var current = new ulong[] { 1, 2 };
        var toRemove = PeerListPrune.PruneKeys(current, new ulong[] { 1, 2 }).ToList();
        Assert.Empty(toRemove);
    }

    [Fact]
    public void Prune_HostKeyNeverPresentInClients_NotConsideredForKeep()
    {
        // The host is never a _clients key (HandlePeerList skips IsHost rows before inserting), so the
        // keep-set passed in is the NON-HOST peer ids only. A stray host id sitting in _clients (it
        // shouldn't) would be pruned — proving the prune keys exactly the surviving non-host roster.
        var current = new ulong[] { 1, HostId };
        var newClientPeers = new ulong[] { 1 };          // host excluded from keep-set, as in production
        var toRemove = PeerListPrune.PruneKeys(current, newClientPeers).ToList();
        Assert.Contains(HostId, toRemove);
        Assert.DoesNotContain(1UL, toRemove);
    }
}
