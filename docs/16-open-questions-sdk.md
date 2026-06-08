# 16 — Open Questions (need PP source / SDK)

[← Index](../README.md)

> This machine has **no PP sources, SDK, or mods**. Items below cannot be verified now — resolve once SDK is available. Do **not** assume from memory.

## Steam Networking

- Confirm Steam P2P is usable by a mod under the **game's App ID** with **no developer "activation"** required.
  - Expected (general Steamworks): friend-to-friend P2P / relay works for any owner; only dedicated-server SDR-with-tickets needs special setup.
- Which Steamworks bindings does PP link (Steamworks.NET? Facepunch?) → mod calls into the same.
- Relevant: [14 — Transport](14-transport-protocol.md).

## Native UI Injection

- PP main-menu UI architecture: state class to patch (e.g. `UIStateMainMenu`?), button prefab to clone for native fonts/styling.
- UI state stack push/pop API for sub-screens (Network Game / Join / Lobby).
- Relevant: [11 — Connection Menu](11-connection-menu-ui.md).

## Local Dev / Testing

- Does PP enforce a **single-instance mutex** (blocks a 2nd process on one PC)?
  - If yes → workaround: launch `.exe` directly (bypass Steam) and/or a second game-folder copy.
- Needed for loopback `127.0.0.1` solo testing → [04 — Networking](04-networking.md), [14 — Transport](14-transport-protocol.md).

## Save System

- PP save format: already compressed/binary on disk? (decides gzip vs send-as-is).
- Save/load API to invoke for serialize (host) + load (client).
- Relevant: [08 — Serialization](08-serialization.md), [13 — Session Start](13-session-start-sync.md).

## Loading Progress Hook

- Locate PP's loading-progress source (class / float field / event / coroutine) to Harmony-hook for **phase-2 load %**.
- Relevant: [13 — Session Start](13-session-start-sync.md).

## Tactical / Campaign Entry Points

- All Harmony intercept sites for actions + subsystem permission checks remain open.
- Relevant: [05 — Investigation: Tactical](05-investigation-tactical.md), [06 — Investigation: Campaign](06-investigation-campaign.md).

## Mid-Battle Save & Reconnect

- Reconnect-resync needs a **save while in a tactical mission** → [21](21-disconnect-reconnect.md).
- Game supports mid-battle save, but **possibly via an experimental mod, not vanilla** — confirm the source.
  - If mod-provided → depend on that mod, or implement own tactical snapshot → [08](08-serialization.md).
  - If unavailable → v1 fallback: reconnect only on geoscape ([21](21-disconnect-reconnect.md)).
- Relevant: [13 — Session Start](13-session-start-sync.md), [21 — Disconnect & Reconnect](21-disconnect-reconnect.md).

## Persistent Config Location

- PP stable per-user config dir that survives a mod **update** (appdata / mod-config dir) — for `playerGUID` + `coop-perms.json` → [20](20-player-management-persistence.md).

## Deferred Scope (decided out of v1)

- Join-in-progress for a **brand-new** peer (mid-campaign / mid-mission). (Reconnect of a known peer is designed → [21](21-disconnect-reconnect.md).)
- **Host migration / re-host** when the host drops (v1 = session ends) → [21](21-disconnect-reconnect.md).
- Persistent ownership sidecar file ([15](15-mp-state-vs-save.md)).
