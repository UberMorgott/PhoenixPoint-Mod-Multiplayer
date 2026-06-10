using System.Net;
using System.Linq;

namespace Multipleer.Util
{
    public enum JoinKind { Invalid, DirectIp, StunCode, SteamId }

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

            return JoinTarget.Invalid();
        }

        private static bool IsCrockford(char c)
        {
            c = char.ToUpperInvariant(c);
            return "0123456789ABCDEFGHJKMNPQRSTVWXYZ".IndexOf(c) >= 0;
        }
    }
}
