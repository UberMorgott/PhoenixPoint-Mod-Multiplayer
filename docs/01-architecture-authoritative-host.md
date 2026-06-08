# 01 — Multiplayer Architecture: Authoritative Host

[← Index](../README.md)

## Authoritative Host

- Host is the **only source of truth**.
- Host responsibilities:
  - Executes all game logic.
  - Executes all random events.
  - Calculates all combat results.
  - Controls AI.
  - Generates missions.
  - Owns campaign state.

## Client Role

- Send player actions to host.
- Receive validated actions/results from host.
- Reproduce game state locally.

## Design Stance

- **Avoid lockstep simulation** if possible.
- Objective: minimize desync caused by RNG + hidden game systems.
- Authoritative model chosen specifically to keep RNG + AI + event generation on a single deterministic node (host).
- Related: desync risk analysis → [07 — Randomness](07-randomness.md); sync pipeline → [09 — Sync Strategy](09-sync-strategy.md).
