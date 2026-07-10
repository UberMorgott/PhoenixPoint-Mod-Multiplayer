using System;
using Multiplayer.Network;   // SteamConnect (pure decisions)
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
                var made = await SteamMatchmaking.CreateLobbyAsync(4);
                if (!made.HasValue) { Stage("Steam lobby creation failed (no lobby returned)", true); return; }

                var lobby = made.Value;
                lobby.SetFriendsOnly();
                lobby.SetJoinable(true);
                lobby.SetData(SteamConnect.HostKey, SteamClient.SteamId.Value.ToString());
                SteamFriends.SetRichPresence("connect", SteamConnect.ConnectString(lobby.Id.Value));
                _hostLobby = lobby;
                Stage($"invite lobby ready ({lobby.Id.Value}) — Invite via Steam is now live");
            }
            catch (Exception ex) { Stage("lobby create exception: " + ex.Message, true); }
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
            Stage("Steam join handlers registered");
        }

        /// <summary>Cold start: this process was launched by accepting a Steam invite → "+connect_lobby &lt;id&gt;" on the command line.</summary>
        public static void HandleColdStart()
        {
            var id = SteamConnect.ParseConnectLobby(Environment.GetCommandLineArgs());
            if (id.HasValue) { Stage("cold-start invite: joining lobby " + id.Value); JoinLobby(id.Value); }
        }

        private static void OnLobbyJoinRequested(Lobby lobby, SteamId invitedBy)
        {
            Stage("Steam invite accepted (lobby " + lobby.Id.Value + ")");
            JoinLobby(lobby.Id.Value);
        }

        private static void OnRichPresenceJoin(Friend friend, string connect)
        {
            var id = SteamConnect.ParseConnectString(connect);
            if (id.HasValue) { Stage("friends-list Join (lobby " + id.Value + ")"); JoinLobby(id.Value); }
            else Stage("rich-presence join carried no lobby id: " + connect, true);
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
