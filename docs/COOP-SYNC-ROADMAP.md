# Co-op Sync Roadmap + Status Tracker

Living roadmap + status tracker for the PhoenixPoint co-op multiplayer sync. NEW SESSION: read the STATUS table + CURRENT POSITION first — they say which sub-project is active and the next action.

> **Last verified against code: 2026-07-03** — HEAD `ace79ae` = Fable re-review fix wave (6 commits `fc2c8b5`→`ace79ae`: sim-freeze anchor relay, host-resolved event texts, correlator hardening, `0x6B` advance-request, wallet diag, native-advance reflection ROOT-CAUSE fix); 995/995 tests green; DLL SHA256 `15f9a08e…` (608768 B) deployed. Awaiting in-game verification pass (see NEXT ACTIONS). Session record: `superpowers/2026-07-03-multiplayer-fable-rereview-fixes-handoff.md`.

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
| **Inc4 client sim-freeze — S0+S1** | **CODE-COMPLETE + DEPLOYED flag-ON (2026-07-02), awaiting combined S0+S1 in-game gate** | ROOT fix (reframes Inc1): pause the client geoscape sim CLOCK so ZERO producer fires — one engine-native `Timing.Paused` freeze replacing the producer-table + event-suppress pile. **S0** = inert scaffolding (`ClientSimFreeze.Enabled` flag + guarded `ClientGeoSimFreezePatch` re-assert hook). **S1** = the freeze itself behind the SAME flag, now flipped ON (enable commit `de3aac7`, rollback = `git revert` → default-OFF → legacy suppress path restored): §3.1 `TimeSyncManager.FreezeClientGeoSim` sets geoscape `Timing.Paused=true` via the SETTER from `ClientGeoSimFreezePatch`'s postfix on `GeoscapeEventSystem.OnLevelStart` (`RescheduleForTiming` Max's every already-Started producer; the hourly producer Started later at `LevelCrt:761` auto-Max's under the now-true `_paused`); §3.2 `WriteClock` pins sim `_paused=true` every frame via `ClientSimFreeze.SimPaused` (display readout still host-tracked via `StartTime`); §3.3 NEW `ClientTimeGlyphFreezePatch` postfixes `UIModuleTimeControl.SetTimerPausedState/SetTimerSpeedState` so the pause/speed glyph mirrors the host anchor (`GlyphHostPaused/SpeedIndex`), not the now-always-true `_timing.Paused`. Flag-OFF = byte-unchanged (legacy path kept until S4). Feat `849743b` + enable `de3aac7`; build 0err/0warn; 927 unit green (924 + 3: SimPaused split + re-assert predicate); DLL `138ece7c` (590848 B) deployed. Spec: `docs/superpowers/specs/2026-07-02-multiplayer-inc4-client-sim-freeze-design.md`. Accepted S1 cost (spec §4): client-local travel/mist visuals co-freeze — host-driven travel = S2/Inc2. |
| Inc5 CRC + reconnect | NOT STARTED | divergence detection + reconnect (folds old #5) |
| Rail unification (geoscape) | **DONE @ `a4781ae` (2026-06-26)** | legacy `0x63`/`0x64` retired; `0xA0 GeoWallet`/`0xA1 GeoState` on the `0x67` envelope = SOLE geoscape wallet/state rail; `GeoRailGate` flag dead + removed |
| Tac Inc1 position (0x0008) | **BUILT** | delta pos in `tac.actorstate` 0x8F; in-game pending |
| Tac Inc2 facing (0x0010) | **CODE-COMPLETE @ `74b462c`** | `feat(tactical): wire actor facing 0x0010 into actor-state delta`; build 0 err / 0 warn, 925 tests green; **in-game acceptance gate pending**; plan: `docs/superpowers/plans/2026-06-25-multiplayer-tactical-fullstate-spine-roadmap.md` §4 |
| Tac Inc3 combat outcome + VFX | **IN PROGRESS** | enemy-turn camera chase landed (`4b561b8`, `8b78360`, `3fbfef6`, `ebca1b9`); explosion VFX from damage-state pending |
| #4 10-player efficiency | NOT STARTED | property of #1/Inc3 |
| OUT | — | host-migration, network obfuscation — excluded by decision |

## CURRENT POSITION (update each session)

- **Shipped (pre-skeleton):** inner main HEAD `60f45a2` (NOT pushed), DLL signature `829DA4F5`, deployed to `D:\Steam\steamapps\common\Phoenix Point\Mods\Multiplayer`, 196 tests green
- **HEAD = `ace79ae` (2026-07-03), NOT pushed.** 995/995 tests green (935→995 this session). Fable re-review fix wave landed: `fc2c8b5` sim-freeze anchor relay + unconditional freeze reschedule, `e9e895b` host-resolved event texts on wire (VoidOmen blank-window real fix), `d3686b1` correlator hardening, `4dbbd14` `EventAdvanceRequest=0x6B` (client OK advances host single-choice modal), `f80bc0c` wallet rail diag, `ace79ae` `EventReflection` type-lookup ROOT CAUSE (`Base.UI.GeoscapeModulesData`, unblocks host native advance/resolve for ALL event kinds). DLL SHA256 `15f9a08e…` (608768 B, 2026-07-03 00:43) deployed. Wallet rail (`f005acc`) **CONFIRMED in-game** (client ver=1→25, values identical). Full session record: `superpowers/2026-07-03-multiplayer-fable-rereview-fixes-handoff.md`. Prior context unchanged: Tasks 1-6 merged (#1 Phase 1, in-game UNVERIFIED); Tac Inc2 (facing 0x0010) code-complete; Tac Inc3 (enemy-turn camera chase) in progress; Geo Inc2 partially started; geoscape rail unified `a4781ae` (`0xA0`/`0xA1` on `0x67` envelope sole rail).
- **Working & deployed:**
  - Host-authoritative sync engine
  - Synced surfaces = research (single-source-of-truth via `ResearchChannel` ch2), manufacture, facility, geoscape events (answer), wallet echo (`0xA0`/`0xA1` sole rail on the `0x67` envelope), time-sync (anchor-rate clock)
  - Universal UI reactivity (`GeoUiRefresh.RefreshNeedsKick`: Research / Manufacturing / BaseLayout; wallet/events/time self-update)
  - Client->host unblocked via `PermissionManager` seeding in `HandlePeerList`
- **NEXT ACTIONS (resume here):**
  1. **In-game verification pass (FIRST THING; restart both instances, HOST FIRST)** — detail in `superpowers/2026-07-03-multiplayer-fable-rereview-fixes-handoff.md`: (a) `ace79ae` single-choice — client OK click advances/closes host modal, no repeat windows on client; (b) `ace79ae` multi-choice — client choice click closes host modal (`TryHostNativeResolve` functional for the FIRST TIME ever; on fail host log `[Multiplayer] TryHostNativeResolve guard=…` names the exact guard — paging guard next suspect); (c) host log startup lookup-audit line must list ZERO null members; (d) `e9e895b` VoidOmen window shows title+text (client log `BuildResultEvent … wireNarrLen>0`); (e) `fc2c8b5` client speed/pause clicks control HOST correctly (no self-pause poison); (f) remaining old backlog — `34cca92` aim relay, `b8e50ec`+`dd47ad1` status magnitude, `3eaf77c` invis-targeting, `cda982c` camera-steal, `09bf9ce` host-bind WARN + `1878aa8` save/load arbiter, `b0e20a0` event dedup. (`f005acc` wallet poll = CONFIRMED in-game 2026-07-02, retired from backlog.)
  2. **UNCHANNELLED OUTCOMES channel** — client never receives: recruits/units, skillpoints, StartMission, haven pop, soldier/aircraft damage, SDI, objectives, timers, subfactions (acknowledged TODOs `AnswerEventAction.cs:71-72`, `SyncEngine.cs:226-229`); start with units/skillpoints/StartMission
  3. **Sim-freeze S2+** per outer spec `E:\DEV\PhoenixPoint\docs\superpowers\specs\2026-07-02-multiplayer-inc4-client-sim-freeze-design.md`
  4. **Action-relay `0x60`-`0x62` → envelope cutover** per outer spec `…-action-relay-envelope-cutover-design.md` (single atomic commit, no dual-rail)
- **Architecture spine (2026-06-17):** full host→client geoscape state replication; per-domain channels = interim baseline → retire at Inc4. Design doc: `docs/superpowers/specs/2026-06-17-multiplayer-full-geoscape-replication-feasibility.md`
- **Note:** per-sub-project design specs go to `E:\DEV\PhoenixPoint\docs\superpowers\specs\YYYY-MM-DD-<topic>-design.md` when each is brainstormed; this file is the higher-level living tracker
