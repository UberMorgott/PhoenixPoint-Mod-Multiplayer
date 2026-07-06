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

        void Update();
    }
}
