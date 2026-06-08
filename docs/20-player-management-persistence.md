# 20 — Player Management UI, Permission Persistence & Cross-Session Identity

[← Index](../README.md)

> Host's permission-assignment UI, how permissions survive across sessions without touching the save, and a transport-independent identity so the same human keeps their permissions no matter how they connect. Permission model → [03](03-campaign-layer.md); ownership/identity baseline → [12](12-lobby.md), [15](15-mp-state-vs-save.md).

## Two Separate Host Surfaces

- **Roster ("Состав")** — host assigns **soldiers** to players only. Pure ownership: `soldierID → playerGUID`. → [15](15-mp-state-vs-save.md).
- **Player Management** — a **separate** host-only screen for **permissions**: `playerGUID → flags`.
  - Entry point: in-game **ESC settings menu**.
  - Distinct interface from Roster — ownership and permissions never share one panel.
- Permission flags (from [03](03-campaign-layer.md), incl. new): Research / Manufacturing / Bases / Recruitment / Aircraft / **Control Time** / Tactical / **Force End Turn** / Full Commander.
- Toggle → `PERMISSION{ playerGUID, flag, value }` broadcast → applied live.

## Cross-Session Identity (transport-independent)

- **Problem:** session `peerID` (SteamID / socket-id / relay-id) is stable only **within** a session. A player who joins via Steam one session and DirectIP the next would look like a different person → permissions lost. Mixed-transport groups make this worse.
- **Rule: identity must never be derived from transport.**
- **Three identity levels:**
  - **`playerGUID`** — persistent, **client-generated random**, stored in the client's mod config, **transport-independent**. Sent in the `JOIN` payload over *any* transport. The **only** persistence key.
  - **`peerID`** — per-session transport handle. Stable within a session; used for routing + in-session ownership binding ([12](12-lobby.md), [15](15-mp-state-vs-save.md)).
  - **`nickname`** — mutable display label; fuzzy-fallback match only.
- **Bridge:** the `JOIN` handshake maps `peerID ↔ playerGUID` at connect. In-session work uses `peerID`; persistence uses `playerGUID`.
- **SteamID is NOT the identity key** — used only for convenience (auto-nickname, friend invite). A Steam user still carries their own client-generated `playerGUID`.
- **Result:** mixed transports (Steam + DirectIP + STUN-relay) and switching transport between sessions both "just work" — the GUID rides in the payload, the pipe is irrelevant ([14](14-transport-protocol.md) transport-agnostic core).

## Permission Persistence (not in the save)

- Permission table persisted as a **mod config file** (e.g. `<mod-config>/coop-perms.json`), **never** inside the PP save. Same invariant as [15](15-mp-state-vs-save.md).
- **Lives on the host** — clients only hold their own `playerGUID`. The table is host state.
- Each entry: `{ playerGUID, lastNickname, flags }`.

## Reconciliation on Session Start

- For each **connected** peer:
  - Match by **`playerGUID`** → restore flags; update stored nickname to current. (Rename never breaks it — GUID is stable.)
  - No GUID match → fallback match by **nickname** → restore flags + rebind to the new GUID.
  - No match at all → new player → flags = **default** (reset / none).
- **Absent players** (in table, did not connect this session):
  - **Keep** their entry — do **not** delete, do **not** reset.
  - Shown greyed / offline in Player Management; their saved permissions wait for their return.
- "Reset" therefore applies **only** to connected-but-unmatched peers (effectively new joiners), never to absent ones.

## Lost-GUID Recovery (player deleted/reinstalled the mod)

- Only the client's **own key** is lost — the permission **table lives on the host**, intact.
- Safety nets:
  - **Nickname fallback** (above) auto-recovers if the nickname is unchanged → flags restored, rebound to the new GUID.
  - **Manual rebind:** in Player Management the host can attach a connected player to an existing offline entry to reclaim its saved permissions.
  - **Update ≠ delete:** store the GUID in an **update-safe** location (per-user config dir, not overwritten by a mod update). Only a deliberate full uninstall loses it.
- **Low stakes:** worst case the host re-ticks the boxes once. No save / progress is ever affected.

## Trust Note

- `playerGUID` sits in a client-side file → copyable / spoofable → impersonation possible in a friendly co-op.
- Acceptable: host is authoritative and grants all permissions manually; stakes are low (no competitive integrity at risk). Logged in [16](16-open-questions-sdk.md).

## Open Questions (SDK)

- PP stable per-user config dir that survives a mod **update** (appdata / mod-config dir) — where to store `playerGUID` and `coop-perms.json` → [16](16-open-questions-sdk.md).

## Related

- Permission model + flag set → [03 — Campaign Layer](03-campaign-layer.md).
- Ownership model + vanilla-save invariant → [15 — MP State vs Save](15-mp-state-vs-save.md).
- Peer identity baseline (session-level) → [12 — Lobby](12-lobby.md).
- Geoscape permission usage (time, deploy) → [19 — Geoscape Concurrency](19-geoscape-concurrency.md).
