# Multiplayer — Cooperative Multiplayer Mod (Documentation)

Local docs are the **single source of truth** for the Multiplayer cooperative-multiplayer mod for Phoenix Point.

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
| [superpowers/2026-07-03-multiplayer-fable-rereview-fixes-handoff.md](superpowers/2026-07-03-multiplayer-fable-rereview-fixes-handoff.md) | **Session handoff 2026-07-03** — Fable re-review fix wave `fc2c8b5`→`ace79ae` (native-advance reflection ROOT CAUSE `Base.UI.GeoscapeModulesData`, host-resolved event texts / VoidOmen fix, correlator hardening, `0x6B` advance-request, wallet diag, sim-freeze anchor relay); deployed DLL `15f9a08e…`, in-game verified (wallet converges) vs pending verification list, known open issues, next arcs |
| [superpowers/plans/2026-06-13-replication-increment3a-vehicle-state-mirror.md](superpowers/plans/2026-06-13-replication-increment3a-vehicle-state-mirror.md) | **SD-AIDR INC-3a** plan — all-factions vehicle state mirror (first `0x35` scope; unblocks the locked host→client movement bug): pure `GeoStateScope` enum + `GeoStateDiffCodec` (generic scope/seq/mask envelope, TDD) + `GeoVehicleStateDiffer` (epsilon diff + monotonic per-identity seq + continuous/discrete channel split, TDD); `GeoBridge` `FindVehicleByFactionAndId`(bug fix)/`RecordVehicleState`/`ApplyVehicleState`(light setters)/`ApplyVehicleStateFull`(`ProcessInstanceData`); `GeoStateSyncBroadcaster` (host snapshot+diff, ticked from `NetworkEngine.Update`); `BroadcastGeoStateDiff` + route `0x35` → `ClientGeoStateApplier`; RETIRE `StartTravel` command-replay, KEEP client→host relay. Two-phase DIAG (Task 0 all-factions `DescribeVehicles` now / Task 13 revert `b753111`+`fbfb3f9` after verify). 14 tasks; pure cores TDD + engine seams build-verified + in-game GATE (host moves Phoenix Manticore + NJ Thunderbird → both mirror) |
| [superpowers/2026-07-05-multiplayer-inc4-s2-travel-mirror-handoff.md](superpowers/2026-07-05-multiplayer-inc4-s2-travel-mirror-handoff.md) | **Session handoff 2026-07-05** — Inc4 S2 travel mirror (17 commits `0d38d20`->`9e80b24`): composite-key ROOT CAUSE, snapshot interpolation, MoveVehicle+ExploreSite relays, report-mirror gate-ON, project rename Multipleer->Multiplayer |
| *(outer)* [specs/2026-07-05-multiplayer-unified-popup-mirror-design.md](../../docs/superpowers/specs/2026-07-05-multiplayer-unified-popup-mirror-design.md) | **Unified popup-mirror design** — pillars P1-P7: mission-state mirror, universal blocking, outcome modals, unified sequencer, occId dedup, resource-harvest float, objectives channel; world-activity batches WA-1..4; marketplace; personnel sync PS1-PS4 |
| *(outer)* [specs/2026-07-05-multiplayer-personnel-sync-design.md](../../docs/superpowers/specs/2026-07-05-multiplayer-personnel-sync-design.md) | **Personnel sync design** — personnel edit intents 60-65, PersonnelChannel #9, RecruitPoolChannel #10, GeoVehicleChannel crew/loadout tails, ResearchSnapshot v4 AvailableAuthoritative |
| *(outer)* [specs/2026-07-06-multiplayer-tactical-closure-design.md](../../docs/superpowers/specs/2026-07-06-multiplayer-tactical-closure-design.md) | **Tactical closure design** — surfaces 0x92-0x98: actor spawn/despawn, ability-intent relay, ground surfaces, mission conclusion, ammo/MC mask bits, destructibles, enemy-turn camera, AoE/VFX replay |
| *(outer)* [research/2026-07-05-geoscape-personnel-native-taxonomy.md](../../docs/superpowers/research/2026-07-05-geoscape-personnel-native-taxonomy.md) | Personnel native taxonomy — GeoCharacter live-state blobs, GeoUnitId keying, roster membership |
| *(outer)* [research/2026-07-05-multiplayer-personnel-coverage-audit.md](../../docs/superpowers/research/2026-07-05-multiplayer-personnel-coverage-audit.md) | Personnel coverage audit — equipment/augment/hire/transfer/dismiss/rename coverage |
| *(outer)* [research/2026-07-05-geoscape-full-coverage-gap-audit.md](../../docs/superpowers/research/2026-07-05-geoscape-full-coverage-gap-audit.md) | Geoscape full-coverage gap audit — every unsynced surface categorized |
| *(outer)* [research/2026-07-05-multiplayer-three-player-audit.md](../../docs/superpowers/research/2026-07-05-multiplayer-three-player-audit.md) | Three-player topology audit — 3+ player dedup, peer-keyed IntentDedup, mid-session join |
| *(outer)* [research/2026-07-06-tactical-full-coverage-gap-audit.md](../../docs/superpowers/research/2026-07-06-tactical-full-coverage-gap-audit.md) | Tactical full-coverage gap audit — every unsynced tactical surface categorized |

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

### State Channels (on GeoState `0xA1`)

| Ch# | Name | Scope |
|-----|------|-------|
| 1 | Inventory | Faction inventory |
| 2 | Research | `ResearchChannel` (single-source-of-truth); v4 `AvailableAuthoritative` invalidation reconcile; extra dirty trigger `SetPowered` |
| 3 | Unlock | Faction unlocks |
| 4 | Diplomacy | Faction diplomacy + forced `PartyDiplomacyState` byte tail |
| 5 | GeoSite | Site records + extras block: bit0 haven (population/infested), bit1 alien-base (type/addons), bit2 excavation, bit3 attack-schedule, bit4 ActiveMission (`GeoMissionRecord`), bit6 weather, bit7 `ExpiringTimerAt` |
| 6 | GeoVehicle | Vehicle identity/spawn + crew `GeoUnitId[]` + aircraft loadout (weapons/modules) |
| 7 | Objectives | `GeoFaction.Objectives` (4 classes) + `GeoscapeEventSystem` variables + marketplace offers |
| 8 | Mist | `MistRendererSystem` hourly deflate snapshot (chunked 24 KB) |
| 9 | Personnel | Roster membership + whole-`GeoCharacter` live-state blobs, key `GeoUnitId` |
| 10 | RecruitPool | Haven `AvailableRecruit` + `_nakedRecruits` + `_capturedUnits` |

### Geoscape Actions Relayed

| Id | Action | Notes |
|----|--------|-------|
| 40 | MoveVehicle | `StartTravel` intercept |
| 41 | ExploreSite | `StartExploringCurrentSite` |
| 60-65 | Personnel edits | Equip / augment / hire / transfer / dismiss / rename (permission + ownership gated) |
| 80 | GeoAbilityActivateAction | Harvest / Excavate / EmergencyRepair / Scan / AncientSiteProbe / ActivateBase / AncientGuardianGuard (allowlist, `ActionCategory.GeoAbility`) |

### Tactical Surfaces

| Packet | Surface | Notes |
|--------|---------|-------|
| 0x8E | Ability-intent relay | Active allowlist: Heal, RecoverWill, Rally, PsychicScream, Reload, Interact; DeployTurret/OpenCrate deferred |
| 0x8F | Actor state delta | Position (0x0008) + facing (0x0010) + ammo (0x0400) + mind-control faction display (0x0800) |
| 0x92/0x93 | Actor spawn/despawn | Mid-battle reinforcements, eggs, turrets, loot containers — ground entities reuse actor registry |
| 0x94 | Ground surfaces | Fire / goo / acid / mist (`SetVoxelType` leaf funnel) |
| 0x95 | Mission conclusion | `GameOver` chokepoint; client flips `IsGameOver`; outcome modal stays on geoscape 0x69 path |
| 0x96 | Destructibles | `DestructableDamageReceiver.ApplyDamage` mirror, `SceneObjectId` guid key |
| 0x97 | Enemy-turn camera hint | Camera focus during enemy turn |
| 0x98 | AoE/volume VFX replay | Area-of-effect visual replay |

### Display Rail

- Unified `displaySeq` sequencer (P4): host stamps at `GeoscapeViewSwitchQuery`; client queue prio DESC / seq ASC, one-at-a-time; flag `DisplaySequencerGate` default ON.
- `0x69`/`0x6C` occId dedup (P5) — eliminates STUN reliable-transport double-display.
- Marketplace `UIStateMarketplaceGeoscapeEvent` selection mirror.

### Networking

- Tactical `IntentDedup` peer-keyed `(peerId, surfaceId, nonce)` — 3+ players unblocked.
- P1 mid-session join geoscape-only (`JoinReady` 0x45, per-peer unicast save-transfer via `AutosaveGame`, live-battle join rejected with notice).
- Inc3 action-relay envelope `0xA2`-`0xA4` behind `GeoActionRelay.UseEnvelope` flag DEFAULT OFF (legacy path byte-identical; flip after in-game verify).

## Save / Load Gate Matrix

Who can load, when, and what blocks it.

| Scenario | Allowed? | Mechanism / Gate | Notes |
|---|---|---|---|
| **Non-co-op** (mod installed, no active session) | **YES** — untouched | Gate returns `true` (`SaveLoadInterceptPatch.cs:441`); all suppress/curtain patches no-op when `engine==null \|\| !IsActive` | Normal SP load is fully clean; zero mod interference |
| **Host — lobby, session NOT started** | **NO** — captured | Pick captured as lobby base save via `ShouldCaptureAsLobbyPick` (`SessionLifecycle.cs:75`; `SaveLoadInterceptPatch.cs:181`,`:375`) | Save becomes the co-op session seed, not a live load |
| **Host — session started, ≥1 client** | **Rerouted** (conditionally) | CONTINUE / Quickload / pause-LOAD rerouted to F2 host-authoritative in-session reload (`HostStartSessionInGame`) when `HostLoadGuard` open (no transfer in flight); else **BLOCKED** (`SaveLoadInterceptPatch.cs:383-419`; `SessionLifecycle.cs:52`) | Re-runs chunked save transfer + barrier so every client reloads in sync |
| **Host — session started, 0 clients** | **YES** | `HostInSessionHasNoClients` (`SessionLifecycle.cs:111`; `:390`) | No peers to desync; vanilla solo load allowed |
| **Client — active session** | **BLOCKED** | `ShouldBlockClientLoad` (`SessionLifecycle.cs:131`; `:427`) — messagebox "Only the host can load" | Host-authoritative by design; client pulled in only via host transfer |
| **Mid-tactical host load** | **Highest risk** | Gate does not distinguish tactical vs geoscape; reroutes through `HostStartSessionInGame` | Full host→client tactical state replication still incomplete; geoscape-anytime is safe; tactical reload deferred behind full-state tactical spine convergence |

- **Summary:** HOST can load anytime (lobby pick / mid-session reload / clientless solo). CLIENT cannot load by design. NON-co-op player is completely unaffected.

### Save-data poisoning verdict

**Multiplayer writes NOTHING into the savegame graph.** A save written with the mod active is a plain PP/TFTV save and loads cleanly without the mod installed.

- **No save-WRITE hook** — no Harmony patch on `WriteSavegame*`/`SaveGame`/serializer write path.
- **No persisted custom state** — no `ISerializable`, no custom `GameComponentDef`/`GameTagDef` registered into the persisted graph. `DefReflection` only resolves defs, never mutates/adds.
- **All co-op runtime state is transient** — network messages, reassembly buffers, live engine fields; none reach disk.
- The only savegame serialization is read-only on the host: `SaveTransferCoordinator.HostSerializeAndSendCrt` reads the host's existing vanilla save to a `byte[]` for network transfer (`SaveTransferCoordinator.cs:403`). No write/inject.

> Full detail: [superpowers/research/2026-06-27-save-load-robustness-map.md](superpowers/research/2026-06-27-save-load-robustness-map.md)
