# Co-op Sync Roadmap + Status Tracker

Living roadmap + status tracker for the PhoenixPoint co-op multiplayer sync. NEW SESSION: read the STATUS table + CURRENT POSITION first — they say which sub-project is active and the next action.

> **Last verified against code: 2026-07-05** — HEAD `9e80b24` = Inc4 S2 travel mirror shipped + in-game gate loop (17 commits `0d38d20`->`9e80b24`: composite-key ROOT CAUSE, snapshot interpolation, MoveVehicle+ExploreSite relays, report-mirror gate-ON, turbine-tracer+explore-button, deploy-prompt exclusion, project rename Multipleer->Multiplayer); 1098/1098 tests (1091 unit + 7 bridge); DLL SHA256 `DEA2D4C7…838A` deployed both instances. Session record: `superpowers/2026-07-05-multiplayer-inc4-s2-travel-mirror-handoff.md`.

## Vision

- PhoenixPoint co-op multiplayer, host-authoritative, up to ~10 players
- Full GEOSCAPE (global map) co-op: every player interaction synced — events/quests, pause, aircraft/ship control, base building, crafting/manufacture, soldier equipment + customization, recruitment

## Invariants (do NOT violate — they are what prevents a future rewrite)

1. **Host is ALWAYS host.** Single authority; every client holds the FULL campaign state as a mirror.
2. **Snapshot(=save)+delta for join/reconnect.** Reconnect loads ONLY the returning player — others keep playing (no global pause/reload).
3. **Transport is a swappable layer.** Network obfuscation / VPN / anti-DPI = the player's own stack, OUT of mod scope (mod just exposes a clean transport interface, works over any tunnel).
4. **Skeleton ergonomics:** adding a new synced surface = implement the interface (`ISyncedAction` for discrete commands + `IStateChannel` for mirrored state) + register once -> serialization, permission gate, host-apply, broadcast, client mirror, reactive UI refresh, ordering/dedup all come FOR FREE. Zero copy-paste.

## OUT of scope (decided)

- **Host-migration** (any-peer-becomes-host) — explicitly dropped: most complex, deemed not worth it; host stays host
- **Network obfuscation / VPN / anti-DPI / hole-punching** beyond the existing transport — player's network stack

## ARCHITECTURE DECISION (2026-06-17): Full Host→Client Geoscape State Replication

**Verdict: FEASIBLE — adopted as the new spine.** Design doc: `docs/superpowers/specs/2026-06-17-multiplayer-full-geoscape-replication-feasibility.md`.

### Root cause that forced it
- Client geoscape sim is fully LIVE and rolls its own state independently (e.g. `GeoSite.EncounterID` via `Random.Range` in `PhoenixFaction_OnSiteFirstTimeVisited`), so events/research/manufacture/trade/storage all diverge
- Per-domain band-aids (ResearchChannel/wallet/inventory/diplomacy/unlock channels + event-display suppression) are baseline-to-RETIRE, not the target

### Backbone: native per-entity in-place apply
- `GeoSite.ProcessInstanceData` — restores `EncounterID`+`RandomSeed`
- `Timing.ProcessInstanceData` — writes `_paused/_scale/StartTime/OwnNow`, fires ZERO events
- `GeoVehicle.ProcessInstanceData` — in-place vehicle restore (path/site/hitpoints/weapons)
- `GeoFaction.InstanceData` setter — wallet/storage/research/manufacture/diplomacy/unlocks restore
- Full `GeoLevelInstanceData` snapshot = join/reconnect ONLY (heavy level-load via `LevelCrt`); in-play = per-entity diffs + 2-5Hz clock anchor; idle = ~0 bytes
- Save-transfer (32KB chunks via `SaveTransferCoordinator`) already ships

### Prior wipe context
- Tag `pre-wipe-full-2026-06-15`, wipe commit `55a4694` — was a client vehicle RENDER bug (custom ~15Hz transform stream + `ClientVehicleInterpolator` ring-buffer speed-mismatch), NOT a data-replication failure
- Data-replication machinery (diff codec, broadcaster, per-entity applier, sim-freeze, producer table, entity-op) was SOUND and is REVIVABLE
- DO NOT revive: the 15Hz transform stream / `ClientVehicleInterpolator`; replicate travel as a discrete `StartTravel{path,startTime}` (native slaved-clock render via `NavigateRoutine`)
- TFTV `AircraftReworkMaintenance` (hourly re-Navigate + x2 speed at `:384`/`:377`/`:405`) must be frozen on the client (narrow Harmony guard, defensive per `theturned-tftv-compat-required` pattern)

### Increment plan (each in-game-gated, commit to inner `main`, tests-green)
- **Inc 1 — Client sim-freeze + snapshot-on-join:** revive `GeoSimProducerTable` (13-producer set headed by `GeoLevelController.LevelHourlyUpdateCrt`) + `ClientGeoSimSuppressPatch` + travel-emitter suppression + freeze TFTV maintenance → kills the EncounterID divergence
- **Inc 2 — Discrete authoritative deltas:** `0x36 GeoEntityOp` (create/destroy); travel = `StartTravel{path,startTime}` discrete reliable; geoscape events fire host-only → `EventSystemInstanceData`/reveal delta; reuse `SurfaceRouter` chokepoint
- **Inc 3 — Generic per-entity InstanceData-diff:** revive `GeoStateDiffCodec`/`GeoVehicleStateDiffer`; client applies via native per-entity `ProcessInstanceData` + `GeoFaction.InstanceData` + Marketplace blob; light moving-vehicle drift-correction ~1-2Hz (NO 15Hz stream, NO custom interpolation)
- **Inc 4 — Retire redundant per-domain channels:** surface-by-surface, replace ResearchChannel/wallet/inventory/diplomacy/unlock/event-suppression with generic GeoFaction diff; each retire in-game-gated
- **Inc 5 — CRC divergence detection + reconnect:** rolling CRC32 over serialized `RecordInstanceData` → low-freq resync; reconnect = host snapshot → returning player only → buffered-delta catch-up (others unaffected); reuse `SaveTransferCoordinator.Crc32`

### Permissions fit (unchanged)
- Client input → host `PermissionGate` → host apply → host replicate, via the existing `SurfaceRouter` chokepoint (commit `8068c89`)
- Full replication strengthens permissions: client sim frozen → denied input never reaches host-apply → client mirror stays correct

---

## Roadmap (decomposed sub-projects)

Each sub-project gets its own spec -> plan -> impl when started. Order: #0 (read-only, now) -> #1 skeleton -> #2 in-game -> #3 surfaces (NOW REFRAMED: full-replication increments Inc1-Inc5 replace per-surface piecemeal) -> #4 efficiency (held as property of #1/#3) -> #5 reconnect (folded into Inc5).

### #0 — Engine feasibility recon (READ-ONLY, early, parallel, no code) — DONE

- How PP serializes/transfers a save mid-game (for #5 reconnect)
- How each geoscape subsystem exposes mutation entry points + change events (for #3 channels)
- Pause + aircraft-control hooks
- Informs the skeleton's interfaces

**#0 Result (2026-06-16):** Verdict: all 4 items FEASIBLE, no hard blockers. SDK open-questions resolved: mid-battle save = vanilla; save/load API = `PhoenixSaveManager.SaveGame` / `SaveManager.LoadGame` (already driven by mod). Residual unknowns (need in-game/runtime, not deeper dig): (a) base-UI uncommitted-edit-buffer on forced yank; (b) R1 client hourly-sim (`GeoLevelController.LevelHourlyUpdateCrt` runs authoritative income/research/recruit RNG locally) must be host-suppressed/host-driven before geoscape-tick surfaces ship; (c) verify decompile-proxy signatures vs installed Assembly-CSharp.dll before each Harmony patch.

### #1 — CORE / SKELETON (the reusable backbone)

- One inbound pipeline chokepoint: decode -> (host) Authorize -> Order/Dedup -> Apply (`SyncApplyScope`) -> Reactive-refresh -> (host) Rebroadcast + MarkDirty
- ONE registration point for a "synced surface" (`ISyncedAction` + `IStateChannel`)
- Most pieces already exist (`SyncEngine`, `PermissionGate`, `RefreshNeedsKick`, `SequenceTracker`, `RequestDedup`, `StateChannelRegistry`) — this is consolidation into a clean, documented skeleton + registration ergonomics
- Behavior-preserving

#### #1 Phase 1 progress

- **Spec:** `docs/superpowers/specs/2026-06-17-multiplayer-core-skeleton-design.md`. **Plan:** `docs/superpowers/plans/2026-06-17-multiplayer-core-skeleton.md`.
- **Design:** unified `SyncEnvelope` wire (`PacketType.SyncEnvelope=0x67`) + one-touch `SurfaceRegistry` + pure `SurfaceRouter` chokepoint (host-authoritative) wired into `SyncEngine` via `ISyncSink`. Behavior-preserving incremental.
- **DONE (inner main commits):**
  - T1 SyncKind+SyncEnvelope codec `25e9131` + hardening `e8f8d1c`
  - T2 SurfaceIds `10cb23c`
  - T3 SurfaceRegistry (kind-class keyed _actions/_channels) `c6d3ba4`
  - T4 pure SurfaceRouter+ISyncSink (7 router tests, host-auth chokepoint, security-reviewed APPROVED) `8068c89`
  - T5 RegisterSurfaces (9 action surfaces) `85e402b`
  - T6 wire into SyncEngine (ADDITIVE — legacy path still primary, envelope receive DORMANT, build 0err/0warn) `0ed4937`
- **Tests:** 196 -> 215 green at Phase-1 merge (925 green now = 918 unit + 7 bridge). Mod build 0 err / 0 warn. Each task reviewed (spec+quality), fixes looped.
- **IN-GAME STATUS:** UNVERIFIED. Tasks 1-6 are additive so in-game behavior is byte-unchanged (nothing emits SyncEnvelope yet; `SendActionRequest` still legacy).
- **Task 7 DEFERRED** (flip `SendActionRequest`->envelope + DELETE legacy `OnActionRequest`/`OnActionApply`/route cases): requires IN-GAME GATE #1 first (user verifies envelope path host+client DirectIP, same DLL). After gate #1 OK -> execute Task 7 from the plan -> IN-GAME GATE #2 = Phase-1 acceptance.
- **Phase-2 carryovers (noted during impl):**
  - Re-type `SurfaceEntry.Channel` object->IStateChannel when channels migrate
  - `ISyncSink.RejectTo` needs request-correlation (currently nonce=0 placeholder)
  - `MarkSurfaceDirty` broaden beyond research channel 2

### #2 — Migrate existing surfaces onto the skeleton + in-game gate

- Research / manufacture / facility / events / wallet / time already work — route through the skeleton, verify in-game

### #3 — Geoscape surface completeness — REFRAMED as full-replication increments

> **2026-06-17:** per-surface piecemeal replaced by Inc1-Inc5 (see ARCHITECTURE DECISION above). The per-domain channels below are the interim baseline to RETIRE (Inc4), not extend.

- **Interim baseline (working, deployed):** research (ResearchChannel ch2), manufacture, facility, geoscape-events (answer), wallet echo, time-sync (anchor-rate clock) — wallet/state echo now ride the unified `0x67` envelope (`0xA0 GeoWallet`/`0xA1 GeoState`) as the SOLE rail; legacy `0x63`/`0x64` retired `a4781ae` (2026-06-26)
- **Full-replication replaces:** pause (subsumed by sim-freeze Inc1), aircraft control (discrete StartTravel Inc2), equipment/customization/recruitment (GeoFaction InstanceData-diff Inc3), quests/events (host-only fire + EventSystemInstanceData Inc2/Inc3)
- Base-building completeness carries forward as a discrete `ISyncedAction` surface

### #4 — 10-player efficiency

- Deltas not constant full snapshots; host->N fan-out; snapshot only on join/reconnect
- Woven into #1/#3 + Inc3 diff model (idle = ~0 bytes; discrete travel = ZERO continuous bytes)
- Load test deferred until Inc3 lands

### #5 — Reconnect / hot-join — FOLDED into Inc5

- Host snapshots (save) -> sends ONLY to the returning player -> they load + apply a buffered delta from the snapshot moment -> back in; others unaffected
- Rolling CRC32 divergence detection (`SaveTransferCoordinator.Crc32`) + reconnect in one increment (Inc5)
- Depends on #0 findings + Inc1-Inc3 stable

## STATUS

| Sub-project | Status | Notes |
|---|---|---|
| **ARCHITECTURE** | **DECIDED 2026-06-17** | Full host→client geoscape state replication adopted as spine; per-domain channels = interim baseline to retire; see ARCHITECTURE DECISION section |
| #0 Feasibility recon | **DONE** | all 4 items feasible, no hard blockers; see #0 Result block |
| #1 Core skeleton | **IN PROGRESS — Phase 1 code-complete (Tasks 1-6), Task 7 pending in-game gate** | spec+plan written; 6/7 tasks merged to inner main; 925 tests green (918 unit + 7 bridge); in-game UNVERIFIED (additive, behavior-unchanged) |
| #2 Migrate existing + in-game gate | IN PROGRESS | surfaces below already work & deployed; skeleton consolidation pending; in-game verification ongoing |
| #3 Geoscape surfaces | **REFRAMED → Inc1-Inc5** | interim baseline DONE: research, manufacture, facility, geoscape-events(answer), wallet, time/anchor-rate. Full-replication increments replace per-surface piecemeal |
| Inc1 sim-freeze | **CODE-COMPLETE @ `546dcca`** | 13-producer freeze + TFTV maintenance guard + snapshot-on-join; build clean, unit tests green; **in-game verification pending** (acceptance gate not yet run) |
| Inc2 discrete deltas | **PARTIALLY STARTED** | host-only geoscape event fire + EventSystem work landed (`576b585`, `e09915f`, `8a05616`); entity-op + travel NOT landed |
| Inc3 InstanceData-diff | NOT STARTED | generic per-entity diff; retire per-domain channels starts here |
| Inc4 retire channels | NOT STARTED | surface-by-surface; each in-game-gated |
| **Inc4 client sim-freeze — S0+S1** | **SHIPPED + IN-GAME VERIFIED (2026-07-02)** | Client geoscape sim CLOCK paused (`Timing.Paused`); glyphs mirror host anchor. Flag-ON `de3aac7`. Spec: `docs/superpowers/specs/2026-07-02-multiplayer-inc4-client-sim-freeze-design.md`. |
| **Inc4 S2 — travel mirror + action relays** | **SHIPPED + PARTIALLY VERIFIED (2026-07-05)** | 17 commits `0d38d20`->`9e80b24`. Composite key ROOT CAUSE (per-faction VehicleID -> FNV-1a OwnerId key). Snapshot interpolation (ring buffer, slerp, clamp-hold). MoveVehicle=40, ExploreSite=41 relays. `0xA6` travel-meta + Animator State. `0xA7` explore progress. `0x69` report-mirror gate-ON (4-type whitelist). Turbine-tracer fix + explore-button greying. Deploy-prompt exclusion from event mirror. Project renamed Multipleer->Multiplayer. In-game confirmed: smooth vehicle mirror, client-ordered flight+explore, yellow route line, VoidOmen narrative, research popup. Verify pending: explore grey lifecycle, POI icon, deploy-prompt silence, event-duplicate rerun. |
| **Inc4 S3 — sim-freeze default-ON (committed)** | **SHIPPED (2026-07-05)** | S1 (clock-freeze) + S2 (travel mirror) in-game gates passed → `ClientSimFreeze.Enabled=true` blessed as the committed permanent default (was a temporary revertable S1-gate flip). Comment/test reframe + stale "default-OFF" doc fixes; DLL byte-identical (value already ON). Rollback until S4 = source toggle `Enabled=false` + rebuild (legacy suppress path still present). |
| **Inc4 S4 B1 — retire primary collapsed gate** | **SHIPPED (2026-07-05), pending in-game soak** | Deleted `ClientGeoSimSuppressPatch` + `GeoSimProducerTable` (+ its unit tests): the primary collapsed sim-suppress gate is now retired since S3 clock-freeze (`ClientSimFreeze`) is the committed permanent default — the client clock is paused so the 13 producers never re-fire regardless. Build clean; unit count dropped by the deleted table tests. NOTE: rollback via `Enabled=false` no longer revives the suppress path (it is gone) — from here freeze is the only client-inert mechanism. Belts B2-B4 (`EventSuppressClientGeoscapePatch`, `EventRaiseChokepointPatch`, `EventRaisedDisplayPatch`, TFTV + tactical guards) intentionally KEPT — deferred until B1 soaks clean over 2+ in-game sessions. |
| **Inc4 vehicle-creation channel (#6)** | **SHIPPED (2026-07-05), pending in-game soak** | `GeoVehicleChannel` (state channel #6, rides GeoState `0xA1`) closes the spawn-gap: the sim-frozen client never creates a mid-session-acquired craft (manufacture/story/steal), so `0xA5`/`0xA6`/`0xA7` silently skipped its unknown composite key → invisible forever. Host detects a NEW composite key inside the existing `GeoVehicleMirror.HostPollAndBroadcast` ~4 Hz walk (near-zero extra cost) → broadcasts `GeoVehicleIdentity` (owner faction Def.Guid, VehicleID, spawn `ComponentSet.SetDef.Guid`, initial pivot/heading). Client spawns an INERT `ActorSpawner.SpawnActor<GeoVehicle>(setDef,null,callEnterPlayOnActor:false)` (no DoEnterPlay → no RegisterVehicle/controller/producers), stamps Owner+VehicleID+placement, adds direct to `GeoMap.Vehicles` (pure mirror, like `SpawnMirrorSite`) → then `0xA5`/`0xA6`/`0xA7` resolve + drive it. Idempotent by composite key; vehicles present at bind seeded-KNOWN (join save covers them, no re-emit); re-created key re-emits. Build clean, +12 unit tests (DTO roundtrip / poll-diff new-key / apply-idempotence). Native spawn reflection glue is in-game-UNVERIFIED. |
| **Popup-mirror Batch-1 (P1+P2)** | **SHIPPED (2026-07-05), pending in-game soak** | Spec: `docs\superpowers\specs\2026-07-05-multiplayer-unified-popup-mirror-design.md`. **P1 `72774af`** — `site.ActiveMission` mirrored on GeoSite channel #5 (`GeoMissionRecord` tail: class discriminator + missionDef guid + runtime bits — haven attacker/deployments/zone, base-defense enemy/attackingSites; null = tombstone); host dirty-marks on GeoMap `SiteMissionStarted/Ended/Cancelled`; client attaches the class-exact mission via pure serializer ctors + DIRECT `ActiveMission` property write (no SetActiveMission — state channels never open UI). **P2 `98e9f3f`** — 0x69 whitelist +{0,2,11,20,34,36} (`ActiveMissionBrief` variant: client binds its P1-mirrored `site.ActiveMission`, class-checked via `ActiveMissionRebuildMatches`; id-2 two-class ambiguity resolved by the record's discriminator; 34 = fallback family, always degrades); ALL mission briefs now BLOCKING ({15,4,26,28} ∪ {0,2,11,20,34,36}) — gate arms at the 0x69 SHOW, client view-lock rides the mirrored-only origin-tag registry (b5d6cb6 invariant); release via 0x6C on `ModalResultCallback` + NEW `UIModuleModal.Hide` belt (haven-details Defend opener resolves via `OnDefendZoneResult`, bypassing ModalResultCallback). DEGRADED fallback = notify-only native MessageBox; host gate armed either way. 1288 unit + 7 bridge green; DLL SHA256 `4AB26D8E…C03D`. In-game soak = spec Batch-1 checklist (base attack both-see + intent-reject + cancel-close, haven attacker faction, save/load mid-brief). |
| Inc5 CRC + reconnect | NOT STARTED | divergence detection + reconnect (folds old #5) |
| Rail unification (geoscape) | **DONE @ `a4781ae` (2026-06-26)** | legacy `0x63`/`0x64` retired; `0xA0 GeoWallet`/`0xA1 GeoState` on the `0x67` envelope = SOLE geoscape wallet/state rail; `GeoRailGate` flag dead + removed |
| Tac Inc1 position (0x0008) | **BUILT** | delta pos in `tac.actorstate` 0x8F; in-game pending |
| Tac Inc2 facing (0x0010) | **CODE-COMPLETE @ `74b462c`** | `feat(tactical): wire actor facing 0x0010 into actor-state delta`; build 0 err / 0 warn, 925 tests green; **in-game acceptance gate pending**; plan: `docs/superpowers/plans/2026-06-25-multiplayer-tactical-fullstate-spine-roadmap.md` §4 |
| Tac Inc3 combat outcome + VFX | **IN PROGRESS** | enemy-turn camera chase landed (`4b561b8`, `8b78360`, `3fbfef6`, `ebca1b9`); explosion VFX from damage-state pending |
| #4 10-player efficiency | NOT STARTED | property of #1/Inc3 |
| OUT | — | host-migration, network obfuscation — excluded by decision |

## CURRENT POSITION (update each session)

- **Shipped (pre-skeleton):** inner main HEAD `60f45a2` (NOT pushed), DLL signature `829DA4F5`, deployed to `D:\Steam\steamapps\common\Phoenix Point\Mods\Multiplayer`, 196 tests green
- **HEAD = `98e9f3f` (2026-07-05, popup-mirror Batch-1 P1+P2), NOT pushed.** 1295/1295 tests (1288 unit + 7 bridge). DLL SHA256 `4AB26D8EEA9DED6791ED8D8AF6BAC4EA36357E60A89521AC9BBB3066FEC4C03D` deployed (junction covers both instances). 0x69 whitelist = {0,2,4,6,11,14,15,20,25,26,28,34,36,38}; blocking = {0,2,4,11,15,20,26,28,34,36}. In-game soak pending (spec Batch-1 checklist).
- Prior HEAD `9e80b24` (2026-07-05): 1098/1098 tests (1091 unit + 7 bridge). Inc4 S2 travel mirror shipped (17 commits). Project renamed Multipleer->Multiplayer (assembly `Multiplayer.dll`, mod ID `Morgott.Multiplayer`). DLL SHA256 `DEA2D4C7…838A` deployed both instances (junction). Envelope map: `0xA5` vehicle pos (~10 Hz), `0xA6` travel meta + Animator State, `0xA7` explore progress, `0x69` report modals (4-type whitelist), `0xA2`-`0xA4` RESERVED (action-relay cutover). Actions relayed: MoveVehicle=40, ExploreSite=41. In-game confirmed: smooth vehicle mirror (all factions), client-ordered flight+explore, yellow route line, VoidOmen narrative+title, research popup. Full session record: `superpowers/2026-07-05-multiplayer-inc4-s2-travel-mirror-handoff.md`. Prior context: Tasks 1-6 merged (#1 Phase 1); Tac Inc2 (facing 0x0010) code-complete; Tac Inc3 (enemy-turn camera chase) in progress; geoscape rail unified `a4781ae` (`0xA0`/`0xA1` on `0x67` envelope sole rail).
- **Working & deployed:**
  - Host-authoritative sync engine
  - Synced surfaces = research (single-source-of-truth via `ResearchChannel` ch2), manufacture, facility, geoscape events (answer), wallet echo (`0xA0`/`0xA1` sole rail on the `0x67` envelope), time-sync (anchor-rate clock)
  - Universal UI reactivity (`GeoUiRefresh.RefreshNeedsKick`: Research / Manufacturing / BaseLayout; wallet/events/time self-update)
  - Client->host unblocked via `PermissionManager` seeding in `HandlePeerList`
- **NEXT ACTIONS (resume here):**
  1. **In-game verification pass (restart both instances, HOST FIRST)** — detail in `superpowers/2026-07-05-multiplayer-inc4-s2-travel-mirror-handoff.md`: (a) `efa27d0` explore context-menu item never stuck greyed; (b) `efa27d0` POI inspected icon flips on client; (c) `9e80b24` deploy-prompt silence on host cancel; (d) event-duplicate scenario rerun; (e) `057614f` research-nav click; (f) `d2aab7b` explore-button grey lifecycle
  2. **Inc3 action-relay envelope-cutover spec** — awaits user review (`0xA2`-`0xA4`, `docs\superpowers\specs\2026-07-02`)
  3. **Inc4 S4 — retire the collapse set (one gate per commit, each in-game-gated):** B1 (primary gate — `ClientGeoSimSuppressPatch` + `GeoSimProducerTable`) SHIPPED 2026-07-05, **pending in-game soak**. B2-B4 (belts — event-suppress / event-raise chokepoint / event-raised display + TFTV & tactical guards) DEFERRED until B1 soaks clean over 2+ sessions. (S1+S2 in-game gates + S3 `ClientSimFreeze.Enabled=true` permanent default already shipped.)
  4. **Ability-relay backlog** (same `MoveVehiclePatch` shape): Harvest, Excavate, EmergencyRepair, Scan, AncientSiteProbe, ActivateBase, AncientGuardianGuard; mission flows (LaunchMission/Behemoth/TriggerEncounter/StealAircraft) separate
  5. **Spawn-gap** — ADDRESSED 2026-07-05 by `GeoVehicleChannel` (#6): a mid-session host-acquired craft now spawns an inert client mirror (owner+VehicleID+placement) so `0xA5`/`0xA6`/`0xA7` resolve it. In-game soak pending (native spawn reflection unverified)
  6. Host context-menu live-refresh while open = declined (fragile), greys on reopen
  7. **USER TODO:** rename root folder `E:\DEV\PhoenixPoint\Multipleer` -> `Multiplayer` (deploy.ps1 path-agnostic, .gitignore has both); re-enable mod in in-game mod menu BOTH instances (new ID `Morgott.Multiplayer`)
- **Architecture spine (2026-06-17):** full host→client geoscape state replication; per-domain channels = interim baseline → retire at Inc4. Design doc: `docs/superpowers/specs/2026-06-17-multiplayer-full-geoscape-replication-feasibility.md`
- **Note:** per-sub-project design specs go to `E:\DEV\PhoenixPoint\docs\superpowers\specs\YYYY-MM-DD-<topic>-design.md` when each is brainstormed; this file is the higher-level living tracker
