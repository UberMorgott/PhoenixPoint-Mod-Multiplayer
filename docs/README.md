# Multipleer — Cooperative Multiplayer Mod (Documentation)

Local docs are the **single source of truth** for the Multipleer cooperative-multiplayer mod for Phoenix Point.

- A **cooperative campaign** mod built on the official **SDK** + **Harmony** patches.
- **Not** a traditional turn-based PvP / wait-for-each-other mode.
- One shared campaign; multiple players co-control a single faction by **ownership + permissions**.
- **Authoritative host** model (not lockstep): the host runs all logic + RNG + AI; clients send actions and reproduce validated results.

> **Where are we now?** → [research/00-current-state.md](research/00-current-state.md) — branch HEAD, what's built vs stub, undocumented-but-shipped working-tree changes, active next step, deferred scope, in-game test status. Read this first.

## Document Map

### `specs/` — Design

| Doc | Scope |
|-----|-------|
| [specs/01-design.md](specs/01-design.md) | Project goal & co-op concept; authoritative-host architecture; class diagrams; network protocol; Harmony patch plan; risk + desync assessment; implementation order; PoC roadmap; key file refs |
| [specs/02-session-lifecycle-and-player-management.md](specs/02-session-lifecycle-and-player-management.md) | Lobby & peer identity; session-start save transfer + barrier sync; MP state vs vanilla save (ownership model); player-management UI + permission persistence + transport-independent `playerGUID` |
| [specs/03-open-questions-sdk.md](specs/03-open-questions-sdk.md) | Items needing PP source/SDK to verify; deferred scope (join-in-progress, host migration) |

### `research/` — Source-Dive & Concurrency Design

| Doc | Scope |
|-----|-------|
| [research/01-tactical-action-pipeline.md](research/01-tactical-action-pipeline.md) | Tactical class hierarchy, action execution flow, `ActivateAbility` chokepoint, turn system, stable actor IDs, candidate Harmony patch sites |
| [research/02-rng-analysis.md](research/02-rng-analysis.md) | RNG sources (`SharedData.Random` + `UnityEngine.Random`), combat determinism, host-only-randomness decision, hidden RNG risks |
| [research/03-campaign-layer.md](research/03-campaign-layer.md) | Campaign subsystem entry points (research/manufacturing/base/aircraft/soldier), permission injection points, authoritative `CampaignPermission` set |
| [research/04-serialization.md](research/04-serialization.md) | Save/load engine, save file structure, `RecordInstanceData` snapshots, network-sync implications, snapshot use cases (join/reconnect/divergence) |
| [research/05-steam-networking.md](research/05-steam-networking.md) | Facepunch.Steamworks availability, P2P/matchmaking APIs, Steam-relay-vs-direct-IP recommendation, transport-agnostic stance |
| [research/06-harmony-patterns.md](research/06-harmony-patterns.md) | Reusable Harmony patterns from existing mods: lifecycle, patch types, internal-type patching, field access, event subscription |
| [research/07-tactical-concurrency.md](research/07-tactical-concurrency.md) | Simultaneous play, same-tile conflict, host receipt-order authority, destination reservation, turn-end ready-gate |
| [research/08-geoscape-concurrency.md](research/08-geoscape-concurrency.md) | Two state layers, shared clock, events (informational vs decision), forced state transitions (yank-to-briefing) |
| [research/09-disconnect-reconnect.md](research/09-disconnect-reconnect.md) | Orphan takeover, reconnect resync via the start-barrier, mid-battle-save caveat, host-loss, toasts |
| [research/10-messagebox-input-prompt.md](research/10-messagebox-input-prompt.md) | Native message-box / text-input prompt API for in-game co-op dialogs |
| [research/11-console-hotkey-suppress.md](research/11-console-hotkey-suppress.md) | Console / hotkey suppression seam |
| [research/12-time-flow-and-sync-seams.md](research/12-time-flow-and-sync-seams.md) | Geoscape time-flow API + host-authoritative clock sync seams (grounding for Stage-2 time sync): clock owner, pause/speed API, auto-pause sites, `RecordInstanceData`/`ProcessInstanceData` settability, hook points, risks R1–R8 |
| [research/00-current-state.md](research/00-current-state.md) | **Status note** — as-built state, branch HEAD, built-vs-stub, active/deferred work, in-game test status |

### `superpowers/` — Approved Designs & Staged Plans

| Doc | Scope |
|-----|-------|
| [superpowers/specs/2026-06-12-geoscape-command-sync-design.md](superpowers/specs/2026-06-12-geoscape-command-sync-design.md) | Host-authoritative command-result relay — architecture index, module map (CommandRelay/Codec/HostArbiter/ClientApplier/InterceptRegistry/PermissionGate), broad-intercept registry, staging (Stage 1 commands / Stage 2 time / Stage 3 events) |
| [superpowers/plans/2026-06-12-geoscape-command-sync-stage1.md](superpowers/plans/2026-06-12-geoscape-command-sync-stage1.md) | Stage-1 implementation plan — command actions + real per-GUID permissions, first vertical proof `GeoVehicle.StartTravel`. **(Implemented; see 00-current-state.)** |
| [superpowers/plans/2026-06-13-time-sync-stage2-increment1.md](superpowers/plans/2026-06-13-time-sync-stage2-increment1.md) | **Active** — Stage-2 Increment-1 host-authoritative time: `SetTimeState` action, client pause/speed intercepts, client hourly-sim suppression, continuous `0x34` clock mirror |
| [superpowers/specs/2026-06-12-coop-loading-screen-overlay-design.md](superpowers/specs/2026-06-12-coop-loading-screen-overlay-design.md) + [plans/…-coop-loading-screen-overlay.md](superpowers/plans/2026-06-12-coop-loading-screen-overlay.md) | Co-op loading-screen roster overlay (separate milestone) |

### `engine/` — As-Built Implementation

| Doc | Scope |
|-----|-------|
| [engine/01-networking-core.md](engine/01-networking-core.md) | `NetworkEngine` singleton + lifecycle, message routing, `PacketType`, binary message formats, `SessionManager` (heartbeat, ready-state), reliable message flow |
| [engine/02-transport-layer.md](engine/02-transport-layer.md) | `ITransport` + three transports (Steam P2P / Direct TCP / STUN UDP), comparison table, message envelope + message-type catalog by phase, reliability |
| [engine/03-harmony-patches.md](engine/03-harmony-patches.md) | Patch table (P0–P3 tactical, C1–C5 campaign), runtime type resolution, per-patch detail, connection-menu UI injection design; **native Load-screen intercept** for the co-op "Choose save" (open native `UIStateHomeLoadGame` + Prefix `UIModuleSaveGame.OnLoadGamePressed` to capture-and-return without loading) |

> **Command-sync layer (as-built code, no dedicated engine doc yet):** `src/Network/CommandSync/` — the host-authoritative command relay built from the [geoscape-command-sync design](superpowers/specs/2026-06-12-geoscape-command-sync-design.md). Module map + line refs live in [research/00-current-state.md](research/00-current-state.md).

### `diagrams/`

Reserved for diagram assets (currently empty).

## Quick Reference

- **Source of truth:** host only. Clients send actions, receive validated results; they reproduce, never recompute.
- **Sync:** world-changing actions only (move/shoot/reload/ability/inventory/world-interact). **Never sync:** camera, selection, cursor, UI navigation.
- **Transport:** pluggable `ITransport` core (transport-agnostic). Steam P2P primary; DirectIP for LAN/dev (loopback solo test); STUN UDP for Steam-less direct P2P.
- **Connection input:** one box, autodetect IP vs Steam code; + Steam friends invite.
- **Lobby-first:** create → lobby → all ready → host picks save → gzip transfer → **barrier sync** (all `LOADED` → `BEGIN`) → play. On-disk save = single source of truth at start.
- **Identity:** persistent client-generated `playerGUID` (the only persistence key) + per-session `peerID` + mutable nickname; ownership/permissions bind to `playerGUID`/`peerID`, never the nickname.
- **Vanilla save untouched:** ownership/nicks/permissions = mod runtime-state, reconciled each session, never written into the PP save.
- **Top desync risk:** RNG + hidden game systems → [research/02-rng-analysis](research/02-rng-analysis.md).
- **Blocked on SDK:** UI injection, Steam availability, save/load API, loading-progress hook, 2nd-instance, mid-battle save → [specs/03-open-questions-sdk](specs/03-open-questions-sdk.md).
