# Playing co-op over the real internet (WAN)

How two players on different networks connect. For same-PC dev testing see
[`../tools/COOP-TESTING.md`](../tools/COOP-TESTING.md).

The host creates a session (Multiplayer → it auto-hosts on **Direct + STUN + Steam**
simultaneously); the joiner uses **Join a game…** and pastes one of the targets below.
The Join box auto-detects which kind of target you paste (`SmartJoinParser`).

## Which path to use

| Path | Reliability | What to share / paste | Router setup |
|------|-------------|-----------------------|--------------|
| **Direct IP** (recommended) | ✅ Works whenever the port is reachable | `"<host-public-ip>:14242"` | **HOST forwards TCP 14242** (client: none) |
| **Direct IP over VPN** (easiest) | ✅ Works, no router setup | the host's VPN IP `:14242` | none — VPN/ZeroTier/Radmin/Hamachi makes it a LAN IP |
| **Invite code** (STUN) | ⚠️ Best-effort — fails on symmetric NAT / CGNAT (common on home/mobile) | the SHARE code from the host's lobby | none, but not guaranteed to traverse |
| **Steam invite** | ✅ Works between Steam friends (P2P relay, no NAT setup) | nothing — click **INVITE VIA STEAM** | none — Steam relays P2P |

## Direct IP — exact port-forward answer

- **Protocol + port: TCP `14242`.** (`DirectTransport` = TCP; the host binds
  `IPAddress.Any:14242`, `DefaultDirectPort` in `MultiplayerUI` / `SmartJoinParser`.)
- **Only the HOST forwards a port.** Forward **TCP 14242** on the host's router to the
  host PC's LAN IP, and allow Phoenix Point inbound in the host's OS firewall.
  **The client forwards nothing** — it makes an outbound TCP connection.
- The host finds their public IP at e.g. whatismyipaddress.com; the client pastes
  `"<host-public-ip>:14242"` into **Join a game…**.
- A different port works too: if the host is started on port N (currently always 14242),
  forward TCP N and the client pastes `":N"`.
- Failure messages now name the cause: **ConnectionRefused** = nothing listening on that
  port (wrong port / host not hosting); **TimedOut** = firewall or port not forwarded.

## Invite code (STUN) — why it can fail

- The lobby SHARE code encodes the host's STUN-discovered public UDP endpoint; joining by
  code uses UDP hole-punching (`StunTransport`). Many home routers (and all CGNAT / mobile)
  use symmetric NAT that hole-punching cannot traverse — the join then fails with
  `no HOLE_PUNCH_ACK from host`. This is a network-environment limit, not a bug.
- Hole-punching beyond the plain transport is **explicitly out of scope** (see
  `COOP-SYNC-ROADMAP.md` → OUT of scope). If the code fails, use **Direct IP** above.

## Steam invite — IMPLEMENTED (increment 1)

Uses the game's OWN shipped `Facepunch.Steamworks.Win64.dll` bindings and its OWN callback pump
(`SteamClient.RunCallbacks`, pumped every frame by `Base.Platforms.Steam.PlatformSteam.UpdateSteamworksApi`).
No new dependency. All Facepunch code is confined to `src/Transport/SteamInvite.cs`
(`internal`, so it stays out of the mod's `ExportedTypes`); the pure decisions live in
`Multiplayer.Core/Network/SteamConnect.cs` (unit-tested, `Multiplayer.Tests/SteamConnectTests.cs`).

### Implemented behavior

- **Host** (`StartHostAndOpenLobby` → `SteamInvite.HostPublish()`): on lobby open, creates a
  **friends-only** Steam lobby (`SteamMatchmaking.CreateLobbyAsync(2)` — 2-player co-op — →
  `SetFriendsOnly()` + `SetJoinable(true)`), advertises the host `SteamID64` in lobby data key
  `mp_host`, and sets rich presence `connect = "+connect_lobby <lobbyId>"` (lights the friends-list
  **Join Game**; also the cold-start payload) + `status = "Hosting co-op campaign"` (View Game Info
  text). A post-create race guard drops the lobby if the host session died while `CreateLobbyAsync`
  was in flight (no "Join Game" onto a dead session).
- **Canonical lifecycle** (Valve: rich presence does NOT auto-clear while the game keeps running):
  - EVERY teardown routes through `NetworkEngine.Shutdown()`/`TearDown()` → static hook
    `NetworkEngine.SteamLobbyCleanup` → `SteamInvite.LeaveHostLobby()` = `Lobby.Leave()` **+**
    `SteamFriends.ClearRichPresence()` together. Covers the leave button, the smart-join handoff
    (joining someone else drops the auto-hosted lobby), and the return-to-menu TearDown patch —
    friends' **Join Game** goes dark the moment the host stops hosting.
  - Capacity gate: host-side `NetworkEngine.OnPeerConnected/OnPeerDisconnected` → hook
    `SteamLobbySetJoinable(ClientCount == 0)` → `Lobby.SetJoinable(false)` when the single client
    slot fills, back to `true` when it frees while still hosting.
  - The hooks are delegate fields (wired in `MultiplayerUI.WireSteamInvite`) so `NetworkEngine`
    never references Steamworks types — JIT-safe without the Facepunch assembly (test runners).
- **Invite button**: the existing host-only SHARE-rail **"INVITE VIA STEAM"** button
  (`MultiplayerUI.InvitePlayers`) now opens Steam's invite dialog for that lobby
  (`SteamFriends.OpenGameInviteOverlay(lobbyId)`). No new UI widget. If the lobby is not ready yet
  it says so (retry) instead of a silent no-op.
- **Client** (`SteamInvite.RegisterJoinHandlers`, wired once in `MultiplayerUI.Awake`): subscribes
  `SteamFriends.OnGameLobbyJoinRequested` (overlay invite) + `OnGameRichPresenceJoinRequested`
  (friends-list Join) + `SteamApps.OnNewLaunchParameters` (relaunch-from-Steam while PP is already
  running → re-reads `SteamApps.CommandLine`; guarded — ignored while genuinely in a session; an
  EMPTY auto-hosted lobby doesn't count). Cold start (`OnMenuReady` → `HandleColdStart`) parses BOTH
  canonical launch forms via `SteamConnect.TryParseLaunch`: `+connect_lobby <id64>` (lobby invite)
  AND `+connect <value>` (rich-presence join; the value goes to the normal join classifier — lobby
  wins if both are present). On accept → `SteamMatchmaking.JoinLobbyAsync(id)` → resolve host from
  `Lobby.Owner` (or `mp_host` data) → `SteamConnect.ResolveJoinString` →
  **`MultiplayerUI.OnLobbyJoin("<hostSteamID64>")`**, i.e. the EXISTING client join flow
  (`SmartJoinParser` → Steam-P2P transport → `SteamTransport.Connect`). DirectIP fallback is
  read-supported (`mp_ip` lobby key) but the host advertises none today (P2P is the Steam path).
- **Never-silent diagnostics**: every stage (lobby create, invite open, invite accepted, lobby join,
  host resolve) logs to `Player.log` with the `[Multiplayer][steam-invite]` tag; failures also raise a
  native message box naming the stage (`SteamInvite.Report` → `MultiplayerUI`). A downstream Steam-P2P
  connect failure still surfaces via `NetworkEngine.BuildTransportFailureReason` (SteamP2P hint updated).
- **Cleanup**: see Canonical lifecycle above — all teardown paths funnel through the
  `NetworkEngine.SteamLobbyCleanup` hook; re-hosting (`HostPublish`) also drops any prior lobby first.

### 2-PC live test checklist (real Steam, BOTH machines have the mod enabled + are Steam friends)

1. **Host PC**: launch PP → main menu → **MULTIPLAYER** (auto-hosts Direct+STUN+Steam, opens lobby).
2. Host: confirm `Player.log` shows `[steam-invite] invite lobby ready (<id>) — Invite via Steam is now live`.
3. Host: click **INVITE VIA STEAM** → Steam overlay invite dialog opens → invite the friend.
4. **Client PC**: accept the invite from the Steam overlay/notification (game already running).
5. Client: expect `[steam-invite] Steam invite accepted … resolved host <id> — starting join`, then the
   native **Connecting to host…** box, then the co-op lobby with both players in the roster.
6. Also test the **friends-list "Join Game"** button (rich presence) and **cold start** (accept the
   invite while the client's PP is CLOSED → Steam relaunches it with `+connect_lobby`; join fires at menu ready).
7. **Relaunch-while-running**: with client PP already open, click **Join Game** from Steam's friends
   list UI (not the in-game overlay) → the running instance receives `OnNewLaunchParameters` and joins.
8. Host picks a save / NEW CAMPAIGN → PLAY → verify the client loads into the same campaign.
9. **Lifecycle**: after the client connects, a third friend's view of the host shows the lobby as not
   joinable (session full). Client leaves → host's lobby becomes joinable again.
10. **Verify Join Game disappears for friends after the host disconnects** (leave lobby or return to
    menu): the host's friends-list entry must lose the **Join Game** button within seconds
    (rich presence cleared + lobby left). A lingering button = cleanup regression.
11. Negative: click **INVITE VIA STEAM** immediately at host start before the lobby is ready → expect
    the "lobby not ready — try again" box (never a silent no-op).

### Behavior when the game is NOT modded on accept

- **Client not running the mod (or mod disabled)**: accepting the invite still launches/focuses PP via
  Steam, but no mod callback is registered, so nothing auto-joins — the client just lands on the normal
  main menu. There is no crash and no error; the join simply does not happen. Both players must have the
  Multiplayer mod enabled. (Vanilla PP has no co-op, so Steam's own "Join Game" has nothing to route to.)
- **Host not running the mod**: no lobby is ever published, so the friend has nothing to accept.
