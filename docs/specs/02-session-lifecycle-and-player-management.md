# Session Lifecycle, Identity & Player Management

> Connection → lobby → identity → session-start barrier → state-vs-save invariant → player management & permission persistence. This is the design layer that surrounds the action-sync core in [01-design](01-design.md). Connection-menu UI injection → [engine/03-harmony-patches](../engine/03-harmony-patches.md); transport/message protocol → [engine/02-transport-layer](../engine/02-transport-layer.md).

---

## 1. Lobby & Peer Identity

### Lobby-First Model

- `Create Game` opens the **lobby immediately** — the campaign is **NOT loaded yet**.
- Host cannot play (no geoscape interaction) while in the lobby.
- **Why:** prevents divergence. If the host played before others joined, the host's live in-memory state would drift from the on-disk save (e.g. "aircraft already moved"). Lobby-first makes the **on-disk save the single source of truth** at start — nobody has played, everybody loads identical state.
- Bulk state sync happens **exactly once** (at session start, §2); afterwards the action pipeline keeps everyone consistent (see [01-design](01-design.md) §1).

### Lobby Room Contents

- Host view:
  - Own **IP / connection code** displayed for copy-paste.
  - `Invite Friends` button (Steam overlay).
  - Player list with **ready** status.
  - `Play` button (host only) → enabled when all ready.
- Client view:
  - Player list + ready status.
  - `Ready` toggle.
- Both: **nickname edit field** (see below).

### Peer Identity

- Each peer = **stable `peerID`** (transport-level: SteamID or connection id) — never changes during a session.
- **`nickname`** = mutable display label mapped to `peerID`.
- Default nickname: Steam persona name (Steam transport) else `Player N`.

### Nicknames (live)

- Any player (including host) edits **their own** nickname in the lobby.
- Edit → small `RENAME{peerID, newName}` packet → broadcast → everyone's UI updates instantly.
- Synced through the same lobby-state broadcast as player list / ready / progress.

### Why Identity Matters

- Soldier ownership (and later permissions) bind to **`peerID`, NOT nickname**.
- The Roster ("Состав") dropdown **shows the nickname, stores the `peerID`** → renaming never breaks assignments; reconnect re-binds by id.
- This is the "stable networking id" referenced in [research/01-tactical-action-pipeline](../research/01-tactical-action-pipeline.md) (`GeoTacUnitId` for soldiers, `peerID` for players).
- Ownership / nickname / permission data is **mod runtime-state, never written to the vanilla save** → §3.

---

## 2. Session Start & Sync (Save Transfer + Barrier)

> How a session goes from "all ready in lobby" to "everyone playing the same campaign".

### Trigger

- All players `Ready` → host presses `Play`.
- **Then** the native save-picker opens (host chooses the campaign save).
- Save selection happens **after** the lobby, not before.

### Save Transfer

- Host serializes the chosen save → sends to all clients.
- **Compression:** gzip via `System.IO.Compression.GZipStream` (built-in, zero deps).
  - JSON-like saves compress ~5-10× (5MB → ~0.5-1MB).
  - **VERIFY:** if PP already stores saves compressed/binary, skip re-compress, send as-is → [open questions](03-open-questions-sdk.md). (PP save formats `.zsav`/`.zjsav` are already gzip — see [research/04-serialization](../research/04-serialization.md).)
- Wire format: **length-prefixed blob**.
  - DirectIP/TCP → stream directly.
  - Steam P2P → **chunk** (reliable message size limit ~512KB-1MB).
- One-time transfer at start → size is not latency-critical.

### Barrier Sync (Ready-Gate)

- Standard co-op/RTS pattern. **Host = barrier coordinator.**
- Steps:
  1. Host broadcasts the save blob to all.
  2. Each client loads the save locally; on finish → sends `LOADED` ack to host.
  3. Host waits for `LOADED` from **all** clients (+ host loaded itself).
  4. **Loading screen stays up + input blocked** on everyone until the start signal.
  5. Host has all `LOADED` → broadcasts `BEGIN` → all unblock **simultaneously** → enter geoscape synced.
- **Input blocking:** keep the loading overlay on top, ignore input until `BEGIN`. Released for everyone at the same moment.
- **Timeout / failure (required):** a client that never sends `LOADED` (crash / slow disk) must not hang the barrier forever → host shows "waiting for X", offers kick / abort after N seconds.
- After `BEGIN` there are **no more barriers** — the action pipeline runs without waiting (see [01-design](01-design.md) §1). The per-turn ready-gate ([research/07-tactical-concurrency](../research/07-tactical-concurrency.md)) is a separate, lightweight barrier.

> The as-built `SessionManager` Ready-state coordination (`ClientReady` → `AllClientsReady`) implements this barrier; see [engine/01-networking-core](../engine/01-networking-core.md) §5.

### Loading Screen — Per-Player Progress

- Two phases shown per peer:
  - **Phase 1 — Download:** `bytes received / total` from our own transfer → exact %, no hacks.
  - **Phase 2 — Game load:** Harmony-hook PP's native loading-progress source → read value → report.
    - **VERIFY:** locate PP's loading progress class/field/event → [open questions](03-open-questions-sdk.md).
- Each peer sends `PROGRESS` packets to host; host aggregates + rebroadcasts.
- Display = player list with phase + bar:
  ```
  Host (you)   ██████████ ready ✓
  Player2      █████░░░░░ loading 52%
  Player3      ███░░░░░░░ downloading 31%
  ```
- **Throttle:** send progress every ~150ms or ~5% delta, not per-frame. Packets are tiny; may be **unreliable** (a dropped progress frame doesn't matter).

### Join Model (v1)

- **Join-at-start only.** No join-in-progress (fresh peer mid-campaign) in v1.
- **Reconnect is designed** (no longer deferred): host auto-saves live state via the game's own save system, then re-runs this same barrier mid-session → [research/09-disconnect-reconnect](../research/09-disconnect-reconnect.md). Avoids the custom live-state serializer that originally blocked it.
- Still deferred: join-in-progress for a brand-new peer → [open questions](03-open-questions-sdk.md).

---

## 3. Multiplayer State vs Vanilla Save

### Invariant: Vanilla Save Untouched

- Multiplayer data is **mod runtime-state**, held in memory — **never written into the PP save file**.
- Multiplayer data = soldier ownership, nicknames, permissions, peer list.
- **Why:**
  - The save stays 100% vanilla → loadable in an unmodded game, **zero corruption risk**.
  - Game updates that change the save format don't break our data.
  - No save-migration burden.

### Ownership Model

- Ownership = `soldierID → peerID` map (mod memory), persisted across sessions by `playerGUID` (§4).
- Bound to the **stable `peerID`** within a session, never the nickname (§1).
- Host re-assigns soldiers **each session** in the "Состав" / Roster UI:
  - Host loads save → assigns soldiers via per-soldier dropdown (shows nickname, stores `peerID`) → broadcast to clients.
- `ASSIGN_OWNER` message propagates changes (see [engine/02-transport-layer](../engine/02-transport-layer.md) message catalog).

### Permissions

- Same treatment: runtime-state, broadcast, not saved.
- Permission set + enforcement → [research/03-campaign-layer](../research/03-campaign-layer.md).

### Optional (NOT v1)

- Mod **sidecar file** beside the save (e.g. `savename.coop.json`) to remember last assignments across sessions.
- **Separate file**, never inside the PP save.
- v1 = manual re-assign each start; the sidecar is a later convenience.

---

## 4. Player Management UI, Permission Persistence & Cross-Session Identity

> Host's permission-assignment UI, how permissions survive across sessions without touching the save, and a transport-independent identity so the same human keeps their permissions no matter how they connect.

### Two Separate Host Surfaces

- **Roster ("Состав")** — host assigns **soldiers** to players only. Pure ownership: `soldierID → playerGUID` (§3).
- **Player Management** — a **separate** host-only screen for **permissions**: `playerGUID → flags`.
  - Entry point: in-game **ESC settings menu**.
  - Distinct interface from the Roster — ownership and permissions never share one panel.
- Permission flags (from [research/03-campaign-layer](../research/03-campaign-layer.md), incl. new): Research / Manufacturing / Bases / Recruitment / Aircraft / **Control Time** / Tactical / **Force End Turn** / Full Commander.
- Toggle → `PERMISSION{ playerGUID, flag, value }` broadcast → applied live.

### Cross-Session Identity (transport-independent)

- **Problem:** the session `peerID` (SteamID / socket-id / relay-id) is stable only **within** a session. A player who joins via Steam one session and DirectIP the next would look like a different person → permissions lost. Mixed-transport groups make this worse.
- **Rule: identity must never be derived from transport.**
- **Three identity levels:**
  - **`playerGUID`** — persistent, **client-generated random**, stored in the client's mod config, **transport-independent**. Sent in the `JOIN` payload over *any* transport. The **only** persistence key.
  - **`peerID`** — per-session transport handle. Stable within a session; used for routing + in-session ownership binding (§1, §3).
  - **`nickname`** — mutable display label; fuzzy-fallback match only.
- **Bridge:** the `JOIN` handshake maps `peerID ↔ playerGUID` at connect. In-session work uses `peerID`; persistence uses `playerGUID`.
- **SteamID is NOT the identity key** — used only for convenience (auto-nickname, friend invite). A Steam user still carries their own client-generated `playerGUID`.
- **Result:** mixed transports (Steam + DirectIP + STUN-relay) and switching transport between sessions both "just work" — the GUID rides in the payload, the pipe is irrelevant (the transport-agnostic core, [engine/02-transport-layer](../engine/02-transport-layer.md)).

### Permission Persistence (not in the save)

- Permission table persisted as a **mod config file** (e.g. `<mod-config>/coop-perms.json`), **never** inside the PP save. Same invariant as §3.
- **Lives on the host** — clients only hold their own `playerGUID`. The table is host state.
- Each entry: `{ playerGUID, lastNickname, flags }`.

### Reconciliation on Session Start

- For each **connected** peer:
  - Match by **`playerGUID`** → restore flags; update stored nickname to current. (Rename never breaks it — the GUID is stable.)
  - No GUID match → fallback match by **nickname** → restore flags + rebind to the new GUID.
  - No match at all → new player → flags = **default** (reset / none).
- **Absent players** (in table, did not connect this session):
  - **Keep** their entry — do **not** delete, do **not** reset.
  - Shown greyed / offline in Player Management; their saved permissions wait for their return.
- "Reset" therefore applies **only** to connected-but-unmatched peers (effectively new joiners), never to absent ones.

### Lost-GUID Recovery (player deleted/reinstalled the mod)

- Only the client's **own key** is lost — the permission **table lives on the host**, intact.
- Safety nets:
  - **Nickname fallback** (above) auto-recovers if the nickname is unchanged → flags restored, rebound to the new GUID.
  - **Manual rebind:** in Player Management the host can attach a connected player to an existing offline entry to reclaim its saved permissions.
  - **Update ≠ delete:** store the GUID in an **update-safe** location (per-user config dir, not overwritten by a mod update). Only a deliberate full uninstall loses it.
- **Low stakes:** worst case the host re-ticks the boxes once. No save / progress is ever affected.

### Trust Note

- `playerGUID` sits in a client-side file → copyable / spoofable → impersonation possible in a friendly co-op.
- Acceptable: the host is authoritative and grants all permissions manually; stakes are low (no competitive integrity at risk). Logged in [open questions](03-open-questions-sdk.md).

### Open Questions (SDK)

- PP stable per-user config dir that survives a mod **update** (appdata / mod-config dir) — where to store `playerGUID` and `coop-perms.json` → [open questions](03-open-questions-sdk.md).

---

## Related

- Action-sync core, class diagrams, patch table → [01-design](01-design.md).
- Permission model + flag set + enforcement → [research/03-campaign-layer](../research/03-campaign-layer.md).
- Connection-menu UI injection (Network Game button, autodetect join box) → [engine/03-harmony-patches](../engine/03-harmony-patches.md).
- Transport, message envelope, message-type catalog → [engine/02-transport-layer](../engine/02-transport-layer.md).
- Disconnect / orphan takeover / reconnect (reuses the §2 barrier) → [research/09-disconnect-reconnect](../research/09-disconnect-reconnect.md).
- Geoscape concurrency (shared clock, forced transitions) → [research/08-geoscape-concurrency](../research/08-geoscape-concurrency.md).
