# 15 — Multiplayer State vs Vanilla Save

[← Index](../README.md)

## Invariant: Vanilla Save Untouched

- Multiplayer data is **mod runtime-state**, held in memory — **never written into the PP save file**.
- Multiplayer data = soldier ownership, nicknames, permissions, peer list.
- **Why:**
  - Save stays 100% vanilla → loadable in an unmodded game, **zero corruption risk**.
  - Game updates that change save format don't break our data.
  - No save-migration burden.

## Ownership Model

- Ownership = `soldierID → peerID` map (mod memory).
- Bound to **stable `peerID`**, never nickname → [12 — Peer Identity](12-lobby.md).
- Host re-assigns soldiers **each session** in the "Состав" / Roster UI:
  - Host loads save → assigns soldiers via per-soldier dropdown (shows nickname, stores peerID) → broadcast to clients.
- `ASSIGN_OWNER` message propagates changes ([14](14-transport-protocol.md)).

## Permissions

- Same treatment: runtime-state, broadcast, not saved.
- Permission set + enforcement → [03 — Campaign Layer](03-campaign-layer.md).

## Optional (NOT v1)

- Mod **sidecar file** beside the save (e.g. `savename.coop.json`) to remember last assignments across sessions.
- **Separate file**, never inside the PP save.
- v1 = manual re-assign each start; sidecar is a later convenience.
