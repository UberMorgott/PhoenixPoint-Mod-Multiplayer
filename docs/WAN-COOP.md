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
| **Steam invite** | ❌ Not implemented (see below) | — | — |

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

## Steam invite — NOT implemented yet

- The lobby **"INVITE VIA STEAM"** button only opens the Steam friends overlay
  (`SteamFriends.OpenOverlay("friends")`). There is **no** Steam lobby creation, no game
  invite, and no client-side join-accept callback — so a Steam invite cannot auto-join.
  (The client already in the lobby correctly has no invite button; SHARE is host-only.)
- `SteamTransport` implements only raw P2P packet send/recv keyed by a `SteamID64`. A
  **manual** Steam join is theoretically possible — host shares their SteamID64, friend
  pastes it into Join — but this is **unverified** and there is no UI to surface the host's
  SteamID64 today.
- Smallest viable plan to make Steam invites real (a discrete subsystem, not yet built):
  1. Host: `SteamMatchmaking.CreateLobby` + set the host SteamID64 in lobby data / rich
     presence `connect`.
  2. Host invite button: `SteamMatchmaking.InviteUserToLobby` (or rely on the overlay's
     built-in invite once a lobby exists).
  3. Client: subscribe `SteamMatchmaking.OnLobbyEntered` / `SteamFriends.OnGameLobbyJoinRequested`
     (and the `+connect_lobby` launch arg) → read host SteamID64 → `SteamTransport.Connect`.
