# 11 — Connection Menu & UI

[← Index](../README.md)

> Native-styled menu for entering multiplayer. Brainstormed; UI-binding specifics need SDK → [16 — Open Questions (SDK)](16-open-questions-sdk.md).

## Main Menu Injection

- Add button **"Сетевая игра" / "Network Game"** above **"Play"** in the main menu.
- Implemented via **Harmony** injection into PP's main-menu UI state.
- **Native look for free:** clone an existing menu button prefab → inherits the game's fonts, styling, hover/click behavior.
- No custom skinning — reuse game UI so it's indistinguishable from vanilla.

## "Network Game" Screen

- Cloned style of the existing **Mods / Options** screen.
- Two entries:
  - `Create Game` → opens **Lobby (host)** immediately (no save-picker yet — see [13](13-session-start-sync.md)).
  - `Join Game` → opens **Join** screen.

## Join Screen

- Three inputs, all feed the same `ITransport` ([14](14-transport-protocol.md)):
  - `Steam: Friends` → Steam overlay invite (live in a later phase).
  - **Single text input** with **autodetect**:
    - Matches `IP[:port]` / hostname → route to **DirectIP** transport.
    - Otherwise → treat as **Steam connection code**.
  - `Connect` button → joins **Lobby (client)**.
- One box, auto-routing → user pastes either an IP or a code, mod figures out which.

## Navigation

- All screens go through PP's native UI state stack (push/pop), consistent with vanilla menu flow.
- Back/escape behaves like other native sub-screens.

## Related

- Lobby contents + peer identity → [12 — Lobby](12-lobby.md).
- What happens after `Play` → [13 — Session Start & Sync](13-session-start-sync.md).
