# Phoenix Point Cooperative Multiplayer Mod

![License: PolyForm-NC-1.0.0](https://img.shields.io/badge/license-PolyForm--NC--1.0.0-blue)

## Overview

A cooperative campaign mod where multiple players share a single Phoenix Point campaign.
The host runs authoritative game logic; clients send actions and receive validated results.

## Documents

### Engine — as-built implementation

The current source of truth for how the mod actually works today.

| File | Description |
|------|-------------|
| `docs/engine/01-networking-core.md` | `NetworkEngine` singleton + lifecycle, message routing, `PacketType`, binary message formats, `SessionManager` (heartbeat, ready-state), reliable message flow |
| `docs/engine/02-transport-layer.md` | `ITransport` + three transports (Steam P2P / Direct TCP / STUN UDP), comparison table, message envelope + message-type catalog by phase, reliability |
| `docs/engine/03-harmony-patches.md` | Patch table (P0–P3 tactical, C1–C5 campaign), runtime type resolution, per-patch detail, connection-menu UI injection, native Load-screen intercept for the co-op "Choose save" |

### Research — source-dive & reference material

| File | Description |
|------|-------------|
| `docs/research/00-current-state.md` | Status note: as-built state, branch HEAD, built-vs-stub, active/deferred work, in-game test status |
| `docs/research/01-tactical-action-pipeline.md` | Tactical combat action flow, turn system, ability execution |
| `docs/research/02-rng-analysis.md` | RNG implementation, combat calculations, randomness sources |
| `docs/research/03-campaign-layer.md` | Research, manufacturing, base, aircraft, soldier systems |
| `docs/research/04-serialization.md` | Save/load system, state snapshots, networking implications |
| `docs/research/05-steam-networking.md` | Steam integration, Facepunch.Steamworks, networking approach |
| `docs/research/06-harmony-patterns.md` | Harmony patch patterns from existing mods |
| `docs/research/07-tactical-concurrency.md` | Simultaneous play, same-tile conflict, host receipt-order authority, turn-end ready-gate |
| `docs/research/08-geoscape-concurrency.md` | Two state layers, shared clock, events, forced state transitions |
| `docs/research/09-disconnect-reconnect.md` | Orphan takeover, reconnect resync, host-loss, toasts |
| `docs/research/10-messagebox-input-prompt.md` | Native message-box / text-input prompt API for in-game co-op dialogs |
| `docs/research/11-console-hotkey-suppress.md` | Console / hotkey suppression seam |
| `docs/research/12-time-flow-and-sync-seams.md` | Geoscape time-flow API + host-authoritative clock sync seams |

### Design history / lineage

> These docs describe the **original** architecture and design intent. They are kept for lineage and rationale, **not** as current truth — for what is actually built, read the `docs/engine/*` docs above.

| File | Description |
|------|-------------|
| `docs/specs/01-design.md` | Full original design: architecture, class diagrams, patch locations, roadmap (**superseded** by the engine docs) |
| `docs/specs/02-session-lifecycle-and-player-management.md` | Lobby & peer identity, session-start barrier, ownership model, player-management/permission design |
| `docs/specs/03-open-questions-sdk.md` | Items needing PP source/SDK to verify; deferred scope (join-in-progress, host migration) |

## License

This project is licensed under the **PolyForm Noncommercial License 1.0.0** — noncommercial use only, attribution required. See the [`LICENSE`](LICENSE) file for the full terms.

**DISCLAIMER:** Using this mod requires a legally owned copy of Phoenix Point. It is built on the official Snapshot Games modding framework. It is not affiliated with or endorsed by Snapshot Games.
