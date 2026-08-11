using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;   // MethodImpl(NoInlining) — the JIT boundary in SteamProbe
using Multiplayer.Network;   // SteamConnect (pure decisions)
using Multiplayer.Util;      // InviteCode (pure codec)
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace Multiplayer.Transport
{
    /// <summary>
    /// One friend who is currently in a co-op Steam lobby of this game. BCL fields ONLY — a name to draw
    /// and a lobby id to hand straight back to <see cref="SteamInvite.JoinFriendLobby"/>, so the UI never
    /// has to name a Steamworks type to show a friends list.
    ///
    /// TOP-LEVEL, NOT NESTED IN SteamInvite, and that placement is the point. As SteamInvite.HostingFriend
    /// every caller had to name SteamInvite to declare a variable or a List&lt;&gt; of it — and SteamInvite
    /// holds <c>Lobby?</c> statics, so resolving that name means loading a type whose layout needs
    /// Steamworks.Data.Lobby. Type LOAD, not class init: it can fault before any guard of ours runs. Out
    /// here the struct resolves on its own and no UI file names SteamInvite at all.
    /// </summary>
    internal struct HostingFriend
    {
        /// <summary>The Steam persona of the friend whose row this is — always present, the last-resort label.</summary>
        public string Name;
        public ulong LobbyId;

        /// <summary>The HOST's chosen nickname (the one the lobby roster shows), or null when the friend
        /// publishes no browse record — an older build, or a friend who merely JOINED someone's session.</summary>
        public string HostName;
        /// <summary>Occupancy to draw as "n/max". BOTH are 0 when unknown; the row then draws no count
        /// rather than a made-up one.</summary>
        public int Players;
        public int MaxPlayers;
        /// <summary>The host's SteamID64, advertised so a row click can start the join with NO Steam
        /// round trip. 0 = unknown, and the click falls back to entering the lobby to read it.</summary>
        public ulong HostId;

        /// <summary>The row label: the HOST's name when it advertised one, else the friend's persona.</summary>
        public string Label => string.IsNullOrWhiteSpace(HostName) ? Name : HostName;
        /// <summary>"2/50", or null when this friend advertised no occupancy.</summary>
        public string Occupancy => MaxPlayers > 0 ? Players + "/" + MaxPlayers : null;
    }

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
        /// <summary>Seats the published lobby was CREATED with — captured in <see cref="HostPublish"/>, which
        /// is the one method law L91 sanctions to read the seat count. 0 until a lobby exists.</summary>
        private static int _hostSeats;
        private static Lobby? _hostLobby;   // set on the HOST once its invite lobby exists
        // …and the lobby we entered as a CLIENT (accepted invite, friends-list row, "+connect_lobby").
        // JoinLobby used to drop this handle on the floor, which is why the invite button was host-only:
        // OpenInviteOverlay had nothing to point at. Cleared by LeaveSteamLobby, which EVERY session
        // teardown runs (NetworkEngine.RunSteamLobbyCleanup), so a handle from a dead session is never
        // reused to invite someone into a lobby that no longer exists.
        private static Lobby? _joinedLobby;

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

        /// <summary>True when local Steam is running — gates the client cascade's Steam-P2P attempt.</summary>
        public static bool IsSteamAlive()
        {
            try { return SteamClient.IsValid; }
            catch { return false; }
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
                LeaveSteamLobby();
                Stage("creating Steam lobby…");
                // The seat count is NetworkEngine's, not a second opinion — see NetworkEngine.MaxPlayers.
                // ONE read, used twice: it sizes the Steam lobby AND it is the "max" a browse row shows,
                // so the advertised capacity is provably the lobby's real capacity and never a second
                // literal to keep in step. Law L91 closes the reader set at this method — PublishSessionPresence
                // deliberately takes the number from here rather than reading the field itself.
                var seats = NetworkEngine.MaxPlayers;
                _hostSeats = seats;
                var made = await SteamMatchmaking.CreateLobbyAsync(seats);
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
                // button (cleared explicitly in LeaveSteamLobby — Steam does NOT auto-clear it while
                // the game keeps running); "status" is the human-readable View Game Info line.
                SteamFriends.SetRichPresence("connect", SteamConnect.ConnectString(lobby.Id.Value));
                SteamFriends.SetRichPresence("status", "Hosting co-op campaign");
                _hostLobby = lobby;
                // The browse record a friend's join-list row draws from — see SteamConnect.SessionKey.
                // Published here and refreshed on every seat change by SetLobbyJoinable, which the engine
                // already calls on each peer connect/disconnect.
                PublishSessionPresence();
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
                // Seats just changed — that IS the occupancy a browse row shows, so refresh it here
                // rather than adding a second engine hook that could drift out of step with this one.
                PublishSessionPresence();
            }
            catch (Exception ex) { Stage("SetJoinable failed: " + ex.Message, true); }
        }

        /// <summary>
        /// (Re)write the one rich-presence key a friend's join-list row reads: host nickname, host
        /// SteamID64, and n/max occupancy. Rich presence — NOT lobby data — because only rich presence is
        /// replicated to a friend who has not entered the lobby, and entering it is exactly the 8-second
        /// round trip the row must not pay (see JoinFriendLobby).
        ///
        /// The nickname is the SAME one the roster shows (ClientIdentity.LocalNickname, what JoinMessage
        /// carries), so the name on the browse row and the name in the lobby can never disagree. Silent
        /// no-op off the host path: only a host has seats to advertise.
        /// </summary>
        private static void PublishSessionPresence()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsHost) return;
                // +1 for the host's own seat: the row reads "how many people are in there", and
                // ClientCount deliberately counts CLIENTS only (NetworkEngine.MaxClients = MaxPlayers - 1).
                var players = (engine.Session?.ClientCount ?? 0) + 1;
                if (_hostSeats <= 0) return;   // no lobby sized yet — nothing truthful to advertise
                SteamFriends.SetRichPresence(SteamConnect.SessionKey, SteamConnect.SessionValue(
                    ClientIdentity.LocalNickname, SteamClient.SteamId.Value, players, _hostSeats));
            }
            catch (Exception ex) { Stage("session presence publish failed: " + ex.Message); }
        }

        /// <summary>
        /// Invite button: open Steam's invite dialog for WHICHEVER lobby this session holds — the one we
        /// published as host, or the one we joined as a client. Steam permits an explicit overlay invite
        /// from any lobby MEMBER; friends-only governs who may BROWSE a lobby, not who may be invited into
        /// it, so a joiner can pull their own friends into the host's session. Returns false (with a stage
        /// message) when we are in no lobby yet.
        /// </summary>
        public static bool OpenInviteOverlay()
        {
            var lobby = _hostLobby ?? _joinedLobby;
            if (!lobby.HasValue) { Stage("Steam invite lobby not ready yet — try again in a moment", true); return false; }
            try
            {
                SteamFriends.OpenGameInviteOverlay(lobby.Value.Id);
                Stage("invite overlay opened");
                return true;
            }
            catch (Exception ex) { Stage("invite overlay failed: " + ex.Message, true); return false; }
        }

        /// <summary>
        /// Leave + forget EVERY Steam lobby this session holds — the one published as host AND the one
        /// entered as a client — and clear rich presence. THE teardown chokepoint: every session end runs
        /// it through NetworkEngine.RunSteamLobbyCleanup, which is what stops a stale handle from a
        /// previous session reaching <see cref="OpenInviteOverlay"/>. Also run by HostPublish before it
        /// creates a new lobby (idempotent; each half null-guards).
        /// </summary>
        public static void LeaveSteamLobby()
        {
            if (_hostLobby.HasValue) { try { _hostLobby.Value.Leave(); } catch { } _hostLobby = null; }
            _hostSeats = 0;   // the sized lobby is gone; a stale capacity must not outlive it
            if (_joinedLobby.HasValue) { try { _joinedLobby.Value.Leave(); } catch { } _joinedLobby = null; }
            // Unconditional: rich presence never auto-clears while the game keeps running (canonical Valve
            // lifecycle), and leaving a "Join Game" pointing at a dead session is exactly the bug.
            try { SteamFriends.ClearRichPresence(); } catch { }
        }

        /// <summary>
        /// Steam friends who are in a co-op lobby of THIS game right now — one row each for the join
        /// screen. Empty (never null) when Steam is off, so the caller needs no Steam knowledge at all.
        ///
        /// TWO SIGNALS, most specific first. Our OWN rich-presence "connect" value is written by exactly
        /// one place (<see cref="HostPublish"/>) and cleared by exactly one place
        /// (<see cref="LeaveSteamLobby"/>), so parsing it back through the same pure grammar the launch
        /// line uses (SteamConnect.ParseConnectString) means "this friend is HOSTING a session of this
        /// mod" and nothing else. Steam's own view of which lobby the friend sits in is the fallback: it
        /// also covers a friend who JOINED someone else's session, and that is still a useful row —
        /// <see cref="JoinLobby"/> reads the advertised HostKey off the lobby, so the row resolves to the
        /// real HOST either way rather than to the friend.
        ///
        /// VERIFIED AGAINST THE SHIPPED ASSEMBLY (Facepunch.Steamworks.Win64, Version=1.0.0.0), not from
        /// memory: SteamFriends.GetFriends() returns IEnumerable&lt;Steamworks.Friend&gt;; Friend.Name is
        /// a string, Friend.Id is a SteamId FIELD, Friend.IsPlayingThisGame is a bool property;
        /// Friend.GameInfo is Nullable&lt;Steamworks.Friend.FriendGameInfo&gt; (a public NESTED type) and
        /// FriendGameInfo.Lobby is Nullable&lt;Steamworks.Data.Lobby&gt; whose Id.Value is the SteamID64
        /// of the lobby. Every one of those members exists and is public in this build.
        /// </summary>
        public static List<HostingFriend> HostingFriends()
        {
            var list = new List<HostingFriend>();
            try
            {
                if (!SteamClient.IsValid) return list;
                var me = SteamClient.SteamId.Value;
                foreach (var f in SteamFriends.GetFriends())
                {
                    if (f.Id.Value == me || !f.IsPlayingThisGame) continue;

                    var lobbyId = SteamConnect.ParseConnectString(f.GetRichPresence("connect")) ?? 0UL;
                    if (lobbyId == 0)
                    {
                        var info = f.GameInfo;
                        if (info.HasValue && info.Value.Lobby.HasValue)
                            lobbyId = info.Value.Lobby.Value.Id.Value;
                    }
                    if (lobbyId == 0) continue;   // in the game, but not in any lobby we could join

                    // The browse record (host nickname / occupancy / host id) rides the SAME already-
                    // replicated rich-presence dictionary "connect" was just read from, so naming the host
                    // and its seat count costs no extra call and no extra latency. Absent or malformed →
                    // every field stays neutral and the row degrades to persona-only (SteamConnect.TryParseSession).
                    string hostName; ulong hostId; int players; int maxPlayers;
                    SteamConnect.TryParseSession(f.GetRichPresence(SteamConnect.SessionKey),
                        out hostName, out hostId, out players, out maxPlayers);

                    list.Add(new HostingFriend
                    {
                        Name = f.Name,
                        LobbyId = lobbyId,
                        HostName = hostName,
                        Players = players,
                        MaxPlayers = maxPlayers,
                        HostId = hostId,
                    });
                }
            }
            catch (Exception ex)
            {
                // Log, do NOT raise the error box. This runs to PAINT a list, and the list already states
                // its own emptiness on screen; a modal error over the join screen (which has the menu
                // chrome hidden behind it) would be a worse answer than the empty state.
                Stage("friends scan failed: " + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// A friends-list row click. Routes into the EXISTING client join flow, adding no networking of
        /// its own — but NOT via <see cref="JoinLobby"/> when the row already carries the host id.
        ///
        /// WHY THE FAST PATH EXISTS. JoinLobby's whole job is to learn the host SteamID64, and the only
        /// way it can is to ENTER the lobby first: `await SteamMatchmaking.JoinLobbyAsync` is a round trip
        /// to Steam's matchmaking backend, and its cost has nothing to do with the distance to the host.
        /// Measured on the 2026-08-10 loopback playtest — three instances on one machine — it took 10,0 s
        /// (D:\PP-Instance3\Player.log: "joining Steam lobby 109775242037499855…" at 83,691 →
        /// "resolved host 76561197996210592 — starting join" at 93,706). The row now learns that same id
        /// from the host's rich presence when the LIST is built, at no cost, so the join starts at once.
        ///
        /// The lobby is still entered — just off the critical path — because membership is what
        /// <see cref="OpenInviteOverlay"/> needs to let a joiner invite their own friends.
        /// </summary>
        public static void JoinFriendLobby(ulong lobbyId, ulong hostId)
        {
            // No advertised id (an older host, or a friend who merely JOINED someone's session): fall back
            // to reading it off the lobby, which is the only place it can be found.
            if (hostId == 0) { JoinLobby(lobbyId); return; }

            var joinStr = SteamConnect.ResolveJoinString(hostId);
            Stage("friend row: host " + joinStr + " already known from rich presence — starting join now "
                  + "(entering the Steam lobby in the background)");
            var handler = OnJoinResolved;
            if (handler != null) handler(joinStr);
            else Stage("no join handler wired (mod menu not ready)", true);

            // AFTER the handler, for the same reason JoinLobby stores its handle after it: OnLobbyJoin
            // tears the PREVIOUS session down, and that teardown runs LeaveSteamLobby.
            AdoptLobbyInBackground(lobbyId);
        }

        /// <summary>Enter the row's lobby without anybody waiting on it, purely so the invite overlay has a
        /// handle. Discards the result if the session it belonged to is already gone — a lobby nobody is
        /// in must not outlive it and light a "Join Game" pointing at nothing.</summary>
        private static async void AdoptLobbyInBackground(ulong lobbyId)
        {
            try
            {
                var res = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
                if (!res.HasValue) { Stage("background lobby adopt: could not enter lobby " + lobbyId); return; }
                if (NetworkEngine.Instance?.IsActiveSession != true)
                {
                    try { res.Value.Leave(); } catch { }
                    return;
                }
                _joinedLobby = res.Value;
            }
            catch (Exception ex) { Stage("background lobby adopt failed: " + ex.Message); }
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

                var joinStr = SteamConnect.ResolveJoinString(hostId);
                if (string.IsNullOrEmpty(joinStr)) { Stage("resolved no host connect info from the lobby", true); return; }

                Stage("resolved host " + joinStr + " — starting join");
                var handler = OnJoinResolved;
                if (handler != null) handler(joinStr);
                else Stage("no join handler wired (mod menu not ready)", true);

                // KEEP THE HANDLE — and keep it AFTER the handler, not before. OnLobbyJoin tears the
                // PREVIOUS session down first (Disconnect + Shutdown), and Shutdown runs
                // RunSteamLobbyCleanup → LeaveSteamLobby, which would immediately drop the lobby we just
                // entered if it had already been stored. Stored here it belongs to the session that is
                // now starting, and that session's own teardown clears it.
                _joinedLobby = lobby;
            }
            catch (Exception ex) { Stage("lobby join exception: " + ex.Message, true); }
        }
    }

    /// <summary>
    /// THE ONLY SAFE WAY TO ASK ABOUT STEAM FROM UI CODE — and it is a separate type on purpose.
    ///
    /// <see cref="SteamInvite"/> holds <c>Lobby?</c> static fields (Facepunch structs), so the CLR cannot
    /// even load that type where Facepunch.Steamworks.Win64 is absent — a GOG/Epic install, which this
    /// mod supports and JoinPlan's comments are written around. The throw then lands in the frame of
    /// whoever touched the first static member, which is why SteamInvite's own internal try/catch blocks
    /// cannot help: they are inside the type that failed to load. A wrapper declared IN SteamInvite would
    /// be no better, for exactly the same reason. Only a type that names no Facepunch member can hold the
    /// catch, so this one does, and every UI caller goes through it.
    ///
    /// ONE LATCH, because a missing assembly does not appear later in the process: the first failure
    /// turns Steam off for the rest of the run and logs once. Without it a per-frame caller (the lobby
    /// greys its invite button every frame) throws and logs sixty times a second.
    ///
    /// ONE NOTION OF "STEAM IS ALIVE": this is the same SteamClient.IsValid that gates the Steam-P2P
    /// JOIN leg (JoinPlan.Build's steamAlive), so the button that starts an invite and the leg that
    /// finishes one can never disagree — RailCheck L155 arm (f) pins that chain.
    /// </summary>
    internal static class SteamProbe
    {
        private static bool _unavailable;

        // ─── THE JIT BOUNDARY, and why every entry point below needs one ───
        //
        // A try/catch cannot catch a failure raised while COMPILING the method the try lives in. The
        // exception surfaces at that method's CALL site — one frame up, outside its own handler. So a
        // method that both names SteamInvite AND holds the catch protects nobody: naming the type is what
        // fails, and it fails before the first instruction of the handler exists. That is precisely why
        // SteamInvite's own internal try/catch never runs, and the rule does not weaken by being applied
        // one level up.
        //
        // So each entry point below keeps its body free of the name and calls a NoInlining shim that has
        // nothing else in it. The shim's compilation is what fails; the failure lands at the call site,
        // which is INSIDE the caller's try. NoInlining is required, not decorative — inlining would fold
        // the SteamInvite reference back into the guarded method and reintroduce the fault.
        //
        // Same shape, same reason as ClientIdentity.ReadNicknamePref / WriteNicknamePref, which isolate
        // their PlayerPrefs ECalls this way (ClientIdentity.cs:82-97).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CallIsAlive() => SteamInvite.IsSteamAlive();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<HostingFriend> CallFriends() => SteamInvite.HostingFriends();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string CallLocalInviteCode() => SteamInvite.LocalInviteCode();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CallHandleColdStart() => SteamInvite.HandleColdStart();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CallHostPublish() => SteamInvite.HostPublish();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CallJoinFriendLobby(ulong lobbyId, ulong hostId) => SteamInvite.JoinFriendLobby(lobbyId, hostId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CallOpenInviteOverlay() => SteamInvite.OpenInviteOverlay();

        /// <summary>True when local Steam is running. False — never a throw — when it is not, or when
        /// there is no Steam runtime in this process at all.</summary>
        internal static bool IsAlive()
        {
            if (_unavailable) return false;
            try { return CallIsAlive(); }
            catch (Exception e) { Latch(e); return false; }
        }

        /// <summary>Friends currently hosting a co-op lobby; an EMPTY list (never null, never a throw)
        /// when Steam is missing or unhappy.</summary>
        internal static List<HostingFriend> Friends()
        {
            if (_unavailable) return new List<HostingFriend>();
            try { return CallFriends() ?? new List<HostingFriend>(); }
            catch (Exception e) { Latch(e); return new List<HostingFriend>(); }
        }

        /// <summary>The local player's own 8-symbol invite code, or NULL when Steam is missing or
        /// unhappy — deliberately the SAME null SteamInvite.LocalInviteCode already returns for
        /// !SteamClient.IsValid, so no caller's behaviour moves: GetOwnInviteCode still falls through its
        /// `?? "(Steam offline)"`, and CopyInviteCode still hits its IsNullOrEmpty → ShowSteamNotAvailable
        /// arm. Where Steam IS present this is the same string it always was.</summary>
        internal static string LocalInviteCode()
        {
            if (_unavailable) return null;
            try { return CallLocalInviteCode(); }
            catch (Exception e) { Latch(e); return null; }
        }

        /// <summary>Deliver a "+connect_lobby" cold start, if the game was launched by accepting an
        /// invite. A no-op where there is no Steam — which is the honest answer, since a process with no
        /// Steam runtime cannot have been launched from a Steam invite.</summary>
        internal static void HandleColdStart()
        {
            if (_unavailable) return;
            try { CallHandleColdStart(); }
            catch (Exception e) { Latch(e); }
        }

        /// <summary>Publish the friends-only Steam lobby that advertises this host. THE HOST PATH RUNS
        /// THROUGH HERE AND NOT THROUGH SteamInvite DIRECTLY: <c>StartHostAndOpenLobby</c> used to name
        /// SteamInvite in its own body, so on a GOG/Epic box the JIT failed while compiling that method and
        /// the throw landed at its CALL site — before <c>NetworkEngine.Create()</c>, i.e. CREATE SESSION did
        /// nothing at all for every non-Steam player. Fire-and-forget and harmless off Steam.</summary>
        internal static void HostPublish()
        {
            if (_unavailable) return;
            try { CallHostPublish(); }
            catch (Exception e) { Latch(e); }
        }

        /// <summary>Enter a friend's advertised lobby (the friends-list row). Same JIT boundary as
        /// <see cref="HostPublish"/>; a no-op where there is no Steam, which is also the only place the
        /// friends list can be empty enough for the row not to exist.</summary>
        internal static void JoinFriendLobby(ulong lobbyId, ulong hostId)
        {
            if (_unavailable) return;
            try { CallJoinFriendLobby(lobbyId, hostId); }
            catch (Exception e) { Latch(e); }
        }

        /// <summary>Open Steam's own invite dialog for the published lobby. Same JIT boundary again: the
        /// caller's try/catch could not have caught this, because the failure is raised while COMPILING the
        /// method that names SteamInvite and surfaces one frame up.</summary>
        internal static void OpenInviteOverlay()
        {
            if (_unavailable) return;
            try { CallOpenInviteOverlay(); }
            catch (Exception e) { Latch(e); }
        }

        /// <summary>Steam is not available in this process — ONE latch and ONE log for the whole run.
        /// internal so the startup wiring (MultiplayerUI.WireSteamInvite) reports through the same one:
        /// if the wiring could not even be attached, nothing else Steam-shaped can work either, and every
        /// probe above should short-circuit rather than re-discover it.</summary>
        internal static void Latch(Exception e)
        {
            if (_unavailable) return;   // a missing assembly does not come back — say it once
            _unavailable = true;
            Debug.LogWarning("[Multiplayer] no Steam runtime in this process (" + e.GetType().Name +
                             ") — Steam invites and the friends list stay off for the rest of the run.");
        }
    }
}
