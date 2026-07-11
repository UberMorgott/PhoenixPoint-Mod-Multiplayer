using System;
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
        private const int SsdpTimeoutMs = 3000;
        private const int HttpTimeoutMs = 4000;

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
            using (var udp = new UdpClient())
            {
                udp.Client.ReceiveTimeout = SsdpTimeoutMs;
                var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                foreach (var st in new[]
                {
                    "urn:schemas-upnp-org:service:WANIPConnection:1",
                    "urn:schemas-upnp-org:service:WANPPPConnection:1"
                })
                {
                    var msg = "M-SEARCH * HTTP/1.1\r\n" +
                              "HOST: 239.255.255.250:1900\r\n" +
                              "MAN: \"ssdp:discover\"\r\n" +
                              "MX: 2\r\n" +
                              "ST: " + st + "\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(msg);
                    try { udp.Send(bytes, bytes.Length, multicast); } catch { }
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(SsdpTimeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var resp = udp.Receive(ref from);
                        var loc = ParseLocationFromSsdp(Encoding.ASCII.GetString(resp));
                        if (loc != null) return (loc, LocalIpToReach(from.Address));
                    }
                    catch (SocketException) { break; } // receive timeout — no responder
                }
                return (null, null);
            }
        }

        // The local NIC address that routes to the router — the AddPortMapping "internal client".
        private static string LocalIpToReach(IPAddress remote)
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(remote, 1900); // no packet sent for a UDP "connect" — just picks the route
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch { return null; }
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
