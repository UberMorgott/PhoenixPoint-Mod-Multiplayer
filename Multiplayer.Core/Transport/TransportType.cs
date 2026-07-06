namespace Multiplayer.Transport
{
    public enum TransportType : byte
    {
        SteamP2P = 0,
        DirectIP = 1,
        StunUDP = 2
    }

    public enum ConnectionState : byte
    {
        Disconnected,
        Connecting,
        Connected,
        Failed
    }
}
