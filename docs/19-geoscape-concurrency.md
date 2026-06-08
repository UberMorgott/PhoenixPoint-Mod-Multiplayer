# 19 — Geoscape Concurrency & Global State Sync

[← Index](../README.md)

> How the real-time-with-pause geoscape works with multiple simultaneous players: shared clock, event handling, and forced state transitions (e.g. a player deep in a base menu when a mission launches). Campaign permission model → [03](03-campaign-layer.md); UI + persistence → [20](20-player-management-persistence.md).

## Two Layers of State

- **Local view state — NOT synced** (extends [02](02-tactical-combat.md)):
  - Camera position, which base sub-panel is open, scroll, hover, cursor.
  - Each player browses independently.
- **Global game state — host-authoritative, SYNCED:**
  - Time flow (paused / speed).
  - Campaign "mode": free-geoscape / event-modal / mission-briefing / in-tactical.
- Refinement of [02](02-tactical-combat.md): the *view* is never synced, but a *game-mode transition* IS pushed to everyone.

## Shared Clock & Time Control

- One authoritative clock on host. Clients display host's clock, never run their own.
- **Control Time** permission gates who may pause / change speed → [03](03-campaign-layer.md), [20](20-player-management-persistence.md).
- **Conflict rule = last writer wins** (consistent with host receipt-order, [17](17-tactical-concurrency.md)):
  - Player with permission changes time → request to host → host sets clock → `TIME_STATE` broadcast → all match.
  - Two near-simultaneous commands → host queue order decides; latest applied.
- **Auto-pause override:** a decision-required event forces pause on host regardless of player commands → broadcast.

## Events

- All geoscape events fire on **host** (authoritative event generation, [01](01-architecture-authoritative-host.md)) → broadcast `EVENT`.
- **Event classes:**
  - **Informational** (research done, item built, soldier healed) → toast/notification. No state transition. A player inside a menu just sees a toast.
  - **Decision / global** (mission available, faction popup, base assault) → forces a state transition for everyone (see below).
- **Camera-center on event:**
  - Only for clients currently in **free-geoscape** view → center camera on the event site.
  - Clients inside a menu → notification only, not yanked (unless the event is decision/global).

## Forced State Transition (the "yank from base menu" case)

- Single campaign = one global state machine; geoscape and tactical cannot run at once → the **whole group** transitions together.
- Trigger example: a player with Aircraft/Tactical permission launches a deployment.
  - Initiator → `ACTION` to host → host validates permission → host enters briefing mode.
  - Host broadcasts `STATE_ENTER{ briefing, missionId }`.
  - Each client: gracefully close current local menu → **force-pop** its local UI stack → push the briefing state.
- Mission ends → `STATE_ENTER{ geoscape }` → everyone returns.
- **Who gets pulled:** everyone. A player in the base menu is yanked into briefing. v1 = **hard yank**, no grace prompt.
- **Data-loss concern:** PP base edits apply immediately (no transactional "unsaved" buffer expected) → yank is safe.
  - ⚠️ **VERIFY (SDK):** confirm base UI has no uncommitted edit buffer that a yank would lose → [16](16-open-questions-sdk.md).

## Rule Summary

```
time change  → permission check → host sets clock → TIME_STATE broadcast (last-writer wins)
event fires  → host → EVENT broadcast
                 ├ informational → toast (no transition); camera-center only if in geoscape view
                 └ decision/global → STATE_ENTER → force-pop local UI on ALL clients → new mode
```

- Authority always host. View stays local; game-mode + clock are global.

## New Message Types

- `TIME_STATE{ paused, speed }`, `EVENT{ class, payload }`, `STATE_ENTER{ mode, args }` → added to [14](14-transport-protocol.md).

## Open Questions (SDK)

- PP global state-machine + UI state stack push/pop API (to drive forced transitions).
- Geoscape event hook points (where to intercept event generation for `EVENT`).
- Time-flow API (pause/speed) to drive the shared clock.
- → all logged in [16](16-open-questions-sdk.md).

## Related

- Permission model + set → [03 — Campaign Layer](03-campaign-layer.md).
- Player Management UI, permission persistence, cross-session identity → [20](20-player-management-persistence.md).
- Tactical-side concurrency + turn-end → [17](17-tactical-concurrency.md).
