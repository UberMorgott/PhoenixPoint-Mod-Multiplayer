# 02 — Tactical Combat Design

[← Index](../README.md)

## Player Phase

- Multiple players may act **simultaneously**.
- Each player may only control **assigned** soldiers.
- Local interface elements are **NOT** synchronized: camera, UI state, inventory windows, unit selection, etc.
- Only **world-changing** actions are synchronized.

## Synchronize

- Move
- Shoot
- Reload
- Ability usage
- Inventory transfers
- Interaction with world objects

## Do NOT Synchronize

- Camera movement
- Unit selection
- Cursor position
- UI navigation

## Validation

- Host **validates every action** before execution.
- Validation includes ownership check (does this player own this soldier?) + legality check (is the action valid in current state?).
- Entry points + Harmony intercept locations → [05 — Investigation: Tactical](05-investigation-tactical.md).
