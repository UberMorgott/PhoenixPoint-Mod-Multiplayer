# Co-op Sync Roadmap + Status Tracker

Living roadmap + status tracker for the PhoenixPoint co-op multiplayer sync. NEW SESSION: read the STATUS table + CURRENT POSITION first — they say which sub-project is active and the next action.

> **Last verified against code: 2026-07-06** — autonomous night session `35e156a`..`ff003a6` on inner main. 1805/1805 tests (1798 unit + 7 bridge). Every batch deployed via `deploy.ps1` (final DLL 2026-07-06 12:56:35). Session record: `E:\DEV\PhoenixPoint\docs\superpowers\2026-07-06-EOD-handoff-autonomous-night.md`.

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
| **Popup-mirror Batch-2 (P3+P6)** | **SHIPPED (2026-07-05), pending in-game soak** | Spec: `docs\superpowers\specs\2026-07-05-multiplayer-unified-popup-mirror-design.md` §Batch-2. **P3 `00a4b2d`** — mission OUTCOME modals mirrored: 0x69 whitelist +{1,3,5,12,16,21,27,29,35,37} (`MissionOutcome` variant; post-tac rail `UIStateInitial.cs:105-139` prio int.MaxValue + cancel paths `:1934/:1938` — same OpenModal(Persistent) chokepoint). TOMBSTONE-ORDERING DECISION: payload is SELF-SUFFICIENT (missionClass + outcomeState + `RewardDisplaySnapshot` blob ride the 0x69 tail — event-result-card pattern); client rebuilds a DISPLAY-ONLY mission via the P1 pure-ctor map (never `site.ActiveMission`, never attached) + stamps minimal `Result` (viewer FactionResult) + reconstructed display `Reward` (resources/items/skill pts) → native outcome binds (`RewardsController.SetReward`) render natively. NON-blocking (no gate, native close). Queue-don't-drop: outcome landing while client still in tactical is queued (bounded 8), drained on geoscape tick. `ReportOutcomeDedup` = consecutive byte-identical drop (interim until Batch-3 P5 occIds). EXCLUDED: 33 Interception (live GeoAirMission binds), 18 Infiltrate (outside P1 class map); 35 whitelisted-but-always-skip (fallback family). Diplomacy attitude row deliberately empty on client (snapshot carries display names, not guids). **P6 (this commit)** — resource-harvest float mirrored: `GeoSite.ShowResourceHarvested` chokepoint patch (host Postfix reads siteId + FIRST ResourceUnit → broadcast `0xA8 GeoHarvestFloat` on 0x67 envelope w/ occId; client Prefix suppresses local, replays same native call under SyncApplyScope). occId ring-dedup (64) vs STUN double-send; display-only (wallet 0xA0 stays sole balance writer); unresolved site = drop (cosmetic). Both under `ReportMirrorGate` (Batch-1 precedent). 1360 unit + 7 bridge green. In-game soak = spec Batch-2 checklist (shared tactical → same loot report; harvest tick → client float at right site; no double-credit). |
| **WA-1 world-activity: MIST + BEHEMOTH** | **SHIPPED (2026-07-05), pending in-game soak** | Spec: `docs\superpowers\specs\2026-07-05-multiplayer-unified-popup-mirror-design.md` §5 WA-1 (audit gaps 4a/5a HIGH). **Mist ch#8 `bcf75f0`** — host echoes the native `MistRendererSystem.RecordInstanceData()` gift once per in-game hour (cheap `_hoursPassed` poll, content-hash send-dedup, 5 s deflate throttle); payload = raw deflate bytes (base64-decoded, 25% smaller), split into 24 KB chunks each riding one ordinary versioned ch#8 flush on GeoState `0xA1` (no new packet family; `AttachHost` doubles as the per-tick chunk pump, ~1 chunk/frame). Client reassembles by emission seq (`MistReassembler` — last-wins, duplicate/stale idempotent, lost chunk superseded by next hourly emission) and redraws via native `ProcessInstanceData` (single synchronous MoveNext — frozen-sim safe, zero client producers; client's own active-generator set preserved). Channel id **7 stays RESERVED** for the P7 objectives channel. **Behemoth `eef1c54`** — `GeoBehemothActor` lives OUTSIDE `GeoMap.Vehicles`, now rides the EXISTING surfaces under reserved composite key (FNV `"__behemoth"`, id 1): placement appended to the 0xA5 walk (shared sig-skip; dormant = 0 bytes; interp write via type-correct Surface prop), presence/`BehemothStatus`/tombstone as SENTINEL `GeoVehicleIdentity` on ch#6 (status digit in `VehicleSetDefGuid`; wire codec unchanged; tracker `UpsertResident` dirty on first-sighting + status edge; `HostPrune` → tombstone → client native `Destroy()`). Client spawn-if-absent = inert `SpawnActor<GeoBehemothActor>(FesteringSkiesSettings.BehemothDef, null, false)` (native template resolved LOCALLY — no guids on wire) + native `RegisterBehemoth`; NO DoEnterPlay/OnLevelStart/OnFactionsReady → zero producers; join-save behemoth = idempotent adopt; status stamp value-only (field + VisualsRoot per Dormant/Dead + Animator int). +24 unit tests (mist codec/chunk/reassembler ×14, behemoth conventions + tracker no-regress ×10). 1384 unit + 7 bridge green; DLL SHA256 `57B5D753…AD2C`. In-game soak: mist edge visibly advances on client after host hours; behemoth visible/moving on client, hides on submerge, despawns on kill; no vehicle-mirror regressions. |
| **WA-2 GeoSiteChannel optional tails: HAVEN + ALIEN BASE + EXCAVATION + MISSION DRIFT** | **SHIPPED (2026-07-05), pending in-game soak** | Spec: `docs\superpowers\specs\2026-07-05-multiplayer-unified-popup-mirror-design.md` §5 WA-2 (audit gaps 4d MED-HIGH / 4b MED / 3c MED / 1c LOW-MED). Versioned EXTRAS BLOCK on the ch#5 wire (`c04382f`+`b8734c5`): appended after the record array ONLY when ≥1 site carries a tail (no-tail payload byte-identical to pre-WA-2; older decoders ignore trailing bytes; per-record recLen → parse-known-then-skip for future bits). Tails: **haven** bit0 `{population:i32, infested:bool(bit4)}` — dirty from GeoMap `HavenPopulationChanged`/`HavenPopulationZoneAttrition` (dirty trigger only, per-zone health NOT carried — accepted cosmetic)/`HavenInfestationStateChanged` (arg0=GeoHaven → `GetOwningSiteId` unwraps `.Site`); client writes `_population` BACKING field only (native setter cascades ZonesStats/DestroySite); Infested is DERIVED from the mirrored Owner (carried flag = diagnostic). **Alien base** bit1 `{typeDefGuid, addonDefGuids[]}` — dirty from `SiteAlienBaseTypeChanged`+`SiteAddonsChanged` (15 GeoMap events now bound); client stamps the `AlienBaseTypeDef` auto-prop setter only (never `ChangeAlienBaseType` — sim cascade) + rewrites `GeoSite._addons` (empty = honest clear). **Excavation** bit2 `{excavating(bit5), endDateTicks:i64}` — dirty from `GeoPhoenixFaction.OnExcavationStarted/Completed` (arg-1 `SiteExcavationState` carrier, new argIndex adapter); client find-or-creates the faction record (pure ctor + list add, NEVER Init/StartExcavation) and stamps `IsExcavated`/`ExcavationEndDate` via private setters → `RefreshVisuals` flips native ancient-site art. **Mission drift** — `MissionDriftDirtyPatch` postfix on private `GeoUpdateableMission.Update(Timing)` → host-only ≤1/s-per-site throttled dirty-mark (`GeoSiteChannel.MarkSiteDirtyExternal`); ZERO wire change (snapshot re-reads live deployments; ApplyMission refreshes mutable bits on same class+def). +31 unit tests (tail codecs/compat pins/forward-skip/equality ×25, throttle ×6). 1422 green (1415 unit + 7 bridge); DLL SHA256 `CAE45072…41FF`. In-game soak: host haven population drop → client site details match; infested haven flips on client; alien base promote (nest→lair) → client icon/type; excavation start/finish mirrored (art + IsExcavated); haven-defense brief deployments drift on client while mission ticks. |
| **WA-3 world-activity: AIRCRAFT HP + FORCED DIPLOMACY + INTERCEPTION** | **SHIPPED (2026-07-05), pending in-game soak — WORLD-ACTIVITY BATCHES (WA-1..3) COMPLETE** | Spec: `docs\superpowers\specs\2026-07-05-multiplayer-unified-popup-mirror-design.md` §5 WA-3 (audit gaps 5d MED-HIGH FS / 4e MED / 5c MED). **Aircraft HP/repair `1780108`** — 0xA6 travel-meta wire gains a versioned EXTRAS BLOCK (GeoSiteSnapshot precedent; no-tail payload byte-identical, join by composite key, truncation rejects whole batch): optional `GeoVehicleHealthTail {hp:i32, maxHp:i32, isRepairing(bit4)}` read off `Stats.HitPoints/MaxHitPoints` + `_maintenancePointsToRepair>0`; tail in the change signature so the hourly `RepairFactionAircrafts` tick re-ships a parked repairing craft; initial-suppress now requires a PRISTINE hull (damaged vehicle ships its first state). Client = wallet-style silent field writes (never `SetHitpoints` — OnMaintenanceChanged/breaking-down cascade) + `_maintenancePointsToRepair` 1/0 sentinel; HP-bar repaint via the module's OWN deferred flag (`UIModuleVehicleSelection._refreshVehicleBars = true` — the exact flag `Vehicle_OnMaintenanceChanged` sets). **Forced diplomacy state `353eb05`** — ch#4 wire gains an index-aligned `PartyDiplomacyState` byte per relation (versioned tail; legacy payload → empty; count-mismatch/truncation rejects; all-255 → byte-identical legacy wire): host reads `GetDiplomacyState(relation.MaxValue)` — the exact previousState read native `SetMaxDiplomacyState` performs (FactionDiplomacy.cs:57); client stamps the limitMax=true writes value-only (`MaxValue=GetStateEntry(state).Range.Max`, `MinValue=-MaxDiplomacy`) with NO `OnFactionDiplomacyStateChanged` cascade; NEW dirty trigger = that same event subscribed on every faction (DynamicMethod adapter) so a forced war/alliance mirrors immediately. **Interception `(this commit)`** — modals 32/33 into the 0x69 classifier as `InterceptionNotice` variant (ALWAYS notify-only: 32's `InterceptionInfoData` is an INTERACTIVE live-aircraft brief — InterceptionBriefDataBind:81 cycles site vehicle lists; 33's `GeoAirMission` is NOT a GeoMission (standalone class) → outside the P1 ctor map AND its bind reads live `PlayerAircraft/EnemyAircraft.Vehicle` equipment + crew + Reward → cannot ride the payload-carried MissionOutcome variant — decision grounded GeoscapeView.cs:762/781). 32 = BLOCKING (native PauseGame=true at OpenModalPersistent :861; gate arms at the 0x69 SHOW, releases via the existing ModalResultCallback :833 + Hide belt rails), NON-mandatory, pending-decision notice; 33 = non-blocking resolved notice (loot rides the wallet rail, hull damage rides the WA-3 0xA6 tail); host payload build reads NOTHING off modalData (zero reflection); client dedups via `ReportOutcomeDedup`. +30 unit tests (0xA6 tail codec/pins/signature ×7, diplomacy tail codec/pins/apply-decision ×8, classifier/protocol interception pins ×15 incl. whole-enum no-regress sweeps). 1451 green (1444 unit + 7 bridge); DLL SHA256 `3BAC940D…53A4`. In-game soak: damaged/repairing aircraft HP bar tracks host on client (hourly ticks); forced war (event outcome) → client relation caps + war state match; host interception → client sees pending notice + intents rejected until resolve, then resolved notice; no travel-mirror/diplomacy/modal regressions. |
| **WA-4 pre-attack countdown: SiteAttackSchedule tail (gap 6b)** | **SHIPPED (2026-07-05), pending in-game soak** | Closes audit gap 6b (details in prior handoff) |
| **Popup-mirror Batch-3 (P4+P5)** | **SHIPPED (2026-07-06)** | P4 unified displaySeq sequencer (host stamp at `GeoscapeViewSwitchQuery`, client queue prio DESC/seq ASC one-at-a-time, flag `DisplaySequencerGate` default ON). P5 occId dedup on 0x69/0x6C — eliminates STUN reliable-transport double-display. |
| **Popup-mirror Batch-4 (P7 objectives + marketplace)** | **SHIPPED (2026-07-06)** | P7 `ObjectivesChannel` ch#7 (`GeoFaction.Objectives` 4 classes + `GeoscapeEventSystem` variables + marketplace offers section). Marketplace `UIStateMarketplaceGeoscapeEvent` selection mirror. |
| **Personnel sync PS1-PS4** | **SHIPPED (2026-07-06)** | **PS1-PS3** personnel edit intents 60-65 (equip/augment/hire/transfer/dismiss/rename — permission+ownership gated). `PersonnelChannel` ch#9 (roster membership + whole-`GeoCharacter` live-state blobs, key `GeoUnitId`). `RecruitPoolChannel` ch#10 (haven `AvailableRecruit` + `_nakedRecruits` + `_capturedUnits`). **PS4** `GeoVehicleChannel` ch#6 new tails: crew `GeoUnitId[]` + aircraft loadout (weapons/modules). Research `Available` invalidation reconcile (`ResearchSnapshot` v4 `AvailableAuthoritative`). |
| **Tactical surfaces TS1-TS7** | **SHIPPED (2026-07-06)** | **TS1** 0x92/0x93 actor spawn/despawn (mid-battle reinforcements/eggs/turrets/loot containers — ground entities reuse actor registry). **TS2** 0x8E generic ability-intent relay (active allowlist: Heal, RecoverWill, Rally, PsychicScream, Reload, Interact; DeployTurret/OpenCrate deferred). **TS3** 0x94 ground surfaces fire/goo/acid/mist (`SetVoxelType` leaf funnel). **TS4** 0x95 mission conclusion (`GameOver` chokepoint, client flips `IsGameOver`, outcome modal stays on geoscape 0x69 path). **TS5** 0x8F new mask bits 0x0400 ammo + 0x0800 mind-control faction display. **TS6** 0x96 destructibles (`DestructableDamageReceiver.ApplyDamage` mirror, `SceneObjectId` guid key). **TS7** 0x97 enemy-turn camera hint + 0x98 AoE/volume VFX replay. |
| **P1 mid-session join (geoscape-only)** | **SHIPPED (2026-07-06)** | `JoinReady` 0x45, per-peer unicast save-transfer via `AutosaveGame`, live-battle join rejected with notice. 3+ players unblocked. |
| **P0 new-campaign co-op bootstrap** | **SHIPPED (2026-07-07), pending in-game soak** | Host starts a FRESH campaign from the lobby (NEW CAMPAIGN… rail button → native `UIStateNewGeoscapeGameSettings`; difficulty/DLC host-only, tutorial forced OFF via `GeoscapeGameParams.TutorialEnabled`). Durable gate at the native confirm (`NewCampaignInterceptPatch` on `GameSettings_OnConfirm`: lobby arm = `NewCampaignArmGuard`; mid-session second fresh campaign = existing `HostLoadGuard`; client new-game BLOCKED; clientless mid-session vanilla). At the first playable GEOSCAPE frame (CurtainShowPatch "Playing" seam) the host autosaves (`AutosaveGame`, P1 precedent; `FreshAutosaveCaptured` staleness check) and feeds the meta into the EXISTING `LaunchTransfer` chunked transfer + 2-phase barrier — every peer (host incl.) loads the byte-identical campaign start; clients wait in lobby behind a system-chat notice. Core latch `NewCampaignBootstrap` (single-shot per geoscape frame) pinned by `NewCampaignBootstrapTests`. |
| **Inc3 action-relay envelope (0xA2-0xA4)** | **SHIPPED (2026-07-06), DEFAULT OFF** | Behind `GeoActionRelay.UseEnvelope` flag DEFAULT OFF (legacy path byte-identical; flip after in-game verify). |
| **Known follow-ups sweep** | **SHIPPED (2026-07-06)** | GeoSiteChannel ch#5 +bit6 weather +bit7 `ExpiringTimerAt`. Ch#2 extra dirty trigger `SetPowered`. Tactical `IntentDedup` peer-keyed `(peerId,surfaceId,nonce)` — 3+ players unblocked. `GeoAbilityActivateAction`=80 generic geo-ability relay (Harvest/Excavate/EmergencyRepair/Scan/AncientSiteProbe/ActivateBase/AncientGuardianGuard allowlist, `ActionCategory.GeoAbility`). |
| Inc5 CRC + reconnect | NOT STARTED | divergence detection + reconnect (folds old #5) |
| Rail unification (geoscape) | **DONE @ `a4781ae` (2026-06-26)** | legacy `0x63`/`0x64` retired; `0xA0 GeoWallet`/`0xA1 GeoState` on the `0x67` envelope = SOLE geoscape wallet/state rail; `GeoRailGate` flag dead + removed |
| Tac Inc1 position (0x0008) | **BUILT** | delta pos in `tac.actorstate` 0x8F; in-game pending |
| Tac Inc2 facing (0x0010) | **CODE-COMPLETE @ `74b462c`** | `feat(tactical): wire actor facing 0x0010 into actor-state delta`; build 0 err / 0 warn, 925 tests green; **in-game acceptance gate pending**; plan: `docs/superpowers/plans/2026-06-25-multiplayer-tactical-fullstate-spine-roadmap.md` §4 |
| Tac Inc3 combat outcome + VFX | **IN PROGRESS** | enemy-turn camera chase landed (`4b561b8`, `8b78360`, `3fbfef6`, `ebca1b9`); explosion VFX from damage-state pending |
| #4 10-player efficiency | NOT STARTED | property of #1/Inc3 |
| OUT | — | host-migration, network obfuscation — excluded by decision |

## CURRENT POSITION (update each session)

- **HEAD = autonomous night session (2026-07-05/06, commits `35e156a`..`ff003a6`), NOT pushed.** 1805/1805 tests (1798 unit + 7 bridge). Every batch deployed via `deploy.ps1` (final DLL 2026-07-06 12:56:35). Full handoff: `E:\DEV\PhoenixPoint\docs\superpowers\2026-07-06-EOD-handoff-autonomous-night.md`.
- **State channels (on GeoState `0xA1`):** #1 Inventory, #2 Research, #3 Unlock, #4 Diplomacy, #5 GeoSite (+ extras: haven/alien-base/excavation/attack-schedule/weather/ExpiringTimerAt/ActiveMission), #6 GeoVehicle (+ crew GeoUnitId[] + loadout weapons/modules), #7 Objectives (GeoFaction.Objectives 4 classes + GeoscapeEventSystem variables + marketplace offers), #8 Mist, #9 Personnel (roster membership + whole-GeoCharacter live-state blobs, key GeoUnitId), #10 RecruitPool (haven AvailableRecruit + _nakedRecruits + _capturedUnits).
- **Display rail:** unified displaySeq sequencer (P4, host stamp at GeoscapeViewSwitchQuery, client queue prio DESC/seq ASC one-at-a-time, `DisplaySequencerGate` default ON); 0x69/0x6C occId dedup (P5); marketplace `UIStateMarketplaceGeoscapeEvent` selection.
- **Research:** client Available invalidation reconcile (ResearchSnapshot v4 AvailableAuthoritative).
- **Tactical surfaces:** 0x92/0x93 actor spawn/despawn; 0x8E generic ability-intent relay (Heal/RecoverWill/Rally/PsychicScream/Reload/Interact; DeployTurret/OpenCrate deferred); 0x94 ground surfaces fire/goo/acid/mist; 0x95 mission conclusion; 0x8F +0x0400 ammo +0x0800 mind-control faction display; 0x96 destructibles; 0x97 enemy-turn camera hint; 0x98 AoE/volume VFX replay.
- **Geoscape actions relayed:** MoveVehicle=40, ExploreSite=41, personnel edits 60-65 (equip/augment/hire/transfer/dismiss/rename — permission+ownership gated), GeoAbilityActivateAction=80 (Harvest/Excavate/EmergencyRepair/Scan/AncientSiteProbe/ActivateBase/AncientGuardianGuard allowlist).
- **Net:** tactical IntentDedup peer-keyed `(peerId,surfaceId,nonce)` — 3+ players unblocked; P1 mid-session join geoscape-only (JoinReady 0x45, per-peer unicast save-transfer via AutosaveGame, live-battle join rejected with notice); Inc3 action-relay envelope 0xA2-0xA4 behind `GeoActionRelay.UseEnvelope` flag DEFAULT OFF (legacy path byte-identical; flip after in-game verify).
- **Working & deployed:**
  - Host-authoritative sync engine
  - All geoscape state channels #1-#10 + mist + behemoth
  - Popup-mirror Batch-1..4 (mission brief/outcome/harvest/objectives/marketplace)
  - World-activity WA-1..4 (mist/behemoth/haven/alien-base/excavation/hp/diplomacy/interception/countdown)
  - Personnel sync PS1-PS4 (edit intents + roster + recruit pool + vehicle crew/loadout)
  - Tactical surfaces TS1-TS7 (spawn/despawn/ability-intent/ground/conclusion/ammo/MC/destructibles/camera/VFX)
  - Display sequencer + occId dedup
  - Mid-session join (geoscape-only) + 3+ player topology
  - Universal UI reactivity (`GeoUiRefresh.RefreshNeedsKick`: Research / Manufacturing / BaseLayout; wallet/events/time self-update)
  - Client->host unblocked via `PermissionManager` seeding in `HandlePeerList`
- **NEXT ACTIONS (resume here):**
  1. **In-game verification pass (2-instance soak):** user soak checklist 14310C95 never reported + EVERY batch from this session unverified in-game (sequencer burst/STUN dups, objectives/marketplace, personnel PS1-4 flows incl. client edits, tactical TS1-7, drop-in join, sweep tails, geo ability relays)
  2. **DEFERRED items:** DeployTurret + OpenCrate relay; window-break direct-geometry capture; evac-zone list population; lost-hide reconnect self-heal → Inc5; P2 ownership isolation = USER DECISION; Inc3 UseEnvelope flip; belts B2-B4 removal gated on clean B1 soak #2; cosmetics 4d/6a/3b/6g/6h/1e verified-no-code
  3. Inc5 CRC + reconnect (folds old #5)
- **Architecture spine (2026-06-17):** full host→client geoscape state replication; per-domain channels = interim baseline → retire at Inc4. Design doc: `docs/superpowers/specs/2026-06-17-multiplayer-full-geoscape-replication-feasibility.md`
- **Note:** per-sub-project design specs go to `E:\DEV\PhoenixPoint\docs\superpowers\specs\YYYY-MM-DD-<topic>-design.md` when each is brainstormed; this file is the higher-level living tracker
