# 14 — Transport & Message Protocol

[← Index](../README.md)

## Transport-Agnostic Core

- Core (sync / permissions / save-sync / game logic) is **transport-independent**.
- All networking goes through one interface; core never knows if Steam or sockets delivered the bytes.

```
[ Sync / Permissions / Save-sync / Game logic ]   ← core, transport-agnostic
                  ↕  ITransport (bytes + peerId)
   ┌──────────────┼──────────────┐
SteamP2pTransport   DirectIpTransport   (future)
```

## ITransport Interface

- `Send(peerId, bytes, reliable)`
- `Broadcast(bytes, reliable)`
- event `OnReceive(peerId, bytes)`
- event `OnPeerConnect(peerId)` / `OnPeerDisconnect(peerId)`
- New transport = new implementation, core untouched.

## Implementations

- **`DirectIpTransport`** (TCP sockets) — **built first**.
  - Loopback `127.0.0.1` enables full solo dev/testing on one PC → [16](16-open-questions-sdk.md) (2nd-instance caveat).
  - ZeroTier / VPN "just works" as a normal IP — no extra code.
- **`SteamP2pTransport`** (`ISteamNetworkingSockets.ConnectP2P` / `ISteamNetworkingMessages`) — later phase.
  - Runs under the game's Steam App ID; tries direct NAT-punch first, relay as fallback.
  - **VERIFY:** Steamworks availability to mod + no dev "activation" needed → [16](16-open-questions-sdk.md).

## Latency Stance

- Model is **authoritative-host, NOT lockstep** → latency-tolerant.
- Turn-based tactical / geoscape → 100-200ms unnoticed. No need to chase minimal ping.

## Message Envelope

- Simple framing: `[1 byte type][payload]`.
- **Payload serialization:**
  - Small control packets → **JSON** (`System.Text.Json`, built-in, debuggable).
  - Save blob → **raw bytes** (gzip'd, chunked).

## Message Types (by phase)

- **Lobby:** `JOIN` (carries persistent `playerGUID` → [20](20-player-management-persistence.md)), `RENAME`, `READY`, `PEER_LIST`, `LEAVE`
- **Start / sync:** `SAVE_CHUNK`, `SAVE_DONE`, `PROGRESS`, `LOADED`, `BEGIN`
- **In-game (later):** `ACTION`, `ACTION_RESULT`, `ACTION_REJECT`, `ASSIGN_OWNER`, `PERMISSION`
- **Tactical:** `END_TURN_READY` → [17](17-tactical-concurrency.md)
- **Geoscape:** `TIME_STATE`, `EVENT`, `STATE_ENTER` → [19](19-geoscape-concurrency.md)
- **Session/system:** `NOTICE` (toasts: disconnect / takeover / reconnect / host-loss) → [21](21-disconnect-reconnect.md)

## Reliability

- Lobby / save / action → **reliable + ordered** (TCP gives it free; Steam → reliable flag).
- `PROGRESS` → may be **unreliable** (lost progress frame is harmless).
