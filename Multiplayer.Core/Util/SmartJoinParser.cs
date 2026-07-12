using System.Net;
using System.Linq;

namespace Multiplayer.Util
{
    public enum JoinKind { Invalid, DirectIp, DirectHost, StunCode, SteamId, Unified }

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
        // Unified (v2) code: carries a Steam id (0 = none) AND/OR an endpoint (ip = null = none). The
        // client cascades over whatever is present (JoinPlan.Build).
        public static JoinTarget Unified(ulong steamId, string ip, int port) => new JoinTarget(JoinKind.Unified, ip, port, steamId);
    }

    /// <summary>
    /// Classifies a pasted join string in a fixed precedence (R6): IPv4[:port] first
    /// (DirectIP), else an 8-symbol invite code (decodes to a Steam account id → SteamID64),
    /// else a 10-symbol Crockford short code (STUN via ConnectCode.Decode), else a bare 64-bit
    /// number (Steam), else a DNS hostname, else Invalid. Pure — does no networking.
    /// </summary>
    public static class SmartJoinParser
    {
        public const int DefaultDirectPort = 14242;

        // True when a parsed target is a plain DirectIP aimed at our OWN loopback:boundPort — i.e. the
        // endpoint our own auto-host binds. Pure (no networking): callers pair it with a live "am I a
        // healthy host" check to refuse a host trying to join its own game. IPAddress.IsLoopback covers
        // the whole 127.0.0.0/8 block and ::1; localhost is already normalized to 127.0.0.1 by Parse.
        public static bool IsOwnLoopback(JoinTarget target, int boundPort)
            => target.Kind == JoinKind.DirectIp
               && target.Port == boundPort
               && IPAddress.TryParse(target.Ip, out var a)
               && IPAddress.IsLoopback(a);

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

            var stripped = s.Replace("-", "").Replace(" ", "");

            // 2) Invite code: exactly 8 Crockford symbols with a valid check symbol → a Steam account
            // id. Checked BEFORE STUN (10 symbols) — distinct length, so the two never collide. The
            // resolved SteamID64 rides the EXISTING SteamId join path (no new downstream surface).
            if (stripped.Length == InviteCode.TotalSymbols && InviteCode.TryDecode(s, out var accountId))
                return JoinTarget.Steam(InviteCode.ToSteamId64(accountId));

            // 3) STUN short code (Crockford base32, optionally dash-grouped).
            if (stripped.Length == 10 && stripped.All(IsCrockford))
            {
                var ep = ConnectCode.Decode(s);
                if (ep != null) return JoinTarget.Stun(ep);
            }

            // 3b) Unified (v2) invite code: 9 / 13 / 19 Crockford symbols carrying a Steam id and/or a
            // public endpoint. Distinct lengths from InviteCode(8)/ConnectCode(10), and checked BEFORE
            // the bare-SteamID branch so a 19-symbol all-digit code is never misread as a SteamID64.
            if ((stripped.Length == 9 || stripped.Length == 13 || stripped.Length == 19)
                && UnifiedCode.TryDecode(s, out var uAccount, out var uHasSteam, out var uEp, out var uHasEp))
            {
                return JoinTarget.Unified(
                    uHasSteam ? InviteCode.ToSteamId64(uAccount) : 0UL,
                    uHasEp ? uEp.Address.ToString() : null,
                    uHasEp ? uEp.Port : 0);
            }

            // 4) Bare 64-bit SteamID.
            if (ulong.TryParse(s, out var steamId) && s.Length >= 15)
                return JoinTarget.Steam(steamId);

            // 5) DNS hostname[:port] (e.g. "myhost.ddns.net", "host.com:9000"). Checked LAST so a
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
