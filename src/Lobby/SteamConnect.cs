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
        // Lobby-data key advertising the host's connect info (read by a joiner after entering the lobby).
        // (The "mp_ip" DirectIP-fallback key was deleted 2026-07-22: HostPublish never wrote it, and the
        // parity gate means both peers always run the same DLL — no cross-version joiner to serve.)
        public const string HostKey = "mp_host"; // host SteamID64 (decimal string) → Steam-P2P join

        /// <summary>Rich-presence "connect" value Steam relaunches the joiner with (cold start → command line "+connect_lobby &lt;id&gt;").</summary>
        public static string ConnectString(ulong lobbyId) => "+connect_lobby " + lobbyId;

        // ─── The browse record: what a join-list row can say WITHOUT entering the lobby ───
        //
        // A row used to read "<steam persona>   —   JOIN" and nothing else, because the only thing the
        // host advertised was HostKey in LOBBY DATA — and lobby data is only readable once you are IN the
        // lobby (or the lobby came back from a RequestLobbyList). Neither holds for a friend row, which is
        // built from the friends list alone. RICH PRESENCE is the fix and it costs nothing: Steam already
        // replicates a friend's full rich-presence dictionary to us (that is how "connect" is read in
        // SteamInvite.HostingFriends), so these fields arrive with ZERO round trips and the row can name
        // the host and its occupancy the moment the screen opens.
        //
        // ONE key, not four: Steam caps rich presence at 20 keys per user and the game itself already
        // spends some, so the whole record is packed into a single pipe-delimited value.
        public const string SessionKey = "mp_s";

        /// <summary>Pack a host's browse record. Never throws; a null/blank nickname becomes "Host".</summary>
        public static string SessionValue(string hostName, ulong hostId, int players, int maxPlayers)
            => Sanitize(hostName) + "|" + hostId + "|" + players + "|" + maxPlayers;

        /// <summary>
        /// Unpack a browse record. Returns false — and leaves every out at its neutral value — for
        /// anything it does not fully understand, so a host running an older build (or a FRIEND who
        /// merely JOINED someone's session and therefore publishes no record of their own) degrades to
        /// the persona-name-only row rather than to a wrong number on screen.
        /// </summary>
        public static bool TryParseSession(string value, out string hostName, out ulong hostId,
                                           out int players, out int maxPlayers)
        {
            hostName = null; hostId = 0; players = 0; maxPlayers = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var p = value.Split('|');
            if (p.Length != 4) return false;
            if (!ulong.TryParse(p[1], out hostId)) return false;
            if (!int.TryParse(p[2], out players) || !int.TryParse(p[3], out maxPlayers)) return false;
            // A seat count that cannot be true is not rendered as one — occupancy is dropped, the name kept.
            if (players < 0 || maxPlayers <= 0 || players > maxPlayers) { players = 0; maxPlayers = 0; }
            hostName = string.IsNullOrWhiteSpace(p[0]) ? null : p[0];
            return true;
        }

        // '|' is the delimiter and a persona name may legally contain one; Steam caps a rich-presence
        // VALUE at 256 bytes, so the name is also clipped well inside that with the three numbers.
        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Host";
            var clean = name.Trim().Replace('|', '/');
            return clean.Length > 64 ? clean.Substring(0, 64) : clean;
        }

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
        /// Launch-parameter resolve covering BOTH canonical Steam launch forms: "+connect_lobby &lt;id64&gt;"
        /// (lobby invite) and "+connect &lt;value&gt;" (rich-presence Join Game; the value goes to the
        /// normal join classifier — ip:port or SteamID64). Lobby wins when both are present. Used for
        /// the cold-start command line AND for SteamApps.CommandLine on a relaunch-while-running.
        /// </summary>
        public static bool TryParseLaunch(string[] args, out ulong lobbyId, out string joinString)
        {
            lobbyId = 0;
            joinString = null;
            var id = ParseConnectLobby(args);
            if (id.HasValue) { lobbyId = id.Value; return true; }
            if (args != null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "+connect", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        joinString = args[i + 1].Trim();
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Raw command-line overload (SteamApps.CommandLine is ONE string). Whitespace split is
        /// enough — our connect values never contain spaces (lobby id / ip:port / SteamID64).</summary>
        public static bool TryParseLaunch(string commandLine, out ulong lobbyId, out string joinString)
        {
            return TryParseLaunch(
                string.IsNullOrWhiteSpace(commandLine)
                    ? null
                    : commandLine.Split((char[])null, StringSplitOptions.RemoveEmptyEntries),
                out lobbyId, out joinString);
        }

        /// <summary>
        /// The string to hand to the EXISTING join flow (SmartJoinParser classifies a 15+ digit number
        /// as Steam-P2P), or null when the lobby carried no usable host id.
        /// </summary>
        public static string ResolveJoinString(ulong hostSteamId)
            => hostSteamId != 0 ? hostSteamId.ToString() : null;
    }
}
