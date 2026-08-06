using System;

namespace Multiplayer.Transport
{
    public interface ITransport
    {
        TransportType TransportType { get; }
        ConnectionState State { get; }
        bool IsHost { get; }
        string LocalEndpoint { get; }

        // Public (NAT-mapped) IPv4 endpoint for the host; null on Direct/Steam or until STUN
        // discovery completes. Used to derive the rail's short connect code.
        System.Net.IPEndPoint PublicEndPoint { get; }

        event Action<ConnectionState> OnStateChanged;
        event Action<ulong, byte[]> OnPacketReceived;
        event Action<ulong, string> OnPeerConnected;
        event Action<ulong, string> OnPeerDisconnected;

        void Initialize();
        void Shutdown();

        void Host(int port = 0);
        void Connect(string address, int port);
        void Disconnect();

        void Send(ulong peerId, byte[] data, bool reliable = true);
        void Broadcast(byte[] data, bool reliable = true);

        // WILL A BROADCAST REACH THIS PEER? Every transport keeps a peer set that Broadcast iterates
        // (SteamTransport._connectedPeers, DirectTransport._clients, StunTransport._peers, and the
        // Composite's outward map on top of them), while Send/unicast does NOT consult it. Those two
        // reaches drifting apart is invisible from outside — a peer got its unicast accept and then
        // waited forever for the broadcast-only PEER_LIST, which is precisely the shape of the bug this
        // predicate exists to make visible (see SteamTransport.Update's registration comment). It answers
        // the OUTCOME, not the storage: "if I Broadcast right now, does this peer get the bytes".
        bool CanReach(ulong peerId);

        // Drop ONE peer (heartbeat timeout / graceful leave): close its session/socket AND remove it
        // from the transport's peer set so Send/Broadcast stop writing to the dead id. Returns true if
        // the peer was known — an OnPeerDisconnected raise follows (inline, or on the next Update for
        // transports that marshal peer events); false for an unknown id (no event will fire — the
        // caller must clean up itself).
        bool DisconnectPeer(ulong peerId);

        void Update();
    }
}
