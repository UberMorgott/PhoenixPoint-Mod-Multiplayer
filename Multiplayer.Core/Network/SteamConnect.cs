using System;

namespace Multiplayer.Network
{
    /// <summary>
    /// Pure helpers for the Steam lobby-based invite/join subsystem. NO Steam runtime — every method
    /// is BCL-only string/number work, so the connect-info resolve, the cold-start command-line parse,
    /// and the transport-fallback selection are all unit-testable without a Steam client. The Steam
    /// glue that actually talks to Facepunch (SteamInvite) delegates these decisions here.
    /// </summary>
    public static class SteamConnect
    {
        // Lobby-data keys advertising the host's connect info (read by a joiner after entering the lobby).
        public const string HostKey = "mp_host"; // host SteamID64 (decimal string) → Steam-P2P join
        public const string IpKey = "mp_ip";     // optional "ip:port" → DirectIP fallback if set

        /// <summary>Rich-presence "connect" value Steam relaunches the joiner with (cold start → command line "+connect_lobby &lt;id&gt;").</summary>
        public static string ConnectString(ulong lobbyId) => "+connect_lobby " + lobbyId;

        /// <summary>Cold start: find "+connect_lobby &lt;id&gt;" in a process command line → lobby id (null if absent / 0).</summary>
        public static ulong? ParseConnectLobby(string[] args)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase)
                    && ulong.TryParse(args[i + 1], out var id) && id != 0)
                    return id;
            }
            return null;
        }

        /// <summary>Same, but for a single rich-presence connect string like "+connect_lobby 123".</summary>
        public static ulong? ParseConnectString(string connect)
        {
            if (string.IsNullOrWhiteSpace(connect)) return null;
            return ParseConnectLobby(connect.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// Fallback selection: prefer Steam-P2P (the host's SteamID64) when present, else the DirectIP
        /// "ip:port" carried in lobby data. Returns the string to hand to the EXISTING join flow
        /// (SmartJoinParser classifies a 15+ digit number as Steam and an ip:port as DirectIP), or null
        /// when neither is usable.
        /// </summary>
        public static string ResolveJoinString(ulong hostSteamId, string ipPort)
        {
            if (hostSteamId != 0) return hostSteamId.ToString();
            if (!string.IsNullOrWhiteSpace(ipPort)) return ipPort.Trim();
            return null;
        }
    }
}
