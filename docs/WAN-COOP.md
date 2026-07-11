# Playing co-op over the real internet (WAN)

How two players on different networks connect. For same-PC dev testing see
[`../tools/COOP-TESTING.md`](../tools/COOP-TESTING.md).

The host creates a session (Multiplayer → it auto-hosts on **Direct + STUN + Steam**
simultaneously, and asks the router to open port 14242 via **UPnP**); the joiner uses
**Join a game…** and pastes the host's **invite code** (or a raw IP). The Join box
auto-detects which kind of target you paste (`SmartJoinParser`).

## ONE unified invite code (v2) — steam-free

The host's SHARE rail now shows a **single** `INVITE CODE` (click-to-copy). It packs whatever
the host has — an optional Steam id **and/or** the best public endpoint (**UPnP-forwarded WAN
endpoint** preferred over the STUN-discovered one) — into one Crockford string (`UnifiedCode`,
`Multiplayer.Core/Util/UnifiedCode.cs`). Lengths are unique per variant (9 / 13 / 19 symbols)
so it never collides with the old formats. No Steam required: a GOG/Epic host still produces a
usable endpoint code.

Pasting it makes the client **cascade** transports in order until one connects
(`JoinPlan.Build`): **Steam P2P** (only if the code carries a steam id AND local Steam is
running) → **STUN UDP hole-punch** → **Direct TCP** to the same endpoint. The connecting box
names each stage ("Trying Steam… / hole-punch… / direct connection…"); the FIRST that
handshakes wins, and only the last stage's failure surfaces an error.

## Which path to use

| Path | Reliability | What to share / paste | Router setup |
|------|-------------|-----------------------|--------------|
| **Invite code** (recommended) | ✅ UPnP endpoint or Steam usually connects; ⚠️ STUN-only leg fails on symmetric NAT / CGNAT | the single SHARE **INVITE CODE** from the host's lobby | none if UPnP works on the host's router; else host forwards 14242 |
| **Direct IP** | ✅ Works whenever the port is reachable | `"<host-public-ip>:14242"` | **HOST forwards TCP 14242** (client: none) |
| **Direct IP over VPN** (easiest) | ✅ Works, no router setup | the host's VPN IP `:14242` | none — VPN/ZeroTier/Radmin/Hamachi makes it a LAN IP |
| **Steam invite** | ✅ Works between Steam friends (P2P relay, no NAT setup) | nothing — click **INVITE VIA STEAM** | none — Steam relays P2P |

## UPnP automatic port-forward (host)

- On host start (`StartHostAndOpenLobby`) the host fires `UpnpPortMapper.TryMap()`
  (`Multiplayer.Core/Net/UpnpPortMapper.cs`, hand-rolled SSDP + SOAP, no new dependency): SSDP
  M-SEARCH → fetch the IGD device XML → `AddPortMapping` **TCP+UDP 14242** to the host's LAN IP
  (lease 7200 s, auto-refreshed) and `GetExternalIPAddress` for the WAN IP.
- Success feeds the invite code's endpoint (that WAN IP:14242 is now actually open for BOTH the
  Direct TCP and the STUN UDP legs) and logs one line to `Player.log`. The mapping is removed on
  the same teardown chokepoint that drops the Steam lobby (`NetworkEngine.RunSteamLobbyCleanup`
  → `UpnpPortMapper.Unmap()`).
- **All best-effort**: routers with UPnP disabled/unsupported just yield no mapping (the code
  falls back to the STUN endpoint) — never a crash, never log-spam.

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

## Remaining limitation — both-CGNAT

- The cascade removes most failures: if UPnP opened the host's port, the **Direct TCP** leg
  reaches it even when STUN hole-punch can't; if the host is on Steam, the **Steam** leg relays
  with no NAT setup at all.
- What still can't work automatically: **both** players behind carrier-grade NAT (CGNAT / mobile)
  with **no** UPnP and **no** Steam — there is no reachable endpoint and no relay. The final error
  box says so and points to the workaround: a LAN/VPN tunnel (Radmin / ZeroTier / Hamachi → then
  join by the VPN IP as Direct IP), the host forwarding TCP+UDP 14242 by hand, or the Steam build.
- A built-in relay (TURN-style) is **explicitly out of scope** (see `COOP-SYNC-ROADMAP.md`).

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

## Connection resilience & parity (WAN test hardening)

Fixes from the 2-PC Steam WAN test. Constants: `HeartbeatIntervalMs=5000`, `HeartbeatTimeoutMs=20000`
(`SessionManager`).

- **Liveness = ANY inbound packet, not just Heartbeat** (`SessionManager.RefreshLiveness`, called from
  the single receive chokepoint `NetworkEngine.OnPacketReceived`). Fixes the false "Host heartbeat timed
  out — treating as host-leave" seen while `RosterProgress` (which bypasses the Heartbeat handler) was
  still arriving during the host's save-pick. Only refreshes ALREADY-tracked peers (no phantom entry from
  a pre-registration / rejected JOIN).
- **Timeouts SUSPENDED during a co-op transfer/world-load** (`SaveTransfer.TransferActive || InPhase2 ||
  LoadPhaseStarted`). A big WAN transfer can starve heartbeats and a slow-HDD native world-load can block
  the busy peer's main thread >20 s — the peer is alive; the barrier owns its own straggler timeout
  (`Phase1LoadTimeoutMs`/`RevealDeadlineMs` = 180 s).
- **Half-open send channel detection (client)**: the client tracks the last `HeartbeatAck` separately. If
  host traffic keeps ARRIVING but the host stops ACKing our heartbeats for `HeartbeatTimeoutMs`, our
  client→host P2P session is half-open (dead outbound) → leave via the existing `HostLeaveHandler`
  teardown chokepoint with a specific reason ("send channel dead — stage: heartbeat-ack timeout"). Ack
  clock is pinned during a load so a long transfer can't false-fire on completion.
- **`SteamTransport.SendPacket` no longer swallows errors**: counts consecutive send failures per peer,
  logs the first, and after 5 in a row surfaces it — client-side as a transport failure
  (`OnConnectionFailed`), host-side as that one client dropping (`OnPeerDisconnected`), never host-wide.
  (Note: a HALF-open session reports send SUCCESS locally, so the ack-timeout above is what catches it.)
- **Client enters the game's NATIVE loading screen for the save DOWNLOAD** (not the lobby + a corner
  plaque). On the first received chunk (`SaveTransferCoordinator.OnSaveChunk` first-chunk branch →
  `MultiplayerUI.EnterDownloadLoadingScreen`) the client drops the native curtain
  (`LevelSwitchCurtainController.DropCurtainInstant`) and hides the lobby, so the full-screen loading
  page appears immediately. The native BOTTOM bar shows TWO sequential phases on the SAME
  `Base.Utils.ProgressBarController`:
  1. **Download** 0→100% — `NativeWidgetFactory.BeginDownloadBar` assigns the live bar a
     `Base.Core.LoadingProgress` via its private `_currentLoadingProgress` field and the per-frame
     `Update` driver calls `SetDownloadBar(rxReceived/rxTotalBytes)`; label `LoadingText` = "Downloading
     save…" (→ "Waiting for players…" while it holds full through the prepare + LOADED-barrier gap).
  2. **Level load** — at phase-2 the native path (`OnLevelStateChanged` Loading →
     `SceneFadeController.DropCurtainInstant(level)` → `ProgressBar.SetLoadingLevel`) OVERWRITES the bar's
     source with the level's own `LoadingProgress`; `SaveTransferCoordinator.SetLoadingLevel` clears the
     download driver + restores the native label. Seamless hand-off, no flicker back to the lobby.
- **Top-right plaque stays on top as secondary detail** during BOTH phases (`LoadOverlayVisibility.
  ShouldShow` still gates on `downloading` = `IsDownloading`; the plaque canvas sorts at 7000, above the
  native curtain). The host is never downloading, so no lobby-after-PLAY popup returns.
- **Never-silent failure under the curtain**: a bad blob / checksum mismatch / prepare failure calls
  `SaveTransferCoordinator.AbortDownloadCurtain(stage)` → `MultiplayerUI.OnClientTransferFailed` which
  lifts the curtain, hides the plaque, and shows the staged native message box ("Save transfer failed
  (<stage>)…") instead of stranding the player on a stuck bar. A lost link / leave routes through
  `MultiplayerUI.TeardownLobbyState`, which also lifts the curtain.

### All-loaded reveal barrier (CS-style: nobody's loading screen closes until EVERY peer is in)

- **The gate**: `CurtainLiftGatePatch` (Postfix on `LevelSwitchCurtainController.LiftCurtainCrt`) wraps
  EVERY native curtain lift in a coroutine that parks on `SaveTransferMath.HoldCurtain(engineActive,
  sessionStarted, revealed)`, evaluated live each frame. Single chokepoint: the game lifts the curtain
  through MULTIPLE paths — the Loaded→Playing auto-lift, but ALSO direct `LiftCurtainCrt` calls that
  BYPASS `OnLevelStateChanged` (`UIStateSimulation.ExitState`, `UIStateInitView` tactical init,
  `GeoLevelController` error paths). The bypass lifts were why the loading screen closed for whoever
  finished loading (live RCA 2026-07-11). All routes converge on `LiftCurtainCrt`; one gate covers all.
  Host included — the host waits too.
- **Done = Playing, not data-read**: a peer reports `LoadComplete` ONLY at `OnReachedPlaying`
  (Loaded→Playing, curtain-liftable). The phase-2 pump's `LoadingProgress → null` no longer fires done —
  that is the DATA read finishing, seconds before the scene is actually playable; an early done there
  could open the reveal while a peer was still mid-init.
- **Hold UX**: the held peer sits at the native loading screen (bar full, label "Waiting for players…"
  via `NativeWidgetFactory.SetCurtainLabel`, restored at reveal); the top-right plaque keeps showing each
  peer's live progress.
- **Release together**: host tracks per-slot done; `RosterProgressTracker.AllDone(GetRosterSlots())` →
  broadcast `RevealAll` → every peer's `Revealed` flips → all parked lifts resume the same native fade
  simultaneously.
- **Failure semantics (no infinite wait)**: the wait set is the LIVE roster — a peer dropping mid-load is
  removed from the roster before the disconnect event fires, so all-done re-evaluates over the SHRUNK set
  and releases the rest (unit-pinned in `SaveTransferBarrierTests`). Belts: 180 s host forced-reveal +
  per-peer self-reveal deadlines, and any session teardown (engine inactive) opens the gate instantly — a
  parked lift can never hang a peer on a dead session.

### Parity SOFT-gate (host/client DLC + mods + settings)

At the join handshake the client sends a **parity manifest** inside its JOIN (`ParityManifestCollector.
Collect` → pure `ParityManifest`/`ParityComparer` in `Multiplayer.Core`, backward-compatible trailing
block on the JOIN packet). A mismatch does **NOT block the join** — the client joins the lobby normally:

- **READY LOCK**: the mismatched client's READY button greys + relabels "MODS MISMATCH"; the host ALSO
  ignores a READY from a mismatched client at the `SetClientReady` chokepoint (never trust client UI).
  Shared pure decision `ParityComparer.ReadyAllowed`.
- **ROSTER BADGE**: a native-cloned "!" button (warning tint) on the mismatched player's row, on BOTH
  the host and the affected client (own row). Click → the exact host-computed diff list (missing/extra
  mods, `version host≠client`, missing DLC, `Setting mod.key: host=… client=…`) via the native message
  box — the lobby has no hover-tooltip mechanism, the message box is the mod's detail surface. Status
  rides `PeerListEntry.ParityDiffs` (trailing block on PEER_LIST, legacy-compatible).
- **AUTO-APPLY of host mod settings**: `ConnectionAccepted` carries the HOST manifest; the client
  applies the host's **scalar** settings (bool/int/float/enum/string/decimal) IN MEMORY via the game's
  own `ModConfigField` accessors, fires `ModManager.OnConfigChanged` (mods supporting runtime changes
  re-read), then re-sends a fresh manifest (`ParityUpdate` 0x46). The host re-compares → if only config
  diffs existed the mismatch clears: badge disappears, READY unlocks (same PEER_LIST rail).
  - **Restore**: the client's original values are snapshotted once per (mod, field) and restored at the
    session teardown chokepoint (`NetworkEngine.Shutdown`/`TearDown` → `ParityConfigRestore` delegate
    hook). This path never writes disk — `ModConfig.json` persists only via `ModManager.SaveModConfig`
    (game init + the mod-management UI). CAVEAT: if the user opens+exits the game's MOD SETTINGS screen
    while host values are applied, the game itself persists them; the teardown restore still puts the
    in-memory values back, but that one disk write keeps host values until the next own edit.
- **Diff rules**: DLC diffs only when the host has one the client lacks (extra client DLC is fine);
  mods missing/extra/version-differ all diff; settings per-key value diffs. Mods/version/DLC are NOT
  auto-fixable → badge + lock persist with the exact diff.
- **LIMITATION (settings coverage)**: values are stringified without a JSON dependency — scalars diff
  exactly and auto-apply; complex/array config values are compared by stable type-string only and are
  NOT auto-appliable → they stay a mismatch (badge + lock persist; the diff names the mod/key). A client
  on an OLD (pre-parity) Multiplayer build sends no manifest → permanent "manifest missing" mismatch
  (joins, but can never ready).
