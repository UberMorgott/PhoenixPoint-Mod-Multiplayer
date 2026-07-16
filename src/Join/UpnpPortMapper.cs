using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplayer.Net
{
    /// <summary>The router-opened public endpoint (external WAN IP + the forwarded port).</summary>
    public sealed class MappedEndpoint
    {
        public IPAddress ExternalIp { get; }
        public int Port { get; }
        public MappedEndpoint(IPAddress ip, int port) { ExternalIp = ip; Port = port; }
        public IPEndPoint ToEndPoint() => new IPEndPoint(ExternalIp, Port);
    }

    /// <summary>
    /// Hand-rolled UPnP IGD port mapper (pure BCL — SSDP discovery + SOAP over HTTP). Opens TCP+UDP
    /// 14242 on the host's router so a Steam-free (GOG/Epic) host is directly reachable across the
    /// internet, and hands back the router's real WAN IP for the invite code. Everything is
    /// best-effort: UPnP being off/unsupported is the NORMAL case, so every failure is swallowed to
    /// null (one info log line on success only — never crash, never log-spam).
    ///
    /// Host-side singleton (one host at a time). <see cref="TryMap"/> runs off the main thread;
    /// <see cref="Unmap"/> is the teardown chokepoint (wired into NetworkEngine.RunSteamLobbyCleanup).
    /// </summary>
    public static class UpnpPortMapper
    {
        public const int MappedPort = 14242;
        private const string Description = "PhoenixPoint Coop";
        private const int LeaseSeconds = 7200;
        private const int SsdpTimeoutMs = 4000;
        private const int HttpTimeoutMs = 4000;

        // ST set: IGD device roots + WAN service types. Some routers (e.g. KeeneticOS) answer only a
        // subset, so ask for several — any hit yields a LOCATION whose XML lists the WAN service.
        private static readonly string[] SearchTargets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        };

        private static readonly object _lock = new object();
        private static string _controlUrl;
        private static string _serviceType;
        private static string _localIp;
        private static Timer _refreshTimer;

        /// <summary>The current mapping (null until <see cref="TryMap"/> succeeds; null on a client).</summary>
        public static MappedEndpoint Current { get; private set; }

        /// <summary>Discover + map (TCP+UDP) off the main thread. Returns the mapped endpoint or null.</summary>
        public static async Task<MappedEndpoint> TryMap()
        {
            try { return await Task.Run(() => TryMapBlocking()); }
            catch { return null; }
        }

        private static MappedEndpoint TryMapBlocking()
        {
            try
            {
                var (location, localIp) = Discover();
                if (location == null || localIp == null) return null;

                var deviceXml = HttpGet(location);
                if (deviceXml == null) return null;

                var svc = ParseControlUrl(deviceXml, location);
                if (svc == null) return null;
                var serviceType = svc.Value.serviceType;
                var controlUrl = svc.Value.controlUrl;

                var extIpStr = ParseExternalIp(SoapPost(controlUrl, serviceType, "GetExternalIPAddress", ""));
                if (extIpStr == null || !IPAddress.TryParse(extIpStr, out var externalIp)) return null;

                // UDP is the STUN hole-punch path (must succeed); TCP is the Direct fallback (best-effort).
                if (!AddMapping(controlUrl, serviceType, "UDP", localIp)) return null;
                AddMapping(controlUrl, serviceType, "TCP", localIp);

                lock (_lock)
                {
                    _controlUrl = controlUrl;
                    _serviceType = serviceType;
                    _localIp = localIp;
                    Current = new MappedEndpoint(externalIp, MappedPort);
                    // Re-add the mapping before the lease expires (routers drop it otherwise).
                    _refreshTimer?.Dispose();
                    var half = TimeSpan.FromSeconds(LeaseSeconds / 2);
                    _refreshTimer = new Timer(_ => RefreshLease(), null, half, half);
                }
                LogInfo($"[Multiplayer] UPnP: mapped {externalIp}:{MappedPort} (TCP+UDP) via {serviceType}.");
                return Current;
            }
            catch { return null; }
        }

        /// <summary>Remove the mapping + stop the lease timer. Safe/no-op when nothing was mapped (client).</summary>
        public static void Unmap()
        {
            string url, type;
            lock (_lock)
            {
                _refreshTimer?.Dispose();
                _refreshTimer = null;
                url = _controlUrl; type = _serviceType;
                _controlUrl = null; _serviceType = null; _localIp = null;
                Current = null;
            }
            if (url == null) return; // nothing mapped
            // Fire-and-forget so a teardown on the main thread never blocks on a SOAP round-trip.
            Task.Run(() =>
            {
                try { DeleteMapping(url, type, "UDP"); DeleteMapping(url, type, "TCP"); } catch { }
            });
        }

        private static void RefreshLease()
        {
            string url, type, ip;
            lock (_lock) { url = _controlUrl; type = _serviceType; ip = _localIp; }
            if (url == null) return;
            try { AddMapping(url, type, "UDP", ip); AddMapping(url, type, "TCP", ip); } catch { }
        }

        // ─── SSDP discovery ───────────────────────────────────────────────

        private static (string location, string localIp) Discover()
        {
            // Root cause of the earlier "0 responders": a single Any-bound UdpClient lets the OS pick
            // the multicast egress interface, which on a multi-homed host (WSL/Hyper-V/ZeroTier NICs)
            // is often NOT the LAN adapter — so the M-SEARCH never reaches the router. Fix: send from
            // EVERY private IPv4 interface (socket bound to it + multicast egress pinned to it) and
            // receive across all sockets at once; whichever interface reaches an IGD answers, and its
            // bound IP is exactly the AddPortMapping "internal client".
            var localIps = LocalIPv4Candidates();
            if (localIps.Count == 0) return (null, null);

            var sockets = new List<Socket>();
            var ipOf = new Dictionary<Socket, string>();
            try
            {
                foreach (var ip in localIps)
                {
                    var s = TryMakeSearchSocket(ip);
                    if (s != null) { sockets.Add(s); ipOf[s] = ip; }
                }
                if (sockets.Count == 0) return (null, null);

                var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                SendMSearchBurst(sockets, multicast);

                var start = DateTime.UtcNow;
                var deadline = start.AddMilliseconds(SsdpTimeoutMs);
                var resent = false;
                var buf = new byte[4096];
                while (DateTime.UtcNow < deadline)
                {
                    // Resend once mid-window to cover UDP packet loss (M-SEARCH is unreliable by spec).
                    if (!resent && (DateTime.UtcNow - start).TotalMilliseconds > 1500)
                    {
                        SendMSearchBurst(sockets, multicast);
                        resent = true;
                    }

                    var readable = new List<Socket>(sockets);
                    var leftMs = (deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (leftMs <= 0) break;
                    var micros = (int)Math.Min(500, Math.Max(1, leftMs)) * 1000;
                    try { Socket.Select(readable, null, null, micros); } catch { break; }
                    foreach (var s in readable)
                    {
                        try
                        {
                            var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                            var n = s.ReceiveFrom(buf, ref from);
                            var loc = ParseLocationFromSsdp(Encoding.ASCII.GetString(buf, 0, n));
                            if (loc != null) return (loc, ipOf[s]);
                        }
                        catch { }
                    }
                }
                return (null, null);
            }
            finally { foreach (var s in sockets) { try { s.Close(); } catch { } } }
        }

        // One M-SEARCH per ST, sent out every interface socket.
        private static void SendMSearchBurst(List<Socket> sockets, IPEndPoint multicast)
        {
            foreach (var st in SearchTargets)
            {
                var msg = "M-SEARCH * HTTP/1.1\r\n" +
                          "HOST: 239.255.255.250:1900\r\n" +
                          "MAN: \"ssdp:discover\"\r\n" +
                          "MX: 2\r\n" +
                          "ST: " + st + "\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(msg);
                foreach (var s in sockets)
                    try { s.SendTo(bytes, multicast); } catch { }
            }
        }

        // A UDP socket bound to one interface, with multicast egress pinned to that same interface.
        private static Socket TryMakeSearchSocket(string localIp)
        {
            try
            {
                var addr = IPAddress.Parse(localIp);
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(new IPEndPoint(addr, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                return s;
            }
            catch { return null; }
        }

        // Up, non-loopback IPv4 interface addresses to send M-SEARCH from.
        private static List<string> LocalIPv4Candidates()
        {
            var ips = new List<IPAddress>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            ips.Add(ua.Address);
                }
            }
            catch { }
            return FilterCandidateIPv4(ips);
        }

        // ─── HTTP / SOAP ──────────────────────────────────────────────────

        private static string HttpGet(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = HttpTimeoutMs;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        // POST a SOAP action. Returns the 200 response body, or null on any error (incl. a SOAP fault,
        // which the router returns as HTTP 500). Callers treat null as "action failed".
        private static string SoapPost(string controlUrl, string serviceType, string action, string argsXml)
        {
            try
            {
                var soap =
                    "<?xml version=\"1.0\"?>" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                    "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    "<s:Body><u:" + action + " xmlns:u=\"" + serviceType + "\">" + argsXml +
                    "</u:" + action + "></s:Body></s:Envelope>";
                var data = Encoding.UTF8.GetBytes(soap);

                var req = (HttpWebRequest)WebRequest.Create(controlUrl);
                req.Method = "POST";
                req.ContentType = "text/xml; charset=\"utf-8\"";
                req.Headers.Add("SOAPACTION", "\"" + serviceType + "#" + action + "\"");
                req.Timeout = HttpTimeoutMs;
                req.ContentLength = data.Length;
                using (var rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        private static bool AddMapping(string controlUrl, string serviceType, string proto, string internalIp)
        {
            var args =
                "<NewRemoteHost></NewRemoteHost>" +
                "<NewExternalPort>" + MappedPort + "</NewExternalPort>" +
                "<NewProtocol>" + proto + "</NewProtocol>" +
                "<NewInternalPort>" + MappedPort + "</NewInternalPort>" +
                "<NewInternalClient>" + internalIp + "</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>" + Description + "</NewPortMappingDescription>" +
                "<NewLeaseDuration>" + LeaseSeconds + "</NewLeaseDuration>";
            return SoapPost(controlUrl, serviceType, "AddPortMapping", args) != null;
        }

        private static void DeleteMapping(string controlUrl, string serviceType, string proto)
        {
            var args =
                "<NewRemoteHost></NewRemoteHost>" +
                "<NewExternalPort>" + MappedPort + "</NewExternalPort>" +
                "<NewProtocol>" + proto + "</NewProtocol>";
            SoapPost(controlUrl, serviceType, "DeletePortMapping", args);
        }

        // ─── Pure parse helpers (unit-tested with canned strings) ─────────

        /// <summary>Extract the LOCATION header URL from an SSDP M-SEARCH response, or null.</summary>
        public static string ParseLocationFromSsdp(string ssdpResponse)
        {
            if (string.IsNullOrEmpty(ssdpResponse)) return null;
            var m = Regex.Match(ssdpResponse, @"(?im)^\s*LOCATION\s*:\s*(\S+)\s*$");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>Find the WANIP/WANPPPConnection service's absolute control URL in the device XML.</summary>
        public static (string serviceType, string controlUrl)? ParseControlUrl(string deviceXml, string locationUrl)
        {
            if (string.IsNullOrEmpty(deviceXml)) return null;
            foreach (Match svc in Regex.Matches(deviceXml, @"<service>(.*?)</service>", RegexOptions.Singleline))
            {
                var block = svc.Groups[1].Value;
                var typeM = Regex.Match(block, @"<serviceType>\s*(.*?)\s*</serviceType>", RegexOptions.Singleline);
                if (!typeM.Success) continue;
                var type = typeM.Groups[1].Value.Trim();
                if (type.IndexOf("WANIPConnection", StringComparison.OrdinalIgnoreCase) < 0
                    && type.IndexOf("WANPPPConnection", StringComparison.OrdinalIgnoreCase) < 0) continue;

                var ctrlM = Regex.Match(block, @"<controlURL>\s*(.*?)\s*</controlURL>", RegexOptions.Singleline);
                if (!ctrlM.Success) continue;
                var abs = ResolveUrl(locationUrl, ctrlM.Groups[1].Value.Trim());
                if (abs != null) return (type, abs);
            }
            return null;
        }

        /// <summary>Extract NewExternalIPAddress from a GetExternalIPAddress SOAP response, or null.</summary>
        public static string ParseExternalIp(string soapResponse)
        {
            if (string.IsNullOrEmpty(soapResponse)) return null;
            var m = Regex.Match(soapResponse, @"<NewExternalIPAddress>\s*(.*?)\s*</NewExternalIPAddress>",
                                RegexOptions.Singleline);
            var ip = m.Success ? m.Groups[1].Value.Trim() : null;
            return string.IsNullOrEmpty(ip) ? null : ip;
        }

        /// <summary>Keep routable IPv4 to M-SEARCH from: drop loopback + APIPA link-local, dedup, preserve order.</summary>
        public static List<string> FilterCandidateIPv4(IEnumerable<IPAddress> addresses)
        {
            var result = new List<string>();
            if (addresses == null) return result;
            var seen = new HashSet<string>();
            foreach (var a in addresses)
            {
                if (a == null || a.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = a.GetAddressBytes();
                if (b[0] == 127) continue;                 // loopback
                if (b[0] == 169 && b[1] == 254) continue;  // APIPA link-local
                var s = a.ToString();
                if (seen.Add(s)) result.Add(s);
            }
            return result;
        }

        /// <summary>Resolve a (possibly relative) controlURL against the device LOCATION url.</summary>
        public static string ResolveUrl(string baseUrl, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            if (relative.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return relative;
            try { return new Uri(new Uri(baseUrl), relative).ToString(); }
            catch { return null; }
        }

        // Core is Unity-free at compile time; reach UnityEngine.Debug.Log via reflection so the one
        // success line lands in Player.log in-game and is a harmless no-op under tests (cached).
        private static System.Reflection.MethodInfo _unityLog;
        private static bool _unityLogResolved;
        private static void LogInfo(string message)
        {
            try
            {
                if (!_unityLogResolved)
                {
                    _unityLogResolved = true;
                    var debugType = Type.GetType("UnityEngine.Debug, UnityEngine.CoreModule")
                                    ?? Type.GetType("UnityEngine.Debug, UnityEngine");
                    if (debugType != null)
                        _unityLog = debugType.GetMethod("Log", new[] { typeof(object) });
                }
                if (_unityLog != null) _unityLog.Invoke(null, new object[] { message });
            }
            catch { /* logging must never break hosting */ }
        }
    }
}
