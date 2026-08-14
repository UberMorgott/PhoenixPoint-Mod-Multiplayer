using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Multiplayer.Util
{
    /// <summary>
    /// EVERY IPv4 THIS MACHINE ANSWERS ON, each paired with the interface that carries it.
    ///
    /// This used to be a single <c>TryResolveLocalIPv4</c> that opened a UDP socket to 8.8.8.8 and read
    /// back the source address the OS picked. That answers exactly one question — "which adapter would a
    /// packet to the internet leave by" — and it is the WRONG question for an invite card: the whole
    /// point of Radmin / ZeroTier / Hamachi is that the peer is reachable on an adapter that is NOT the
    /// default route, and that adapter's address was the one the old resolver could never return.
    ///
    /// So enumerate instead, and keep the NAME: "Ethernet" and "ZeroTier One" are the two lines a player
    /// has to tell apart, and only the interface knows which is which. Kept: interfaces that are
    /// <see cref="OperationalStatus.Up"/>, their IPv4 unicast addresses, minus loopback (127/8) and APIPA
    /// (169.254/16) — an address nobody outside this box can dial is worse than no line at all.
    /// </summary>
    public static class LanIpResolver
    {
        /// <summary>One dialable local address and the adapter it belongs to.</summary>
        public struct LocalAddress
        {
            public string Interface;
            public IPAddress Ip;
            /// <summary>The IPv4 subnet mask, or null when the adapter reports none. Carried because the
            /// only correct broadcast for this address is the SUBNET-DIRECTED one (LanBeacon), and the mask
            /// is the one thing that says where this subnet ends.</summary>
            public IPAddress Mask;
        }

        // CACHED, WITH A TTL — the lobby asks every frame and GetAllNetworkInterfaces is a syscall sweep,
        // but a cable plugged in (or a VPN brought up) after the lobby opened must not stay invisible for
        // the rest of the session. Ten seconds is the cheapest trigger that satisfies both: no event to
        // subscribe to and unsubscribe from, no polling thread, and the worst case is a line appearing ten
        // seconds late on a screen the player is reading anyway.
        // ponytail: TTL, not System.Net.NetworkInformation.NetworkChange. Swap in the event if the sweep
        // ever shows up in a profile — it is one subscription, but its Mono behaviour would need proving.
        private const double CacheSeconds = 10;
        private static List<LocalAddress> _cache;
        private static DateTime _cachedAt;

        /// <summary>Every up, non-loopback, non-APIPA IPv4 with its interface name. Never null; the list
        /// instance is stable between re-enumerations, so callers can use a reference compare to decide
        /// whether anything can possibly have changed.</summary>
        public static List<LocalAddress> LocalIPv4Addresses()
        {
            if (_cache != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < CacheSeconds)
                return _cache;

            var found = new List<LocalAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a == null || a.AddressFamily != AddressFamily.InterNetwork) continue;
                        var b = a.GetAddressBytes();
                        if (b[0] == 127) continue;                 // loopback — reachable by nobody else
                        if (b[0] == 169 && b[1] == 254) continue;  // APIPA — a failed DHCP, not an address
                        IPAddress mask = null;
                        try { mask = ua.IPv4Mask; } catch { }   // not every platform/adapter answers this
                        found.Add(new LocalAddress { Interface = ni.Name, Ip = a, Mask = mask });
                    }
                }
            }
            catch
            {
                // Sandboxed / permission-denied enumeration → whatever was collected before the throw.
                // The caller simply shows fewer lines; it must never take the lobby down with it.
            }

            _cache = found;
            _cachedAt = DateTime.UtcNow;
            return _cache;
        }
    }
}
