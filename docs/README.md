# Multiplayer — Cooperative Multiplayer Mod (Documentation Index)

These docs are the **single source of truth** for the Multiplayer cooperative-campaign mod
for Phoenix Point.

- A **cooperative campaign** mod built on the official **SDK** + **Harmony** patches.
- **Not** a traditional turn-based PvP / wait-for-each-other mode.
- One shared campaign; multiple players co-control a single faction by **ownership + permissions**.
- **Authoritative host** model (not lockstep): the host runs all logic + RNG + AI; clients send
  actions and reproduce validated results — clients never simulate.

> **Where are we now (current status)?** → [COOP-SYNC-ROADMAP.md](COOP-SYNC-ROADMAP.md) — the living
> roadmap + status tracker (read the STATUS table + CURRENT POSITION first). This is the authoritative
> "what is built / what is next" record. The older per-arc status note
> [research/00-current-state.md](research/00-current-state.md) is **SUPERSEDED** (kept for lineage only).

## How to read this tree (status legend)

A new contributor should read top-to-bottom: the first two groups are **current truth**, the rest is
**history**. Nothing below the Roadmap is maintained as current — it is kept for rationale and lineage.

| Group | What it is | Trust as current? |
|-------|------------|-------------------|
| **1. Engine — as-built** | How the shipped mod actually works today | ✅ Yes — current truth |
| **2. Research — reference** | Source-dive notes on the Phoenix Point engine | ✅ Yes — stable reference |
| **3. Roadmap & status** | Living tracker of built vs. next | ✅ Yes — current status |
| **4. Design history / lineage** | Original design + early build plans | ⚠️ Superseded — rationale only |
| **5. Development history** | Dated session working-notes / plans / patches | 🕘 Point-in-time — not maintained |

---

## 1. Engine — as-built implementation (CURRENT TRUTH)

The current source of truth for how the mod works today.

| Doc | Scope |
|-----|-------|
| [engine/01-networking-core.md](engine/01-networking-core.md) | `NetworkEngine` singleton + lifecycle, message routing, `PacketType`, binary message formats, `SessionManager` (heartbeat, ready-state), reliable message flow |
| [engine/02-transport-layer.md](engine/02-transport-layer.md) | `ITransport` + three transports (Steam P2P / Direct TCP / STUN UDP), comparison table, message envelope + message-type catalog by phase, reliability |
| [engine/03-harmony-patches.md](engine/03-harmony-patches.md) | Patch table (P0–P3 tactical, C1–C5 campaign), runtime type resolution, per-patch detail, connection-menu UI injection, native Load-screen intercept for the co-op "Choose save" |
| [WAN-COOP.md](WAN-COOP.md) | **Real-internet (WAN) join runbook:** which path to use, the exact Direct-IP port-forward answer (**TCP 14242, host-side only**), why the STUN invite code fails on symmetric NAT/CGNAT, and the Steam-invite gap (not implemented) + smallest viable plan |

> **Command-sync / state-sync layer (as-built code, no dedicated engine doc yet):** the
> host-authoritative sync backbone lives under `src/Network/` (`SyncEngine`, `SurfaceRouter`,
> state channels, geoscape/tactical surfaces). Its design is in the sync-canon docs (group 5);
> its current shape + the full channel/surface catalog live in [COOP-SYNC-ROADMAP.md](COOP-SYNC-ROADMAP.md)
> and the Quick Reference below.

## 2. Research — source-dive & reference material

Decompile-grounded notes on the Phoenix Point engine. Reference material — nothing compiles against
these; they inform where and how to patch.

| Doc | Scope |
|-----|-------|
| [research/00-current-state.md](research/00-current-state.md) | **⚠ SUPERSEDED status note** (thin-client vehicle-sync era, 2026-06). Superseded 2026-07-02 by the Roadmap; the co-op **loading-screen internals** section is still accurate, treat the rest as history |
| [research/01-tactical-action-pipeline.md](research/01-tactical-action-pipeline.md) | Tactical class hierarchy, action execution flow, `ActivateAbility` chokepoint, turn system, stable actor IDs, candidate Harmony patch sites |
| [research/02-rng-analysis.md](research/02-rng-analysis.md) | RNG sources (`SharedData.Random` + `UnityEngine.Random`), combat determinism, host-only-randomness decision, hidden RNG risks |
| [research/03-campaign-layer.md](research/03-campaign-layer.md) | Campaign subsystem entry points (research/manufacturing/base/aircraft/soldier), permission injection points, authoritative `CampaignPermission` set |
| [research/04-serialization.md](research/04-serialization.md) | Save/load engine, save file structure, `RecordInstanceData` snapshots, network-sync implications, snapshot use cases (join/reconnect/divergence) |
| [research/05-steam-networking.md](research/05-steam-networking.md) | Facepunch.Steamworks availability, P2P/matchmaking APIs, Steam-relay-vs-direct-IP recommendation, transport-agnostic stance |
| [research/06-harmony-patterns.md](research/06-harmony-patterns.md) | Reusable Harmony patterns from existing mods: lifecycle, patch types, internal-type patching, field access, event subscription |
| [research/07-tactical-concurrency.md](research/07-tactical-concurrency.md) | Simultaneous play, same-tile conflict, host receipt-order authority, destination reservation, turn-end ready-gate |
| [research/08-geoscape-concurrency.md](research/08-geoscape-concurrency.md) | Two state layers, shared clock, events (informational vs decision), forced state transitions (yank-to-briefing) |
| [research/09-disconnect-reconnect.md](research/09-disconnect-reconnect.md) | Orphan takeover, reconnect resync via the start-barrier, mid-battle-save caveat, host-loss, toasts, co-op loading overlay |
| [research/10-messagebox-input-prompt.md](research/10-messagebox-input-prompt.md) | Native message-box / text-input prompt API for in-game co-op dialogs |
| [research/11-console-hotkey-suppress.md](research/11-console-hotkey-suppress.md) | Console / hotkey suppression seam |
| [research/12-time-flow-and-sync-seams.md](research/12-time-flow-and-sync-seams.md) | Geoscape time-flow API + host-authoritative clock sync seams: clock owner, pause/speed API, auto-pause sites, `RecordInstanceData`/`ProcessInstanceData` settability, hook points, risks R1–R8 |

## 3. Roadmap & current status

| Doc | Scope |
|-----|-------|
| [COOP-SYNC-ROADMAP.md](COOP-SYNC-ROADMAP.md) | **Living roadmap + status tracker.** Vision, invariants, out-of-scope decisions, the 2026-06-17 full-geoscape-replication architecture decision, the decomposed sub-projects (#0–#5 / Inc1–Inc5), the STATUS table (every batch: geoscape channels, world-activity, popup-mirror, personnel, tactical surfaces), and CURRENT POSITION / next actions. **Start here for status.** |
| [plans/2026-07-12-tactical-entry-save-transfer.md](plans/2026-07-12-tactical-entry-save-transfer.md) | **ACTIVE plan.** Tactical mission ENTRY via host-authored mid-tactical save transfer (replaces client self-launch + reconcile/snap) + host loading-screen barrier. 2 batches, grounded anchors, tests, risks, rollback. |

### Audits (`audits/`)

| Doc | Scope |
|-----|-------|
| [audits/2026-07-12-geoscape-economy-sync-audit.md](audits/2026-07-12-geoscape-economy-sync-audit.md) | Geoscape economy/research sync audit: marketplace, manufacturing, loot, research, unlocks **clean**; haven resource trade **NOT synced** (BUG, only canon violation); haven StockedResources + marketplace offer-regen dirty-trigger unconfirmed |

## 4. Design history / lineage (SUPERSEDED originals)

> These describe the **original** architecture and design intent, plus the early build/UI plans.
> Kept for lineage and rationale, **not** current truth — for what is actually built, read groups 1–3.

### `specs/` — original design (superseded)

| Doc | Scope |
|-----|-------|
| [specs/01-design.md](specs/01-design.md) | Full original design: project goal & co-op concept, authoritative-host architecture, class diagrams, network protocol, Harmony patch plan, risk/desync assessment, implementation order, PoC roadmap (**superseded** by the engine docs + Roadmap) |
| [specs/02-session-lifecycle-and-player-management.md](specs/02-session-lifecycle-and-player-management.md) | Lobby & peer identity, session-start save transfer + barrier sync, MP state vs vanilla save (ownership model), player-management UI + permission persistence + transport-independent `playerGUID` |
| [specs/03-open-questions-sdk.md](specs/03-open-questions-sdk.md) | Items needing PP source/SDK to verify; deferred scope (join-in-progress, host migration) |

### `plans/` — early build & UI plans (superseded)

| Doc | Scope |
|-----|-------|
| [plans/foundation-build-plan.md](plans/foundation-build-plan.md) | Source-grounded build spec for foundation work-items (lobby / identity / save-transfer / permissions) |
| [plans/flow-reconciliation-plan.md](plans/flow-reconciliation-plan.md) | Reconcile the existing mod into the canonical lobby-first flow from `specs/02` |
| [plans/native-lobby-ui-plan.md](plans/native-lobby-ui-plan.md) | Replace the from-code uGUI overlay with cloned **native** Phoenix Point widgets for the lobby + save-picker |
| [plans/responsive-layout-refactor.md](plans/responsive-layout-refactor.md) | Responsive "rubber" layout refactor for the co-op lobby (removes manual-coordinate code) |

## 5. Development history (point-in-time working notes — NOT maintained)

> Dated session artifacts under `docs/superpowers/` and the raw patches under `docs/lineage/`.
> These are **snapshots from the day they were written** — session handoffs, per-increment
> implementation plans, design specs, and system-map research. They record how the project got
> here and are kept for lineage. They are **not** updated and may contradict current truth;
> when in doubt, groups 1–3 win.
>
> Note: some of these notes reference now-removed local two-instance testing scripts
> (`make-second-copy.bat` / `launch-second-copy.bat`, etc.). Current local-testing guidance lives in
> `tools/COOP-TESTING.md` (a developer's own second Steam-API session; no emulator tool is bundled).

### Session handoffs (`superpowers/*.md`)

| Doc | Scope |
|-----|-------|
| [superpowers/2026-06-13-EOD-replication-handoff.md](superpowers/2026-06-13-EOD-replication-handoff.md) | SD-AIDR replication + the host→client movement blocker; next-session pickup |
| [superpowers/2026-06-13-EOD-thin-client-vehicle-sync-handoff.md](superpowers/2026-06-13-EOD-thin-client-vehicle-sync-handoff.md) | Thin-client vehicle sync + snapshot interpolation; next-session pickup |
| [superpowers/2026-06-20-multiplayer-tactical-handoff.md](superpowers/2026-06-20-multiplayer-tactical-handoff.md) | Tactical battle replication — next-session handoff |
| [superpowers/2026-06-20-multiplayer-tactical-mirror-handoff.md](superpowers/2026-06-20-multiplayer-tactical-mirror-handoff.md) | Tactical full-mirror arc — night-session handoff |
| [superpowers/2026-07-03-multiplayer-fable-rereview-fixes-handoff.md](superpowers/2026-07-03-multiplayer-fable-rereview-fixes-handoff.md) | Fable re-review fix wave (native-advance reflection root cause, host-resolved event texts, correlator hardening, `0x6B` advance-request, wallet convergence) |
| [superpowers/2026-07-05-multiplayer-inc4-s2-travel-mirror-handoff.md](superpowers/2026-07-05-multiplayer-inc4-s2-travel-mirror-handoff.md) | Inc4 S2 travel mirror: composite-key root cause, snapshot interpolation, MoveVehicle/ExploreSite relays, report-mirror gate-ON, project rename Multipleer→Multiplayer |

### Implementation plans (`superpowers/plans/`)

| Doc | Scope |
|-----|-------|
| [superpowers/plans/2026-06-12-coop-loading-screen-overlay.md](superpowers/plans/2026-06-12-coop-loading-screen-overlay.md) | Co-op loading-screen roster overlay build plan |
| [superpowers/plans/2026-06-12-geoscape-command-sync-stage1.md](superpowers/plans/2026-06-12-geoscape-command-sync-stage1.md) | Stage-1 command actions + per-GUID permissions; first vertical proof `GeoVehicle.StartTravel` |
| [superpowers/plans/2026-06-13-replication-increment1-client-inert.md](superpowers/plans/2026-06-13-replication-increment1-client-inert.md) | SD-AIDR INC-1: client inert + slaved-clock travel mirror (13-producer suppress table, travel-emitter suppress) |
| [superpowers/plans/2026-06-13-replication-increment2-entity-lifecycle.md](superpowers/plans/2026-06-13-replication-increment2-entity-lifecycle.md) | SD-AIDR INC-2: entity lifecycle `0x36 GeoEntityOp` (create/destroy replay + VehicleID reconcile) |
| [superpowers/plans/2026-06-13-replication-increment3a-vehicle-state-mirror.md](superpowers/plans/2026-06-13-replication-increment3a-vehicle-state-mirror.md) | SD-AIDR INC-3a: all-factions vehicle state mirror (`0x35 GeoStateDiff`; `(factionGuid,VehicleID)` resolver unblocks host→client movement) |
| [superpowers/plans/2026-06-13-thin-client-vehicle-sync-increments.md](superpowers/plans/2026-06-13-thin-client-vehicle-sync-increments.md) | Thin-client vehicle sync — snapshot-interpolation increment plan |
| [superpowers/plans/2026-06-13-time-sync-stage2-increment1.md](superpowers/plans/2026-06-13-time-sync-stage2-increment1.md) | Stage-2 Inc-1 host-authoritative time: `SetTimeState`, client pause/speed intercepts, hourly-sim suppression, `0x34` clock mirror |
| [superpowers/plans/2026-06-15-coop-action-sync-engine.md](superpowers/plans/2026-06-15-coop-action-sync-engine.md) | Co-op action-sync & permission engine — implementation plan |
| [superpowers/plans/2026-06-16-coop-sync-v2-state-echo.md](superpowers/plans/2026-06-16-coop-sync-v2-state-echo.md) | Co-op sync v2: host-authoritative state echo (frozen-client model) |
| [superpowers/plans/2026-06-25-multiplayer-tactical-fullstate-spine-roadmap.md](superpowers/plans/2026-06-25-multiplayer-tactical-fullstate-spine-roadmap.md) | Tactical full-state spine roadmap + Inc2 implementation plan |
| [superpowers/plans/2026-06-26-multiplayer-event-window-mirror.md](superpowers/plans/2026-06-26-multiplayer-event-window-mirror.md) | Host→client report/event-window mirror — implementation plan |
| [superpowers/plans/2026-06-26-multiplayer-inc1-rail-unification.md](superpowers/plans/2026-06-26-multiplayer-inc1-rail-unification.md) | Increment 1 rail unification (slice 1): fold legacy rails onto the `0x67` envelope |
| [superpowers/plans/2026-06-27-inc3-tactical-combat-camera.md](superpowers/plans/2026-06-27-inc3-tactical-combat-camera.md) | Inc3: combat outcome + explosion VFX + enemy-turn camera — implementation plan |
| [superpowers/plans/2026-06-27-multiplayer-sync-canon-rollout.md](superpowers/plans/2026-06-27-multiplayer-sync-canon-rollout.md) | Sync-canon rollout — implementation plan (converge side-rails onto the one canon) |

### Design specs (`superpowers/specs/`)

| Doc | Scope |
|-----|-------|
| [superpowers/specs/2026-06-12-coop-loading-screen-overlay-design.md](superpowers/specs/2026-06-12-coop-loading-screen-overlay-design.md) | Co-op loading-screen roster overlay — design |
| [superpowers/specs/2026-06-12-geoscape-command-sync-design.md](superpowers/specs/2026-06-12-geoscape-command-sync-design.md) | Host-authoritative command-result relay: module map (CommandRelay/Codec/HostArbiter/ClientApplier/InterceptRegistry/PermissionGate), broad-intercept registry, staging |
| [superpowers/specs/2026-06-13-coop-state-replication-design.md](superpowers/specs/2026-06-13-coop-state-replication-design.md) | Host-authoritative geoscape state replication (SD-AIDR): slaved-clock spectator-drive + native InstanceData-diff stream; supersedes the per-action StartTravel intercept |
| [superpowers/specs/2026-06-13-coop-state-replication-inc3-geostatediff.md](superpowers/specs/2026-06-13-coop-state-replication-inc3-geostatediff.md) | SD-AIDR INC-3 design: generic host→client state mirror over `0x35 GeoStateDiff` (scope-keyed, seq-guarded, `(factionGuid,VehicleID)` resolver) |
| [superpowers/specs/2026-06-13-thin-client-vehicle-sync-snapshot-interpolation.md](superpowers/specs/2026-06-13-thin-client-vehicle-sync-snapshot-interpolation.md) | Thin-client vehicle sync — snapshot-interpolation re-architecture (client render half of the `0x35` mirror) |
| [superpowers/specs/2026-06-19-multiplayer-tactical-generic-state-spine-design.md](superpowers/specs/2026-06-19-multiplayer-tactical-generic-state-spine-design.md) | Tactical generic state-spine design (host↔client full-state replication; read-only investigation) |
| [superpowers/specs/2026-06-26-multiplayer-unified-sync-backbone-design.md](superpowers/specs/2026-06-26-multiplayer-unified-sync-backbone-design.md) | Unified co-op sync backbone — design spec |
| [superpowers/specs/2026-06-27-inc3-tactical-combat-camera-design.md](superpowers/specs/2026-06-27-inc3-tactical-combat-camera-design.md) | Inc3: combat outcome + explosion VFX + enemy-turn camera — design |
| [superpowers/specs/2026-06-27-multiplayer-sync-canon-design.md](superpowers/specs/2026-06-27-multiplayer-sync-canon-design.md) | The sync canon: one host↔client synchronization pattern for all sync (the basis of `CLAUDE.md` / `CONTRIBUTING.md`) |

### System-map research (`superpowers/research/`)

| Doc | Scope |
|-----|-------|
| [superpowers/research/2026-06-26-event-window-mirror-system-map.md](superpowers/research/2026-06-26-event-window-mirror-system-map.md) | Geoscape event-window mirror — system map |
| [superpowers/research/2026-06-27-save-load-robustness-map.md](superpowers/research/2026-06-27-save-load-robustness-map.md) | Save/load robustness map (basis of the Save/Load Gate Matrix below) |

### Raw patches (`lineage/`)

Point-in-time git patches from superseded pivots. **Historical diffs, not applied** — kept for lineage
of two abandoned netcode directions. Do not apply against current source.

| Patch | What it was |
|-------|-------------|
| [lineage/command-replication-pivot-2026-06-14.patch](lineage/command-replication-pivot-2026-06-14.patch) | The command-replication pivot (travel-emitter suppression era) |
| [lineage/transform-streaming-netcode-2026-06-14.patch](lineage/transform-streaming-netcode-2026-06-14.patch) | The ~15 Hz transform-streaming client-interpolation netcode (abandoned — see the Roadmap "prior wipe context") |

---

## Quick Reference (as-built)

- **Source of truth:** host only. Clients send actions, receive validated results; they reproduce, never recompute.
- **Sync:** world-changing actions only (move/shoot/reload/ability/inventory/world-interact). **Never sync:** camera, selection, cursor, UI navigation.
- **Transport:** pluggable `ITransport` core (transport-agnostic). Steam P2P primary; DirectIP for LAN/dev (loopback solo test); STUN UDP for Steam-less direct P2P.
- **Connection input:** one box, autodetect IP vs Steam code; + Steam friends invite.
- **Lobby-first:** create → lobby → all ready → host picks save → gzip transfer → **barrier sync** (all `LOADED` → `BEGIN`) → play. On-disk save = single source of truth at start.
- **Identity:** persistent client-generated `playerGUID` (the only persistence key) + per-session `peerID` + mutable nickname; ownership/permissions bind to `playerGUID`/`peerID`, never the nickname.
- **Vanilla save untouched:** ownership/nicks/permissions = mod runtime-state, reconciled each session, never written into the PP save.
- **Top desync risk:** RNG + hidden game systems → [research/02-rng-analysis.md](research/02-rng-analysis.md).
- **Blocked on SDK:** UI injection, Steam availability, save/load API, loading-progress hook, 2nd-instance, mid-battle save → [specs/03-open-questions-sdk.md](specs/03-open-questions-sdk.md).

### State Channels (on GeoState `0xA1`)

| Ch# | Name | Scope |
|-----|------|-------|
| 1 | Inventory | Faction inventory |
| 2 | Research | `ResearchChannel` (single-source-of-truth); v4 `AvailableAuthoritative` invalidation reconcile; extra dirty trigger `SetPowered` |
| 3 | Unlock | Faction unlocks |
| 4 | Diplomacy | Faction diplomacy + forced `PartyDiplomacyState` byte tail |
| 5 | GeoSite | Site records + extras: bit0 haven (population/infested), bit1 alien-base (type/addons), bit2 excavation, bit3 attack-schedule, bit4 ActiveMission (`GeoMissionRecord`), bit6 weather, bit7 `ExpiringTimerAt` |
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

### Display Rail & Networking

- Unified `displaySeq` sequencer (P4): host stamps at `GeoscapeViewSwitchQuery`; client queue prio DESC / seq ASC, one-at-a-time; flag `DisplaySequencerGate` default ON.
- `0x69`/`0x6C` occId dedup (P5) — eliminates STUN reliable-transport double-display.
- Marketplace `UIStateMarketplaceGeoscapeEvent` selection mirror.
- Tactical `IntentDedup` peer-keyed `(peerId, surfaceId, nonce)` — 3+ players unblocked.
- P1 mid-session join geoscape-only (`JoinReady` 0x45, per-peer unicast save-transfer via `AutosaveGame`, live-battle join rejected with notice).
- Inc3 action-relay envelope `0xA2`-`0xA4` behind `GeoActionRelay.UseEnvelope` flag DEFAULT OFF (legacy path byte-identical; flip after in-game verify).

## Save / Load Gate Matrix

Who can load, when, and what blocks it.

| Scenario | Allowed? | Mechanism / Gate | Notes |
|---|---|---|---|
| **Non-co-op** (mod installed, no active session) | **YES** — untouched | Gate returns `true` (`SaveLoadInterceptPatch.cs:441`); all suppress/curtain patches no-op when `engine==null \|\| !IsActive` | Normal SP load is fully clean; zero mod interference |
| **Host — lobby, session NOT started** | **NO** — captured | Pick captured as lobby base save via `ShouldCaptureAsLobbyPick` (`SessionLifecycle.cs:75`; `SaveLoadInterceptPatch.cs:181`,`:375`) | Save becomes the co-op session seed, not a live load |
| **Host — session started, ≥1 client** | **Rerouted** (conditionally) | CONTINUE / Quickload / pause-LOAD rerouted to F2 host-authoritative in-session reload (`HostStartSessionInGame`) when `HostLoadGuard` open; else **BLOCKED** (`SaveLoadInterceptPatch.cs:383-419`; `SessionLifecycle.cs:52`) | Re-runs chunked save transfer + barrier so every client reloads in sync |
| **Host — session started, 0 clients** | **YES** | `HostInSessionHasNoClients` (`SessionLifecycle.cs:111`; `:390`) | No peers to desync; vanilla solo load allowed |
| **Client — active session** | **BLOCKED** | `ShouldBlockClientLoad` (`SessionLifecycle.cs:131`; `:427`) — messagebox "Only the host can load" | Host-authoritative by design; client pulled in only via host transfer |
| **Mid-tactical host load** | **Highest risk** | Gate does not distinguish tactical vs geoscape; reroutes through `HostStartSessionInGame` | Geoscape-anytime is safe; tactical reload deferred behind full-state tactical spine convergence |

- **Summary:** HOST can load anytime (lobby pick / mid-session reload / clientless solo). CLIENT cannot load by design. NON-co-op player is completely unaffected.

### Save-data poisoning verdict

**Multiplayer writes NOTHING into the savegame graph.** A save written with the mod active is a plain
PP/TFTV save and loads cleanly without the mod installed.

- **No save-WRITE hook** — no Harmony patch on `WriteSavegame*`/`SaveGame`/serializer write path.
- **No persisted custom state** — no `ISerializable`, no custom `GameComponentDef`/`GameTagDef` registered into the persisted graph.
- **All co-op runtime state is transient** — network messages, reassembly buffers, live engine fields; none reach disk.
- The only savegame serialization is read-only on the host: `SaveTransferCoordinator.HostSerializeAndSendCrt` reads the host's existing vanilla save to a `byte[]` for network transfer.

> Full detail: [superpowers/research/2026-06-27-save-load-robustness-map.md](superpowers/research/2026-06-27-save-load-robustness-map.md)
</content>
</invoke>
