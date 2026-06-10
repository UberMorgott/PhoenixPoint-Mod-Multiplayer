using System;
using System.Net;
using System.Net.Sockets;

namespace Multipleer.Util
{
    /// <summary>
    /// Resolves the host's advertisable LAN IPv4 by connecting a UDP socket to a public address
    /// and reading its LocalEndPoint — this returns the actual outbound-interface address and
    /// avoids the VPN/virtual/loopback adapters that Dns.GetHostAddresses enumerates blindly.
    /// No packet is actually sent: UDP "connect" only fixes the socket's default peer, which is
    /// enough for the OS to bind a source address.
    /// </summary>
    public static class LanIpResolver
    {
        public static bool TryResolveLocalIPv4(out IPAddress ip)
        {
            ip = null;
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork,
                    SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect("8.8.8.8", 65530);
                    if (socket.LocalEndPoint is IPEndPoint ep &&
                        ep.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ep.Address))
                    {
                        ip = ep.Address;
                        return true;
                    }
                }
            }
            catch
            {
                // No route / sandboxed network → caller falls back to a placeholder rail value.
            }
            return false;
        }
    }
}
