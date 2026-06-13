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
| [superpowers/specs/2026-06-13-coop-state-replication-design.md](superpowers/specs/2026-06-13-coop-state-replication-design.md) | **Host-authoritative geoscape state replication (SD-AIDR)** — slaved-clock spectator-drive + native InstanceData-diff stream; supersedes per-action StartTravel intercept; verified seams (clock C1/travel-render C3/entity-lifecycle C7/input-funnel C9/reload C14), 13-producer client-suppress set, 3 travel emitters, launch-loop gating, `0x35`/`0x36` packets, 5-increment rollout |
| [superpowers/plans/2026-06-13-replication-increment1-client-inert.md](superpowers/plans/2026-06-13-replication-increment1-client-inert.md) | **SD-AIDR INC-1** plan — client inert + slaved-clock travel mirror: pure auditable 13-producer table (TDD), `ClientGeoSimSuppressPatch` (each producer → `NextUpdate.Never`, client-only), `ClientTravelEmitterSuppressPatch` (3 `GeoVehicle` emitters, render-only), verify `0x34` clock advances. 5 tasks; patches build-verified + in-game 2-instance checkpoint |
| [superpowers/plans/2026-06-13-replication-increment2-entity-lifecycle.md](superpowers/plans/2026-06-13-replication-increment2-entity-lifecycle.md) | **SD-AIDR INC-2** plan — entity lifecycle `0x36 GeoEntityOp` (reliable): pure `GeoEntityOpCodec` (4 op-types, TDD) + `EntityReplicationScope` guard (TDD), `HostEntityOpBroadcastPatch` (host postfix on `CreateVehicle`/`CreateVehicleAtPosition`/`UnregisterVehicle`/`DestroySite`), `ClientEntityOpApplier` (native `CreateVehicle` lifecycle replay + `VehicleID`/`_lastVehicleIndex` reconcile). Fixes host-created "vehicle N not found"; SiteCreated + arrival authority deferred to INC-3. 8 tasks; codec TDD + patches build-verified + in-game checkpoint |
| [superpowers/specs/2026-06-13-coop-state-replication-inc3-geostatediff.md](superpowers/specs/2026-06-13-coop-state-replication-inc3-geostatediff.md) | **SD-AIDR INC-3** design — generic host→client geoscape state mirror over a single scope-keyed packet `0x35 GeoStateDiff` (generalizes the `0x34` clock mirror from one Timing object to N entities). Host walks all factions × vehicles, native `RecordInstanceData` snapshot → diff → broadcasts only CHANGED records (UNRELIABLE continuous pos/rot/range + RELIABLE discrete Travelling/CurrentSite/DestinationSites). Client is a PURE mirror keyed by stable `(factionGuid,VehicleID)` (THE live movement-bug fix — replaces Phoenix-only `FindVehicleById`), seq-guarded, applied under `EntityReplicationScope`. Scope enum `Vehicle=1`(INC-3a)/Site/MarketPrice(INC-3b)/Faction(INC-3c)/`Checksum=255`. Retires per-action `StartTravel` command-replay, keeps client→host input relay. CRC detector + targeted single-entity self-heal (full save-reload = INC-5) |
| [superpowers/plans/2026-06-13-replication-increment3a-vehicle-state-mirror.md](superpowers/plans/2026-06-13-replication-increment3a-vehicle-state-mirror.md) | **SD-AIDR INC-3a** plan — all-factions vehicle state mirror (first `0x35` scope; unblocks the locked host→client movement bug): pure `GeoStateScope` enum + `GeoStateDiffCodec` (generic scope/seq/mask envelope, TDD) + `GeoVehicleStateDiffer` (epsilon diff + monotonic per-identity seq + continuous/discrete channel split, TDD); `GeoBridge` `FindVehicleByFactionAndId`(bug fix)/`RecordVehicleState`/`ApplyVehicleState`(light setters)/`ApplyVehicleStateFull`(`ProcessInstanceData`); `GeoStateSyncBroadcaster` (host snapshot+diff, ticked from `NetworkEngine.Update`); `BroadcastGeoStateDiff` + route `0x35` → `ClientGeoStateApplier`; RETIRE `StartTravel` command-replay, KEEP client→host relay. Two-phase DIAG (Task 0 all-factions `DescribeVehicles` now / Task 13 revert `b753111`+`fbfb3f9` after verify). 14 tasks; pure cores TDD + engine seams build-verified + in-game GATE (host moves Phoenix Manticore + NJ Thunderbird → both mirror) |

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
