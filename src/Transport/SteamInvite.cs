using System;
using Multiplayer.Network;   // SteamConnect (pure decisions)
using Multiplayer.Util;      // InviteCode (pure codec)
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace Multiplayer.Transport
{
    /// <summary>
    /// Steam lobby-based invite/join glue. The HOST publishes a friends-only lobby advertising its
    /// connect info; a friend who accepts the invite (overlay invite, friends-list "Join Game", or a
    /// cold-start "+connect_lobby &lt;id&gt;") is routed back into the EXISTING client join flow via
    /// <see cref="OnJoinResolved"/>. Everything downstream (Steam-P2P transport, SmartJoinParser,
    /// OnLobbyJoin, OnConnectionFailed diagnostics) already works — this only supplies discovery.
    ///
    /// Uses the game's OWN shipped Facepunch.Steamworks bindings (already loaded + initialised by
    /// Base.Platforms.Steam.PlatformSteam) and the game's OWN callback pump
    /// (SteamClient.RunCallbacks, pumped every frame by PlatformSteam.UpdateSteamworksApi): we only
    /// subscribe to the static events, exactly as the game does — no custom pump.
    ///
    /// internal on purpose: keeps the Facepunch-typed members out of the mod assembly's ExportedTypes,
    /// so the mod loader never has to resolve Steamworks types at enable time; the methods here JIT
    /// only when a Steam path actually runs (host publish / invite accept).
    /// </summary>
    internal static class SteamInvite
    {
        /// <summary>Hand-off into the existing join flow (a host SteamID64 or an ip:port). Wired by MultiplayerUI → OnLobbyJoin.</summary>
        public static Action<string> OnJoinResolved;

        /// <summary>Never-silent stage diagnostics: (message, isError). isError → UI message box; both cases also log.</summary>
        public static Action<string, bool> Report;

        private static bool _handlersRegistered;
        private static Lobby? _hostLobby;   // set on the HOST once its invite lobby exists

        public static bool HasLobby => _hostLobby.HasValue;

        private static void Stage(string msg, bool isError = false)
        {
            if (isError) Debug.LogError("[Multiplayer][steam-invite] " + msg);
            else Debug.Log("[Multiplayer][steam-invite] " + msg);
            try { Report?.Invoke(msg, isError); } catch { }
        }

        /// <summary>
        /// Host's own short invite code (Crockford "XXXX-XXXX") derived from our Steam account id
        /// (the low 32 bits of our SteamID64). Null when Steam isn't running. The joiner decodes it
        /// back to our SteamID64 (InviteCode.ToSteamId64) and joins via the existing Steam-P2P path.
        /// Facepunch types stay confined to this internal class (JITs only when a Steam path runs).
        /// </summary>
        public static string LocalInviteCode()
        {
            try
            {
                if (!SteamClient.IsValid) return null;
                return InviteCode.Encode((uint)SteamClient.SteamId.Value);
            }
            catch { return null; }
        }

        // ─── HOST ──────────────────────────────────────────────────────────

        /// <summary>
        /// Create a friends-only lobby advertising our SteamID64 + a rich-presence connect string, so
        /// the invite overlay and the friends-list "Join Game" both work. async void: fire-and-forget;
        /// the result is reported via Stage. Idempotent — a prior lobby is left first.
        /// </summary>
        public static async void HostPublish()
        {
            try
            {
                if (!SteamClient.IsValid) { Stage("Steam not running — cannot create invite lobby", true); return; }
                LeaveHostLobby();
                Stage("creating Steam lobby…");
                var made = await SteamMatchmaking.CreateLobbyAsync(2); // 2-player co-op: host + 1 client
                if (!made.HasValue) { Stage("Steam lobby creation failed (no lobby returned)", true); return; }

                var lobby = made.Value;
                // Race guard: the user may have left / joined elsewhere while CreateLobbyAsync was in
                // flight — publishing now would light a "Join Game" pointing at a dead session.
                if (NetworkEngine.Instance?.IsHost != true)
                {
                    try { lobby.Leave(); } catch { }
                    Stage("host session gone before Steam lobby was ready — lobby dropped");
                    return;
                }
                lobby.SetFriendsOnly();
                lobby.SetJoinable(true);
                lobby.SetData(SteamConnect.HostKey, SteamClient.SteamId.Value.ToString());
                // Canonical rich presence: non-empty "connect" lights the friends-list "Join Game"
                // button (cleared explicitly in LeaveHostLobby — Steam does NOT auto-clear it while
                // the game keeps running); "status" is the human-readable View Game Info line.
                SteamFriends.SetRichPresence("connect", SteamConnect.ConnectString(lobby.Id.Value));
                SteamFriends.SetRichPresence("status", "Hosting co-op campaign");
                _hostLobby = lobby;
                Stage($"invite lobby ready ({lobby.Id.Value}) — Invite via Steam is now live");
            }
            catch (Exception ex) { Stage("lobby create exception: " + ex.Message, true); }
        }

        /// <summary>
        /// Canonical capacity gate: SetJoinable(false) once the co-op session is full, back to true
        /// when the slot frees while still hosting. Driven by NetworkEngine's existing peer
        /// connect/disconnect hooks (via NetworkEngine.SteamLobbySetJoinable) — no state of its own.
        /// </summary>
        public static void SetLobbyJoinable(bool joinable)
        {
            if (!_hostLobby.HasValue) return;
            try
            {
                _hostLobby.Value.SetJoinable(joinable);
                Stage("invite lobby joinable → " + joinable);
            }
            catch (Exception ex) { Stage("SetJoinable failed: " + ex.Message, true); }
        }

        /// <summary>HOST invite button: open Steam's invite dialog for our lobby. Returns false (with a stage message) when no lobby exists yet.</summary>
        public static bool OpenInviteOverlay()
        {
            if (!_hostLobby.HasValue) { Stage("Steam invite lobby not ready yet — try again in a moment", true); return false; }
            try
            {
                SteamFriends.OpenGameInviteOverlay(_hostLobby.Value.Id);
                Stage("invite overlay opened");
                return true;
            }
            catch (Exception ex) { Stage("invite overlay failed: " + ex.Message, true); return false; }
        }

        /// <summary>Leave + forget the host lobby and clear rich presence (session leave / re-host).</summary>
        public static void LeaveHostLobby()
        {
            if (!_hostLobby.HasValue) return;
            try { _hostLobby.Value.Leave(); } catch { }
            try { SteamFriends.ClearRichPresence(); } catch { }
            _hostLobby = null;
        }

        // ─── CLIENT ────────────────────────────────────────────────────────

        /// <summary>Subscribe once to the accept callbacks. Static events fire from the game's own RunCallbacks pump.</summary>
        public static void RegisterJoinHandlers()
        {
            if (_handlersRegistered) return;
            _handlersRegistered = true;
            SteamFriends.OnGameLobbyJoinRequested += OnLobbyJoinRequested;
            SteamFriends.OnGameRichPresenceJoinRequested += OnRichPresenceJoin;
            // Relaunch-while-running: accepting an invite from Steam's UI while PP is already open
            // fires OnNewLaunchParameters instead of a fresh process — re-read SteamApps.CommandLine
            // and route through the same launch handler.
            SteamApps.OnNewLaunchParameters += OnNewLaunchParams;
            Stage("Steam join handlers registered");
        }

        /// <summary>Cold start: launched by accepting a Steam invite. Handles BOTH canonical forms —
        /// "+connect_lobby &lt;id&gt;" (lobby invite) and "+connect &lt;value&gt;" (rich-presence join).</summary>
        public static void HandleColdStart()
        {
            RouteLaunch(Environment.GetCommandLineArgs(), null, "cold-start");
        }

        private static void OnNewLaunchParams()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                // Guard: never yank a live session. An EMPTY auto-hosted lobby doesn't count — the
                // join flow tears that down anyway (same as pasting a join target by hand).
                if (engine != null && engine.IsActiveSession
                    && (!engine.IsHost || engine.Session?.ClientCount > 0))
                {
                    Stage("relaunch params ignored — already in a co-op session");
                    return;
                }
                RouteLaunch(null, SteamApps.CommandLine, "relaunch");
            }
            catch (Exception ex) { Stage("relaunch params exception: " + ex.Message, true); }
        }

        // One connect-target chokepoint for cold start / relaunch / rich-presence join: args OR a raw
        // string. Returns false when no connect target was present (a normal launch — callers that
        // EXPECTED a target, like a Join click, surface their own error).
        private static bool RouteLaunch(string[] args, string commandLine, string source)
        {
            ulong lobbyId;
            string joinString;
            bool parsed = args != null
                ? SteamConnect.TryParseLaunch(args, out lobbyId, out joinString)
                : SteamConnect.TryParseLaunch(commandLine, out lobbyId, out joinString);
            if (!parsed) return false; // no connect params — a normal launch, stay quiet

            if (lobbyId != 0)
            {
                Stage(source + " invite: joining lobby " + lobbyId);
                JoinLobby(lobbyId);
            }
            else
            {
                // "+connect <value>": hand the value to the existing join flow; SmartJoinParser
                // classifies it (SteamID64 / ip:port), and an unreadable value surfaces through
                // OnLobbyJoin's own native error box — never a silent drop.
                Stage(source + " +connect: joining " + joinString);
                var handler = OnJoinResolved;
                if (handler != null) handler(joinString);
                else Stage("no join handler wired (mod menu not ready)", true);
            }
            return true;
        }

        private static void OnLobbyJoinRequested(Lobby lobby, SteamId invitedBy)
        {
            Stage("Steam invite accepted (lobby " + lobby.Id.Value + ")");
            JoinLobby(lobby.Id.Value);
        }

        private static void OnRichPresenceJoin(Friend friend, string connect)
        {
            // The connect string is our own rich-presence value ("+connect_lobby <id>" — or a
            // "+connect <value>" form from a future host); same grammar as a launch line.
            if (!RouteLaunch(null, connect, "friends-list Join"))
                Stage("rich-presence join carried no connect target: " + connect, true);
        }

        private static async void JoinLobby(ulong lobbyId)
        {
            try
            {
                Stage("joining Steam lobby " + lobbyId + "…");
                var res = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
                if (!res.HasValue) { Stage("could not join Steam lobby " + lobbyId, true); return; }

                var lobby = res.Value;
                // Authoritative host = the lobby owner; the advertised HostKey overrides it if present.
                ulong hostId = lobby.Owner.Id.Value;
                if (ulong.TryParse(lobby.GetData(SteamConnect.HostKey), out var hid) && hid != 0) hostId = hid;

                var joinStr = SteamConnect.ResolveJoinString(hostId, lobby.GetData(SteamConnect.IpKey));
                if (string.IsNullOrEmpty(joinStr)) { Stage("resolved no host connect info from the lobby", true); return; }

                Stage("resolved host " + joinStr + " — starting join");
                var handler = OnJoinResolved;
                if (handler != null) handler(joinStr);
                else Stage("no join handler wired (mod menu not ready)", true);
            }
            catch (Exception ex) { Stage("lobby join exception: " + ex.Message, true); }
        }
    }
}
