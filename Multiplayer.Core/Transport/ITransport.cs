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

        // Drop ONE peer (heartbeat timeout / graceful leave): close its session/socket AND remove it
        // from the transport's peer set so Send/Broadcast stop writing to the dead id. Returns true if
        // the peer was known — an OnPeerDisconnected raise follows (inline, or on the next Update for
        // transports that marshal peer events); false for an unknown id (no event will fire — the
        // caller must clean up itself).
        bool DisconnectPeer(ulong peerId);

        void Update();
    }
}
