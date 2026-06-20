using System.Net;
using System.Linq;

namespace Multipleer.Util
{
    public enum JoinKind { Invalid, DirectIp, DirectHost, StunCode, SteamId }

    public readonly struct JoinTarget
    {
        public JoinKind Kind { get; }
        public string Ip { get; }
        public int Port { get; }
        public ulong SteamId { get; }

        private JoinTarget(JoinKind kind, string ip, int port, ulong steamId)
        { Kind = kind; Ip = ip; Port = port; SteamId = steamId; }

        public static JoinTarget Invalid() => new JoinTarget(JoinKind.Invalid, null, 0, 0);
        public static JoinTarget Direct(string ip, int port) => new JoinTarget(JoinKind.DirectIp, ip, port, 0);
        // DirectHost: a DNS hostname[:port]. The host string is carried in Ip; DirectTransport's
        // BeginConnect(host, port) resolves it at connect time (the parser never does networking).
        public static JoinTarget DirectHost(string host, int port) => new JoinTarget(JoinKind.DirectHost, host, port, 0);
        public static JoinTarget Stun(IPEndPoint ep) => new JoinTarget(JoinKind.StunCode, ep.Address.ToString(), ep.Port, 0);
        public static JoinTarget Steam(ulong id) => new JoinTarget(JoinKind.SteamId, null, 0, id);
    }

    /// <summary>
    /// Classifies a pasted join string in a fixed precedence (R6): IPv4[:port] first
    /// (DirectIP), else a Crockford short code (STUN via ConnectCode.Decode), else a bare
    /// 64-bit number (Steam), else Invalid. Pure — does no networking.
    /// </summary>
    public static class SmartJoinParser
    {
        public const int DefaultDirectPort = 14242;

        public static JoinTarget Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return JoinTarget.Invalid();
            var s = input.Trim();

            // 1) DirectIP: ip[:port]
            var hostPart = s;
            var port = DefaultDirectPort;
            var colon = s.LastIndexOf(':');
            if (colon > 0 && colon < s.Length - 1 &&
                int.TryParse(s.Substring(colon + 1), out var p) && p > 0 && p <= 65535)
            {
                hostPart = s.Substring(0, colon);
                port = p;
            }
            // "localhost" (case-insensitive) is a DirectIP alias for 127.0.0.1 — lets two
            // instances on one PC connect by typing "localhost[:port]" (IPAddress.TryParse
            // does not resolve hostnames). Keep port handling identical to a literal IP.
            if (string.Equals(hostPart, "localhost", System.StringComparison.OrdinalIgnoreCase))
                return JoinTarget.Direct("127.0.0.1", port);

            if (IPAddress.TryParse(hostPart, out var ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return JoinTarget.Direct(ip.ToString(), port);

            // 2) STUN short code (Crockford base32, optionally dash-grouped).
            var stripped = s.Replace("-", "").Replace(" ", "");
            if (stripped.Length == 10 && stripped.All(IsCrockford))
            {
                var ep = ConnectCode.Decode(s);
                if (ep != null) return JoinTarget.Stun(ep);
            }

            // 3) Bare 64-bit SteamID.
            if (ulong.TryParse(s, out var steamId) && s.Length >= 15)
                return JoinTarget.Steam(steamId);

            // 4) DNS hostname[:port] (e.g. "myhost.ddns.net", "host.com:9000"). Checked LAST so a
            // Crockford code or a bare SteamID is never misread as a host. DirectTransport resolves
            // the name via BeginConnect(host, port); only the parser previously rejected it.
            if (IsHostname(hostPart))
                return JoinTarget.DirectHost(hostPart, port);

            return JoinTarget.Invalid();
        }

        // A pasteable DNS hostname: dotted, hostname char-class only, with an alphabetic top-level
        // label (a real TLD is never all-numeric — this is what keeps "999.999.999.999" Invalid
        // rather than a bogus host, and lets dotless codes/SteamIDs fall through untouched).
        private static bool IsHostname(string h)
        {
            if (string.IsNullOrEmpty(h) || h.Length > 253) return false;
            if (h[0] == '.' || h[0] == '-' || h[h.Length - 1] == '.' || h[h.Length - 1] == '-')
                return false;

            var labels = h.Split('.');
            if (labels.Length < 2) return false; // require at least one dot (one label separator)

            foreach (var label in labels)
            {
                if (label.Length == 0) return false; // no empty label ("..", leading/trailing dot)
                foreach (var c in label)
                {
                    var u = char.ToUpperInvariant(c);
                    var ok = (u >= 'A' && u <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                    if (!ok) return false;
                }
            }

            // The rightmost label (TLD) must contain at least one ASCII letter.
            var tld = labels[labels.Length - 1];
            foreach (var c in tld)
            {
                var u = char.ToUpperInvariant(c);
                if (u >= 'A' && u <= 'Z') return true;
            }
            return false;
        }

        private static bool IsCrockford(char c)
        {
            c = char.ToUpperInvariant(c);
            return "0123456789ABCDEFGHJKMNPQRSTVWXYZ".IndexOf(c) >= 0;
        }
    }
}
