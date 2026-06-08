# 13 — Session Start & Sync (Save Transfer + Barrier)

[← Index](../README.md)

> How a session goes from "all ready in lobby" to "everyone playing the same campaign".

## Trigger

- All players `Ready` → host presses `Play`.
- **Then** native save-picker opens (host chooses the campaign save).
- Save selection happens **after** lobby, not before.

## Save Transfer

- Host serializes chosen save → sends to all clients.
- **Compression:** gzip via `System.IO.Compression.GZipStream` (built-in, zero deps).
  - JSON-like saves compress ~5-10× (5MB → ~0.5-1MB).
  - **VERIFY:** if PP already stores saves compressed/binary, skip re-compress, send as-is → [16](16-open-questions-sdk.md).
- Wire format: **length-prefixed blob**.
  - DirectIP/TCP → stream directly.
  - Steam P2P → **chunk** (reliable message size limit ~512KB-1MB).
- One-time transfer at start → size not latency-critical.

## Barrier Sync (Ready-Gate)

- Standard co-op/RTS pattern. **Host = barrier coordinator.**
- Steps:
  1. Host broadcasts save blob to all.
  2. Each client loads save locally; on finish → sends `LOADED` ack to host.
  3. Host waits for `LOADED` from **all** clients (+ host loaded itself).
  4. **Loading screen stays up + input blocked** on everyone until start signal.
  5. Host has all `LOADED` → broadcasts `BEGIN` → all unblock **simultaneously** → enter geoscape synced.
- **Input blocking:** keep loading overlay on top, ignore input until `BEGIN`. Released for everyone at the same moment.
- **Timeout / failure (required):** a client that never sends `LOADED` (crash / slow disk) must not hang the barrier forever → host shows "waiting for X", offers kick / abort after N seconds.
- After `BEGIN` there are **no more barriers** — the action pipeline runs without waiting ([09](09-sync-strategy.md)).

## Loading Screen — Per-Player Progress

- Two phases shown per peer:
  - **Phase 1 — Download:** `bytes received / total` from our own transfer → exact %, no hacks.
  - **Phase 2 — Game load:** Harmony-hook PP's native loading-progress source → read value → report.
    - **VERIFY:** locate PP's loading progress class/field/event → [16](16-open-questions-sdk.md).
- Each peer sends `PROGRESS` packets to host; host aggregates + rebroadcasts.
- Display = player list with phase + bar:
  ```
  Host (you)   ██████████ ready ✓
  Player2      █████░░░░░ loading 52%
  Player3      ███░░░░░░░ downloading 31%
  ```
- **Throttle:** send progress every ~150ms or ~5% delta, not per-frame. Packets are tiny; may be **unreliable** (a dropped progress frame doesn't matter).

## Join Model (v1)

- **Join-at-start only.** No join-in-progress (fresh peer mid-campaign) in v1.
- **Reconnect is designed** (no longer deferred): host auto-saves live state via the game's own save system, then re-runs this same barrier mid-session → [21 — Disconnect & Reconnect](21-disconnect-reconnect.md). Avoids the custom live-state serializer that originally blocked it.
- Still deferred: join-in-progress for a brand-new peer → [16](16-open-questions-sdk.md).
